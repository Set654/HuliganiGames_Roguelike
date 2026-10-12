using System;
using UnityEngine;

public interface ICombatDamageable
{
    bool ReceiveDamage(float amount, GameObject source);
}

[DisallowMultipleComponent]
[RequireComponent(typeof(Rigidbody2D))]
[RequireComponent(typeof(CircleCollider2D))]
[RequireComponent(typeof(SpriteRenderer))]
public sealed class Projectile : MonoBehaviour
{
    private PlayerController owner;
    private Rigidbody2D body;
    private CircleCollider2D projectileCollider;
    private SpriteRenderer spriteRenderer;
    private Texture2D generatedTexture;
    private Sprite generatedSprite;
    private PhysicsMaterial2D generatedPhysicsMaterial;
    private Collider2D[] ownerColliders = Array.Empty<Collider2D>();
    private Vector2 lastTravelDirection = Vector2.right;
    private float baseDamage;
    private float currentDamage;
    private float movementSpeed;
    private float maximumLifetime;
    private float elapsedLifetime;
    private int ricochetCount;
    private bool initialized;
    private bool destructionRequested;
    private bool ownerCollisionEnabled;

    private readonly Color initialProjectileColor = new Color(0.50f, 0.035f, 0.055f, 1f);
    private readonly Color reflectedProjectileColor = new Color(1f, 0.17f, 0.12f, 1f);

    public PlayerController Owner => owner;
    public int RicochetCount => ricochetCount;
    public bool HasRicocheted => ricochetCount > 0;
    public float CurrentDamage => currentDamage;

    public event Action<Projectile, Vector2> FirstRicochet;
    public event Action<Projectile> Destroyed;

    private void Awake()
    {
        EnsurePhysicsComponents();
        EnsureVisualComponents();
    }

    private void Update()
    {
        if (!initialized || destructionRequested) return;
        elapsedLifetime += Time.deltaTime;
        if (elapsedLifetime >= maximumLifetime) { RequestDestroy(); return; }
        Vector3 p = transform.position;
        // An unlocked wall door uses a trigger instead of a solid collider.
        // Destroy dagger shots at the inner room bounds before they could
        // escape into the off-map area through that opening.
        if (Mathf.Abs(p.x) >= 7.42f || Mathf.Abs(p.y) >= 4.02f)
            RequestDestroy();
    }

    private void FixedUpdate()
    {
        if (GameplayRoomReveal.IsRevealing)
        {
            if (body != null) body.linearVelocity = Vector2.zero;
            return;
        }
        if (!initialized || destructionRequested || body == null) return;
        Vector2 velocity = body.linearVelocity;
        if (velocity.sqrMagnitude > 0.0001f) lastTravelDirection = velocity.normalized;
        if (lastTravelDirection.sqrMagnitude <= 0.0001f) lastTravelDirection = Vector2.right;
        body.linearVelocity = lastTravelDirection * movementSpeed;
    }

    public void Initialize(PlayerController projectileOwner, Vector2 spawnPosition, Vector2 direction, float speed, float damage, float lifetime)
    {
        owner = projectileOwner;
        movementSpeed = Mathf.Max(0.1f, speed);
        baseDamage = Mathf.Max(0f, damage);
        currentDamage = baseDamage;
        maximumLifetime = Mathf.Max(0.1f, lifetime);
        ricochetCount = 0;
        elapsedLifetime = 0f;
        Vector2 safeDirection = direction.sqrMagnitude > 0.0001f ? direction.normalized : Vector2.right;
        lastTravelDirection = safeDirection;
        transform.position = spawnPosition;
        transform.rotation = Quaternion.Euler(0f, 0f, Mathf.Atan2(safeDirection.y, safeDirection.x) * Mathf.Rad2Deg);
        spriteRenderer.color = RuntimeGameArt.Load("Projectiles/Dagger_Right") != null ? Color.white : initialProjectileColor;
        CacheOwnerColliders();
        SetOwnerCollisionIgnored(true);
        body.linearVelocity = safeDirection * movementSpeed;
        initialized = true;
    }

    private void OnCollisionEnter2D(Collision2D collision)
    {
        if (!initialized || destructionRequested || collision == null || collision.collider == null) return;

        Projectile otherProjectile = collision.collider.GetComponent<Projectile>();
        if (otherProjectile != null && otherProjectile != this)
        {
            Physics2D.IgnoreCollision(projectileCollider, collision.collider, true);
            return;
        }

        if (IsOwnerCollision(collision.collider)) { HandleOwnerCollision(); return; }

        ICombatDamageable damageable = FindDamageable(collision.collider);
        if (damageable != null) { HandleDamageableCollision(damageable); return; }

        HandleSurfaceCollision(collision);
    }

    private void HandleOwnerCollision()
    {
        if (!HasRicocheted || owner == null) return;
        owner.ReceiveDamage(1f, gameObject);
        RuntimeVfx.SpawnCircularBurst(transform.position, new Color(0.55f, 0.02f, 0.025f, 1f), 8, 0.11f, 2.3f, 0.32f);
        RequestDestroy();
    }

    private void HandleDamageableCollision(ICombatDamageable damageable)
    {
        damageable.ReceiveDamage(currentDamage, gameObject);
        RuntimeVfx.SpawnCircularBurst(transform.position, HasRicocheted ? reflectedProjectileColor : initialProjectileColor, 7, 0.10f, 2f, 0.28f);
        RequestDestroy();
    }

    private void HandleSurfaceCollision(Collision2D collision)
    {
        if (ricochetCount >= 1)
        {
            RuntimeVfx.SpawnCircularBurst(transform.position, reflectedProjectileColor, 9, 0.10f, 2.5f, 0.32f);
            RequestDestroy();
            return;
        }

        Vector2 normal = GetBestCollisionNormal(collision);
        Vector2 incoming = lastTravelDirection.sqrMagnitude > 0.0001f ? lastTravelDirection.normalized : body.linearVelocity.normalized;
        if (incoming.sqrMagnitude <= 0.0001f) incoming = Vector2.right;

        Vector2 reflected = Vector2.Reflect(incoming, normal);
        if (Vector2.Dot(reflected, normal) < 0.01f)
        {
            reflected -= normal * Vector2.Dot(reflected, normal);
            reflected += normal * 0.08f;
        }
        if (reflected.sqrMagnitude <= 0.0001f) reflected = -incoming;
        reflected.Normalize();

        ricochetCount = 1;
        currentDamage = Mathf.Max(2f, baseDamage * 2f);
        lastTravelDirection = reflected;
        spriteRenderer.color = reflectedProjectileColor;
        EnableOwnerCollisionAfterRicochet();
        FirstRicochet?.Invoke(this, reflected);

        // Passive effects such as Angel Lens may consume the original projectile
        // during the ricochet event.
        if (destructionRequested)
            return;

        body.position += reflected * 0.035f;
        body.linearVelocity = reflected * movementSpeed;
        transform.rotation = Quaternion.Euler(0f, 0f, Mathf.Atan2(reflected.y, reflected.x) * Mathf.Rad2Deg);
        if (collision.contactCount > 0)
            RuntimeVfx.SpawnCircularBurst(collision.GetContact(0).point, reflectedProjectileColor, 6, 0.075f, 1.8f, 0.22f);
    }

    private static Vector2 GetBestCollisionNormal(Collision2D collision)
    {
        if (collision.contactCount <= 0)
        {
            Vector2 fallback = -collision.relativeVelocity;
            return fallback.sqrMagnitude > 0.0001f ? fallback.normalized : Vector2.up;
        }
        Vector2 sum = Vector2.zero;
        for (int i = 0; i < collision.contactCount; i++) sum += collision.GetContact(i).normal;
        return sum.sqrMagnitude <= 0.0001f ? collision.GetContact(0).normal : sum.normalized;
    }

    private static ICombatDamageable FindDamageable(Collider2D hitCollider)
    {
        if (hitCollider == null) return null;
        MonoBehaviour[] behaviours = hitCollider.GetComponentsInParent<MonoBehaviour>();
        for (int i = 0; i < behaviours.Length; i++)
            if (behaviours[i] is ICombatDamageable damageable) return damageable;
        return null;
    }

    private bool IsOwnerCollision(Collider2D hitCollider)
    {
        if (owner == null || hitCollider == null) return false;
        Transform hit = hitCollider.transform;
        return hit == owner.transform || hit.IsChildOf(owner.transform);
    }

    private void CacheOwnerColliders()
    {
        ownerColliders = owner == null ? Array.Empty<Collider2D>() : owner.GetComponentsInChildren<Collider2D>(true);
    }

    private void SetOwnerCollisionIgnored(bool ignored)
    {
        if (projectileCollider == null || ownerColliders == null) return;
        for (int i = 0; i < ownerColliders.Length; i++)
        {
            Collider2D c = ownerColliders[i];
            if (c == null || c == projectileCollider) continue;
            Physics2D.IgnoreCollision(projectileCollider, c, ignored);
        }
        ownerCollisionEnabled = !ignored;
    }

    private void EnableOwnerCollisionAfterRicochet()
    {
        if (!ownerCollisionEnabled) SetOwnerCollisionIgnored(false);
    }

    public void ConsumeAfterRicochet()
    {
        if (destructionRequested)
            return;

        RuntimeVfx.SpawnCircularBurst(
            transform.position,
            new Color(0.94f, 0.85f, 0.52f, 1f),
            8,
            0.07f,
            2.0f,
            0.24f);

        RequestDestroy();
    }

    private void RequestDestroy()
    {
        if (destructionRequested) return;
        destructionRequested = true;
        if (body != null) body.linearVelocity = Vector2.zero;
        Destroy(gameObject);
    }

    private void OnDestroy()
    {
        FirstRicochet = null;
        Destroyed?.Invoke(this);
        Destroyed = null;
        if (generatedSprite != null) Destroy(generatedSprite);
        if (generatedTexture != null) Destroy(generatedTexture);
        if (generatedPhysicsMaterial != null) Destroy(generatedPhysicsMaterial);
    }

    private void EnsurePhysicsComponents()
    {
        body = GetComponent<Rigidbody2D>() ?? gameObject.AddComponent<Rigidbody2D>();
        body.bodyType = RigidbodyType2D.Dynamic;
        body.gravityScale = 0f;
        body.linearDamping = 0f;
        body.angularDamping = 0f;
        body.freezeRotation = true;
        body.collisionDetectionMode = CollisionDetectionMode2D.Continuous;
        body.interpolation = RigidbodyInterpolation2D.Interpolate;

        projectileCollider = GetComponent<CircleCollider2D>() ?? gameObject.AddComponent<CircleCollider2D>();
        projectileCollider.isTrigger = false;
        projectileCollider.offset = Vector2.zero;
        projectileCollider.radius = 0.115f;

        generatedPhysicsMaterial = new PhysicsMaterial2D("Runtime Projectile Physics") { friction = 0f, bounciness = 0f };
        projectileCollider.sharedMaterial = generatedPhysicsMaterial;
    }

    private void EnsureVisualComponents()
    {
        spriteRenderer = GetComponent<SpriteRenderer>() ?? gameObject.AddComponent<SpriteRenderer>();
        Sprite dagger = RuntimeGameArt.Load("Projectiles/Dagger_Right");
        if (dagger != null)
        {
            spriteRenderer.sprite = dagger;
            spriteRenderer.color = Color.white;
            spriteRenderer.sortingOrder = 20;
            CircleCollider2D tipCollider = GetComponent<CircleCollider2D>();
            if (tipCollider != null) tipCollider.offset = new Vector2(0.10f, 0f);
            return;
        }
        generatedTexture = GenerateProjectileTexture(24);
        generatedSprite = Sprite.Create(generatedTexture, new Rect(0f, 0f, 24f, 24f), new Vector2(0.5f, 0.5f), 64f, 0, SpriteMeshType.FullRect);
        generatedSprite.name = "Runtime Projectile Sprite";
        spriteRenderer.sprite = generatedSprite;
        spriteRenderer.color = initialProjectileColor;
        spriteRenderer.sortingOrder = 20;
    }

    private static Texture2D GenerateProjectileTexture(int size)
    {
        Texture2D texture = new Texture2D(size, size, TextureFormat.RGBA32, false) { name = "Runtime Projectile Texture", filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp };
        Color[] pixels = new Color[size * size];
        float center = (size - 1) * 0.5f;
        float radius = size * 0.43f;
        for (int y = 0; y < size; y++)
        for (int x = 0; x < size; x++)
        {
            float dx = x - center, dy = y - center;
            float d = Mathf.Sqrt(dx * dx + dy * dy);
            int index = y * size + x;
            if (d > radius) { pixels[index] = Color.clear; continue; }
            float n = d / radius;
            float b = Mathf.Lerp(1f, 0.56f, n);
            if (n > 0.84f) b *= 0.52f;
            pixels[index] = new Color(b, b, b, 1f);
        }
        texture.SetPixels(pixels); texture.Apply(false, false); return texture;
    }
}

internal static class RuntimeVfx
{
    public static void SpawnCircularBurst(Vector3 worldPosition, Color color, int particleCount, float particleSize, float particleSpeed, float lifetime)
    {
        int count = Mathf.Clamp(particleCount, 1, 128);
        float size = Mathf.Max(0.01f, particleSize);
        float speed = Mathf.Max(0f, particleSpeed);
        float life = Mathf.Max(0.05f, lifetime);

        GameObject effectObject = new GameObject("Runtime Circular Burst");
        effectObject.transform.position = worldPosition;
        ParticleSystem particles = effectObject.AddComponent<ParticleSystem>();
        ParticleSystemRenderer renderer = effectObject.GetComponent<ParticleSystemRenderer>();

        var main = particles.main;
        main.playOnAwake = false;
        main.loop = false;
        main.simulationSpace = ParticleSystemSimulationSpace.World;
        main.startLifetime = new ParticleSystem.MinMaxCurve(life * 0.72f, life);
        main.startSpeed = new ParticleSystem.MinMaxCurve(speed * 0.65f, speed);
        main.startSize = new ParticleSystem.MinMaxCurve(size * 0.65f, size * 1.25f);
        main.startColor = color;
        main.gravityModifier = 0f;
        main.maxParticles = count;

        var emission = particles.emission;
        emission.rateOverTime = 0f;
        emission.SetBursts(new[] { new ParticleSystem.Burst(0f, (short)count) });

        var shape = particles.shape;
        shape.enabled = true;
        shape.shapeType = ParticleSystemShapeType.Circle;
        shape.radius = 0.08f;
        shape.arc = 360f;

        var sizeLife = particles.sizeOverLifetime;
        sizeLife.enabled = true;
        sizeLife.size = new ParticleSystem.MinMaxCurve(1f, new AnimationCurve(new Keyframe(0f, 1f), new Keyframe(0.55f, 0.72f), new Keyframe(1f, 0f)));

        var colorLife = particles.colorOverLifetime;
        colorLife.enabled = true;
        Gradient gradient = new Gradient();
        gradient.SetKeys(new[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(Color.white, 1f) }, new[] { new GradientAlphaKey(1f, 0f), new GradientAlphaKey(0.85f, 0.55f), new GradientAlphaKey(0f, 1f) });
        colorLife.color = gradient;

        Texture2D texture = CreateCircularParticleTexture(16);
        Shader shader = Shader.Find("Sprites/Default") ?? Shader.Find("Universal Render Pipeline/Particles/Unlit");
        Material material = null;
        if (shader != null && renderer != null)
        {
            material = new Material(shader) { name = "Runtime Circular Particle Material", mainTexture = texture };
            renderer.material = material;
        }
        if (renderer != null)
        {
            renderer.renderMode = ParticleSystemRenderMode.Billboard;
            renderer.sortingOrder = 30;
        }
        particles.Play();

        float cleanup = life + 0.35f;
        UnityEngine.Object.Destroy(effectObject, cleanup);
        if (material != null) UnityEngine.Object.Destroy(material, cleanup);
        UnityEngine.Object.Destroy(texture, cleanup);
    }

    private static Texture2D CreateCircularParticleTexture(int size)
    {
        Texture2D texture = new Texture2D(size, size, TextureFormat.RGBA32, false) { name = "Runtime Circular Particle Texture", filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp };
        Color[] pixels = new Color[size * size];
        float center = (size - 1) * 0.5f;
        float radius = size * 0.46f;
        for (int y = 0; y < size; y++)
        for (int x = 0; x < size; x++)
        {
            float dx = x - center, dy = y - center;
            float distance = Mathf.Sqrt(dx * dx + dy * dy);
            float edge = Mathf.Clamp01((radius - distance) / 1.25f);
            pixels[y * size + x] = new Color(1f, 1f, 1f, edge);
        }
        texture.SetPixels(pixels); texture.Apply(false, false); return texture;
    }
}
