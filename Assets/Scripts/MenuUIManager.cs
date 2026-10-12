using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

[DisallowMultipleComponent]
public sealed class MenuUIManager : MonoBehaviour
{
    public static MenuUIManager Instance { get; private set; }
    private Canvas rootCanvas;
    private GameObject mainMenuRoot, settingsRoot, pauseRoot;
    private Image brightnessOverlay;
    private Slider brightnessSlider, masterVolumeSlider, musicVolumeSlider, sfxVolumeSlider;
    private Text brightnessValue, masterVolumeValue, musicVolumeValue, sfxVolumeValue;
    private GameManager gameManager;
    private readonly Dictionary<GameManager.BindingAction, Text> bindingButtonTexts = new Dictionary<GameManager.BindingAction, Text>();
    private bool awaitingBinding;
    private GameManager.BindingAction pendingBindingAction;
    private int bindingInputAllowedFrame;

    private void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(gameObject); return; }
        Instance = this; DontDestroyOnLoad(gameObject); RuntimeUIFactory.EnsureEventSystem(); BuildInterface();
    }
    private void Start() { TryBindGameManager(); RefreshEntireInterface(); }
    private void Update()
    {
        if (gameManager == null) TryBindGameManager();
        if (awaitingBinding) CaptureBindingInput();
        else if (gameManager != null && Input.GetKeyDown(KeyCode.Escape))
        {
            if (gameManager.CurrentState == GameManager.GameState.GAMEPLAY)
            {
                if (gameManager.IsGameplayPaused) gameManager.ResumeGameplay();
                else gameManager.PauseGameplay();
                RefreshVisibility();
            }
            else if (gameManager.CurrentState == GameManager.GameState.SETTINGS)
            {
                gameManager.CloseSettings();
                RefreshVisibility();
            }
        }
        ApplyBrightnessOverlay();
    }
    private void OnDestroy() { if (Instance == this) Instance = null; UnbindGameManager(); }

    private void TryBindGameManager()
    {
        if (gameManager != null || GameManager.Instance == null) return; gameManager = GameManager.Instance; gameManager.StateChanged += HandleGameStateChanged; gameManager.PauseChanged += HandlePauseChanged; RefreshEntireInterface();
    }
    private void UnbindGameManager() { if (gameManager == null) return; gameManager.StateChanged -= HandleGameStateChanged; gameManager.PauseChanged -= HandlePauseChanged; gameManager = null; }
    private void HandleGameStateChanged(GameManager.GameState state) { awaitingBinding = false; RefreshVisibility(); if (state == GameManager.GameState.SETTINGS) { SyncSettingsControls(); RefreshBindingButtons(); } }
    private void HandlePauseChanged(bool paused) { RefreshVisibility(); }

    private void BuildInterface()
    {
        rootCanvas = RuntimeUIFactory.CreateCanvas(transform, "Persistent Menu Canvas", 1000); BuildMainMenu(); BuildSettings(); BuildPauseScreen(); BuildBrightnessOverlay(); RefreshVisibility();
    }

    private void BuildMainMenu()
    {
        mainMenuRoot = RuntimeUIFactory.CreatePanel(rootCanvas.transform, "Main Menu", Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero, Color.black);
        BuildReferenceInspiredMainMenuBackdrop(mainMenuRoot.transform);

        Text title = RuntimeUIFactory.CreateText(mainMenuRoot.transform, "Title", "HuliganiGames", 80,
            new Vector2(0.03f, 0.76f), new Vector2(0.52f, 0.96f), Vector2.zero, Vector2.zero,
            TextAnchor.MiddleLeft, new Color(0.94f, 0.92f, 0.87f, 1f), FontStyle.Bold);
        title.resizeTextForBestFit = true;
        title.resizeTextMinSize = 52;
        title.resizeTextMaxSize = 90;
        Shadow titleShadow = title.gameObject.AddComponent<Shadow>();
        titleShadow.effectColor = new Color(0.05f, 0.01f, 0.01f, 0.95f);
        titleShadow.effectDistance = new Vector2(4f, -4f);

        RuntimeUIFactory.CreateText(mainMenuRoot.transform, "Subtitle", "МРАЧНОЕ ФЭНТЕЗИ · РОГАЛИК", 24,
            new Vector2(0.105f, 0.705f), new Vector2(0.395f, 0.77f), Vector2.zero, Vector2.zero,
            TextAnchor.MiddleCenter, new Color(0.83f, 0.78f, 0.72f, 0.95f), FontStyle.Normal);

        // The line ends at the subtitle edges and remains centered beneath it.
        GameObject divider = RuntimeUIFactory.CreatePanel(mainMenuRoot.transform, "Title Divider",
            new Vector2(0.113f, 0.692f), new Vector2(0.387f, 0.696f), Vector2.zero, Vector2.zero,
            new Color(0.70f, 0.66f, 0.61f, 0.72f));
        divider.GetComponent<Image>().raycastTarget = false;

        CreateMainMenuActionButton("New Game Button", "НОВАЯ ИГРА", 0.575f, HandlePlayPressed, true);
        CreateMainMenuActionButton("Settings Button", "НАСТРОЙКИ", 0.470f, HandleSettingsPressed, false);
        CreateMainMenuActionButton("Exit Button", "ВЫХОД", 0.365f, HandleExitPressed, false);

    }

    private void CreateMainMenuActionButton(string name, string label, float centerY, UnityEngine.Events.UnityAction callback, bool selected)
    {
        GameObject root = new GameObject(name, typeof(RectTransform), typeof(Image), typeof(Button));
        root.transform.SetParent(mainMenuRoot.transform, false);
        RectTransform rect = root.GetComponent<RectTransform>();
        RuntimeUIFactory.SetRect(rect,
            new Vector2(0.052f, centerY - 0.045f),
            new Vector2(0.382f, centerY + 0.045f),
            Vector2.zero, Vector2.zero);

        Image background = root.GetComponent<Image>();
        // A barely lit blood-red brushstroke that brightens gently on hover.
        background.color = selected
            ? new Color(0.52f, 0.035f, 0.035f, 0.78f)
            : new Color(0.32f, 0.025f, 0.028f, 0.40f);
        background.sprite = RuntimeUIFactory.GetBrushStrokeSprite();
        background.type = Image.Type.Sliced;
        background.preserveAspect = false;

        Button button = root.GetComponent<Button>();
        // The custom hover component handles tint/scale. Avoid double-tinting
        // caused by uGUI Button ColorTint multiplying Image.color.
        button.transition = Selectable.Transition.None;
        button.targetGraphic = background;
        button.onClick.AddListener(callback);

        // No leading arrow: the text starts with its actual first letter.
        Text text = RuntimeUIFactory.CreateText(root.transform, "Label", label, 31,
            Vector2.zero, Vector2.one,
            new Vector2(30f, 0f), new Vector2(-65f, 0f),
            TextAnchor.MiddleLeft,
            new Color(0.95f, 0.92f, 0.89f, 1f), FontStyle.Bold);
        text.resizeTextForBestFit = true;
        text.resizeTextMinSize = 24;
        text.resizeTextMaxSize = 34;
        Outline textOutline = text.gameObject.AddComponent<Outline>();
        textOutline.effectColor = new Color(0f, 0f, 0f, 0.72f);
        textOutline.effectDistance = new Vector2(1.5f, -1.5f);

        // Pointer is placed on the RIGHT and faces LEFT towards the button.
        // Use the game's existing pixel-art dagger sprite.
        GameObject daggerObject = new GameObject("Hover Dagger Pointer", typeof(RectTransform), typeof(Image));
        daggerObject.transform.SetParent(root.transform, false);
        RectTransform daggerRect = daggerObject.GetComponent<RectTransform>();
        RuntimeUIFactory.SetRect(daggerRect,
            new Vector2(0.82f, 0.15f), new Vector2(1.045f, 0.85f),
            Vector2.zero, Vector2.zero);
        daggerRect.localRotation = Quaternion.Euler(0f, 0f, 180f);
        Image daggerImage = daggerObject.GetComponent<Image>();
        daggerImage.sprite = Resources.Load<Sprite>("GameArt/Projectiles/Dagger_Right");
        daggerImage.preserveAspect = true;
        daggerImage.color = new Color(1f, 0.90f, 0.79f, 1f);
        daggerImage.raycastTarget = false;
        daggerObject.SetActive(false);

        MenuButtonHoverFeedback feedback = root.AddComponent<MenuButtonHoverFeedback>();
        feedback.TargetGraphic = background;
        feedback.DaggerIndicator = daggerObject;
    }

    private void BuildReferenceInspiredMainMenuBackdrop(Transform parent)
    {
        GameObject backdrop = new GameObject("Main Menu Backdrop", typeof(RectTransform), typeof(RawImage));
        backdrop.transform.SetParent(parent, false);
        RuntimeUIFactory.SetRect(backdrop.GetComponent<RectTransform>(), Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero);
        RawImage image = backdrop.GetComponent<RawImage>();
        Texture2D menuBackground = Resources.Load<Texture2D>("MenuBackgrounds/MainMenuBackground");
        image.texture = menuBackground != null ? menuBackground : CreateReferenceInspiredMainMenuTexture(1280, 720);
        image.color = Color.white;
        image.raycastTarget = false;
        backdrop.transform.SetAsFirstSibling();

        GameObject vignette = RuntimeUIFactory.CreatePanel(parent, "Menu Vignette", Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero, new Color(0f, 0f, 0f, 0.18f));
        vignette.GetComponent<Image>().raycastTarget = false;
        vignette.transform.SetSiblingIndex(1);

        GameObject leftShade = RuntimeUIFactory.CreatePanel(parent, "Menu Left Readability Shade",
            new Vector2(0f, 0f), new Vector2(0.40f, 1f), Vector2.zero, Vector2.zero, new Color(0.02f, 0.01f, 0.01f, 0.34f));
        leftShade.GetComponent<Image>().raycastTarget = false;
        leftShade.transform.SetSiblingIndex(2);
    }

    private static Texture2D CreateReferenceInspiredMainMenuTexture(int width, int height)
    {
        Texture2D texture = new Texture2D(width, height, TextureFormat.RGBA32, false);
        texture.name = "Runtime Main Menu Backdrop";
        texture.filterMode = FilterMode.Bilinear;
        texture.wrapMode = TextureWrapMode.Clamp;
        Color32[] pixels = new Color32[width * height];

        Color top = new Color32(18, 14, 14, 255);
        Color bottom = new Color32(7, 6, 8, 255);
        Color cloud = new Color32(90, 46, 46, 190);
        Color moon = new Color32(194, 6, 10, 255);
        Color moonDark = new Color32(130, 0, 0, 255);
        Color stone = new Color32(45, 43, 49, 255);
        Color deepStone = new Color32(28, 26, 30, 255);
        Color fog = new Color32(78, 72, 72, 180);
        Color banner = new Color32(103, 12, 20, 255);
        Color iron = new Color32(86, 84, 89, 255);
        Color cloak = new Color32(107, 15, 18, 255);
        Color body = new Color32(18, 16, 18, 255);
        Color blood = new Color32(132, 14, 16, 255);

        for (int y = 0; y < height; y++)
        {
            float t = y / (float)(height - 1);
            Color baseColor = Color.Lerp(bottom, top, t);
            for (int x = 0; x < width; x++)
            {
                float n = Mathf.PerlinNoise(x * 0.0065f, y * 0.012f) * 0.65f + Mathf.PerlinNoise((x + 300f) * 0.003f, (y + 90f) * 0.008f) * 0.35f;
                float smoke = Mathf.Clamp01((n - 0.44f) * 1.35f) * Mathf.SmoothStep(0f, 1f, t) * 0.55f;
                pixels[y * width + x] = (Color32)Color.Lerp(baseColor, cloud, smoke);
            }
        }

        FillCircle(pixels, width, height, (int)(width * 0.67f), (int)(height * 0.60f), (int)(height * 0.20f), moon);
        for (int i = 0; i < 34; i++)
        {
            int cx = (int)(width * 0.67f + Mathf.Sin(i * 0.73f) * height * 0.08f);
            int cy = (int)(height * 0.60f + Mathf.Cos(i * 1.21f) * height * 0.075f);
            int radius = 10 + (i * 13 % 34);
            FillCircleAlpha(pixels, width, height, cx, cy, radius, moonDark, 0.20f);
        }

        DrawJaggedGround(pixels, width, height, (int)(height * 0.20f), deepStone, stone);
        DrawTower(pixels, width, height, (int)(width * 0.13f), (int)(height * 0.15f), 72, 180, stone, banner, false);
        DrawTower(pixels, width, height, (int)(width * 0.30f), (int)(height * 0.18f), 86, 225, stone, banner, true);
        DrawTower(pixels, width, height, (int)(width * 0.90f), (int)(height * 0.28f), 92, 250, stone, banner, true);
        DrawArch(pixels, width, height, (int)(width * 0.18f), (int)(height * 0.11f), 68, 98, stone);
        DrawArch(pixels, width, height, (int)(width * 0.84f), (int)(height * 0.16f), 68, 112, stone);
        DrawKnight(pixels, width, height, (int)(width * 0.63f), (int)(height * 0.18f), 1.08f, body, iron, cloak, blood);
        DrawFogLayer(pixels, width, height, (int)(height * 0.14f), fog);
        DrawFogLayer(pixels, width, height, (int)(height * 0.10f), new Color32(98, 92, 92, 135));

        texture.SetPixels32(pixels);
        texture.Apply(false, false);
        return texture;
    }

    private static void DrawFogLayer(Color32[] pixels, int width, int height, int bandHeight, Color32 color)
    {
        for (int y = 0; y < bandHeight; y++)
        {
            float t = y / Mathf.Max(1f, bandHeight - 1f);
            for (int x = 0; x < width; x++)
            {
                float n = Mathf.PerlinNoise((x + 250f) * 0.008f, (y + 30f) * 0.05f);
                float alpha = Mathf.Clamp01((1f - t) * 0.9f + (n - 0.5f) * 0.5f) * (color.a / 255f);
                BlendPixel(pixels, width, height, x, y, new Color32(color.r, color.g, color.b, (byte)(alpha * 255f)));
            }
        }
    }

    private static void DrawJaggedGround(Color32[] pixels, int width, int height, int horizon, Color32 fill, Color32 ridge)
    {
        for (int x = 0; x < width; x++)
        {
            float n = Mathf.PerlinNoise(x * 0.008f, 0.14f);
            int groundY = horizon + Mathf.RoundToInt((n - 0.5f) * 60f);
            for (int y = 0; y < groundY; y++)
                pixels[y * width + x] = fill;
            if (groundY >= 0 && groundY < height)
                for (int yy = Mathf.Max(0, groundY - 2); yy < Mathf.Min(height, groundY + 4); yy++)
                    pixels[yy * width + x] = ridge;
        }
    }

    private static void DrawTower(Color32[] pixels, int width, int height, int x, int y, int towerWidth, int towerHeight, Color32 stone, Color32 banner, bool withBanner)
    {
        FillRect(pixels, width, height, x, y, towerWidth, towerHeight, stone);
        int battlementWidth = Mathf.Max(12, towerWidth / 5);
        for (int i = 0; i < 4; i++)
            FillRect(pixels, width, height, x + i * battlementWidth + 2, y + towerHeight - 8, battlementWidth - 4, 14, stone);
        FillRect(pixels, width, height, x + towerWidth / 2 - 8, y, 16, Mathf.RoundToInt(towerHeight * 0.42f), new Color32((byte)Mathf.Clamp(stone.r - 6,0,255), (byte)Mathf.Clamp(stone.g - 6,0,255), (byte)Mathf.Clamp(stone.b - 6,0,255), 255));
        if (withBanner)
        {
            int bx = x + towerWidth - 12;
            FillRect(pixels, width, height, bx, y + towerHeight - 22, 4, 42, new Color32(95, 85, 60, 255));
            FillRect(pixels, width, height, bx + 4, y + towerHeight - 58, 24, 44, banner);
            DrawLine(pixels, width, height, bx + 14, y + towerHeight - 51, bx + 14, y + towerHeight - 22, new Color32(225, 220, 210, 255), 1);
            DrawLine(pixels, width, height, bx + 4, y + towerHeight - 36, bx + 28, y + towerHeight - 36, new Color32(225, 220, 210, 255), 1);
        }
    }

    private static void DrawArch(Color32[] pixels, int width, int height, int x, int y, int archWidth, int archHeight, Color32 stone)
    {
        FillRect(pixels, width, height, x, y, 12, archHeight, stone);
        FillRect(pixels, width, height, x + archWidth - 12, y, 12, archHeight, stone);
        FillRect(pixels, width, height, x, y + archHeight - 10, archWidth, 10, stone);
    }

    private static void DrawKnight(Color32[] pixels, int width, int height, int centerX, int baseY, float scale, Color32 body, Color32 armor, Color32 cloak, Color32 blood)
    {
        int bodyH = Mathf.RoundToInt(210f * scale);
        int bodyW = Mathf.RoundToInt(74f * scale);
        int legH = Mathf.RoundToInt(72f * scale);
        FillRect(pixels, width, height, centerX - bodyW / 2, baseY + legH, bodyW, bodyH - legH, armor);
        FillRect(pixels, width, height, centerX - bodyW / 3, baseY, bodyW / 3, legH, body);
        FillRect(pixels, width, height, centerX + 5, baseY, bodyW / 3, legH, body);
        FillCircle(pixels, width, height, centerX, baseY + bodyH + 26, Mathf.RoundToInt(28f * scale), body);
        FillCircle(pixels, width, height, centerX - 6, baseY + bodyH + 16, Mathf.RoundToInt(18f * scale), new Color32(10,10,12,255));
        FillRect(pixels, width, height, centerX - bodyW / 2 - 24, baseY + bodyH - 92, 24, Mathf.RoundToInt(104f * scale), armor);
        FillRect(pixels, width, height, centerX + bodyW / 2, baseY + bodyH - 96, 18, Mathf.RoundToInt(106f * scale), armor);
        FillRect(pixels, width, height, centerX + bodyW / 2 + 18, baseY + 18, Mathf.RoundToInt(66f * scale), Mathf.RoundToInt(132f * scale), armor);
        DrawLine(pixels, width, height, centerX + bodyW / 2 + 28, baseY + 26, centerX + bodyW / 2 + 78, baseY - 62, armor, 3);
        FillRect(pixels, width, height, centerX - bodyW / 2 - 10, baseY + bodyH - 78, Mathf.RoundToInt(18f * scale), Mathf.RoundToInt(150f * scale), armor);
        DrawLine(pixels, width, height, centerX - bodyW / 2 - 4, baseY + bodyH - 60, centerX - bodyW / 2 - 84, baseY - 40, armor, 4);
        FillRect(pixels, width, height, centerX - bodyW / 2 - 84, baseY - 46, Mathf.RoundToInt(8f * scale), Mathf.RoundToInt(90f * scale), blood);
        FillRect(pixels, width, height, centerX - bodyW / 2 - 78, baseY - 10, Mathf.RoundToInt(52f * scale), Mathf.RoundToInt(10f * scale), blood);

        for (int i = 0; i < 7; i++)
        {
            int capeY = baseY + bodyH - 18 - i * Mathf.RoundToInt(20f * scale);
            int capeW = Mathf.RoundToInt((148f - i * 10f) * scale);
            FillRect(pixels, width, height, centerX - 10, capeY, capeW, Mathf.RoundToInt(24f * scale), cloak);
        }
    }

    private static void FillCircle(Color32[] pixels, int width, int height, int centerX, int centerY, int radius, Color32 color)
    {
        int r2 = radius * radius;
        for (int y = -radius; y <= radius; y++)
        for (int x = -radius; x <= radius; x++)
        {
            if (x * x + y * y > r2) continue;
            int px = centerX + x;
            int py = centerY + y;
            if (px >= 0 && px < width && py >= 0 && py < height)
                pixels[py * width + px] = color;
        }
    }

    private static void FillCircleAlpha(Color32[] pixels, int width, int height, int centerX, int centerY, int radius, Color32 color, float alphaMultiplier)
    {
        int r2 = radius * radius;
        for (int y = -radius; y <= radius; y++)
        for (int x = -radius; x <= radius; x++)
        {
            if (x * x + y * y > r2) continue;
            int px = centerX + x;
            int py = centerY + y;
            if (px >= 0 && px < width && py >= 0 && py < height)
                BlendPixel(pixels, width, height, px, py, new Color32(color.r, color.g, color.b, (byte)(color.a * alphaMultiplier)));
        }
    }

    private static void FillRect(Color32[] pixels, int width, int height, int x, int y, int rectWidth, int rectHeight, Color32 color)
    {
        for (int yy = Mathf.Max(0, y); yy < Mathf.Min(height, y + rectHeight); yy++)
        for (int xx = Mathf.Max(0, x); xx < Mathf.Min(width, x + rectWidth); xx++)
            pixels[yy * width + xx] = color;
    }

    private static void BlendPixel(Color32[] pixels, int width, int height, int x, int y, Color32 color)
    {
        if (x < 0 || x >= width || y < 0 || y >= height) return;
        Color dst = pixels[y * width + x];
        Color src = color;
        pixels[y * width + x] = Color.Lerp(dst, src, src.a);
    }

    private void BuildMedievalPatternBackdrop(Transform parent)
    {
        GameObject patternObject =
            new GameObject(
                "Blood Medieval Pattern",
                typeof(RectTransform),
                typeof(RawImage));

        patternObject.transform.SetParent(
            parent,
            false);

        RectTransform rect =
            patternObject.GetComponent<RectTransform>();

        RuntimeUIFactory.SetRect(
            rect,
            Vector2.zero,
            Vector2.one,
            Vector2.zero,
            Vector2.zero);

        RawImage image =
            patternObject.GetComponent<RawImage>();

        image.texture =
            CreateMedievalPatternTexture(
                512,
                288);

        image.color =
            Color.white;

        image.raycastTarget =
            false;

        patternObject.transform.SetAsFirstSibling();
    }

    private static Texture2D CreateMedievalPatternTexture(
        int width,
        int height)
    {
        Texture2D texture =
            new Texture2D(
                width,
                height,
                TextureFormat.RGBA32,
                false);

        texture.name =
            "Runtime Blood Medieval Pattern";

        texture.filterMode =
            FilterMode.Bilinear;

        texture.wrapMode =
            TextureWrapMode.Clamp;

        Color32[] pixels =
            new Color32[width * height];

        Color32 blood =
            new Color32(
                112,
                2,
                17,
                150);

        Color32 faintBlood =
            new Color32(
                82,
                0,
                12,
                95);

        int margin = 22;

        DrawLine(pixels, width, height, margin, margin, width - margin, margin, blood, 3);
        DrawLine(pixels, width, height, margin, height - margin, width - margin, height - margin, blood, 3);
        DrawLine(pixels, width, height, margin, margin, margin, height - margin, blood, 3);
        DrawLine(pixels, width, height, width - margin, margin, width - margin, height - margin, blood, 3);

        // Double medieval frame.
        DrawLine(pixels, width, height, margin + 10, margin + 10, width - margin - 10, margin + 10, faintBlood, 2);
        DrawLine(pixels, width, height, margin + 10, height - margin - 10, width - margin - 10, height - margin - 10, faintBlood, 2);

        // Repeating diamond/runic ornaments.
        for (int x = 58; x < width - 58; x += 58)
        {
            DrawDiamond(pixels, width, height, x, margin, 9, blood, 2);
            DrawDiamond(pixels, width, height, x, height - margin, 9, blood, 2);
        }

        for (int y = 62; y < height - 62; y += 54)
        {
            DrawDiamond(pixels, width, height, margin, y, 8, blood, 2);
            DrawDiamond(pixels, width, height, width - margin, y, 8, blood, 2);
        }

        // Large corner flourishes.
        DrawCornerFlourish(pixels, width, height, margin + 5, margin + 5, 1, 1, blood);
        DrawCornerFlourish(pixels, width, height, width - margin - 5, margin + 5, -1, 1, blood);
        DrawCornerFlourish(pixels, width, height, margin + 5, height - margin - 5, 1, -1, blood);
        DrawCornerFlourish(pixels, width, height, width - margin - 5, height - margin - 5, -1, -1, blood);

        // Central sigils, kept subtle behind the buttons.
        DrawDiamond(pixels, width, height, width / 2, height / 2, 64, faintBlood, 4);
        DrawDiamond(pixels, width, height, width / 2, height / 2, 38, faintBlood, 2);
        DrawLine(pixels, width, height, width / 2 - 70, height / 2, width / 2 + 70, height / 2, faintBlood, 2);
        DrawLine(pixels, width, height, width / 2, height / 2 - 70, width / 2, height / 2 + 70, faintBlood, 2);

        texture.SetPixels32(pixels);
        texture.Apply(false, false);
        return texture;
    }

    private static void DrawCornerFlourish(
        Color32[] pixels,
        int width,
        int height,
        int x,
        int y,
        int xDirection,
        int yDirection,
        Color32 color)
    {
        DrawLine(pixels, width, height, x, y, x + 55 * xDirection, y, color, 3);
        DrawLine(pixels, width, height, x, y, x, y + 55 * yDirection, color, 3);
        DrawLine(pixels, width, height, x + 8 * xDirection, y + 8 * yDirection, x + 42 * xDirection, y + 42 * yDirection, color, 2);
        DrawDiamond(pixels, width, height, x + 48 * xDirection, y + 48 * yDirection, 9, color, 2);
    }

    private static void DrawDiamond(
        Color32[] pixels,
        int width,
        int height,
        int centerX,
        int centerY,
        int radius,
        Color32 color,
        int thickness)
    {
        DrawLine(pixels, width, height, centerX, centerY - radius, centerX + radius, centerY, color, thickness);
        DrawLine(pixels, width, height, centerX + radius, centerY, centerX, centerY + radius, color, thickness);
        DrawLine(pixels, width, height, centerX, centerY + radius, centerX - radius, centerY, color, thickness);
        DrawLine(pixels, width, height, centerX - radius, centerY, centerX, centerY - radius, color, thickness);
    }

    private static void DrawLine(
        Color32[] pixels,
        int width,
        int height,
        int x0,
        int y0,
        int x1,
        int y1,
        Color32 color,
        int thickness)
    {
        int dx = Mathf.Abs(x1 - x0);
        int sx = x0 < x1 ? 1 : -1;
        int dy = -Mathf.Abs(y1 - y0);
        int sy = y0 < y1 ? 1 : -1;
        int error = dx + dy;

        while (true)
        {
            for (int oy = -thickness; oy <= thickness; oy++)
            {
                for (int ox = -thickness; ox <= thickness; ox++)
                {
                    int px = x0 + ox;
                    int py = y0 + oy;

                    if (px >= 0 && px < width && py >= 0 && py < height)
                        pixels[py * width + px] = color;
                }
            }

            if (x0 == x1 && y0 == y1)
                break;

            int twiceError = 2 * error;

            if (twiceError >= dy)
            {
                error += dy;
                x0 += sx;
            }

            if (twiceError <= dx)
            {
                error += dx;
                y0 += sy;
            }
        }
    }

    private void BuildSettings()
    {
        settingsRoot = RuntimeUIFactory.CreatePanel(rootCanvas.transform, "Settings", Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero, new Color(0.025f, 0.026f, 0.030f, 1f));
        RuntimeUIFactory.CreateText(settingsRoot.transform, "Settings Title", "НАСТРОЙКИ", 48, new Vector2(0.25f, 0.88f), new Vector2(0.75f, 0.98f), Vector2.zero, Vector2.zero, TextAnchor.MiddleCenter, new Color(0.81f, 0.81f, 0.80f, 1f), FontStyle.Bold);
        GameObject controls = RuntimeUIFactory.CreatePanel(settingsRoot.transform, "Controls Panel", new Vector2(0.05f, 0.13f), new Vector2(0.49f, 0.86f), Vector2.zero, Vector2.zero, new Color(0.055f, 0.056f, 0.062f, 0.96f));
        RuntimeUIFactory.CreateText(controls.transform, "Controls Title", "УПРАВЛЕНИЕ", 28, new Vector2(0.05f, 0.90f), new Vector2(0.95f, 0.99f), Vector2.zero, Vector2.zero, TextAnchor.MiddleLeft, new Color(0.67f, 0.12f, 0.13f, 1f), FontStyle.Bold);
        CreateBindingRow(controls.transform, "ВВЕРХ", GameManager.BindingAction.MoveUp, 0.83f);
        CreateBindingRow(controls.transform, "ВНИЗ", GameManager.BindingAction.MoveDown, 0.73f);
        CreateBindingRow(controls.transform, "ВЛЕВО", GameManager.BindingAction.MoveLeft, 0.63f);
        CreateBindingRow(controls.transform, "ВПРАВО", GameManager.BindingAction.MoveRight, 0.53f);
        CreateBindingRow(controls.transform, "ВЗАИМОДЕЙСТВИЕ", GameManager.BindingAction.Interact, 0.43f);
        CreateBindingRow(controls.transform, "БРОСОК", GameManager.BindingAction.Fire, 0.33f);
        CreateBindingRow(controls.transform, "ПРИМЕНИТЬ ПРЕДМЕТ", GameManager.BindingAction.UseActiveItem, 0.23f);
        CreateBindingRow(controls.transform, "СМЕНА ПРЕДМЕТА", GameManager.BindingAction.SwapActiveItem, 0.13f);
        // Reset Defaults shares the bottom navigation row with Back instead of
        // overlapping the final control binding row.

        GameObject values = RuntimeUIFactory.CreatePanel(settingsRoot.transform, "Values Panel", new Vector2(0.51f, 0.13f), new Vector2(0.95f, 0.86f), Vector2.zero, Vector2.zero, new Color(0.055f, 0.056f, 0.062f, 0.96f));
        RuntimeUIFactory.CreateText(values.transform, "Values Title", "ИГРА И ЗВУК", 28, new Vector2(0.05f, 0.90f), new Vector2(0.95f, 0.99f), Vector2.zero, Vector2.zero, TextAnchor.MiddleLeft, new Color(0.67f, 0.12f, 0.13f, 1f), FontStyle.Bold);
        CreateSettingsSlider(values.transform, "ЯРКОСТЬ ЭКРАНА", 0f, 1f, 0.75f, out brightnessSlider, out brightnessValue);
        CreateSettingsSlider(values.transform, "ОБЩАЯ ГРОМКОСТЬ", 0f, 1f, 0.56f, out masterVolumeSlider, out masterVolumeValue);
        CreateSettingsSlider(values.transform, "ГРОМКОСТЬ МУЗЫКИ", 0f, 1f, 0.37f, out musicVolumeSlider, out musicVolumeValue);
        CreateSettingsSlider(values.transform, "ЗВУКИ И ВЗРЫВЫ", 0f, 1f, 0.18f, out sfxVolumeSlider, out sfxVolumeValue);
        brightnessSlider.onValueChanged.AddListener(v => { if (gameManager != null) gameManager.SetScreenBrightness(v); RefreshSettingsLabels(); ApplyBrightnessOverlay(); });
        masterVolumeSlider.onValueChanged.AddListener(v => { if (gameManager != null) gameManager.SetMasterVolume(v); RefreshSettingsLabels(); });
        musicVolumeSlider.onValueChanged.AddListener(v => { if (gameManager != null) gameManager.SetMusicVolume(v); RefreshSettingsLabels(); });
        sfxVolumeSlider.onValueChanged.AddListener(v => { if (gameManager != null) gameManager.SetSfxVolume(v); RefreshSettingsLabels(); });
        Button back = RuntimeUIFactory.CreateButton(settingsRoot.transform, "Back Button", "НАЗАД",
            new Vector2(0.05f, 0.035f), new Vector2(0.20f, 0.105f), Vector2.zero, Vector2.zero, 23);
        back.onClick.AddListener(HandleBackPressed);

        Button reset = RuntimeUIFactory.CreateButton(settingsRoot.transform, "Reset Controls", "СБРОСИТЬ НАСТРОЙКИ",
            new Vector2(0.218f, 0.035f), new Vector2(0.445f, 0.105f), Vector2.zero, Vector2.zero, 19);
        reset.onClick.AddListener(ResetControls);
    }

    private void BuildPauseScreen()
    {
        pauseRoot = RuntimeUIFactory.CreatePanel(rootCanvas.transform, "Pause Overlay", Vector2.zero, Vector2.one,
            Vector2.zero, Vector2.zero, new Color(0.005f, 0.005f, 0.01f, 0.86f));
        GameObject panel = RuntimeUIFactory.CreatePanel(pauseRoot.transform, "Pause Parchment",
            new Vector2(0.32f, 0.19f), new Vector2(0.68f, 0.81f), Vector2.zero, Vector2.zero, Color.white);
        RuntimeUIFactory.ApplyParchmentFrame(panel);
        RuntimeUIFactory.CreateText(panel.transform, "Pause Title", "ПАУЗА", 42,
            new Vector2(0.08f, 0.70f), new Vector2(0.92f, 0.92f), Vector2.zero, Vector2.zero,
            TextAnchor.MiddleCenter, new Color(0.42f, 0.02f, 0.03f, 1f), FontStyle.Bold);
        Button resume = RuntimeUIFactory.CreateButton(panel.transform, "Resume", "ПРОДОЛЖИТЬ",
            new Vector2(0.22f, 0.50f), new Vector2(0.78f, 0.61f), Vector2.zero, Vector2.zero, 25);
        resume.onClick.AddListener(() => { if (gameManager != null) gameManager.ResumeGameplay(); });
        Button settings = RuntimeUIFactory.CreateButton(panel.transform, "Pause Settings", "НАСТРОЙКИ",
            new Vector2(0.22f, 0.34f), new Vector2(0.78f, 0.45f), Vector2.zero, Vector2.zero, 25);
        settings.onClick.AddListener(() => { if (gameManager != null) gameManager.OpenSettings(); });
        Button mainMenu = RuntimeUIFactory.CreateButton(panel.transform, "Quit to Menu", "ГЛАВНОЕ МЕНЮ",
            new Vector2(0.22f, 0.18f), new Vector2(0.78f, 0.29f), Vector2.zero, Vector2.zero, 25);
        mainMenu.onClick.AddListener(() => { if (gameManager != null) gameManager.ReturnToMainMenu(true); });
        pauseRoot.SetActive(false);
    }

    private void CreateBindingRow(Transform parent, string label, GameManager.BindingAction action, float centerY)
    {
        float h = 0.038f;
        RuntimeUIFactory.CreateText(parent, label + " Label", label, 18, new Vector2(0.05f, centerY - h), new Vector2(0.49f, centerY + h), Vector2.zero, Vector2.zero, TextAnchor.MiddleLeft, new Color(0.78f, 0.78f, 0.77f, 1f), FontStyle.Normal);
        Button button = RuntimeUIFactory.CreateButton(parent, label + " Binding", "...", new Vector2(0.55f, centerY - h), new Vector2(0.94f, centerY + h), Vector2.zero, Vector2.zero, 18);
        Text text = button.GetComponentInChildren<Text>(); bindingButtonTexts[action] = text; button.onClick.AddListener(() => BeginRebinding(action));
    }

    private void CreateSettingsSlider(Transform parent, string label, float min, float max, float centerY, out Slider slider, out Text valueText)
    {
        RuntimeUIFactory.CreateText(parent, label + " Label", label, 18, new Vector2(0.05f, centerY + 0.035f), new Vector2(0.73f, centerY + 0.105f), Vector2.zero, Vector2.zero, TextAnchor.MiddleLeft, new Color(0.78f, 0.78f, 0.77f, 1f), FontStyle.Normal);
        valueText = RuntimeUIFactory.CreateText(parent, label + " Value", "0", 17, new Vector2(0.73f, centerY + 0.035f), new Vector2(0.95f, centerY + 0.105f), Vector2.zero, Vector2.zero, TextAnchor.MiddleRight, new Color(0.58f, 0.60f, 0.62f, 1f), FontStyle.Normal);
        slider = RuntimeUIFactory.CreateSlider(parent, label + " Slider", new Vector2(0.05f, centerY - 0.025f), new Vector2(0.95f, centerY + 0.025f), Vector2.zero, Vector2.zero); slider.minValue = min; slider.maxValue = max;
    }

    private void BuildBrightnessOverlay()
    {
        GameObject overlay = RuntimeUIFactory.CreatePanel(rootCanvas.transform, "Brightness Overlay", Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero, Color.clear); brightnessOverlay = overlay.GetComponent<Image>(); brightnessOverlay.raycastTarget = false; overlay.transform.SetAsLastSibling();
    }

    private void HandlePlayPressed() { if (gameManager == null) TryBindGameManager(); if (gameManager == null) return; Time.timeScale = 1f; gameManager.BeginNewRun(true); }
    private void HandleSettingsPressed() { if (gameManager != null) gameManager.OpenSettings(); }
    private void HandleExitPressed() { if (gameManager != null) gameManager.QuitGame(); else Application.Quit(); }
    private void HandleBackPressed() { awaitingBinding = false; if (gameManager != null) gameManager.CloseSettings(); }

    private void BeginRebinding(GameManager.BindingAction action)
    {
        if (gameManager == null) return;
        if (awaitingBinding && bindingButtonTexts.TryGetValue(pendingBindingAction, out Text oldText)) oldText.text = FormatKey(gameManager.GetBinding(pendingBindingAction));
        awaitingBinding = true; pendingBindingAction = action; bindingInputAllowedFrame = Time.frameCount + 1; if (bindingButtonTexts.TryGetValue(action, out Text current)) current.text = "НАЖМИТЕ КЛАВИШУ...";
    }

    private void CaptureBindingInput()
    {
        if (gameManager == null || Time.frameCount < bindingInputAllowedFrame) return;
        if (Input.GetKeyDown(KeyCode.Escape)) { awaitingBinding = false; RefreshBindingButtons(); return; }
        if (!Input.anyKeyDown) return;
        Array values = Enum.GetValues(typeof(KeyCode));
        for (int i = 0; i < values.Length; i++)
        {
            KeyCode key = (KeyCode)values.GetValue(i); if (key == KeyCode.None || key == KeyCode.Escape || !Input.GetKeyDown(key)) continue;
            gameManager.SetBinding(pendingBindingAction, key); awaitingBinding = false; RefreshBindingButtons(); return;
        }
    }

    private void ResetControls() { if (gameManager == null) return; awaitingBinding = false; gameManager.ResetBindingsToDefaults(); RefreshBindingButtons(); }
    private void RefreshBindingButtons() { if (gameManager == null) return; foreach (var pair in bindingButtonTexts) pair.Value.text = awaitingBinding && pair.Key == pendingBindingAction ? "НАЖМИТЕ КЛАВИШУ..." : FormatKey(gameManager.GetBinding(pair.Key)); }
    private static string FormatKey(KeyCode key)
    {
        switch (key) { case KeyCode.Mouse0: return "ЛКМ"; case KeyCode.Mouse1: return "МЫШЬ 2"; case KeyCode.Mouse2: return "МЫШЬ 3"; case KeyCode.Space: return "ПРОБЕЛ"; case KeyCode.LeftShift: return "L-SHIFT"; case KeyCode.RightShift: return "R-SHIFT"; case KeyCode.LeftControl: return "L-CTRL"; case KeyCode.RightControl: return "R-CTRL"; default: return key.ToString().ToUpperInvariant(); }
    }

    private void SyncSettingsControls()
    {
        if (gameManager == null || brightnessSlider == null) return;
        brightnessSlider.SetValueWithoutNotify(gameManager.ScreenBrightness); masterVolumeSlider.SetValueWithoutNotify(gameManager.MasterVolume); musicVolumeSlider.SetValueWithoutNotify(gameManager.MusicVolume); sfxVolumeSlider.SetValueWithoutNotify(gameManager.SfxVolume); RefreshSettingsLabels();
    }
    private void RefreshSettingsLabels()
    {
        if (gameManager == null || brightnessValue == null) return;
        brightnessValue.text = Mathf.RoundToInt(gameManager.ScreenBrightness * 100f) + "%"; masterVolumeValue.text = Mathf.RoundToInt(gameManager.MasterVolume * 100f) + "%"; musicVolumeValue.text = Mathf.RoundToInt(gameManager.MusicVolume * 100f) + "%"; sfxVolumeValue.text = Mathf.RoundToInt(gameManager.SfxVolume * 100f) + "%";
    }
    private void ApplyBrightnessOverlay() { if (brightnessOverlay == null) return; float b = gameManager != null ? gameManager.ScreenBrightness : 1f; brightnessOverlay.color = new Color(0f, 0f, 0f, (1f - Mathf.Clamp01(b)) * 0.86f); }
    private void RefreshEntireInterface() { RefreshVisibility(); SyncSettingsControls(); RefreshBindingButtons(); ApplyBrightnessOverlay(); }
    private void RefreshVisibility()
    {
        GameManager.GameState state = gameManager != null ? gameManager.CurrentState : GameManager.GameState.MAIN_MENU;
        if (mainMenuRoot != null) mainMenuRoot.SetActive(state == GameManager.GameState.MAIN_MENU);
        if (settingsRoot != null) settingsRoot.SetActive(state == GameManager.GameState.SETTINGS);
        if (pauseRoot != null) pauseRoot.SetActive(gameManager != null && state == GameManager.GameState.GAMEPLAY && gameManager.IsGameplayPaused);
    }
}

internal sealed class MenuButtonHoverFeedback : MonoBehaviour,
    IPointerEnterHandler, IPointerExitHandler, IPointerDownHandler, IPointerUpHandler
{
    public Graphic TargetGraphic { get; set; }
    public GameObject DaggerIndicator { get; set; }

    private RectTransform rect;
    private Vector3 originalScale;
    private Vector3 desiredScale;
    private Color normalColor;
    private Color desiredColor;
    private bool hovered;

    private void Awake()
    {
        rect = GetComponent<RectTransform>();
        originalScale = rect != null ? rect.localScale : Vector3.one;
        desiredScale = originalScale;
        if (TargetGraphic == null) TargetGraphic = GetComponent<Graphic>();
        normalColor = TargetGraphic != null ? TargetGraphic.color : Color.white;
        desiredColor = normalColor;
    }

    private void OnEnable()
    {
        hovered = false;
        if (rect != null) rect.localScale = originalScale;
        if (TargetGraphic != null)
        {
            normalColor = TargetGraphic.color;
            desiredColor = normalColor;
        }
        if (DaggerIndicator != null) DaggerIndicator.SetActive(false);
    }

    private void OnDisable()
    {
        hovered = false;
        if (rect != null) rect.localScale = originalScale;
        if (TargetGraphic != null) TargetGraphic.color = normalColor;
        if (DaggerIndicator != null) DaggerIndicator.SetActive(false);
    }

    private void Update()
    {
        float t = Mathf.Clamp01(Time.unscaledDeltaTime * 13f);
        if (rect != null) rect.localScale = Vector3.Lerp(rect.localScale, desiredScale, t);
        if (TargetGraphic != null) TargetGraphic.color = Color.Lerp(TargetGraphic.color, desiredColor, t);
    }

    public void OnPointerEnter(PointerEventData eventData)
    {
        hovered = true;
        desiredScale = originalScale * 1.025f;
        desiredColor = new Color(0.70f, 0.08f, 0.09f, 0.88f);
        if (DaggerIndicator != null) DaggerIndicator.SetActive(true);
    }

    public void OnPointerExit(PointerEventData eventData)
    {
        hovered = false;
        desiredScale = originalScale;
        desiredColor = normalColor;
        if (DaggerIndicator != null) DaggerIndicator.SetActive(false);
    }

    public void OnPointerDown(PointerEventData eventData)
    {
        desiredScale = originalScale * 0.985f;
        desiredColor = new Color(0.85f, 0.13f, 0.13f, 0.94f);
    }

    public void OnPointerUp(PointerEventData eventData)
    {
        desiredScale = hovered ? originalScale * 1.025f : originalScale;
        desiredColor = hovered ? new Color(0.70f, 0.08f, 0.09f, 0.88f) : normalColor;
    }
}

internal static class RuntimeUIFactory
{
    private static Font cachedLegacyFont;
    private static Texture2D cachedParchmentTexture;
    private static Sprite cachedBrushStrokeSprite;
    private static Texture2D cachedBrushStrokeTexture;
    public static Font LegacyFont => cachedLegacyFont != null ? cachedLegacyFont : (cachedLegacyFont = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf"));

    public static Sprite GetBrushStrokeSprite()
    {
        if (cachedBrushStrokeSprite != null)
            return cachedBrushStrokeSprite;

        const int width = 256;
        const int height = 64;
        cachedBrushStrokeTexture = new Texture2D(width, height, TextureFormat.RGBA32, false)
        {
            name = "Runtime Menu Brush Stroke",
            filterMode = FilterMode.Bilinear,
            wrapMode = TextureWrapMode.Clamp
        };
        Color[] pixels = new Color[width * height];
        Color brush = new Color(0.54f, 0.05f, 0.05f, 0.94f);
        Color darkBrush = new Color(0.34f, 0.02f, 0.02f, 0.94f);
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
        {
            float nx = x / (float)(width - 1);
            float ny = y / (float)(height - 1);
            float centerFalloff = 1f - Mathf.Abs(ny - 0.5f) * 1.55f;
            float ragged = Mathf.PerlinNoise(nx * 8.5f, ny * 6.3f) * 0.40f + Mathf.PerlinNoise((nx + 0.5f) * 14.1f, (ny + 0.17f) * 12.4f) * 0.20f;
            float alpha = Mathf.Clamp01(centerFalloff + ragged - 0.18f);
            alpha *= Mathf.SmoothStep(0.18f, 0.02f, nx) + 0.45f;
            alpha *= Mathf.SmoothStep(0.06f, 0.34f, nx);
            Color c = Color.Lerp(darkBrush, brush, Mathf.Clamp01(0.35f + nx * 0.65f));
            pixels[y * width + x] = new Color(c.r, c.g, c.b, alpha);
        }
        cachedBrushStrokeTexture.SetPixels(pixels);
        cachedBrushStrokeTexture.Apply(false, false);
        cachedBrushStrokeSprite = Sprite.Create(cachedBrushStrokeTexture, new Rect(0, 0, width, height), new Vector2(0.5f, 0.5f), 100f, 0, SpriteMeshType.FullRect, new Vector4(24f, 12f, 24f, 12f));
        return cachedBrushStrokeSprite;
    }

    private static Texture2D GetParchmentTexture()
    {
        if (cachedParchmentTexture != null)
            return cachedParchmentTexture;

        const int size = 256;
        cachedParchmentTexture = new Texture2D(size, size, TextureFormat.RGBA32, false)
        {
            name = "Runtime Aged Parchment",
            filterMode = FilterMode.Bilinear,
            wrapMode = TextureWrapMode.Clamp
        };
        Color[] pixels = new Color[size * size];
        Color baseColor = new Color(0.92f, 0.89f, 0.77f, 1f);
        Color stain = new Color(0.76f, 0.68f, 0.48f, 1f);
        Color darkEdge = new Color(0.56f, 0.42f, 0.24f, 1f);
        for (int y = 0; y < size; y++)
        for (int x = 0; x < size; x++)
        {
            float nx = x / (float)(size - 1);
            float ny = y / (float)(size - 1);
            float noise = Mathf.PerlinNoise(nx * 6.5f, ny * 6.1f) * 0.55f + Mathf.PerlinNoise((nx + 0.43f) * 14f, (ny + 0.12f) * 12f) * 0.25f;
            float speckle = Mathf.PerlinNoise((nx + 0.11f) * 40f, (ny + 0.53f) * 40f);
            float edge = Mathf.Min(Mathf.Min(nx, 1f - nx), Mathf.Min(ny, 1f - ny));
            float edgeDark = Mathf.Clamp01(1f - edge * 7f);
            Color c = Color.Lerp(baseColor, stain, Mathf.Clamp01(noise * 0.55f));
            c = Color.Lerp(c, darkEdge, edgeDark * 0.36f);
            if (speckle > 0.82f)
                c = Color.Lerp(c, new Color(0.49f, 0.36f, 0.20f, 1f), (speckle - 0.82f) * 1.8f);
            pixels[y * size + x] = c;
        }
        cachedParchmentTexture.SetPixels(pixels);
        cachedParchmentTexture.Apply(false, false);
        return cachedParchmentTexture;
    }

    public static void EnsureEventSystem()
    {
        if (EventSystem.current != null) return;
        GameObject obj = new GameObject("Runtime EventSystem"); obj.AddComponent<EventSystem>(); obj.AddComponent<StandaloneInputModule>();
    }

    public static Canvas CreateCanvas(Transform parent, string name, int sortingOrder)
    {
        GameObject obj = new GameObject(name, typeof(RectTransform)); obj.transform.SetParent(parent, false);
        Canvas canvas = obj.AddComponent<Canvas>(); canvas.renderMode = RenderMode.ScreenSpaceOverlay; canvas.sortingOrder = sortingOrder;
        CanvasScaler scaler = obj.AddComponent<CanvasScaler>(); scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize; scaler.referenceResolution = new Vector2(1920f, 1080f); scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight; scaler.matchWidthOrHeight = 0.5f;
        obj.AddComponent<GraphicRaycaster>(); return canvas;
    }

    public static GameObject CreatePanel(Transform parent, string name, Vector2 anchorMin, Vector2 anchorMax, Vector2 offsetMin, Vector2 offsetMax, Color color)
    {
        GameObject obj = new GameObject(name, typeof(RectTransform), typeof(Image)); obj.transform.SetParent(parent, false); SetRect(obj.GetComponent<RectTransform>(), anchorMin, anchorMax, offsetMin, offsetMax); obj.GetComponent<Image>().color = color; return obj;
    }

    public static void ApplyParchmentFrame(GameObject panel)
    {
        if (panel == null)
            return;

        Image outer = panel.GetComponent<Image>();
        if (outer != null)
            outer.color = new Color(0.25f, 0.03f, 0.02f, 0.98f);

        // TAB, tutorial, pause and the shared defeat/victory card get a much
        // thicker double-layer medieval frame. Keep small item cards compact.
        bool ceremonialDialog = panel.name == "TAB Character Screen" ||
            panel.name == "Tutorial Card" ||
            panel.name == "Pause Parchment" ||
            panel.name == "Medieval Victory and Defeat Frame";
        float border = ceremonialDialog ? 16f : 5f;

        if (ceremonialDialog)
        {
            GameObject goldRim = new GameObject("Wide Aged Gold Inlay", typeof(RectTransform), typeof(Image));
            goldRim.transform.SetParent(panel.transform, false);
            SetRect(goldRim.GetComponent<RectTransform>(), Vector2.zero, Vector2.one,
                new Vector2(7f, 7f), new Vector2(-7f, -7f));
            Image rimImage = goldRim.GetComponent<Image>();
            rimImage.color = new Color(0.59f, 0.38f, 0.14f, 1f);
            rimImage.raycastTarget = false;
            goldRim.transform.SetAsFirstSibling();
        }

        GameObject inner = new GameObject("Parchment Interior", typeof(RectTransform), typeof(Image));
        inner.transform.SetParent(panel.transform, false);
        SetRect(inner.GetComponent<RectTransform>(), Vector2.zero, Vector2.one,
            new Vector2(border, border), new Vector2(-border, -border));
        Image image = inner.GetComponent<Image>();
        image.color = new Color(1f, 1f, 1f, 0.08f);
        image.raycastTarget = false;

        GameObject parchment = new GameObject("Aged Parchment", typeof(RectTransform), typeof(RawImage));
        parchment.transform.SetParent(inner.transform, false);
        SetRect(parchment.GetComponent<RectTransform>(), Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero);
        RawImage parchmentImage = parchment.GetComponent<RawImage>();
        parchmentImage.texture = GetParchmentTexture();
        parchmentImage.color = Color.white;
        parchmentImage.raycastTarget = false;

        // Keep the parchment behind all headings and frame engravings.
        inner.transform.SetSiblingIndex(ceremonialDialog ? 1 : 0);

        AddFrameCorner(panel.transform, new Vector2(0f, 0f), 45f);
        AddFrameCorner(panel.transform, new Vector2(1f, 0f), -45f);
        AddFrameCorner(panel.transform, new Vector2(0f, 1f), -45f);
        AddFrameCorner(panel.transform, new Vector2(1f, 1f), 45f);

        // Thin inset gold rules and engraved red-gold filigree decorations.
        AddFrameRule(panel.transform, "Gold Engraving Top", new Vector2(0.015f, 1f),
            new Vector2(0.985f, 1f), new Vector2(0f, -13f), new Vector2(0f, -10f));
        AddFrameRule(panel.transform, "Gold Engraving Bottom", new Vector2(0.015f, 0f),
            new Vector2(0.985f, 0f), new Vector2(0f, 10f), new Vector2(0f, 13f));
        AddFrameRule(panel.transform, "Gold Engraving Left", new Vector2(0f, 0.025f),
            new Vector2(0f, 0.975f), new Vector2(10f, 0f), new Vector2(13f, 0f));
        AddFrameRule(panel.transform, "Gold Engraving Right", new Vector2(1f, 0.025f),
            new Vector2(1f, 0.975f), new Vector2(-13f, 0f), new Vector2(-10f, 0f));

        AddGothicMedallion(panel.transform, new Vector2(0.5f, 1f), 0f);
        AddGothicMedallion(panel.transform, new Vector2(0.5f, 0f), 180f);
        AddGothicMedallion(panel.transform, new Vector2(0f, 0.5f), 90f);
        AddGothicMedallion(panel.transform, new Vector2(1f, 0.5f), -90f);

        // Larger illuminated gothic corner emblems, plus tiny scarlet gem rivets.
        // These four prominent dialog screens share a matching medieval border.
        if (ceremonialDialog)
        {
            AddGothicCornerEmblem(panel.transform, new Vector2(0f, 0f), 0f);
            AddGothicCornerEmblem(panel.transform, new Vector2(1f, 0f), 90f);
            AddGothicCornerEmblem(panel.transform, new Vector2(1f, 1f), 180f);
            AddGothicCornerEmblem(panel.transform, new Vector2(0f, 1f), 270f);
        }
    }

    private static Sprite cachedGothicCorner;
    private static Texture2D cachedGothicCornerTexture;

    private static void AddGothicCornerEmblem(Transform parent, Vector2 anchor, float angle)
    {
        if (cachedGothicCorner == null)
            BuildGothicCornerEmblem();

        GameObject objectName = new GameObject("Gothic Engraved Corner", typeof(RectTransform), typeof(Image));
        objectName.transform.SetParent(parent, false);
        RectTransform rect = objectName.GetComponent<RectTransform>();
        rect.anchorMin = anchor;
        rect.anchorMax = anchor;
        rect.pivot = new Vector2(0.5f, 0.5f);
        rect.sizeDelta = new Vector2(76f, 76f);
        rect.anchoredPosition = new Vector2(anchor.x > 0.5f ? -28f : 28f,
                                            anchor.y > 0.5f ? -28f : 28f);
        rect.localRotation = Quaternion.Euler(0f, 0f, angle);
        Image image = objectName.GetComponent<Image>();
        image.sprite = cachedGothicCorner;
        image.preserveAspect = true;
        image.raycastTarget = false;
    }

    private static void BuildGothicCornerEmblem()
    {
        const int size = 96;
        cachedGothicCornerTexture = new Texture2D(size, size, TextureFormat.RGBA32, false)
        {
            name = "Runtime Gothic Heraldic Corner",
            filterMode = FilterMode.Bilinear,
            wrapMode = TextureWrapMode.Clamp
        };
        Color[] colors = new Color[size * size];
        Color brass = new Color(0.70f, 0.47f, 0.18f, 1f);
        Color darkBrass = new Color(0.42f, 0.27f, 0.095f, 1f);
        Color ruby = new Color(0.52f, 0.016f, 0.042f, 1f);
        for (int y = 0; y < size; y++)
        for (int x = 0; x < size; x++)
        {
            // The local origin points outward; the accent arcs curve inward.
            float dx = x - 47.5f, dy = y - 47.5f;
            float radius = Mathf.Sqrt(dx * dx + dy * dy);
            float angle = Mathf.Atan2(dy, dx);
            float diamond = Mathf.Abs(dx) + Mathf.Abs(dy);
            bool brassRing = Mathf.Abs(radius - 34f) < 1.55f;
            bool insetRing = Mathf.Abs(radius - 27f) < 1.25f;
            bool thorn = Mathf.Abs(Mathf.Sin(angle * 8f) * 4f + 34f - radius) < 1.2f && radius < 42f;
            bool cross = (Mathf.Abs(dx) < 2f || Mathf.Abs(dy) < 2f) && radius < 22f;
            bool jewelBorder = Mathf.Abs(diamond - 18f) < 2f;
            bool jewel = diamond < 15f;
            colors[y * size + x] = jewel ? ruby : jewelBorder || brassRing || thorn || cross
                ? brass : insetRing ? darkBrass : Color.clear;
        }
        cachedGothicCornerTexture.SetPixels(colors);
        cachedGothicCornerTexture.Apply(false, false);
        cachedGothicCorner = Sprite.Create(cachedGothicCornerTexture,
            new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), 100f);
    }

    private static Sprite cachedFrameOrnament;
    private static Texture2D cachedFrameOrnamentTexture;

    private static void AddFrameRule(Transform parent, string name, Vector2 anchorMin,
        Vector2 anchorMax, Vector2 offsetMin, Vector2 offsetMax)
    {
        GameObject line = new GameObject(name, typeof(RectTransform), typeof(Image));
        line.transform.SetParent(parent, false);
        SetRect(line.GetComponent<RectTransform>(), anchorMin, anchorMax, offsetMin, offsetMax);
        Image image = line.GetComponent<Image>();
        image.color = new Color(0.54f, 0.36f, 0.13f, 0.85f);
        image.raycastTarget = false;
    }

    private static void AddGothicMedallion(Transform parent, Vector2 anchor, float rotation)
    {
        if (cachedFrameOrnament == null)
            BuildFrameOrnament();

        GameObject ornament = new GameObject("Engraved Gothic Fleur Ornament", typeof(RectTransform), typeof(Image));
        ornament.transform.SetParent(parent, false);
        RectTransform rect = ornament.GetComponent<RectTransform>();
        rect.anchorMin = anchor;
        rect.anchorMax = anchor;
        rect.pivot = new Vector2(0.5f, 0.5f);
        rect.sizeDelta = new Vector2(84f, 28f);
        rect.anchoredPosition = Vector2.zero;
        rect.localRotation = Quaternion.Euler(0f, 0f, rotation);
        Image image = ornament.GetComponent<Image>();
        image.sprite = cachedFrameOrnament;
        image.raycastTarget = false;
        image.preserveAspect = true;
    }

    private static void BuildFrameOrnament()
    {
        const int width = 96, height = 40;
        cachedFrameOrnamentTexture = new Texture2D(width, height, TextureFormat.RGBA32, false)
        {
            name = "Procedural Gothic Frame Filigree",
            filterMode = FilterMode.Bilinear,
            wrapMode = TextureWrapMode.Clamp
        };
        Color[] pixels = new Color[width * height];
        Color gold = new Color(0.53f, 0.35f, 0.14f, 0.95f);
        Color ruby = new Color(0.45f, 0.017f, 0.039f, 0.98f);
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
        {
            float dx = x - 47.5f;
            float dy = y - 19.5f;
            float ax = Mathf.Abs(dx);
            float ay = Mathf.Abs(dy);
            float diamond = ax / 13f + ay / 13f;
            bool gem = diamond < 0.78f;
            bool outline = Mathf.Abs(diamond - 1f) < 0.17f;
            float wingY = Mathf.Sin((ax - 12f) * 0.27f) * (10f - ax * 0.11f);
            bool scroll = ax > 14f && ax < 45f && Mathf.Abs(ay - Mathf.Abs(wingY)) < 1.6f;
            bool needle = ax > 12f && ax < 46f && ay < 1.3f;
            bool droplet = ax > 27f && ax < 38f && Mathf.Abs(ay - 6f) < 1.5f;
            pixels[y * width + x] = gem ? ruby : (outline || scroll || needle || droplet ? gold : Color.clear);
        }
        cachedFrameOrnamentTexture.SetPixels(pixels);
        cachedFrameOrnamentTexture.Apply(false, false);
        cachedFrameOrnament = Sprite.Create(cachedFrameOrnamentTexture,
            new Rect(0, 0, width, height), new Vector2(0.5f, 0.5f), 100f);
    }

    private static void AddFrameCorner(
        Transform parent,
        Vector2 anchor,
        float rotation)
    {
        GameObject corner =
            new GameObject(
                "Frame Corner",
                typeof(RectTransform),
                typeof(Image));

        corner.transform.SetParent(
            parent,
            false);

        RectTransform rect =
            corner.GetComponent<RectTransform>();

        rect.anchorMin = anchor;
        rect.anchorMax = anchor;
        rect.pivot = anchor;
        rect.sizeDelta = new Vector2(34f, 5f);
        rect.anchoredPosition = Vector2.zero;
        rect.localRotation = Quaternion.Euler(0f, 0f, rotation);

        Image image =
            corner.GetComponent<Image>();

        image.color =
            new Color(
                0.46f,
                0.015f,
                0.03f,
                1f);

        image.raycastTarget = false;
    }

    public static Text CreateText(Transform parent, string name, string content, int fontSize, Vector2 anchorMin, Vector2 anchorMax, Vector2 offsetMin, Vector2 offsetMax, TextAnchor alignment, Color color, FontStyle style)
    {
        GameObject obj = new GameObject(name, typeof(RectTransform), typeof(Text)); obj.transform.SetParent(parent, false); SetRect(obj.GetComponent<RectTransform>(), anchorMin, anchorMax, offsetMin, offsetMax);
        Text t = obj.GetComponent<Text>(); t.font = LegacyFont; t.text = content; t.fontSize = fontSize + 2; t.fontStyle = style; t.alignment = alignment; t.color = color; t.supportRichText = true; t.horizontalOverflow = HorizontalWrapMode.Wrap; t.verticalOverflow = VerticalWrapMode.Truncate; t.raycastTarget = false; return t;
    }

    public static Button CreateButton(Transform parent, string name, string label, Vector2 anchorMin, Vector2 anchorMax, Vector2 offsetMin, Vector2 offsetMax, int fontSize)
    {
        GameObject obj = new GameObject(name, typeof(RectTransform), typeof(Image), typeof(Button)); obj.transform.SetParent(parent, false); SetRect(obj.GetComponent<RectTransform>(), anchorMin, anchorMax, offsetMin, offsetMax);
        obj.GetComponent<Image>().color = new Color(0.115f, 0.115f, 0.125f, 0.98f); Button b = obj.GetComponent<Button>(); ColorBlock colors = b.colors; colors.highlightedColor = new Color(0.86f, 0.42f, 0.42f, 1f); colors.pressedColor = new Color(0.58f, 0.18f, 0.19f, 1f); colors.selectedColor = colors.highlightedColor; b.colors = colors;
        CreateText(obj.transform, "Label", label, fontSize, Vector2.zero, Vector2.one, new Vector2(8f, 4f), new Vector2(-8f, -4f), TextAnchor.MiddleCenter, new Color(0.88f, 0.88f, 0.86f, 1f), FontStyle.Bold); return b;
    }

    public static Slider CreateSlider(Transform parent, string name, Vector2 anchorMin, Vector2 anchorMax, Vector2 offsetMin, Vector2 offsetMax)
    {
        GameObject obj = new GameObject(name, typeof(RectTransform), typeof(Slider)); obj.transform.SetParent(parent, false); SetRect(obj.GetComponent<RectTransform>(), anchorMin, anchorMax, offsetMin, offsetMax);
        GameObject bg = new GameObject("Background", typeof(RectTransform), typeof(Image)); bg.transform.SetParent(obj.transform, false); SetRect(bg.GetComponent<RectTransform>(), new Vector2(0f, 0.25f), new Vector2(1f, 0.75f), Vector2.zero, Vector2.zero); bg.GetComponent<Image>().color = new Color(0.13f, 0.13f, 0.145f, 1f);
        GameObject fillArea = new GameObject("Fill Area", typeof(RectTransform)); fillArea.transform.SetParent(obj.transform, false); SetRect(fillArea.GetComponent<RectTransform>(), new Vector2(0f, 0.25f), new Vector2(1f, 0.75f), new Vector2(6f, 0f), new Vector2(-6f, 0f));
        GameObject fill = new GameObject("Fill", typeof(RectTransform), typeof(Image)); fill.transform.SetParent(fillArea.transform, false); SetRect(fill.GetComponent<RectTransform>(), Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero); fill.GetComponent<Image>().color = new Color(0.57f, 0.045f, 0.06f, 1f);
        GameObject handleArea = new GameObject("Handle Slide Area", typeof(RectTransform)); handleArea.transform.SetParent(obj.transform, false); SetRect(handleArea.GetComponent<RectTransform>(), Vector2.zero, Vector2.one, new Vector2(8f, 0f), new Vector2(-8f, 0f));
        GameObject handle = new GameObject("Handle", typeof(RectTransform), typeof(Image)); handle.transform.SetParent(handleArea.transform, false); handle.GetComponent<RectTransform>().sizeDelta = new Vector2(22f, 36f); handle.GetComponent<Image>().color = new Color(0.77f, 0.78f, 0.80f, 1f);
        Slider slider = obj.GetComponent<Slider>(); slider.direction = Slider.Direction.LeftToRight; slider.fillRect = fill.GetComponent<RectTransform>(); slider.handleRect = handle.GetComponent<RectTransform>(); slider.targetGraphic = handle.GetComponent<Image>(); return slider;
    }

    public static RectTransform CreateEmptyRect(Transform parent, string name, Vector2 anchorMin, Vector2 anchorMax, Vector2 offsetMin, Vector2 offsetMax)
    {
        GameObject obj = new GameObject(name, typeof(RectTransform)); obj.transform.SetParent(parent, false); RectTransform rect = obj.GetComponent<RectTransform>(); SetRect(rect, anchorMin, anchorMax, offsetMin, offsetMax); return rect;
    }

    public static void SetRect(RectTransform rect, Vector2 anchorMin, Vector2 anchorMax, Vector2 offsetMin, Vector2 offsetMax)
    {
        rect.anchorMin = anchorMin; rect.anchorMax = anchorMax; rect.offsetMin = offsetMin; rect.offsetMax = offsetMax; rect.localScale = Vector3.one; rect.localRotation = Quaternion.identity;
    }
}
