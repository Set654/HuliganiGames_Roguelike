using UnityEngine;

/// <summary>
/// Plays the artist-provided eight-way walk cycles at runtime. Only SpriteRenderer.sprite
/// changes; rotation of the player GameObject remains zero so colliders and aiming are
/// completely independent of facing. No external animator state transitions are needed.
/// </summary>
[DisallowMultipleComponent]
[RequireComponent(typeof(SpriteRenderer))]
public sealed class RuntimePlayerEightDirectionAnimator : MonoBehaviour
{
    private enum Direction
    {
        Down = 0,
        DownLeft = 1,
        Left = 2,
        UpLeft = 3,
        Up = 4,
        UpRight = 5,
        Right = 6,
        DownRight = 7
    }

    private static readonly string[] DirectionNames =
    {
        "Down", "DownLeft", "Left", "UpLeft", "Up", "UpRight", "Right", "DownRight"
    };

    [SerializeField, Range(4f, 18f)] private float walkFramesPerSecond = 10f;
    [SerializeField] private float minimumMovementMagnitude = 0.09f;
    [SerializeField] private float throwDuration = 0.20f;

    private SpriteRenderer spriteRenderer;
    private Rigidbody2D body;
    private PlayerController player;
    private Sprite[][] directionalFrames;
    private Sprite[] dashSprites; // Down, Up, Left, Right (supplied asset)
    private Vector2 dashDirection;
    private float dashUntil;
    private Direction facing = Direction.Down;
    private float walkClock;
    private float throwTimer;
    private bool ready;

    public static bool AssetsAvailable()
    {
        return Resources.Load<Sprite>("GameArt/AnimationFrames/Player/Directional/Player_Down_Walk_00") != null;
    }

    private void Awake()
    {
        spriteRenderer = GetComponent<SpriteRenderer>();
        body = GetComponent<Rigidbody2D>();
        player = GetComponent<PlayerController>();
        directionalFrames = new Sprite[DirectionNames.Length][];

        for (int d = 0; d < DirectionNames.Length; d++)
        {
            directionalFrames[d] = new Sprite[8];
            for (int f = 0; f < 8; f++)
            {
                string path = "GameArt/AnimationFrames/Player/Directional/Player_" +
                              DirectionNames[d] + "_Walk_" + f.ToString("00");
                directionalFrames[d][f] = Resources.Load<Sprite>(path);
                if (directionalFrames[d][f] == null)
                {
                    Debug.LogError("[HuliganiGames] Missing directional player frame: " + path, this);
                    enabled = false;
                    return;
                }
            }
        }

        dashSprites = new[]
        {
            Resources.Load<Sprite>("GameArt/AnimationFrames/Player/Dash/Player_Dash_Down"),
            Resources.Load<Sprite>("GameArt/AnimationFrames/Player/Dash/Player_Dash_Up"),
            Resources.Load<Sprite>("GameArt/AnimationFrames/Player/Dash/Player_Dash_Left"),
            Resources.Load<Sprite>("GameArt/AnimationFrames/Player/Dash/Player_Dash_Right")
        };
        ready = true;
        if (spriteRenderer != null)
            spriteRenderer.sprite = directionalFrames[(int)facing][0];
    }

    private void Update()
    {
        if (!ready || spriteRenderer == null)
            return;

        // Project input controls the intended facing; physical motion decides whether
        // feet cycle, so pushing against a solid wall does not animate in place.
        Vector2 movement = Vector2.zero;
        if (player != null)
            movement = player.MovementInput;
        if (movement.sqrMagnitude <= 0.001f && body != null)
            movement = body.linearVelocity;

        bool alive = player == null || !player.IsDead;
        bool moving = alive && !GameplayRoomReveal.IsRevealing && body != null &&
                      body.linearVelocity.sqrMagnitude > minimumMovementMagnitude * minimumMovementMagnitude;

        if (movement.sqrMagnitude > 0.001f && alive && !GameplayRoomReveal.IsRevealing)
        {
            Direction newDirection = ResolveDirection(movement);
            if (newDirection != facing)
            {
                facing = newDirection;
                walkClock = 0f;
            }
        }

        // Dash has visual priority over walking and dagger throwing. Only
        // the sprite changes; Rigidbody2D velocity remains player-controlled.
        if (alive && Time.time < dashUntil && dashSprites != null)
        {
            int dashIndex = Mathf.Abs(dashDirection.x) >= Mathf.Abs(dashDirection.y)
                ? (dashDirection.x >= 0f ? 3 : 2)
                : (dashDirection.y >= 0f ? 1 : 0);
            Sprite dash = dashSprites[dashIndex];
            if (dash != null)
            {
                spriteRenderer.sprite = dash;
                return;
            }
        }

        if (throwTimer > 0f)
            throwTimer = Mathf.Max(0f, throwTimer - Time.deltaTime);

        if (moving)
            walkClock += Time.deltaTime * Mathf.Max(1f, walkFramesPerSecond);
        else
            walkClock = 0f;

        int frame = moving ? Mathf.FloorToInt(walkClock) % 8 : 0;
        spriteRenderer.sprite = directionalFrames[(int)facing][frame];
    }

    public void PlayDash(Vector2 movementDirection, float duration)
    {
        if (!ready) return;
        if (movementDirection.sqrMagnitude < 0.0001f) return;
        dashDirection = movementDirection.normalized;
        dashUntil = Time.time + Mathf.Max(0.02f, duration);
        // The dash is not a throw animation.
        throwTimer = 0f;
    }

    public void PlayThrow()
    {
        // Throwing a dagger no longer swaps the hero sprite.
        throwTimer = 0f;
    }

    // Damage flashing and temporary invulnerability are driven by PlayerController.
    // Keeping these separate avoids resetting the current facing or movement cycle.
    public void PlayHurt() { }

    private static Direction ResolveDirection(Vector2 movement)
    {
        float angle = Mathf.Atan2(movement.y, movement.x) * Mathf.Rad2Deg;
        int sector = Mathf.RoundToInt(angle / 45f);
        switch (sector)
        {
            case -4:
            case 4: return Direction.Left;
            case -3: return Direction.DownLeft;
            case -2: return Direction.Down;
            case -1: return Direction.DownRight;
            case 0: return Direction.Right;
            case 1: return Direction.UpRight;
            case 2: return Direction.Up;
            case 3: return Direction.UpLeft;
            default: return Direction.Down;
        }
    }
}
