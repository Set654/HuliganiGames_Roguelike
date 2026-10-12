using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

[DisallowMultipleComponent]
[RequireComponent(typeof(Rigidbody2D))]
[RequireComponent(typeof(CircleCollider2D))]
[RequireComponent(typeof(SpriteRenderer))]
public sealed class PlayerController : MonoBehaviour, ICombatDamageable
{
    [Serializable]
    public sealed class ActiveItemSlot
    {
        [SerializeField] private string itemId = string.Empty;
        [SerializeField] private string displayName = string.Empty;
        [SerializeField] private int requiredKills = 1;
        [SerializeField] private int storedKills;
        [SerializeField] private int usesPerCharge = 1;
        [SerializeField] private int usesRemaining = 1;

        public string ItemId => itemId;
        public string DisplayName => displayName;
        public int RequiredKills => Mathf.Max(1, requiredKills);
        public int StoredKills => Mathf.Clamp(storedKills, 0, RequiredKills);
        public bool IsEmpty => string.IsNullOrWhiteSpace(itemId);
        public bool IsCharged => !IsEmpty && StoredKills >= RequiredKills;
        public float ChargeNormalized => IsEmpty ? 0f : Mathf.Clamp01(StoredKills / (float)RequiredKills);
        public int UsesPerCharge => Mathf.Max(1, usesPerCharge);
        public int UsesRemaining => IsCharged ? Mathf.Clamp(usesRemaining, 1, UsesPerCharge) : 0;

        internal void Assign(string newItemId, string newDisplayName, int newRequiredKills, bool startCharged)
        {
            AssignWithCharge(newItemId, newDisplayName, newRequiredKills, startCharged ? Mathf.Max(1, newRequiredKills) : 0);
        }

        internal void AssignWithCharge(string newItemId, string newDisplayName, int newRequiredKills, int startingStoredKills, int startingUsesRemaining = -1)
        {
            itemId = newItemId;
            displayName = string.IsNullOrWhiteSpace(newDisplayName) ? newItemId : newDisplayName;
            requiredKills = Mathf.Max(1, newRequiredKills);
            storedKills = Mathf.Clamp(startingStoredKills, 0, requiredKills);
            usesPerCharge = string.Equals(itemId, "active.dash", StringComparison.Ordinal) ? 2 : 1;
            if (storedKills >= requiredKills)
                usesRemaining = startingUsesRemaining >= 0 ? Mathf.Clamp(startingUsesRemaining, 1, usesPerCharge) : usesPerCharge;
            else
                usesRemaining = 0;
        }
        internal void Clear() { itemId = string.Empty; displayName = string.Empty; requiredKills = 1; storedKills = 0; usesPerCharge = 1; usesRemaining = 0; }
        internal bool AddKillCharge(int amount)
        {
            if (IsEmpty || amount <= 0 || IsCharged) return false;
            int old = storedKills;
            storedKills = Mathf.Clamp(storedKills + amount, 0, RequiredKills);
            if (old < RequiredKills && storedKills >= RequiredKills)
                usesRemaining = UsesPerCharge;
            return old != storedKills;
        }
        internal bool ConsumeCharge()
        {
            if (!IsCharged) return false;
            if (usesRemaining > 1)
            {
                usesRemaining--;
                return true;
            }
            usesRemaining = 0;
            storedKills = 0;
            return true;
        }
        internal bool ReduceCharge(int amount)
        {
            if (IsEmpty || amount <= 0) return false;
            int old = storedKills;
            storedKills = Mathf.Max(0, storedKills - amount);
            if (storedKills < RequiredKills) usesRemaining = 0;
            return old != storedKills;
        }
    }


    public struct ActiveItemSnapshot
    {
        public string ItemId;
        public string DisplayName;
        public int RequiredKills;
        public int StoredKills;
        public int UsesRemaining;
        public bool IsValid => !string.IsNullOrWhiteSpace(ItemId);
    }

    [Header("Movement")]
    [SerializeField] private float movementSpeed = 4.35f;

    [Header("Combat")]
    [SerializeField] private float projectileDamage = 1f;
    [SerializeField] private float projectileSpeed = 11f;
    [SerializeField] private float projectileLifetime = 8f;
    [SerializeField] private float fireInterval = 0.18f;
    [SerializeField] private int maximumActiveProjectiles = 3;
    [SerializeField] private float projectileSpawnOffset = 0.62f;

    [Header("Health")]
    [SerializeField] private float maxHealth = 3f;
    [SerializeField] private float invulnerabilityDuration = 0.5f;
    [SerializeField] private float flashInterval = 0.055f;
    [SerializeField] private float incomingDamageMultiplier = 1f;

    [Header("Active Item Inventory")]
    [Range(1, 2)] [SerializeField] private int activeItemCapacity = 1;
    [SerializeField] private ActiveItemSlot[] activeItemSlots = { new ActiveItemSlot(), new ActiveItemSlot() };

    private Rigidbody2D body;
    private CircleCollider2D bodyCollider;
    private SpriteRenderer spriteRenderer;
    private Texture2D generatedTexture;
    private Sprite generatedSprite;
    private Vector2 movementInput;
    private Vector2 aimDirection = Vector2.right;
    private Vector2 temporaryMovementVelocity;
    private float temporaryMovementVelocityEndTime;
    private float currentHealth;
    private float nextFireTime;
    private bool isInvulnerable;
    private bool isDead;
    private int selectedActiveSlot;
    private Coroutine invulnerabilityCoroutine;
    private GameManager boundGameManager;
    private readonly HashSet<Projectile> activeProjectiles = new HashSet<Projectile>();
    private readonly Dictionary<string, Func<PlayerController, bool>> activeItemBehaviours = new Dictionary<string, Func<PlayerController, bool>>(StringComparer.Ordinal);
    private readonly List<Func<PlayerController, bool>> deathInterceptors = new List<Func<PlayerController, bool>>();

    public float CurrentHealth => currentHealth;
    public float MaxHealth => maxHealth;
    public float ProjectileDamage => projectileDamage;
    public float MovementSpeed => movementSpeed;
    public bool IsInvulnerable => isInvulnerable;
    public bool IsDead => isDead;
    public Vector2 MovementInput => movementInput;
    public Vector2 AimDirection => aimDirection;
    public int ActiveProjectileCount { get { PruneDestroyedProjectiles(); return activeProjectiles.Count; } }
    public int MaximumActiveProjectiles => maximumActiveProjectiles;
    public int ActiveItemCapacity => activeItemCapacity;
    public int SelectedActiveSlotIndex => selectedActiveSlot;
    public ActiveItemSlot SelectedActiveItem => activeItemSlots[selectedActiveSlot];

    public event Action<float, float> HealthChanged;
    public event Action PlayerDied;
    public event Action StatsChanged;
    public event Action InventoryChanged;
    public event Action<int> SelectedActiveSlotChanged;
    public event Action<PlayerController> InteractPressed;
    public event Action<Projectile, Vector2> ProjectileFirstRicochet;
    public event Action<Projectile> ProjectileFired;

    private void Awake()
    {
        // Slightly larger approved pixel-art hero, preserving current room mechanics.
        transform.localScale = Vector3.one * 1.332f;
        EnsureInventoryArray();
        EnsurePhysicsComponents();
        EnsureVisualComponents();
        maxHealth = Mathf.Max(0.5f, maxHealth);
        projectileDamage = Mathf.Max(0f, projectileDamage);
        incomingDamageMultiplier = Mathf.Max(0f, incomingDamageMultiplier);
        currentHealth = maxHealth;
        activeItemCapacity = Mathf.Clamp(activeItemCapacity, 1, 2);
        maximumActiveProjectiles = Mathf.Max(1, maximumActiveProjectiles);
    }

    private void OnEnable()
    {
        transform.rotation = Quaternion.identity;
        EnsurePhysicsComponents();
        EnsureVisualComponentsIfMissing();
        TryBindGameManager();
    }

    private void Start()
    {
        EnsurePhysicsComponents();
        EnsureVisualComponentsIfMissing();
        HealthChanged?.Invoke(currentHealth, maxHealth);
    }

    private void OnDisable()
    {
        UnbindGameManager();
        if (body != null) body.linearVelocity = Vector2.zero;
    }

    private void OnDestroy()
    {
        UnbindGameManager();
        foreach (Projectile projectile in activeProjectiles)
        {
            if (projectile == null) continue;
            projectile.FirstRicochet -= HandleProjectileFirstRicochet;
            projectile.Destroyed -= HandleProjectileDestroyed;
        }
        activeProjectiles.Clear();
        if (generatedSprite != null) Destroy(generatedSprite);
        if (generatedTexture != null) Destroy(generatedTexture);
    }

    private void Update()
    {
        if (boundGameManager == null) TryBindGameManager();
        if (isDead || GameplayRoomReveal.IsRevealing)
        {
            movementInput = Vector2.zero;
            return;
        }
        ReadMovementInput();
        UpdateAimDirection();
        if (GameManager.Instance != null && GameManager.Instance.IsGameplayPaused) return;
        ReadActionInput();
    }

    private void FixedUpdate()
    {
        if (GameplayRoomReveal.IsRevealing)
        {
            movementInput = Vector2.zero;
            if (body != null) body.linearVelocity = Vector2.zero;
            return;
        }
        if (body == null)
        {
            EnsurePhysicsComponents();
            if (body == null)
            {
                // Last-resort movement fallback. This should never be needed with
                // RequireComponent, but keeps the player controllable if Unity strips
                // a 2D physics component during project migration/import.
                transform.position += (Vector3)(movementInput * movementSpeed * Time.fixedDeltaTime);
                return;
            }
        }

        if (isDead)
        {
            body.linearVelocity = Vector2.zero;
            return;
        }

        Vector2 wantedVelocity = Time.time < temporaryMovementVelocityEndTime
            ? temporaryMovementVelocity
            : movementInput * movementSpeed;

        body.linearVelocity = wantedVelocity;
    }

    private void ReadMovementInput()
    {
        float x = 0f, y = 0f;
        if (Input.GetKey(GetBinding(GameManager.BindingAction.MoveLeft, KeyCode.A))) x -= 1f;
        if (Input.GetKey(GetBinding(GameManager.BindingAction.MoveRight, KeyCode.D))) x += 1f;
        if (Input.GetKey(GetBinding(GameManager.BindingAction.MoveDown, KeyCode.S))) y -= 1f;
        if (Input.GetKey(GetBinding(GameManager.BindingAction.MoveUp, KeyCode.W))) y += 1f;
        movementInput = new Vector2(x, y);
        if (movementInput.sqrMagnitude > 1f) movementInput.Normalize();
    }

    private void ReadActionInput()
    {
        // Unity 6.6 hardening: keep the configurable legacy binding, but always
        // accept the documented default mouse/space controls as a fallback.
        bool fireHeld = Input.GetMouseButton(0) ||
                        Input.GetKey(GetBinding(GameManager.BindingAction.Fire, KeyCode.Mouse0));
        if (fireHeld) TryFireProjectile();

        if (Input.GetKeyDown(GetBinding(GameManager.BindingAction.Interact, KeyCode.E)))
            InteractPressed?.Invoke(this);

        bool activePressed = Input.GetKeyDown(KeyCode.Space) ||
                             Input.GetKeyDown(GetBinding(GameManager.BindingAction.UseActiveItem, KeyCode.Space));
        if (activePressed) TryUseSelectedActiveItem();

        bool swapPressed = Input.GetKeyDown(KeyCode.Q) ||
                           Input.GetKeyDown(GetBinding(GameManager.BindingAction.SwapActiveItem, KeyCode.Q));
        if (swapPressed) SwapActiveItemSlot();
    }

    private KeyCode GetBinding(GameManager.BindingAction action, KeyCode fallback)
    {
        if (boundGameManager != null) return boundGameManager.GetBinding(action);
        return GameManager.Instance != null ? GameManager.Instance.GetBinding(action) : fallback;
    }

    private void UpdateAimDirection()
    {
        Camera camera = Camera.main;
        if (camera == null) return;
        Vector3 world = camera.ScreenToWorldPoint(Input.mousePosition);
        Vector2 direction = (Vector2)world - (Vector2)transform.position;
        if (direction.sqrMagnitude < 0.0001f) return;
        // Top-down aiming follows the cursor directly. No hidden or persisted
        // sensitivity setting affects it anymore.
        // Cursor direction controls projectiles, NOT the character's sprite rotation.
        // Keep the character upright while it moves in top-down 2D space.
        aimDirection = direction.normalized;
        transform.rotation = Quaternion.identity;
    }

    public bool TryFireProjectile()
    {
        if (GameplayRoomReveal.IsRevealing || isDead || Time.time < nextFireTime) return false;
        PruneDestroyedProjectiles();
        if (activeProjectiles.Count >= maximumActiveProjectiles || aimDirection.sqrMagnitude <= 0.0001f) return false;

        Vector2 spawnPosition = (Vector2)transform.position + aimDirection.normalized * projectileSpawnOffset;
        GameObject projectileObject = new GameObject("Player Projectile");

        // Add the physics components before the script. This avoids the same Unity 6.6
        // runtime AddComponent timing issue that previously affected the Player/Enemy.
        Rigidbody2D projectileBody = projectileObject.AddComponent<Rigidbody2D>();
        projectileBody.bodyType = RigidbodyType2D.Dynamic;
        projectileBody.gravityScale = 0f;
        projectileBody.linearDamping = 0f;
        projectileBody.angularDamping = 0f;
        projectileBody.freezeRotation = true;
        projectileBody.collisionDetectionMode = CollisionDetectionMode2D.Continuous;
        projectileBody.interpolation = RigidbodyInterpolation2D.Interpolate;

        CircleCollider2D projectileCircle = projectileObject.AddComponent<CircleCollider2D>();
        projectileCircle.isTrigger = false;
        projectileCircle.radius = 0.115f;
        projectileObject.AddComponent<SpriteRenderer>();

        Projectile projectile = projectileObject.AddComponent<Projectile>();
        projectile.Initialize(this, spawnPosition, aimDirection, projectileSpeed, projectileDamage, projectileLifetime);
        projectile.FirstRicochet += HandleProjectileFirstRicochet;
        projectile.Destroyed += HandleProjectileDestroyed;
        activeProjectiles.Add(projectile);
        try
        {
            ProjectileFired?.Invoke(projectile);
        }
        catch (Exception eventException)
        {
            // Audio/UI listeners are optional and must never cancel a valid shot.
            Debug.LogException(eventException, this);
        }
        nextFireTime = Time.time + Mathf.Max(0.01f, fireInterval);
        return true;
    }

    private void HandleProjectileFirstRicochet(Projectile projectile, Vector2 reflectedDirection) => ProjectileFirstRicochet?.Invoke(projectile, reflectedDirection);

    private void HandleProjectileDestroyed(Projectile projectile)
    {
        if (projectile == null) return;
        projectile.FirstRicochet -= HandleProjectileFirstRicochet;
        projectile.Destroyed -= HandleProjectileDestroyed;
        activeProjectiles.Remove(projectile);
    }

    private void PruneDestroyedProjectiles() => activeProjectiles.RemoveWhere(p => p == null);

    public bool ReceiveDamage(float amount, GameObject source) => TakeDamage(amount, source);

    public bool TakeDamage(float amount, GameObject source = null)
    {
        if (amount <= 0f || isDead || isInvulnerable) return false;
        float finalDamage = Mathf.Max(0f, amount * incomingDamageMultiplier);
        if (finalDamage <= 0f) return false;
        currentHealth = Mathf.Max(0f, currentHealth - finalDamage);
        ApplyBackpackStowedChargePenalty();
        RuntimeVfx.SpawnCircularBurst(transform.position, new Color(0.38f, 0.015f, 0.02f, 1f), 13, 0.18f, 3.4f, 0.48f);
        HealthChanged?.Invoke(currentHealth, maxHealth);

        if (currentHealth <= 0f)
        {
            if (TryInterceptDeath())
            {
                currentHealth = Mathf.Clamp(currentHealth <= 0f ? 1f : currentHealth, 0.01f, maxHealth);
                HealthChanged?.Invoke(currentHealth, maxHealth);
                BeginInvulnerability();
                return true;
            }
            Die();
            return true;
        }
        BeginInvulnerability();
        return true;
    }

    private void BeginInvulnerability()
    {
        if (invulnerabilityCoroutine != null) StopCoroutine(invulnerabilityCoroutine);
        invulnerabilityCoroutine = StartCoroutine(InvulnerabilityRoutine());
    }

    private IEnumerator InvulnerabilityRoutine()
    {
        isInvulnerable = true;
        float finish = Time.unscaledTime + Mathf.Max(0f, invulnerabilityDuration);
        bool visible = true;
        while (Time.unscaledTime < finish)
        {
            visible = !visible;
            if (spriteRenderer != null) spriteRenderer.enabled = visible;
            yield return new WaitForSecondsRealtime(Mathf.Max(0.015f, flashInterval));
        }
        if (spriteRenderer != null) spriteRenderer.enabled = true;
        isInvulnerable = false;
        invulnerabilityCoroutine = null;
    }

    private bool TryInterceptDeath()
    {
        Func<PlayerController, bool>[] snapshot = deathInterceptors.ToArray();
        for (int i = 0; i < snapshot.Length; i++)
        {
            try { if (snapshot[i] != null && snapshot[i](this)) return true; }
            catch (Exception e) { Debug.LogException(e, this); }
        }
        return false;
    }

    private void Die()
    {
        if (isDead) return;
        isDead = true;
        isInvulnerable = false;
        movementInput = Vector2.zero;
        if (invulnerabilityCoroutine != null) { StopCoroutine(invulnerabilityCoroutine); invulnerabilityCoroutine = null; }
        if (spriteRenderer != null) spriteRenderer.enabled = true;
        if (body != null) body.linearVelocity = Vector2.zero;
        PlayerDied?.Invoke();
        // The sound plays only after death interceptors (including Resurrection) fail.
        if (AudioManager.Instance != null)
            AudioManager.Instance.PlaySfx(AudioManager.SfxId.PlayerDeath);
        StartCoroutine(DeathSequenceRoutine());
    }

    private IEnumerator DeathSequenceRoutine()
    {
        if (body != null)
        {
            body.linearVelocity = Vector2.zero;
            body.angularVelocity = 0f;
            body.simulated = false;
        }

        RuntimePlayerEightDirectionAnimator eightWayAnimator = GetComponent<RuntimePlayerEightDirectionAnimator>();
        if (eightWayAnimator != null) eightWayAnimator.enabled = false;
        Animator unityAnimator = GetComponent<Animator>();
        if (unityAnimator != null) unityAnimator.enabled = false;

        Sprite originalSprite = spriteRenderer != null ? spriteRenderer.sprite : null;
        Vector3 originalScale = transform.localScale;
        Sprite deathFrame0 = Resources.Load<Sprite>("GameArt/AnimationFrames/Player/Death_00");
        Sprite deathFrame1 = Resources.Load<Sprite>("GameArt/AnimationFrames/Player/Death_01");
        float deathScale0 = 1f;
        float deathScale1 = 1f;
        if (originalSprite != null && deathFrame0 != null)
            deathScale0 = Mathf.Min(originalSprite.bounds.size.x / Mathf.Max(0.01f, deathFrame0.bounds.size.x), originalSprite.bounds.size.y / Mathf.Max(0.01f, deathFrame0.bounds.size.y));
        if (originalSprite != null && deathFrame1 != null)
            deathScale1 = Mathf.Min(originalSprite.bounds.size.x / Mathf.Max(0.01f, deathFrame1.bounds.size.x), originalSprite.bounds.size.y / Mathf.Max(0.01f, deathFrame1.bounds.size.y));
        if (spriteRenderer != null && deathFrame0 != null) { spriteRenderer.sprite = deathFrame0; transform.localScale = originalScale * deathScale0 * 2f; }

        Canvas fadeCanvas = RuntimeUIFactory.CreateCanvas(transform, "Death Presentation Canvas", 140);
        fadeCanvas.transform.SetParent(null, false);
        DontDestroyOnLoad(fadeCanvas.gameObject);
        GameObject panel = RuntimeUIFactory.CreatePanel(fadeCanvas.transform, "Death Fade", Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero, new Color(0f, 0f, 0f, 0f));
        Image fadeImage = panel.GetComponent<Image>();
        fadeImage.raycastTarget = false;

        Camera cam = Camera.main;
        float originalSize = cam != null ? cam.orthographicSize : 5f;
        float targetSize = Mathf.Max(2.4f, originalSize * 0.72f);

        // Match the supplied ~2.4 s death sound before switching to defeat music.
        float totalDuration = 2.40f;
        float elapsed = 0f;
        bool secondFrameShown = false;
        while (elapsed < totalDuration)
        {
            elapsed += Time.unscaledDeltaTime;
            float t = Mathf.Clamp01(elapsed / totalDuration);
            if (!secondFrameShown && elapsed >= 0.20f)
            {
                secondFrameShown = true;
                if (spriteRenderer != null && deathFrame1 != null) { spriteRenderer.sprite = deathFrame1; transform.localScale = originalScale * deathScale1 * 2f; }
            }

            if (cam != null)
            {
                cam.orthographicSize = Mathf.Lerp(originalSize, targetSize, Mathf.SmoothStep(0f, 1f, t));
                Vector3 pos = cam.transform.position;
                cam.transform.position = new Vector3(transform.position.x, transform.position.y, pos.z);
            }

            if (fadeImage != null)
            {
                float alpha = Mathf.Lerp(0f, 0.62f, Mathf.SmoothStep(0f, 1f, t));
                fadeImage.color = new Color(0f, 0f, 0f, alpha);
            }
            yield return null;
        }

        if (GameManager.Instance != null) GameManager.Instance.HandlePlayerDeath();

        if (cam != null) cam.orthographicSize = originalSize;
        if (fadeCanvas != null) Destroy(fadeCanvas.gameObject);
    }

    public bool Heal(float amount)
    {
        if (amount <= 0f || isDead || currentHealth >= maxHealth) return false;
        float old = currentHealth;
        currentHealth = Mathf.Min(maxHealth, currentHealth + amount);
        if (Mathf.Approximately(old, currentHealth)) return false;
        HealthChanged?.Invoke(currentHealth, maxHealth);
        return true;
    }

    public void IncreaseMaxHealth(float amount, bool healAddedHealth = true)
    {
        if (amount <= 0f) return;
        maxHealth += amount;
        if (healAddedHealth) currentHealth = Mathf.Min(maxHealth, currentHealth + amount);
        HealthChanged?.Invoke(currentHealth, maxHealth);
        StatsChanged?.Invoke();
    }

    public void ModifyProjectileDamage(float delta) { projectileDamage = Mathf.Max(0f, projectileDamage + delta); StatsChanged?.Invoke(); }
    public void SetProjectileDamage(float value) { projectileDamage = Mathf.Max(0f, value); StatsChanged?.Invoke(); }
    public void ModifyMovementSpeed(float delta) { movementSpeed = Mathf.Max(0.1f, movementSpeed + delta); StatsChanged?.Invoke(); }
    public void SetIncomingDamageMultiplier(float multiplier) { incomingDamageMultiplier = Mathf.Max(0f, multiplier); StatsChanged?.Invoke(); }

    public bool ApplyDash(Vector2 direction, float dashSpeed, float dashDuration)
    {
        if (isDead) return false;
        if (direction.sqrMagnitude <= 0.0001f) direction = aimDirection;
        if (direction.sqrMagnitude <= 0.0001f) return false;
        temporaryMovementVelocity = direction.normalized * Mathf.Max(0.1f, dashSpeed);
        temporaryMovementVelocityEndTime = Time.time + Mathf.Max(0.02f, dashDuration);
        RuntimePlayerEightDirectionAnimator dashAnimator = GetComponent<RuntimePlayerEightDirectionAnimator>();
        if (dashAnimator != null)
            dashAnimator.PlayDash(direction, Mathf.Max(0.02f, dashDuration));
        return true;
    }

    public void RegisterDeathInterceptor(Func<PlayerController, bool> interceptor)
    {
        if (interceptor != null && !deathInterceptors.Contains(interceptor)) deathInterceptors.Add(interceptor);
    }
    public void UnregisterDeathInterceptor(Func<PlayerController, bool> interceptor) { if (interceptor != null) deathInterceptors.Remove(interceptor); }

    public void SetActiveItemCapacity(int capacity)
    {
        int clamped = Mathf.Clamp(capacity, 1, 2);
        if (activeItemCapacity == clamped) return;
        activeItemCapacity = clamped;
        if (activeItemCapacity == 1)
        {
            selectedActiveSlot = 0;
            if (!activeItemSlots[1].IsEmpty) activeItemSlots[1].Clear();
        }
        InventoryChanged?.Invoke();
        SelectedActiveSlotChanged?.Invoke(selectedActiveSlot);
    }

    public bool TryEquipActiveItem(string itemId, string displayName, int requiredKills, bool startCharged = true)
    {
        if (string.IsNullOrWhiteSpace(itemId)) return false;
        for (int i = 0; i < activeItemCapacity; i++)
        {
            if (!activeItemSlots[i].IsEmpty) continue;
            activeItemSlots[i].Assign(itemId, displayName, requiredKills, startCharged);
            if (activeItemSlots[selectedActiveSlot].IsEmpty) { selectedActiveSlot = i; SelectedActiveSlotChanged?.Invoke(selectedActiveSlot); }
            InventoryChanged?.Invoke();
            return true;
        }
        return false;
    }

    public bool EquipOrSwapActiveItem(
        string itemId,
        string displayName,
        int requiredKills,
        int startingStoredKills,
        int startingUsesRemaining,
        out ActiveItemSnapshot droppedItem)
    {
        droppedItem = default;

        if (string.IsNullOrWhiteSpace(itemId))
            return false;

        int targetSlot = selectedActiveSlot;

        // With Backpack, prefer an empty slot before replacing an occupied one.
        if (!activeItemSlots[targetSlot].IsEmpty)
        {
            for (int i = 0; i < activeItemCapacity; i++)
            {
                if (activeItemSlots[i].IsEmpty)
                {
                    targetSlot = i;
                    break;
                }
            }
        }

        ActiveItemSlot slot = activeItemSlots[targetSlot];

        if (!slot.IsEmpty)
        {
            droppedItem = new ActiveItemSnapshot
            {
                ItemId = slot.ItemId,
                DisplayName = slot.DisplayName,
                RequiredKills = slot.RequiredKills,
                StoredKills = slot.StoredKills,
                UsesRemaining = slot.UsesRemaining
            };
        }

        slot.AssignWithCharge(
            itemId,
            displayName,
            requiredKills,
            startingStoredKills < 0 ? Mathf.Max(1, requiredKills) : startingStoredKills,
            startingUsesRemaining);

        selectedActiveSlot = targetSlot;
        InventoryChanged?.Invoke();
        SelectedActiveSlotChanged?.Invoke(selectedActiveSlot);
        return true;
    }

    public bool RemoveSelectedActiveItem(out string removedItemId)
    {
        ActiveItemSlot slot = activeItemSlots[selectedActiveSlot];
        if (slot.IsEmpty) { removedItemId = string.Empty; return false; }
        removedItemId = slot.ItemId;
        slot.Clear();
        SelectFirstOccupiedSlot();
        InventoryChanged?.Invoke();
        SelectedActiveSlotChanged?.Invoke(selectedActiveSlot);
        return true;
    }

    public ActiveItemSlot GetActiveItemSlot(int index)
    {
        if (index < 0 || index >= activeItemSlots.Length) throw new ArgumentOutOfRangeException(nameof(index));
        return activeItemSlots[index];
    }

    public void SwapActiveItemSlot()
    {
        if (activeItemCapacity < 2) return;
        int other = selectedActiveSlot == 0 ? 1 : 0;
        if (activeItemSlots[other].IsEmpty) return;
        selectedActiveSlot = other;
        SelectedActiveSlotChanged?.Invoke(selectedActiveSlot);
    }

    private void SelectFirstOccupiedSlot()
    {
        for (int i = 0; i < activeItemCapacity; i++) if (!activeItemSlots[i].IsEmpty) { selectedActiveSlot = i; return; }
        selectedActiveSlot = 0;
    }

    public void RegisterActiveItemBehaviour(string itemId, Func<PlayerController, bool> behaviour)
    {
        if (string.IsNullOrWhiteSpace(itemId)) throw new ArgumentException("Active item ID cannot be empty.", nameof(itemId));
        if (behaviour == null) throw new ArgumentNullException(nameof(behaviour));
        activeItemBehaviours[itemId] = behaviour;
    }
    public void UnregisterActiveItemBehaviour(string itemId) { if (!string.IsNullOrWhiteSpace(itemId)) activeItemBehaviours.Remove(itemId); }

    public bool TryUseSelectedActiveItem()
    {
        if (GameplayRoomReveal.IsRevealing || isDead) return false;
        ActiveItemSlot slot = activeItemSlots[selectedActiveSlot];
        if (slot.IsEmpty || !slot.IsCharged) return false;

        // Re-register active behaviours on demand. This makes active items resilient
        // to scene transitions and manager/player creation order in Unity 6.6.
        if (ItemSystem.Instance != null)
            ItemSystem.Instance.EnsurePlayerBinding(this);

        if (!activeItemBehaviours.TryGetValue(slot.ItemId, out Func<PlayerController, bool> behaviour))
        {
            Debug.LogWarning("Active item behaviour is not registered for: " + slot.ItemId, this);
            return false;
        }
        bool ok;
        try { ok = behaviour(this); } catch (Exception e) { Debug.LogException(e, this); return false; }
        if (!ok) return false;
        slot.ConsumeCharge();
        InventoryChanged?.Invoke();
        return true;
    }

    private void ApplyBackpackStowedChargePenalty()
    {
        if (activeItemCapacity < 2 || activeItemSlots[0].IsEmpty || activeItemSlots[1].IsEmpty) return;
        int stowed = selectedActiveSlot == 0 ? 1 : 0;
        if (activeItemSlots[stowed].ReduceCharge(1)) InventoryChanged?.Invoke();
    }

    private void HandleEnemyKilled()
    {
        bool changed = false;
        for (int i = 0; i < activeItemCapacity; i++) if (activeItemSlots[i].AddKillCharge(1)) changed = true;
        if (changed) InventoryChanged?.Invoke();
    }

    private void TryBindGameManager()
    {
        if (boundGameManager != null || GameManager.Instance == null) return;
        boundGameManager = GameManager.Instance;
        boundGameManager.EnemyKilled += HandleEnemyKilled;
    }

    private void UnbindGameManager()
    {
        if (boundGameManager == null) return;
        boundGameManager.EnemyKilled -= HandleEnemyKilled;
        boundGameManager = null;
    }

    private void EnsureInventoryArray()
    {
        if (activeItemSlots == null || activeItemSlots.Length != 2) activeItemSlots = new ActiveItemSlot[2];
        for (int i = 0; i < 2; i++) if (activeItemSlots[i] == null) activeItemSlots[i] = new ActiveItemSlot();
    }

    public void EnsureRuntimeReady()
    {
        EnsurePhysicsComponents();
        EnsureVisualComponentsIfMissing();
    }

    private void EnsurePhysicsComponents()
    {
        body = GetComponent<Rigidbody2D>();
        if (body == null)
        {
            body = gameObject.AddComponent<Rigidbody2D>();
        }

        body.bodyType = RigidbodyType2D.Dynamic;
        body.gravityScale = 0f;
        body.linearDamping = 0f;
        body.angularDamping = 0f;
        body.freezeRotation = true;
        body.collisionDetectionMode = CollisionDetectionMode2D.Continuous;
        body.interpolation = RigidbodyInterpolation2D.Interpolate;

        bodyCollider = GetComponent<CircleCollider2D>();
        if (bodyCollider == null)
        {
            bodyCollider = gameObject.AddComponent<CircleCollider2D>();
        }

        bodyCollider.isTrigger = false;
        bodyCollider.offset = Vector2.zero;
        bodyCollider.radius = 0.43f;
    }

    private void EnsureVisualComponentsIfMissing()
    {
        spriteRenderer = GetComponent<SpriteRenderer>();
        if (spriteRenderer == null)
        {
            spriteRenderer = gameObject.AddComponent<SpriteRenderer>();
        }

        if (spriteRenderer.sprite != null)
        {
            spriteRenderer.sortingOrder = 16;
            return;
        }

        if (generatedSprite != null)
        {
            spriteRenderer.sprite = generatedSprite;
            spriteRenderer.sortingOrder = 16;
            return;
        }

        // Approved player pixel art replaces the old metal circle when available.
        Sprite importedPlayer = RuntimeGameArt.Load("Characters/Player");
        if (importedPlayer != null)
        {
            spriteRenderer.sprite = importedPlayer;
            spriteRenderer.color = Color.white;
            spriteRenderer.sortingOrder = 16;
            RuntimeGameArt.AttachCharacterAnimator(gameObject, "Player");
            return;
        }
        generatedTexture = GeneratePlayerTexture(64);
        generatedSprite = Sprite.Create(generatedTexture, new Rect(0f, 0f, 64f, 64f), new Vector2(0.5f, 0.5f), 64f, 0, SpriteMeshType.FullRect);
        generatedSprite.name = "Runtime Player Sprite";
        spriteRenderer.sprite = generatedSprite;
        spriteRenderer.color = Color.white;
        spriteRenderer.sortingOrder = 16;
    }

    private void EnsureVisualComponents()
    {
        EnsureVisualComponentsIfMissing();
    }

    private static Texture2D GeneratePlayerTexture(int size)
    {
        Texture2D texture = new Texture2D(size, size, TextureFormat.RGBA32, false) { name = "Runtime Player Texture", filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp };
        Color[] pixels = new Color[size * size];
        float center = (size - 1) * 0.5f, radius = size * 0.455f;
        Color visor = new Color(0.035f, 0.04f, 0.045f, 1f);
        for (int y = 0; y < size; y++)
        for (int x = 0; x < size; x++)
        {
            float dx = x - center, dy = y - center, distance = Mathf.Sqrt(dx * dx + dy * dy);
            int index = y * size + x;
            if (distance > radius) { pixels[index] = Color.clear; continue; }
            float nx = dx / radius, ny = dy / radius, nd = distance / radius;
            float radial = Mathf.Lerp(0.78f, 0.46f, nd);
            float brightness = radial + Mathf.Clamp01((ny + 1f) * 0.5f) * 0.08f;
            Color metal = new Color(brightness * 0.82f, brightness * 0.87f, brightness * 0.91f, 1f);
            if (nd > 0.91f) metal = new Color(0.16f, 0.18f, 0.20f, 1f);
            bool visorPixel = Mathf.Abs(ny - (0.17f * nx + 0.04f)) < 0.075f && nx > -0.64f && nx < 0.67f;
            pixels[index] = visorPixel ? visor : metal;
        }
        texture.SetPixels(pixels); texture.Apply(false, false); return texture;
    }
}
