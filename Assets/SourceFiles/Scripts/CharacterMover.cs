using System.Collections.Generic;
using Unity.Cinemachine;
using UnityEngine;
using UnityEngine.InputSystem;

[RequireComponent(typeof(CharacterController))]
public class CharacterMover : MonoBehaviour
{
    [Header("Movement Settings")]
    public float moveSpeed = 5.0f;
    [Tooltip("Hold Left Shift to move this many times faster (the real terrain is 2 km across)")]
    [SerializeField] private float sprintMultiplier = 3f;
    public float jumpHeight = 1.5f;
    public float gravity = -7.42f;

    [Header("Look Settings")]
    [SerializeField] private CinemachinePanTilt panTiltCamera;
    [Tooltip("Left empty: WASD / arrow keys")]
    [SerializeField] private InputAction moveAction;
    [Tooltip("Left empty: Space")]
    [SerializeField] private InputAction jumpAction;

    private CharacterController controller;
    private Vector3 verticalVelocity;
    private bool warnedNoCamera;

    private void Awake()
    {
        controller = GetComponent<CharacterController>();
        Cursor.lockState = CursorLockMode.Locked;
        Cursor.visible = false;
        AddDefaultBindings(); // Awake runs before OnEnable, while the actions are still disabled
    }

    // The actions in the scene have no bindings, so nothing moved. Bindings set in the
    // Inspector always win: defaults are only added to an action that has none.
    private void AddDefaultBindings()
    {
        var added = new List<string>();

        if (moveAction == null)
            moveAction = new InputAction("Move", InputActionType.Value, expectedControlType: "Vector2");
        if (moveAction.bindings.Count == 0)
        {
            moveAction.AddCompositeBinding("2DVector")
                .With("Up", "<Keyboard>/w").With("Down", "<Keyboard>/s")
                .With("Left", "<Keyboard>/a").With("Right", "<Keyboard>/d");
            moveAction.AddCompositeBinding("2DVector")
                .With("Up", "<Keyboard>/upArrow").With("Down", "<Keyboard>/downArrow")
                .With("Left", "<Keyboard>/leftArrow").With("Right", "<Keyboard>/rightArrow");
            added.Add("Move = WASD / arrow keys");
        }

        if (jumpAction == null)
            jumpAction = new InputAction("Jump", InputActionType.Button);
        if (jumpAction.bindings.Count == 0)
        {
            jumpAction.AddBinding("<Keyboard>/space");
            added.Add("Jump = Space");
        }

        if (added.Count > 0)
            Debug.Log("[CharacterMover] No bindings set in the Inspector, using defaults: " +
                      string.Join(", ", added) + " (hold Left Shift to sprint)", this);
    }

    private void OnEnable()
    {
        moveAction?.Enable();
        jumpAction?.Enable();
    }

    private void OnDisable()
    {
        moveAction?.Disable();
        jumpAction?.Disable();
    }

    void Update()
    {
        HandleMovement();
        HandleVertical();
    }

    private void HandleMovement()
    {
        Vector2 input = moveAction.ReadValue<Vector2>();

        // Align player yaw with the Cinemachine Pan angle (or keep the current yaw without one)
        float yaw;
        if (panTiltCamera != null)
        {
            yaw = panTiltCamera.PanAxis.Value;
        }
        else
        {
            if (!warnedNoCamera)
            {
                warnedNoCamera = true;
                Debug.LogWarning("[CharacterMover] Pan Tilt Camera is not assigned; moving relative to the player's own facing.", this);
            }
            yaw = transform.eulerAngles.y;
        }
        Quaternion yawRotation = Quaternion.Euler(0f, yaw, 0f);
        transform.rotation = yawRotation;

        Keyboard keyboard = Keyboard.current;
        float speed = moveSpeed;
        if (keyboard != null && keyboard.leftShiftKey.isPressed) speed *= sprintMultiplier;

        // Calculate world-space movement relative to look direction
        Vector3 moveDirection = yawRotation * new Vector3(input.x, 0f, input.y);
        controller.Move(moveDirection * (speed * Time.deltaTime));
    }
    private void HandleVertical()
    {
        if (controller.isGrounded && verticalVelocity.y < 0f)
        {
            verticalVelocity.y = -2f;
        }

        if (jumpAction.WasPressedThisFrame() && controller.isGrounded)
        {
            verticalVelocity.y = Mathf.Sqrt(jumpHeight * -2f * gravity);
        }

        verticalVelocity.y += gravity * Time.deltaTime;
        controller.Move(verticalVelocity * Time.deltaTime);
    }
}
