using System.Collections.Generic;
using UnityEngine;

/// <summary>Loads the approved, imported pixel-art sprites from Resources/GameArt.
/// Missing imports return null, allowing the old procedural fallback.</summary>
public static class RuntimeGameArt
{
    private static readonly Dictionary<string, Sprite> Sprites = new Dictionary<string, Sprite>();

    public static Sprite Load(string relativePath)
    {
        if (string.IsNullOrEmpty(relativePath)) return null;
        if (Sprites.TryGetValue(relativePath, out Sprite cached)) return cached;
        Sprite result = Resources.Load<Sprite>("GameArt/" + relativePath);
        Sprites[relativePath] = result;
        if (result == null) Debug.LogWarning("Imported art sprite not found: GameArt/" + relativePath + ". Check Texture Import Settings.");
        return result;
    }

    public static void AttachCharacterAnimator(GameObject actor, string characterId)
    {
        if (actor == null || string.IsNullOrEmpty(characterId)) return;

        if (characterId == "Player" && RuntimePlayerEightDirectionAnimator.AssetsAvailable())
        {
            RuntimePlayerEightDirectionAnimator driver =
                actor.GetComponent<RuntimePlayerEightDirectionAnimator>();
            if (driver == null) actor.AddComponent<RuntimePlayerEightDirectionAnimator>();
            return;
        }

        if (RuntimeBossEightDirectionAnimator.AssetsAvailable(characterId))
        {
            RuntimeBossEightDirectionAnimator driver =
                actor.GetComponent<RuntimeBossEightDirectionAnimator>();
            if (driver == null) driver = actor.AddComponent<RuntimeBossEightDirectionAnimator>();
            driver.Configure(characterId);
            return;
        }

        if (RuntimeEnemyEightDirectionAnimator.AssetsAvailable(characterId))
        {
            RuntimeEnemyEightDirectionAnimator driver =
                actor.GetComponent<RuntimeEnemyEightDirectionAnimator>();
            if (driver == null) driver = actor.AddComponent<RuntimeEnemyEightDirectionAnimator>();
            driver.Configure(characterId);
            return;
        }

        Debug.LogWarning("[HuliganiGames] Missing final character animations: " + characterId);
    }

    public static GameObject SpawnDecor(string spritePath, Transform parent, Vector2 position, float worldWidth,
        int order, bool solid = false)
    {
        Sprite s = Load(spritePath);
        if (s == null) return null;
        GameObject obj = new GameObject("Art " + spritePath.Replace('/', ' '));
        obj.transform.SetParent(parent, false);
        obj.transform.position = new Vector3(position.x, position.y, 0f);
        float scale = Mathf.Max(0.08f, worldWidth / Mathf.Max(0.001f, s.bounds.size.x));
        obj.transform.localScale = Vector3.one * scale;
        SpriteRenderer sr = obj.AddComponent<SpriteRenderer>();
        sr.sprite = s;
        sr.sortingOrder = order;
        if (solid)
        {
            Rigidbody2D rb = obj.AddComponent<Rigidbody2D>();
            rb.bodyType = RigidbodyType2D.Static;
            rb.gravityScale = 0f;
            BoxCollider2D collider = obj.AddComponent<BoxCollider2D>();
            // Low obstacle footprint: hitboxes cover the physical base, not transparent image or decoration.
            collider.size = new Vector2(s.bounds.size.x * 0.70f, s.bounds.size.y * 0.54f);
            collider.offset = new Vector2(0f, -s.bounds.size.y * 0.15f);
        }
        return obj;
    }
}
