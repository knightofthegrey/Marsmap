using UnityEngine;

public class Initialize : MonoBehaviour
{
    public float extraHeightOffset = 0.5f;
    // Start is called once before the first execution of Update after the MonoBehaviour is created
    void Start()
    {
        Terrain terrain = Terrain.activeTerrain;
        if (terrain == null) return;
        float terrainY = terrain.SampleHeight(transform.position) + terrain.transform.position.y;
        float halfHeight = 1f;
        if (TryGetComponent<Collider>(out Collider col))
        {
            halfHeight = col.bounds.extents.y;
        }
        Vector3 pos = transform.position;
        pos.y = terrainY + halfHeight + extraHeightOffset;
        transform.position = pos;
    }

    // Update is called once per frame
    void Update()
    {
        
    }
}
