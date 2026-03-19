using UnityEngine;
using Unity.Netcode;
using System.Collections;
using Blocks.Gameplay.Core;

/// <summary>
/// Changes the scale of the interactor with smooth animation and optional physics adjustments.
/// Fully synchronized across the network using RPCs.
/// Great for creating shrink/grow power-ups or puzzle mechanics.
/// </summary>
public class ChangeSizeEffect : NetworkBehaviour, IInteractionEffect
{
    [Header("Size Settings")]
    [SerializeField] private Vector3 targetScale = new Vector3(2f, 2f, 2f);
    [SerializeField] private float transitionDuration = 0.5f;
    [SerializeField] private AnimationCurve scaleCurve = AnimationCurve.EaseInOut(0, 0, 1, 1);
    [SerializeField] private bool resetOnNextInteraction = false;

    [Header("Effect Priority")]
    [SerializeField] private int priority = 0;

    [Header("Visual Effects")]
    [SerializeField] private GameObject sizeChangeVFX;
    [SerializeField] private SoundDef sizeChangeSound;

    public int Priority => priority;

    private static readonly System.Collections.Generic.Dictionary<ulong, TransformData> k_OriginalData =
        new System.Collections.Generic.Dictionary<ulong, TransformData>();

    private static readonly System.Collections.Generic.Dictionary<ulong, Coroutine> k_ActiveCoroutines =
        new System.Collections.Generic.Dictionary<ulong, Coroutine>();

    [System.Serializable]
    private class TransformData
    {
        public Vector3 originalScale;
    }

    public IEnumerator ApplyEffect(GameObject interactor, GameObject interactable)
    {
        // Get NetworkObject to identify the player across the network
        if (!interactor.TryGetComponent<NetworkObject>(out var netObj))
        {
            Debug.LogWarning("[ChangeSizeEffect] Interactor does not have a NetworkObject component.", interactor);
            yield break;
        }

        ulong networkId = netObj.NetworkObjectId;
        bool shouldReset = resetOnNextInteraction && k_OriginalData.ContainsKey(networkId);
        Vector3 targetScaleValue = shouldReset ? k_OriginalData[networkId].originalScale : targetScale;
        TriggerSizeChangeRpc(networkId, targetScaleValue, shouldReset);

        if (netObj.IsOwner)
        {
            yield return new WaitForSeconds(transitionDuration);
        }
    }

    /// <summary>
    /// RPC that triggers the size change animation on all clients.
    /// </summary>
    [Rpc(SendTo.Everyone)]
    private void TriggerSizeChangeRpc(ulong networkObjectId, Vector3 newTargetScale, bool isReset)
    {
        // Find the NetworkObject by ID
        if (!NetworkManager.Singleton.SpawnManager.SpawnedObjects.TryGetValue(networkObjectId, out var netObj))
        {
            Debug.LogWarning($"[ChangeSizeEffect] Could not find NetworkObject with ID {networkObjectId}");
            return;
        }

        GameObject interactor = netObj.gameObject;

        // Stop any existing size change coroutine for this object
        if (k_ActiveCoroutines.TryGetValue(networkObjectId, out var existingCoroutine))
        {
            if (existingCoroutine != null)
            {
                StopCoroutine(existingCoroutine);
            }

            k_ActiveCoroutines.Remove(networkObjectId);
        }

        // Store original data if not already stored
        if (!k_OriginalData.ContainsKey(networkObjectId))
        {
            k_OriginalData[networkObjectId] = CaptureOriginalData(interactor);
        }

        // Start the size change animation
        var coroutine = StartCoroutine(AnimateSizeChange(interactor, networkObjectId, newTargetScale, isReset));
        k_ActiveCoroutines[networkObjectId] = coroutine;

        // Play effects
        PlayEffects(interactor.transform.position);
    }

    /// <summary>
    /// Coroutine that smoothly animates the scale change on all clients.
    /// </summary>
    private IEnumerator AnimateSizeChange(GameObject interactor, ulong networkObjectId, Vector3 endScale, bool isReset)
    {
        if (interactor == null) yield break;

        Vector3 startScale = interactor.transform.localScale;

        // Animate the scale change
        float elapsed = 0f;
        while (elapsed < transitionDuration)
        {
            if (interactor == null) yield break;

            elapsed += Time.deltaTime;
            float t = scaleCurve.Evaluate(elapsed / transitionDuration);
            interactor.transform.localScale = Vector3.Lerp(startScale, endScale, t);
            yield return null;
        }

        // Ensure we end at exactly the target scale
        if (interactor != null)
        {
            interactor.transform.localScale = endScale;
            if (isReset)
            {
                k_OriginalData.Remove(networkObjectId);
            }
        }

        // Clean up coroutine reference
        k_ActiveCoroutines.Remove(networkObjectId);
    }

    /// <summary>
    /// Plays visual and audio effects at the specified position.
    /// </summary>
    private void PlayEffects(Vector3 position)
    {
        if (sizeChangeVFX != null)
        {
            CoreDirector.CreatePrefabEffect(sizeChangeVFX)
                .WithPosition(position)
                .WithName("SizeChange")
                .WithDuration(2f)
                .Create();
        }

        if (sizeChangeSound != null)
        {
            CoreDirector.RequestAudio(sizeChangeSound)
                .WithPosition(position)
                .Play();
        }
    }

    /// <summary>
    /// Captures the original transform and movement data from the interactor.
    /// </summary>
    private TransformData CaptureOriginalData(GameObject interactor)
    {
        var data = new TransformData { originalScale = interactor.transform.localScale, };
        return data;
    }

    public void CancelEffect(GameObject interactor)
    {
        if (!interactor.TryGetComponent<NetworkObject>(out var netObj))
        {
            return;
        }

        ulong networkId = netObj.NetworkObjectId;
        if (k_ActiveCoroutines.TryGetValue(networkId, out var coroutine))
        {
            if (coroutine != null)
            {
                StopCoroutine(coroutine);
            }

            k_ActiveCoroutines.Remove(networkId);
        }

        if (k_OriginalData.TryGetValue(networkId, out var data))
        {
            TriggerSizeChangeRpc(networkId, data.originalScale, true);
        }
    }

    /// <summary>
    /// Cleanup when the effect is destroyed or the scene changes.
    /// </summary>
    public override void OnDestroy()
    {
        foreach (var coroutine in k_ActiveCoroutines.Values)
        {
            if (coroutine != null)
            {
                StopCoroutine(coroutine);
            }
        }

        k_ActiveCoroutines.Clear();
    }
}

