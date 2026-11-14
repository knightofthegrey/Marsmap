using UnityEngine;

public class CameraFollow : MonoBehaviour
{
    public Transform target;
    public Vector3 offset = new Vector3(0f, 2f, -10f);
    public float smoothTime = 0.25f;

    private Vector3 currentVelocity = Vector3.zero;
    // Start is called once before the first execution of Update after the MonoBehaviour is created
    private void LateUpdate()
    {
        if (target == null) return;
        Vector3 targetPosition = target.position + offset;

        transform.position = Vector3.SmoothDamp(
            transform.position,
            targetPosition,
            ref currentVelocity,
            smoothTime);
    }
}
