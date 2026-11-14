using UnityEngine;

public class Terraingen : MonoBehaviour
{
    public Transform player;
    public Terrain terrain;
    
    public float scale = 20f;
    [ContextMenu("Test Terraingen")]
    async void Start()
    {
        await GenerateTerrain();

        PlacePlayer();
    }

    async Awaitable GenerateTerrain()
    {
        TerrainData terrainData = terrain.terrainData;

        int width = terrainData.heightmapResolution;
        int height = terrainData.heightmapResolution;
        float[,] heights = new float[width, height];

        float offsetX = Random.Range(0f, 9999f);
        float offsetY = Random.Range(0f, 9999f);

        for (int x = 0; x < width; x++)
        {
            for (int y = 0; y < height; y++)
            {
                float xCoord = (float)x / width * scale + offsetX;
                float yCoord = (float)y / height * scale + offsetY;
                heights[x, y] = Mathf.PerlinNoise(xCoord, yCoord);
            }
        }

        terrainData.SetHeights(0, 0, heights);
        terrain.terrainData = terrainData;
    }

    void PlacePlayer()
    {
        if (player == null || terrain == null) return;

        Vector3 pos = player.position;
        float terrainHeight = terrain.SampleHeight(pos) + terrain.transform.position.y;
        pos.y = terrainHeight + 0.1f; // Slight buffer so feet don't clip

        // Safely move a CharacterController if one is attached
        CharacterController cc = player.GetComponent<CharacterController>();
        if (cc != null) cc.enabled = false;

        player.position = pos;

        if (cc != null) cc.enabled = true;
    }
}
