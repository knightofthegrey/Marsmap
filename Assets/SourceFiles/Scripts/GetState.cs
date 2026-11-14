using TMPro;
using UnityEngine;
using UnityEngine.Rendering;

public class GetState : MonoBehaviour
{
    [Header("References")]
    public Transform targetTransform;
    public TextMeshProUGUI angleText;
    public TMP_Text coordinates;

    // Update is called once per frame
    void Update()
    {
        if (targetTransform == null || angleText == null) return;

        float horizontalAngle = targetTransform.eulerAngles.y;

        float rawPitch = targetTransform.eulerAngles.x;
        float verticalAngle = (rawPitch > 180f) ? rawPitch - 360f : rawPitch;

        Vector3 pos = targetTransform.position;

        angleText.text = $"Yaw {horizontalAngle:F0}\nPitch: {verticalAngle:F0}" +
            $"\n X: {pos.x:F2} Y: {pos.y:F2}";
    }
}
