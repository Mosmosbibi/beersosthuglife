using UnityEngine;
using Blocks.Gameplay.Core;

public class DashAbility : MonoBehaviour, IMovementAbility
{
    [Header("Dash Settings")]
    [SerializeField] private float dashForce = 50f;
    [SerializeField] private float dashDuration = 0.2f;
    [SerializeField] private float dashCooldown = 1.0f;
    [SerializeField] private float staminaCost = 15f;
    [SerializeField] private bool requireGrounded = false;
    [SerializeField] private bool allowAirDash = true;
    [SerializeField] private int maxAirDashes = 1;

    [Header("Effects")]
    [SerializeField] private GameObject dashStartEffect;
    [SerializeField] private SoundDef dashStartSound;

    // Higher priority overrides standard movement
    public int Priority => 20;
    public float StaminaCost => staminaCost;

    // Internal State
    private bool m_IsDashing;
    private float m_DashTimer;
    private CoreMovement m_Motor;
    private float m_CooldownTimer;
    private Vector3 m_DashDirection;
    private int m_RemainingAirDashes;

    public void Initialize(CoreMovement motor)
    {
        m_Motor = motor;
        m_Motor.OnGroundedStateChanged += OnGroundedStateChanged;
        m_RemainingAirDashes = maxAirDashes;
    }

    // The physics logic applied every frame
    public MovementModifier Process()
    {
        var modifier = new MovementModifier();

        // Handle Cooldown
        if (m_CooldownTimer > 0)
        {
            m_CooldownTimer -= Time.deltaTime;
        }

        // Handle Active Dash
        if (m_IsDashing)
        {
            m_DashTimer -= Time.deltaTime;

            if (m_DashTimer <= 0)
            {
                EndDash();
            }
            else
            {
                // Apply Dash Velocity
                modifier.ArealVelocity = m_DashDirection * dashForce;
                // Disable gravity during dash
                modifier.OverrideGravity = true;
            }
        }

        return modifier;
    }

    public bool TryActivate()
    {
        // Validation Checks
        if (m_CooldownTimer > 0 || m_IsDashing) return false;
        if (requireGrounded && !m_Motor.IsGrounded) return false;
        if (!m_Motor.IsGrounded && !allowAirDash) return false;
        if (!m_Motor.IsGrounded && m_RemainingAirDashes <= 0) return false;

        // Calculate Direction
        Vector3 dashDir = CalculateDashDirection();

        // Fallback if no input: dash forward
        if (dashDir.magnitude < 0.1f)
        {
            dashDir = m_Motor.RotationTransform != null
                ? m_Motor.RotationTransform.forward
                : m_Motor.transform.forward;
        }

        // Start Dash
        StartDash(dashDir.normalized);
        return true;
    }

    private Vector3 CalculateDashDirection()
    {
        // If there is movement input, dash in that direction relative to camera/character
        if (m_Motor.MoveInput.magnitude > 0.1f)
        {
            Vector3 inputDirection = new Vector3(m_Motor.MoveInput.x, 0.0f, m_Motor.MoveInput.y);
            switch (m_Motor.directionMode)
            {
                case CoreMovement.MovementDirectionMode.CharacterRelative:
                    return m_Motor.transform.rotation * inputDirection;
                case CoreMovement.MovementDirectionMode.CameraRelative:
                    return Quaternion.Euler(0.0f, m_Motor.TargetRotationY, 0.0f) * inputDirection;
                default:
                    return inputDirection;
            }
        }

        // Otherwise default to forward
        Transform rotationTransform = m_Motor.RotationTransform != null
            ? m_Motor.RotationTransform
            : m_Motor.transform;

        return rotationTransform.forward;
    }

    private void StartDash(Vector3 direction)
    {
        m_IsDashing = true;
        m_DashTimer = dashDuration;
        m_CooldownTimer = dashCooldown;
        m_DashDirection = new Vector3(direction.x, 0f, direction.z).normalized;

        if (!m_Motor.IsGrounded)
        {
            m_RemainingAirDashes--;
        }

        // Reset vertical velocity for a snappy dash feel
        m_Motor.SetVerticalVelocity(0f);

        // Play Effects
        if (dashStartEffect != null)
        {
            CoreDirector.CreatePrefabEffect(dashStartEffect)
                .WithPosition(m_Motor.transform.position)
                .WithRotation(Quaternion.LookRotation(m_DashDirection))
                .WithName("DashStart")
                .WithDuration(dashDuration + 0.5f)
                .Create();
        }

        if (dashStartSound != null)
        {
            CoreDirector.RequestAudio(dashStartSound)
                .AttachedTo(m_Motor.transform)
                .Play();
        }
    }

    private void EndDash()
    {
        m_IsDashing = false;
        m_DashTimer = 0f;
    }

    private void OnGroundedStateChanged(bool isGrounded)
    {
        if (isGrounded)
        {
            // Reset air dashes
            m_RemainingAirDashes = maxAirDashes;
        }
    }

    private void OnDestroy()
    {
        if (m_Motor != null)
        {
            m_Motor.OnGroundedStateChanged -= OnGroundedStateChanged;
        }
    }
}


