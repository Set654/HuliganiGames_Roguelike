using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 8-direction, 8-frame walk animation using the supplied PNG sprites, with no
/// rotation or movement of the Rigidbody2D / hitbox. Shared by Cyclops, Skeleton
/// SkeletonMinion. Idle retains the last facing direction.
/// </summary>
[DisallowMultipleComponent]
[RequireComponent(typeof(SpriteRenderer))]
public sealed class RuntimeEnemyEightDirectionAnimator : MonoBehaviour
{
    private static readonly Dictionary<string, Sprite[][]> SharedFrames =
        new Dictionary<string, Sprite[][]>();

    private static readonly string[] Directions =
    {
        "Down", "DownLeft", "Left", "UpLeft", "Up", "UpRight", "Right", "DownRight"
    };

    [SerializeField, Range(4f, 18f)] private float framesPerSecond = 10f;
    [SerializeField] private float movementThreshold = 0.08f;
    private SpriteRenderer spriteRenderer;
    private Rigidbody2D body;
    private EnemyAI enemy;
    private Sprite[][] frames;
    private int directionIndex;
    private float frameTime;
    private bool ready;
    private string characterId;

    public static bool AssetsAvailable(string character)
    {
        if (character != "Skeleton" && character != "Cyclops" && character != "SkeletonMinion")
            return false;
        return Resources.Load<Sprite>("GameArt/AnimationFrames/Enemies/" + character +
            "/Directional/" + character + "_Down_Walk_00") != null;
    }

    private void Awake()
    {
        spriteRenderer = GetComponent<SpriteRenderer>();
        body = GetComponent<Rigidbody2D>();
        enemy = GetComponent<EnemyAI>();
    }

    public void Configure(string id)
    {
        if (ready && characterId == id) return;
        characterId = id;
        ready = false;
        if (spriteRenderer == null) spriteRenderer = GetComponent<SpriteRenderer>();
        if (body == null) body = GetComponent<Rigidbody2D>();
        if (enemy == null) enemy = GetComponent<EnemyAI>();
        if (SharedFrames.TryGetValue(id, out Sprite[][] cached))
        {
            frames = cached;
            ResetPlayback(id);
            return;
        }

        Sprite[][] loaded = new Sprite[8][];
        for (int d = 0; d < Directions.Length; d++)
        {
            loaded[d] = new Sprite[8];
            for (int f = 0; f < 8; f++)
            {
                string path = "GameArt/AnimationFrames/Enemies/" + id + "/Directional/" +
                    id + "_" + Directions[d] + "_Walk_" + f.ToString("00");
                loaded[d][f] = Resources.Load<Sprite>(path);
                if (loaded[d][f] == null)
                {
                    Debug.LogError("[HuliganiGames] Missing enemy frame: " + path, this);
                    enabled = false;
                    return;
                }
            }
        }
        SharedFrames[id] = loaded;
        frames = loaded;
        ResetPlayback(id);
    }

    private void ResetPlayback(string id)
    {
        directionIndex = 0;
        frameTime = 0f;
        framesPerSecond = id == "SkeletonMinion" ? 11f : id == "Skeleton" ? 9f : 8f;
        ready = true;
        enabled = true;
        spriteRenderer.sprite = frames[directionIndex][0];
    }

    private void Update()
    {
        if (!ready || spriteRenderer == null || body == null || enemy == null) return;
        if (enemy.IsDead) return;
        Vector2 velocity = body.linearVelocity;
        bool moving = !GameplayRoomReveal.IsRevealing &&
            velocity.sqrMagnitude > movementThreshold * movementThreshold;
        if (moving)
        {
            float angle = Mathf.Atan2(velocity.y, velocity.x) * Mathf.Rad2Deg;
            int sector = (Mathf.RoundToInt(angle / 45f) + 8) % 8;
            int nextDirection;
            switch (sector)
            {
                case 0: nextDirection = 6; break; // Right
                case 1: nextDirection = 5; break; // UpRight
                case 2: nextDirection = 4; break; // Up
                case 3: nextDirection = 3; break; // UpLeft
                case 4: nextDirection = 2; break; // Left
                case 5: nextDirection = 1; break; // DownLeft
                case 6: nextDirection = 0; break; // Down
                default: nextDirection = 7; break; // DownRight
            }
            if (nextDirection != directionIndex)
            {
                directionIndex = nextDirection;
                frameTime = 0f;
            }
            frameTime += Time.deltaTime * framesPerSecond;
        }
        else
            frameTime = 0f;

        int frame = moving ? Mathf.FloorToInt(frameTime) % 8 : 0;
        spriteRenderer.sprite = frames[directionIndex][frame];
    }
}
