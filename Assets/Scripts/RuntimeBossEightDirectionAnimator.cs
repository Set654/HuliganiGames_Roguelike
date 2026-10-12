using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Plays the author's 8-direction / 8-frame boss walking sprites without
/// modifying the Rigidbody2D, collision geometry, movement or special attacks.
/// Cyclops throw art takes priority until every thrown eye has returned.
/// </summary>
[DisallowMultipleComponent]
[RequireComponent(typeof(SpriteRenderer))]
public sealed class RuntimeBossEightDirectionAnimator : MonoBehaviour
{
    private static readonly Dictionary<string, Sprite[][]> SharedFrames =
        new Dictionary<string, Sprite[][]>();

    private static readonly string[] Directions =
    {
        "Down", "DownLeft", "Left", "UpLeft", "Up", "UpRight", "Right", "DownRight"
    };

    [SerializeField] private float movementThreshold = 0.08f;
    [SerializeField] private float framesPerSecond = 8f;

    private SpriteRenderer spriteRenderer;
    private Rigidbody2D body;
    private BossController boss;
    private Sprite[][] frames;
    private string characterId;
    private int directionIndex;
    private float frameTime;
    private bool ready;

    public static bool AssetsAvailable(string id)
    {
        if (id != "WraithKing" && id != "CyclopsKing") return false;
        return Resources.Load<Sprite>("GameArt/AnimationFrames/Bosses/" + id +
            "/Directional/" + id + "_Down_Walk_00") != null;
    }

    private void Awake()
    {
        spriteRenderer = GetComponent<SpriteRenderer>();
        body = GetComponent<Rigidbody2D>();
        boss = GetComponent<BossController>();
    }

    public void Configure(string id)
    {
        if (ready && characterId == id) return;
        ready = false;
        characterId = id;
        if (spriteRenderer == null) spriteRenderer = GetComponent<SpriteRenderer>();
        if (body == null) body = GetComponent<Rigidbody2D>();
        if (boss == null) boss = GetComponent<BossController>();

        if (SharedFrames.TryGetValue(id, out Sprite[][] cached))
        {
            frames = cached;
            ResetPlayback(id);
            return;
        }

        Sprite[][] loaded = new Sprite[Directions.Length][];
        for (int d = 0; d < Directions.Length; d++)
        {
            loaded[d] = new Sprite[8];
            for (int f = 0; f < 8; f++)
            {
                string path = "GameArt/AnimationFrames/Bosses/" + id +
                    "/Directional/" + id + "_" + Directions[d] + "_Walk_" + f.ToString("00");
                loaded[d][f] = Resources.Load<Sprite>(path);
                if (loaded[d][f] == null)
                {
                    Debug.LogError("[HuliganiGames] Missing boss walk frame: " + path, this);
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
        framesPerSecond = id == "WraithKing" ? 8.5f : 7.5f;
        directionIndex = 0;
        frameTime = 0f;
        ready = true;
        enabled = true;
        spriteRenderer.sprite = frames[directionIndex][0];
    }

    private void Update()
    {
        if (!ready || body == null || spriteRenderer == null || boss == null) return;
        if (boss.IsDead || boss.IsResurrectionInProgress || boss.IsCyclopsThrowPoseActive || boss.IsWraithAttackPoseActive)
            return;

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
                case 0: nextDirection = 6; break;
                case 1: nextDirection = 5; break;
                case 2: nextDirection = 4; break;
                case 3: nextDirection = 3; break;
                case 4: nextDirection = 2; break;
                case 5: nextDirection = 1; break;
                case 6: nextDirection = 0; break;
                default: nextDirection = 7; break;
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
