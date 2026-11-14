using UnityEngine;

public class Terraingen : MonoBehaviour
{
    
    public float scale = 20f;
    [ContextMenu("Test Terraingen")]
    void Start()
    {
        Terrain terrain = GetComponent<Terrain>();
        terrain.terrainData = GenerateTerrain(terrain.terrainData);
    }

    TerrainData GenerateTerrain(TerrainData terrainData)
    {
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
        return terrainData;
    }
}
