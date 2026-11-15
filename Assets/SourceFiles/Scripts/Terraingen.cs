using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Unity.Cinemachine;
using UnityEngine;
using UnityEngine.InputSystem;

// Builds the terrain entirely at runtime (no TerrainData .asset needed, nothing to sync in git).
//  1. Awake: creates an empty TerrainData so the Terrain is always valid.
//  2. Waits for Python/server.py to send a JMARS heightmap (see PythonConnection).
//  3. If nothing arrives in time, falls back to Perlin noise.
//  4. Places the player on top and points the camera at them.
// A --polygon too big for one terrain arrives as a grid of tiles (TILE messages) that are
// laid side by side into one seamless terrain; see the "Tiles" section.
public class Terraingen : MonoBehaviour
{
    const int HeaderSize = 24; // "HMAP" + int res + 4 floats

    [Header("References")]
    public Transform player;
    [Tooltip("Optional. Uses the Terrain on this object, or creates one.")]
    public Terrain terrain;

    [Header("Python / JMARS")]
    [Tooltip("Connect to Python/server.py for a real Mars heightmap")]
    public bool connectToPython = true;
    [Tooltip("If Python hasn't sent a heightmap after this many seconds, use the Perlin fallback")]
    public float waitForPythonSeconds = 5f;

    [Header("Player placement")]
    [Tooltip("Keep the player switched off until the terrain is ready, so they can't fall into the void")]
    public bool freezePlayerUntilReady = true;
    [Tooltip("Move the player to the middle of the terrain whenever new terrain is applied")]
    public bool centerPlayerOnNewTerrain = true;
    [Tooltip("Safety net: if the player ever drops below the terrain surface, lift them back onto it")]
    public bool keepPlayerAboveTerrain = true;
    [Tooltip("Shift the terrain so its middle sits at world (0,0,0). Physics and rendering " +
             "get unreliable tens of km from the origin, and JMARS regions are huge.")]
    public bool recenterTerrainAtOrigin = true;

    [Header("Look")]
    [Tooltip("Multiplies terrain height so relief is easier to see (1 = true scale)")]
    public float verticalExaggeration = 5f;
    [Tooltip("Optional. If empty, uses URP's Terrain/Lit shader.")]
    public Material terrainMaterial;
    [Tooltip("Colours from lowest to highest elevation")]
    public Color[] gradient =
    {
        new Color(0.20f, 0.10f, 0.07f), // low: dark brown
        new Color(0.50f, 0.22f, 0.12f), // rust
        new Color(0.75f, 0.45f, 0.25f), // orange-tan
        new Color(0.92f, 0.80f, 0.62f), // high: pale sand
    };

    [Header("Camera")]
    [Tooltip("How far the camera can see")]
    public float cameraFarClip = 20000f;

    [Header("Perlin fallback")]
    [Tooltip("Must be 2^n + 1: 33, 65, 129, 257, 513, 1025, 2049, 4097")]
    public int perlinResolution = 513;
    public Vector3 perlinSize = new Vector3(1000f, 100f, 1000f); // width, max height, length
    public float scale = 20f;

    [Header("Debug")]
    [Tooltip("Log player height vs. terrain collider once a second")]
    public bool logPlayerHeight = false;

    bool terrainReady;

    // True until either Python's heightmap or the Perlin fallback has been applied.
    // PythonConnection only asks the server for the heightmap while this is true.
    // Also true while a tile grid is only partly here (e.g. the connection dropped mid-way),
    // so a reconnect asks again; tiles that already arrived are simply replaced.
    public bool WaitingForHeightmap =>
        connectToPython && (!terrainReady || (tiles != null && tilesReceived < tilesExpected));

    // ---------------------------------------------------------------
    // Startup
    // ---------------------------------------------------------------

    // Awake runs before every script's Start(), so the Terrain always has valid data
    // before anything else (e.g. Initialize.cs) samples it.
    void Awake()
    {
        // Flat placeholder until real data arrives
        TerrainData placeholder = NewTerrainData(33, new Vector3(perlinSize.x, 1f, perlinSize.z));

        if (terrain == null) terrain = GetComponent<Terrain>();
        if (terrain == null)
        {
            GameObject go = Terrain.CreateTerrainGameObject(placeholder);
            go.name = "Terrain (Generated)";
            terrain = go.GetComponent<Terrain>();
        }

        AssignTerrainData(placeholder);
        EnsureTerrainMaterial();
        terrain.drawHeightmap = true;
        terrain.enabled = true;
        terrain.gameObject.SetActive(true);

        if (connectToPython && FindFirstObjectByType<PythonConnection>() == null)
            gameObject.AddComponent<PythonConnection>();
    }

    void Start()
    {
        if (freezePlayerUntilReady && player != null)
            player.gameObject.SetActive(false);

        if (connectToPython)
            Invoke(nameof(PerlinFallback), waitForPythonSeconds);
        else
            PerlinFallback();
    }

    void PerlinFallback()
    {
        if (terrainReady) return; // Python already delivered real data
        if (connectToPython)
            Debug.LogWarning($"Terraingen: no heightmap from Python after {waitForPythonSeconds}s, " +
                             "using Perlin fallback. Is server.py running?");
        GeneratePerlin();
        StartCoroutine(PlaceAfterPhysics());
    }

    [ContextMenu("Regenerate Perlin Terrain")]
    void RegeneratePerlin()
    {
        GeneratePerlin();
        StartCoroutine(PlaceAfterPhysics());
    }

    // ---------------------------------------------------------------
    // Called by PythonConnection when a heightmap message arrives
    // ---------------------------------------------------------------
    public void ApplyHeightmap(byte[] msg)
    {
        int res = BitConverter.ToInt32(msg, 4);
        float minElev = BitConverter.ToSingle(msg, 8);
        float maxElev = BitConverter.ToSingle(msg, 12);
        float sizeX = BitConverter.ToSingle(msg, 16);
        float sizeZ = BitConverter.ToSingle(msg, 20);

        if (msg.Length != HeaderSize + res * res * 2)
        {
            Debug.LogError($"Bad heightmap message: {msg.Length} bytes for res {res}");
            return;
        }

        float[,] heights = new float[res, res];
        for (int y = 0; y < res; y++)
            for (int x = 0; x < res; x++)
            {
                int i = HeaderSize + (y * res + x) * 2;
                heights[y, x] = BitConverter.ToUInt16(msg, i) / 65535f; // [y, x], row 0 = south
            }

        float heightRange = Mathf.Max((maxElev - minElev) * verticalExaggeration, 1f);
        ApplyHeights(heights, res, new Vector3(sizeX, heightRange, sizeZ));

        Debug.Log($"Heightmap applied: {res}x{res}, {minElev:F0} to {maxElev:F0} m, " +
                  $"{sizeX:F0} x {sizeZ:F0} m (vertical x{verticalExaggeration})");

        StartCoroutine(PlaceAfterPhysics());
    }

    // ---------------------------------------------------------------
    // Terrain building
    // ---------------------------------------------------------------
    void GeneratePerlin()
    {
        int res = perlinResolution;
        float[,] heights = new float[res, res];

        float offsetX = UnityEngine.Random.Range(0f, 9999f);
        float offsetY = UnityEngine.Random.Range(0f, 9999f);

        for (int y = 0; y < res; y++)
            for (int x = 0; x < res; x++)
                heights[y, x] = Mathf.PerlinNoise(
                    (float)x / res * scale + offsetX,
                    (float)y / res * scale + offsetY);

        ApplyHeights(heights, res, perlinSize);
        Debug.Log($"Terraingen: Perlin terrain {res}x{res}, size {perlinSize}");
    }

    // Shared by JMARS and Perlin: build a fresh TerrainData, colour it, position it.
    void ApplyHeights(float[,] heights, int res, Vector3 size)
    {
        ClearTiles(); // a single terrain replaces any tile grid
        TerrainData td = NewTerrainData(res, size);
        td.SetHeights(0, 0, heights);
        ApplyColors(td, heights, res);
        AssignTerrainData(td);
        EnsureTerrainMaterial();

        if (recenterTerrainAtOrigin)
        {
            float centerH = heights[res / 2, res / 2] * size.y;
            terrain.transform.position = new Vector3(-size.x / 2f, -centerH, -size.z / 2f);
        }
        else
        {
            terrain.transform.position = Vector3.zero;
        }

        terrain.Flush();
        Physics.SyncTransforms();
        terrainReady = true;
    }

    TerrainData NewTerrainData(int res, Vector3 size)
    {
        TerrainData td = new TerrainData();
        td.name = "Runtime Mars TerrainData";
        td.heightmapResolution = res; // must be 2^n + 1; set BEFORE size (it resets size)
        td.size = size;
        td.alphamapResolution = 512;
        return td;
    }

    void AssignTerrainData(TerrainData td) => AssignTerrainData(terrain, td);

    void AssignTerrainData(Terrain t, TerrainData td)
    {
        t.terrainData = td;

        // The collider needs the new data too, or the player falls straight through
        TerrainCollider col = t.GetComponent<TerrainCollider>();
        if (col == null) col = t.gameObject.AddComponent<TerrainCollider>();
        col.enabled = false; // force PhysX to rebuild the heightfield
        col.terrainData = td;
        col.enabled = true;
    }

    // Never trust the material saved in the scene: a wrong one (e.g. a TextMesh Pro or HDRP shader)
    // makes the terrain invisible in URP. Use the assigned one, otherwise URP's Terrain/Lit.
    void EnsureTerrainMaterial()
    {
        if (terrainMaterial != null)
        {
            terrain.materialTemplate = terrainMaterial;
            return;
        }

        Material current = terrain.materialTemplate;
        bool currentOk = current != null && current.shader != null && current.shader.isSupported
                         && current.shader.name.Contains("Terrain");
        if (currentOk) return;

        Shader shader = Shader.Find("Universal Render Pipeline/Terrain/Lit");
        if (shader != null)
            terrain.materialTemplate = new Material(shader);
        else
            Debug.LogWarning("Terraingen: URP Terrain/Lit shader not found. Assign TerrainLit to " +
                             "'Terrain Material' in the Inspector.", this);
    }

    // Paints the terrain with a low-to-high colour gradient so height is visible.
    void ApplyColors(TerrainData td, float[,] heights, int res)
    {
        int n = gradient.Length;
        if (n < 2) return;

        TerrainLayer[] layers = new TerrainLayer[n];
        for (int i = 0; i < n; i++)
        {
            Texture2D tex = new Texture2D(4, 4);
            Color[] px = new Color[16];
            for (int p = 0; p < 16; p++) px[p] = gradient[i];
            tex.SetPixels(px);
            tex.Apply();
            // Matte: otherwise URP reads the texture's alpha (1) as smoothness and the ground
            // turns into a mirror that reflects the blue sky ("blue hills").
            layers[i] = new TerrainLayer
            {
                diffuseTexture = tex,
                tileSize = new Vector2(100, 100),
                metallic = 0f,
                smoothness = 0f,
                smoothnessSource = TerrainLayerSmoothnessSource.ConstantOnly,
            };
        }
        td.terrainLayers = layers;

        int a = td.alphamapResolution;
        float[,,] maps = new float[a, a, n];
        for (int y = 0; y < a; y++)
            for (int x = 0; x < a; x++)
            {
                float h = heights[y * (res - 1) / (a - 1), x * (res - 1) / (a - 1)];
                float t = h * (n - 1);
                int i = Mathf.Clamp((int)t, 0, n - 2);
                float f = Mathf.Clamp01(t - i);
                maps[y, x, i] = 1f - f;
                maps[y, x, i + 1] = f;
            }
        td.SetAlphamaps(0, 0, maps);
    }

    // ---------------------------------------------------------------
    // Tiles: a big --polygon arrives as nx x nz terrains that join into one
    // ---------------------------------------------------------------
    const int TileHeaderSize = 44; // "TILE" + 6 int32 + 4 float32 (see server.py)

    class TileInfo
    {
        public Terrain terrain;
        public TerrainLayer[] colourLayers; // Terraingen's elevation colours
        public float[,,] colourMaps;
        public TerrainLayer photoLayer;     // satellite image, if one was sent
    }

    Terrain[,] tiles;                 // [ix, iz], null where the polygon didn't reach
    readonly Dictionary<Vector2Int, TileInfo> tileInfos = new Dictionary<Vector2Int, TileInfo>();
    readonly List<GameObject> extraTileObjects = new List<GameObject>(); // all tiles but `terrain`
    int tileCountX, tileCountZ, tilesExpected, tilesReceived;
    Vector3 tileSize, tileOrigin;     // tile (0,0)'s south-west corner in world space
    bool showTilePhotos = true;
    bool mainTerrainUsed;

    // "TILE" | ix iz nx nz count res (int32) | minElev maxElev sizeX sizeZ (float32) | res*res uint16
    public void ApplyTile(byte[] msg)
    {
        int ix = BitConverter.ToInt32(msg, 4), iz = BitConverter.ToInt32(msg, 8);
        int nx = BitConverter.ToInt32(msg, 12), nz = BitConverter.ToInt32(msg, 16);
        int count = BitConverter.ToInt32(msg, 20), res = BitConverter.ToInt32(msg, 24);
        float minElev = BitConverter.ToSingle(msg, 28), maxElev = BitConverter.ToSingle(msg, 32);
        float sizeX = BitConverter.ToSingle(msg, 36), sizeZ = BitConverter.ToSingle(msg, 40);
        if (msg.Length != TileHeaderSize + res * res * 2 || ix < 0 || iz < 0 || ix >= nx || iz >= nz)
        {
            Debug.LogError($"Bad tile message: {msg.Length} bytes, tile {ix},{iz} of {nx}x{nz}, res {res}");
            return;
        }

        // First tile of a new grid (or the last grid was complete): start over
        if (tiles == null || tilesReceived >= tilesExpected || nx != tileCountX || nz != tileCountZ)
        {
            ClearTiles();
            tiles = new Terrain[nx, nz];
            tileCountX = nx;
            tileCountZ = nz;
            tilesExpected = count;
            tilesReceived = 0;
            // Every tile uses the SAME height range, so neighbouring edges meet exactly
            tileSize = new Vector3(sizeX, Mathf.Max((maxElev - minElev) * verticalExaggeration, 1f), sizeZ);
            // Centre the whole grid on the origin (physics gets unreliable far away)
            tileOrigin = recenterTerrainAtOrigin
                ? new Vector3(-sizeX * nx / 2f, 0f, -sizeZ * nz / 2f) : Vector3.zero;
        }

        float[,] heights = new float[res, res];
        for (int y = 0; y < res; y++)
            for (int x = 0; x < res; x++)
                heights[y, x] = BitConverter.ToUInt16(msg, TileHeaderSize + (y * res + x) * 2) / 65535f;

        TerrainData td = NewTerrainData(res, tileSize);
        td.SetHeights(0, 0, heights);
        ApplyColors(td, heights, res); // same gradient everywhere, since heights share one range

        Terrain t = tiles[ix, iz];
        if (t == null)
        {
            if (!mainTerrainUsed)
            {
                t = terrain; // reuse the scene's terrain for the first tile
                mainTerrainUsed = true;
            }
            else
            {
                GameObject go = Terrain.CreateTerrainGameObject(td);
                go.name = $"Terrain Tile {ix},{iz}";
                t = go.GetComponent<Terrain>();
                t.materialTemplate = terrain.materialTemplate;
                extraTileObjects.Add(go);
            }
            tiles[ix, iz] = t;
            tilesReceived++;
        }
        AssignTerrainData(t, td);
        t.transform.position = tileOrigin + new Vector3(ix * sizeX, 0f, iz * sizeZ);
        t.heightmapPixelError = terrain.heightmapPixelError;
        t.basemapDistance = terrain.basemapDistance;
        t.groupingID = terrain.groupingID;
        t.allowAutoConnect = true;
        t.Flush();

        var info = new TileInfo { terrain = t, colourLayers = td.terrainLayers };
        if (td.alphamapLayers > 0)
            info.colourMaps = td.GetAlphamaps(0, 0, td.alphamapResolution, td.alphamapResolution);
        tileInfos[new Vector2Int(ix, iz)] = info;
        terrainReady = true; // cancels the Perlin fallback

        if (tilesReceived == tilesExpected) FinishTiles(minElev, maxElev);
    }

    // "TIMG" | ix iz (int32) | PNG/JPG covering exactly that tile, north up
    public void ApplyTileImage(byte[] msg)
    {
        if (msg.Length < 13) return;
        var key = new Vector2Int(BitConverter.ToInt32(msg, 4), BitConverter.ToInt32(msg, 8));
        if (!tileInfos.TryGetValue(key, out TileInfo info))
        {
            Debug.LogWarning($"Terraingen: image for tile {key.x},{key.y}, which hasn't arrived");
            return;
        }
        byte[] file = new byte[msg.Length - 12];
        Buffer.BlockCopy(msg, 12, file, 0, file.Length);
        var tex = new Texture2D(2, 2, TextureFormat.RGB24, true);
        if (!tex.LoadImage(file, true))
        {
            Destroy(tex);
            Debug.LogWarning($"Terraingen: could not decode the image for tile {key.x},{key.y}");
            return;
        }
        tex.name = $"Tile photo {key.x},{key.y}";
        tex.wrapMode = TextureWrapMode.Clamp; // no bleeding from the opposite edge at the seams
        tex.filterMode = FilterMode.Trilinear;
        tex.anisoLevel = 4;

        // Same look as TerrainOverlay: one matte layer the size of the tile
        info.photoLayer = new TerrainLayer
        {
            name = tex.name,
            diffuseTexture = tex,
            tileSize = new Vector2(tileSize.x, tileSize.z),
            metallic = 0f,
            smoothness = 0f,
            smoothnessSource = TerrainLayerSmoothnessSource.ConstantOnly,
        };
        ShowTileLayers(info);
    }

    void ShowTileLayers(TileInfo info)
    {
        TerrainData td = info.terrain.terrainData;
        if (showTilePhotos && info.photoLayer != null)
        {
            td.terrainLayers = new[] { info.photoLayer };
            int a = td.alphamapResolution;
            var full = new float[a, a, 1];
            for (int y = 0; y < a; y++)
                for (int x = 0; x < a; x++)
                    full[y, x, 0] = 1f;
            td.SetAlphamaps(0, 0, full);
        }
        else if (info.colourLayers != null)
        {
            td.terrainLayers = info.colourLayers;
            if (info.colourMaps != null) td.SetAlphamaps(0, 0, info.colourMaps);
        }
    }

    void FinishTiles(float minElev, float maxElev)
    {
        // Tell each tile who its neighbours are, so their level of detail matches at the seams
        for (int x = 0; x < tileCountX; x++)
            for (int z = 0; z < tileCountZ; z++)
                if (tiles[x, z] != null)
                    tiles[x, z].SetNeighbors(TileOrNull(x - 1, z), TileOrNull(x, z + 1),
                                             TileOrNull(x + 1, z), TileOrNull(x, z - 1));
        Physics.SyncTransforms();
        Debug.Log($"Terrain tiles applied: {tilesReceived} of {tileCountX}x{tileCountZ}, each " +
                  $"{tileSize.x:F0} x {tileSize.z:F0} m, {minElev:F0} to {maxElev:F0} m " +
                  $"(vertical x{verticalExaggeration}). L: photo / elevation colours");
        StartCoroutine(PlaceAfterPhysics());
    }

    // Bottom-left label in the same style as TerrainOverlay (which has no layers in tiled mode)
    const string TileCredit = "THEMIS day IR 100 m (NASA/JPL/ASU) + CTX 6 m (NASA/JPL/MSSS); " +
                              "heights: CTX DTMs 20 m + MOLA-HRSC 200 m (USGS)";
    GUIStyle tileTitleStyle, tileCreditStyle;

    void OnGUI()
    {
        if (tiles == null || tilesReceived < tilesExpected) return;
        bool photo = showTilePhotos && tileInfos.Values.Any(i => i.photoLayer != null);
        if (tileTitleStyle == null)
        {
            tileTitleStyle = new GUIStyle(GUI.skin.label) { fontSize = 14, fontStyle = FontStyle.Bold, wordWrap = false };
            tileTitleStyle.normal.textColor = Color.white;
            tileCreditStyle = new GUIStyle(GUI.skin.label) { fontSize = 11, wordWrap = true };
            tileCreditStyle.normal.textColor = new Color(0.85f, 0.85f, 0.85f);
        }
        var title = new GUIContent((photo ? "Satellite image" : "Elevation colours") + "  (L: switch)");
        var credit = new GUIContent(photo ? TileCredit
            : "Terraingen colours by height: dark brown low, pale sand high");

        const float pad = 6f, margin = 10f;
        Vector2 titleSize = tileTitleStyle.CalcSize(title);
        float inner = Mathf.Min(Mathf.Max(titleSize.x, tileCreditStyle.CalcSize(credit).x),
                                Mathf.Max(100f, Screen.width - 2f * (margin + pad)));
        float creditHeight = tileCreditStyle.CalcHeight(credit, inner);
        var box = new Rect(margin, Screen.height - margin - titleSize.y - creditHeight - 2f * pad,
                           inner + 2f * pad, titleSize.y + creditHeight + 2f * pad);
        Color previous = GUI.color;
        GUI.color = new Color(0f, 0f, 0f, 0.65f);
        GUI.DrawTexture(box, Texture2D.whiteTexture);
        GUI.color = previous;
        GUI.Label(new Rect(box.x + pad, box.y + pad, inner, titleSize.y), title, tileTitleStyle);
        GUI.Label(new Rect(box.x + pad, box.y + pad + titleSize.y, inner, creditHeight), credit, tileCreditStyle);
    }

    Terrain TileOrNull(int x, int z) =>
        tiles != null && x >= 0 && z >= 0 && x < tileCountX && z < tileCountZ ? tiles[x, z] : null;

    // The tile under a world position, else the nearest one; `terrain` when not tiled
    Terrain TileAt(Vector3 pos)
    {
        if (tiles == null) return terrain;
        int x = Mathf.Clamp(Mathf.FloorToInt((pos.x - tileOrigin.x) / tileSize.x), 0, tileCountX - 1);
        int z = Mathf.Clamp(Mathf.FloorToInt((pos.z - tileOrigin.z) / tileSize.z), 0, tileCountZ - 1);
        if (tiles[x, z] != null) return tiles[x, z];
        Terrain best = terrain;
        float bestD = float.MaxValue;
        foreach (Terrain t in tiles)
        {
            if (t == null) continue;
            float d = (t.transform.position + new Vector3(tileSize.x, 0, tileSize.z) / 2f - pos).sqrMagnitude;
            if (d < bestD) { bestD = d; best = t; }
        }
        return best;
    }

    void ClearTiles()
    {
        if (tiles == null) return;
        foreach (GameObject go in extraTileObjects)
            if (go != null) Destroy(go);
        extraTileObjects.Clear();
        tileInfos.Clear();
        terrain.SetNeighbors(null, null, null, null);
        tiles = null;
        tilesExpected = tilesReceived = 0;
        mainTerrainUsed = false;
    }

    // ---------------------------------------------------------------
    // Player + camera
    // ---------------------------------------------------------------

    // Wait for a physics step so the new collider exists before the player appears
    IEnumerator PlaceAfterPhysics()
    {
        yield return new WaitForFixedUpdate();
        yield return new WaitForFixedUpdate();
        Physics.SyncTransforms();
        if (centerPlayerOnNewTerrain) CenterPlayerXZ();
        PlacePlayer();
        SetupCamera();
    }

    void CenterPlayerXZ()
    {
        if (player == null) return;
        Vector3 pos = player.position;
        if (tiles != null)
        {
            // Middle of the whole tile grid, or of the nearest tile if that one isn't there
            Terrain t = TileAt(tileOrigin + new Vector3(tileSize.x * tileCountX, 0, tileSize.z * tileCountZ) / 2f);
            Vector3 c = tileOrigin + new Vector3(tileSize.x * tileCountX, 0, tileSize.z * tileCountZ) / 2f;
            Vector3 o = t.transform.position;
            pos.x = Mathf.Clamp(c.x, o.x + 1f, o.x + tileSize.x - 1f);
            pos.z = Mathf.Clamp(c.z, o.z + 1f, o.z + tileSize.z - 1f);
            SetPlayerPosition(pos);
            return;
        }
        Vector3 origin = terrain.transform.position;
        Vector3 size = terrain.terrainData.size;
        pos.x = origin.x + size.x / 2f;
        pos.z = origin.z + size.z / 2f;
        SetPlayerPosition(pos);
    }

    // Ground height under a world position (whichever tile it is on)
    float GroundHeight(Vector3 pos)
    {
        Terrain t = TileAt(pos);
        return t.SampleHeight(pos) + t.transform.position.y;
    }

    void PlacePlayer()
    {
        if (player == null || terrain == null) return;

        Vector3 pos = player.position;
        float terrainHeight = GroundHeight(pos);
        pos.y = terrainHeight + PlayerBottomOffset() + 0.1f; // capsule bottom just above ground
        SetPlayerPosition(pos);

        if (!player.gameObject.activeSelf) player.gameObject.SetActive(true);
        Debug.Log($"Terraingen: placed player at {pos} (ground height {terrainHeight:F1}, " +
                  $"terrain size {terrain.terrainData.size}" +
                  (tiles != null ? $", one of {tileCountX}x{tileCountZ} tiles)" : ")"), player);
    }

    // Distance from the player's pivot down to the bottom of its CharacterController capsule
    float PlayerBottomOffset()
    {
        CharacterController cc = player.GetComponent<CharacterController>();
        return cc != null ? cc.height * 0.5f - cc.center.y + cc.skinWidth : 1f;
    }

    void SetPlayerPosition(Vector3 pos)
    {
        // Safely move a CharacterController if one is attached
        CharacterController cc = player.GetComponent<CharacterController>();
        if (cc != null) cc.enabled = false;
        player.position = pos;
        Physics.SyncTransforms();
        if (cc != null) cc.enabled = true;
    }

    // Don't rely on scene settings: point the Cinemachine camera at the player
    // and make sure it can see far enough.
    void SetupCamera()
    {
        CinemachineCamera vcam = FindFirstObjectByType<CinemachineCamera>();
        if (vcam != null)
        {
            if (player != null)
            {
                vcam.Target.TrackingTarget = player;
                vcam.ForceCameraPosition(player.position, vcam.transform.rotation);
            }
            vcam.Lens.FarClipPlane = cameraFarClip;
        }
        else
        {
            Debug.LogWarning("Terraingen: no CinemachineCamera found in the scene.", this);
        }

        Camera cam = Camera.main;
        if (cam != null) cam.farClipPlane = cameraFarClip;
    }

    // ---------------------------------------------------------------
    // Safety net + debug
    // ---------------------------------------------------------------
    void LateUpdate()
    {
        if (!keepPlayerAboveTerrain || !terrainReady || player == null
            || !player.gameObject.activeSelf) return;

        float ground = GroundHeight(player.position);
        float minY = ground + PlayerBottomOffset() - 0.5f;
        if (player.position.y < minY)
        {
            Vector3 pos = player.position;
            pos.y = ground + PlayerBottomOffset() + 0.1f;
            SetPlayerPosition(pos);
        }
    }

    void Update()
    {
        // L: satellite photo <-> elevation colours on the tiles (TerrainOverlay does this for
        // a single terrain; it has no layers in tiled mode, so the two never both react)
        Keyboard keyboard = Keyboard.current;
        if (tiles != null && keyboard != null && keyboard.lKey.wasPressedThisFrame)
        {
            showTilePhotos = !showTilePhotos;
            foreach (TileInfo info in tileInfos.Values) ShowTileLayers(info);
        }

        if (!logPlayerHeight || player == null || !player.gameObject.activeSelf || !terrainReady) return;
        if (Time.frameCount % 60 != 0) return;

        // Cast from high above the terrain, not from the player, so the result
        // is still meaningful if the player has sunk below the surface
        Vector3 p = player.position;
        Vector3 from = new Vector3(p.x, terrain.transform.position.y +
                                   terrain.terrainData.size.y + 100f, p.z);
        bool hit = Physics.Raycast(from, Vector3.down, out RaycastHit h, 1000000f);
        Debug.Log($"Player Y={p.y:F1} | " +
                  (hit ? $"collider surface y={h.point.y:F1}" : "NO collider below") +
                  (hit && p.y < h.point.y - 0.5f ? " | PLAYER IS BELOW THE SURFACE" : ""));
    }
}
