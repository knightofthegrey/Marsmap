using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Drapes the site images that Python/server.py sends (photo, slope, thermal) over the
/// runtime terrain. Press L (or B on a Quest controller) to cycle: each site layer in
/// site.json order, then Terraingen's elevation colours, then back to the first layer.
/// The layer name and credit show bottom-left: an IMGUI label on desktop, a world-space
/// label that follows the head in VR (IMGUI isn't drawn in a headset).
///
/// Each image covers exactly the heightmap's box (top row = north, left column = west), so a
/// single TerrainLayer as big as the whole terrain lines it up with the heights, no flipping:
/// LoadImage puts the top row at v = 1 = terrain z max = north, and u = 0 = terrain x = 0 = west.
///
/// PythonConnection adds this component automatically when the first site message arrives.
/// </summary>
public class TerrainOverlay : MonoBehaviour
{
    [Tooltip("Optional. Found automatically; its terrain is used, else Terrain.activeTerrain.")]
    public Terraingen terraingen;
    [Tooltip("Show the layer name and credit in the bottom-left corner")]
    public bool showLabel = true;

    const string PreferredLayer = "photo";
    const string ElevationLabel = "Elevation colours";
    const string ElevationCredit = "Terraingen colours by height: dark brown low, pale sand high";

    readonly Dictionary<string, Texture2D> textures = new Dictionary<string, Texture2D>();
    readonly List<string> arrivalOrder = new List<string>();
    SiteMessage site;

    string selected;        // layer name, or null = Terraingen's elevation colours
    bool selectionMade;     // the first suitable layer (or the user) has picked something
    TerrainData siteData;   // the terrain the images belong to (they arrive right after its heightmap)

    TerrainData trackedData;        // the TerrainData the cache below belongs to
    TerrainLayer[] originalLayers;  // Terraingen's elevation colours, cached on first replace
    float[,,] originalMaps;
    TerrainLayer overlayLayer;      // the one layer we made; destroyed when replaced
    bool overlayShown;
    float[,,] fullWeights;          // [res, res, 1] all ones, reused

    bool searchedTerraingen;
    GUIStyle titleStyle, creditStyle;

    // VR only: world-space copy of the IMGUI label, made the first time it has something to show
    static readonly Vector2 VrLabelOffset = new Vector2(-0.35f, -0.28f); // lower-left of the view
    VRHudLabel vrLabel;
    string vrShownLabel, vrShownCredit; // what vrLabel was last given, so Set runs only on change
    bool vrLabelVisible;

    /// <summary>True when site images exist for the terrain currently shown.</summary>
    public bool HasLayers => textures.Count > 0 && siteData != null && siteData == TrackTerrain();

    /// <summary>Name of the layer shown, or null for elevation colours.</summary>
    public string SelectedLayer => selected;

    /// <summary>Labels and credits for the layers. A new site message precedes a new heightmap
    /// and fresh images, so anything from an earlier site is dropped.</summary>
    public void SetSite(SiteMessage message)
    {
        if (textures.Count > 0)
        {
            ShowElevationColours();
            ClearTextures();
        }
        site = message;
        siteData = null;
        selected = null;
        selectionMade = false;
    }

    /// <summary>Decode one JPEG/PNG layer image and show it if it is the selected layer.</summary>
    public bool AddLayerImage(string layerName, byte[] imageBytes)
    {
        if (string.IsNullOrEmpty(layerName) || imageBytes == null || imageBytes.Length == 0)
        {
            Debug.LogWarning("[TerrainOverlay] Ignored an empty layer image", this);
            return false;
        }

        var tex = new Texture2D(2, 2, TextureFormat.RGB24, true); // mipChain: LoadImage keeps mips
        if (!tex.LoadImage(imageBytes, true)) // true: free the CPU copy once uploaded
        {
            Destroy(tex);
            Debug.LogWarning($"[TerrainOverlay] Could not decode layer \"{layerName}\" ({imageBytes.Length} bytes)", this);
            return false;
        }
        tex.name = "Site layer " + layerName;
        tex.wrapMode = TextureWrapMode.Clamp;
        tex.filterMode = FilterMode.Trilinear;
        tex.anisoLevel = 4;

        textures.TryGetValue(layerName, out Texture2D old);
        if (old == null && !arrivalOrder.Contains(layerName)) arrivalOrder.Add(layerName);
        textures[layerName] = tex;

        // Images follow their heightmap, which Terraingen has already applied synchronously.
        siteData = TrackTerrain();
        Debug.Log($"[TerrainOverlay] Layer \"{layerName}\": {tex.width}x{tex.height}" +
                  (siteData == null ? " (no terrain yet, not shown)" : ""), this);

        if (!selectionMade && (layerName == PreferredLayer || !SiteHasLayer(PreferredLayer)))
        {
            selected = layerName;
            selectionMade = true;
        }
        if (selected == layerName) ApplySelection();

        if (old != null) Destroy(old); // after the terrain stopped using it
        return true;
    }

    /// <summary>Show the next site layer; after the last one, Terraingen's elevation colours.</summary>
    public void NextLayer()
    {
        List<string> names = OrderedNames();
        if (names.Count == 0) return;
        int index = selected == null ? -1 : names.IndexOf(selected);
        if (index < 0) index = names.Count; // elevation colours sit after the last layer
        int next = (index + 1) % (names.Count + 1);
        selected = next < names.Count ? names[next] : null;
        selectionMade = true;
        ApplySelection();
    }

    void Update()
    {
        if (MarsmapInput.NextLayer.WasPressedThisFrame() && HasLayers) NextLayer(); // L key or B button
        UpdateVrLabel();
    }

    // ---------------------------------------------------------------
    // Terrain
    // ---------------------------------------------------------------

    void ApplySelection()
    {
        TerrainData td = TrackTerrain();
        if (td == null || td != siteData) return; // images belong to another terrain
        if (selected != null && textures.TryGetValue(selected, out Texture2D tex) && tex != null)
            ShowTexture(td, tex);
        else
            ShowElevationColours();
    }

    void ShowTexture(TerrainData td, Texture2D tex)
    {
        if (originalLayers == null)
        {
            originalLayers = td.terrainLayers;
            int a = td.alphamapResolution;
            originalMaps = td.alphamapLayers > 0 ? td.GetAlphamaps(0, 0, a, a) : null;
        }

        Vector3 size = td.size;
        var layer = new TerrainLayer
        {
            name = tex.name,
            diffuseTexture = tex,
            tileSize = new Vector2(size.x, size.z), // one tile = the whole terrain
            tileOffset = Vector2.zero,
            metallic = 0f,
            smoothness = 0f,
            smoothnessSource = TerrainLayerSmoothnessSource.ConstantOnly, // matte rock, not glossy
        };
        td.terrainLayers = new[] { layer };
        td.SetAlphamaps(0, 0, FullWeights(td.alphamapResolution));

        if (overlayLayer != null) Destroy(overlayLayer);
        overlayLayer = layer;
        overlayShown = true;
    }

    void ShowElevationColours()
    {
        TrackTerrain();
        RestoreOriginal();
    }

    void RestoreOriginal()
    {
        if (!overlayShown) return;
        overlayShown = false;
        TerrainData td = trackedData;
        if (td != null && originalLayers != null)
        {
            td.terrainLayers = originalLayers;
            if (originalMaps != null && originalMaps.GetLength(2) == td.alphamapLayers
                && originalMaps.GetLength(0) == td.alphamapResolution)
                td.SetAlphamaps(0, 0, originalMaps);
        }
        if (overlayLayer != null) Destroy(overlayLayer);
        overlayLayer = null;
    }

    // Terraingen makes a new TerrainData per heightmap (and for the Perlin fallback). The new
    // one already has its elevation colours; the cache belonged to the old one.
    TerrainData TrackTerrain()
    {
        Terrain t = FindTerrain();
        TerrainData td = t != null ? t.terrainData : null;
        if (td != trackedData)
        {
            trackedData = td;
            originalLayers = null;
            originalMaps = null;
            overlayShown = false;
        }
        return td;
    }

    Terrain FindTerrain()
    {
        if (terraingen == null && !searchedTerraingen)
        {
            searchedTerraingen = true;
            terraingen = GetComponent<Terraingen>();
            if (terraingen == null) terraingen = FindAnyObjectByType<Terraingen>();
        }
        if (terraingen != null && terraingen.terrain != null) return terraingen.terrain;
        return Terrain.activeTerrain;
    }

    float[,,] FullWeights(int res)
    {
        if (fullWeights == null || fullWeights.GetLength(0) != res)
        {
            fullWeights = new float[res, res, 1];
            for (int y = 0; y < res; y++)
                for (int x = 0; x < res; x++)
                    fullWeights[y, x, 0] = 1f;
        }
        return fullWeights;
    }

    // ---------------------------------------------------------------
    // Layer list + labels
    // ---------------------------------------------------------------

    // Site order first, then anything that arrived without being listed.
    List<string> OrderedNames()
    {
        var names = new List<string>();
        if (site != null && site.layers != null)
            foreach (SiteLayer l in site.layers)
                if (l != null && !string.IsNullOrEmpty(l.name) && textures.ContainsKey(l.name) && !names.Contains(l.name))
                    names.Add(l.name);
        foreach (string n in arrivalOrder)
            if (!names.Contains(n)) names.Add(n);
        return names;
    }

    SiteLayer FindSiteLayer(string layerName)
    {
        if (site == null || site.layers == null) return null;
        foreach (SiteLayer l in site.layers)
            if (l != null && l.name == layerName) return l;
        return null;
    }

    bool SiteHasLayer(string layerName) => FindSiteLayer(layerName) != null;

    // Name and credit of the layer shown (existing strings, nothing allocated).
    void CurrentLabel(out string label, out string credit)
    {
        SiteLayer info = selected != null ? FindSiteLayer(selected) : null;
        label = selected == null ? ElevationLabel
              : info != null && !string.IsNullOrEmpty(info.label) ? info.label : selected;
        credit = selected == null ? ElevationCredit : info?.credit;
    }

    // In VR the label is a world-space panel lower-left in view; hidden without site layers.
    void UpdateVrLabel()
    {
        bool show = showLabel && MarsmapInput.XRActive && HasLayers;
        if (!show)
        {
            if (vrLabel != null && vrLabelVisible)
            {
                vrLabel.SetVisible(false);
                vrLabelVisible = false;
            }
            return;
        }

        if (vrLabel == null)
        {
            vrLabel = VRHud.CreateLabel("Layer label", VrLabelOffset);
            if (vrLabel == null) return;
            vrShownLabel = vrShownCredit = null;
            vrLabelVisible = false;
        }

        CurrentLabel(out string label, out string credit);
        credit = credit ?? "";
        if (!ReferenceEquals(label, vrShownLabel) || !ReferenceEquals(credit, vrShownCredit))
        {
            vrLabel.Set(label + "  (B: next layer)", credit);
            vrShownLabel = label;
            vrShownCredit = credit;
        }
        if (!vrLabelVisible)
        {
            vrLabel.SetVisible(true);
            vrLabelVisible = true;
        }
    }

    void OnGUI()
    {
        if (!showLabel || !HasLayers || MarsmapInput.XRActive) return; // VR uses vrLabel instead

        if (titleStyle == null)
        {
            titleStyle = new GUIStyle(GUI.skin.label) { fontSize = 14, fontStyle = FontStyle.Bold, wordWrap = false };
            titleStyle.normal.textColor = Color.white;
            creditStyle = new GUIStyle(GUI.skin.label) { fontSize = 11, wordWrap = true };
            creditStyle.normal.textColor = new Color(0.85f, 0.85f, 0.85f);
        }

        CurrentLabel(out string label, out string credit);
        var title = new GUIContent(label + "  (L: next layer)");
        var creditContent = new GUIContent(credit ?? "");

        const float pad = 6f, margin = 10f;
        Vector2 titleSize = titleStyle.CalcSize(title);
        float maxInner = Mathf.Max(100f, Screen.width - 2f * (margin + pad));
        float creditWidth = string.IsNullOrEmpty(credit) ? 0f : creditStyle.CalcSize(creditContent).x;
        float inner = Mathf.Min(Mathf.Max(titleSize.x, creditWidth), maxInner);
        float creditHeight = creditWidth > 0f ? creditStyle.CalcHeight(creditContent, inner) : 0f;

        var box = new Rect(margin, Screen.height - margin - titleSize.y - creditHeight - 2f * pad,
                           inner + 2f * pad, titleSize.y + creditHeight + 2f * pad);
        Color previous = GUI.color;
        GUI.color = new Color(0f, 0f, 0f, 0.65f);
        GUI.DrawTexture(box, Texture2D.whiteTexture);
        GUI.color = previous;

        GUI.Label(new Rect(box.x + pad, box.y + pad, inner, titleSize.y), title, titleStyle);
        if (creditHeight > 0f)
            GUI.Label(new Rect(box.x + pad, box.y + pad + titleSize.y, inner, creditHeight), creditContent, creditStyle);
    }

    void ClearTextures()
    {
        foreach (Texture2D tex in textures.Values)
            if (tex != null) Destroy(tex);
        textures.Clear();
        arrivalOrder.Clear();
    }

    void OnDestroy()
    {
        RestoreOriginal(); // leave the terrain with its elevation colours, not a destroyed texture
        ClearTextures();
        if (vrLabel != null) Destroy(vrLabel.gameObject);
        vrLabel = null;
    }
}
