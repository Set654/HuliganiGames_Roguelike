using UnityEngine;

/// <summary>
/// World-space fog of war for the opening of every newly constructed room.
/// At entry only a circle around the player is clear. The fog recedes as the
/// camera moves from an intimate player-centered view to the full-room view.
/// During the reveal the player, regular enemies and bosses are input/AI locked.
/// No gameplay Time.timeScale override is necessary, so the tutorial and pause
/// menus continue to own their existing pause state.
/// </summary>
[DisallowMultipleComponent]
public sealed class GameplayRoomReveal : MonoBehaviour
{
    private static GameplayRoomReveal current;

    public static bool IsRevealing => current != null && current.revealing;

    // Initially false, including before the first reveal object is created.
    // Prevents the tutorial from flashing for one frame before room fog starts.
    public static bool HasCompletedCurrentRoomReveal { get; private set; }
    public static void PrepareForNewRoom() => HasCompletedCurrentRoomReveal = false;

    [SerializeField] private float revealDuration = 2.17175f; // 15% faster than v11
    [SerializeField] private float initialClearRadius = 1.65f;
    [SerializeField] private float featherWidth = 1.05f;
    [SerializeField] private float startingZoom = 0.56f;

    private const int FogTextureWidth = 256;
    private const int FogTextureHeight = 144;
    private const float FogWorldWidth = 16f;
    private const float FogWorldHeight = 9f;

    private Texture2D fogTexture;
    private Sprite fogSprite;
    private SpriteRenderer fogRenderer;
    private Color32[] fogPixels;
    private float[] pixelDistances;
    private float[] pixelNoise;

    private Camera gameCamera;
    private Vector2 entryPosition;
    private float fullCameraSize;
    private float elapsed;
    private bool revealing;

    public static void BeginForRoom(Transform roomRoot, PlayerController player)
    {
        PrepareForNewRoom();
        if (current != null)
            current.FinishImmediately();

        if (roomRoot == null || player == null)
            return;

        GameObject fog = new GameObject("Room Entry Fog / Camera Reveal");
        fog.transform.SetParent(roomRoot, false);
        GameplayRoomReveal reveal = fog.AddComponent<GameplayRoomReveal>();
        reveal.Begin(player.transform.position);
    }

    public static void CancelForSceneChange()
    {
        PrepareForNewRoom();
        if (current != null)
            current.FinishImmediately();
    }

    private void Begin(Vector3 spawn)
    {
        if (current != null && current != this)
            current.FinishImmediately();

        gameCamera = Camera.main;
        if (gameCamera == null)
        {
            Destroy(gameObject);
            return;
        }

        current = this;
        revealing = true;
        elapsed = 0f;
        entryPosition = new Vector2(spawn.x, spawn.y);
        fullCameraSize = Mathf.Max(2f, gameCamera.orthographicSize);

        fogTexture = new Texture2D(FogTextureWidth, FogTextureHeight, TextureFormat.RGBA32, false)
        {
            name = "Room Reveal Fog Texture",
            filterMode = FilterMode.Bilinear,
            wrapMode = TextureWrapMode.Clamp
        };
        fogPixels = new Color32[FogTextureWidth * FogTextureHeight];
        pixelDistances = new float[fogPixels.Length];
        pixelNoise = new float[fogPixels.Length];
        for (int y = 0; y < FogTextureHeight; y++)
        {
            float worldY = ((y + 0.5f) / FogTextureHeight - 0.5f) * FogWorldHeight;
            for (int x = 0; x < FogTextureWidth; x++)
            {
                int index = y * FogTextureWidth + x;
                float worldX = ((x + 0.5f) / FogTextureWidth - 0.5f) * FogWorldWidth;
                float dx = worldX - entryPosition.x;
                float dy = worldY - entryPosition.y;
                pixelDistances[index] = Mathf.Sqrt(dx * dx + dy * dy);
                pixelNoise[index] = Mathf.Sin(worldX * 3.5f + worldY * 5.1f) * 0.13f
                    + Mathf.Sin(worldX * 7.7f - worldY * 2.9f) * 0.075f;
            }
        }
        fogSprite = Sprite.Create(
            fogTexture,
            new Rect(0f, 0f, FogTextureWidth, FogTextureHeight),
            new Vector2(0.5f, 0.5f),
            FogTextureWidth / FogWorldWidth,
            0,
            SpriteMeshType.FullRect);

        fogRenderer = gameObject.AddComponent<SpriteRenderer>();
        fogRenderer.sprite = fogSprite;
        fogRenderer.sortingOrder = 900;
        fogRenderer.color = Color.white;
        transform.position = new Vector3(0f, 0f, 0f);

        gameCamera.orthographicSize = fullCameraSize * Mathf.Clamp(startingZoom, 0.4f, 0.95f);
        RuntimeCameraShake.SetHome(new Vector3(entryPosition.x, entryPosition.y, -10f));
        UpdateFog(initialClearRadius, false);
    }

    private void Update()
    {
        if (!revealing)
            return;

        // Esc pause freezes the transition as well. Tutorial's modal may pause
        // Time.timeScale but the visual intro still finishes using unscaled time.
        GameManager manager = GameManager.Instance;
        if (manager != null && manager.IsGameplayPaused)
            return;

        elapsed += Time.unscaledDeltaTime;
        float t = Mathf.Clamp01(elapsed / Mathf.Max(0.15f, revealDuration));
        float ease = t * t * (3f - 2f * t);

        if (gameCamera != null)
        {
            gameCamera.orthographicSize = Mathf.Lerp(fullCameraSize * startingZoom, fullCameraSize, ease);
            Vector2 focus = Vector2.Lerp(entryPosition, Vector2.zero, ease);
            RuntimeCameraShake.SetHome(new Vector3(focus.x, focus.y, -10f));
        }

        // The farthest corner from an entry on the left is around 15 world units.
        float radius = Mathf.Lerp(initialClearRadius, 18.25f, ease);
        UpdateFog(radius, t >= 1f);

        if (t >= 1f)
        {
            FinishImmediately();
            HasCompletedCurrentRoomReveal = true;
        }
    }

    private void UpdateFog(float radius, bool clearAll)
    {
        if (fogTexture == null || fogPixels == null)
            return;

        Color32 fogColor = new Color32(9, 11, 16, 244);
        float softEdge = Mathf.Max(0.3f, featherWidth);
        for (int index = 0; index < fogPixels.Length; index++)
        {
            if (clearAll)
            {
                fogPixels[index] = new Color32(9, 11, 16, 0);
                continue;
            }

            float opacity = Mathf.SmoothStep(0f, 1f,
                (pixelDistances[index] - radius + pixelNoise[index]) / softEdge);
            fogColor.a = (byte)Mathf.RoundToInt(opacity * 244f);
            fogPixels[index] = fogColor;
        }

        fogTexture.SetPixels32(fogPixels);
        fogTexture.Apply(false, false);
    }

    private void FinishImmediately()
    {
        if (!revealing)
            return;

        revealing = false;
        if (current == this)
            current = null;

        if (gameCamera != null)
            gameCamera.orthographicSize = fullCameraSize;

        RuntimeCameraShake.SetHome(new Vector3(0f, 0f, -10f));
        if (fogRenderer != null)
            fogRenderer.enabled = false;

        Destroy(gameObject);
    }

    private void OnDestroy()
    {
        if (current == this)
        {
            current = null;
            RuntimeCameraShake.SetHome(new Vector3(0f, 0f, -10f));
            if (gameCamera != null && fullCameraSize > 0f)
                gameCamera.orthographicSize = fullCameraSize;
        }

        revealing = false;
        if (fogSprite != null) Destroy(fogSprite);
        if (fogTexture != null) Destroy(fogTexture);
    }
}
