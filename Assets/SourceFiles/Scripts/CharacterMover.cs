using Unity.Cinemachine;
using Unity.Collections.Tests.CoreCLR.TestJobs;
using UnityEngine;
using UnityEngine.InputSystem;

[RequireComponent(typeof(CharacterController))]
public class CharacterMover : MonoBehaviour
{
    [Header("Movement Settings")]
    public float moveSpeed = 5.0f;
    public float jumpHeight = 1.5f;
    public float gravity = -7.42f;

    public CinemachinePanTilt panTiltCamera;
    private CharacterController controller;
    private InputSystem_Actions inputActions;
    private Vector3 verticalVelocity;

    private void Awake()
    {
        controller = GetComponent<CharacterController>();
        inputActions = new InputSystem_Actions();
        OnEnable();
        Cursor.lockState = CursorLockMode.Locked;
        Cursor.visible = false;
    }

    private void OnEnable()
    {
        inputActions.Enable();
    }

    private void OnDisable()
    {
        inputActions.Disable();
    }

    void Update()
    {
        HandleMovement();
        HandleVertical();
    }

    private void HandleMovement()
    {
        Vector2 input = inputActions.Player.Move.ReadValue<Vector2>();
        Debug.Log($"{input}");

        // Align player yaw with the Cinemachine Pan angle
        float yaw = panTiltCamera.PanAxis.Value;
        Quaternion yawRotation = Quaternion.Euler(0f, yaw, 0f);
        transform.rotation = yawRotation;

        // Calculate world-space movement relative to look direction
        Vector3 moveDirection = yawRotation * new Vector3(input.x, 0f, input.y);
        controller.Move(moveDirection * (moveSpeed * Time.deltaTime));
    }
    private void HandleVertical()
    {
        if (controller.isGrounded && verticalVelocity.y < 0f)
        {
            verticalVelocity.y = -2f;
        }

        if (inputActions.Player.Jump.WasPressedThisFrame() && controller.isGrounded)
        {
            verticalVelocity.y = Mathf.Sqrt(jumpHeight * -2f * gravity);
        }

        verticalVelocity.y += gravity * Time.deltaTime;
        controller.Move(verticalVelocity * Time.deltaTime);
    }
}
