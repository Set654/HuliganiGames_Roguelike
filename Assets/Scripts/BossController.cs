using System;
using System.Collections;
using UnityEngine;

[DisallowMultipleComponent]
[RequireComponent(typeof(Rigidbody2D))]
[RequireComponent(typeof(CircleCollider2D))]
[RequireComponent(typeof(SpriteRenderer))]
public sealed class BossController : MonoBehaviour, ICombatDamageable
{
    public enum BossKind { WraithKing, CyclopsKing }
    public enum BossState { Initializing, Waiting, Moving, Telegraphing, Attacking, Recovering, Dead }

    [SerializeField] private BossKind bossKind = BossKind.WraithKing;
    [Header("Core Stats")]
    [SerializeField] private float maxHealth = 30f;
    [SerializeField] private float contactDamage = 1f;
    [SerializeField] private float movementSpeed = 4.1f;
    [SerializeField] private float attackRecovery = 0.55f;
    [Header("Arena")]
    [SerializeField] private float arenaHalfWidth = 7.55f;
    [SerializeField] private float arenaHalfHeight = 4.05f;
    [Header("Wraith King")]
    [SerializeField] private float homingProjectileSpeed = 3.15f;
    [SerializeField] private float wraithMeleeRange = 2.65f;
    [SerializeField] private float wraithMeleeArcDegrees = 130f;
    [SerializeField] private float wraithMeleeDamage = 1f;
    [SerializeField] private float summonEdgeInset = 0.85f;
    [Header("Cyclops King")]
    [SerializeField] private float laserWarningDuration = 0.496f;
    [SerializeField] private float laserLockedDelay = 0.312f;
    [SerializeField] private float slamApproachDuration = 1.45f;
    [SerializeField] private float slamTriggerDistanceFactor = 0.84f;
    [SerializeField] private float laserActiveDuration = 1.08f;
    [SerializeField] private float laserDamage = 1f;
    [SerializeField] private float groundSlamRadius = 3.45f;
    [SerializeField] private float colossalEyeSpeed = 7.6f; // 20% slower than v31
    [SerializeField] private int colossalEyeBounces = 7;
    [Header("External Physics")]
    [SerializeField] private float externalVelocityDamping = 10f;
    [SerializeField] private float maximumExternalVelocity = 8f;

    private Rigidbody2D body;
    private CircleCollider2D bodyCollider;
    private SpriteRenderer spriteRenderer;
    private PlayerController target;
    private Texture2D generatedTexture;
    private Sprite generatedSprite;
    private Sprite cyclopsThrowSprite;
    private Sprite cyclopsDefaultSprite;
    private Animator cachedAnimator;
    private bool cyclopsThrowPoseActive;
    private LineRenderer attackLine;
    private SpriteRenderer laserArtwork;
    private Sprite laserArtworkSprite;
    private float currentHealth;
    private float nextContactDamageTime;
    private float nextWraithMeleeOpportunityTime;
    private bool isDead;
    private bool hasStarted;
    private int attackSequenceIndex;
    private Coroutine combatRoutine;
    private BossState currentState = BossState.Initializing;
    private Vector2 externalVelocity;
    private bool wraithResurrectionUsed;
    private bool resurrectionInProgress;
    private LineRenderer activeGroundSlamRing;
    private int activeEyeProjectiles;
    private bool eyeThrowPositionLocked;
    private RigidbodyConstraints2D originalEyeThrowConstraints;
    private bool wraithAttackPoseActive;
    private Vector3 lastWraithDeathPosition;

    public BossKind Kind => bossKind;
    public BossState State => currentState;
    public float CurrentHealth => currentHealth;
    public float MaxHealth => maxHealth;
    public int TotalLives => bossKind == BossKind.WraithKing ? 2 : 1;
    public int RemainingLives => bossKind == BossKind.WraithKing ? (wraithResurrectionUsed ? 1 : 2) : 1;
    public bool IsResurrectionInProgress => resurrectionInProgress;
    public bool IsCyclopsThrowPoseActive => cyclopsThrowPoseActive;
    public bool IsWraithAttackPoseActive => wraithAttackPoseActive;
    public bool IsDead => isDead;
    public static event Action<BossController, Vector3> BossRewardRequested;
    public event Action<BossController> BossDefeated;

    private void Awake() { EnsureRuntimeReady(); }
    private void OnEnable() { EnsureRuntimeReady(); }
    public void EnsureRuntimeReady()
    {
        EnsurePhysicsComponents();
        EnsureSpriteRenderer();
        EnsureAttackLine();
    }
    private void Start()
    {
        hasStarted = true;
        if (target == null) target = FindAnyObjectByType<PlayerController>();
        maxHealth = Mathf.Max(1f, maxHealth);
        currentHealth = maxHealth;
        contactDamage = Mathf.Max(0f, contactDamage);
        RebuildVisual();
        combatRoutine = StartCoroutine(CombatLoop());
    }

    private void FixedUpdate()
    {
        if (GameplayRoomReveal.IsRevealing)
        {
            if (body != null) body.linearVelocity = Vector2.zero;
            return;
        }
        if (body == null) return;
        if (isDead) { body.linearVelocity = Vector2.zero; externalVelocity = Vector2.zero; return; }
        if (eyeThrowPositionLocked)
        {
            body.linearVelocity = Vector2.zero;
            externalVelocity = Vector2.zero;
            return;
        }
        if (externalVelocity.sqrMagnitude > 0.0001f)
        {
            body.linearVelocity += externalVelocity;
            externalVelocity = Vector2.MoveTowards(externalVelocity, Vector2.zero, Mathf.Max(0.1f, externalVelocityDamping) * Time.fixedDeltaTime);
        }
    }

    public void Initialize(BossKind kind, PlayerController playerTarget)
    {
        bossKind = kind;
        target = playerTarget;
        // Wraith King keeps 30 HP. Cyclops King has 67.5 HP (1.5x its previous 45 HP).
        maxHealth = kind == BossKind.CyclopsKing ? 67.5f : 30f;
        if (hasStarted)
        {
            currentHealth = maxHealth;
            RebuildVisual();
        }
    }

    public void ApplyExternalImpulse(Vector2 impulse)
    {
        if (isDead || body == null || eyeThrowPositionLocked) return;
        Vector2 reduced = impulse * 0.55f;
        body.AddForce(reduced, ForceMode2D.Impulse);
        externalVelocity += reduced * 0.55f;
        externalVelocity = Vector2.ClampMagnitude(externalVelocity, Mathf.Max(0.1f, maximumExternalVelocity));
    }

    public void ApplyExternalPull(Vector2 acceleration)
    {
        if (isDead || body == null || eyeThrowPositionLocked) return;
        Vector2 reduced = acceleration * 0.45f;
        body.AddForce(reduced, ForceMode2D.Force);
        externalVelocity += reduced * Time.fixedDeltaTime;
        externalVelocity = Vector2.ClampMagnitude(externalVelocity, Mathf.Max(0.1f, maximumExternalVelocity));
    }

    private IEnumerator CombatLoop()
    {
        currentState = BossState.Waiting;
        yield return new WaitForSeconds(1f);
        while (!isDead)
        {
            if (GameplayRoomReveal.IsRevealing)
            {
                if (body != null) body.linearVelocity = Vector2.zero;
                yield return null;
                continue;
            }
            if (target == null || target.IsDead)
            {
                target = FindAnyObjectByType<PlayerController>();
                body.linearVelocity = Vector2.zero;
                yield return null;
                continue;
            }
            if (bossKind == BossKind.WraithKing) yield return ExecuteWraithAttack(); else yield return ExecuteCyclopsAttack();
            if (isDead) yield break;

            // Bosses do not remain stationary between attacks anymore.
            yield return AggressiveReposition();
            if (isDead) yield break;

            currentState = BossState.Recovering;
            body.linearVelocity = Vector2.zero;
            yield return new WaitForSeconds(Mathf.Max(0.1f, attackRecovery));
        }
    }

    private IEnumerator ExecuteWraithAttack()
    {
        float distance = target != null && !target.IsDead
            ? Vector2.Distance(body.position, target.transform.position)
            : float.PositiveInfinity;
        float meleeTrigger = wraithMeleeRange * 0.87f;

        // Distance takes priority over rotation: previously a melee turn could
        // expire chasing from the far side of the arena, so the swing rarely played.
        if (distance <= meleeTrigger && Time.time >= nextWraithMeleeOpportunityTime)
        {
            nextWraithMeleeOpportunityTime = Time.time + 1.65f;
            yield return WraithKingStrike();
            yield break;
        }

        int attack = attackSequenceIndex++ % 3;
        if (attack == 0)
        {
            // This turn aggressively tries to close the gap, but melee still
            // never deals damage beyond the configured trigger distance.
            yield return WraithKingStrike();
        }
        else if (attack == 1)
        {
            yield return WraithUndeadSummon();
        }
        else
        {
            yield return WraithBlast();
        }
    }

    private IEnumerator ExecuteCyclopsAttack()
    {
        int attack = attackSequenceIndex++ % 3;
        float range = Mathf.Max(1f, groundSlamRadius * slamTriggerDistanceFactor);
        float distance = target != null ? Vector2.Distance(body.position, target.transform.position) : float.PositiveInfinity;
        // Prefer a slam when the player is close. Otherwise, periodically pursue
        // the player to make the area attack a meaningful threat.
        if (distance <= range && (attack != 2 || UnityEngine.Random.value < 0.67f))
        {
            yield return CyclopsGroundSlam();
        }
        else if (attack == 1)
        {
            yield return CyclopsGroundSlam();
        }
        else if (attack == 0)
        {
            yield return CyclopsEyeLaser();
        }
        else
        {
            yield return CyclopsColossalEyeRoll();
        }
    }

    private IEnumerator WraithKingStrike()
    {
        if (target == null || target.IsDead)
            yield break;

        // King's Strike is a real close-range sword swing. If the player is too far
        // away, the Wraith aggressively closes distance but does NOT create a projectile.
        float closeTime = 0f;
        float meleeTrigger = wraithMeleeRange * 0.87f;
        while (!isDead && target != null && !target.IsDead &&
               Vector2.Distance(body.position, target.transform.position) > meleeTrigger &&
               closeTime < 2.25f)
        {
            currentState = BossState.Moving;
            Vector2 toPlayer = (Vector2)target.transform.position - body.position;
            if (toPlayer.sqrMagnitude > 0.001f)
                body.linearVelocity = toPlayer.normalized * movementSpeed * 1.65f;
            closeTime += Time.fixedDeltaTime;
            yield return new WaitForFixedUpdate();
        }

        body.linearVelocity = Vector2.zero;
        if (isDead || target == null || target.IsDead ||
            Vector2.Distance(body.position, target.transform.position) > meleeTrigger)
            yield break;

        nextWraithMeleeOpportunityTime = Time.time + 1.65f;
        currentState = BossState.Telegraphing;
        attackLine.enabled = false;
        attackLine.useWorldSpace = true;
        attackLine.positionCount = 15;

        float telegraph = 0.312f;
        float elapsed = 0f;
        Vector2 facing = Vector2.right;

        while (elapsed < telegraph)
        {
            if (isDead || target == null || target.IsDead)
            {
                attackLine.enabled = false;
                yield break;
            }

            elapsed += Time.deltaTime;
            Vector2 liveDirection = (Vector2)target.transform.position - body.position;
            if (liveDirection.sqrMagnitude > 0.001f) facing = liveDirection.normalized;
            yield return null;
        }

        // Re-aim at the last possible moment: the melee strike is much more accurate.
        Vector2 finalDirection = (Vector2)target.transform.position - body.position;
        if (finalDirection.sqrMagnitude > 0.001f) facing = finalDirection.normalized;

        currentState = BossState.Attacking;
        StartCoroutine(PlayWraithAttackAnimation(facing, 0.312f));

        if (IsPlayerInsideMeleeArc(facing, wraithMeleeRange, wraithMeleeArcDegrees * 0.5f))
            target.TakeDamage(wraithMeleeDamage, gameObject);

        RuntimeVfx.SpawnCircularBurst(
            transform.position + (Vector3)(facing * (wraithMeleeRange * 0.60f)),
            new Color(0.62f, 0.025f, 0.035f, 1f), 18, 0.13f, 4.2f, 0.40f);
        RuntimeVfx.SpawnCircularBurst(transform.position + (Vector3)(facing * 1.15f), new Color(0.48f, 0.43f, 0.36f, 1f), 24, 0.12f, 3.8f, 0.36f);
        RuntimeCameraShake.Shake(0.10f, 0.17f);

        yield return new WaitForSeconds(0.14f);

        // During the resurrected / second phase, the king follows with a fast reverse sweep.
        if (!isDead && currentHealth <= maxHealth * 0.5f && target != null && !target.IsDead &&
            Vector2.Distance(body.position, target.transform.position) <= wraithMeleeRange)
        {
            Vector2 secondDirection = ((Vector2)target.transform.position - body.position).normalized;
            DrawSwordArc(secondDirection, wraithMeleeRange * 1.06f, wraithMeleeArcDegrees + 18f);
            StartCoroutine(PlayWraithAttackAnimation(secondDirection, 0.234f));
            if (IsPlayerInsideMeleeArc(secondDirection, wraithMeleeRange * 1.06f, (wraithMeleeArcDegrees + 18f) * 0.5f))
                target.TakeDamage(wraithMeleeDamage, gameObject);
            yield return new WaitForSeconds(0.12f);
        }

        attackLine.enabled = false;
        attackLine.useWorldSpace = false;
        attackLine.positionCount = 2;
    }

    private IEnumerator PlayWraithAttackAnimation(Vector2 facing, float duration)
    {
        if (bossKind != BossKind.WraithKing || spriteRenderer == null)
            yield break;

        string direction = ResolveWraithAttackDirection(facing);
        Sprite[] frames = new Sprite[8];
        int loaded = 0;
        for (int i = 0; i < frames.Length; i++)
        {
            frames[i] = Resources.Load<Sprite>("GameArt/AnimationFrames/Bosses/WraithKing/Attack/WraithKing_Attack_" + direction + "_" + i.ToString("00"));
            if (frames[i] != null) loaded++;
        }
        if (loaded == 0)
            yield break;

        wraithAttackPoseActive = true;
        float elapsed = 0f;
        while (elapsed < duration && !resurrectionInProgress && !isDead)
        {
            float t = duration <= 0.001f ? 1f : Mathf.Clamp01(elapsed / duration);
            int frame = Mathf.Clamp(Mathf.FloorToInt(t * frames.Length), 0, frames.Length - 1);
            if (frames[frame] != null)
                spriteRenderer.sprite = frames[frame];
            elapsed += Time.deltaTime;
            yield return null;
        }
        if (frames[frames.Length - 1] != null && !resurrectionInProgress && !isDead)
            spriteRenderer.sprite = frames[frames.Length - 1];
        wraithAttackPoseActive = false;
    }

    private static string ResolveWraithAttackDirection(Vector2 facing)
    {
        if (Mathf.Abs(facing.x) >= Mathf.Abs(facing.y))
            return facing.x >= 0f ? "Right" : "Left";
        return facing.y >= 0f ? "Up" : "Down";
    }

    private void DrawSwordArc(Vector2 facing, float radius, float arcDegrees)
    {
        if (attackLine == null) return;
        float centerAngle = Mathf.Atan2(facing.y, facing.x) * Mathf.Rad2Deg;
        int count = Mathf.Max(3, attackLine.positionCount);
        for (int i = 0; i < count; i++)
        {
            float t = i / (float)(count - 1);
            float angle = centerAngle + Mathf.Lerp(-arcDegrees * 0.5f, arcDegrees * 0.5f, t);
            Vector2 point = body.position + new Vector2(Mathf.Cos(angle * Mathf.Deg2Rad), Mathf.Sin(angle * Mathf.Deg2Rad)) * radius;
            attackLine.SetPosition(i, point);
        }
    }

    private bool IsPlayerInsideMeleeArc(Vector2 facing, float radius, float halfArcDegrees)
    {
        if (target == null || target.IsDead) return false;
        Vector2 toPlayer = (Vector2)target.transform.position - body.position;
        if (toPlayer.magnitude > radius) return false;
        if (toPlayer.sqrMagnitude < 0.001f) return true;
        return Vector2.Angle(facing, toPlayer.normalized) <= halfArcDegrees;
    }

    private IEnumerator WraithUndeadSummon()
    {
        currentState = BossState.Moving;
        yield return MoveToPosition(ChooseArenaEdgeDestination(), 1.65f);
        if (isDead) yield break;
        currentState = BossState.Telegraphing;
        RuntimeVfx.SpawnCircularBurst(transform.position, new Color(0.35f, 0.02f, 0.12f, 1f), 22, 0.13f, 2.1f, 0.7f);
        yield return new WaitForSeconds(0.42f);
        if (isDead) yield break;
        currentState = BossState.Attacking;
        SpawnSkeletonMinion((Vector2)transform.position + new Vector2(0.85f, 0.42f));
        SpawnSkeletonMinion((Vector2)transform.position + new Vector2(-0.85f, -0.42f));
        SpawnSkeletonMinion((Vector2)transform.position + new Vector2(0f, -0.95f));
        yield return new WaitForSeconds(0.28f);
    }

    private void SpawnSkeletonMinion(Vector2 position)
    {
        GameObject obj = new GameObject("Wraith King Skeleton Minion");

        Vector2 safePosition = ClampInsideArena(position);
        if (target != null)
        {
            Vector2 away = safePosition - (Vector2)target.transform.position;
            if (away.sqrMagnitude < 7.84f)
            {
                if (away.sqrMagnitude < 0.001f)
                    away = Vector2.right;

                safePosition = ClampInsideArena(
                    (Vector2)target.transform.position +
                    away.normalized * 2.8f);
            }
        }

        obj.transform.position = safePosition;
        Rigidbody2D minionBody = obj.AddComponent<Rigidbody2D>();
        minionBody.bodyType = RigidbodyType2D.Dynamic;
        minionBody.gravityScale = 0f;
        minionBody.linearDamping = 3f;
        minionBody.angularDamping = 0f;
        minionBody.freezeRotation = true;
        minionBody.collisionDetectionMode = CollisionDetectionMode2D.Continuous;
        minionBody.interpolation = RigidbodyInterpolation2D.Interpolate;
        obj.AddComponent<CircleCollider2D>();
        obj.AddComponent<SpriteRenderer>();
        EnemyAI enemy = obj.AddComponent<EnemyAI>();
        enemy.EnsureRuntimeReady();
        enemy.Initialize(EnemyAI.EnemyKind.Skeleton, target, true);
    }

    private IEnumerator WraithBlast()
    {
        currentState = BossState.Telegraphing;
        float duration = 0.55f, elapsed = 0f;
        while (elapsed < duration)
        {
            if (isDead) yield break;
            elapsed += Time.deltaTime;
            transform.localScale = Vector3.one * 2.2464f * (1f + Mathf.Sin(elapsed * 18f) * 0.08f);
            yield return null;
        }
        transform.localScale = Vector3.one * (bossKind == BossKind.WraithKing ? 2.016f : 2.2464f);
        currentState = BossState.Attacking;
        Vector2 dir = target != null ? (Vector2)target.transform.position - (Vector2)transform.position : Vector2.right;
        if (dir.sqrMagnitude < 0.001f) dir = Vector2.right;
        GameObject obj = new GameObject("Wraith Homing Blast");
        obj.AddComponent<HomingBossProjectile>().Initialize(transform.position, dir.normalized, homingProjectileSpeed, target, 1f, 3.65f, 165f);
        yield return new WaitForSeconds(0.30f);
    }

    private IEnumerator CyclopsEyeLaser()
    {
        if (target == null || target.IsDead) yield break;
        EnsureLaserArtwork();
        currentState = BossState.Telegraphing;
        body.linearVelocity = Vector2.zero;
        Vector2 direction = ((Vector2)target.transform.position - body.position).normalized;
        if (direction.sqrMagnitude < 0.001f) direction = Vector2.right;
        Vector2 lockedOrigin = GetCyclopsEyeOrigin(direction);
        Vector2 endpoint = CalculateArenaRayEndpoint(lockedOrigin, direction);
        attackLine.enabled = true;
        attackLine.useWorldSpace = true;
        attackLine.positionCount = 2;
        attackLine.startWidth = attackLine.endWidth = 0.045f;
        attackLine.startColor = new Color(0.85f, 0.03f, 0.05f, 0.77f);
        attackLine.endColor = new Color(0.72f, 0.02f, 0.04f, 0.45f);

        // First warning phase: the thin line tracks the player, but does no damage.
        float elapsed = 0f;
        while (elapsed < laserWarningDuration)
        {
            if (isDead || target == null || target.IsDead)
            {
                ResetAttackLine();
                yield break;
            }
            elapsed += Time.deltaTime;
            body.linearVelocity = Vector2.zero;
            lockedOrigin = GetCyclopsEyeOrigin(direction);
            Vector2 towards = (Vector2)target.transform.position - lockedOrigin;
            if (towards.sqrMagnitude > 0.001f) direction = towards.normalized;
            endpoint = CalculateArenaRayEndpoint(lockedOrigin, direction);
            attackLine.SetPosition(0, lockedOrigin);
            attackLine.SetPosition(1, endpoint);
            yield return null;
        }

        // Direction LOCKS here. The player gets a clear, stationary dodge window.
        // Crucially, the beam will NOT snap to the player's updated position.
        elapsed = 0f;
        attackLine.startWidth = attackLine.endWidth = 0.11f;
        attackLine.startColor = attackLine.endColor = new Color(1f, 0.14f, 0.07f, 0.94f);
        while (elapsed < laserLockedDelay)
        {
            if (isDead)
            {
                ResetAttackLine();
                yield break;
            }
            elapsed += Time.deltaTime;
            body.linearVelocity = Vector2.zero;
            attackLine.SetPosition(0, lockedOrigin);
            attackLine.SetPosition(1, endpoint);
            yield return null;
        }

        currentState = BossState.Attacking;
        attackLine.enabled = false;
        EnsureLaserArtwork();
        if (laserArtwork != null) laserArtwork.enabled = true;
        elapsed = 0f;
        float nextDamage = 0f;
        while (elapsed < laserActiveDuration)
        {
            if (isDead)
            {
                ResetAttackLine();
                yield break;
            }
            elapsed += Time.deltaTime;
            body.linearVelocity = Vector2.zero;
            PositionLaserArtwork(lockedOrigin, endpoint);
            if (target != null && !target.IsDead && Time.time >= nextDamage &&
                IsPlayerInsideBeam(lockedOrigin, endpoint, target.transform.position, 0.90f))
            {
                target.TakeDamage(laserDamage, gameObject);
                nextDamage = Time.time + 0.2f;
            }
            yield return null;
        }
        ResetAttackLine();
        RuntimeVfx.SpawnCircularBurst(lockedOrigin, new Color(0.85f, 0.01f, 0.02f, 1f), 14, 0.11f, 2.8f, 0.42f);
    }

    private void ResetAttackLine()
    {
        if (laserArtwork != null) laserArtwork.enabled = false;
        if (attackLine == null) return;
        attackLine.enabled = false;
        attackLine.useWorldSpace = false;
        attackLine.loop = false;
        attackLine.positionCount = 2;
    }

    private void EnsureLaserArtwork()
    {
        if (laserArtwork != null) return;
        laserArtworkSprite = RuntimeGameArt.Load("Projectiles/CyclopsKing_Laser");
        if (laserArtworkSprite == null) return;
        GameObject beam = new GameObject("Approved Cyclops Laser - Visual Only");
        // Use a separate world-space transform: the boss sprite scales during attacks.
        beam.transform.SetParent(transform, true);
        laserArtwork = beam.AddComponent<SpriteRenderer>();
        laserArtwork.sprite = laserArtworkSprite;
        laserArtwork.sortingOrder = 31;
        laserArtwork.enabled = false;
    }

    private void PositionLaserArtwork(Vector2 from, Vector2 to)
    {
        if (laserArtwork == null || laserArtworkSprite == null) return;
        Vector2 beam = to - from;
        float distance = beam.magnitude;
        if (distance < 0.001f) return;
        Transform visual = laserArtwork.transform;
        visual.position = new Vector3((from.x + to.x) * 0.5f, (from.y + to.y) * 0.5f, 0f);
        visual.rotation = Quaternion.Euler(0f, 0f, Mathf.Atan2(beam.y, beam.x) * Mathf.Rad2Deg);
        Vector2 size = laserArtworkSprite.bounds.size;
        // Since the object is a child of the dynamically scaled boss, compensate
        // for the parent's transform to preserve a thicker 1.08-world-unit beam.
        float bossScale = Mathf.Max(0.01f, transform.lossyScale.x);
        visual.localScale = new Vector3(distance / Mathf.Max(0.01f, size.x) / bossScale,
            2.16f / Mathf.Max(0.01f, size.y) / bossScale, 1f / bossScale);
    }

    private static bool IsPlayerInsideBeam(Vector2 start, Vector2 end, Vector2 point, float radius)
    {
        Vector2 segment = end - start; float len = segment.sqrMagnitude;
        if (len <= 0.0001f) return Vector2.Distance(start, point) <= radius;
        float t = Mathf.Clamp01(Vector2.Dot(point - start, segment) / len);
        return Vector2.Distance(start + segment * t, point) <= radius;
    }

    private IEnumerator CyclopsGroundSlam()
    {
        if (target == null || target.IsDead) yield break;
        float activationRange = groundSlamRadius * slamTriggerDistanceFactor;
        float approachElapsed = 0f;

        // Aggressive pursuit; only wind up when the player is INSIDE the smaller
        // activation radius, not at any arbitrary distance across the arena.
        while (!isDead && target != null && !target.IsDead &&
               Vector2.Distance(body.position, target.transform.position) > activationRange &&
               approachElapsed < slamApproachDuration)
        {
            currentState = BossState.Moving;
            Vector2 toPlayer = (Vector2)target.transform.position - body.position;
            if (toPlayer.sqrMagnitude > 0.0001f)
                body.linearVelocity = toPlayer.normalized * movementSpeed * 1.5f;
            approachElapsed += Time.fixedDeltaTime;
            yield return new WaitForFixedUpdate();
        }
        body.linearVelocity = Vector2.zero;
        if (isDead || target == null || target.IsDead ||
            Vector2.Distance(body.position, target.transform.position) > activationRange)
            yield break;

        currentState = BossState.Telegraphing;
        ClearSlamTelegraph();
        activeGroundSlamRing = CreateTemporaryRing();
        float duration = 0.72f;
        float elapsed = 0f;
        while (elapsed < duration)
        {
            if (isDead)
            {
                ClearSlamTelegraph();
                yield break;
            }
            elapsed += Time.deltaTime;
            if (activeGroundSlamRing != null)
            {
                activeGroundSlamRing.transform.position = transform.position;
                UpdateRing(activeGroundSlamRing, Mathf.Lerp(0.35f, groundSlamRadius, Mathf.Clamp01(elapsed / duration)));
            }
            yield return null;
        }
        ClearSlamTelegraph();
        if (isDead) yield break;
        currentState = BossState.Attacking;
        Collider2D[] hits = Physics2D.OverlapCircleAll(transform.position, groundSlamRadius);
        for (int i = 0; i < hits.Length; i++)
        {
            PlayerController player = hits[i].GetComponentInParent<PlayerController>();
            if (player == null) continue;
            player.TakeDamage(1f, gameObject);
            break;
        }

        // The impact produces pale stone dust and a short screen shake.
        Vector3 center = transform.position;
        RuntimeVfx.SpawnCircularBurst(center, new Color(0.57f, 0.51f, 0.43f, 1f), 52, 0.21f, 6.7f, 0.72f);
        RuntimeVfx.SpawnCircularBurst(center, new Color(0.29f, 0.26f, 0.24f, 1f), 32, 0.15f, 4.2f, 0.62f);
        RuntimeCameraShake.Shake(0.24f, 0.36f);
        yield return new WaitForSeconds(0.25f);
    }

    private void ClearSlamTelegraph()
    {
        if (activeGroundSlamRing == null) return;
        activeGroundSlamRing.enabled = false;
        Destroy(activeGroundSlamRing.gameObject);
        activeGroundSlamRing = null;
    }

    private IEnumerator CyclopsColossalEyeRoll()
    {
        currentState = BossState.Telegraphing;
        BeginEyeThrowPositionLock();
        float elapsed = 0f, duration = 0.55f;
        while (elapsed < duration)
        {
            if (isDead) { EndEyeThrowPositionLock(); yield break; }
            elapsed += Time.deltaTime;
            body.linearVelocity = Vector2.zero;
            float scale = 2.2464f * (1f + Mathf.Sin(elapsed * 22f) * 0.065f);
            transform.localScale = Vector3.one * scale;
            yield return null;
        }
        transform.localScale = Vector3.one * 2.2464f;
        currentState = BossState.Attacking;
        body.linearVelocity = Vector2.zero;
        Vector2 dir = target != null ? (Vector2)target.transform.position - (Vector2)transform.position : Vector2.right;
        if (dir.sqrMagnitude < 0.001f) dir = Vector2.right;
        dir.Normalize();
        GameObject obj = new GameObject("Colossal Eye Roll");
        obj.AddComponent<ColossalEyeProjectile>().Initialize(this, target, (Vector2)transform.position + dir * 1.05f, dir, colossalEyeSpeed, Mathf.Max(2, colossalEyeBounces), 1f);

        if (currentHealth <= maxHealth * 0.5f)
        {
            Vector2 secondDirection = RotateVector(dir, 28f);
            GameObject secondEye = new GameObject("Colossal Eye Roll - Phase Two");
            secondEye.AddComponent<ColossalEyeProjectile>().Initialize(
                this,
                target,
                (Vector2)transform.position + secondDirection * 1.05f,
                secondDirection,
                colossalEyeSpeed * 1.05f,
                Mathf.Max(2, colossalEyeBounces),
                1f);
        }

        // Do NOT resume moving after a fixed timer. Wait for *all* thrown
        // eyes (two in phase two) to actually return to the Cyclops King.
        while (!isDead && activeEyeProjectiles > 0)
        {
            body.linearVelocity = Vector2.zero;
            yield return null;
        }
        EndEyeThrowPositionLock();
    }

    public bool ReceiveDamage(float amount, GameObject source)
    {
        if (isDead || amount <= 0f) return false;
        currentHealth = Mathf.Max(0f, currentHealth - amount);
        RuntimeVfx.SpawnCircularBurst(transform.position, new Color(0.42f, 0.006f, 0.012f, 1f), 13, 0.13f, 3.1f, 0.38f);
        StartCoroutine(BossDamageFlash());
        if (currentHealth <= 0f) Die();
        return true;
    }

    private IEnumerator BossDamageFlash()
    {
        if (spriteRenderer == null) yield break; Color original = spriteRenderer.color; spriteRenderer.color = new Color(1f, 0.30f, 0.30f, 1f);
        yield return new WaitForSeconds(0.075f); if (spriteRenderer != null) spriteRenderer.color = original;
    }

    private void Die()
    {
        if (isDead || resurrectionInProgress)
            return;
        EndEyeThrowPositionLock();

        if (bossKind == BossKind.WraithKing && !wraithResurrectionUsed)
        {
            StartCoroutine(WraithResurrectionRoutine());
            return;
        }

        BeginFinalDeath();
    }

    private IEnumerator WraithResurrectionRoutine()
    {
        wraithResurrectionUsed = true;
        resurrectionInProgress = true;
        isDead = true;
        wraithAttackPoseActive = false;
        lastWraithDeathPosition = transform.position;
        currentState = BossState.Dead;

        ClearSlamTelegraph();
        ResetAttackLine();
        if (combatRoutine != null)
        {
            StopCoroutine(combatRoutine);
            combatRoutine = null;
        }

        body.linearVelocity = Vector2.zero;
        body.simulated = false;
        bodyCollider.enabled = false;
        attackLine.enabled = false;

        RuntimeVfx.SpawnCircularBurst(
            transform.position,
            new Color(0.50f, 0.015f, 0.12f, 1f),
            64,
            0.24f,
            7.8f,
            1.15f);
        RuntimeVfx.SpawnCircularBurst(
            transform.position,
            new Color(0.96f, 0.82f, 0.35f, 0.95f),
            28,
            0.18f,
            4.8f,
            0.85f);
        RuntimeCameraShake.Shake(0.18f, 0.30f);

        Color baseColor = spriteRenderer.color;
        float fadeDuration = 0.50f;
        float elapsed = 0f;

        while (elapsed < fadeDuration)
        {
            elapsed += Time.deltaTime;
            float t = Mathf.Clamp01(elapsed / fadeDuration);
            spriteRenderer.color = new Color(baseColor.r, baseColor.g, baseColor.b, 1f - t);
            yield return null;
        }

        yield return new WaitForSeconds(0.48f);

        RuntimeVfx.SpawnCircularBurst(transform.position, new Color(0.28f, 0.01f, 0.08f, 1f), 38, 0.14f, 3.6f, 0.72f);
        yield return new WaitForSeconds(0.27f);

        transform.position = lastWraithDeathPosition;
        currentHealth = maxHealth * 0.5f;
        attackRecovery = Mathf.Max(0.32f, attackRecovery * 0.78f);
        movementSpeed *= 1.15f;

        body.simulated = true;
        bodyCollider.enabled = true;
        body.WakeUp();

        RuntimeVfx.SpawnCircularBurst(transform.position, new Color(0.72f, 0.02f, 0.16f, 1f), 72, 0.26f, 8.6f, 1.10f);
        RuntimeVfx.SpawnCircularBurst(transform.position, new Color(1.00f, 0.88f, 0.40f, 0.95f), 40, 0.22f, 5.4f, 0.88f);
        RuntimeCameraShake.Shake(0.22f, 0.38f);

        elapsed = 0f;
        while (elapsed < 0.62f)
        {
            elapsed += Time.deltaTime;
            float t = Mathf.Clamp01(elapsed / 0.62f);
            spriteRenderer.color = new Color(baseColor.r, baseColor.g, baseColor.b, t);
            transform.localScale = Vector3.one * Mathf.Lerp(0.96f, 2.18f, t);
            yield return null;
        }

        spriteRenderer.color = Color.white;
        transform.localScale = Vector3.one * 2.016f;
        RuntimeVfx.SpawnCircularBurst(transform.position, new Color(0.92f, 0.78f, 0.24f, 1f), 54, 0.18f, 6.4f, 0.75f);
        isDead = false;
        resurrectionInProgress = false;
        currentState = BossState.Waiting;

        SpawnSkeletonMinion((Vector2)transform.position + new Vector2(1.0f, 0.65f));
        SpawnSkeletonMinion((Vector2)transform.position + new Vector2(-1.0f, 0.65f));
        SpawnSkeletonMinion((Vector2)transform.position + new Vector2(0f, -1.05f));

        combatRoutine = StartCoroutine(CombatLoop());
    }

    private void BeginFinalDeath()
    {
        isDead = true;
        wraithAttackPoseActive = false;
        currentState = BossState.Dead;

        ClearSlamTelegraph();
        ResetAttackLine();
        if (combatRoutine != null)
        {
            StopCoroutine(combatRoutine);
            combatRoutine = null;
        }

        body.linearVelocity = Vector2.zero;
        body.simulated = false;
        bodyCollider.enabled = false;
        attackLine.enabled = false;

        RuntimeVfx.SpawnCircularBurst(transform.position, new Color(0.40f, 0.005f, 0.012f, 1f), 48, 0.22f, 6.5f, 0.95f);

        if (GameManager.Instance != null)
            GameManager.Instance.AddScore(1000);

        BossRewardRequested?.Invoke(this, transform.position);
        SpawnAdvanceHatch();
        BossDefeated?.Invoke(this);
        StartCoroutine(DeathFadeRoutine());
    }

    private IEnumerator DeathFadeRoutine()
    {
        float duration = 0.65f, elapsed = 0f; Color original = spriteRenderer.color; Vector3 startScale = transform.localScale;
        while (elapsed < duration)
        {
            elapsed += Time.deltaTime; float t = Mathf.Clamp01(elapsed / duration);
            spriteRenderer.color = new Color(original.r, original.g, original.b, 1f - t);
            transform.localScale = startScale * Mathf.Lerp(1f, 0.25f, t); yield return null;
        }
        Destroy(gameObject);
    }

    private void SpawnAdvanceHatch()
    {
        GameObject obj = new GameObject("Level Advance Hatch"); obj.transform.position = ClampInsideArena((Vector2)transform.position + new Vector2(0f, -1.35f)); obj.AddComponent<LevelAdvanceHatch>();
    }

    private void OnCollisionEnter2D(Collision2D c) => TryDealContactDamage(c);
    private void OnCollisionStay2D(Collision2D c) => TryDealContactDamage(c);
    private void TryDealContactDamage(Collision2D c)
    {
        if (GameplayRoomReveal.IsRevealing || isDead || Time.time < nextContactDamageTime) return;
        PlayerController p = c.collider.GetComponentInParent<PlayerController>();
        if (p != null && p.TakeDamage(contactDamage, gameObject)) nextContactDamageTime = Time.time + 0.65f;
    }

    private IEnumerator AggressiveReposition()
    {
        if (target == null || target.IsDead)
            yield break;

        currentState = BossState.Moving;
        float duration = currentHealth <= maxHealth * 0.5f ? 0.78f : 0.62f;
        float elapsed = 0f;
        float orbitDirection = UnityEngine.Random.value < 0.5f ? -1f : 1f;

        while (elapsed < duration)
        {
            if (isDead || target == null || target.IsDead)
            {
                body.linearVelocity = Vector2.zero;
                yield break;
            }

            elapsed += Time.fixedDeltaTime;
            Vector2 playerPosition = target.transform.position;
            Vector2 toPlayer = playerPosition - body.position;
            if (toPlayer.sqrMagnitude < 0.001f) toPlayer = Vector2.right;

            float desiredDistance = bossKind == BossKind.WraithKing ? 1.75f : 2.15f;
            Vector2 radial = toPlayer.normalized;
            Vector2 tangent = new Vector2(-radial.y, radial.x) * orbitDirection;
            float distance = toPlayer.magnitude;
            float radialWeight = distance > desiredDistance ? 1.15f : -0.55f;
            float phaseMultiplier = currentHealth <= maxHealth * 0.5f ? 1.32f : 1.15f;
            Vector2 movement = radial * radialWeight + tangent * 0.85f;
            body.linearVelocity = movement.normalized * movementSpeed * phaseMultiplier;

            yield return new WaitForFixedUpdate();
        }

        body.linearVelocity = Vector2.zero;
    }

    private static Vector2 RotateVector(Vector2 vector, float degrees)
    {
        float radians = degrees * Mathf.Deg2Rad;
        float cos = Mathf.Cos(radians);
        float sin = Mathf.Sin(radians);

        return new Vector2(
            vector.x * cos - vector.y * sin,
            vector.x * sin + vector.y * cos);
    }

    private IEnumerator MoveToPosition(Vector2 destination, float maxDuration)
    {
        float elapsed = 0f;
        while (elapsed < maxDuration)
        {
            if (isDead) { body.linearVelocity = Vector2.zero; yield break; }
            elapsed += Time.deltaTime; Vector2 delta = destination - body.position;
            if (delta.sqrMagnitude < 0.055f) { body.linearVelocity = Vector2.zero; yield break; }
            body.linearVelocity = delta.normalized * movementSpeed; yield return new WaitForFixedUpdate();
        }
        body.linearVelocity = Vector2.zero;
    }

    private Vector2 ChooseArenaEdgeDestination()
    {
        bool horizontal = UnityEngine.Random.value > 0.5f;
        float x, y;
        if (horizontal)
        {
            x = UnityEngine.Random.value > 0.5f ? arenaHalfWidth - summonEdgeInset : -arenaHalfWidth + summonEdgeInset;
            y = UnityEngine.Random.Range(-arenaHalfHeight + summonEdgeInset, arenaHalfHeight - summonEdgeInset);
        }
        else
        {
            y = UnityEngine.Random.value > 0.5f ? arenaHalfHeight - summonEdgeInset : -arenaHalfHeight + summonEdgeInset;
            x = UnityEngine.Random.Range(-arenaHalfWidth + summonEdgeInset, arenaHalfWidth - summonEdgeInset);
        }
        return new Vector2(x, y);
    }

    private Vector2 ClampInsideArena(Vector2 p) => new Vector2(Mathf.Clamp(p.x, -arenaHalfWidth + 0.6f, arenaHalfWidth - 0.6f), Mathf.Clamp(p.y, -arenaHalfHeight + 0.6f, arenaHalfHeight - 0.6f));

    private Vector2 PredictPlayerPosition(float time)
    {
        if (target == null) return transform.position;
        return (Vector2)target.transform.position + target.MovementInput * target.MovementSpeed * Mathf.Max(0f, time);
    }

    private Vector2 CalculateArenaRayEndpoint(Vector2 origin, Vector2 direction)
    {
        direction.Normalize(); float dx = float.PositiveInfinity, dy = float.PositiveInfinity;
        if (Mathf.Abs(direction.x) > 0.0001f) dx = ((direction.x > 0f ? arenaHalfWidth : -arenaHalfWidth) - origin.x) / direction.x;
        if (Mathf.Abs(direction.y) > 0.0001f) dy = ((direction.y > 0f ? arenaHalfHeight : -arenaHalfHeight) - origin.y) / direction.y;
        float d = Mathf.Min(Mathf.Abs(dx), Mathf.Abs(dy)); if (float.IsInfinity(d) || float.IsNaN(d)) d = 20f;
        return origin + direction * d;
    }

    private void EnsurePhysicsComponents()
    {
        body = GetComponent<Rigidbody2D>() ?? gameObject.AddComponent<Rigidbody2D>();
        body.bodyType = RigidbodyType2D.Dynamic; body.gravityScale = 0f; body.linearDamping = 2.4f; body.angularDamping = 0f; body.freezeRotation = true;
        body.collisionDetectionMode = CollisionDetectionMode2D.Continuous; body.interpolation = RigidbodyInterpolation2D.Interpolate;
        bodyCollider = GetComponent<CircleCollider2D>() ?? gameObject.AddComponent<CircleCollider2D>(); bodyCollider.radius = 0.72f; bodyCollider.isTrigger = false;
    }

    private void EnsureSpriteRenderer() { spriteRenderer = GetComponent<SpriteRenderer>() ?? gameObject.AddComponent<SpriteRenderer>(); spriteRenderer.sortingOrder = 15; }

    private void EnsureAttackLine()
    {
        if (attackLine != null) return;

        Transform existing = transform.Find("Boss Attack Line");
        if (existing != null) attackLine = existing.GetComponent<LineRenderer>();

        if (attackLine == null)
        {
            GameObject obj = new GameObject("Boss Attack Line");
            obj.transform.SetParent(transform, false);
            attackLine = obj.AddComponent<LineRenderer>();
        }

        attackLine.enabled = false;
        attackLine.positionCount = 2;
        attackLine.useWorldSpace = false;
        Material mat = Procedural2DUtility.GetSharedLineMaterial();
        if (mat != null) attackLine.sharedMaterial = mat;
        attackLine.sortingOrder = 30;
        attackLine.numCapVertices = 8;
        attackLine.numCornerVertices = 8;
    }

    private void RebuildVisual()
    {
        if (generatedSprite != null) Destroy(generatedSprite); if (generatedTexture != null) Destroy(generatedTexture);
        Sprite approvedBoss = RuntimeGameArt.Load(bossKind == BossKind.WraithKing ? "Characters/WraithKing" : "Characters/CyclopsKing");
        transform.localScale = Vector3.one * (bossKind == BossKind.WraithKing ? 2.016f : 2.2464f);
        if (approvedBoss != null)
        {
            cyclopsDefaultSprite = bossKind == BossKind.CyclopsKing ? approvedBoss : null;
            cyclopsThrowSprite = bossKind == BossKind.CyclopsKing ? RuntimeGameArt.Load("Characters/CyclopsKing_Throw") : null;
            spriteRenderer.sprite = approvedBoss;
            spriteRenderer.sortingOrder = 15;
            spriteRenderer.color = Color.white;
            RuntimeGameArt.AttachCharacterAnimator(gameObject,
                bossKind == BossKind.WraithKing ? "WraithKing" : "CyclopsKing");
            cachedAnimator = GetComponent<Animator>();
            cyclopsThrowPoseActive = false;
            return;
        }
        generatedTexture = bossKind == BossKind.WraithKing ? Procedural2DUtility.CreateWraithKingTexture(112) : Procedural2DUtility.CreateCyclopsKingTexture(112);
        generatedSprite = Procedural2DUtility.CreateSprite(generatedTexture, 64f, bossKind.ToString()); spriteRenderer.sprite = generatedSprite; spriteRenderer.color = Color.white;
        cyclopsDefaultSprite = bossKind == BossKind.CyclopsKing ? generatedSprite : null;
        cyclopsThrowSprite = null;
        cachedAnimator = GetComponent<Animator>();
        cyclopsThrowPoseActive = false;
    }

    private void BeginEyeThrowPositionLock()
    {
        if (body == null || eyeThrowPositionLocked) return;
        originalEyeThrowConstraints = body.constraints;
        eyeThrowPositionLocked = true;
        externalVelocity = Vector2.zero;
        body.linearVelocity = Vector2.zero;
        body.angularVelocity = 0f;
        body.constraints |= RigidbodyConstraints2D.FreezePositionX | RigidbodyConstraints2D.FreezePositionY;
        SetCyclopsThrowPose(true);
    }

    private void EndEyeThrowPositionLock()
    {
        if (!eyeThrowPositionLocked) return;
        eyeThrowPositionLocked = false;
        if (body != null)
        {
            body.linearVelocity = Vector2.zero;
            body.constraints = originalEyeThrowConstraints;
        }
        SetCyclopsThrowPose(false);
    }

    public void RegisterThrownEye()
    {
        activeEyeProjectiles++;
    }

    public void NotifyThrownEyeReturned()
    {
        activeEyeProjectiles = Mathf.Max(0, activeEyeProjectiles - 1);
    }

    private Vector2 GetCyclopsEyeOrigin(Vector2 direction)
    {
        Vector2 center = body != null ? body.position : (Vector2)transform.position;
        Vector2 eye = center + new Vector2(0f, 0.82f);
        if (direction.sqrMagnitude > 0.0001f)
            eye += direction.normalized * 0.20f;
        return eye;
    }

    private void SetCyclopsThrowPose(bool enabled)
    {
        if (bossKind != BossKind.CyclopsKing || spriteRenderer == null) return;
        if (cyclopsThrowPoseActive == enabled) return;
        if (cachedAnimator == null) cachedAnimator = GetComponent<Animator>();
        cyclopsThrowPoseActive = enabled;
        if (cachedAnimator != null)
            cachedAnimator.enabled = !enabled && GetComponent<RuntimeBossEightDirectionAnimator>() == null;
        if (enabled && cyclopsThrowSprite != null)
            spriteRenderer.sprite = cyclopsThrowSprite;
        else if (cyclopsDefaultSprite != null)
            spriteRenderer.sprite = cyclopsDefaultSprite;
    }

    private LineRenderer CreateTemporaryRing()
    {
        GameObject obj = new GameObject("Ground Slam Telegraph"); obj.transform.position = transform.position;
        LineRenderer ring = obj.AddComponent<LineRenderer>(); ring.useWorldSpace = false; ring.loop = true; ring.positionCount = 65; ring.startWidth = ring.endWidth = 0.065f;
        ring.startColor = ring.endColor = new Color(0.72f, 0.02f, 0.02f, 0.85f); Material mat = Procedural2DUtility.GetSharedLineMaterial(); if (mat != null) ring.sharedMaterial = mat; ring.sortingOrder = 25;
        UpdateRing(ring, 0.35f); return ring;
    }

    private static void UpdateRing(LineRenderer ring, float radius)
    {
        if (ring == null) return; int count = ring.positionCount;
        for (int i = 0; i < count; i++) { float angle = (i / (float)(count - 1)) * Mathf.PI * 2f; ring.SetPosition(i, new Vector3(Mathf.Cos(angle) * radius, Mathf.Sin(angle) * radius, 0f)); }
    }

    private void SpawnLinearBossProjectile(Vector2 direction, float speed, float damage, float lifetime, float scale)
    {
        GameObject obj = new GameObject("King Strike Wave"); obj.AddComponent<LinearBossProjectile>().Initialize(transform.position, direction, speed, damage, lifetime, scale);
    }

    private void OnDisable() { EndEyeThrowPositionLock(); SetCyclopsThrowPose(false); ClearSlamTelegraph(); ResetAttackLine(); }
    private void OnDestroy()
    {
        ClearSlamTelegraph();
        if (generatedSprite != null) Destroy(generatedSprite);
        if (generatedTexture != null) Destroy(generatedTexture);
    }
}

[DisallowMultipleComponent]
internal sealed class LinearBossProjectile : MonoBehaviour
{
    private Rigidbody2D body; private Texture2D texture; private Sprite sprite; private Vector2 direction; private float speed, damage, lifetime, elapsed; private bool initialized;
    private void Awake()
    {
        body = gameObject.AddComponent<Rigidbody2D>(); body.bodyType = RigidbodyType2D.Kinematic; body.gravityScale = 0f; body.collisionDetectionMode = CollisionDetectionMode2D.Continuous;
        CircleCollider2D c = gameObject.AddComponent<CircleCollider2D>(); c.isTrigger = true; c.radius = 0.34f;
        SpriteRenderer r = gameObject.AddComponent<SpriteRenderer>(); r.sortingOrder = 22;
        texture = Procedural2DUtility.CreateDiscTexture(32, new Color(0.78f, 0.05f, 0.06f, 1f), new Color(0.12f, 0.01f, 0.015f, 0.35f), "King Strike Wave Texture"); sprite = Procedural2DUtility.CreateSprite(texture, 64f, "King Strike Wave"); r.sprite = sprite;
    }
    public void Initialize(Vector2 p, Vector2 d, float s, float dmg, float life, float scale) { transform.position = p; direction = d.sqrMagnitude > 0.001f ? d.normalized : Vector2.right; speed = Mathf.Max(0.1f, s); damage = Mathf.Max(0f, dmg); lifetime = Mathf.Max(0.1f, life); transform.localScale = Vector3.one * Mathf.Max(0.1f, scale); initialized = true; }
    private void FixedUpdate() { if (initialized) body.MovePosition(body.position + direction * speed * Time.fixedDeltaTime); }
    private void Update() { if (!initialized) return; elapsed += Time.deltaTime; if (elapsed >= lifetime) Destroy(gameObject); }
    private void OnTriggerEnter2D(Collider2D other) { PlayerController p = other.GetComponentInParent<PlayerController>(); if (p == null) return; p.TakeDamage(damage, gameObject); RuntimeVfx.SpawnCircularBurst(transform.position, new Color(0.68f, 0.015f, 0.025f, 1f), 10, 0.11f, 2.5f, 0.35f); Destroy(gameObject); }
    private void OnDestroy() { if (sprite != null) Destroy(sprite); if (texture != null) Destroy(texture); }
}

[DisallowMultipleComponent]
internal sealed class HomingBossProjectile : MonoBehaviour
{
    private static readonly string[] DirectionNames =
    {
        "Right", "UpRight", "Up", "UpLeft", "Left", "DownLeft", "Down", "DownRight"
    };

    private Rigidbody2D body;
    private SpriteRenderer spriteRenderer;
    private Sprite[] directionalSprites;
    private Texture2D fallbackTexture;
    private Sprite fallbackSprite;
    private PlayerController target;
    private Vector2 direction;
    private float speed, damage, lifetime, rotationSpeed, elapsed;
    private bool initialized;

    private void Awake()
    {
        body = gameObject.AddComponent<Rigidbody2D>(); body.bodyType = RigidbodyType2D.Kinematic; body.gravityScale = 0f;
        CircleCollider2D c = gameObject.AddComponent<CircleCollider2D>(); c.isTrigger = true; c.radius = 0.27f;
        spriteRenderer = gameObject.AddComponent<SpriteRenderer>(); spriteRenderer.sortingOrder = 23;
        directionalSprites = new Sprite[8];
        bool hasImported = true;
        for (int i = 0; i < directionalSprites.Length; i++)
        {
            directionalSprites[i] = RuntimeGameArt.Load("Projectiles/WraithKing_FireSkull_" + DirectionNames[i]);
            if (directionalSprites[i] == null) hasImported = false;
        }
        if (!hasImported)
        {
            fallbackTexture = Procedural2DUtility.CreateDiscTexture(38, new Color(0.50f, 0.015f, 0.15f, 1f), new Color(0.04f, 0.005f, 0.055f, 0.2f), "Wraith Homing Blast Texture");
            fallbackSprite = Procedural2DUtility.CreateSprite(fallbackTexture, 64f, "Wraith Homing Blast");
            spriteRenderer.sprite = fallbackSprite;
        }
    }
    public void Initialize(Vector2 p, Vector2 d, float s, PlayerController t, float dmg, float life, float degrees) { transform.position = p; direction = d.sqrMagnitude > 0.001f ? d.normalized : Vector2.right; speed = Mathf.Max(0.1f, s); target = t; damage = Mathf.Max(0f, dmg); lifetime = Mathf.Max(0.1f, life); rotationSpeed = Mathf.Max(0f, degrees); transform.localScale = Vector3.one * 2f; UpdateDirectionalSprite(); initialized = true; }
    private void FixedUpdate()
    {
        if (!initialized) return;
        if (target != null && !target.IsDead)
        {
            Vector2 wanted = (Vector2)target.transform.position - body.position;
            if (wanted.sqrMagnitude > 0.001f)
            {
                wanted.Normalize(); float ca = Mathf.Atan2(direction.y, direction.x) * Mathf.Rad2Deg, ta = Mathf.Atan2(wanted.y, wanted.x) * Mathf.Rad2Deg;
                float next = Mathf.MoveTowardsAngle(ca, ta, rotationSpeed * Time.fixedDeltaTime); direction = new Vector2(Mathf.Cos(next * Mathf.Deg2Rad), Mathf.Sin(next * Mathf.Deg2Rad));
            }
        }
        UpdateDirectionalSprite();
        body.MovePosition(body.position + direction.normalized * speed * Time.fixedDeltaTime);
    }
    private void Update() { elapsed += Time.deltaTime; if (elapsed >= lifetime) Destroy(gameObject); }
    private void OnTriggerEnter2D(Collider2D other) { PlayerController p = other.GetComponentInParent<PlayerController>(); if (p == null) return; p.TakeDamage(damage, gameObject); RuntimeVfx.SpawnCircularBurst(transform.position, new Color(0.55f, 0.02f, 0.17f, 1f), 13, 0.13f, 2.7f, 0.38f); Destroy(gameObject); }
    private void UpdateDirectionalSprite()
    {
        if (spriteRenderer == null || directionalSprites == null || directionalSprites.Length != 8) return;
        int index = GetDirectionIndex(direction);
        if (directionalSprites[index] != null)
            spriteRenderer.sprite = directionalSprites[index];
    }
    private static int GetDirectionIndex(Vector2 direction)
    {
        float angle = Mathf.Atan2(direction.y, direction.x) * Mathf.Rad2Deg;
        int sector = (Mathf.RoundToInt(angle / 45f) + 8) % 8;
        return sector;
    }
    private void OnDestroy() { if (fallbackSprite != null) Destroy(fallbackSprite); if (fallbackTexture != null) Destroy(fallbackTexture); }
}

[DisallowMultipleComponent]
internal sealed class ColossalEyeProjectile : MonoBehaviour
{
    private BossController owner; private PlayerController player; private Rigidbody2D body; private CircleCollider2D hitCollider; private SpriteRenderer renderer; private Texture2D texture; private Sprite sprite; private Sprite[] boulderFrames; private PhysicsMaterial2D material; private Vector2 direction; private float speed, damage; private int maximumBounces, bounceCount; private bool initialized, returning; private float nextPlayerDamageTime; private float spawnTime;
    private void Awake()
    {
        body = gameObject.AddComponent<Rigidbody2D>(); body.bodyType = RigidbodyType2D.Dynamic; body.gravityScale = 0f; body.linearDamping = 0f; body.angularDamping = 0f; body.freezeRotation = true; body.collisionDetectionMode = CollisionDetectionMode2D.Continuous; body.interpolation = RigidbodyInterpolation2D.Interpolate;
        hitCollider = gameObject.AddComponent<CircleCollider2D>(); hitCollider.radius = 0.48f; hitCollider.isTrigger = false; material = new PhysicsMaterial2D("Colossal Eye Physics") { friction = 0f, bounciness = 0f }; hitCollider.sharedMaterial = material;
        renderer = gameObject.AddComponent<SpriteRenderer>();
        renderer.sortingOrder = 24;
        boulderFrames = new Sprite[8];
        bool complete = true;
        for (int i = 0; i < boulderFrames.Length; i++)
        {
            boulderFrames[i] = RuntimeGameArt.Load("Projectiles/CyclopsKing_BoulderEye_" + i.ToString("00"));
            if (boulderFrames[i] == null) complete = false;
        }
        if (complete) renderer.sprite = boulderFrames[0];
        else
        {
            boulderFrames = null;
            texture = Procedural2DUtility.CreateCyclopsTexture(72);
            sprite = Procedural2DUtility.CreateSprite(texture, 64f, "Colossal Eye Fallback");
            renderer.sprite = sprite;
        }
        transform.localScale = Vector3.one * 1.25f;
    }
    public void Initialize(BossController o, PlayerController p, Vector2 pos, Vector2 d, float s, int limit, float dmg)
    {
        owner = o;
        player = p;
        transform.position = pos;
        direction = d.sqrMagnitude > 0.001f ? d.normalized : Vector2.right;
        speed = Mathf.Max(0.1f, s);
        maximumBounces = Mathf.Max(1, limit);
        damage = Mathf.Max(0f, dmg);
        CacheOwnerCollision();
        body.linearVelocity = direction * speed;
        spawnTime = Time.time;
        initialized = true;
        if (owner != null) owner.RegisterThrownEye();
    }
    private void FixedUpdate()
    {
        if (!initialized) return;
        // Previously the eye disappeared at 4.6 seconds, even if it had never
        // returned. Start an actual homing return instead of releasing the boss.
        if (!returning && Time.time - spawnTime >= 4.6f)
            BeginReturnPhase();
        if (returning)
        {
            if (owner == null || owner.IsDead) { Destroy(gameObject); return; }
            Vector2 toOwner = (Vector2)owner.transform.position - body.position;
            if (toOwner.sqrMagnitude < 0.38f) { RuntimeVfx.SpawnCircularBurst(transform.position, new Color(0.68f, 0.015f, 0.025f, 1f), 14, 0.11f, 2.2f, 0.35f); Destroy(gameObject); return; }
            // 20% less travel time on the return leg = 1 / 0.80 = 1.25x speed.
            direction = toOwner.normalized; body.linearVelocity = direction * speed * 1.18f * 1.25f; return;
        }
        if (body.linearVelocity.sqrMagnitude > 0.001f) direction = body.linearVelocity.normalized; body.linearVelocity = direction * speed;
    }
    private void OnCollisionEnter2D(Collision2D c)
    {
        if (!initialized || returning) return;
        PlayerController hit = c.collider.GetComponentInParent<PlayerController>();
        if (hit != null) { if (Time.time >= nextPlayerDamageTime) { hit.TakeDamage(damage, gameObject); nextPlayerDamageTime = Time.time + 0.55f; } return; }
        if (c.collider.GetComponentInParent<BossController>() != null || c.contactCount <= 0) return;
        Vector2 normal = c.GetContact(0).normal; Vector2 incoming = body.linearVelocity.sqrMagnitude > 0.001f ? body.linearVelocity.normalized : direction;
        direction = Vector2.Reflect(incoming, normal).normalized; bounceCount++;
        RuntimeVfx.SpawnCircularBurst(c.GetContact(0).point, new Color(0.73f, 0.02f, 0.025f, 1f), 10, 0.09f, 2.4f, 0.30f);
        if (bounceCount >= maximumBounces) { BeginReturnPhase(); return; }
        body.position += direction * 0.04f; body.linearVelocity = direction * speed;
    }
    private void Update()
    {
        if (boulderFrames != null && boulderFrames.Length == 8 && renderer != null)
            renderer.sprite = boulderFrames[(Mathf.FloorToInt((Time.time - spawnTime) * 11f) % 8 + 8) % 8];
    }
    private void BeginReturnPhase() { returning = true; hitCollider.isTrigger = true; renderer.color = new Color(1f, 0.28f, 0.28f, 1f); }
    private void OnTriggerEnter2D(Collider2D other) { if (!returning) return; PlayerController hit = other.GetComponentInParent<PlayerController>(); if (hit == null || Time.time < nextPlayerDamageTime) return; hit.TakeDamage(damage, gameObject); nextPlayerDamageTime = Time.time + 0.55f; }
    private void CacheOwnerCollision() { if (owner == null) return; Collider2D[] cs = owner.GetComponentsInChildren<Collider2D>(); for (int i = 0; i < cs.Length; i++) if (cs[i] != null) Physics2D.IgnoreCollision(hitCollider, cs[i], true); }
    private void OnDestroy()
    {
        if (initialized && owner != null) owner.NotifyThrownEyeReturned();
        if (sprite != null) Destroy(sprite);
        if (texture != null) Destroy(texture);
        if (material != null) Destroy(material);
    }
}

[DisallowMultipleComponent]
internal sealed class LevelAdvanceHatch : MonoBehaviour
{
    private Texture2D texture; private Sprite sprite; private bool consumed;
    private void Awake()
    {
        SpriteRenderer r = gameObject.AddComponent<SpriteRenderer>();
        r.sortingOrder = 4;
        Sprite importedHatch = RuntimeGameArt.Load("Hatches/Hatch_Open");
        if (importedHatch != null) r.sprite = importedHatch;
        else
        {
            texture = CreateHatchTexture(72);
            sprite = Procedural2DUtility.CreateSprite(texture, 64f, "Level Advance Hatch");
            r.sprite = sprite;
        }
        CircleCollider2D c = gameObject.AddComponent<CircleCollider2D>(); c.radius = 0.52f; c.isTrigger = true;
    }
    private void Update() { transform.localScale = Vector3.one * (1f + Mathf.Sin(Time.time * 2.8f) * 0.035f); }
    private void OnTriggerEnter2D(Collider2D other) { if (GameplayRoomReveal.IsRevealing || consumed) return; PlayerController p = other.GetComponentInParent<PlayerController>(); if (p == null || p.IsDead) return; consumed = true; if (GameManager.Instance != null) GameManager.Instance.AdvanceRoom(); Destroy(gameObject); }
    private static Texture2D CreateHatchTexture(int size)
    {
        Texture2D t = new Texture2D(size, size, TextureFormat.RGBA32, false) { name = "Runtime Hatch Texture", filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp }; Color[] p = new Color[size * size]; float c = (size - 1) * 0.5f, o = size * 0.45f, inn = size * 0.34f;
        for (int y = 0; y < size; y++) for (int x = 0; x < size; x++) { float dx = x - c, dy = y - c, d = Mathf.Sqrt(dx * dx + dy * dy); int i = y * size + x; p[i] = d > o ? Color.clear : d > inn ? new Color(0.22f, 0.20f, 0.18f, 1f) : Color.Lerp(new Color(0.008f, 0.004f, 0.006f, 1f), new Color(0.13f, 0.015f, 0.02f, 1f), (d / inn) * 0.55f); }
        t.SetPixels(p); t.Apply(false, false); return t;
    }
    private void OnDestroy() { if (sprite != null) Destroy(sprite); if (texture != null) Destroy(texture); }
}

// Animates the single approved strike-frame visually. Damage and collision
// remain exclusively in BossController.WraithKingStrike.

