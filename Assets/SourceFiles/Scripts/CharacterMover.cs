using Unity.Collections.Tests.CoreCLR.TestJobs;
using UnityEngine;
using UnityEngine.InputSystem;

[RequireComponent(typeof(CharacterController))]
public class CharacterMover : MonoBehaviour
{
    [Header("Movement Settings")]
    public float walkSpeed = 5.0f;
    public float sprintSpeed = 9.0f;
    public float jumpHeight = 1.5f;
    public float gravity = -19.62f;

    [Header("Look Settings")]
    public Transform playerCamera;
    public float mouseSensitivity = 0.15f;
    public float maxLookAngle = 85.0f;

    [Header("Input Actions")]
    public InputAction moveAction;
    public InputAction lookAction;
    public InputAction jumpAction;
    public InputAction sprintAction;

    private CharacterController controller;
    private Vector3 velocity;
    private float xRotation = 0f;

    private void OnEnable()
    {
        moveAction.Enable();
        lookAction.Enable();
        jumpAction.Enable();
        sprintAction.Enable();
    }

    private void OnDisable()
    {
        moveAction.Disable();
        lookAction.Disable();
        jumpAction.Disable();
        sprintAction.Disable();
    }

    void Start()
    {
        controller = GetComponent<CharacterController>();

        Cursor.lockState = CursorLockMode.Locked;
        Cursor.visible = false;
    }

    void Update()
    {
        HandleLook();
        HandleMovement();
    }

    private void HandleLook()
    {
        // Read mouse delta or stick input
        Vector2 lookInput = lookAction.ReadValue<Vector2>();
        float mouseX = lookInput.x * mouseSensitivity;
        float mouseY = lookInput.y * mouseSensitivity;

        // Rotate camera vertically and clamp
        xRotation -= mouseY;
        xRotation = Mathf.Clamp(xRotation, -maxLookAngle, maxLookAngle);
        playerCamera.localRotation = Quaternion.Euler(xRotation, 0f, 0f);

        // Rotate player horizontally
        transform.Rotate(Vector3.up * mouseX);
    }

    private void HandleMovement()
    {
        if (controller.isGrounded && velocity.y < 0)
        {
            velocity.y = -2f;
        }

        // Read 2D movement vector
        Vector2 moveInput = moveAction.ReadValue<Vector2>();

        // Check if sprint button is held
        float currentSpeed = sprintAction.IsPressed() ? sprintSpeed : walkSpeed;

        Vector3 move = transform.right * moveInput.x + transform.forward * moveInput.y;
        controller.Move(move.normalized * currentSpeed * Time.deltaTime);

        // Check if jump was triggered this frame
        if (jumpAction.WasPressedThisFrame() && controller.isGrounded)
        {
            velocity.y = Mathf.Sqrt(jumpHeight * -2f * gravity);
        }

        velocity.y += gravity * Time.deltaTime;
        controller.Move(velocity * Time.deltaTime);
    }
}
