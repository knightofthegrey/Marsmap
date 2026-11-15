using UnityEditor.Experimental.GraphView;
using UnityEngine;

public class PointAt : MonoBehaviour
{
    public Transform playerTransform;
    public Transform dest;
    public Transform compassDial;

    // Update is called once per frame
    void Update()
    {
        if (playerTransform == null || compassDial == null || dest == null) return;

        Vector2 direction = (Vector2)dest.position - (Vector2)playerTransform.position;

        float dialZ =  Mathf.Atan2(direction.y, direction.x) * Mathf.Rad2Deg - playerTransform.eulerAngles.y;

        compassDial.localEulerAngles = new Vector3(0f, 0f, dialZ);
    }
}
