using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

[DisallowMultipleComponent]
[RequireComponent(typeof(Rigidbody2D))]
[RequireComponent(typeof(CircleCollider2D))]
[RequireComponent(typeof(SpriteRenderer))]
public sealed class EnemyAI : MonoBehaviour, ICombatDamageable
{
    public enum EnemyKind { Skeleton, Cyclops }
    public enum EnemyState { AcquiringTarget, Chasing, AvoidingObstacle, Dead }

    [SerializeField] private EnemyKind enemyKind = EnemyKind.Skeleton;
    [Header("Movement")]
    [SerializeField] private float skeletonBaseSpeed = 1.99f;
    [SerializeField] private float cyclopsBaseSpeed = 1.46f;
    [SerializeField] private float obstacleProbeDistance = 1.55f;
    [SerializeField] private float obstacleProbeRadius = 0.23f;
    [SerializeField] private float obstacleSteeringStrength = 2.65f;
    [SerializeField] private float crowdAvoidanceRadius = 1.75f;
    [SerializeField] private float crowdAvoidanceWeight = 1.28f;
    [Header("Combat")]
    [SerializeField] private float contactDamageCooldown = 0.65f;
    [Header("External Physics")]
    [SerializeField] private float externalVelocityDamping = 8.5f;
    [SerializeField] private float maximumExternalVelocity = 14f;

    private Rigidbody2D body;
    private CircleCollider2D bodyCollider;
    private SpriteRenderer spriteRenderer;
    private PlayerController target;
    private Texture2D generatedTexture;
    private Sprite generatedSprite;
    private readonly List<LineRenderer> generatedLines = new List<LineRenderer>();
    private EnemyState currentState = EnemyState.AcquiringTarget;
    private float maxHealth;
    private float currentHealth;
    private float contactDamage;
    private float movementSpeed;
    private float nextContactDamageTime;
    private bool isDead;
    private bool hasStarted;
    private bool isBossSummonedMinion;
    private Vector2 externalVelocity;
    private int avoidanceSide = 1;
    private float avoidanceSideLockUntil;
    private Vector2 previousPosition;
    private float stuckTimer;
    private float detourUntil;
    private Vector2 detourDirection;

    public EnemyKind Kind => enemyKind;
    public EnemyState State => currentState;
    public float CurrentHealth => currentHealth;
    public float MaxHealth => maxHealth;
    public float ContactDamage => contactDamage;
    public float MovementSpeed => movementSpeed;
    public bool IsDead => isDead;
    public bool IsBossSummonedMinion => isBossSummonedMinion;
    public event Action<EnemyAI> EnemyDied;

    private void Awake() { EnsureRuntimeReady(); }
    private void OnEnable() { EnsureRuntimeReady(); }
    public void EnsureRuntimeReady()
    {
        EnsurePhysicsComponents();
        EnsureSpriteRenderer();
    }
    private void Start()
    {
        hasStarted = true;
        if (target == null) target = FindAnyObjectByType<PlayerController>();
        ConfigureRuntimeStats();
        RebuildVisuals();
        if (body != null) previousPosition = body.position;
    }

    private void FixedUpdate()
    {
        if (GameplayRoomReveal.IsRevealing)
        {
            if (body != null) body.linearVelocity = Vector2.zero;
            return;
        }
        if (body == null || bodyCollider == null)
        {
            EnsureRuntimeReady();
            if (body == null) return;
        }
        if (isDead) { body.linearVelocity = Vector2.zero; externalVelocity = Vector2.zero; return; }
        if (target == null || target.IsDead)
        {
            currentState = EnemyState.AcquiringTarget;
            target = FindAnyObjectByType<PlayerController>();
            body.linearVelocity = externalVelocity;
            DecayExternalVelocity();
            return;
        }
        ChaseTarget();
        body.linearVelocity += externalVelocity;
        KeepMotionOutsideStaticObstacles();
        DecayExternalVelocity();
    }

    public void Initialize(EnemyKind kind, PlayerController playerTarget, bool bossSummonedMinion = false)
    {
        enemyKind = kind;
        target = playerTarget;
        isBossSummonedMinion = bossSummonedMinion;
        if (hasStarted) { ConfigureRuntimeStats(); RebuildVisuals(); }
    }

    private void ConfigureRuntimeStats()
    {
        float multiplier = GameManager.Instance != null ? GameManager.Instance.GetEnemyMovementSpeedMultiplier() : 1f;
        if (enemyKind == EnemyKind.Cyclops)
            movementSpeed = cyclopsBaseSpeed * multiplier;
        else
            movementSpeed = skeletonBaseSpeed * multiplier;

        if (isBossSummonedMinion)
        {
            maxHealth = 2f;
            contactDamage = 0.5f;
        }
        else if (GameManager.Instance != null)
        {
            maxHealth = GameManager.Instance.GetScaledNormalEnemyHealth();
            contactDamage = GameManager.Instance.GetScaledNormalEnemyDamage();
        }
        else
        {
            maxHealth = 2f;
            contactDamage = 0.5f;
        }
        currentHealth = maxHealth;
    }

    private void ChaseTarget()
    {
        Vector2 toTarget = (Vector2)target.transform.position - body.position;
        if (toTarget.sqrMagnitude <= 0.001f) { body.linearVelocity = Vector2.zero; return; }

        Vector2 desired = toTarget.normalized;

        // Cached room-wide flood-fill navigation replaces getting trapped
        // between multiple adjacent walls and stones. It considers the *real*
        // Collider2D geometry and an enemy-sized clearance radius.
        if (LevelRoomManager.Instance != null &&
            LevelRoomManager.Instance.TryGetEnemySteering(body.position,
                target.transform.position, out Vector2 routeDirection) &&
            routeDirection.sqrMagnitude > 0.001f)
        {
            Vector2 crowdSeparation = CalculateOtherEnemySeparation();
            Vector2 pathVelocity = routeDirection + crowdSeparation * crowdAvoidanceWeight;
            // A timed diversion prevents every enemy from repeatedly choosing the
            // same blocked doorway/waypoint on every physics frame.
            if (Time.time < detourUntil && detourDirection.sqrMagnitude > 0.001f)
                pathVelocity += detourDirection * 1.20f;
            if (pathVelocity.sqrMagnitude < 0.001f)
                pathVelocity = routeDirection;
            body.linearVelocity = pathVelocity.normalized * movementSpeed;
            currentState = Vector2.Dot(routeDirection, desired) < 0.92f
                ? EnemyState.AvoidingObstacle : EnemyState.Chasing;

            // Physical enemies may block each other on a narrow turn. The
            // tangent push is short, while the shared route remains unchanged.
            float routeMovedDistance = Vector2.Distance(body.position, previousPosition);
            if (routeMovedDistance < 0.009f && body.linearVelocity.sqrMagnitude > 0.4f)
                stuckTimer += Time.fixedDeltaTime;
            else
                stuckTimer = Mathf.Max(0f, stuckTimer - Time.fixedDeltaTime * 1.8f);

            if (stuckTimer > 0.48f && Time.time >= detourUntil)
            {
                Vector2 tangentA = new Vector2(-routeDirection.y, routeDirection.x);
                Vector2 tangentB = -tangentA;
                float clearanceA = ProbeClearanceScore(tangentA);
                float clearanceB = ProbeClearanceScore(tangentB);
                // Only choose a tangent with static clearance. Keep it long enough
                // for the crowd to spread instead of oscillating every tick.
                detourDirection = clearanceA >= clearanceB ? tangentA : tangentB;
                detourUntil = Time.time + 0.72f;
                body.linearVelocity = (routeDirection * 0.45f +
                    detourDirection * 1.25f + crowdSeparation * 0.65f).normalized * movementSpeed;
                stuckTimer = 0f;
            }
            previousPosition = body.position;
            return;
        }

        Vector2 finalDirection = desired;

        if (TryGetBlockingObstacle(desired, out RaycastHit2D blocker))
        {
            currentState = EnemyState.AvoidingObstacle;
            Vector2 normal = blocker.normal.sqrMagnitude > 0.001f ? blocker.normal.normalized : -desired;
            Vector2 tangentA = new Vector2(-normal.y, normal.x);
            Vector2 tangentB = -tangentA;

            if (Time.time >= avoidanceSideLockUntil)
            {
                float scoreA = Vector2.Dot(tangentA, desired) + ProbeClearanceScore(tangentA);
                float scoreB = Vector2.Dot(tangentB, desired) + ProbeClearanceScore(tangentB);
                avoidanceSide = scoreA >= scoreB ? 1 : -1;
                avoidanceSideLockUntil = Time.time + 0.55f;
            }

            Vector2 tangent = avoidanceSide > 0 ? tangentA : tangentB;
            if (ProbeClearanceScore(tangent) < -0.25f && ProbeClearanceScore(-tangent) > 0f)
            {
                tangent = -tangent;
                avoidanceSide *= -1;
                avoidanceSideLockUntil = Time.time + 0.65f;
            }

            Vector2 repulsion = CalculateNearbyObstacleRepulsion();
            finalDirection = desired * 0.30f + tangent * obstacleSteeringStrength + normal * 0.85f + repulsion * 1.25f + CalculateOtherEnemySeparation() * 0.90f;
        }
        else
        {
            currentState = EnemyState.Chasing;
            finalDirection = desired + CalculateNearbyObstacleRepulsion() * 0.55f + CalculateOtherEnemySeparation() * crowdAvoidanceWeight;
        }

        if (finalDirection.sqrMagnitude <= 0.001f) finalDirection = desired;
        body.linearVelocity = finalDirection.normalized * movementSpeed;

        float moved = Vector2.Distance(body.position, previousPosition);
        if (moved < 0.012f && body.linearVelocity.sqrMagnitude > 0.2f)
            stuckTimer += Time.fixedDeltaTime;
        else
            stuckTimer = Mathf.Max(0f, stuckTimer - Time.fixedDeltaTime * 2f);

        if (stuckTimer > 0.35f)
        {
            avoidanceSide *= -1;
            avoidanceSideLockUntil = Time.time + 0.85f;
            body.linearVelocity = RotateVector(desired, avoidanceSide * 72f).normalized * movementSpeed;
            stuckTimer = 0f;
        }

        previousPosition = body.position;
    }

    private Vector2 CalculateOtherEnemySeparation()
    {
        Vector2 result = Vector2.zero;
        Collider2D[] close = Physics2D.OverlapCircleAll(body.position, crowdAvoidanceRadius);
        for (int i = 0; i < close.Length; i++)
        {
            Collider2D collider = close[i];
            if (collider == null || collider == bodyCollider || !collider.enabled || collider.isTrigger)
                continue;
            EnemyAI other = collider.GetComponentInParent<EnemyAI>();
            if (other == null || other == this || other.IsDead) continue;
            Vector2 away = body.position - (Vector2)other.transform.position;
            float distance = away.magnitude;
            // Stable opposite nudges for perfectly overlapping enemies.
            if (distance < 0.001f)
                away = GetEntityId().CompareTo(other.GetEntityId()) < 0 ? Vector2.left : Vector2.right;
            float strength = Mathf.Pow(1f - Mathf.Clamp01(distance / crowdAvoidanceRadius), 1.25f);
            result += away.normalized * strength;
        }
        return Vector2.ClampMagnitude(result, 1f);
    }

    private bool TryGetBlockingObstacle(Vector2 direction, out RaycastHit2D best)
    {
        RaycastHit2D[] hits = Physics2D.CircleCastAll(body.position, obstacleProbeRadius, direction, obstacleProbeDistance);
        float nearest = float.PositiveInfinity;
        best = default(RaycastHit2D);
        bool found = false;
        for (int i = 0; i < hits.Length; i++)
        {
            if (!IsObstacle(hits[i].collider)) continue;
            if (hits[i].distance < nearest)
            {
                nearest = hits[i].distance;
                best = hits[i];
                found = true;
            }
        }
        return found;
    }

    private float ProbeClearanceScore(Vector2 direction)
    {
        if (direction.sqrMagnitude < 0.001f) return -1f;
        RaycastHit2D[] hits = Physics2D.CircleCastAll(body.position, obstacleProbeRadius * 0.9f, direction.normalized, obstacleProbeDistance * 0.9f);
        float nearest = float.PositiveInfinity;
        for (int i = 0; i < hits.Length; i++)
            if (IsObstacle(hits[i].collider)) nearest = Mathf.Min(nearest, hits[i].distance);
        if (float.IsPositiveInfinity(nearest)) return 0.65f;
        return Mathf.Lerp(-0.65f, 0.35f, Mathf.Clamp01(nearest / obstacleProbeDistance));
    }

    private Vector2 CalculateNearbyObstacleRepulsion()
    {
        Collider2D[] hits = Physics2D.OverlapCircleAll(body.position, 0.78f);
        Vector2 repulsion = Vector2.zero;
        for (int i = 0; i < hits.Length; i++)
        {
            Collider2D c = hits[i];
            if (!IsObstacle(c)) continue;
            Vector2 closest = c.ClosestPoint(body.position);
            Vector2 away = body.position - closest;
            float distance = away.magnitude;
            if (distance < 0.001f) away = body.position - (Vector2)c.bounds.center;
            if (away.sqrMagnitude < 0.001f) away = Vector2.right;
            float strength = 1f - Mathf.Clamp01(distance / 0.78f);
            repulsion += away.normalized * strength;
        }
        return repulsion;
    }

    private bool IsObstacle(Collider2D c)
    {
        if (c == null || c == bodyCollider || c.isTrigger) return false;
        if (target != null && (c.transform == target.transform || c.transform.IsChildOf(target.transform))) return false;
        if (c.GetComponentInParent<EnemyAI>() != null || c.GetComponentInParent<BossController>() != null) return false;
        Rigidbody2D rb = c.attachedRigidbody;
        return rb == null || rb.bodyType == RigidbodyType2D.Static;
    }

    private static Vector2 RotateVector(Vector2 v, float degrees)
    {
        float r = degrees * Mathf.Deg2Rad, c = Mathf.Cos(r), s = Mathf.Sin(r);
        return new Vector2(v.x * c - v.y * s, v.x * s + v.y * c);
    }

    public void ApplyExternalImpulse(Vector2 impulse)
    {
        if (isDead || body == null) return;
        body.AddForce(impulse, ForceMode2D.Impulse);
        externalVelocity += impulse * 0.72f;
        externalVelocity = Vector2.ClampMagnitude(externalVelocity, Mathf.Max(0.1f, maximumExternalVelocity));
    }

    public void ApplyExternalPull(Vector2 acceleration)
    {
        if (isDead || body == null) return;
        body.AddForce(acceleration, ForceMode2D.Force);
        externalVelocity += acceleration * Time.fixedDeltaTime;
        externalVelocity = Vector2.ClampMagnitude(externalVelocity, Mathf.Max(0.1f, maximumExternalVelocity));
    }

    private void KeepMotionOutsideStaticObstacles()
    {
        Vector2 velocity = body.linearVelocity;
        float speed = velocity.magnitude;
        if (speed < 0.001f) return;
        Vector2 direction = velocity / speed;
        float worldRadius = bodyCollider.radius * Mathf.Max(Mathf.Abs(transform.lossyScale.x), Mathf.Abs(transform.lossyScale.y));
        RaycastHit2D[] hits = Physics2D.CircleCastAll(body.position, worldRadius,
            direction, speed * Time.fixedDeltaTime + 0.08f);
        float nearest = float.PositiveInfinity;
        Vector2 surfaceNormal = Vector2.zero;
        foreach (RaycastHit2D hit in hits)
        {
            if (!IsObstacle(hit.collider) || hit.distance >= nearest || hit.normal.sqrMagnitude < 0.01f)
                continue;
            nearest = hit.distance;
            surfaceNormal = hit.normal.normalized;
        }
        if (surfaceNormal.sqrMagnitude > 0.01f && Vector2.Dot(velocity, surfaceNormal) < 0f)
        {
            // Slide along the solid surface. Never use transform.position or
            // teleportation for navigation: that would bypass 2D collisions.
            body.linearVelocity = velocity - surfaceNormal * Vector2.Dot(velocity, surfaceNormal);
        }
    }

    private void DecayExternalVelocity()
    {
        externalVelocity = Vector2.MoveTowards(externalVelocity, Vector2.zero, Mathf.Max(0.1f, externalVelocityDamping) * Time.fixedDeltaTime);
    }

    private void OnCollisionEnter2D(Collision2D c) => TryDealContactDamage(c);
    private void OnCollisionStay2D(Collision2D c) => TryDealContactDamage(c);
    private void TryDealContactDamage(Collision2D collision)
    {
        if (GameplayRoomReveal.IsRevealing || isDead || collision == null || Time.time < nextContactDamageTime) return;
        PlayerController p = collision.collider.GetComponentInParent<PlayerController>();
        if (p != null && p.TakeDamage(contactDamage, gameObject)) nextContactDamageTime = Time.time + Mathf.Max(0.05f, contactDamageCooldown);
    }

    public bool ReceiveDamage(float amount, GameObject source)
    {
        if (isDead || amount <= 0f) return false;
        currentHealth = Mathf.Max(0f, currentHealth - amount);
        RuntimeVfx.SpawnCircularBurst(transform.position, new Color(0.36f, 0.01f, 0.015f, 1f), 9, 0.105f, 2.35f, 0.32f);
        StartCoroutine(DamageFlashRoutine());
        if (currentHealth <= 0f) Die();
        return true;
    }

    private IEnumerator DamageFlashRoutine()
    {
        if (spriteRenderer == null) yield break;
        Color original = spriteRenderer.color;
        spriteRenderer.color = new Color(1f, 0.24f, 0.24f, 1f);
        yield return new WaitForSeconds(0.06f);
        if (spriteRenderer != null) spriteRenderer.color = original;
    }

    private void Die()
    {
        if (isDead) return;
        isDead = true;
        currentState = EnemyState.Dead;
        body.linearVelocity = Vector2.zero;
        body.simulated = false;
        if (bodyCollider != null) bodyCollider.enabled = false;
        RuntimeVfx.SpawnCircularBurst(transform.position, new Color(0.33f, 0.008f, 0.012f, 1f), 18, 0.17f, 4f, 0.55f);
        if (GameManager.Instance != null) GameManager.Instance.NotifyEnemyKilled(enemyKind == EnemyKind.Cyclops ? 150 : 100);
        EnemyDied?.Invoke(this);
        Destroy(gameObject, 0.08f);
    }

    private void EnsurePhysicsComponents()
    {
        body = GetComponent<Rigidbody2D>() ?? gameObject.AddComponent<Rigidbody2D>();
        body.bodyType = RigidbodyType2D.Dynamic;
        body.gravityScale = 0f;
        body.linearDamping = 3f;
        body.angularDamping = 0f;
        body.freezeRotation = true;
        body.collisionDetectionMode = CollisionDetectionMode2D.Continuous;
        body.interpolation = RigidbodyInterpolation2D.Interpolate;
        bodyCollider = GetComponent<CircleCollider2D>() ?? gameObject.AddComponent<CircleCollider2D>();
        bodyCollider.isTrigger = false;
        bodyCollider.offset = Vector2.zero;
    }

    private void EnsureSpriteRenderer()
    {
        spriteRenderer = GetComponent<SpriteRenderer>() ?? gameObject.AddComponent<SpriteRenderer>();
        spriteRenderer.sortingOrder = 14;
    }

    private void RebuildVisuals()
    {
        ClearGeneratedVisuals();
        string artKey = enemyKind == EnemyKind.Cyclops
            ? "Characters/Cyclops"
            : (isBossSummonedMinion ? "Characters/SkeletonMinion" : "Characters/Skeleton");
        Sprite approvedSprite = RuntimeGameArt.Load(artKey);
        string animationKey = enemyKind == EnemyKind.Cyclops ? "Cyclops" :
            (isBossSummonedMinion ? "SkeletonMinion" : "Skeleton");
        bool directional = RuntimeEnemyEightDirectionAnimator.AssetsAvailable(animationKey);
        Sprite sizingSprite = directional
            ? Resources.Load<Sprite>("GameArt/AnimationFrames/Enemies/" + animationKey +
                "/Directional/" + animationKey + "_Down_Walk_00") : approvedSprite;
        // The 8-frame cells intentionally include transparent margins so feet/head
        // never wobble. Correct for these margins when matching the old enemy size.
        float visibleWidthFraction = enemyKind == EnemyKind.Cyclops ? 0.70f :
            isBossSummonedMinion ? 0.71f : 0.72f;
        float targetWidth = enemyKind == EnemyKind.Cyclops ? 1.47f : 1.39f;
        float defaultScale = enemyKind == EnemyKind.Cyclops ? 1.2544f : 1.2432f;
        float visualScale = sizingSprite != null
            ? targetWidth / Mathf.Max(0.01f, sizingSprite.bounds.size.x *
                (directional ? visibleWidthFraction : 1f)) : defaultScale;
        // Minions summoned by Wraith King are 30% smaller than normal skeletons.
        if (isBossSummonedMinion) visualScale *= 0.70f;
        transform.localScale = Vector3.one * visualScale;
        if (approvedSprite != null)
        {
            spriteRenderer.sprite = approvedSprite;
            spriteRenderer.sortingOrder = 14;
            spriteRenderer.color = Color.white;
            // Clear passage through walls/stone, with collider width independent
            // of the original PNG crop or SkeletonMinion image dimensions.
            // Match the actual visible body width so an enemy cannot appear to
            // walk across a stone/wall while its smaller physics circle is outside.
            float worldRadius = enemyKind == EnemyKind.Cyclops ? 0.66f
                : isBossSummonedMinion ? 0.40f : 0.60f;
            bodyCollider.radius = worldRadius / Mathf.Max(0.01f, visualScale);
            RuntimeGameArt.AttachCharacterAnimator(gameObject,
                enemyKind == EnemyKind.Cyclops ? "Cyclops" :
                (isBossSummonedMinion ? "SkeletonMinion" : "Skeleton"));
            return;
        }
        if (enemyKind == EnemyKind.Skeleton)
        {
            generatedTexture = Procedural2DUtility.CreateSkeletonTexture(64);
            generatedSprite = Procedural2DUtility.CreateSprite(generatedTexture, 64f, "Runtime Skeleton Sprite");
            spriteRenderer.sprite = generatedSprite;
            bodyCollider.radius = (isBossSummonedMinion ? 0.40f : 0.60f) / Mathf.Max(0.01f, visualScale);
            CreateLimb("Left Arm", new Vector3(-0.19f, 0.06f), new Vector3(-0.46f, -0.05f), new Vector3(-0.58f, -0.25f));
            CreateLimb("Right Arm", new Vector3(0.19f, 0.06f), new Vector3(0.46f, -0.05f), new Vector3(0.58f, -0.25f));
            CreateLimb("Left Leg", new Vector3(-0.12f, -0.23f), new Vector3(-0.22f, -0.50f), new Vector3(-0.34f, -0.65f));
            CreateLimb("Right Leg", new Vector3(0.12f, -0.23f), new Vector3(0.22f, -0.50f), new Vector3(0.34f, -0.65f));
        }
        else
        {
            generatedTexture = Procedural2DUtility.CreateCyclopsTexture(80);
            generatedSprite = Procedural2DUtility.CreateSprite(generatedTexture, 64f, "Runtime Cyclops Sprite");
            spriteRenderer.sprite = generatedSprite;
            bodyCollider.radius = 0.66f / Mathf.Max(0.01f, visualScale);
        }
        spriteRenderer.color = Color.white;
    }

    private void CreateLimb(string name, Vector3 a, Vector3 b, Vector3 c)
    {
        GameObject obj = new GameObject(name);
        obj.transform.SetParent(transform, false);
        LineRenderer line = obj.AddComponent<LineRenderer>();
        line.useWorldSpace = false;
        line.positionCount = 3;
        line.SetPosition(0, a); line.SetPosition(1, b); line.SetPosition(2, c);
        line.startWidth = 0.065f; line.endWidth = 0.052f;
        Color bone = new Color(0.78f, 0.76f, 0.67f, 1f);
        line.startColor = bone; line.endColor = bone;
        Material mat = Procedural2DUtility.GetSharedLineMaterial(); if (mat != null) line.sharedMaterial = mat;
        line.sortingOrder = 7;
        generatedLines.Add(line);
    }

    private void ClearGeneratedVisuals()
    {
        for (int i = 0; i < generatedLines.Count; i++) if (generatedLines[i] != null) Destroy(generatedLines[i].gameObject);
        generatedLines.Clear();
        if (generatedSprite != null) { Destroy(generatedSprite); generatedSprite = null; }
        if (generatedTexture != null) { Destroy(generatedTexture); generatedTexture = null; }
    }

    private void OnDestroy()
    {
        if (generatedSprite != null) Destroy(generatedSprite);
        if (generatedTexture != null) Destroy(generatedTexture);
    }
}

[DisallowMultipleComponent]
internal sealed class HeartPickup : MonoBehaviour
{
    private SpriteRenderer spriteRenderer;
    private Texture2D generatedTexture;
    private Sprite generatedSprite;
    private float spawnTime;
    private const float Lifetime = 18f;

    private void Awake()
    {
        spawnTime = Time.time;
        spriteRenderer = gameObject.AddComponent<SpriteRenderer>();
        spriteRenderer.sortingOrder = 12;
        Sprite approvedHeart = RuntimeGameArt.Load("UI/Hearts_Full");
        if (approvedHeart != null) spriteRenderer.sprite = approvedHeart;
        else
        {
            generatedTexture = Procedural2DUtility.CreateHeartTexture(48);
            generatedSprite = Procedural2DUtility.CreateSprite(generatedTexture, 64f, "Runtime Heart Pickup Fallback");
            spriteRenderer.sprite = generatedSprite;
        }
        CircleCollider2D c = gameObject.AddComponent<CircleCollider2D>(); c.radius = 0.31f; c.isTrigger = true;
    }

    private void Update()
    {
        transform.rotation = Quaternion.Euler(0f, 0f, Mathf.Sin(Time.time * 2.5f) * 5f);
        transform.localScale = Vector3.one * (0.78f + Mathf.Sin(Time.time * 5f) * 0.045f);
        if (Time.time - spawnTime >= Lifetime) Destroy(gameObject);
    }

    private void OnTriggerEnter2D(Collider2D other)
    {
        PlayerController p = other.GetComponentInParent<PlayerController>();
        if (p == null || !p.Heal(1f)) return;
        RuntimeVfx.SpawnCircularBurst(transform.position, new Color(0.80f, 0.02f, 0.04f, 1f), 10, 0.09f, 1.8f, 0.30f);
        Destroy(gameObject);
    }

    private void OnDestroy() { if (generatedSprite != null) Destroy(generatedSprite); if (generatedTexture != null) Destroy(generatedTexture); }
}

internal static class Procedural2DUtility
{
    private static Material sharedLineMaterial;
    public static Material GetSharedLineMaterial()
    {
        if (sharedLineMaterial != null) return sharedLineMaterial;
        Shader shader = Shader.Find("Sprites/Default") ?? Shader.Find("Universal Render Pipeline/Unlit");
        if (shader == null) return null;
        sharedLineMaterial = new Material(shader) { name = "Runtime Shared 2D Line Material" };
        return sharedLineMaterial;
    }

    public static Sprite CreateSprite(Texture2D texture, float ppu, string name)
    {
        Sprite sprite = Sprite.Create(texture, new Rect(0f, 0f, texture.width, texture.height), new Vector2(0.5f, 0.5f), ppu, 0, SpriteMeshType.FullRect);
        sprite.name = name; return sprite;
    }

    public static Texture2D CreateSkeletonTexture(int size)
    {
        Texture2D texture = PrepareTexture(size, "Runtime Skeleton Texture");
        Color[] pixels = new Color[size * size]; float center = (size - 1) * 0.5f, radius = size * 0.35f;
        Color bone = new Color(0.82f, 0.80f, 0.70f, 1f), shade = new Color(0.56f, 0.54f, 0.47f, 1f), cavity = new Color(0.055f, 0.045f, 0.04f, 1f);
        for (int y = 0; y < size; y++) for (int x = 0; x < size; x++)
        {
            float dx = x - center, dy = y - center, d = Mathf.Sqrt(dx * dx + dy * dy); int index = y * size + x;
            if (d > radius) { pixels[index] = Color.clear; continue; }
            float nx = dx / radius, ny = dy / radius;
            Color result = Color.Lerp(bone, shade, (d / radius) * 0.55f);
            bool eye = Mathf.Pow(nx + 0.30f, 2f) + Mathf.Pow(ny - 0.10f, 2f) < 0.045f || Mathf.Pow(nx - 0.30f, 2f) + Mathf.Pow(ny - 0.10f, 2f) < 0.045f;
            bool mouth = Mathf.Abs(ny + 0.37f) < 0.055f && Mathf.Abs(nx) < 0.36f;
            pixels[index] = eye || mouth ? cavity : result;
        }
        texture.SetPixels(pixels); texture.Apply(false, false); return texture;
    }

    public static Texture2D CreateCyclopsTexture(int size)
    {
        Texture2D texture = PrepareTexture(size, "Runtime Cyclops Texture");
        Color[] pixels = new Color[size * size]; float center = (size - 1) * 0.5f, radius = size * 0.44f;
        Color body = new Color(0.23f, 0.24f, 0.25f, 1f), edge = new Color(0.075f, 0.08f, 0.085f, 1f), sclera = new Color(0.58f, 0.51f, 0.46f, 1f), crimson = new Color(0.68f, 0.015f, 0.025f, 1f), pupil = new Color(0.08f, 0.005f, 0.008f, 1f);
        for (int y = 0; y < size; y++) for (int x = 0; x < size; x++)
        {
            float dx = x - center, dy = y - center, d = Mathf.Sqrt(dx * dx + dy * dy); int index = y * size + x;
            if (d > radius) { pixels[index] = Color.clear; continue; }
            Color result = Color.Lerp(body, edge, (d / radius) * 0.85f);
            float ex = dx / (radius * 0.50f), ey = dy / (radius * 0.26f);
            if (ex * ex + ey * ey <= 1f) result = sclera;
            float pd = Mathf.Sqrt(Mathf.Pow(dx / (radius * 0.22f), 2f) + Mathf.Pow(dy / (radius * 0.22f), 2f));
            if (pd <= 1f) result = pd < 0.42f ? pupil : crimson;
            pixels[index] = result;
        }
        texture.SetPixels(pixels); texture.Apply(false, false); return texture;
    }

    public static Texture2D CreateHeartTexture(int size)
    {
        Texture2D texture = PrepareTexture(size, "Runtime Heart Texture"); Color[] pixels = new Color[size * size];
        Color heart = new Color(0.76f, 0.018f, 0.035f, 1f), edge = new Color(0.25f, 0.005f, 0.01f, 1f);
        for (int y = 0; y < size; y++) for (int x = 0; x < size; x++)
        {
            float nx = ((x + 0.5f) / size) * 2f - 1f, ny = ((y + 0.5f) / size) * 2f - 1f + 0.12f;
            float e = Mathf.Pow(nx * nx + ny * ny - 0.62f, 3f) - nx * nx * Mathf.Pow(ny, 3f); int index = y * size + x;
            pixels[index] = e > 0f ? Color.clear : Color.Lerp(edge, heart, Mathf.Clamp01(Mathf.Abs(e) * 18f));
        }
        texture.SetPixels(pixels); texture.Apply(false, false); return texture;
    }

    public static Texture2D CreateWraithKingTexture(int size)
    {
        Texture2D texture = PrepareTexture(size, "Runtime Wraith King Texture"); Color[] pixels = new Color[size * size]; float center = (size - 1) * 0.5f, radius = size * 0.43f;
        Color inner = new Color(0.20f, 0.21f, 0.24f, 1f), outer = new Color(0.025f, 0.027f, 0.035f, 1f), eyes = new Color(0.72f, 0.02f, 0.035f, 1f);
        for (int y = 0; y < size; y++) for (int x = 0; x < size; x++)
        {
            float dx = x - center, dy = y - center, d = Mathf.Sqrt(dx * dx + dy * dy); int index = y * size + x;
            if (d > radius) { pixels[index] = Color.clear; continue; }
            float nx = dx / radius, ny = dy / radius; Color result = Color.Lerp(inner, outer, d / radius);
            if (ny > 0.03f && ny < 0.17f && Mathf.Abs(nx) > 0.16f && Mathf.Abs(nx) < 0.43f) result = eyes;
            pixels[index] = result;
        }
        texture.SetPixels(pixels); texture.Apply(false, false); return texture;
    }

    public static Texture2D CreateCyclopsKingTexture(int size) { Texture2D t = CreateCyclopsTexture(size); t.name = "Runtime Cyclops King Texture"; return t; }

    public static Texture2D CreateDiscTexture(int size, Color centerColor, Color edgeColor, string name)
    {
        Texture2D texture = PrepareTexture(size, name); Color[] pixels = new Color[size * size]; float center = (size - 1) * 0.5f, radius = size * 0.46f;
        for (int y = 0; y < size; y++) for (int x = 0; x < size; x++)
        {
            float dx = x - center, dy = y - center, d = Mathf.Sqrt(dx * dx + dy * dy); int index = y * size + x;
            pixels[index] = d > radius ? Color.clear : Color.Lerp(centerColor, edgeColor, d / radius);
        }
        texture.SetPixels(pixels); texture.Apply(false, false); return texture;
    }

    private static Texture2D PrepareTexture(int size, string name)
    {
        return new Texture2D(size, size, TextureFormat.RGBA32, false) { name = name, filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp };
    }
}
