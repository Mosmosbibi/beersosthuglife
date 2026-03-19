using UnityEngine;
using Unity.Netcode;
using Blocks.Gameplay.Core;

public class DashAddon : NetworkBehaviour, IPlayerAddon
{
    [Header("References")]
    [SerializeField] private DashAbility dashAbility;
    [SerializeField] private GameEvent onPrimaryActionPressed;

    [Header("Feedback")]
    [SerializeField] private SoundDef insufficientStaminaSound;
    [SerializeField] private float warningCooldown = 2.0f;

    private CorePlayerManager m_PlayerManager;
    private CoreStatsHandler m_StatsHandler;
    private float m_LastWarningTime;

    public void Initialize(CorePlayerManager playerManager)
    {
        m_PlayerManager = playerManager;
        m_StatsHandler = playerManager.CoreStats;
        if (dashAbility == null)
        {
            dashAbility = GetComponent<DashAbility>();
        }
    }

    // Enable input listening when player spawns
    public void OnPlayerSpawn()
    {
        if (!m_PlayerManager.IsOwner) return;
        if (onPrimaryActionPressed != null)
        {
            onPrimaryActionPressed.RegisterListener(HandleDashInput);
        }
    }

    // Cleanup when player despawns
    public void OnPlayerDespawn()
    {
        if (!m_PlayerManager.IsOwner) return;
        if (onPrimaryActionPressed != null)
        {
            onPrimaryActionPressed.UnregisterListener(HandleDashInput);
        }
    }

    public void OnLifeStateChanged(PlayerLifeState previousState, PlayerLifeState newState) { }

    private void HandleDashInput()
    {
        if (dashAbility == null)
        {
            Debug.LogWarning("[DashAddon] DashAbility component not found.", this);
            return;
        }

        float staminaCost = dashAbility.StaminaCost;
        // Get current stamina from stats system
        float currentStamina = m_StatsHandler.GetCurrentValue(StatKeys.Stamina);

        // Check Stamina
        if (currentStamina < staminaCost)
        {
            HandleInsufficientStamina();
            return;
        }

        // Attempt Dash
        if (dashAbility.TryActivate())
        {
            // Consume Stamina
            m_StatsHandler.ModifyStat(
                StatKeys.Stamina,
                -staminaCost,
                m_PlayerManager.OwnerClientId,
                ModificationSource.Consumption
            );
        }
    }

    private void HandleInsufficientStamina()
    {
        // Prevent spamming the warning sound
        if (Time.time - m_LastWarningTime < warningCooldown)
        {
            return;
        }

        m_LastWarningTime = Time.time;
        if (insufficientStaminaSound != null)
        {
            CoreDirector.RequestAudio(insufficientStaminaSound)
                .AttachedTo(transform)
                .Play(0.5f);
        }
    }
}



