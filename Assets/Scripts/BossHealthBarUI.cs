using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

[DisallowMultipleComponent]
public sealed class BossHealthBarUI : MonoBehaviour
{
    private Canvas canvas;
    private GameObject root;
    private Image backgroundImage;
    private Image fillImage;
    private Image[] lifeHearts;
    private BossController trackedBoss;
    private PlayerController trackedPlayer;
    private float nextSearchTime;

    private const float FrameWidth = 760f;
    private const float FrameHeight = 252f;
    // Authored frame sprites share a 2172x724 canvas but have openings at
    // different heights. Align the HP image within each frame's clear window.
    private const float WraithInnerWidth = 616f;
    private const float WraithInnerHeight = 39f;
    private const float CyclopsInnerWidth = 494f;
    private const float CyclopsInnerHeight = 39f;

    private void Awake()
    {
        DontDestroyOnLoad(gameObject);
        BuildUi();
        SceneManager.sceneLoaded += HandleSceneLoaded;
    }

    private void Start() => RefreshVisibility(forceHide: true);

    private void Update()
    {
        if (canvas == null || root == null)
            BuildUi();

        GameManager gameManager = GameManager.Instance;
        if (SceneManager.GetActiveScene().name != "Game" || gameManager == null || gameManager.CurrentState != GameManager.GameState.GAMEPLAY)
        {
            trackedBoss = null;
            RefreshVisibility(forceHide: true);
            return;
        }

        if (trackedPlayer == null)
            trackedPlayer = FindAnyObjectByType<PlayerController>();
        if (trackedPlayer != null && trackedPlayer.IsDead)
        {
            RefreshVisibility(forceHide: true);
            return;
        }

        if (trackedBoss == null || (trackedBoss.IsDead && !trackedBoss.IsResurrectionInProgress))
        {
            if (Time.unscaledTime >= nextSearchTime)
            {
                trackedBoss = FindAnyObjectByType<BossController>();
                nextSearchTime = Time.unscaledTime + 0.20f;
            }
        }

        if (trackedBoss == null || (trackedBoss.IsDead && !trackedBoss.IsResurrectionInProgress))
        {
            RefreshVisibility(forceHide: true);
            return;
        }

        RefreshVisibility(forceHide: false);
        UpdateFrame();
        UpdateFill();
        UpdateLifeHearts();
    }

    private void UpdateFrame()
    {
        if (backgroundImage == null || trackedBoss == null)
            return;

        string path = trackedBoss.Kind == BossController.BossKind.CyclopsKing
            ? "UI/CyclopsKing_HealthBar"
            : "UI/SkeletonKing_HealthBar";
        Sprite frame = RuntimeGameArt.Load(path);
        if (frame != null)
        {
            backgroundImage.sprite = frame;
            backgroundImage.preserveAspect = true;
        }
    }

    private void UpdateFill()
    {
        if (fillImage == null || trackedBoss == null)
            return;

        bool wraith = trackedBoss.Kind == BossController.BossKind.WraithKing;
        float fullWidth = wraith ? WraithInnerWidth : CyclopsInnerWidth;
        float fullHeight = wraith ? WraithInnerHeight : CyclopsInnerHeight;
        // Wraith opening is lower on its picture; the Cyclops opening is higher.
        Vector2 leftEdge = wraith ? new Vector2(-308f, -48f) : new Vector2(-247f, -2f);
        float fraction = Mathf.Clamp01(trackedBoss.CurrentHealth / Mathf.Max(1f, trackedBoss.MaxHealth));

        RectTransform fillRect = fillImage.rectTransform;
        fillRect.anchoredPosition = leftEdge;
        fillRect.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, fullWidth * fraction);
        fillRect.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, fullHeight);
        fillImage.color = wraith
            ? new Color(0.90f, 0.08f, 0.12f, 0.97f)
            : new Color(0.98f, 0.30f, 0.10f, 0.97f);
        // Intentionally never move this image over the frame's painted details.
    }

    private void UpdateLifeHearts()
    {
        if (lifeHearts == null || lifeHearts.Length == 0)
            return;

        bool showHearts = trackedBoss != null && trackedBoss.Kind == BossController.BossKind.WraithKing;
        Sprite heartSprite = RuntimeGameArt.Load("UI/Hearts_Full");
        for (int i = 0; i < lifeHearts.Length; i++)
        {
            if (lifeHearts[i] == null)
                continue;
            lifeHearts[i].gameObject.SetActive(showHearts);
            if (!showHearts)
                continue;

            if (heartSprite != null)
            {
                lifeHearts[i].sprite = heartSprite;
                lifeHearts[i].preserveAspect = true;
            }

            lifeHearts[i].color = i < trackedBoss.RemainingLives
                ? Color.white
                : new Color(0.30f, 0.30f, 0.30f, 0.85f);
        }
    }

    private void HandleSceneLoaded(Scene scene, LoadSceneMode mode)
    {
        nextSearchTime = 0f;
        trackedBoss = null;
        trackedPlayer = null;
        RefreshVisibility(forceHide: scene.name != "Game");
    }

    private void BuildUi()
    {
        if (canvas != null)
            return;

        canvas = RuntimeUIFactory.CreateCanvas(transform, "Boss Health Bar Canvas", 110);

        root = new GameObject("Boss Health Bar Root", typeof(RectTransform));
        root.transform.SetParent(canvas.transform, false);
        RectTransform rootRect = root.GetComponent<RectTransform>();
        rootRect.anchorMin = new Vector2(0.5f, 1f);
        rootRect.anchorMax = new Vector2(0.5f, 1f);
        rootRect.pivot = new Vector2(0.5f, 1f);
        rootRect.anchoredPosition = new Vector2(0f, -18f);
        rootRect.sizeDelta = new Vector2(FrameWidth, FrameHeight);

        GameObject fill = new GameObject("Boss Health Fill", typeof(RectTransform), typeof(Image));
        fill.transform.SetParent(root.transform, false);
        fillImage = fill.GetComponent<Image>();
        fillImage.raycastTarget = false;
        RectTransform fillRect = fill.GetComponent<RectTransform>();
        fillRect.anchorMin = new Vector2(0.5f, 0.5f);
        fillRect.anchorMax = new Vector2(0.5f, 0.5f);
        fillRect.pivot = new Vector2(0f, 0.5f);
        fillRect.anchoredPosition = new Vector2(-308f, -43f);
        fillRect.sizeDelta = new Vector2(WraithInnerWidth, WraithInnerHeight);


        GameObject background = new GameObject("Boss Health Background", typeof(RectTransform), typeof(Image));
        background.transform.SetParent(root.transform, false);
        backgroundImage = background.GetComponent<Image>();
        backgroundImage.raycastTarget = false;
        RectTransform backgroundRect = background.GetComponent<RectTransform>();
        backgroundRect.anchorMin = new Vector2(0.5f, 0.5f);
        backgroundRect.anchorMax = new Vector2(0.5f, 0.5f);
        backgroundRect.pivot = new Vector2(0.5f, 0.5f);
        backgroundRect.anchoredPosition = Vector2.zero;
        backgroundRect.sizeDelta = new Vector2(FrameWidth, FrameHeight);

        lifeHearts = new Image[2];
        for (int i = 0; i < lifeHearts.Length; i++)
        {
            GameObject heart = new GameObject("Boss Life Heart " + (i + 1), typeof(RectTransform), typeof(Image));
            heart.transform.SetParent(root.transform, false);
            Image image = heart.GetComponent<Image>();
            image.raycastTarget = false;
            RectTransform heartRect = heart.GetComponent<RectTransform>();
            heartRect.anchorMin = new Vector2(0.5f, 0.5f);
            heartRect.anchorMax = new Vector2(0.5f, 0.5f);
            heartRect.pivot = new Vector2(0.5f, 0.5f);
            // Separate life counter centered below the full boss health frame.
            heartRect.anchoredPosition = new Vector2((i == 0 ? -39f : 39f), -165f);
            heartRect.sizeDelta = new Vector2(66f, 66f);
            lifeHearts[i] = image;
        }
    }

    private void RefreshVisibility(bool forceHide)
    {
        if (root != null)
            root.SetActive(!forceHide);
    }

    private void OnDestroy() => SceneManager.sceneLoaded -= HandleSceneLoaded;
}
