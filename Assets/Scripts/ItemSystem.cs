using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

[DisallowMultipleComponent]
public sealed class ItemSystem : MonoBehaviour
{
    public enum ItemCategory { Active, Passive }

    public sealed class ItemDefinition
    {
        public string Id { get; }
        public string DisplayName { get; }
        public string Description { get; }
        public ItemCategory Category { get; }
        public int RequiredKills { get; }
        public ItemDefinition(string id, string displayName, string description, ItemCategory category, int requiredKills = 0)
        {
            Id = id; DisplayName = displayName;
            Description = description;
            Category = category;
            RequiredKills = category == ItemCategory.Active ? Mathf.Max(1, requiredKills) : 0;
        }
    }

    public static class ItemIds
    {
        public const string ObstacleStone = "active.obstacle_stone";
        public const string Bomb = "active.bomb";
        public const string Pushback = "active.pushback";
        public const string Dash = "active.dash";
        public const string ReversePolarity = "active.reverse_polarity";
        public const string AngelLens = "passive.angel_lens";
        public const string Resurrection = "passive.resurrection";
        public const string Snack = "passive.snack";
        public const string Backpack = "passive.backpack";
        public const string GlassCannon = "passive.glass_cannon";
    }

    public static ItemSystem Instance { get; private set; }

    [Header("Obstacle-Stone")]
    [SerializeField] private float temporaryStoneLifetime = 12f;
    [SerializeField] private float stoneGridSize = 0.5f;
    [Header("Bomb")]
    [SerializeField] private float bombSpeed = 6.7f;
    [SerializeField] private float bombFuseTime = 2.75f;
    [SerializeField] private float bombExplosionRadius = 2.15f;
    [SerializeField] private float bombDamage = 8.5f;
    [Header("Pushback")]
    [SerializeField] private float pushbackRadius = 3.2f;
    [SerializeField] private float pushbackImpulse = 25.5f;
    [Header("Dash")]
    [SerializeField] private float dashSpeed = 13f;
    [SerializeField] private float dashDuration = 0.16f;
    [Header("Tornado / Торнадо")]
    [SerializeField] private float polarityDuration = 4.5f;
    [SerializeField] private float polarityRadius = 7.5f;
    [SerializeField] private float polarityPullForce = 15f;
    [Header("Angel Lens")]
    [SerializeField] private float angelSplinterSpeed = 13f;
    [SerializeField] private float angelSplinterLifetime = 1.65f;
    [SerializeField] private float angelSplinterDamage = 0.25f;

    private readonly Dictionary<string, ItemDefinition> definitions = new Dictionary<string, ItemDefinition>(StringComparer.Ordinal);
    private readonly HashSet<string> ownedPassives = new HashSet<string>(StringComparer.Ordinal);
    private PlayerController player;
    private GameManager boundGameManager;
    private bool resurrectionAvailable;
    private bool pendingRunReset;

    public PlayerController Player => player;
    public event Action<ItemDefinition> ActiveItemCollected;
    public event Action<ItemDefinition> PassiveItemCollected;
    public event Action<ItemDefinition> PassiveItemConsumed;

    private void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(gameObject); return; }
        Instance = this; DontDestroyOnLoad(gameObject); BuildDefinitions();
    }
    private void OnEnable() => BossController.BossRewardRequested += HandleBossRewardRequested;
    private void Start() { TryBindGameManager(); TryBindPlayer(); }
    private void Update() { if (boundGameManager == null) TryBindGameManager(); if (player == null) TryBindPlayer(); }
    private void OnDisable() => BossController.BossRewardRequested -= HandleBossRewardRequested;
    private void OnDestroy() { if (Instance == this) Instance = null; UnbindPlayer(); UnbindGameManager(); }

    private void BuildDefinitions()
    {
        definitions.Clear();
        RegisterDefinition(new ItemDefinition(ItemIds.Bomb, "Бомба", "Тяжёлая прыгающая бомба. Взрывается при ударе о врага или по окончании запала.", ItemCategory.Active, 5));
        RegisterDefinition(new ItemDefinition(ItemIds.Pushback, "Отталкивание", "Мощная ударная волна отбрасывает ближайших врагов.", ItemCategory.Active, 2));
        RegisterDefinition(new ItemDefinition(ItemIds.Dash, "Рывок", "Быстрое перемещение в выбранную сторону. Полный заряд даёт два рывка.", ItemCategory.Active, 1));
        RegisterDefinition(new ItemDefinition(ItemIds.ReversePolarity, "Торнадо", "Создаёт большой вращающийся вихрь возле курсора. Притягивает и закручивает врагов в радиусе 7,5.", ItemCategory.Active, 4));
        RegisterDefinition(new ItemDefinition(ItemIds.AngelLens, "Ангельская линза", "После первого рикошета кинжал распадается на 5 маленьких по 0,25 урона. Рикошетные кинжалы Ангельской линзы НЕ наносят урон герою.", ItemCategory.Passive));
        RegisterDefinition(new ItemDefinition(ItemIds.Resurrection, "Воскрешение", "Одноразовая реинкарнация. При смертельном ударе автоматически спасает героя: оставляет 1 сердце и даёт короткую неуязвимость. После срабатывания исчезает.", ItemCategory.Passive));
        RegisterDefinition(new ItemDefinition(ItemIds.Snack, "Плотный перекус", "Добавляет одно максимальное сердце и +1 к урону кинжалов.", ItemCategory.Passive));
        RegisterDefinition(new ItemDefinition(ItemIds.Backpack, "Рюкзак", "Позволяет носить два активных предмета. Q переключает их. При получении урона запасной теряет заряд.", ItemCategory.Passive));
        RegisterDefinition(new ItemDefinition(ItemIds.GlassCannon, "Стеклянная пушка", "Урон кинжалов +2, но весь получаемый урон увеличивается в 2 раза.", ItemCategory.Passive));
    }

    private void RegisterDefinition(ItemDefinition d) { if (d != null && !string.IsNullOrWhiteSpace(d.Id)) definitions[d.Id] = d; }
    public ItemDefinition GetDefinition(string itemId) { if (string.IsNullOrWhiteSpace(itemId)) return null; definitions.TryGetValue(itemId, out ItemDefinition d); return d; }
    public bool HasPassive(string itemId) => !string.IsNullOrWhiteSpace(itemId) && ownedPassives.Contains(itemId);
    public List<ItemDefinition> GetOwnedPassiveDefinitions()
    {
        List<ItemDefinition> result = new List<ItemDefinition>();
        foreach (string id in ownedPassives) { ItemDefinition d = GetDefinition(id); if (d != null) result.Add(d); }
        result.Sort((a, b) => string.Compare(a.DisplayName, b.DisplayName, StringComparison.OrdinalIgnoreCase));
        return result;
    }

    private void TryBindGameManager()
    {
        if (boundGameManager != null || GameManager.Instance == null) return;
        boundGameManager = GameManager.Instance;
        boundGameManager.StateChanged += HandleGameStateChanged;
        boundGameManager.RunEnded += HandleRunEnded;
        if (boundGameManager.CurrentState == GameManager.GameState.MAIN_MENU && !boundGameManager.RunActive) pendingRunReset = true;
    }
    private void UnbindGameManager()
    {
        if (boundGameManager == null) return;
        boundGameManager.StateChanged -= HandleGameStateChanged; boundGameManager.RunEnded -= HandleRunEnded; boundGameManager = null;
    }
    private void HandleRunEnded(GameManager.RunEndReason reason, int score, int kills) => pendingRunReset = true;
    private void HandleGameStateChanged(GameManager.GameState state)
    {
        if (boundGameManager == null) return;
        if (state == GameManager.GameState.MAIN_MENU && !boundGameManager.RunActive) { pendingRunReset = true; return; }
        if (state == GameManager.GameState.GAMEPLAY && pendingRunReset) { ResetForNewRun(); pendingRunReset = false; }
    }
    public void ResetForNewRun() { ownedPassives.Clear(); resurrectionAvailable = false; UnbindPlayer(); TryBindPlayer(); }

    private void TryBindPlayer()
    {
        PlayerController found = FindAnyObjectByType<PlayerController>(); if (found != null) BindPlayer(found);
    }
    public void EnsurePlayerBinding(PlayerController newPlayer)
    {
        if (newPlayer == null) return;
        if (player != newPlayer)
        {
            BindPlayer(newPlayer);
            return;
        }

        // The same player may survive room changes while this persistent manager
        // was rebound. Re-registering is safe because PlayerController stores
        // behaviours by item id.
        RegisterActiveItemBehaviours();
    }

    private void BindPlayer(PlayerController newPlayer)
    {
        if (newPlayer == null) return;
        if (player == newPlayer)
        {
            RegisterActiveItemBehaviours();
            return;
        }
        UnbindPlayer();
        player = newPlayer;
        player.ProjectileFirstRicochet += HandleProjectileFirstRicochet;
        RegisterActiveItemBehaviours();
        ApplyExistingPassivesToPlayer();
    }
    private void UnbindPlayer()
    {
        if (player == null) return;
        player.ProjectileFirstRicochet -= HandleProjectileFirstRicochet; player.UnregisterDeathInterceptor(HandleResurrection); UnregisterActiveItemBehaviours(); player = null;
    }
    private void RegisterActiveItemBehaviours()
    {
        if (player == null) return;
        // Obstacle Stone has been retired; no player can equip or use it.
        player.RegisterActiveItemBehaviour(ItemIds.Bomb, UseBomb);
        player.RegisterActiveItemBehaviour(ItemIds.Pushback, UsePushback);
        player.RegisterActiveItemBehaviour(ItemIds.Dash, UseDash);
        player.RegisterActiveItemBehaviour(ItemIds.ReversePolarity, UseReversePolarity);
    }
    private void UnregisterActiveItemBehaviours()
    {
        if (player == null) return;
        player.UnregisterActiveItemBehaviour(ItemIds.ObstacleStone); player.UnregisterActiveItemBehaviour(ItemIds.Bomb); player.UnregisterActiveItemBehaviour(ItemIds.Pushback); player.UnregisterActiveItemBehaviour(ItemIds.Dash); player.UnregisterActiveItemBehaviour(ItemIds.ReversePolarity);
    }

    public bool TryCollect(
        PlayerController collector,
        ItemDefinition definition,
        Vector3 pickupPosition,
        int activeStoredKills = -1,
        int activeUsesRemaining = -1)
    {
        if (collector == null || definition == null)
            return false;

        EnsurePlayerBinding(collector);

        if (definition.Category == ItemCategory.Active)
        {
            int startingCharge = activeStoredKills < 0
                ? definition.RequiredKills
                : Mathf.Clamp(activeStoredKills, 0, definition.RequiredKills);

            if (!collector.EquipOrSwapActiveItem(
                    definition.Id,
                    definition.DisplayName,
                    definition.RequiredKills,
                    startingCharge,
                    activeUsesRemaining,
                    out PlayerController.ActiveItemSnapshot dropped))
            {
                return false;
            }

            ActiveItemCollected?.Invoke(definition);

            if (dropped.IsValid)
            {
                // Swap happens exactly where the picked-up item was lying.
                SpawnWorldItem(dropped.ItemId, pickupPosition, dropped.StoredKills, dropped.UsesRemaining, true);
            }

            return true;
        }

        return AcquirePassive(definition);
    }

    private bool AcquirePassive(ItemDefinition definition)
    {
        if (definition == null || definition.Category != ItemCategory.Passive || !ownedPassives.Add(definition.Id)) return false;
        ApplyPassiveToPlayer(definition.Id); PassiveItemCollected?.Invoke(definition); return true;
    }
    private void ApplyExistingPassivesToPlayer() { foreach (string id in ownedPassives) ApplyPassiveToPlayer(id); }
    private void ApplyPassiveToPlayer(string id)
    {
        if (player == null) return;
        switch (id)
        {
            case ItemIds.Resurrection: resurrectionAvailable = true; player.RegisterDeathInterceptor(HandleResurrection); break;
            case ItemIds.Snack: player.IncreaseMaxHealth(1f, true); player.ModifyProjectileDamage(1f); break;
            case ItemIds.Backpack: player.SetActiveItemCapacity(2); break;
            case ItemIds.GlassCannon: player.ModifyProjectileDamage(2f); player.SetIncomingDamageMultiplier(2f); break;
        }
    }

    private void HandleProjectileFirstRicochet(Projectile projectile, Vector2 reflectedDirection)
    {
        if (!ownedPassives.Contains(ItemIds.AngelLens) || projectile == null)
            return;

        Vector2 center = reflectedDirection.sqrMagnitude > 0.0001f
            ? reflectedDirection.normalized
            : Vector2.right;

        const int count = 5;
        const float totalArc = 86f;
        float start = -totalArc * 0.5f;
        float interval = totalArc / (count - 1);

        for (int i = 0; i < count; i++)
        {
            Vector2 dir = RotateVector(center, start + interval * i);
            Vector2 pos = (Vector2)projectile.transform.position + center * 0.20f;

            GameObject obj = new GameObject("Angel Lens Splinter");
            obj.AddComponent<AngelSplinterProjectile>().Initialize(
                player,
                pos,
                dir,
                angelSplinterSpeed,
                Mathf.Max(0.01f, angelSplinterDamage),
                angelSplinterLifetime);
        }

        // Angel Lens replaces the reflected main shot rather than adding free damage
        // on top of it.
        projectile.ConsumeAfterRicochet();
    }

    private bool HandleResurrection(PlayerController targetPlayer)
    {
        if (!resurrectionAvailable || !ownedPassives.Contains(ItemIds.Resurrection)) return false;
        resurrectionAvailable = false; ownedPassives.Remove(ItemIds.Resurrection); if (targetPlayer != null) targetPlayer.UnregisterDeathInterceptor(HandleResurrection);
        RuntimeVfx.SpawnCircularBurst(targetPlayer != null ? targetPlayer.transform.position : Vector3.zero, new Color(0.92f, 0.82f, 0.45f, 1f), 36, 0.17f, 4.5f, 0.85f);
        PassiveItemConsumed?.Invoke(GetDefinition(ItemIds.Resurrection)); return true;
    }

    private bool UseObstacleStone(PlayerController user)
    {
        Vector2 p = GetMouseWorldPosition(); float grid = Mathf.Max(0.1f, stoneGridSize); p.x = Mathf.Round(p.x / grid) * grid; p.y = Mathf.Round(p.y / grid) * grid;
        Vector2 playerPos = user.transform.position;
        if (Vector2.Distance(p, playerPos) < 0.85f)
        {
            Vector2 dir = p - playerPos; if (dir.sqrMagnitude < 0.001f) dir = user.AimDirection; if (dir.sqrMagnitude < 0.001f) dir = Vector2.right; p = playerPos + dir.normalized * 0.95f;
        }
        GameObject obj = new GameObject("Active Item Reflection Stone"); obj.transform.position = p; obj.AddComponent<TemporaryReflectionStone>().Initialize(temporaryStoneLifetime); return true;
    }

    private bool UseBomb(PlayerController user)
    {
        Vector2 dir = GetMouseWorldPosition() - (Vector2)user.transform.position; if (dir.sqrMagnitude < 0.001f) dir = user.AimDirection; if (dir.sqrMagnitude < 0.001f) dir = Vector2.right; dir.Normalize();
        GameObject obj = new GameObject("Active Item Bomb"); obj.AddComponent<BombProjectile>().Initialize(user, (Vector2)user.transform.position + dir * 0.72f, dir, bombSpeed, bombFuseTime, bombExplosionRadius, bombDamage); return true;
    }

    private bool UsePushback(PlayerController user)
    {
        Vector2 origin = user.transform.position; Collider2D[] colliders = Physics2D.OverlapCircleAll(origin, pushbackRadius); HashSet<Rigidbody2D> affected = new HashSet<Rigidbody2D>();
        for (int i = 0; i < colliders.Length; i++)
        {
            Collider2D hit = colliders[i]; if (hit == null) continue;
            EnemyAI enemy = hit.GetComponentInParent<EnemyAI>(); BossController boss = hit.GetComponentInParent<BossController>(); if (enemy == null && boss == null) continue;
            Rigidbody2D rb = hit.attachedRigidbody; if (rb == null || !affected.Add(rb)) continue;
            Vector2 away = rb.position - origin; if (away.sqrMagnitude < 0.001f) { away = UnityEngine.Random.insideUnitCircle; if (away.sqrMagnitude < 0.001f) away = Vector2.right; } away.Normalize();
            float distanceFactor = 1f - Mathf.Clamp01(Vector2.Distance(rb.position, origin) / Mathf.Max(0.1f, pushbackRadius));
            float strength = pushbackImpulse * Mathf.Lerp(0.40f, 1f, distanceFactor);
            if (enemy != null) enemy.ApplyExternalImpulse(away * strength); else boss.ApplyExternalImpulse(away * strength);
        }
        RuntimeVfx.SpawnCircularBurst(origin, new Color(0.62f, 0.64f, 0.69f, 1f), 28, 0.13f, 5.2f, 0.52f); return true;
    }

    private bool UseDash(PlayerController user)
    {
        Vector2 dir = user.MovementInput; if (dir.sqrMagnitude < 0.001f) dir = user.AimDirection;
        bool applied = user.ApplyDash(dir, dashSpeed, dashDuration); if (!applied) return false;
        RuntimeVfx.SpawnCircularBurst(user.transform.position, new Color(0.58f, 0.60f, 0.64f, 0.85f), 13, 0.09f, 2.7f, 0.28f); return true;
    }

    private bool UseReversePolarity(PlayerController user)
    {
        GameObject obj = new GameObject("Торнадо"); obj.AddComponent<TornadoField>().Initialize(GetMouseWorldPosition(), polarityDuration, polarityRadius, polarityPullForce); return true;
    }

    public RuntimeItemPickup SpawnWorldItem(string itemId, Vector3 requestedPosition, int activeStoredKills = -1, int activeUsesRemaining = -1, bool swappingInPlace = false)
    {
        ItemDefinition d = GetDefinition(itemId);
        if (d == null) return null;

        // The old code placed rewards at fixed coordinates, even when a room
        // contained a new wall/stone at the same location. Query all solid 2D
        // colliders after syncing room transforms, then search nearby free cells.
        Physics2D.SyncTransforms();
        Vector2 safePosition;
        if (!TryFindFreePickupPosition(requestedPosition, out safePosition, swappingInPlace))
        {
            Debug.LogWarning("В комнате не найдено свободное место для предмета: " + d.DisplayName);
            return null;
        }

        GameObject obj = new GameObject("Item Pickup - " + d.DisplayName);
        obj.transform.position = new Vector3(safePosition.x, safePosition.y, 0f);
        RuntimeItemPickup pickup = obj.AddComponent<RuntimeItemPickup>();
        pickup.Initialize(this, d, activeStoredKills, activeUsesRemaining);
        return pickup;
    }

    private bool TryFindFreePickupPosition(Vector2 preferred, out Vector2 result, bool swappingInPlace)
    {
        const float radius = 0.64f;
        if (IsPickupPositionFree(preferred, radius, swappingInPlace))
        {
            result = preferred;
            return true;
        }

        // Try concentric rings first so displaced rewards stay near the requested
        // location, and finally scan the full accessible room on a regular grid.
        for (int ring = 1; ring <= 18; ring++)
        {
            float distance = ring * 0.38f;
            int samples = Mathf.Max(12, ring * 12);
            for (int i = 0; i < samples; i++)
            {
                float angle = (i / (float)samples) * Mathf.PI * 2f;
                Vector2 candidate = preferred + new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)) * distance;
                if (!IsPickupPositionFree(candidate, radius, false)) continue;
                result = candidate;
                return true;
            }
        }

        for (float y = -3.15f; y <= 3.15f; y += 0.65f)
        for (float x = -6.5f; x <= 6.5f; x += 0.65f)
        {
            Vector2 candidate = new Vector2(x, y);
            if (!IsPickupPositionFree(candidate, radius, false)) continue;
            result = candidate;
            return true;
        }

        result = preferred;
        return false;
    }

    private static bool IsPickupPositionFree(Vector2 position, float radius, bool swappingInPlace)
    {
        if (Mathf.Abs(position.x) > 6.75f || Mathf.Abs(position.y) > 3.32f)
            return false;

        Collider2D[] hits = Physics2D.OverlapCircleAll(position, radius);
        for (int i = 0; i < hits.Length; i++)
        {
            Collider2D hit = hits[i];
            if (hit == null || !hit.enabled) continue;
            // A pickup can coexist with harmless trigger zones, but NEVER with
            // walls, boulders, crates, living characters or existing loot.
            if (!hit.isTrigger) return false;
            // The currently collected item still exists until end-of-frame.
            // During an exchange only, its trigger may occupy this same spot.
            if (hit.GetComponent<RuntimeItemPickup>() != null && swappingInPlace)
                continue;
            if (hit.GetComponent<RuntimeItemPickup>() != null ||
                hit.GetComponentInParent<PlayerController>() != null ||
                hit.GetComponentInParent<EnemyAI>() != null ||
                hit.GetComponentInParent<BossController>() != null)
                return false;
        }
        return true;
    }

    public RuntimeItemPickup SpawnTutorialDash(Vector3 position) => SpawnWorldItem(ItemIds.Dash, position);
    public RuntimeItemPickup SpawnRoomClearReward(Vector3 position, int roomIndex)
    {
        if (roomIndex <= 1) { ItemDefinition p = GetRandomPassiveDefinition(); return p != null ? SpawnWorldItem(p.Id, position) : SpawnRandomActiveReward(position); }
        return SpawnRandomReward(position);
    }
    public RuntimeItemPickup SpawnRandomReward(Vector3 position)
    {
        if (UnityEngine.Random.value < 0.5f) { ItemDefinition p = GetRandomPassiveDefinition(); if (p != null) return SpawnWorldItem(p.Id, position); }
        return SpawnRandomActiveReward(position);
    }
    private RuntimeItemPickup SpawnRandomActiveReward(Vector3 position)
    {
        string[] ids = { ItemIds.Bomb, ItemIds.Pushback, ItemIds.Dash, ItemIds.ReversePolarity };
        List<string> available = new List<string>();
        for (int i = 0; i < ids.Length; i++)
            if (!IsActiveItemEquipped(ids[i])) available.Add(ids[i]);

        if (available.Count == 0)
        {
            ItemDefinition passive = GetRandomPassiveDefinition();
            return passive != null ? SpawnWorldItem(passive.Id, position) : null;
        }

        return SpawnWorldItem(available[UnityEngine.Random.Range(0, available.Count)], position);
    }

    private bool IsActiveItemEquipped(string itemId)
    {
        if (player == null) TryBindPlayer();
        if (player == null || string.IsNullOrWhiteSpace(itemId)) return false;
        for (int i = 0; i < player.ActiveItemCapacity; i++)
        {
            PlayerController.ActiveItemSlot slot = player.GetActiveItemSlot(i);
            if (slot != null && !slot.IsEmpty && string.Equals(slot.ItemId, itemId, StringComparison.Ordinal))
                return true;
        }
        return false;
    }
    private ItemDefinition GetRandomPassiveDefinition()
    {
        // Resurrection is a unique guaranteed Wraith King reward, never a random room or boss drop.
        List<ItemDefinition> available = new List<ItemDefinition>(); string[] ids = { ItemIds.AngelLens, ItemIds.Snack, ItemIds.Backpack, ItemIds.GlassCannon };
        for (int i = 0; i < ids.Length; i++) if (!ownedPassives.Contains(ids[i])) { ItemDefinition d = GetDefinition(ids[i]); if (d != null) available.Add(d); }
        return available.Count == 0 ? null : available[UnityEngine.Random.Range(0, available.Count)];
    }
    private void HandleBossRewardRequested(BossController boss, Vector3 position)
    {
        if (boss == null) return;

        if (boss.Kind == BossController.BossKind.WraithKing)
        {
            // Called from BeginFinalDeath, NOT the boss's intermediate resurrection.
            // Guaranteed drop, independent of random item pools or owned passives.
            SpawnWorldItem(ItemIds.Resurrection, position);
            return;
        }

        SpawnRandomReward(position);
    }
    private static Vector2 RotateVector(Vector2 v, float degrees) { float r = degrees * Mathf.Deg2Rad, c = Mathf.Cos(r), s = Mathf.Sin(r); return new Vector2(v.x * c - v.y * s, v.x * s + v.y * c); }
    private static Vector2 GetMouseWorldPosition() { Camera c = Camera.main; if (c == null) return Vector2.zero; Vector3 p = c.ScreenToWorldPoint(Input.mousePosition); return new Vector2(p.x, p.y); }
}

[DisallowMultipleComponent]
public sealed class RuntimeItemPickup : MonoBehaviour
{
    private ItemSystem system;
    private ItemSystem.ItemDefinition definition;
    private SpriteRenderer renderer;
    private CircleCollider2D trigger;
    private Texture2D texture;
    private Sprite sprite;
    private Canvas descriptionCanvas;
    private Text descriptionText;
    private PlayerController player;
    private bool initialized;
    private bool collected;
    private float baseY;
    private int activeStoredKills = -1;
    private int activeUsesRemaining = -1;
    private int interactionEnabledFrame;

    private const float DescriptionDistance = 3.75f;
    private const float InteractionDistance = 1.35f;

    public ItemSystem.ItemDefinition Definition => definition;

    private void Awake()
    {
        renderer = gameObject.AddComponent<SpriteRenderer>();
        renderer.sortingOrder = 11;

        trigger = gameObject.AddComponent<CircleCollider2D>();
        trigger.isTrigger = true;
        trigger.radius = 0.42f;

        baseY = transform.position.y;
        interactionEnabledFrame = Time.frameCount + 2;
    }

    public void Initialize(
        ItemSystem owningSystem,
        ItemSystem.ItemDefinition itemDefinition,
        int storedKills = -1,
        int storedUsesRemaining = -1)
    {
        system = owningSystem;
        definition = itemDefinition;
        activeStoredKills = storedKills;
        activeUsesRemaining = storedUsesRemaining;

        RebuildVisual();
        BuildDescriptionCanvas();

        initialized = true;
        baseY = transform.position.y;
        interactionEnabledFrame = Time.frameCount + 2;
    }

    private void Update()
    {
        if (!initialized || collected)
            return;

        if (player == null)
            player = FindAnyObjectByType<PlayerController>();

        Vector3 position = transform.position;
        position.y = baseY + Mathf.Sin(Time.time * 2.5f) * 0.055f;
        transform.position = position;

        float distance = player != null
            ? Vector2.Distance(player.transform.position, transform.position)
            : float.PositiveInfinity;

        if (descriptionCanvas != null)
        {
            // The canvas is deliberately detached from item rotation and kept level.
            descriptionCanvas.transform.rotation = Quaternion.identity;
            // Keep this larger text box INSIDE the room instead of clipping at walls.
            float boxX = Mathf.Clamp(transform.position.x, -5.10f, 5.10f);
            float boxY = Mathf.Clamp(transform.position.y + 1.42f, -2.25f, 2.28f);
            descriptionCanvas.transform.position = new Vector3(boxX, boxY, 0f);
            descriptionCanvas.enabled = distance <= DescriptionDistance;
        }

        if (player == null ||
            distance > InteractionDistance ||
            Time.frameCount < interactionEnabledFrame)
        {
            return;
        }

        if (!IsClosestPickupToPlayer())
            return;

        KeyCode interactKey =
            GameManager.Instance != null
                ? GameManager.Instance.InteractKey
                : KeyCode.E;

        bool interactPressed =
            Input.GetKeyDown(interactKey) ||
            (interactKey != KeyCode.E && Input.GetKeyDown(KeyCode.E));

        if (interactPressed)
            TryInteract();
    }

    private bool IsClosestPickupToPlayer()
    {
        if (player == null)
            return false;

        RuntimeItemPickup[] pickups =
            FindObjectsByType<RuntimeItemPickup>();

        float myDistance =
            ((Vector2)transform.position -
             (Vector2)player.transform.position).sqrMagnitude;

        for (int i = 0; i < pickups.Length; i++)
        {
            RuntimeItemPickup other = pickups[i];

            if (other == null ||
                other == this ||
                other.collected ||
                !other.initialized)
            {
                continue;
            }

            float otherDistance =
                ((Vector2)other.transform.position -
                 (Vector2)player.transform.position).sqrMagnitude;

            if (otherDistance + 0.0001f < myDistance)
                return false;
        }

        return true;
    }

    private void TryInteract()
    {
        if (GameplayRoomReveal.IsRevealing || collected || player == null)
            return;

        if (system == null)
            system = ItemSystem.Instance;

        if (system == null)
            return;

        if (!system.TryCollect(
                player,
                definition,
                transform.position,
                activeStoredKills,
                activeUsesRemaining))
        {
            return;
        }

        collected = true;

        if (trigger != null)
            trigger.enabled = false;

        if (renderer != null)
            renderer.enabled = false;

        if (descriptionCanvas != null)
            descriptionCanvas.enabled = false;

        Vector3 effectPosition = transform.position;

        Destroy(gameObject);

        RuntimeVfx.SpawnCircularBurst(
            effectPosition,
            definition.Category == ItemSystem.ItemCategory.Active
                ? new Color(0.68f, 0.04f, 0.055f, 1f)
                : new Color(0.77f, 0.70f, 0.42f, 1f),
            18,
            0.11f,
            2.8f,
            0.42f);
    }

    private void RebuildVisual()
    {
        bool active =
            definition.Category ==
            ItemSystem.ItemCategory.Active;

        Sprite importedIcon = RuntimeGameArt.Load("Items/" +
            (active ? "active/" : "passive/") +
            definition.Id.Substring(definition.Id.IndexOf('.') + 1));
        if (importedIcon != null)
        {
            // UI and floor pick-ups share the same authentic author-provided art.
            renderer.sprite = importedIcon;
            renderer.color = Color.white;
            return;
        }

        texture = ItemRuntimeVisuals.CreateItemRuneTexture(64, active);
        sprite = ItemRuntimeVisuals.CreateSprite(texture, 64f, definition.DisplayName);
        renderer.sprite = sprite;
    }

    // Every element has its own dedicated rect: long passive descriptions can
    // never push the E-interaction prompt out of view.
    private void BuildDescriptionCanvas()
    {
        GameObject canvasObject = new GameObject("Item Description Canvas", typeof(RectTransform));
        // The pickup bobs, but the box is placed in WORLD SPACE each Update.
        canvasObject.transform.SetParent(transform, false);
        canvasObject.transform.localPosition = new Vector3(0f, 1.42f, 0f);
        canvasObject.transform.rotation = Quaternion.identity;
        canvasObject.transform.localScale = Vector3.one * 0.0076f;

        descriptionCanvas = canvasObject.AddComponent<Canvas>();
        descriptionCanvas.renderMode = RenderMode.WorldSpace;
        descriptionCanvas.sortingOrder = 50;
        RectTransform canvasRect = canvasObject.GetComponent<RectTransform>();
        canvasRect.sizeDelta = new Vector2(580f, 310f);

        GameObject border = new GameObject("Ornamental Border", typeof(RectTransform), typeof(Image));
        border.transform.SetParent(canvasObject.transform, false);
        RectTransform borderRect = border.GetComponent<RectTransform>();
        borderRect.anchorMin = Vector2.zero;
        borderRect.anchorMax = Vector2.one;
        borderRect.offsetMin = borderRect.offsetMax = Vector2.zero;
        border.GetComponent<Image>().color = new Color(0.34f, 0.015f, 0.025f, 0.98f);

        GameObject paper = new GameObject("Parchment", typeof(RectTransform), typeof(Image));
        paper.transform.SetParent(canvasObject.transform, false);
        RectTransform paperRect = paper.GetComponent<RectTransform>();
        paperRect.anchorMin = Vector2.zero;
        paperRect.anchorMax = Vector2.one;
        paperRect.offsetMin = new Vector2(7f, 7f);
        paperRect.offsetMax = new Vector2(-7f, -7f);
        paper.GetComponent<Image>().color = new Color(0.95f, 0.94f, 0.90f, 0.99f);
        CreateCornerMark(paper.transform, new Vector2(0f, 1f), new Vector2(1f, -1f));
        CreateCornerMark(paper.transform, new Vector2(1f, 1f), new Vector2(-1f, -1f));
        CreateCornerMark(paper.transform, new Vector2(0f, 0f), new Vector2(1f, 1f));
        CreateCornerMark(paper.transform, new Vector2(1f, 0f), new Vector2(-1f, 1f));

        CreatePickupText(canvasObject.transform, "Название", "<b>" + definition.DisplayName + "</b>",
            33, 28, new Vector2(0.05f, 0.79f), new Vector2(0.95f, 0.96f), true);
        CreatePickupText(canvasObject.transform, "Категория",
            definition.Category == ItemSystem.ItemCategory.Active ? "[Активная]" : "[Пассивная]",
            27, 23, new Vector2(0.05f, 0.67f), new Vector2(0.95f, 0.81f), false);

        string bodyText = definition.Description;
        if (definition.Category == ItemSystem.ItemCategory.Active)
            bodyText += "\nЗаряд: " + definition.RequiredKills +
                (definition.RequiredKills == 1 ? " убийство" : " убийств");
        descriptionText = CreatePickupText(canvasObject.transform, "Описание", bodyText,
            27, 22, new Vector2(0.065f, 0.20f), new Vector2(0.935f, 0.68f), false);

        KeyCode interaction = GameManager.Instance != null ? GameManager.Instance.InteractKey : KeyCode.E;
        string interactLabel = interaction == KeyCode.E ? "E" :
            interaction == KeyCode.Mouse0 ? "ЛКМ" : interaction.ToString();
        string action = definition.Category == ItemSystem.ItemCategory.Active
            ? "Нажмите " + interactLabel + " — подобрать / заменить"
            : "Нажмите " + interactLabel + " — подобрать";
        // Always visible, including for the longest description: Angel Lens.
        CreatePickupText(canvasObject.transform, "Подобрать", "<b>" + action + "</b>",
            29, 25, new Vector2(0.045f, 0.025f), new Vector2(0.955f, 0.19f), true);
        descriptionCanvas.enabled = false;
    }

    private static Text CreatePickupText(Transform parent, string name, string value,
        int fontSize, int minimumSize, Vector2 anchorMin, Vector2 anchorMax, bool bold)
    {
        GameObject textObject = new GameObject(name, typeof(RectTransform), typeof(Text));
        textObject.transform.SetParent(parent, false);
        RectTransform rect = textObject.GetComponent<RectTransform>();
        rect.anchorMin = anchorMin;
        rect.anchorMax = anchorMax;
        rect.offsetMin = rect.offsetMax = Vector2.zero;
        Text text = textObject.GetComponent<Text>();
        text.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        text.fontSize = fontSize;
        text.resizeTextForBestFit = true;
        text.resizeTextMinSize = minimumSize;
        text.resizeTextMaxSize = fontSize;
        text.lineSpacing = 1.04f;
        text.alignment = TextAnchor.MiddleCenter;
        text.horizontalOverflow = HorizontalWrapMode.Wrap;
        text.verticalOverflow = VerticalWrapMode.Truncate;
        text.fontStyle = bold ? FontStyle.Bold : FontStyle.Normal;
        text.supportRichText = true;
        text.color = new Color(0.055f, 0.045f, 0.04f, 1f);
        text.raycastTarget = false;
        text.text = value;
        return text;
    }

    private static void CreateCornerMark(
        Transform parent,
        Vector2 anchor,
        Vector2 direction)
    {
        GameObject mark =
            new GameObject(
                "Corner Ornament",
                typeof(RectTransform),
                typeof(Image));

        mark.transform.SetParent(
            parent,
            false);

        RectTransform rect =
            mark.GetComponent<RectTransform>();

        rect.anchorMin = anchor;
        rect.anchorMax = anchor;
        rect.pivot = anchor;
        rect.sizeDelta = new Vector2(28f, 5f);
        rect.anchoredPosition = new Vector2(
            direction.x * 5f,
            direction.y * 5f);

        rect.localRotation =
            Quaternion.Euler(
                0f,
                0f,
                direction.x * direction.y > 0f ? 45f : -45f);

        mark.GetComponent<Image>().color =
            new Color(
                0.34f,
                0.015f,
                0.025f,
                1f);
    }

    private void OnDestroy()
    {
        if (sprite != null)
            Destroy(sprite);

        if (texture != null)
            Destroy(texture);
    }
}

[DisallowMultipleComponent]
internal sealed class TemporaryReflectionStone : MonoBehaviour
{
    private Texture2D texture; private Sprite sprite; private SpriteRenderer renderer; private float lifetime, elapsed; private bool initialized;
    private void Awake()
    {
        Rigidbody2D rb = gameObject.AddComponent<Rigidbody2D>(); rb.bodyType = RigidbodyType2D.Static; rb.gravityScale = 0f; CircleCollider2D c = gameObject.AddComponent<CircleCollider2D>(); c.radius = 0.48f;
        renderer = gameObject.AddComponent<SpriteRenderer>(); renderer.sortingOrder = 5; texture = ItemRuntimeVisuals.CreateStoneTexture(72); sprite = ItemRuntimeVisuals.CreateSprite(texture, 64f, "Temporary Reflection Stone"); renderer.sprite = sprite;
    }
    public void Initialize(float maximumLifetime) { lifetime = Mathf.Max(0.5f, maximumLifetime); initialized = true; }
    private void Update() { if (!initialized) return; elapsed += Time.deltaTime; if (lifetime - elapsed <= 2f) { Color c = renderer.color; c.a = Mathf.Lerp(0.25f, 1f, Mathf.Abs(Mathf.Sin(Time.time * 8f))); renderer.color = c; } if (elapsed >= lifetime) Destroy(gameObject); }
    private void OnDestroy() { if (sprite != null) Destroy(sprite); if (texture != null) Destroy(texture); }
}

[DisallowMultipleComponent]
internal sealed class BombProjectile : MonoBehaviour
{
    private PlayerController owner; private Rigidbody2D body; private CircleCollider2D bombCollider; private SpriteRenderer renderer; private Texture2D texture; private Sprite sprite; private PhysicsMaterial2D material; private Vector2 direction; private float speed, fuse, radius, damage, elapsed; private bool initialized, exploded;
    private void Awake()
    {
        body = gameObject.AddComponent<Rigidbody2D>(); body.bodyType = RigidbodyType2D.Dynamic; body.gravityScale = 0f; body.linearDamping = 0f; body.angularDamping = 0.45f; body.collisionDetectionMode = CollisionDetectionMode2D.Continuous; body.interpolation = RigidbodyInterpolation2D.Interpolate;
        bombCollider = gameObject.AddComponent<CircleCollider2D>(); bombCollider.radius = 0.39f; material = new PhysicsMaterial2D("Runtime Bomb Physics") { friction = 0f, bounciness = 0f }; bombCollider.sharedMaterial = material;
        renderer = gameObject.AddComponent<SpriteRenderer>(); renderer.sortingOrder = 21; Sprite approvedBomb = RuntimeGameArt.Load("Projectiles/Bomb"); if (approvedBomb != null) renderer.sprite = approvedBomb; else { texture = ItemRuntimeVisuals.CreateBombTexture(64); sprite = ItemRuntimeVisuals.CreateSprite(texture, 64f, "Runtime Bomb"); renderer.sprite = sprite; }
    }
    public void Initialize(PlayerController projectileOwner, Vector2 position, Vector2 initialDirection, float movementSpeed, float fuseTime, float explosionRadius, float explosionDamage)
    {
        owner = projectileOwner;
        direction = initialDirection.sqrMagnitude > 0.001f ? initialDirection.normalized : Vector2.right;
        speed = Mathf.Max(0.1f, movementSpeed);
        fuse = Mathf.Max(0.15f, fuseTime);
        radius = Mathf.Max(0.2f, explosionRadius);
        damage = Mathf.Max(0f, explosionDamage);
        Vector2 limits = GetArenaLimits();
        body.position = new Vector2(Mathf.Clamp(position.x, -limits.x, limits.x),
                                    Mathf.Clamp(position.y, -limits.y, limits.y));
        IgnoreOwnerCollision();
        transform.localScale = Vector3.one * 2f;
        body.linearVelocity = direction * speed;
        initialized = true;
    }
    private void Update() { if (!initialized || exploded) return; elapsed += Time.deltaTime; float t = Mathf.Clamp01(elapsed / fuse); transform.localScale = Vector3.one * (2f + Mathf.Sin(Time.time * Mathf.Lerp(5f, 18f, t)) * 0.14f); if (elapsed >= fuse) Explode(); }
    private void FixedUpdate()
    {
        if (!initialized || exploded) return;
        if (body.linearVelocity.sqrMagnitude > 0.01f)
            direction = body.linearVelocity.normalized;

        // The door collider becomes a trigger after unlocking, so physics alone
        // cannot keep bombs in the room. Enforce the same room-wide boundary
        // independently from door status, without blocking player transitions.
        Vector2 limits = GetArenaLimits();
        Vector2 here = body.position;
        Vector2 next = here + direction * (speed * Time.fixedDeltaTime);
        if (next.x > limits.x && direction.x > 0f) direction.x = -direction.x;
        else if (next.x < -limits.x && direction.x < 0f) direction.x = -direction.x;
        if (next.y > limits.y && direction.y > 0f) direction.y = -direction.y;
        else if (next.y < -limits.y && direction.y < 0f) direction.y = -direction.y;
        body.position = new Vector2(Mathf.Clamp(here.x, -limits.x, limits.x),
                                    Mathf.Clamp(here.y, -limits.y, limits.y));
        body.linearVelocity = direction.normalized * speed;
    }
    private Vector2 GetArenaLimits()
    {
        LevelRoomManager room = LevelRoomManager.Instance;
        return room != null ? room.GetBombMovementLimits(bombCollider != null ? bombCollider.radius : 0.31f)
                            : new Vector2(7.0f, 3.5f);
    }
    private void OnCollisionEnter2D(Collision2D collision)
    {
        if (!initialized || exploded || collision == null || collision.collider == null) return;
        if (collision.collider.GetComponentInParent<EnemyAI>() != null || collision.collider.GetComponentInParent<BossController>() != null) { Explode(); return; }
        if (collision.contactCount <= 0) { direction = -direction; return; }
        Vector2 incoming = body.linearVelocity.sqrMagnitude > 0.001f ? body.linearVelocity.normalized : direction; direction = Vector2.Reflect(incoming, collision.GetContact(0).normal).normalized; body.position += direction * 0.04f; body.linearVelocity = direction * speed;
    }
    private void IgnoreOwnerCollision() { if (owner == null) return; Collider2D[] cs = owner.GetComponentsInChildren<Collider2D>(); for (int i = 0; i < cs.Length; i++) if (cs[i] != null) Physics2D.IgnoreCollision(bombCollider, cs[i], true); }
    private void Explode()
    {
        if (exploded) return; exploded = true; if (AudioManager.Instance != null) AudioManager.Instance.PlaySfx(AudioManager.SfxId.Explosion); body.linearVelocity = Vector2.zero; bombCollider.enabled = false;
        RuntimeVfx.SpawnCircularBurst(transform.position, new Color(1.0f, 0.75f, 0.15f, 1f), 36, 0.22f, 6.8f, 0.60f);
        RuntimeVfx.SpawnCircularBurst(transform.position, new Color(0.86f, 0.18f, 0.04f, 1f), 52, 0.28f, 8.7f, 0.95f);
        RuntimeVfx.SpawnCircularBurst(transform.position, new Color(0.24f, 0.24f, 0.24f, 0.90f), 28, 0.18f, 4.2f, 0.85f);
        Collider2D[] hits = Physics2D.OverlapCircleAll(transform.position, radius); HashSet<ICombatDamageable> damaged = new HashSet<ICombatDamageable>();
        for (int i = 0; i < hits.Length; i++)
        {
            if (hits[i].GetComponentInParent<PlayerController>() != null) continue; ICombatDamageable target = FindDamageable(hits[i]); if (target != null && damaged.Add(target)) target.ReceiveDamage(damage, gameObject);
        }
        RuntimeVfx.SpawnCircularBurst(transform.position, new Color(1.0f, 0.86f, 0.20f, 0.95f), 24, 0.36f, 6.0f, 0.55f); RuntimeVfx.SpawnCircularBurst(transform.position, new Color(0.72f, 0.09f, 0.025f, 1f), 58, 0.28f, 8.2f, 0.95f); Destroy(gameObject);
    }
    private static ICombatDamageable FindDamageable(Collider2D c) { MonoBehaviour[] bs = c.GetComponentsInParent<MonoBehaviour>(); for (int i = 0; i < bs.Length; i++) if (bs[i] is ICombatDamageable d) return d; return null; }
    private void OnDestroy() { if (sprite != null) Destroy(sprite); if (texture != null) Destroy(texture); if (material != null) Destroy(material); }
}

[DisallowMultipleComponent]
internal sealed class TornadoField : MonoBehaviour
{
    private SpriteRenderer outerRenderer;
    private Transform innerSwirl;
    private SpriteRenderer innerRenderer;
    private Texture2D texture;
    private Sprite sprite;
    private float lifetime, radius, pullForce, elapsed;
    private bool initialized;

    private void Awake()
    {
        outerRenderer = gameObject.AddComponent<SpriteRenderer>();
        outerRenderer.sortingOrder = 6;
        texture = ItemRuntimeVisuals.CreateTornadoTexture(128);
        sprite = ItemRuntimeVisuals.CreateSprite(texture, 64f, "Торнадо — спираль");
        outerRenderer.sprite = sprite;
        outerRenderer.color = new Color(0.93f, 0.94f, 0.96f, 0.94f);

        GameObject secondary = new GameObject("Внутренний вращающийся вихрь");
        secondary.transform.SetParent(transform, false);
        innerSwirl = secondary.transform;
        innerSwirl.localScale = Vector3.one * 0.63f;
        innerRenderer = secondary.AddComponent<SpriteRenderer>();
        innerRenderer.sortingOrder = 7;
        innerRenderer.sprite = sprite;
        innerRenderer.color = new Color(0.80f, 0.84f, 0.94f, 0.64f);
    }

    public void Initialize(Vector2 position, float maximumLifetime, float effectRadius, float force)
    {
        transform.position = position;
        lifetime = Mathf.Max(0.1f, maximumLifetime);
        radius = Mathf.Max(0.25f, effectRadius);
        pullForce = Mathf.Max(0f, force);
        // Previous visual scale was radius(5) * .32 = 1.6.
        // New radius(7.5) * .32 = 2.4, exactly 1.5x bigger.
        transform.localScale = Vector3.one * Mathf.Clamp(radius * 0.32f, 0.8f, 3.8f);
        initialized = true;
    }

    private void Update()
    {
        if (!initialized) return;
        elapsed += Time.deltaTime;
        transform.Rotate(0f, 0f, 175f * Time.deltaTime);
        if (innerSwirl != null)
        {
            innerSwirl.Rotate(0f, 0f, -335f * Time.deltaTime);
            float breathing = 0.62f + Mathf.Sin(elapsed * 9f) * 0.06f;
            innerSwirl.localScale = Vector3.one * breathing;
        }
        float alpha = Mathf.Clamp01(1f - elapsed / lifetime);
        alpha *= Mathf.Clamp01(elapsed * 5f);
        if (outerRenderer != null)
            outerRenderer.color = new Color(0.93f, 0.94f, 0.96f, 0.94f * alpha);
        if (innerRenderer != null)
            innerRenderer.color = new Color(0.80f, 0.84f, 0.94f, 0.68f * alpha);
        if (elapsed >= lifetime) Destroy(gameObject);
    }

    private void FixedUpdate()
    {
        if (!initialized || GameplayRoomReveal.IsRevealing) return;
        Collider2D[] hits = Physics2D.OverlapCircleAll(transform.position, radius);
        HashSet<Rigidbody2D> processed = new HashSet<Rigidbody2D>();
        for (int i = 0; i < hits.Length; i++)
        {
            EnemyAI enemy = hits[i].GetComponentInParent<EnemyAI>();
            BossController boss = hits[i].GetComponentInParent<BossController>();
            if (enemy == null && boss == null) continue;
            Rigidbody2D rb = hits[i].attachedRigidbody;
            if (rb == null || !processed.Add(rb)) continue;

            Vector2 offset = (Vector2)transform.position - rb.position;
            float distance = offset.magnitude;
            if (distance <= 0.05f) continue;

            Vector2 inward = offset / distance;
            Vector2 tangential = new Vector2(-inward.y, inward.x);
            float falloff = Mathf.Lerp(1f, 0.20f, Mathf.Clamp01(distance / radius));
            Vector2 swirlForce = (inward * 0.90f + tangential * 0.55f).normalized * pullForce * falloff;
            if (enemy != null) enemy.ApplyExternalPull(swirlForce);
            else boss.ApplyExternalPull(swirlForce);
        }
    }

    private void OnDestroy()
    {
        if (sprite != null) Destroy(sprite);
        if (texture != null) Destroy(texture);
    }
}

[DisallowMultipleComponent]
internal sealed class AngelSplinterProjectile : MonoBehaviour
{
    private Rigidbody2D body; private Texture2D texture; private Sprite sprite; private Vector2 direction; private float speed, damage, lifetime, elapsed; private bool initialized, hitSomething;
    private void Awake()
    {
        body = gameObject.AddComponent<Rigidbody2D>();
        body.bodyType = RigidbodyType2D.Kinematic;
        body.gravityScale = 0f;
        body.collisionDetectionMode = CollisionDetectionMode2D.Continuous;

        CircleCollider2D c = gameObject.AddComponent<CircleCollider2D>();
        c.radius = 0.12f;
        c.isTrigger = true;

        SpriteRenderer renderer = gameObject.AddComponent<SpriteRenderer>();
        renderer.sortingOrder = 22;

        // Angel Lens splinters are miniature golden versions of the approved
        // real dagger art, not generated yellow circles / flashes.
        Sprite goldenDagger = RuntimeGameArt.Load("Projectiles/Dagger_AngelYellow");
        if (goldenDagger != null)
        {
            renderer.sprite = goldenDagger;
            renderer.color = Color.white;
            transform.localScale = Vector3.one * 0.56f;
        }
        else
        {
            // Graceful offline fallback, only used if the imported asset is missing.
            Sprite dagger = RuntimeGameArt.Load("Projectiles/Dagger_Right");
            if (dagger != null)
            {
                renderer.sprite = dagger;
                renderer.color = new Color(1f, 0.86f, 0.20f, 1f);
                transform.localScale = Vector3.one * 0.56f;
            }
            else
            {
                texture = ItemRuntimeVisuals.CreateAngelSplinterTexture(28);
                sprite = ItemRuntimeVisuals.CreateSprite(texture, 64f, "Angel Lens Splinter");
                renderer.sprite = sprite;
            }
        }
    }
    public void Initialize(PlayerController owner, Vector2 position, Vector2 initialDirection, float movementSpeed, float projectileDamage, float maximumLifetime)
    {
        transform.position = position;
        direction = initialDirection.sqrMagnitude > 0.001f ? initialDirection.normalized : Vector2.right;
        transform.rotation = Quaternion.Euler(0f, 0f, Mathf.Atan2(direction.y, direction.x) * Mathf.Rad2Deg);
        speed = Mathf.Max(0.1f, movementSpeed);
        damage = Mathf.Max(0f, projectileDamage);
        lifetime = Mathf.Max(0.1f, maximumLifetime);
        initialized = true;
    }
    private void FixedUpdate()
    {
        if (!initialized || hitSomething) return;
        if (GameplayRoomReveal.IsRevealing) return;
        Vector2 nextPosition = body.position + direction * speed * Time.fixedDeltaTime;
        if (Mathf.Abs(nextPosition.x) >= 7.42f || Mathf.Abs(nextPosition.y) >= 4.02f)
        {
            hitSomething = true;
            Destroy(gameObject);
            return;
        }
        body.MovePosition(nextPosition);
    }
    private void Update() { if (!initialized) return; elapsed += Time.deltaTime; if (elapsed >= lifetime) Destroy(gameObject); }
    private void OnTriggerEnter2D(Collider2D other)
    {
        if (!initialized || hitSomething || other == null || other.GetComponentInParent<PlayerController>() != null || other.GetComponentInParent<Projectile>() != null) return;
        ICombatDamageable d = FindDamageable(other); if (d != null) { hitSomething = true; d.ReceiveDamage(damage, gameObject); Destroy(gameObject); return; } if (!other.isTrigger) { hitSomething = true; Destroy(gameObject); }
    }
    private static ICombatDamageable FindDamageable(Collider2D c) { MonoBehaviour[] bs = c.GetComponentsInParent<MonoBehaviour>(); for (int i = 0; i < bs.Length; i++) if (bs[i] is ICombatDamageable d) return d; return null; }
    private void OnDestroy() { if (sprite != null) Destroy(sprite); if (texture != null) Destroy(texture); }
}

internal static class ItemRuntimeVisuals
{
    public static Sprite CreateSprite(Texture2D texture, float ppu, string name) { Sprite s = Sprite.Create(texture, new Rect(0f, 0f, texture.width, texture.height), new Vector2(0.5f, 0.5f), ppu, 0, SpriteMeshType.FullRect); s.name = name; return s; }
    public static Texture2D CreateItemRuneTexture(int size, bool active)
    {
        Texture2D t = PrepareTexture(size, "Runtime Item Rune"); Color[] p = new Color[size * size]; float c = (size - 1) * 0.5f, outer = size * 0.43f, inner = size * 0.31f;
        Color oc = active ? new Color(0.42f, 0.018f, 0.028f, 1f) : new Color(0.46f, 0.40f, 0.20f, 1f); Color ic = active ? new Color(0.72f, 0.05f, 0.065f, 1f) : new Color(0.84f, 0.73f, 0.36f, 1f);
        for (int y = 0; y < size; y++) for (int x = 0; x < size; x++) { float dx = x - c, dy = y - c, d = Mathf.Sqrt(dx * dx + dy * dy); int i = y * size + x; if (d > outer) p[i] = Color.clear; else if (d > inner) p[i] = oc; else { float nx = dx / inner, ny = dy / inner; bool rune = Mathf.Abs(nx) < 0.08f || Mathf.Abs(ny - nx * 0.65f) < 0.07f || Mathf.Abs(ny + nx * 0.65f) < 0.07f; p[i] = rune ? ic : new Color(0.07f, 0.065f, 0.065f, 1f); } }
        t.SetPixels(p); t.Apply(false, false); return t;
    }
    public static Texture2D CreateStoneTexture(int size)
    {
        Texture2D t = PrepareTexture(size, "Temporary Stone Texture"); Color[] p = new Color[size * size]; float c = (size - 1) * 0.5f, r = size * 0.44f;
        for (int y = 0; y < size; y++) for (int x = 0; x < size; x++) { float dx = x - c, dy = y - c, a = Mathf.Atan2(dy, dx), d = Mathf.Sqrt(dx * dx + dy * dy); int i = y * size + x; float irr = 1f + Mathf.Sin(a * 5f + 0.7f) * 0.045f + Mathf.Sin(a * 9f) * 0.025f; if (d > r * irr) p[i] = Color.clear; else { float s = Mathf.Lerp(0.43f, 0.18f, d / r) + Mathf.Sin(x * 0.71f + y * 1.13f) * 0.035f; p[i] = new Color(s, s * 0.98f, s * 0.94f, 1f); } }
        t.SetPixels(p); t.Apply(false, false); return t;
    }
    public static Texture2D CreateBombTexture(int size)
    {
        Texture2D t = PrepareTexture(size, "Runtime Bomb Texture"); Color[] p = new Color[size * size]; float c = (size - 1) * 0.5f, r = size * 0.37f;
        for (int y = 0; y < size; y++) for (int x = 0; x < size; x++) { float dx = x - c, dy = y - c, d = Mathf.Sqrt(dx * dx + dy * dy); int i = y * size + x; bool fuse = x > c + r * 0.38f && x < c + r * 0.62f && y > c + r * 0.60f && y < c + r * 1.20f; if (fuse) p[i] = new Color(0.72f, 0.32f, 0.08f, 1f); else if (d > r) p[i] = Color.clear; else { float s = Mathf.Lerp(0.22f, 0.035f, d / r); p[i] = new Color(s, s, s * 1.05f, 1f); } }
        t.SetPixels(p); t.Apply(false, false); return t;
    }
    public static Texture2D CreateTornadoTexture(int size)
    {
        Texture2D t = PrepareTexture(size, "Торнадо — пиксельный спиральный вихрь");
        Color[] pixels = new Color[size * size];
        float center = (size - 1) * 0.5f;
        float radius = size * 0.48f;

        for (int y = 0; y < size; y++)
        for (int x = 0; x < size; x++)
        {
            float dx = x - center;
            float dy = y - center;
            float distance = Mathf.Sqrt(dx * dx + dy * dy);
            int index = y * size + x;
            if (distance >= radius) { pixels[index] = Color.clear; continue; }

            float radial = distance / radius;
            float theta = Mathf.Atan2(dy, dx);
            // Three trailing spiral arms wind around a darker central funnel.
            float spiral = Mathf.Sin(theta * 3f + radial * 17.5f);
            float secondary = Mathf.Sin(theta * 6f + radial * 22f);
            float cloud = Mathf.PerlinNoise(x * 0.11f + 3.1f, y * 0.11f + 2.2f);
            float arm = Mathf.Pow(Mathf.Clamp01(0.5f + spiral * 0.5f), 3.0f);
            float mist = Mathf.Pow(Mathf.Clamp01(0.5f + secondary * 0.5f), 4.0f);
            float rimFade = Mathf.SmoothStep(1f, 0f, Mathf.Clamp01((radial - 0.76f) / 0.24f));
            float centerFunnel = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(radial / 0.22f));
            float alpha = Mathf.Clamp01((arm * 0.73f + mist * 0.22f + cloud * 0.16f) * rimFade * centerFunnel);
            Color darkSmoke = new Color(0.21f, 0.24f, 0.30f, 1f);
            Color lightSmoke = new Color(0.81f, 0.85f, 0.87f, 1f);
            Color shade = Color.Lerp(darkSmoke, lightSmoke, Mathf.Clamp01(arm * 0.85f + cloud * 0.20f));
            pixels[index] = new Color(shade.r, shade.g, shade.b, alpha);
        }
        t.SetPixels(pixels);
        t.Apply(false, false);
        return t;
    }
    public static Texture2D CreateAngelSplinterTexture(int size)
    {
        Texture2D t = PrepareTexture(size, "Angel Splinter Texture"); Color[] p = new Color[size * size]; float c = (size - 1) * 0.5f, r = size * 0.41f;
        for (int y = 0; y < size; y++) for (int x = 0; x < size; x++) { float dx = x - c, dy = y - c, d = Mathf.Sqrt(dx * dx + dy * dy); int i = y * size + x; p[i] = d > r ? Color.clear : Color.Lerp(new Color(1f, 0.94f, 0.68f, 1f), new Color(0.62f, 0.45f, 0.16f, 0.4f), d / r); }
        t.SetPixels(p); t.Apply(false, false); return t;
    }
    private static Texture2D PrepareTexture(int size, string name) => new Texture2D(size, size, TextureFormat.RGBA32, false) { name = name, filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp };
}
