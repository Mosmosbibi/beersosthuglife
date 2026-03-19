using UnityEngine;
using Unity.Netcode;
using Blocks.Gameplay.Core;
using Blocks.Gameplay.Shooter;
using System.Collections.Generic;

public class ChainLightningBehavior : NetworkBehaviour, IShootingBehavior
{
    [Header("Chain Lightning Settings")]
    [SerializeField] private float initialRange = 30f;
    [SerializeField] private float chainRange = 15f;
    [SerializeField] private int maxChains = 4;
    [SerializeField] private float chainAngle = 90f;
    [SerializeField] private float targetAcquisitionRadius = 0.5f;

    [Header("Damage Settings")]
    [SerializeField] private float baseDamage = 6f;
    [SerializeField] private float damageDecayPerChain = 0.7f;
    [SerializeField] private float hitForce = 50f;

    [Header("Arc Visual Settings")]
    [SerializeField] private Color lightningColor = new Color(0.4f, 0.8f, 1f, 1f);
    [SerializeField] private float arcWidthStart = 0.02f;
    [SerializeField] private float arcWidthEnd = 0.04f;
    [SerializeField] private float arcDuration = 0.15f;

    [Header("Arc Animation Settings")]
    [SerializeField] private int arcSegments = 12;
    [SerializeField] private float arcAmplitude = 0.3f;
    [SerializeField] private float arcFrequency = 8f;
    [SerializeField] private float arcAnimationSpeed = 10f;
    [SerializeField] private float arcJitter = 0.2f;

    [Header("Advanced Visual Settings")]
    [SerializeField] private Material arcMaterial;
    [SerializeField] private bool emitPointLights = true;
    [SerializeField] private float pointLightIntensity = 2f;
    [SerializeField] private float pointLightRange = 5f;

    // Internal state
    private readonly List<Vector3> m_ChainPoints = new List<Vector3>();
    private readonly List<GameObject> m_HitTargets = new List<GameObject>();
    private readonly List<LineRenderer> m_ActiveArcs = new List<LineRenderer>();
    private readonly List<Light> m_ActiveLights = new List<Light>();
    private Material m_DefaultMaterial;
    private float m_ArcTimeOffset;

    static readonly int k_SrcBlend = Shader.PropertyToID("_SrcBlend");
    static readonly int k_DstBlend = Shader.PropertyToID("_DstBlend");

    private void Awake()
    {
        if (arcMaterial == null)
        {
            m_DefaultMaterial = new Material(Shader.Find("Sprites/Default"));
            m_DefaultMaterial.SetInt(k_SrcBlend, (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
            m_DefaultMaterial.SetInt(k_DstBlend, (int)UnityEngine.Rendering.BlendMode.One);
        }
    }

    private void Update()
    {
        m_ArcTimeOffset += Time.deltaTime * arcAnimationSpeed;

        for (int i = m_ActiveArcs.Count - 1; i >= 0; i--)
        {
            var arc = m_ActiveArcs[i];
            if (arc == null)
            {
                m_ActiveArcs.RemoveAt(i);
                continue;
            }

            if (arcAnimationSpeed > 0 && arc.positionCount > 2)
            {
                AnimateArc(arc, arc.GetPosition(0), arc.GetPosition(arc.positionCount - 1));
            }
        }
    }

    public override void OnDestroy()
    {
        base.OnDestroy();
        if (m_DefaultMaterial != null)
        {
            Destroy(m_DefaultMaterial);
        }
    }

    // ═══════════════════════════════════════════════════════════════
    // IShootingBehavior Implementation
    // ═══════════════════════════════════════════════════════════════

    public void Shoot(ShootingContext context)
    {
        m_ChainPoints.Clear();
        m_HitTargets.Clear();

        Vector3 origin = context.muzzle != null ? context.muzzle.position : context.origin;
        Vector3 direction = ApplySpread(context.direction, context.currentSpread);

        m_ChainPoints.Add(origin);

        // Find initial target
        bool hitInitialTarget = FindInitialTarget(origin, direction, context,
            out Vector3 hitPoint, out IHittable hittable, out GameObject hitObject);

        if (!hitInitialTarget)
        {
            // No target - shoot to max range
            m_ChainPoints.Add(origin + direction * initialRange);
        }
        else
        {
            m_ChainPoints.Add(hitPoint);
            m_HitTargets.Add(hitObject);

            if (hittable != null)
                ApplyDamage(hittable, hitPoint, direction, baseDamage, context);

            // Chain to additional targets
            float currentDamage = baseDamage;
            Vector3 currentPoint = hitPoint;
            Vector3 currentDirection = direction;

            for (int chain = 0; chain < maxChains; chain++)
            {
                currentDamage *= damageDecayPerChain;

                if (!FindNextChainTarget(currentPoint, currentDirection, context,
                    out Vector3 nextHitPoint, out IHittable nextHittable, out GameObject nextHitObject))
                    break;

                m_ChainPoints.Add(nextHitPoint);
                m_HitTargets.Add(nextHitObject);

                if (nextHittable != null)
                {
                    Vector3 chainDirection = (nextHitPoint - currentPoint).normalized;
                    ApplyDamage(nextHittable, nextHitPoint, chainDirection, currentDamage, context);
                }

                currentDirection = (nextHitPoint - currentPoint).normalized;
                currentPoint = nextHitPoint;
            }
        }

        // Sync visuals to all clients
        PlayChainLightningVisualsRpc(m_ChainPoints.ToArray(), emitPointLights);
        context.OnAmmoConsumed?.Invoke(1);

        if (m_ChainPoints.Count > 1)
        {
            context.OnHitPointCalculated?.Invoke(m_ChainPoints[m_ChainPoints.Count - 1], Vector3.up);
        }
    }

    public bool CanShoot() => true;

    public void UpdateShooting(Vector3 updatedDirection, float deltaTime) { }

    public void StopShooting()
    {
        foreach (var arc in m_ActiveArcs)
        {
            if (arc != null) Destroy(arc.gameObject);
        }
        m_ActiveArcs.Clear();

        foreach (var light in m_ActiveLights)
        {
            if (light != null) Destroy(light.gameObject);
        }
        m_ActiveLights.Clear();
    }

    // ═══════════════════════════════════════════════════════════════
    // Target Finding
    // ═══════════════════════════════════════════════════════════════

    private bool FindInitialTarget(Vector3 origin, Vector3 direction, ShootingContext context,
        out Vector3 hitPoint, out IHittable hittable, out GameObject hitObject)
    {
        hitPoint = Vector3.zero;
        hittable = null;
        hitObject = null;

        if (Physics.SphereCast(origin, targetAcquisitionRadius, direction, out RaycastHit hit,
            initialRange, context.hitMask))
        {
            // Skip self
            if (hit.collider.transform.root == context.owner.transform.root)
            {
                if (Physics.Raycast(origin, direction, out hit, initialRange, context.hitMask))
                {
                    if (hit.collider.transform.root == context.owner.transform.root)
                        return false;
                }
                else return false;
            }

            hitPoint = hit.point;
            hitObject = hit.collider.gameObject;
            hittable = hit.collider.GetComponentInParent<IHittable>();

            // Play impact effect
            NetworkObjectReference parentRef = default;
            var parentNetObj = hit.collider.GetComponentInParent<NetworkObject>();
            if (parentNetObj != null) parentRef = parentNetObj;
            context.Weapon?.PlayImpactEffect(hit.point, hit.normal, baseDamage, parentRef);

            return true;
        }

        return false;
    }

    private bool FindNextChainTarget(Vector3 fromPoint, Vector3 previousDirection, ShootingContext context,
        out Vector3 hitPoint, out IHittable hittable, out GameObject hitObject)
    {
        hitPoint = Vector3.zero;
        hittable = null;
        hitObject = null;

        // Find all potential targets in range
        Collider[] colliders = Physics.OverlapSphere(fromPoint, chainRange, context.hitMask);

        float bestScore = float.MinValue;
        Collider bestTarget = null;

        foreach (var collider in colliders)
        {
            // Skip owner
            if (collider.transform.root == context.owner.transform.root)
                continue;

            // Skip already hit targets
            bool alreadyHit = false;
            foreach (var hitTarget in m_HitTargets)
            {
                if (collider.transform.root == hitTarget.transform.root)
                {
                    alreadyHit = true;
                    break;
                }
            }
            if (alreadyHit) continue;

            // Only chain to valid hit targets
            var targetHitProcessor = collider.GetComponentInParent<ShooterHitProcessor>();
            if (targetHitProcessor == null)
                continue;

            // Calculate direction and score
            Vector3 targetPoint = collider.ClosestPoint(fromPoint);
            Vector3 toTarget = targetPoint - fromPoint;
            float distance = toTarget.magnitude;

            if (distance < 0.1f || distance > chainRange)
                continue;

            Vector3 directionToTarget = toTarget.normalized;
            float angle = Vector3.Angle(previousDirection, directionToTarget);

            if (angle > chainAngle)
                continue;

            // Check line of sight
            if (Physics.Raycast(fromPoint, directionToTarget, out RaycastHit losHit, distance, context.hitMask))
            {
                if (losHit.collider.transform.root != collider.transform.root)
                    continue;
            }

            // Score: prefer closer and more aligned
            float score = (1f - distance / chainRange) * 0.4f + (1f - angle / chainAngle) * 0.6f;

            if (score > bestScore)
            {
                bestScore = score;
                bestTarget = collider;
            }
        }

        if (bestTarget != null)
        {
            hitPoint = bestTarget.ClosestPoint(fromPoint);
            hitObject = bestTarget.gameObject;
            hittable = bestTarget.GetComponentInParent<IHittable>();

            NetworkObjectReference parentRef = default;
            var hitProcessor = bestTarget.GetComponentInParent<ShooterHitProcessor>();
            if (hitProcessor != null && hitProcessor.NetworkObject != null)
                parentRef = hitProcessor.NetworkObject;

            Vector3 hitNormal = (fromPoint - hitPoint).normalized;
            float damage = baseDamage * Mathf.Pow(damageDecayPerChain, m_HitTargets.Count);
            context.Weapon?.PlayImpactEffect(hitPoint, hitNormal, damage, parentRef);

            return true;
        }

        return false;
    }

    // ═══════════════════════════════════════════════════════════════
    // Damage
    // ═══════════════════════════════════════════════════════════════

    private void ApplyDamage(IHittable hittable, Vector3 hitPoint, Vector3 direction,
        float damage, ShootingContext context)
    {
        var hitInfo = new HitInfo
        {
            amount = damage,
            hitPoint = hitPoint,
            hitNormal = -direction,
            attackerId = context.ownerClientId,
            impactForce = direction * hitForce
        };

        hittable.OnHit(hitInfo);
        context.OnTargetHit?.Invoke(
            hittable as MonoBehaviour != null ? (hittable as MonoBehaviour).gameObject : null,
            hitInfo);
    }

    // ═══════════════════════════════════════════════════════════════
    // Visuals (synced via RPC)
    // ═══════════════════════════════════════════════════════════════

    [Rpc(SendTo.Everyone)]
    private void PlayChainLightningVisualsRpc(Vector3[] chainPoints, bool createPointLights)
    {
        m_ChainPoints.Clear();
        m_ChainPoints.AddRange(chainPoints);

        CreateLightningArcs();

        if (createPointLights)
            CreatePointLights();
    }

    private void CreateLightningArcs()
    {
        if (m_ChainPoints.Count < 2) return;

        for (int i = 0; i < m_ChainPoints.Count - 1; i++)
        {
            LineRenderer arc = CreateArcRenderer();
            SetupArcPositions(arc, m_ChainPoints[i], m_ChainPoints[i + 1]);
            m_ActiveArcs.Add(arc);
            Destroy(arc.gameObject, arcDuration);
        }
    }

    private LineRenderer CreateArcRenderer()
    {
        GameObject arcObject = new GameObject("LightningArc");
        LineRenderer arc = arcObject.AddComponent<LineRenderer>();

        arc.material = arcMaterial != null ? arcMaterial : m_DefaultMaterial;
        arc.startColor = lightningColor;
        arc.endColor = lightningColor * 0.8f;
        arc.startWidth = arcWidthStart;
        arc.endWidth = arcWidthEnd;
        arc.numCapVertices = 4;
        arc.numCornerVertices = 4;
        arc.useWorldSpace = true;
        arc.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        arc.receiveShadows = false;

        return arc;
    }

    private void SetupArcPositions(LineRenderer arc, Vector3 start, Vector3 end)
    {
        arc.positionCount = arcSegments;

        Vector3 direction = (end - start).normalized;

        Vector3 up = Vector3.Cross(direction, Vector3.right);
        if (up.magnitude < 0.001f)
            up = Vector3.Cross(direction, Vector3.forward);
        up.Normalize();

        Vector3 right = Vector3.Cross(direction, up).normalized;
        Vector3[] positions = new Vector3[arcSegments];

        for (int i = 0; i < arcSegments; i++)
        {
            float t = i / (float)(arcSegments - 1);
            Vector3 basePosition = Vector3.Lerp(start, end, t);

            if (i == 0 || i == arcSegments - 1)
            {
                positions[i] = basePosition;
                continue;
            }

            float noiseOffset = m_ArcTimeOffset + i * 0.7f;
            float displacement = Mathf.Sin(t * arcFrequency * Mathf.PI * 2f + noiseOffset) * arcAmplitude;
            displacement += Random.Range(-arcJitter, arcJitter);

            float secondaryDisplacement = Mathf.Sin(t * arcFrequency * 2f * Mathf.PI + noiseOffset * 1.5f)
                * arcAmplitude * 0.3f;

            Vector3 offset = up * displacement + right * secondaryDisplacement;
            float taperFactor = Mathf.Sin(t * Mathf.PI);
            offset *= taperFactor;

            positions[i] = basePosition + offset;
        }

        arc.SetPositions(positions);
    }

    private void AnimateArc(LineRenderer arc, Vector3 start, Vector3 end)
    {
        if (arc == null || arc.positionCount < 2) return;
        SetupArcPositions(arc, start, end);
    }

    private void CreatePointLights()
    {
        foreach (var light in m_ActiveLights)
        {
            if (light != null) Destroy(light.gameObject);
        }
        m_ActiveLights.Clear();

        for (int i = 1; i < m_ChainPoints.Count; i++)
        {
            GameObject lightObj = new GameObject("ChainLight");
            lightObj.transform.position = m_ChainPoints[i];

            Light pointLight = lightObj.AddComponent<Light>();
            pointLight.type = LightType.Point;
            pointLight.color = lightningColor;
            pointLight.intensity = pointLightIntensity;
            pointLight.range = pointLightRange;
            pointLight.shadows = LightShadows.None;

            m_ActiveLights.Add(pointLight);
            Destroy(lightObj, arcDuration);
        }
    }

    private Vector3 ApplySpread(Vector3 direction, float spreadAngle)
    {
        if (spreadAngle <= 0) return direction;

        Vector2 randomCircle = Random.insideUnitCircle;
        Quaternion spreadRotation = Quaternion.Euler(
            randomCircle.y * spreadAngle,
            randomCircle.x * spreadAngle, 0);
        return spreadRotation * direction;
    }
}