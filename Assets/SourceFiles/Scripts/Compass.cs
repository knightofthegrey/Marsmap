using UnityEngine;

public class Compass : MonoBehaviour
{
    public Transform playerTransform;
    public Transform compassDial;

    // Update is called once per frame
    void Update()
    {
        if (playerTransform == null || compassDial == null) return;

        float headingAngle = playerTransform.eulerAngles.y;
        float dialZ = headingAngle;

        compassDial.localEulerAngles = new Vector3(0f, 0f, dialZ);
    }
}
