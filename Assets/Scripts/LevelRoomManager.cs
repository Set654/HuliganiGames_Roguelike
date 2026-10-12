using System.Collections;
using System.Collections.Generic;
using UnityEngine;

[DisallowMultipleComponent]
public sealed class LevelRoomManager : MonoBehaviour
{
    public static LevelRoomManager Instance { get; private set; }

    // Bombs are allowed to bounce against an invisible boundary at open doors.
    // This boundary is only for bombs; the hero may still use unlocked exits.
    public Vector2 GetBombMovementLimits(float bombRadius)
    {
        float inset = boundaryThickness * 0.5f + Mathf.Max(0.05f, bombRadius) + 0.10f;
        return new Vector2(Mathf.Max(0.5f, roomHalfWidth - inset),
                           Mathf.Max(0.5f, roomHalfHeight - inset));
    }
    [SerializeField] private float roomHalfWidth = 7.68f;
    [SerializeField] private float roomHalfHeight = 4.18f;
    [SerializeField] private float boundaryThickness = 0.34f;
    [SerializeField] private float roomClearRewardDelay = 0.55f;
    [SerializeField] private float minimumEnemySpawnDistanceFromPlayer = 3.4f;
    [SerializeField, Min(1.15f)] private float minimumObstacleGap = 1.65f;

    private GameManager gameManager, boundGameManager;
    private ItemSystem itemSystem;
    private PlayerController player;
    private Transform generatedRoomRoot;
    private readonly HashSet<EnemyAI> trackedEnemies = new HashSet<EnemyAI>();
    private int aliveRoomEnemies, generationVersion;
    private bool roomClearHandled, initialized;
    private Vector2 roomRewardPosition, roomExitPosition;
    private RuntimeRoomDoor currentRoomDoor;
    private RuntimeRoomNavigator roomNavigator;

    private void Awake() { if (Instance != null && Instance != this) { Destroy(gameObject); return; } Instance = this; }
    private void Start() => StartCoroutine(InitializeRoutine());
    private IEnumerator InitializeRoutine()
    {
        yield return null;
        EnsureRequiredManagers();
        BindGameManager();
        initialized = true;
        if (gameManager != null &&
            gameManager.CurrentState == GameManager.GameState.GAMEPLAY &&
            UnityEngine.SceneManagement.SceneManager.GetActiveScene().name == "Game")
            BuildCurrentRoom();
    }
    private void Update()
    {
        if (!initialized) return;
        if (gameManager == null) { EnsureRequiredManagers(); BindGameManager(); }
        if (player == null && gameManager != null && gameManager.CurrentState == GameManager.GameState.GAMEPLAY) EnsurePlayer();

        // Failsafe: a normal combat room can never become permanently locked if an
        // enemy was removed unexpectedly. The portal/reward appears once none remain.
        if (gameManager != null &&
            gameManager.CurrentState == GameManager.GameState.GAMEPLAY &&
            gameManager.CurrentLevelIndex > 0 &&
            gameManager.CurrentRoomIndex > 0 &&
            gameManager.CurrentRoomIndex < 3 &&
            generatedRoomRoot != null &&
            !roomClearHandled)
        {
            trackedEnemies.RemoveWhere(e => e == null || e.IsDead);
            aliveRoomEnemies = trackedEnemies.Count;
            if (aliveRoomEnemies == 0)
            {
                roomClearHandled = true;
                StartCoroutine(HandleRoomClearedRoutine(generationVersion));
            }
        }
    }
    private void OnDestroy() { if (Instance == this) Instance = null; UnbindGameManager(); }

    private void EnsureRequiredManagers() { gameManager = GameManager.Instance; itemSystem = ItemSystem.Instance; }
    private void BindGameManager()
    {
        if (gameManager == null || gameManager == boundGameManager) return; UnbindGameManager(); boundGameManager = gameManager; boundGameManager.ProgressionChanged += HandleProgressionChanged; boundGameManager.StateChanged += HandleStateChanged;
    }
    private void UnbindGameManager() { if (boundGameManager == null) return; boundGameManager.ProgressionChanged -= HandleProgressionChanged; boundGameManager.StateChanged -= HandleStateChanged; boundGameManager = null; }
    private void HandleProgressionChanged(int level, int room) { if (gameManager != null && gameManager.CurrentState == GameManager.GameState.GAMEPLAY && UnityEngine.SceneManagement.SceneManager.GetActiveScene().name == "Game") BuildCurrentRoom(); }
    private void HandleStateChanged(GameManager.GameState state)
    {
        if (state == GameManager.GameState.MAIN_MENU)
        {
            CleanupCurrentRoom();
            return;
        }

        if (state != GameManager.GameState.GAMEPLAY ||
            UnityEngine.SceneManagement.SceneManager.GetActiveScene().name != "Game")
            return;

        // Settings -> Gameplay is NOT room progression. Preserve the exact
        // player position, health, enemy state, drops and current door state.
        // Actual room changes are handled by ProgressionChanged; a fresh
        // Game scene gets its room from InitializeRoutine.
        if (generatedRoomRoot == null && initialized)
            BuildCurrentRoom();
    }

    private void BuildCurrentRoom()
    {
        if (gameManager == null) return;
        gameManager.ConfigureGameplayCamera();
        generationVersion++; CleanupCurrentRoom(); EnsurePlayer(); roomClearHandled = false; aliveRoomEnemies = 0; trackedEnemies.Clear();
        generatedRoomRoot = new GameObject("Generated Room - Level " + gameManager.CurrentLevelIndex + " Room " + gameManager.CurrentRoomIndex).transform;
        CreateFloor(); CreateOuterWalls();
        if (gameManager.CurrentLevelIndex == 0)
            BuildTutorialRoom();
        else if (gameManager.CurrentRoomIndex == 1)
            BuildCombatRoomOne(gameManager.CurrentLevelIndex);
        else if (gameManager.CurrentRoomIndex == 2)
            BuildCombatRoomTwo(gameManager.CurrentLevelIndex);
        else
            BuildBossRoom(gameManager.CurrentLevelIndex);

        // The fixed layouts can have narrow stone/wall gaps after sprite and hitbox
        // updates. Move loose stones/short walls to legal places before adding cover.
        // Intentional central cross segments are kept touching one another.
        NormalizeObstacleClearances();

        // Optional ricochet cover must also satisfy a full enemy-width gap.
        AddSpacedCover();

        // Decor is placed AFTER the entire room and enemies have been created,
        // so boxes cannot overlap a wall, stone, enemy spawn or exit path.
        PlaceApprovedRoomDecor();
        if (!gameManager.IsTutorial) PlaceBreakablePots();

        // Build shared, collider-aware navigation only AFTER all stone and wall
        // geometry is finalized. AI uses the same clearances validated here.
        Physics2D.SyncTransforms();
        GameObject navigationObject = new GameObject("Enemy Navigation - Shared Flow Field");
        navigationObject.transform.SetParent(generatedRoomRoot, false);
        roomNavigator = navigationObject.AddComponent<RuntimeRoomNavigator>();
        roomNavigator.Initialize(roomHalfWidth, roomHalfHeight);
        if (!gameManager.IsTutorial && !RoomTraversalIsValid())
            Debug.LogWarning("Проверка проходов: есть недоступные позиции в схеме комнаты " +
                gameManager.CurrentLevelIndex + "/" + gameManager.CurrentRoomIndex, this);

        // Every room begins close to the hero with local fog already cleared;
        // movement/attacks resume after fog and camera have fully revealed it.
        GameplayRoomReveal.BeginForRoom(generatedRoomRoot, player);
    }

    private void BuildTutorialRoom()
    {
        MovePlayerTo(new Vector2(-5.75f, -0.35f)); CreateStone(new Vector2(-2.2f, 1.65f), 1.05f); CreateStone(new Vector2(2.5f, -1.65f), 1.15f); CreateWall(new Vector2(0.1f, 2.15f), new Vector2(2.2f, 0.30f)); CreateWall(new Vector2(0.65f, -2.2f), new Vector2(2.7f, 0.30f));
        if (itemSystem != null) { RuntimeItemPickup pickup = itemSystem.SpawnTutorialDash(new Vector3(-0.65f, 0.25f, 0f)); if (pickup != null) pickup.transform.SetParent(generatedRoomRoot, true); }
        SpawnExitPortal(new Vector2(6.35f, 0f), true);
    }

    private void BuildCombatRoomOne(int level)
    {
        // Room one is reached through a hatch (from the tutorial or previous boss).
        SpawnSealedEntryHatch(new Vector2(-6.45f, 0f));
        MovePlayerTo(new Vector2(-5.15f, 0f));
        roomRewardPosition = new Vector2(0f, -2.75f);
        roomExitPosition = new Vector2(roomHalfWidth - 0.18f, 0f);

        // First combat room after tutorial: centered CROSS made from consecutive
        // stone-wall segments. Both arms meet EXACTLY at world (0,0).
        // Around the cross there are wide north, south, east and west routes.
        if (level == 1)
        {
            // Room 1 now follows the requested layout: the old central cross remains,
            // and four extra long wall bars surround it with navigable upper/lower routes.
            CreateWall(Vector2.zero, new Vector2(3.75f, 0.70f));
            CreateWall(Vector2.zero, new Vector2(0.65f, 5.10f));
            CreateWall(new Vector2(0.95f, 2.82f), new Vector2(4.70f, 0.28f));
            CreateWall(new Vector2(1.10f, -2.84f), new Vector2(4.05f, 0.28f));
            CreateWall(new Vector2(-3.92f, 0.08f), new Vector2(0.30f, 3.45f));
            CreateWall(new Vector2(3.92f, 0.08f), new Vector2(0.30f, 3.45f));

            CreateStone(new Vector2(-5.05f,  2.10f), 1.00f);
            CreateStone(new Vector2( 4.95f,  2.15f), 1.00f);
            CreateStone(new Vector2( 5.05f, -2.15f), 1.00f);

            SpawnScaledCombatEnemies(level, 1, new List<Vector2> {
                new Vector2(-5.15f, 2.70f), new Vector2(-1.95f, 2.05f),
                new Vector2(3.55f, 2.05f), new Vector2(-5.00f, -1.85f),
                new Vector2(-1.55f, -2.30f), new Vector2(4.95f, -1.10f),
                new Vector2(2.10f, -2.35f), new Vector2(5.10f, 0.85f) });
        }
        else
        {
            // Level 2 / Room 1 now contains more long walls with wider combat lanes.
            CreateWall(new Vector2(-4.20f, 1.00f), new Vector2(0.34f, 4.85f));
            CreateWall(new Vector2(-1.55f, -0.95f), new Vector2(0.34f, 5.20f));
            CreateWall(new Vector2(1.60f, 1.05f), new Vector2(0.34f, 4.85f));
            CreateWall(new Vector2(4.15f, -0.95f), new Vector2(0.34f, 5.20f));
            CreateWall(new Vector2(-2.70f, 2.45f), new Vector2(2.30f, 0.30f));
            CreateWall(new Vector2(3.05f, -2.35f), new Vector2(2.65f, 0.30f));
            CreateWall(new Vector2(4.65f, 2.15f), new Vector2(1.55f, 0.30f));
            CreateStone(new Vector2(-5.55f, -2.45f), 0.92f);
            CreateStone(new Vector2(5.35f, 2.35f), 0.92f);
            SpawnScaledCombatEnemies(level, 1, new List<Vector2> {
                new Vector2(-5.55f, 2.70f), new Vector2(-5.15f, -2.05f),
                new Vector2(-3.00f, -2.15f), new Vector2(-0.40f, 2.75f),
                new Vector2(-0.25f, -2.10f), new Vector2(2.60f, 2.65f),
                new Vector2(2.70f, -2.10f), new Vector2(5.05f, 2.55f),
                new Vector2(5.25f, -2.10f), new Vector2(0.35f, 0.15f) });
        }
    }

    private void BuildCombatRoomTwo(int level)
    {
        // The player enters through the LEFT wall door from the previous room.
        MovePlayerTo(new Vector2(-6.22f, 0f));
        roomRewardPosition = new Vector2(0f, -3.0f);
        roomExitPosition = new Vector2(roomHalfWidth - 0.18f, 0f);
        List<Vector2> positions = new List<Vector2>();

        // Separate small islands replace the old cross-wall maze. Every corridor
        // stays several character widths across, including the approach to the door.
        if (level == 1)
        {
            CreateStone(new Vector2(-4.1f, 1.55f), 0.95f);
            CreateStone(new Vector2(-1.0f, -1.35f), 1.0f);
            CreateStone(new Vector2(2.7f, 1.5f), 1.0f);
            CreateStone(new Vector2(4.55f, -1.9f), 0.85f);
            CreateWall(new Vector2(0.25f, 2.7f), new Vector2(1.5f, 0.27f));
            CreateWall(new Vector2(-2.80f, 2.20f), new Vector2(0.30f, 2.30f));
            CreateWall(new Vector2(1.20f, -2.10f), new Vector2(2.05f, 0.27f));
            CreateWall(new Vector2(3.65f, 0.55f), new Vector2(0.28f, 2.55f));
            positions.AddRange(new[] {
                new Vector2(-5.1f, 2.6f), new Vector2(-2.05f, 0.25f),
                new Vector2(-0.1f, 1.1f), new Vector2(1.7f, 2.6f),
                new Vector2(5.0f, 2.5f), new Vector2(-3.2f, -2.25f),
                new Vector2(2.45f, -0.90f), new Vector2(5.35f, -0.5f) });
        }
        else
        {
            CreateStone(new Vector2(-4.55f, 1.45f), 1.0f);
            CreateStone(new Vector2(-1.45f, -1.8f), 1.0f);
            CreateStone(new Vector2(1.6f, 1.65f), 0.95f);
            CreateStone(new Vector2(4.7f, -1.9f), 0.90f);
            CreateWall(new Vector2(-0.15f, 2.85f), new Vector2(1.45f, 0.28f));
            CreateWall(new Vector2(3.05f, -2.95f), new Vector2(1.20f, 0.28f));
            CreateWall(new Vector2(-3.35f, 0.20f), new Vector2(0.30f, 2.70f));
            CreateWall(new Vector2(0.70f, -0.05f), new Vector2(0.28f, 3.05f));
            CreateWall(new Vector2(3.85f, 1.95f), new Vector2(1.85f, 0.28f));
            positions.AddRange(new[] {
                new Vector2(-5.3f, 2.75f), new Vector2(-2.2f, 2.55f),
                new Vector2(-0.35f, 0.5f), new Vector2(2.3f, 2.8f),
                new Vector2(5.2f, 2.45f), new Vector2(-3.25f, -2.65f),
                new Vector2(1.0f, -2.45f), new Vector2(5.35f, -0.2f) });
        }

        // Extra candidate points remain spread out so higher enemy counts do not
        // stack inside the new wall clusters.
        positions.AddRange(new[] {
            new Vector2(-4.55f, -0.45f), new Vector2(0.35f, -2.40f),
            new Vector2(3.70f, 0.0f), new Vector2(-1.65f, 0.75f),
            new Vector2(0.85f, 1.85f), new Vector2(3.85f, -2.55f),
            new Vector2(-0.55f, 2.20f), new Vector2(2.95f, -2.10f) });
        SpawnScaledCombatEnemies(level, 2, positions);
    }

    private void BuildBossRoom(int level)
    {
        // The boss arena is entered from the same left-wall door as regular rooms.
        MovePlayerTo(new Vector2(-6.22f, 0f));
        // Boss rooms deliberately contain no internal static walls or stones.
        // Outer boundary walls and the sealed entrance remain intact.
        GameObject obj = new GameObject(level == 1 ? "Wraith King" : "Cyclops King");
        obj.transform.SetParent(generatedRoomRoot, false);
        obj.transform.position = new Vector2(2.60f, 0f);
        Rigidbody2D bossBody = obj.AddComponent<Rigidbody2D>();
        bossBody.bodyType = RigidbodyType2D.Dynamic;
        bossBody.gravityScale = 0f;
        bossBody.linearDamping = 2.4f;
        bossBody.angularDamping = 0f;
        bossBody.freezeRotation = true;
        bossBody.collisionDetectionMode = CollisionDetectionMode2D.Continuous;
        bossBody.interpolation = RigidbodyInterpolation2D.Interpolate;
        obj.AddComponent<CircleCollider2D>();
        obj.AddComponent<SpriteRenderer>();
        BossController boss = obj.AddComponent<BossController>();
        boss.EnsureRuntimeReady();
        boss.Initialize(level == 1 ? BossController.BossKind.WraithKing : BossController.BossKind.CyclopsKing, player);
    }

    private void EnsurePlayer()
    {
        if (player != null)
        {
            player.EnsureRuntimeReady();
            return;
        }

        player = FindAnyObjectByType<PlayerController>();
        if (player != null)
        {
            player.EnsureRuntimeReady();
            return;
        }

        GameObject playerObject = new GameObject("Player");

        // Add required runtime components explicitly before PlayerController.
        // This avoids a Unity 6.6 migration edge case where the controller could
        // enter its first physics tick before a Rigidbody2D was available.
        Rigidbody2D rb = playerObject.AddComponent<Rigidbody2D>();
        rb.bodyType = RigidbodyType2D.Dynamic;
        rb.gravityScale = 0f;
        rb.linearDamping = 0f;
        rb.angularDamping = 0f;
        rb.freezeRotation = true;
        rb.collisionDetectionMode = CollisionDetectionMode2D.Continuous;
        rb.interpolation = RigidbodyInterpolation2D.Interpolate;

        CircleCollider2D collider = playerObject.AddComponent<CircleCollider2D>();
        collider.isTrigger = false;
        collider.radius = 0.43f;

        playerObject.AddComponent<SpriteRenderer>();
        player = playerObject.AddComponent<PlayerController>();
        player.EnsureRuntimeReady();
    }

    private void MovePlayerTo(Vector2 p)
    {
        EnsurePlayer();
        if (player == null) return;

        player.EnsureRuntimeReady();
        player.transform.position = new Vector3(p.x, p.y, 0f);

        Rigidbody2D rb = player.GetComponent<Rigidbody2D>();
        if (rb == null)
        {
            rb = player.gameObject.AddComponent<Rigidbody2D>();
            player.EnsureRuntimeReady();
        }

        rb.position = p;
        rb.linearVelocity = Vector2.zero;
        rb.angularVelocity = 0f;
        rb.WakeUp();
    }

    private void SpawnScaledCombatEnemies(int level, int room, List<Vector2> candidates)
    {
        // Final encounter counts, Skeleton and Cyclops enemies only:
        // L1: 3-4 / 5-6, L2: 7-8 / 9-10, Skeletons and Cyclops only.
        int targetCount = room == 1
            ? (level >= 2 ? Random.Range(7, 9) : Random.Range(3, 5))
            : (level >= 2 ? Random.Range(9, 11) : Random.Range(5, 7));

        Shuffle(candidates);
        int spawnCount = 0;
        for (int i = 0; i < candidates.Count && spawnCount < targetCount; i++)
        {
            EnemyAI.EnemyKind kind = Random.value < (level >= 2 ? 0.42f : 0.62f)
                ? EnemyAI.EnemyKind.Skeleton : EnemyAI.EnemyKind.Cyclops;
            SpawnTrackedEnemy(kind, candidates[i]);
            spawnCount++;
        }

        for (; spawnCount < targetCount; spawnCount++)
        {
            EnemyAI.EnemyKind kind = Random.value < 0.5f
                ? EnemyAI.EnemyKind.Skeleton : EnemyAI.EnemyKind.Cyclops;
            SpawnTrackedEnemy(kind,
                new Vector2(Random.Range(-5f, 5f), Random.Range(-2.8f, 2.8f)));
        }
    }

    private Vector2 ResolveSafeEnemySpawn(Vector2 requestedPosition)
    {
        if (player == null)
            return requestedPosition;

        Vector2 playerPosition = player.transform.position;
        float minimumDistance = Mathf.Max(2.5f, minimumEnemySpawnDistanceFromPlayer);

        if (Vector2.Distance(requestedPosition, playerPosition) >= minimumDistance &&
            IsEnemySpawnAreaClear(requestedPosition))
        {
            return requestedPosition;
        }

        // Search the room for a safe spawn point that is both far from the player
        // and clear of static walls/stones.
        for (int attempt = 0; attempt < 28; attempt++)
        {
            Vector2 candidate = new Vector2(
                Random.Range(-roomHalfWidth + 1.0f, roomHalfWidth - 1.0f),
                Random.Range(-roomHalfHeight + 0.85f, roomHalfHeight - 0.85f));

            if (Vector2.Distance(candidate, playerPosition) < minimumDistance)
                continue;

            if (!IsEnemySpawnAreaClear(candidate))
                continue;

            return candidate;
        }

        // Deterministic fallback: choose the farthest corner from the player.
        Vector2[] corners =
        {
            new Vector2(-roomHalfWidth + 1.15f, roomHalfHeight - 1.0f),
            new Vector2(roomHalfWidth - 1.15f, roomHalfHeight - 1.0f),
            new Vector2(-roomHalfWidth + 1.15f, -roomHalfHeight + 1.0f),
            new Vector2(roomHalfWidth - 1.15f, -roomHalfHeight + 1.0f)
        };

        Vector2 best = requestedPosition;
        float bestDistance = float.NegativeInfinity;
        for (int i = 0; i < corners.Length; i++)
        {
            float distance = Vector2.Distance(corners[i], playerPosition);
            if (distance >= minimumDistance && distance > bestDistance &&
                IsEnemySpawnAreaClear(corners[i]))
            {
                best = corners[i];
                bestDistance = distance;
            }
        }
        if (bestDistance > 0f)
            return best;

        // Deterministic fallback grid covers the entire room when candidate
        // positions/corners are crowded by cover or already spawned enemies.
        for (float y = -2.75f; y <= 2.75f; y += 0.95f)
        for (float x = -5.75f; x <= 5.75f; x += 0.95f)
        {
            Vector2 candidate = new Vector2(x, y);
            if (Vector2.Distance(candidate, playerPosition) >= minimumDistance &&
                IsEnemySpawnAreaClear(candidate))
                return candidate;
        }

        Debug.LogWarning("Enemy placement could not find a fully free cell; using original candidate.", this);
        return requestedPosition;
    }

    private static bool IsEnemySpawnAreaClear(Vector2 position)
    {
        Physics2D.SyncTransforms();
        // This also rejects overlapping enemies and the player. Previously the
        // check ignored them and could stack several enemies at one spawn point.
        Collider2D[] hits = Physics2D.OverlapCircleAll(position, 0.70f);
        for (int i = 0; i < hits.Length; i++)
        {
            Collider2D hit = hits[i];
            if (hit == null || hit.isTrigger) continue;
            if (hit.GetComponentInParent<EnemyAI>() != null ||
                hit.GetComponentInParent<PlayerController>() != null)
                return false;
            Rigidbody2D attachedBody = hit.attachedRigidbody;
            if (attachedBody == null || attachedBody.bodyType == RigidbodyType2D.Static)
                return false;
        }
        return true;
    }

    private void SpawnTrackedEnemy(EnemyAI.EnemyKind kind, Vector2 position)
    {
        position = ResolveSafeEnemySpawn(position);

        GameObject obj = new GameObject(kind.ToString());
        obj.transform.SetParent(generatedRoomRoot, false);
        obj.transform.position = position;

        // Explicit component order is required for reliable runtime spawning in Unity 6.6.
        Rigidbody2D rb = obj.AddComponent<Rigidbody2D>();
        rb.bodyType = RigidbodyType2D.Dynamic;
        rb.gravityScale = 0f;
        rb.linearDamping = 3f;
        rb.angularDamping = 0f;
        rb.freezeRotation = true;
        rb.collisionDetectionMode = CollisionDetectionMode2D.Continuous;
        rb.interpolation = RigidbodyInterpolation2D.Interpolate;

        CircleCollider2D circle = obj.AddComponent<CircleCollider2D>();
        circle.isTrigger = false;
        obj.AddComponent<SpriteRenderer>();

        EnemyAI enemy = obj.AddComponent<EnemyAI>();
        enemy.EnsureRuntimeReady();
        enemy.Initialize(kind, player, false);
        enemy.EnemyDied += HandleRoomEnemyDied;
        trackedEnemies.Add(enemy);
        aliveRoomEnemies = trackedEnemies.Count;
    }
    private void HandleRoomEnemyDied(EnemyAI enemy)
    {
        if (enemy != null) { enemy.EnemyDied -= HandleRoomEnemyDied; trackedEnemies.Remove(enemy); } aliveRoomEnemies = Mathf.Max(0, aliveRoomEnemies - 1); if (aliveRoomEnemies > 0 || roomClearHandled) return; roomClearHandled = true; StartCoroutine(HandleRoomClearedRoutine(generationVersion));
    }
    private IEnumerator HandleRoomClearedRoutine(int version)
    {
        yield return new WaitForSeconds(Mathf.Max(0f, roomClearRewardDelay));
        if (version != generationVersion || gameManager == null || gameManager.CurrentState != GameManager.GameState.GAMEPLAY || gameManager.CurrentRoomIndex >= 3) yield break;

        if (itemSystem == null) itemSystem = ItemSystem.Instance;
        if (itemSystem != null)
        {
            RuntimeItemPickup reward = itemSystem.SpawnRoomClearReward(roomRewardPosition, gameManager.CurrentRoomIndex);
            if (reward != null && generatedRoomRoot != null) reward.transform.SetParent(generatedRoomRoot, true);
        }

        if (currentRoomDoor != null)
            currentRoomDoor.Unlock();

        RuntimeVfx.SpawnCircularBurst(roomExitPosition, new Color(0.44f, 0.025f, 0.055f, 1f), 22, 0.13f, 3.7f, 0.55f);
    }

    private void SpawnSealedEntryHatch(Vector2 position)
    {
        GameObject hatch = new GameObject("Sealed Arrival Hatch (Locked)");
        hatch.transform.SetParent(generatedRoomRoot, false);
        hatch.AddComponent<RuntimeSealedEntryHatch>().Initialize(position);
    }

    private void CreateSealedEntranceDoor(Vector2 position, float openingHeight)
    {
        GameObject entryDoor = new GameObject("Previous Room Entrance (Chained Shut)");
        entryDoor.transform.SetParent(generatedRoomRoot, false);
        RuntimeRoomDoor door = entryDoor.AddComponent<RuntimeRoomDoor>();
        door.Initialize(position, new Vector2(0.52f, openingHeight));
        door.FlipArtworkHorizontally();
        // Remains locked permanently; only the right exit door may Unlock().
    }

    /// <summary>
    /// All exposed interior gaps between stone/wall hitboxes must fit a normal
    /// enemy with generous visual clearance. Narrow leftover gaps are removed
    /// by relocating movable cover, rather than leaving a collision trap.
    /// </summary>
    private void NormalizeObstacleClearances()
    {
        if (generatedRoomRoot == null) return;
        Physics2D.SyncTransforms();

        int moved = 0;
        int removed = 0;
        RuntimeDungeonStone[] stones = generatedRoomRoot.GetComponentsInChildren<RuntimeDungeonStone>();
        foreach (RuntimeDungeonStone stone in stones)
        {
            if (stone == null || !stone.gameObject.activeInHierarchy) continue;
            Collider2D solid = stone.GetComponent<Collider2D>();
            if (solid == null || ObstacleHasWideClearance(solid)) continue;
            if (FindWideClearancePosition(solid))
                moved++;
            else
            {
                // Never leave an almost-passable corridor that wedges an enemy.
                solid.enabled = false;
                stone.gameObject.SetActive(false);
                Destroy(stone.gameObject);
                removed++;
            }
        }

        // Interior short walls can also create narrow slots next to stone props
        // or the outer walls. Keep the purpose-built centered cross intact.
        bool keepCenterCross = gameManager != null &&
            gameManager.CurrentLevelIndex == 1 && gameManager.CurrentRoomIndex == 1;
        RuntimeDungeonWall[] walls = generatedRoomRoot.GetComponentsInChildren<RuntimeDungeonWall>();
        foreach (RuntimeDungeonWall wall in walls)
        {
            if (wall == null || !wall.gameObject.activeInHierarchy) continue;
            if (IsOuterBoundaryWall(wall.transform.position)) continue;
            if (keepCenterCross && ((Vector2)wall.transform.position).sqrMagnitude < 0.01f)
                continue;
            Collider2D solid = wall.GetComponent<Collider2D>();
            if (solid == null || ObstacleHasWideClearance(solid)) continue;
            if (FindWideClearancePosition(solid))
                moved++;
            else
            {
                solid.enabled = false;
                wall.gameObject.SetActive(false);
                Destroy(wall.gameObject);
                removed++;
            }
        }
        Physics2D.SyncTransforms();
        // Actor spawn points are subordinate to the safe geometry. Move any
        // enemy that now overlaps a relocated rock, without changing its AI/HP.
        RecheckEnemySpawnsAfterObstacleMoves();
        Debug.Log("Проверка проходов: минимальная ширина " + minimumObstacleGap.ToString("0.00") +
            " м, перемещено препятствий " + moved + ", исключено узких проходов " + removed, this);
    }

    private void RecheckEnemySpawnsAfterObstacleMoves()
    {
        foreach (EnemyAI enemy in trackedEnemies)
        {
            if (enemy == null || enemy.IsDead) continue;
            Collider2D own = enemy.GetComponent<Collider2D>();
            if (own == null) continue;
            // IsEnemySpawnAreaClear checks all actual solid obstacles, and the
            // actor itself must not count as a collision.
            own.enabled = false;
            Physics2D.SyncTransforms();
            Vector2 current = enemy.transform.position;
            if (!IsEnemySpawnAreaClear(current))
            {
                Vector2 destination = ResolveSafeEnemySpawn(current);
                Rigidbody2D body = enemy.GetComponent<Rigidbody2D>();
                enemy.transform.position = destination;
                if (body != null)
                {
                    body.position = destination;
                    body.linearVelocity = Vector2.zero;
                }
            }
            own.enabled = true;
        }
        Physics2D.SyncTransforms();
    }

    private bool IsOuterBoundaryWall(Vector2 position)
    {
        return Mathf.Abs(position.x) > roomHalfWidth - 0.45f ||
               Mathf.Abs(position.y) > roomHalfHeight - 0.45f;
    }

    private static float GapBetweenBounds(Bounds a, Bounds b)
    {
        float dx = Mathf.Max(0f, Mathf.Max(a.min.x - b.max.x, b.min.x - a.max.x));
        float dy = Mathf.Max(0f, Mathf.Max(a.min.y - b.max.y, b.min.y - a.max.y));
        return Mathf.Sqrt(dx * dx + dy * dy);
    }

    private bool ObstacleHasWideClearance(Collider2D obstacle, Collider2D[] cachedColliders = null)
    {
        if (obstacle == null || !obstacle.enabled) return false;
        Bounds a = obstacle.bounds;
        // Leave the room boundary in place; collision positions are checked
        // against the same real wall colliders as all other solid surfaces.
        Collider2D[] all = cachedColliders ?? generatedRoomRoot.GetComponentsInChildren<Collider2D>();
        foreach (Collider2D other in all)
        {
            if (other == null || other == obstacle || !other.enabled || other.isTrigger ||
                !other.gameObject.activeInHierarchy) continue;
            Rigidbody2D rb = other.attachedRigidbody;
            if (rb != null && rb.bodyType != RigidbodyType2D.Static) continue;
            if (GapBetweenBounds(a, other.bounds) < minimumObstacleGap)
                return false;
        }
        return true;
    }

    private bool FindWideClearancePosition(Collider2D obstacle)
    {
        Vector3 original = obstacle.transform.position;
        // Cache colliders once for the search; recomputing the entire hierarchy
        // for every candidate would cause needless garbage and frame hitches.
        Collider2D[] neighbors = generatedRoomRoot.GetComponentsInChildren<Collider2D>();
        // Deterministic closest-first search prevents objects randomly moving
        // to another side of the room when a nearby broad corridor is free.
        const float radialStep = 0.48f;
        for (int ring = 1; ring <= 12; ring++)
        {
            int directions = Mathf.Max(12, ring * 8);
            for (int i = 0; i < directions; i++)
            {
                float radians = (i * 2f * Mathf.PI) / directions;
                Vector2 location = (Vector2)original +
                    new Vector2(Mathf.Cos(radians), Mathf.Sin(radians)) * (ring * radialStep);
                if (TestObstaclePlacement(obstacle, location, neighbors))
                    return true;
            }
        }
        obstacle.transform.position = original;
        Physics2D.SyncTransforms();
        return false;
    }

    private bool TestObstaclePlacement(Collider2D obstacle, Vector2 location, Collider2D[] neighbors)
    {
        obstacle.transform.position = location;
        Physics2D.SyncTransforms();
        Bounds bounds = obstacle.bounds;

        // Keep solid props inside the room without reducing the walking lane
        // next to the top, bottom or side boundaries.
        if (bounds.min.x < -roomHalfWidth + 0.25f ||
            bounds.max.x > roomHalfWidth - 0.25f ||
            bounds.min.y < -roomHalfHeight + 0.25f ||
            bounds.max.y > roomHalfHeight - 0.25f)
            return false;

        if (player != null && bounds.SqrDistance(player.transform.position) < 0.95f * 0.95f)
            return false;
        // Enemy positions are repaired after the obstacle layout is finalized.
        // Do not delete fixed rocks merely because an enemy spawned nearby.
        if (gameManager != null && gameManager.CurrentRoomIndex < 3 &&
            bounds.SqrDistance(roomRewardPosition) < 1.35f * 1.35f)
            return false;
        if (bounds.SqrDistance(roomExitPosition) < 1.60f * 1.60f)
            return false;
        // Reserve an approach lane to the entrance/exit for player and enemies.
        if ((location.x < -4.5f || location.x > 4.65f) &&
            Mathf.Abs(location.y) < 1.55f)
            return false;
        return ObstacleHasWideClearance(obstacle, neighbors);
    }

    private void AddSpacedCover()
    {
        if (gameManager == null || gameManager.IsTutorial || gameManager.IsBossRoom || generatedRoomRoot == null)
            return;
        // The central cross is the deliberate ricochet geometry in Level 1 Room 1;
        // random walls here would re-create narrow passages beside the stones.
        if (gameManager.CurrentLevelIndex == 1 && gameManager.CurrentRoomIndex == 1)
            return;

        // Extra short, isolated walls create reliable dagger-ricochet surfaces.
        // Candidates avoid the center lane and keep doors and enemy start sites free.
        Vector2[] proposed = new[] {
            new Vector2(-5.25f,  2.35f), new Vector2(-3.55f, -2.45f),
            new Vector2(-1.95f,  2.65f), new Vector2( 0.05f, -2.55f),
            new Vector2( 1.85f,  2.45f), new Vector2( 3.70f, -2.55f),
            new Vector2( 5.10f,  2.35f), new Vector2(-4.75f, -2.55f),
            new Vector2(-2.40f, -2.65f), new Vector2(-0.40f,  2.55f),
            new Vector2( 2.60f, -2.50f), new Vector2( 4.45f,  2.55f),
            new Vector2(-3.85f,  2.55f), new Vector2(-1.20f, -2.58f),
            new Vector2( 1.10f,  2.60f), new Vector2( 4.85f, -2.65f),
            new Vector2(-5.45f, -2.40f), new Vector2( 3.20f,  2.52f)
        };
        int targetWalls = gameManager.CurrentRoomIndex == 1 ? 5 : 6;
        int placed = 0;
        int offset = (gameManager.CurrentLevelIndex * 5 + gameManager.CurrentRoomIndex * 3) % proposed.Length;
        Physics2D.SyncTransforms();
        for (int i = 0; i < proposed.Length && placed < targetWalls; i++)
        {
            Vector2 location = proposed[(i + offset) % proposed.Length];
            if (!CoverLocationFree(location, 0.88f))
                continue;

            // Alternating orientations make reflected daggers bounce along
            // horizontal AND vertical axes instead of only one direction.
            bool vertical = (i + gameManager.CurrentLevelIndex) % 3 == 1;
            Vector2 wallSize = vertical ? new Vector2(0.28f, 0.90f) :
                new Vector2(1.18f, 0.28f);

            GameObject wall = new GameObject("Ricochet Wall - Short Stone Barrier");
            wall.transform.SetParent(generatedRoomRoot, false);
            wall.AddComponent<RuntimeDungeonWall>().Initialize(location, wallSize);
            Physics2D.SyncTransforms();

            // No tiny slot between the new wall and a pre-existing stone/wall.
            // Even a traversable flow-field can include a corridor too narrow
            // for the moving sprites, so check real collider bounds as well.
            BoxCollider2D candidateBox = wall.GetComponent<BoxCollider2D>();
            // If an authored ricochet-wall position is too narrow, try another
            // nearby open slot instead of losing every extra bounce surface.
            bool widthValid = candidateBox != null &&
                (ObstacleHasWideClearance(candidateBox) || FindWideClearancePosition(candidateBox));
            if (!widthValid || !RoomTraversalIsValid())
            {
                Collider2D[] colliders = wall.GetComponentsInChildren<Collider2D>();
                foreach (Collider2D collider in colliders) collider.enabled = false;
                wall.SetActive(false);
                Destroy(wall);
                Physics2D.SyncTransforms();
                continue;
            }
            placed++;
        }
        Debug.Log("Установлено дополнительных стен для рикошетов: " + placed, this);
    }

    public bool TryGetEnemySteering(Vector2 from, Vector2 playerPosition, out Vector2 direction)
    {
        direction = Vector2.zero;
        return roomNavigator != null && roomNavigator.TryGetDirection(from, playerPosition, out direction);
    }

    private bool RoomTraversalIsValid()
    {
        // Navigation grid checks a 0.95 unit diameter corridor, slightly wider
        // than the normal enemy collider. BFS uses only orthogonal neighbors,
        // so inaccessible narrow diagonal gaps are correctly rejected.
        const int columns = 35;
        const int rows = 17;
        const float startX = -6.65f;
        const float startY = -3.16f;
        const float stepX = 0.39f;
        const float stepY = 0.395f;
        bool[,] passable = new bool[columns, rows];
        bool[,] seen = new bool[columns, rows];
        Physics2D.SyncTransforms();
        for (int x = 0; x < columns; x++)
        for (int y = 0; y < rows; y++)
        {
            Vector2 point = new Vector2(startX + x * stepX, startY + y * stepY);
            passable[x, y] = IsWalkableForEnemy(point, 0.60f);
        }

        Vector2 playerPosition = player != null ? (Vector2)player.transform.position : new Vector2(-5.15f, 0f);
        if (!TryFindGridNode(playerPosition, passable, columns, rows, startX, startY, stepX, stepY,
            out int startColumn, out int startRow)) return false;

        int[] qx = new int[columns * rows];
        int[] qy = new int[columns * rows];
        int head = 0, tail = 0;
        seen[startColumn, startRow] = true;
        qx[tail] = startColumn;
        qy[tail] = startRow;
        tail++;
        int[] dx = { 1, -1, 0, 0 };
        int[] dy = { 0, 0, 1, -1 };
        while (head < tail)
        {
            int x = qx[head], y = qy[head];
            head++;
            for (int d = 0; d < 4; d++)
            {
                int nx = x + dx[d], ny = y + dy[d];
                if (nx < 0 || ny < 0 || nx >= columns || ny >= rows ||
                    !passable[nx, ny] || seen[nx, ny]) continue;
                seen[nx, ny] = true;
                qx[tail] = nx;
                qy[tail] = ny;
                tail++;
            }
        }

        // The exit must remain reachable after clearing the room.
        Vector2 exitApproach = new Vector2(5.70f, 0f);
        if (!CanReachGridTarget(exitApproach, passable, seen, columns, rows, startX, startY, stepX, stepY))
            return false;
        foreach (EnemyAI enemy in trackedEnemies)
        {
            if (enemy == null || enemy.IsDead) continue;
            if (!CanReachGridTarget(enemy.transform.position, passable, seen, columns, rows,
                startX, startY, stepX, stepY)) return false;
        }
        if (gameManager != null && gameManager.CurrentRoomIndex < 3 &&
            !CanReachGridTarget(roomRewardPosition, passable, seen, columns, rows,
                startX, startY, stepX, stepY)) return false;
        BossController boss = generatedRoomRoot.GetComponentInChildren<BossController>();
        if (boss != null && !CanReachGridTarget(boss.transform.position, passable, seen,
            columns, rows, startX, startY, stepX, stepY)) return false;
        return true;
    }

    private static bool IsWalkableForEnemy(Vector2 position, float radius)
    {
        Collider2D[] overlaps = Physics2D.OverlapCircleAll(position, radius);
        foreach (Collider2D collider in overlaps)
        {
            if (collider == null || !collider.enabled || collider.isTrigger) continue;
            Rigidbody2D rigidbody = collider.attachedRigidbody;
            // Moving actors should not act as permanent blocking geometry.
            if (rigidbody == null || rigidbody.bodyType == RigidbodyType2D.Static)
                return false;
        }
        return true;
    }

    private static bool TryFindGridNode(Vector2 target, bool[,] passable, int columns, int rows,
        float startX, float startY, float stepX, float stepY, out int bestX, out int bestY)
    {
        bestX = -1;
        bestY = -1;
        float bestDistance = 0.90f * 0.90f;
        for (int x = 0; x < columns; x++)
        for (int y = 0; y < rows; y++)
        {
            if (!passable[x, y]) continue;
            Vector2 node = new Vector2(startX + x * stepX, startY + y * stepY);
            float distance = (target - node).sqrMagnitude;
            if (distance < bestDistance)
            {
                bestDistance = distance;
                bestX = x;
                bestY = y;
            }
        }
        return bestX >= 0;
    }

    private static bool CanReachGridTarget(Vector2 target, bool[,] passable, bool[,] seen,
        int columns, int rows, float startX, float startY, float stepX, float stepY)
    {
        return TryFindGridNode(target, passable, columns, rows, startX, startY, stepX, stepY,
            out int x, out int y) && seen[x, y];
    }

    private bool CoverLocationFree(Vector2 point, float clearance)
    {
        if (Mathf.Abs(point.x) > roomHalfWidth - 1.30f ||
            Mathf.Abs(point.y) > roomHalfHeight - 1.20f)
            return false;
        // Never make the left spawn or right exit a chokepoint.
        if (point.x < -3.90f && Mathf.Abs(point.y) < 1.70f) return false;
        if (point.x > 4.40f && Mathf.Abs(point.y) < 1.70f) return false;
        if (Mathf.Abs(point.y) < 1.35f) return false;
        if (gameManager != null && gameManager.CurrentRoomIndex < 3 &&
            Vector2.Distance(point, roomRewardPosition) < 1.70f) return false;
        if (player != null && Vector2.Distance(point, player.transform.position) < 2.50f) return false;
        Physics2D.SyncTransforms();
        Collider2D[] occupants = Physics2D.OverlapCircleAll(point, clearance + 0.45f);
        foreach (Collider2D occupant in occupants)
        {
            if (occupant == null || occupant.isTrigger) continue;
            return false;
        }
        return true;
    }

    private void PlaceApprovedRoomDecor()
    {
        if (RuntimeGameArt.Load("Props/Blood_01") == null || generatedRoomRoot == null)
            return;

        // HARD CAP: six loose Props + exactly two wall torches = eight per room.
        // Floor positions are stratified into a 3x2 grid. At most one prop is
        // placed in each cell, so objects don't stack or cluster at the center.
        Physics2D.SyncTransforms();
        int level = gameManager == null ? 0 : gameManager.CurrentLevelIndex;
        int room = gameManager == null ? 0 : gameManager.CurrentRoomIndex;
        System.Random random = new System.Random(4729 + level * 2011 + room * 409);
        List<Vector2> occupiedPropPositions = new List<Vector2>();
        string[] bones = { "Props/Bones_01", "Props/Bones_02", "Props/Skulls_01", "Props/Bones_04" };
        // The stone obstacles also display Props/Stone, so they count toward
        // the same EIGHT-asset limit as loose remains and mounted torches.
        int stonePropCount = generatedRoomRoot.GetComponentsInChildren<RuntimeDungeonStone>().Length;
        int floorPropBudget = Mathf.Max(0, 8 - stonePropCount - 2);
        int floorPropCount = 0;

        for (int index = 0; index < floorPropBudget; index++)
        {
            // Spread the allowed floor decorations across the entire 3x2 grid,
            // even when a stone-heavy room has room for only 2-3 loose props.
            int cell = Mathf.Clamp(Mathf.FloorToInt((index + 0.5f) * 6f /
                Mathf.Max(1, floorPropBudget)), 0, 5);
            int row = cell / 3;
            int col = cell % 3;
            bool blood = (index % 3 == 1);
            string sprite = blood
                ? "Props/Blood_" + (1 + random.Next(8)).ToString("00")
                : bones[(row * 3 + col) % bones.Length];
            float visualWidth = blood ? 0.94f : 1.22f;
            bool placed = false;
            for (int attempt = 0; attempt < 14 && !placed; attempt++)
            {
                float cellWidth = 13.10f / 3f;
                float cellHeight = 6.55f / 2f;
                float fx = 0.25f + (float)random.NextDouble() * 0.50f;
                float fy = 0.25f + (float)random.NextDouble() * 0.50f;
                Vector2 point = new Vector2(
                    -6.55f + cellWidth * (col + fx),
                    -3.275f + cellHeight * (row + fy));
                if (!CanPlaceNonBlockingDecor(point, visualWidth * 0.57f, occupiedPropPositions))
                    continue;

                GameObject decor = RuntimeGameArt.SpawnDecor(sprite, generatedRoomRoot,
                    point, visualWidth, blood ? -92 : -85);
                if (decor == null) continue;
                occupiedPropPositions.Add(point);
                floorPropCount++;
                placed = true;
            }
        }

        // Replace the old torches with the supplied wall torch artwork.
        SpawnWallTorch(new Vector2(-5.55f, roomHalfHeight - 0.10f), "Torches/WallTorch_Front");
        SpawnWallTorch(new Vector2(5.55f, roomHalfHeight - 0.10f), "Torches/WallTorch_Front");
        // Both sides of every usable door get a torch, without colliders.
        bool hasLeftDoor = gameManager != null && gameManager.CurrentLevelIndex > 0 && gameManager.CurrentRoomIndex >= 2;
        bool hasRightDoor = gameManager != null && gameManager.CurrentLevelIndex > 0 && gameManager.CurrentRoomIndex >= 1 && gameManager.CurrentRoomIndex < 3;
        if (hasLeftDoor)
        {
            SpawnWallTorch(new Vector2(-roomHalfWidth + 0.22f, 1.58f), "Torches/WallTorch_SideRight");
            SpawnWallTorch(new Vector2(-roomHalfWidth + 0.22f, -1.58f), "Torches/WallTorch_SideRight");
        }
        if (hasRightDoor)
        {
            SpawnWallTorch(new Vector2(roomHalfWidth - 0.22f, 1.58f), "Torches/WallTorch_SideLeft");
            SpawnWallTorch(new Vector2(roomHalfWidth - 0.22f, -1.58f), "Torches/WallTorch_SideLeft");
        }
        Debug.Log("Props placed: " + floorPropCount + " floor, " + stonePropCount + " stones and wall torches", this);
    }

    private bool CanPlaceNonBlockingDecor(Vector2 position, float radius, List<Vector2> occupied)
    {
        if (Mathf.Abs(position.x) > roomHalfWidth - radius - boundaryThickness ||
            Mathf.Abs(position.y) > roomHalfHeight - radius - boundaryThickness)
            return false;
        if (player != null && Vector2.Distance(position, player.transform.position) < 1.6f)
            return false;
        if (Vector2.Distance(position, roomExitPosition) < 1.6f)
            return false;
        for (int i = 0; i < occupied.Count; i++)
            if (Vector2.Distance(position, occupied[i]) < radius + 0.75f)
                return false;

        Collider2D[] colliders = Physics2D.OverlapCircleAll(position, radius + 0.18f);
        foreach (Collider2D hit in colliders)
        {
            if (hit == null || !hit.enabled || hit.isTrigger) continue;
            // Solid walls, stones and actors are all avoided.
            return false;
        }
        return true;
    }

    private void SpawnWallTorch(Vector2 wallPosition, string artPath)
    {
        GameObject torch = RuntimeGameArt.SpawnDecor(artPath, generatedRoomRoot, wallPosition, 0.44f, 12);
        if (torch == null) return;
        torch.name = "Wall Mounted Torch";
        // The sprite is centered over the stonework, with its lower part inside
        // the arena; keep its back behind the player but in front of wall art.
        SpriteRenderer renderer = torch.GetComponent<SpriteRenderer>();
        if (renderer != null)
        {
            renderer.color = new Color(0.93f, 0.80f, 0.70f, 1f);
            // Restore the v31 mounting positions/orientation, but mirror the
            // side torches HORIZONTALLY (not vertically, as in v32).
            renderer.flipY = false;
            renderer.flipX = artPath == "Torches/WallTorch_SideLeft" ||
                             artPath == "Torches/WallTorch_SideRight";
        }
    }

    public void RequestEnemyNavigationRefresh()
    {
        if (roomNavigator != null && generatedRoomRoot != null && gameObject.activeInHierarchy)
            StartCoroutine(RefreshEnemyNavigationNextPhysicsStep());
    }

    private System.Collections.IEnumerator RefreshEnemyNavigationNextPhysicsStep()
    {
        RuntimeRoomNavigator currentNavigator = roomNavigator;
        yield return new WaitForFixedUpdate();
        if (currentNavigator != null && currentNavigator == roomNavigator && generatedRoomRoot != null)
            currentNavigator.Initialize(roomHalfWidth, roomHalfHeight);
    }

    private void PlaceBreakablePots()
    {
        bool originallyTraversable = RoomTraversalIsValid();
        int placed = 0;
        List<Vector2> occupied = new List<Vector2>();

        if (gameManager != null && gameManager.IsBossRoom)
        {
            Vector2[] cornerPoints =
            {
                new Vector2(-5.55f,  2.70f),
                new Vector2( 5.55f,  2.70f),
                new Vector2(-5.55f, -2.70f),
                new Vector2( 5.55f, -2.70f)
            };

            foreach (Vector2 point in cornerPoints)
            {
                if (!CanPlaceNonBlockingDecor(point, 0.70f, occupied))
                    continue;
                GameObject pot = new GameObject("Clay Pot - breakable with dagger");
                pot.transform.SetParent(generatedRoomRoot, false);
                pot.transform.position = point;
                pot.AddComponent<RuntimeBreakablePot>();
                Physics2D.SyncTransforms();
                if (originallyTraversable && !RoomTraversalIsValid())
                {
                    pot.SetActive(false);
                    Destroy(pot);
                    Physics2D.SyncTransforms();
                    continue;
                }
                occupied.Add(point);
                placed++;
            }
            return;
        }

        int desired = Random.Range(2, 5);
        List<Vector2> candidates = new List<Vector2>();
        for (float y = -2.8f; y <= 2.8f; y += 1.15f)
        for (float x = -5.7f; x <= 5.7f; x += 1.15f)
            candidates.Add(new Vector2(x + Random.Range(-0.18f, 0.18f), y + Random.Range(-0.15f, 0.15f)));
        Shuffle(candidates);
        foreach (Vector2 point in candidates)
        {
            if (placed >= desired) break;
            if (point.x < -5.9f && Mathf.Abs(point.y) < 1.3f) continue;
            if (point.x > 5.9f && Mathf.Abs(point.y) < 1.3f) continue;
            if (Vector2.Distance(point, roomRewardPosition) < 1.3f && gameManager.IsBossRoom == false) continue;
            if (!CanPlaceNonBlockingDecor(point, 0.78f, occupied)) continue;
            GameObject pot = new GameObject("Clay Pot - breakable with dagger");
            pot.transform.SetParent(generatedRoomRoot, false);
            pot.transform.position = point;
            pot.AddComponent<RuntimeBreakablePot>();
            Physics2D.SyncTransforms();
            if (originallyTraversable && !RoomTraversalIsValid())
            {
                pot.SetActive(false);
                Destroy(pot);
                Physics2D.SyncTransforms();
                continue;
            }
            occupied.Add(point);
            placed++;
        }
        if (placed != desired)
            Debug.LogWarning("Placed " + placed + "/" + desired + " pots due to available clearance", this);
    }

    private void CreateFloor() { GameObject obj = new GameObject("Dungeon Floor"); obj.transform.SetParent(generatedRoomRoot, false); obj.AddComponent<RuntimeDungeonFloor>(); }

    private void CreateOuterWalls()
    {
        float w = roomHalfWidth * 2f, h = roomHalfHeight * 2f;
        CreateWall(new Vector2(0f, roomHalfHeight), new Vector2(w, boundaryThickness));
        CreateWall(new Vector2(0f, -roomHalfHeight), new Vector2(w, boundaryThickness), true);
        bool entryDoor = gameManager != null && gameManager.CurrentLevelIndex > 0 && gameManager.CurrentRoomIndex >= 2;
        if (entryDoor)
        {
            const float entryOpening = 1.74f;
            float entrySegmentHeight = (h - entryOpening) * 0.5f;
            float centerY = (entryOpening + entrySegmentHeight) * 0.5f;
            CreateWall(new Vector2(-roomHalfWidth, centerY), new Vector2(boundaryThickness, entrySegmentHeight), false, true);
            CreateWall(new Vector2(-roomHalfWidth, -centerY), new Vector2(boundaryThickness, entrySegmentHeight), false, true);
            CreateSealedEntranceDoor(new Vector2(-roomHalfWidth + 0.12f, 0f), entryOpening);
        }
        else
        {
            CreateWall(new Vector2(-roomHalfWidth, 0f), new Vector2(boundaryThickness, h), false, true);
        }

        bool useWallDoor = gameManager != null && gameManager.CurrentLevelIndex > 0 && gameManager.CurrentRoomIndex > 0 && gameManager.CurrentRoomIndex < 3;
        if (!useWallDoor)
        {
            CreateWall(new Vector2(roomHalfWidth, 0f), new Vector2(boundaryThickness, h));
            return;
        }

        const float doorOpeningHeight = 1.62f;
        float segmentHeight = (h - doorOpeningHeight) * 0.5f;
        float segmentCenter = doorOpeningHeight * 0.5f + segmentHeight * 0.5f;
        CreateWall(new Vector2(roomHalfWidth, segmentCenter), new Vector2(boundaryThickness, segmentHeight));
        CreateWall(new Vector2(roomHalfWidth, -segmentCenter), new Vector2(boundaryThickness, segmentHeight));

        GameObject doorObject = new GameObject("Locked Room Door");
        doorObject.transform.SetParent(generatedRoomRoot, false);
        currentRoomDoor = doorObject.AddComponent<RuntimeRoomDoor>();
        currentRoomDoor.Initialize(new Vector2(roomHalfWidth - 0.12f, 0f), new Vector2(0.52f, doorOpeningHeight));
        roomExitPosition = new Vector2(roomHalfWidth - 0.12f, 0f);
    }
    private void CreateWall(Vector2 position, Vector2 size, bool frontBottomBorder = false, bool mirrorLeftBorder = false)
    {
        GameObject obj = new GameObject(frontBottomBorder ? "Foreground Bottom Wall" : "Dungeon Wall");
        obj.transform.SetParent(generatedRoomRoot, false);
        obj.AddComponent<RuntimeDungeonWall>().Initialize(position, size, frontBottomBorder, mirrorLeftBorder);
    }
    private void CreateStone(Vector2 position, float diameter) { GameObject obj = new GameObject("Dungeon Stone"); obj.transform.SetParent(generatedRoomRoot, false); obj.AddComponent<RuntimeDungeonStone>().Initialize(position, diameter); }
    private void SpawnExitPortal(Vector2 position, bool hatchStyle) { GameObject obj = new GameObject(hatchStyle ? "Tutorial Hatch" : "Room Exit Portal"); obj.transform.SetParent(generatedRoomRoot, false); obj.AddComponent<RuntimeRoomExitPortal>().Initialize(position, hatchStyle); }

    private void CleanupCurrentRoom()
    {
        GameplayRoomReveal.CancelForSceneChange();
        roomNavigator = null;
        foreach (EnemyAI e in trackedEnemies) if (e != null) e.EnemyDied -= HandleRoomEnemyDied; trackedEnemies.Clear(); aliveRoomEnemies = 0; roomClearHandled = false; currentRoomDoor = null;
        if (generatedRoomRoot != null) { generatedRoomRoot.gameObject.SetActive(false); Destroy(generatedRoomRoot.gameObject); generatedRoomRoot = null; }
        DestroyTransientObjects<Projectile>(); DestroyTransientObjects<AngelSplinterProjectile>(); DestroyTransientObjects<BombProjectile>(); DestroyTransientObjects<TemporaryReflectionStone>(); DestroyTransientObjects<TornadoField>(); DestroyTransientObjects<RuntimeItemPickup>(); DestroyTransientObjects<HeartPickup>(); DestroyTransientObjects<LinearBossProjectile>(); DestroyTransientObjects<HomingBossProjectile>(); DestroyTransientObjects<ColossalEyeProjectile>(); DestroyTransientObjects<EnemyAI>(); DestroyTransientObjects<BossController>(); DestroyTransientObjects<LevelAdvanceHatch>();
    }
    private static void DestroyTransientObjects<T>() where T : Component { T[] objects = FindObjectsByType<T>(); for (int i = 0; i < objects.Length; i++) if (objects[i] != null) { objects[i].gameObject.SetActive(false); Destroy(objects[i].gameObject); } }
    private static void Shuffle<T>(IList<T> list) { for (int i = list.Count - 1; i > 0; i--) { int j = Random.Range(0, i + 1); T temp = list[i]; list[i] = list[j]; list[j] = temp; } }
}

[DisallowMultipleComponent]
internal sealed class RuntimeDungeonFloor : MonoBehaviour
{
    private void Awake()
    {
        SpriteRenderer renderer = gameObject.AddComponent<SpriteRenderer>();
        GameManager game = GameManager.Instance;
        int level = game == null ? 0 : game.CurrentLevelIndex;
        int room = game == null ? 0 : game.CurrentRoomIndex;
        Sprite authoredFloor = RuntimeGameArt.Load("RoomFloors/Room_" + level + "_" + room);
        if (authoredFloor == null)
        {
            Debug.LogError("[HuliganiGames] Missing final room background for " + level + "/" + room, this);
            renderer.enabled = false;
            return;
        }
        renderer.sprite = authoredFloor;
        renderer.color = Color.white;
        renderer.sortingOrder = -100;
        transform.localScale = new Vector3(15.02f / authoredFloor.bounds.size.x,
                                           8.02f / authoredFloor.bounds.size.y, 1f);
    }
}

[DisallowMultipleComponent]
internal sealed class RuntimeDungeonWall : MonoBehaviour
{
    private Texture2D texture; private Sprite sprite;
    private void Awake()
    {
        Rigidbody2D rb = gameObject.AddComponent<Rigidbody2D>(); rb.bodyType = RigidbodyType2D.Static; BoxCollider2D c = gameObject.AddComponent<BoxCollider2D>(); c.size = Vector2.one;
        SpriteRenderer r = gameObject.AddComponent<SpriteRenderer>();
        if (RuntimeGameArt.Load("Walls/Wall_01") != null)
        {
            r.enabled = false;
            return;
        }
        texture = GenerateWallTexture(32); sprite = Sprite.Create(texture, new Rect(0f, 0f, 32f, 32f), new Vector2(0.5f, 0.5f), 32f, 0, SpriteMeshType.FullRect); r.sprite = sprite; r.sortingOrder = 9;
    }
    public void Initialize(Vector2 position, Vector2 size, bool frontBottomBorder = false, bool mirrorLeftBorder = false)
    {
        transform.position = position;
        transform.localScale = new Vector3(Mathf.Max(0.05f, size.x), Mathf.Max(0.05f, size.y), 1f);
        bool horizontalSegment = size.x >= size.y;
        bool outerTopBorder = horizontalSegment && !frontBottomBorder &&
                              Mathf.Abs(position.y - 4.5f) < 0.8f;

        // The upper wall sprite extends DOWN into the visible room, but its
        // solid collision plane belongs behind the rim of the floor. V25's
        // 0.88 m upper hitbox blocked the player half a tile before the wall.
        // Move that collider OUTWARD while keeping the graphic unchanged.
        // Interior horizontal walls retain their 0.70 m collision width.
        const float topBoundaryHeight = 0.48f;
        const float bottomBoundaryHeight = 0.46f;
        const float interiorHorizontalHeight = 0.70f;
        const float verticalWallWidth = 0.65f;

        float collisionWorldWidth = horizontalSegment ? size.x : verticalWallWidth;
        float collisionWorldHeight = horizontalSegment
            ? (frontBottomBorder ? bottomBoundaryHeight
                : outerTopBorder ? topBoundaryHeight : interiorHorizontalHeight)
            : size.y;
        Vector2 collisionWorldOffset = frontBottomBorder ? new Vector2(0f, -0.075f)
            : outerTopBorder ? new Vector2(0f, 0.18f) : Vector2.zero;

        BoxCollider2D physicsBox = GetComponent<BoxCollider2D>();
        if (physicsBox != null)
        {
            physicsBox.size = new Vector2(collisionWorldWidth / Mathf.Max(0.05f, size.x),
                                          collisionWorldHeight / Mathf.Max(0.05f, size.y));
            physicsBox.offset = new Vector2(collisionWorldOffset.x / Mathf.Max(0.05f, size.x),
                                            collisionWorldOffset.y / Mathf.Max(0.05f, size.y));
        }

        SpriteRenderer fallbackRenderer = GetComponent<SpriteRenderer>();
        if (fallbackRenderer != null)
        {
            // Upper border stays behind every actor (enemies: 8+, bosses: 9,
            // player: 10+). Lower border remains in front of all actors.
            fallbackRenderer.sortingOrder = outerTopBorder ? -5 : (frontBottomBorder ? 60 : 9);
            fallbackRenderer.flipX = mirrorLeftBorder;
            fallbackRenderer.flipY = frontBottomBorder;
        }
        Sprite art = RuntimeGameArt.Load("Walls/Wall_01");
        if (art == null) return;
        bool horizontal = size.x >= size.y;
        float length = horizontal ? size.x : size.y;
        int pieces = Mathf.Max(1, Mathf.CeilToInt(length / 1.3f));
        float step = length / pieces;
        for (int i = 0; i < pieces; i++)
        {
            // Horizontal sequence only uses STRAIGHT sections of the approved atlas.
            // Wall_06 through Wall_10 are L-shaped corners, which previously
            // appeared erroneously near the right edge of top/bottom borders.
            int[] horizontalStraight = { 1, 2, 3, 4, 5, 1, 2, 3 };
            int tileIndex = horizontal ? horizontalStraight[i % horizontalStraight.Length] : 14 + (i % 8);
            Sprite segment = RuntimeGameArt.Load("Walls/Wall_" + tileIndex.ToString("00"));
            if (segment == null) segment = art;
            GameObject obj = new GameObject("Approved Stone Wall Segment " + i);
            obj.transform.SetParent(transform, false);
            float axis = (i + .5f) * step - length * .5f;
            obj.transform.localPosition = horizontal ? new Vector3(axis / size.x, 0f, 0f) : new Vector3(0f, axis / size.y, 0f);
            SpriteRenderer sr = obj.AddComponent<SpriteRenderer>();
            sr.sprite = segment;
            sr.color = new Color(0.76f, 0.73f, 0.78f, 1f);
            sr.sortingOrder = outerTopBorder ? -5 : (frontBottomBorder ? 60 : 9);
            // Mirror the visual sprite, never the world transform: wall hitboxes,
            // exit triggers and projectile ricochets must keep their coordinates.
            sr.flipX = mirrorLeftBorder;
            sr.flipY = frontBottomBorder;
            // Sprite footprint and collider use the same geometry. Extra 1.0%
            // along the tile axis hides subpixel seams without changing hitboxes.
            float worldWidth = horizontal ? step * 1.01f : 0.65f;
            float worldHeight = horizontal ? 0.70f : step * 1.01f;
            obj.transform.localScale = new Vector3(
                worldWidth / (Mathf.Max(0.05f, size.x) * segment.bounds.size.x),
                worldHeight / (Mathf.Max(0.05f, size.y) * segment.bounds.size.y), 1f);
        }
    }
    private static Texture2D GenerateWallTexture(int size)
    {
        Texture2D t = new Texture2D(size, size, TextureFormat.RGBA32, false) { name = "Runtime Dungeon Wall Texture", filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Repeat }; Color[] p = new Color[size * size];
        for (int y = 0; y < size; y++) for (int x = 0; x < size; x++) { bool mortar = y % 8 == 0 || (x % 12 == 0 && (y / 8) % 2 == 0) || ((x + 6) % 12 == 0 && (y / 8) % 2 == 1); float s = mortar ? 0.075f : 0.19f + Mathf.Sin(x * 1.13f + y * 0.83f) * 0.025f; p[y * size + x] = new Color(s, s, s * 1.03f, 1f); }
        t.SetPixels(p); t.Apply(false, false); return t;
    }
    private void OnDestroy() { if (sprite != null) Destroy(sprite); if (texture != null) Destroy(texture); }
}

[DisallowMultipleComponent]
internal sealed class RuntimeDungeonStone : MonoBehaviour
{
    private Texture2D texture; private Sprite sprite;
    private void Awake()
    {
        Rigidbody2D rb = gameObject.AddComponent<Rigidbody2D>(); rb.bodyType = RigidbodyType2D.Static; CircleCollider2D c = gameObject.AddComponent<CircleCollider2D>(); c.radius = 0.5f;
        SpriteRenderer r = gameObject.AddComponent<SpriteRenderer>(); r.sortingOrder = 9;
        Sprite stoneArt = RuntimeGameArt.Load("Props/Stone");
        if (stoneArt != null)
        {
            r.sprite = stoneArt;
            return;
        }
        texture = ItemRuntimeVisuals.CreateStoneTexture(64); sprite = Sprite.Create(texture, new Rect(0f, 0f, 64f, 64f), new Vector2(0.5f, 0.5f), 64f, 0, SpriteMeshType.FullRect); r.sprite = sprite;
    }
    public void Initialize(Vector2 position, float diameter)
    {
        transform.position = position;
        float s = Mathf.Max(0.15f, diameter);
        transform.localScale = new Vector3(s, s, 1f);
        SpriteRenderer sr = GetComponent<SpriteRenderer>();
        CircleCollider2D hitbox = GetComponent<CircleCollider2D>();
        if (sr != null && sr.sprite != null && hitbox != null)
        {
            // Use the visible stone base, not its tall moss silhouette.
            hitbox.radius = Mathf.Min(0.56f, sr.sprite.bounds.size.x * 0.46f);
            hitbox.offset = new Vector2(0f, -0.07f);
        }
    }
    private void OnDestroy() { if (sprite != null) Destroy(sprite); if (texture != null) Destroy(texture); }
}

[DisallowMultipleComponent]
internal sealed class RuntimeRoomDoor : MonoBehaviour
{
    private BoxCollider2D doorCollider;
    private SpriteRenderer spriteRenderer;
    private Texture2D lockedTexture;
    private Texture2D unlockedTexture;
    private Sprite lockedSprite;
    private Sprite unlockedSprite;
    private bool unlocked;
    private bool consumed;
    private float unlockedAt;

    private void Awake()
    {
        Rigidbody2D body = gameObject.AddComponent<Rigidbody2D>();
        body.bodyType = RigidbodyType2D.Static;
        body.gravityScale = 0f;

        doorCollider = gameObject.AddComponent<BoxCollider2D>();
        doorCollider.isTrigger = false;
        doorCollider.size = new Vector2(1f, 2f);

        spriteRenderer = gameObject.AddComponent<SpriteRenderer>();
        spriteRenderer.sortingOrder = 7;

        Sprite importedClosed = RuntimeGameArt.Load("Doors/Door_Closed");
        Sprite importedOpen = RuntimeGameArt.Load("Doors/Door_Open");
        if (importedClosed != null && importedOpen != null)
        {
            lockedSprite = importedClosed;
            unlockedSprite = importedOpen;
            // The collider remains on the original object. A separate child scales
            // the approved arched-door artwork to fill the wall opening.
            spriteRenderer.enabled = false;
            GameObject art = new GameObject("Approved Locked Wall Door");
            art.transform.SetParent(transform, false);
            art.transform.localPosition = Vector3.zero;
            spriteRenderer = art.AddComponent<SpriteRenderer>();
            spriteRenderer.sprite = lockedSprite;
            spriteRenderer.sortingOrder = 7;
            art.transform.localScale = new Vector3(1.18f / (0.52f * lockedSprite.bounds.size.x),
                                                    1.65f / (0.81f * lockedSprite.bounds.size.y), 1f);
            return;
        }
        lockedTexture = CreateDoorTexture(64, 128, false);
        unlockedTexture = CreateDoorTexture(64, 128, true);
        lockedSprite = Sprite.Create(lockedTexture, new Rect(0f, 0f, 64f, 128f), new Vector2(0.5f, 0.5f), 64f, 0, SpriteMeshType.FullRect);
        unlockedSprite = Sprite.Create(unlockedTexture, new Rect(0f, 0f, 64f, 128f), new Vector2(0.5f, 0.5f), 64f, 0, SpriteMeshType.FullRect);
        spriteRenderer.sprite = lockedSprite;
    }

    public void Initialize(Vector2 position, Vector2 size)
    {
        transform.position = position;
        transform.localScale = new Vector3(Mathf.Max(0.1f, size.x), Mathf.Max(0.1f, size.y * 0.5f), 1f);
    }

    public void FlipArtworkHorizontally()
    {
        // A left-wall entrance faces the opposite way from the right exit.
        // Only flip the visual: never flip or shift the collider.
        if (spriteRenderer != null) spriteRenderer.flipX = true;
    }

    public void Unlock()
    {
        if (unlocked) return;
        unlocked = true;
        unlockedAt = Time.time;
        spriteRenderer.sprite = unlockedSprite;
        doorCollider.isTrigger = true;
        RuntimeVfx.SpawnCircularBurst(transform.position, new Color(0.70f, 0.62f, 0.38f, 1f), 16, 0.10f, 2.5f, 0.35f);
    }

    private void Update()
    {
        if (!unlocked || consumed || GameplayRoomReveal.IsRevealing ||
            Time.time < unlockedAt + 0.12f)
            return;

        // Switching a solid collider to a trigger does not reliably generate
        // OnTriggerEnter2D if the player was already touching the closed door.
        // Poll the opening so the character cannot simply leave the map.
        PlayerController player = FindAnyObjectByType<PlayerController>();
        TryEnterOpenedDoor(player);
    }

    private void OnTriggerEnter2D(Collider2D other) => TryEnterOpenedDoor(other.GetComponentInParent<PlayerController>());
    private void OnTriggerStay2D(Collider2D other) => TryEnterOpenedDoor(other.GetComponentInParent<PlayerController>());

    private void TryEnterOpenedDoor(PlayerController player)
    {
        if (!unlocked || consumed || player == null || player.IsDead ||
            GameplayRoomReveal.IsRevealing || Time.time < unlockedAt + 0.12f)
            return;

        GameManager manager = GameManager.Instance;
        if (manager == null || manager.CurrentState != GameManager.GameState.GAMEPLAY ||
            manager.IsGameplayPaused)
            return;

        Vector2 playerPosition = player.transform.position;
        Vector2 doorway = transform.position;
        // The actor can approach the open doorway while already overlapping it;
        // allow a small approach band on the room side for robust transitions.
        if (playerPosition.x < doorway.x - 0.82f ||
            Mathf.Abs(playerPosition.y - doorway.y) > 0.90f)
            return;

        consumed = true;
        doorCollider.enabled = false;
        manager.AdvanceRoom();
    }

    private static Texture2D CreateDoorTexture(int width, int height, bool open)
    {
        Texture2D texture = new Texture2D(width, height, TextureFormat.RGBA32, false);
        texture.name = open ? "Runtime Unlocked Passage" : "Runtime Door Chains and Padlock";
        texture.filterMode = FilterMode.Point;
        Color[] pixels = new Color[width * height];
        Color iron = new Color(0.31f, 0.32f, 0.33f, 1f);
        Color highlight = new Color(0.65f, 0.65f, 0.62f, 1f);
        Color shadow = new Color(0.06f, 0.055f, 0.05f, 1f);
        Color wood = new Color(0.22f, 0.145f, 0.09f, 1f);

        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
        {
            bool frame = x < 6 || x >= width - 6 || y < 5 || y >= height - 5;
            bool boards = x % 13 < 2;
            bool ironBrace = (y >= 20 && y <= 26) || (y >= height - 27 && y <= height - 21);
            if (open)
            {
                pixels[y * width + x] = frame ? iron : new Color(0.013f, 0.011f, 0.012f, 1f);
                if (!frame && (x == 7 || x == width - 8)) pixels[y * width + x] = highlight;
            }
            else
            {
                Color c = frame ? iron : wood;
                if (boards) c *= 0.58f;
                if (ironBrace) c = iron;
                if (frame && (x == 4 || x == width - 5)) c = highlight;
                pixels[y * width + x] = c;
            }
        }

        if (!open)
        {
            // Two diagonal silver chains crossing over the ENTIRE closed door.
            // They are deliberately silver, not a red X painted over a void.
            for (int y = 11; y < height - 11; y += 9)
            {
                int xA = 7 + Mathf.RoundToInt((width - 14f) * (y - 11f) / (height - 22f));
                int xB = width - 1 - xA;
                DrawChainLink(pixels, width, height, xA, y, iron, highlight, shadow);
                DrawChainLink(pixels, width, height, xB, y, iron, highlight, shadow);
            }

            // A centered brass padlock covering the chain intersection.
            int cx = width / 2, cy = height / 2;
            Color brass = new Color(0.70f, 0.55f, 0.21f, 1f);
            Color lightBrass = new Color(0.93f, 0.81f, 0.40f, 1f);
            for (int y = cy - 14; y <= cy + 15; y++)
            for (int x = cx - 13; x <= cx + 13; x++)
            {
                int dx = x - cx, dy = y - cy;
                bool body = Mathf.Abs(dx) <= 12 && dy >= -11 && dy <= 8;
                bool shackle = Mathf.Abs(dx) >= 6 && Mathf.Abs(dx) <= 10 && dy >= 7 && dy <= 15;
                bool shackleTop = dy >= 13 && dy <= 16 && Mathf.Abs(dx) <= 10;
                if (body || shackle || shackleTop)
                {
                    Color color = body ? brass : iron;
                    if (body && (Mathf.Abs(dx) >= 11 || dy == -11)) color = lightBrass;
                    SetDoorPixel(pixels, width, height, x, y, color);
                }
            }
            // Keyhole with a diamond-shaped bottom.
            for (int y = cy - 6; y <= cy + 2; y++)
            for (int x = cx - 3; x <= cx + 3; x++)
            {
                if ((x - cx) * (x - cx) + (y - (cy + 1)) * (y - (cy + 1)) <= 5 ||
                    (Mathf.Abs(x - cx) <= 1 && y < cy + 1))
                    SetDoorPixel(pixels, width, height, x, y, shadow);
            }
        }
        texture.SetPixels(pixels);
        texture.Apply(false, false);
        return texture;
    }

    private static void DrawChainLink(Color[] pixels, int width, int height, int centerX, int centerY,
        Color iron, Color highlight, Color shadow)
    {
        for (int y = centerY - 5; y <= centerY + 5; y++)
        for (int x = centerX - 4; x <= centerX + 4; x++)
        {
            float nx = (x - centerX) / 4f;
            float ny = (y - centerY) / 5f;
            float value = nx * nx + ny * ny;
            if (value <= 1.20f && value >= 0.38f)
                SetDoorPixel(pixels, width, height, x, y, (x + y) % 3 == 0 ? highlight : iron);
            else if (value < 0.38f && ((x + y) % 7 == 0))
                SetDoorPixel(pixels, width, height, x, y, shadow);
        }
    }

    private static void SetDoorPixel(Color[] pixels, int width, int height, int x, int y, Color color)
    {
        if (x < 0 || x >= width || y < 0 || y >= height) return;
        pixels[y * width + x] = color;
    }

    private void OnDestroy()
    {
        // Never destroy sprites owned by Unity's asset importer.
        if (lockedTexture != null && lockedSprite != null) Destroy(lockedSprite);
        if (unlockedTexture != null && unlockedSprite != null) Destroy(unlockedSprite);
        if (lockedTexture != null) Destroy(lockedTexture);
        if (unlockedTexture != null) Destroy(unlockedTexture);
    }
}

[DisallowMultipleComponent]
internal sealed class RuntimeSealedEntryHatch : MonoBehaviour
{
    private Texture2D texture;
    private Sprite sprite;
    private void Awake()
    {
        SpriteRenderer renderer = gameObject.AddComponent<SpriteRenderer>();
        renderer.sortingOrder = 5;
        Sprite hatchArt = RuntimeGameArt.Load("Hatches/Hatch_Closed");
        if (hatchArt != null)
        {
            renderer.sprite = hatchArt;
            return;
        }
        texture = new Texture2D(80, 80, TextureFormat.RGBA32, false)
        {
            name = "Sealed Entrance Hatch — Steel Bars and Lock",
            filterMode = FilterMode.Point,
            wrapMode = TextureWrapMode.Clamp
        };
        Color[] pixels = new Color[80 * 80];
        float center = 39.5f;
        for (int y = 0; y < 80; y++)
        for (int x = 0; x < 80; x++)
        {
            float dx = x - center, dy = y - center;
            float radius = Mathf.Sqrt(dx * dx + dy * dy);
            Color color = Color.clear;
            if (radius < 34f)
            {
                color = radius > 27f ? new Color(0.39f, 0.34f, 0.29f, 1f)
                    : new Color(0.085f, 0.060f, 0.058f, 1f);
                if (Mathf.Abs(dx) < 3f || Mathf.Abs(dy) < 3f ||
                    Mathf.Abs(dx - dy) < 2f || Mathf.Abs(dx + dy) < 2f)
                    color = new Color(0.35f, 0.34f, 0.35f, 1f);
                if (Mathf.Abs(dx) < 6f && Mathf.Abs(dy) < 7f)
                    color = new Color(0.66f, 0.52f, 0.24f, 1f);
                if (Mathf.Abs(dx) < 2f && dy < -1f && dy > -6f)
                    color = new Color(0.06f, 0.05f, 0.04f, 1f);
            }
            pixels[y * 80 + x] = color;
        }
        texture.SetPixels(pixels);
        texture.Apply(false, false);
        sprite = Sprite.Create(texture, new Rect(0f, 0f, 80f, 80f),
            new Vector2(0.5f, 0.5f), 64f, 0, SpriteMeshType.FullRect);
        renderer.sprite = sprite;
    }
    public void Initialize(Vector2 position)
    {
        transform.position = new Vector3(position.x, position.y, 0f);
        transform.localScale = Vector3.one * 1.15f;
    }
    private void OnDestroy()
    {
        if (texture != null && sprite != null) Destroy(sprite);
        if (texture != null) Destroy(texture);
    }
}

[DisallowMultipleComponent]
internal sealed class RuntimeRoomExitPortal : MonoBehaviour
{
    private SpriteRenderer renderer; private CircleCollider2D trigger; private Texture2D texture; private Sprite sprite; private bool hatchStyle, consumed;
    private void Awake() { renderer = gameObject.AddComponent<SpriteRenderer>(); renderer.sortingOrder = 6; trigger = gameObject.AddComponent<CircleCollider2D>(); trigger.radius = 0.55f; trigger.isTrigger = true; }
    public void Initialize(Vector2 position, bool hatch)
    {
        transform.position = position;
        hatchStyle = hatch;
        Sprite importedHatch = hatch ? RuntimeGameArt.Load("Hatches/Hatch_Open") : null;
        if (importedHatch != null)
        {
            renderer.sprite = importedHatch;
            return;
        }
        texture = GeneratePortalTexture(80, hatch);
        sprite = Sprite.Create(texture, new Rect(0f, 0f, 80f, 80f), new Vector2(0.5f, 0.5f), 64f, 0, SpriteMeshType.FullRect);
        renderer.sprite = sprite;
    }
    private void Update() { if (consumed) return; if (!hatchStyle) transform.Rotate(0f, 0f, 38f * Time.deltaTime); transform.localScale = Vector3.one * (1f + Mathf.Sin(Time.time * (hatchStyle ? 2.4f : 4.2f)) * (hatchStyle ? 0.025f : 0.055f)); }
    private void OnTriggerEnter2D(Collider2D other) { if (consumed) return; PlayerController p = other.GetComponentInParent<PlayerController>(); if (p == null || p.IsDead) return; consumed = true; trigger.enabled = false; RuntimeVfx.SpawnCircularBurst(transform.position, new Color(0.58f, 0.025f, 0.08f, 1f), 28, 0.14f, 4.4f, 0.58f); if (GameManager.Instance != null) GameManager.Instance.AdvanceRoom(); }
    private static Texture2D GeneratePortalTexture(int size, bool hatch)
    {
        Texture2D t = new Texture2D(size, size, TextureFormat.RGBA32, false) { name = hatch ? "Runtime Hatch Texture" : "Runtime Portal Texture", filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp }; Color[] p = new Color[size * size]; float c = (size - 1) * 0.5f, r = size * 0.44f;
        for (int y = 0; y < size; y++) for (int x = 0; x < size; x++) { float dx = x - c, dy = y - c, d = Mathf.Sqrt(dx * dx + dy * dy); int i = y * size + x; if (d > r) p[i] = Color.clear; else { float n = d / r; if (hatch) p[i] = n > 0.73f ? new Color(0.28f, 0.25f, 0.21f, 1f) : new Color(0.025f, 0.012f, 0.015f, 1f); else { float ring = Mathf.Abs(Mathf.Sin(n * Mathf.PI * 5f)); p[i] = new Color(0.35f + ring * 0.33f, 0.01f, 0.055f + ring * 0.07f, Mathf.Lerp(0.92f, 0.20f, n)); } } }
        t.SetPixels(p); t.Apply(false, false); return t;
    }
    private void OnDestroy() { if (sprite != null) Destroy(sprite); if (texture != null) Destroy(texture); }
}

// Pot is solid cover for players, enemies and projectiles; incoming daggers
// destroy it instead of ricocheting. Half of broken pots produce one heart.
[DisallowMultipleComponent]
internal sealed class RuntimeBreakablePot : MonoBehaviour, ICombatDamageable
{
    private bool broken;
    private void Awake()
    {
        Sprite art = RuntimeGameArt.Load("Props/ClayPot");
        SpriteRenderer r = gameObject.AddComponent<SpriteRenderer>();
        r.sprite = art;
        r.sortingOrder = 6;
        if (art != null)
        {
            float width = 0.704f; // 20% smaller than v31
            transform.localScale = Vector3.one * (width / Mathf.Max(0.01f, art.bounds.size.x));
        }
        Rigidbody2D rb = gameObject.AddComponent<Rigidbody2D>();
        rb.bodyType = RigidbodyType2D.Static;
        rb.gravityScale = 0f;
        CircleCollider2D c = gameObject.AddComponent<CircleCollider2D>();
        float s = Mathf.Max(0.01f, transform.lossyScale.x);
        c.radius = 0.304f / s;
        c.offset = new Vector2(0f, -0.048f / s);
        c.isTrigger = false;
    }
    public bool ReceiveDamage(float damage, GameObject source)
    {
        if (broken || damage <= 0f || source == null) return false;
        // Only actual player dagger projectiles (including angel splinters)
        // shatter pots; enemies bump into them without breaking them.
        if (source.GetComponent<Projectile>() == null &&
            source.GetComponent<AngelSplinterProjectile>() == null) return false;
        broken = true;
        Vector3 p = transform.position;
        if (Random.value < 0.50f)
        {
            GameObject heart = new GameObject("Heart from smashed clay pot");
            heart.transform.position = p;
            heart.AddComponent<HeartPickup>();
        }
        RuntimeVfx.SpawnCircularBurst(p, new Color(0.66f, 0.41f, 0.25f, 1f),
            13, 0.09f, 2.1f, 0.38f);
        Collider2D solid = GetComponent<Collider2D>();
        if (solid != null) solid.enabled = false;
        if (LevelRoomManager.Instance != null)
            LevelRoomManager.Instance.RequestEnemyNavigationRefresh();
        Destroy(gameObject);
        return true;
    }
}
