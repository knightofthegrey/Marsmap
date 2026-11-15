using System;
using System.Collections;
using Unity.Cinemachine;
using UnityEngine;

// Builds the terrain entirely at runtime (no TerrainData .asset needed, nothing to sync in git).
//  1. Awake: creates an empty TerrainData so the Terrain is always valid.
//  2. Waits for Python/server.py to send a JMARS heightmap (see PythonConnection).
//  3. If nothing arrives in time, falls back to Perlin noise.
//  4. Places the player on top and points the camera at them.
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
    public bool WaitingForHeightmap => connectToPython && !terrainReady;

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

    void AssignTerrainData(TerrainData td)
    {
        terrain.terrainData = td;

        // The collider needs the new data too, or the player falls straight through
        TerrainCollider col = terrain.GetComponent<TerrainCollider>();
        if (col == null) col = terrain.gameObject.AddComponent<TerrainCollider>();
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
        Vector3 origin = terrain.transform.position;
        Vector3 size = terrain.terrainData.size;
        Vector3 pos = player.position;
        pos.x = origin.x + size.x / 2f;
        pos.z = origin.z + size.z / 2f;
        SetPlayerPosition(pos);
    }

    void PlacePlayer()
    {
        if (player == null || terrain == null) return;

        Vector3 pos = player.position;
        float terrainHeight = terrain.SampleHeight(pos) + terrain.transform.position.y;
        pos.y = terrainHeight + PlayerBottomOffset() + 0.1f; // capsule bottom just above ground
        SetPlayerPosition(pos);

        if (!player.gameObject.activeSelf) player.gameObject.SetActive(true);
        Debug.Log($"Terraingen: placed player at {pos} (ground height {terrainHeight:F1}, " +
                  $"terrain size {terrain.terrainData.size})", player);
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

        float ground = terrain.SampleHeight(player.position) + terrain.transform.position.y;
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
