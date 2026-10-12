using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

[DisallowMultipleComponent]
public sealed class HUDManager : MonoBehaviour
{
    private sealed class HeartVisual
    {
        public GameObject Root;
        public Image Fill;
        public Image Half;
    }

    public static HUDManager Instance { get; private set; }

    private Canvas hudCanvas;
    private GameObject gameplayHudRoot;
    private GameObject tutorialRoot;
    private GameObject tabRoot;
    private GameObject gameOverRoot;

    private Text progressionText;
    private Text projectileText;
    private Text comboText;
    private RectTransform heartsContainer;
    // Each skull represents one enemy kill required to charge the Active Item.
    private RectTransform chargePanel;
    private readonly List<Image>[] skullRows = { new List<Image>(), new List<Image>() };
    private readonly Text[] skullRowLabels = new Text[2];
    private readonly GameObject[] skullRowObjects = new GameObject[2];
    private readonly Image[] activeHudIcons = new Image[2];
    private Texture2D skullTexture;
    private Sprite skullSprite;

    private Text activeSlotOneText;
    private Text activeSlotTwoText;
    private Text activeDescriptionText;
    private Image tabActiveIcon;
    private Texture2D tabActiveIconTexture;
    private Sprite tabActiveIconSprite;
    private bool tabActiveIconIsImported;
    private string tabActiveIconItemId;

    private Text tabStatsText;
    private Text tabItemsText;

    private Text tutorialHeaderText;
    private Text tutorialBodyText;
    private Text tutorialButtonText;
    private Button tutorialButton;
    private int tutorialPage;
    private bool tutorialDismissed;

    private Text gameOverTitleText;
    private Text gameOverStatsText;

    private readonly List<HeartVisual> heartVisuals = new List<HeartVisual>();
    private Texture2D heartTexture;
    private Sprite heartSprite;
    private Sprite halfHeartSprite;
    private Sprite emptyHeartSprite;
    private bool heartSpriteIsImported;

    private GameManager gameManager;
    private PlayerController player;
    private ItemSystem itemSystem;
    private bool tabWasVisible;

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }

        Instance = this;
        DontDestroyOnLoad(gameObject);
        RuntimeUIFactory.EnsureEventSystem();
        BuildHUD();
        SceneManager.sceneLoaded += HandleSceneLoaded;
    }

    private void Start()
    {
        TryBindGameManager();
        TryBindPlayer();
        TryBindItemSystem();
        RefreshAll();
    }

    private void Update()
    {
        if (gameManager == null) TryBindGameManager();
        if (player == null) TryBindPlayer();
        if (itemSystem == null) TryBindItemSystem();
        UpdateTabInput();
        RefreshProjectileCounter();
        RefreshComboDisplay();

        // Show the centered tutorial pages only AFTER the initial room fog has
        // expanded and the camera has returned to its full-arena position.
        if (gameManager != null && gameManager.CurrentState == GameManager.GameState.GAMEPLAY &&
            gameManager.IsTutorial && !tutorialDismissed && tutorialRoot != null)
        {
            bool shouldShowTutorial = GameplayRoomReveal.HasCompletedCurrentRoomReveal &&
                                      !GameplayRoomReveal.IsRevealing;
            if (tutorialRoot.activeSelf != shouldShowTutorial)
                RefreshTutorial();
        }
    }

    private void OnDestroy()
    {
        if (Instance == this) Instance = null;
        SceneManager.sceneLoaded -= HandleSceneLoaded;
        UnbindGameManager();
        UnbindPlayer();
        UnbindItemSystem();
        DestroyTabActiveIcon();
        if (heartSprite != null && !heartSpriteIsImported) Destroy(heartSprite);
        if (heartTexture != null) Destroy(heartTexture);
        if (skullSprite != null) Destroy(skullSprite);
        if (skullTexture != null) Destroy(skullTexture);
    }

    private void TryBindGameManager()
    {
        if (gameManager != null || GameManager.Instance == null) return;
        gameManager = GameManager.Instance;
        gameManager.StateChanged += HandleGameStateChanged;
        gameManager.ProgressionChanged += HandleProgressionChanged;
        gameManager.ScoreChanged += HandleScoreChanged;
        gameManager.KillCountChanged += HandleKillCountChanged;
        gameManager.ComboChanged += HandleComboChanged;
        gameManager.RunEnded += HandleRunEnded;
        RefreshAll();
    }

    private void UnbindGameManager()
    {
        if (gameManager == null) return;
        gameManager.StateChanged -= HandleGameStateChanged;
        gameManager.ProgressionChanged -= HandleProgressionChanged;
        gameManager.ScoreChanged -= HandleScoreChanged;
        gameManager.KillCountChanged -= HandleKillCountChanged;
        gameManager.ComboChanged -= HandleComboChanged;
        gameManager.RunEnded -= HandleRunEnded;
        gameManager = null;
    }

    private void TryBindPlayer()
    {
        PlayerController found = FindAnyObjectByType<PlayerController>();
        if (found != null && found != player) BindPlayer(found);
    }

    private void BindPlayer(PlayerController newPlayer)
    {
        UnbindPlayer();
        player = newPlayer;
        if (player == null) return;
        player.HealthChanged += HandleHealthChanged;
        player.StatsChanged += HandlePlayerStatsChanged;
        player.InventoryChanged += HandleInventoryChanged;
        player.SelectedActiveSlotChanged += HandleSelectedActiveSlotChanged;
        RebuildHeartContainers();
        RefreshHealth();
        RefreshActiveItems();
        RefreshTabContents();
    }

    private void UnbindPlayer()
    {
        if (player == null) return;
        player.HealthChanged -= HandleHealthChanged;
        player.StatsChanged -= HandlePlayerStatsChanged;
        player.InventoryChanged -= HandleInventoryChanged;
        player.SelectedActiveSlotChanged -= HandleSelectedActiveSlotChanged;
        player = null;
    }

    private void TryBindItemSystem()
    {
        if (itemSystem != null) return;
        itemSystem = ItemSystem.Instance != null ? ItemSystem.Instance : FindAnyObjectByType<ItemSystem>();
        if (itemSystem == null) return;
        itemSystem.PassiveItemCollected += HandlePassiveItemChanged;
        itemSystem.PassiveItemConsumed += HandlePassiveItemChanged;
        itemSystem.ActiveItemCollected += HandleActiveItemCollected;
        RefreshChargeSkulls();
    }

    private void UnbindItemSystem()
    {
        if (itemSystem == null) return;
        itemSystem.PassiveItemCollected -= HandlePassiveItemChanged;
        itemSystem.PassiveItemConsumed -= HandlePassiveItemChanged;
        itemSystem.ActiveItemCollected -= HandleActiveItemCollected;
        itemSystem = null;
    }

    private void BuildHUD()
    {
        hudCanvas = RuntimeUIFactory.CreateCanvas(transform, "Gameplay HUD Canvas", 100);
        gameplayHudRoot = new GameObject("Gameplay HUD", typeof(RectTransform));
        gameplayHudRoot.transform.SetParent(hudCanvas.transform, false);
        RuntimeUIFactory.SetRect(gameplayHudRoot.GetComponent<RectTransform>(), Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero);

        BuildTopHUD();
        BuildTabOverlay();
        BuildTutorialUI();
        BuildGameOverUI();

        heartSprite = RuntimeGameArt.Load("UI/Hearts_Full");
        halfHeartSprite = RuntimeGameArt.Load("UI/Hearts_Half");
        emptyHeartSprite = RuntimeGameArt.Load("UI/Hearts_Empty");
        heartSpriteIsImported = heartSprite != null;
        if (heartSprite == null)
        {
            heartTexture = CreateHeartTexture(64);
            heartSprite = Sprite.Create(heartTexture, new Rect(0f, 0f, 64f, 64f),
                new Vector2(0.5f, 0.5f), 64f, 0, SpriteMeshType.FullRect);
            heartSprite.name = "Runtime HUD Heart Fallback";
        }
        if (emptyHeartSprite == null) emptyHeartSprite = heartSprite;
    }

    private void BuildTopHUD()
    {
        GameObject top = RuntimeUIFactory.CreatePanel(gameplayHudRoot.transform, "Top HUD Background",
            // Gameplay camera stops at normalized Y=0.90, below this dedicated strip.
            new Vector2(0f, 0.90f), new Vector2(1f, 1f), Vector2.zero, Vector2.zero,
            new Color(0.015f, 0.015f, 0.018f, 1f));

        heartsContainer = RuntimeUIFactory.CreateEmptyRect(top.transform, "Heart Container",
            new Vector2(0.010f, 0.04f), new Vector2(0.34f, 0.96f), Vector2.zero, Vector2.zero);
        HorizontalLayoutGroup layout = heartsContainer.gameObject.AddComponent<HorizontalLayoutGroup>();
        layout.spacing = 8f;
        layout.childAlignment = TextAnchor.MiddleLeft;
        layout.childControlWidth = false;
        layout.childControlHeight = false;
        layout.childForceExpandWidth = false;
        layout.childForceExpandHeight = false;

        BuildChargeSkulls(top.transform);

        progressionText = RuntimeUIFactory.CreateText(top.transform, "Progression", "", 20,
            new Vector2(0.65f, 0f), new Vector2(0.865f, 1f), Vector2.zero, Vector2.zero,
            TextAnchor.MiddleCenter, new Color(0.80f, 0.80f, 0.79f, 1f), FontStyle.Bold);
        progressionText.resizeTextForBestFit = true;
        progressionText.resizeTextMinSize = 12;
        progressionText.resizeTextMaxSize = 22;

        comboText = RuntimeUIFactory.CreateText(gameplayHudRoot.transform, "Kill Combo Indicator", "", 34,
            new Vector2(0.77f, 0.78f), new Vector2(0.975f, 0.89f),
            Vector2.zero, Vector2.zero, TextAnchor.MiddleRight,
            new Color(1f, 0.76f, 0.16f, 1f), FontStyle.Bold);
        comboText.enabled = false;

        projectileText = RuntimeUIFactory.CreateText(top.transform, "Projectile Counter", "", 19,
            new Vector2(0.865f, 0f), new Vector2(0.985f, 1f), Vector2.zero, Vector2.zero,
            TextAnchor.MiddleRight, new Color(0.69f, 0.16f, 0.17f, 1f), FontStyle.Bold);
    }

    private void BuildChargeSkulls(Transform hudTop)
    {
        skullTexture = CreateSkullChargeTexture(40);
        skullSprite = Sprite.Create(skullTexture, new Rect(0, 0, 40, 40),
            new Vector2(0.5f, 0.5f), 40f, 0, SpriteMeshType.FullRect);
        skullSprite.name = "Череп — заряд за убийства";

        chargePanel = RuntimeUIFactory.CreateEmptyRect(hudTop, "Заряды активных предметов",
            new Vector2(0.225f, 0.02f), new Vector2(0.646f, 0.98f), Vector2.zero, Vector2.zero);

        // The upper 32% is reserved exclusively for the label, regardless
        // of how many active-item slots the Backpack unlocks. Skull rows live
        // side-by-side BELOW this area and can never cover the title.
        Text chargeLabel = RuntimeUIFactory.CreateText(chargePanel, "УБИЙСТВА = ЗАРЯДЫ",
            "УБИЙСТВА = ЗАРЯДЫ", 23,
            new Vector2(0.02f, 0.70f), new Vector2(0.99f, 1f),
            Vector2.zero, Vector2.zero, TextAnchor.MiddleLeft,
            new Color(1f, 0.88f, 0.63f, 1f), FontStyle.Bold);
        chargeLabel.resizeTextForBestFit = true;
        chargeLabel.resizeTextMinSize = 18;
        chargeLabel.resizeTextMaxSize = 23;

        for (int slot = 0; slot < 2; slot++)
        {
            GameObject rowObject = new GameObject("Слот " + (slot + 1) + " — черепа",
                typeof(RectTransform));
            rowObject.transform.SetParent(chargePanel, false);
            RectTransform rowRect = rowObject.GetComponent<RectTransform>();
            RuntimeUIFactory.SetRect(rowRect,
                new Vector2(slot == 0 ? 0f : 0.51f, 0.04f),
                new Vector2(slot == 0 ? 0.49f : 1f, 0.67f), Vector2.zero, Vector2.zero);
            skullRowObjects[slot] = rowObject;
            if (slot == 1) rowObject.SetActive(false);

            // The authored item icon is shown directly on the black HUD.
            // No extra background, frame or placeholder image is created.
            GameObject iconObject = new GameObject("Active Item Icon", typeof(RectTransform), typeof(Image));
            iconObject.transform.SetParent(rowObject.transform, false);
            RuntimeUIFactory.SetRect(iconObject.GetComponent<RectTransform>(),
                new Vector2(0.065f, 0.08f), new Vector2(0.235f, 0.92f), Vector2.zero, Vector2.zero);
            Image iconImage = iconObject.GetComponent<Image>();
            iconImage.preserveAspect = true;
            iconImage.raycastTarget = false;
            iconImage.enabled = false;
            activeHudIcons[slot] = iconImage;

            skullRowLabels[slot] = RuntimeUIFactory.CreateText(rowObject.transform,
                "Номер слота", (slot + 1).ToString(), 18,
                new Vector2(0f, 0f), new Vector2(0.065f, 1f), Vector2.zero, Vector2.zero,
                TextAnchor.MiddleCenter, new Color(0.76f, 0.72f, 0.63f, 1f), FontStyle.Bold);

            RectTransform icons = RuntimeUIFactory.CreateEmptyRect(rowObject.transform,
                "Черепа заряда", new Vector2(0.23f, 0f), new Vector2(1f, 1f),
                Vector2.zero, Vector2.zero);
            HorizontalLayoutGroup group = icons.gameObject.AddComponent<HorizontalLayoutGroup>();
            group.spacing = 4f;
            group.childAlignment = TextAnchor.MiddleLeft;
            group.childControlWidth = false;
            group.childControlHeight = false;
            group.childForceExpandWidth = false;
            group.childForceExpandHeight = false;

            for (int i = 0; i < 5; i++)
            {
                GameObject skullObject = new GameObject("Череп " + (i + 1),
                    typeof(RectTransform), typeof(Image), typeof(LayoutElement));
                skullObject.transform.SetParent(icons, false);
                RectTransform skullRect = skullObject.GetComponent<RectTransform>();
                skullRect.sizeDelta = new Vector2(52f, 52f);
                LayoutElement layout = skullObject.GetComponent<LayoutElement>();
                layout.minWidth = layout.preferredWidth = 52f;
                layout.minHeight = layout.preferredHeight = 52f;
                Image skull = skullObject.GetComponent<Image>();
                skull.sprite = skullSprite;
                skull.preserveAspect = true;
                skull.raycastTarget = false;
                skullRows[slot].Add(skull);
            }
        }
        RefreshChargeSkulls();
    }

    private void RefreshChargeSkulls()
    {
        for (int slotIndex = 0; slotIndex < skullRows.Length; slotIndex++)
        {
            // The Backpack passive, not a vacant inventory array slot, unlocks slot two.
            bool availableSlot = player != null && slotIndex < player.ActiveItemCapacity &&
                (slotIndex == 0 || (itemSystem != null && itemSystem.HasPassive(ItemSystem.ItemIds.Backpack)));
            if (skullRowObjects[slotIndex] != null)
                skullRowObjects[slotIndex].SetActive(availableSlot);
            if (!availableSlot) continue;

            PlayerController.ActiveItemSlot slot = player.GetActiveItemSlot(slotIndex);
            bool equipped = slot != null && !slot.IsEmpty;
            int count = equipped ? Mathf.Min(slot.RequiredKills, 5) : 0;
            int filled = equipped ? Mathf.Min(slot.StoredKills, count) : 0;
            bool selected = player.SelectedActiveSlotIndex == slotIndex;
            Image hudIcon = activeHudIcons[slotIndex];
            if (hudIcon != null)
            {
                Sprite icon = equipped ? RuntimeGameArt.Load("Items/" + slot.ItemId.Replace('.', '/')) : null;
                hudIcon.sprite = icon;
                hudIcon.enabled = icon != null;
                hudIcon.color = selected ? Color.white : new Color(0.75f, 0.75f, 0.75f, 1f);
            }
            if (skullRowLabels[slotIndex] != null)
                skullRowLabels[slotIndex].color = selected
                    ? new Color(0.95f, 0.84f, 0.58f, 1f)
                    : new Color(0.53f, 0.53f, 0.51f, 0.85f);

            for (int i = 0; i < skullRows[slotIndex].Count; i++)
            {
                Image skull = skullRows[slotIndex][i];
                if (skull == null) continue;
                skull.gameObject.SetActive(i < count);
                if (i >= count) continue;
                skull.color = i < filled
                    ? (selected ? new Color(0.97f, 0.83f, 0.53f, 1f)
                                : new Color(0.70f, 0.64f, 0.51f, 0.85f))
                    : new Color(0.24f, 0.24f, 0.25f, 0.90f);
            }
        }
    }

    private static Texture2D CreateSkullChargeTexture(int size)
    {
        Texture2D texture = new Texture2D(size, size, TextureFormat.RGBA32, false)
        {
            name = "Пиксельные черепа для заряда",
            filterMode = FilterMode.Point,
            wrapMode = TextureWrapMode.Clamp
        };
        Color[] pixels = new Color[size * size];
        for (int y = 0; y < size; y++)
        for (int x = 0; x < size; x++)
        {
            float nx = (x - (size - 1) * 0.5f) / (size * 0.5f);
            float ny = (y - (size - 1) * 0.5f) / (size * 0.5f);
            bool cranium = (nx * nx / 0.73f + Mathf.Pow((ny - 0.16f) / 0.76f, 2f)) < 0.96f;
            bool jaw = Mathf.Abs(nx) < 0.48f && ny < -0.18f && ny > -0.76f;
            bool eyeLeft = Mathf.Pow((nx + 0.28f) / 0.18f, 2f) + Mathf.Pow((ny - 0.02f) / 0.22f, 2f) < 1f;
            bool eyeRight = Mathf.Pow((nx - 0.28f) / 0.18f, 2f) + Mathf.Pow((ny - 0.02f) / 0.22f, 2f) < 1f;
            bool nose = Mathf.Abs(nx) < 0.08f && ny < -0.20f && ny > -0.37f;
            bool toothGap = (Mathf.Abs(nx - 0.14f) < 0.04f || Mathf.Abs(nx + 0.14f) < 0.04f)
                            && ny < -0.55f && ny > -0.77f;
            pixels[y * size + x] = (cranium || jaw) && !eyeLeft && !eyeRight && !nose && !toothGap
                ? Color.white : Color.clear;
        }
        texture.SetPixels(pixels);
        texture.Apply(false, false);
        return texture;
    }

    private void BuildTabOverlay()
    {
        tabRoot = RuntimeUIFactory.CreatePanel(hudCanvas.transform, "TAB Character Screen", new Vector2(0.18f, 0.14f), new Vector2(0.82f, 0.86f), Vector2.zero, Vector2.zero, Color.white);
        RuntimeUIFactory.ApplyParchmentFrame(tabRoot);

        RuntimeUIFactory.CreateText(tabRoot.transform, "Header", "ПЕРСОНАЖ И ИНВЕНТАРЬ", 32, new Vector2(0.05f, 0.86f), new Vector2(0.95f, 0.97f), Vector2.zero, Vector2.zero, TextAnchor.MiddleCenter, new Color(0.40f, 0.012f, 0.025f, 1f), FontStyle.Bold);

        GameObject divider = RuntimeUIFactory.CreatePanel(tabRoot.transform, "Vertical Divider", new Vector2(0.395f, 0.13f), new Vector2(0.400f, 0.83f), Vector2.zero, Vector2.zero, new Color(0.36f, 0.025f, 0.035f, 0.65f));
        divider.GetComponent<Image>().raycastTarget = false;

        tabStatsText = RuntimeUIFactory.CreateText(tabRoot.transform, "Stats", "", 22, new Vector2(0.055f, 0.15f), new Vector2(0.37f, 0.83f), Vector2.zero, Vector2.zero, TextAnchor.UpperLeft, new Color(0.055f, 0.045f, 0.04f, 1f), FontStyle.Normal);
        tabItemsText = RuntimeUIFactory.CreateText(tabRoot.transform, "Passives", "", 21, new Vector2(0.43f, 0.43f), new Vector2(0.94f, 0.82f), Vector2.zero, Vector2.zero, TextAnchor.UpperLeft, new Color(0.055f, 0.045f, 0.04f, 1f), FontStyle.Normal);

        GameObject activePanel = RuntimeUIFactory.CreatePanel(tabRoot.transform, "Active Item Card", new Vector2(0.43f, 0.12f), new Vector2(0.94f, 0.40f), Vector2.zero, Vector2.zero, Color.white);
        RuntimeUIFactory.ApplyParchmentFrame(activePanel);
        RuntimeUIFactory.CreateText(activePanel.transform, "Active Header", "АКТИВНЫЙ ПРЕДМЕТ", 20, new Vector2(0.04f, 0.72f), new Vector2(0.96f, 0.96f), Vector2.zero, Vector2.zero, TextAnchor.MiddleLeft, new Color(0.40f, 0.012f, 0.025f, 1f), FontStyle.Bold);

        GameObject iconObject = new GameObject("Active Icon", typeof(RectTransform), typeof(Image));
        iconObject.transform.SetParent(activePanel.transform, false);
        RectTransform iconRect = iconObject.GetComponent<RectTransform>();
        RuntimeUIFactory.SetRect(iconRect, new Vector2(0.05f, 0.16f), new Vector2(0.24f, 0.68f), Vector2.zero, Vector2.zero);
        tabActiveIcon = iconObject.GetComponent<Image>();
        tabActiveIcon.preserveAspect = true;
        tabActiveIcon.color = Color.white;

        activeSlotOneText = RuntimeUIFactory.CreateText(activePanel.transform, "Slot 1", "", 18, new Vector2(0.27f, 0.47f), new Vector2(0.96f, 0.70f), Vector2.zero, Vector2.zero, TextAnchor.MiddleLeft, new Color(0.06f, 0.05f, 0.045f, 1f), FontStyle.Bold);
        activeSlotTwoText = RuntimeUIFactory.CreateText(activePanel.transform, "Slot 2", "", 18, new Vector2(0.27f, 0.29f), new Vector2(0.96f, 0.50f), Vector2.zero, Vector2.zero, TextAnchor.MiddleLeft, new Color(0.06f, 0.05f, 0.045f, 1f), FontStyle.Bold);
        activeDescriptionText = RuntimeUIFactory.CreateText(activePanel.transform, "Description", "", 17, new Vector2(0.27f, 0.06f), new Vector2(0.96f, 0.31f), Vector2.zero, Vector2.zero, TextAnchor.UpperLeft, new Color(0.12f, 0.10f, 0.09f, 1f), FontStyle.Normal);

        RuntimeUIFactory.CreateText(tabRoot.transform, "Hint", "Отпустите TAB, чтобы закрыть", 15, new Vector2(0.30f, 0.025f), new Vector2(0.70f, 0.085f), Vector2.zero, Vector2.zero, TextAnchor.MiddleCenter, new Color(0.20f, 0.18f, 0.16f, 1f), FontStyle.Normal);
        tabRoot.SetActive(false);
    }

    private void BuildTutorialUI()
    {
        tutorialRoot = RuntimeUIFactory.CreatePanel(hudCanvas.transform, "Tutorial Modal", Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero, new Color(0f, 0f, 0f, 0.78f));
        tutorialRoot.GetComponent<Image>().raycastTarget = true;

        GameObject card = RuntimeUIFactory.CreatePanel(tutorialRoot.transform, "Tutorial Card", new Vector2(0.25f, 0.20f), new Vector2(0.75f, 0.80f), Vector2.zero, Vector2.zero, Color.white);
        RuntimeUIFactory.ApplyParchmentFrame(card);

        tutorialHeaderText = RuntimeUIFactory.CreateText(card.transform, "Tutorial Header", "", 30, new Vector2(0.08f, 0.73f), new Vector2(0.92f, 0.92f), Vector2.zero, Vector2.zero, TextAnchor.MiddleCenter, new Color(0.42f, 0.015f, 0.025f, 1f), FontStyle.Bold);
        tutorialBodyText = RuntimeUIFactory.CreateText(card.transform, "Tutorial Body", "", 22, new Vector2(0.10f, 0.27f), new Vector2(0.90f, 0.72f), Vector2.zero, Vector2.zero, TextAnchor.UpperLeft, new Color(0.055f, 0.045f, 0.04f, 1f), FontStyle.Normal);
        tutorialBodyText.lineSpacing = 1.08f;
        tutorialBodyText.resizeTextForBestFit = true;
        tutorialBodyText.resizeTextMinSize = 17;
        tutorialBodyText.resizeTextMaxSize = 22;

        tutorialButton = RuntimeUIFactory.CreateButton(card.transform, "Tutorial Next", "ДАЛЕЕ", new Vector2(0.34f, 0.08f), new Vector2(0.66f, 0.20f), Vector2.zero, Vector2.zero, 22);
        tutorialButtonText = tutorialButton.GetComponentInChildren<Text>();
        tutorialButton.onClick.AddListener(AdvanceTutorialPage);
        tutorialRoot.SetActive(false);
    }

    private void BuildGameOverUI()
    {
        // GAME OVER and VICTORY share the same Gothic parchment frame.
        gameOverRoot = RuntimeUIFactory.CreatePanel(hudCanvas.transform, "Game Over", Vector2.zero, Vector2.one,
            Vector2.zero, Vector2.zero, new Color(0.005f, 0.005f, 0.008f, 0.92f));

        GameObject card = RuntimeUIFactory.CreatePanel(gameOverRoot.transform, "Medieval Victory and Defeat Frame",
            new Vector2(0.275f, 0.14f), new Vector2(0.725f, 0.86f), Vector2.zero, Vector2.zero, Color.white);
        RuntimeUIFactory.ApplyParchmentFrame(card);

        gameOverTitleText = RuntimeUIFactory.CreateText(card.transform, "Title", "ИГРА ОКОНЧЕНА", 55,
            new Vector2(0.06f, 0.70f), new Vector2(0.94f, 0.87f), Vector2.zero, Vector2.zero,
            TextAnchor.MiddleCenter, new Color(0.52f, 0.018f, 0.035f, 1f), FontStyle.Bold);
        gameOverStatsText = RuntimeUIFactory.CreateText(card.transform, "Stats", "", 26,
            new Vector2(0.12f, 0.44f), new Vector2(0.88f, 0.67f), Vector2.zero, Vector2.zero,
            TextAnchor.MiddleCenter, new Color(0.11f, 0.07f, 0.05f, 1f), FontStyle.Normal);

        Button restart = RuntimeUIFactory.CreateButton(card.transform, "Restart", "ЗАНОВО",
            new Vector2(0.21f, 0.30f), new Vector2(0.79f, 0.41f), Vector2.zero, Vector2.zero, 25);
        restart.onClick.AddListener(RestartGame);

        Button menu = RuntimeUIFactory.CreateButton(card.transform, "Main Menu", "ГЛАВНОЕ МЕНЮ",
            new Vector2(0.21f, 0.16f), new Vector2(0.79f, 0.27f), Vector2.zero, Vector2.zero, 25);
        menu.onClick.AddListener(ReturnToMainMenu);
        gameOverRoot.SetActive(false);
    }

    private void RebuildHeartContainers()
    {
        for (int i = 0; i < heartVisuals.Count; i++)
            if (heartVisuals[i].Root != null) Destroy(heartVisuals[i].Root);
        heartVisuals.Clear();
        if (player == null || heartSprite == null) return;

        int count = Mathf.Max(1, Mathf.CeilToInt(player.MaxHealth));
        for (int i = 0; i < count; i++)
        {
            GameObject root = new GameObject("Heart " + (i + 1), typeof(RectTransform), typeof(LayoutElement));
            root.transform.SetParent(heartsContainer, false);
            LayoutElement le = root.GetComponent<LayoutElement>();
            le.preferredWidth = le.minWidth = 88f;
            le.preferredHeight = le.minHeight = 88f;
            root.GetComponent<RectTransform>().sizeDelta = new Vector2(88f, 88f);

            GameObject bg = new GameObject("Empty", typeof(RectTransform), typeof(Image));
            bg.transform.SetParent(root.transform, false);
            RuntimeUIFactory.SetRect(bg.GetComponent<RectTransform>(), Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero);
            Image bgImage = bg.GetComponent<Image>();
            bgImage.sprite = emptyHeartSprite;
            bgImage.color = heartSpriteIsImported ? Color.white : new Color(0.16f, 0.16f, 0.17f, 1f);
            bgImage.preserveAspect = true;
            bgImage.raycastTarget = false;

            GameObject fill = new GameObject("Health", typeof(RectTransform), typeof(Image));
            fill.transform.SetParent(root.transform, false);
            RuntimeUIFactory.SetRect(fill.GetComponent<RectTransform>(), Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero);
            Image fi = fill.GetComponent<Image>();
            fi.sprite = heartSprite;
            fi.color = heartSpriteIsImported ? Color.white : new Color(0.72f, 0.025f, 0.04f, 1f);
            fi.preserveAspect = true;
            fi.type = Image.Type.Filled;
            fi.fillMethod = Image.FillMethod.Horizontal;
            fi.fillOrigin = 0;
            fi.raycastTarget = false;
            Image half = null;
            if (halfHeartSprite != null)
            {
                GameObject halfObject = new GameObject("Half Heart", typeof(RectTransform), typeof(Image));
                halfObject.transform.SetParent(root.transform, false);
                RuntimeUIFactory.SetRect(halfObject.GetComponent<RectTransform>(), Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero);
                half = halfObject.GetComponent<Image>();
                half.sprite = halfHeartSprite;
                half.color = Color.white;
                half.preserveAspect = true;
                half.raycastTarget = false;
                half.enabled = false;
            }
            heartVisuals.Add(new HeartVisual { Root = root, Fill = fi, Half = half });
        }
    }

    private void RefreshHealth()
    {
        if (player == null) return;
        int expected = Mathf.Max(1, Mathf.CeilToInt(player.MaxHealth));
        if (heartVisuals.Count != expected) RebuildHeartContainers();
        float health = Mathf.Clamp(player.CurrentHealth, 0f, player.MaxHealth);
        for (int i = 0; i < heartVisuals.Count; i++)
        {
            float amount = Mathf.Clamp01(health - i);
            bool showHalf = heartVisuals[i].Half != null && Mathf.Abs(amount - 0.5f) < 0.01f;
            heartVisuals[i].Fill.fillAmount = amount;
            heartVisuals[i].Fill.enabled = amount > 0.001f && !showHalf;
            if (heartVisuals[i].Half != null) heartVisuals[i].Half.enabled = showHalf;
        }
    }

    private void RefreshProgression()
    {
        if (progressionText == null) return;
        if (gameManager == null) { progressionText.text = ""; return; }
        string level = gameManager.IsTutorial ? "ОБУЧЕНИЕ" : "УРОВЕНЬ " + gameManager.CurrentLevelIndex;
        string room = gameManager.IsTutorial ? "" : "  •  КОМНАТА " + gameManager.CurrentRoomIndex;
        progressionText.text = level + room + "  •  ОЧКИ " + gameManager.CurrentScore + "  •  УБИЙСТВА " + gameManager.TotalKills;
    }

    private void HandleComboChanged(int count) => RefreshComboDisplay();

    private void RefreshComboDisplay()
    {
        if (comboText == null) return;
        int count = gameManager != null && gameManager.CurrentState == GameManager.GameState.GAMEPLAY
            ? gameManager.ComboCount : 0;
        comboText.enabled = count >= 2;
        if (count >= 2) comboText.text = "КОМБО ×" + count;
    }

    private void RefreshProjectileCounter()
    {
        if (player != null && projectileText != null)
            projectileText.text = "КИНЖАЛЫ  " + player.ActiveProjectileCount + " / " + player.MaximumActiveProjectiles;
    }

    private void RefreshActiveItems()
    {
        RefreshChargeSkulls();
        if (activeSlotOneText == null) return;
        if (player == null)
        {
            activeSlotOneText.text = "СЛОТ 1 — ПУСТО";
            activeSlotTwoText.text = "";
            activeDescriptionText.text = "Активного предмета нет.";
            SetActiveIcon(null);
            return;
        }

        activeSlotOneText.text = FormatActiveSlot(0, player.GetActiveItemSlot(0));
        if (player.ActiveItemCapacity >= 2 && itemSystem != null && itemSystem.HasPassive(ItemSystem.ItemIds.Backpack))
        {
            activeSlotTwoText.gameObject.SetActive(true);
            activeSlotTwoText.text = FormatActiveSlot(1, player.GetActiveItemSlot(1));
        }
        else
        {
            activeSlotTwoText.gameObject.SetActive(false);
        }

        PlayerController.ActiveItemSlot selected = player.SelectedActiveItem;
        if (selected == null || selected.IsEmpty)
        {
            activeDescriptionText.text = "Активного предмета нет.";
            SetActiveIcon(null);
            return;
        }

        ItemSystem.ItemDefinition definition = itemSystem != null ? itemSystem.GetDefinition(selected.ItemId) : null;
        string uses = selected.UsesPerCharge > 1 && selected.IsCharged ? "\nИспользований: " + selected.UsesRemaining + " / " + selected.UsesPerCharge : "";
        activeDescriptionText.text = (definition != null ? definition.Description : selected.DisplayName) + uses;
        SetActiveIcon(selected.ItemId);
    }

    private string FormatActiveSlot(int index, PlayerController.ActiveItemSlot slot)
    {
        string prefix = player != null && player.SelectedActiveSlotIndex == index ? "> " : "  ";
        string slotNumber = "СЛОТ " + (index + 1) + "  ";
        if (slot == null || slot.IsEmpty) return prefix + slotNumber + "— ПУСТО";
        string status = slot.IsCharged ? (slot.UsesPerCharge > 1 ? "ГОТОВО ×" + slot.UsesRemaining : "ГОТОВО") : slot.StoredKills + "/" + slot.RequiredKills;
        return prefix + slotNumber + slot.DisplayName + "  [" + status + "]";
    }

    private void SetActiveIcon(string itemId)
    {
        if (string.Equals(tabActiveIconItemId, itemId, System.StringComparison.Ordinal) && tabActiveIconSprite != null)
        {
            if (tabActiveIcon != null) tabActiveIcon.enabled = true;
            return;
        }

        DestroyTabActiveIcon();
        tabActiveIconItemId = itemId;
        if (tabActiveIcon == null || string.IsNullOrWhiteSpace(itemId))
        {
            if (tabActiveIcon != null) tabActiveIcon.enabled = false;
            return;
        }

        Sprite authoredIcon = RuntimeGameArt.Load("Items/" + itemId.Replace('.', '/'));
        if (authoredIcon != null)
        {
            tabActiveIconSprite = authoredIcon;
            tabActiveIconIsImported = true;
            tabActiveIcon.sprite = authoredIcon;
            tabActiveIcon.enabled = true;
            return;
        }

        tabActiveIconIsImported = false;
        tabActiveIconTexture = CreateActiveItemIconTexture(itemId, 64);
        tabActiveIconSprite = Sprite.Create(tabActiveIconTexture, new Rect(0f, 0f, 64f, 64f), new Vector2(0.5f, 0.5f), 64f, 0, SpriteMeshType.FullRect);
        tabActiveIcon.sprite = tabActiveIconSprite;
        tabActiveIcon.enabled = true;
    }

    private void DestroyTabActiveIcon()
    {
        if (tabActiveIconSprite != null)
        {
            if (!tabActiveIconIsImported) Destroy(tabActiveIconSprite);
            tabActiveIconSprite = null;
        }
        tabActiveIconIsImported = false;
        if (tabActiveIconTexture != null) { Destroy(tabActiveIconTexture); tabActiveIconTexture = null; }
        tabActiveIconItemId = null;
    }

    private static Texture2D CreateActiveItemIconTexture(string itemId, int size)
    {
        Texture2D texture = new Texture2D(size, size, TextureFormat.RGBA32, false);
        texture.filterMode = FilterMode.Point;
        Color[] pixels = new Color[size * size];
        float c = (size - 1) * 0.5f;
        Color dark = new Color(0.07f, 0.055f, 0.05f, 1f);
        Color blood = new Color(0.50f, 0.015f, 0.03f, 1f);
        Color bone = new Color(0.87f, 0.83f, 0.70f, 1f);
        for (int y = 0; y < size; y++)
        for (int x = 0; x < size; x++)
        {
            float dx = x - c, dy = y - c;
            float r = Mathf.Sqrt(dx * dx + dy * dy);
            if (r > size * 0.44f) { pixels[y * size + x] = Color.clear; continue; }
            Color color = dark;
            if (itemId == ItemSystem.ItemIds.ObstacleStone)
                color = Color.Lerp(new Color(0.42f,0.40f,0.36f,1f), new Color(0.14f,0.13f,0.12f,1f), r/(size*0.44f));
            else if (itemId == ItemSystem.ItemIds.Bomb)
                color = r < size * 0.30f ? new Color(0.06f,0.06f,0.065f,1f) : blood;
            else if (itemId == ItemSystem.ItemIds.Pushback)
                color = Mathf.Abs(r - size * 0.26f) < 3f || Mathf.Abs(r - size * 0.38f) < 2f ? bone : dark;
            else if (itemId == ItemSystem.ItemIds.Dash)
                color = Mathf.Abs(dy + dx * 0.45f) < 4f && dx > -size * 0.28f ? bone : dark;
            else if (itemId == ItemSystem.ItemIds.ReversePolarity)
                color = (Mathf.Abs(r - size * 0.28f) < 3f || Mathf.Abs(dx) < 3f) ? blood : dark;
            pixels[y * size + x] = color;
        }
        texture.SetPixels(pixels);
        texture.Apply(false, false);
        return texture;
    }

    private void UpdateTabInput()
    {
        bool blockedByTutorial = tutorialRoot != null && tutorialRoot.activeSelf;
        bool shouldShow = !blockedByTutorial && gameManager != null && !gameManager.IsGameplayPaused && gameManager.CurrentState == GameManager.GameState.GAMEPLAY && Input.GetKey(KeyCode.Tab);
        if (shouldShow != tabWasVisible)
        {
            tabWasVisible = shouldShow;
            tabRoot.SetActive(shouldShow);
        }
        if (shouldShow) RefreshTabContents();
    }

    private void RefreshTabContents()
    {
        if (tabStatsText == null || tabItemsText == null) return;
        if (player == null)
        {
            tabStatsText.text = "ИГРОК НЕ НАЙДЕН";
            tabItemsText.text = "";
            return;
        }

        float ricochet = Mathf.Max(2f, player.ProjectileDamage * 2f);
        tabStatsText.text =
            "<b>ХАРАКТЕРИСТИКИ</b>\n\n" +
            "Здоровье   " + player.CurrentHealth.ToString("0.0") + " / " + player.MaxHealth.ToString("0.0") + "\n\n" +
            "Урон   " + player.ProjectileDamage.ToString("0.0") + "\n" +
            "Рикошет   " + ricochet.ToString("0.0") + "\n\n" +
            "Очки   " + (gameManager != null ? gameManager.CurrentScore : 0) + "\n" +
            "Убийства   " + (gameManager != null ? gameManager.TotalKills : 0);

        string passives = "<b>ПАССИВНЫЕ ПРЕДМЕТЫ</b>\n\n";
        if (itemSystem == null)
        {
            passives += "Нет";
        }
        else
        {
            List<ItemSystem.ItemDefinition> definitions = itemSystem.GetOwnedPassiveDefinitions();
            if (definitions.Count == 0) passives += "Нет";
            else
            {
                for (int i = 0; i < definitions.Count; i++)
                {
                    passives += "• <b>" + definitions[i].DisplayName + "</b> [Пассивная] — " + CompactPassiveEffect(definitions[i].Id);
                    if (i < definitions.Count - 1) passives += "\n\n";
                }
            }
        }
        tabItemsText.text = passives;
        RefreshActiveItems();
    }


    private static string CompactPassiveEffect(string itemId)
    {
        if (itemId == ItemSystem.ItemIds.AngelLens) return "5 кинжалов по 0,25 после рикошета; не ранят героя";
        if (itemId == ItemSystem.ItemIds.Resurrection) return "одноразовая: спасает от смертельного удара, оставляет 1 сердце";
        if (itemId == ItemSystem.ItemIds.Snack) return "+1 здоровье, +1 урон";
        if (itemId == ItemSystem.ItemIds.Backpack) return "можно носить 2 активных предмета";
        if (itemId == ItemSystem.ItemIds.GlassCannon) return "+2 урона, ×2 получаемый урон";
        return "пассивный эффект";
    }

    private void RefreshTutorial()
    {
        if (tutorialRoot == null) return;
        bool isTutorial = gameManager != null && gameManager.CurrentState == GameManager.GameState.GAMEPLAY && gameManager.IsTutorial;
        bool visible = isTutorial && !tutorialDismissed &&
                       GameplayRoomReveal.HasCompletedCurrentRoomReveal &&
                       !GameplayRoomReveal.IsRevealing;
        tutorialRoot.SetActive(visible);

        if (!visible)
        {
            if (gameManager != null && !gameManager.IsGameplayPaused && gameManager.CurrentState == GameManager.GameState.GAMEPLAY && Time.timeScale == 0f)
                Time.timeScale = 1f;
            return;
        }

        tabRoot.SetActive(false);
        tabWasVisible = false;
        Time.timeScale = 0f;
        RefreshTutorialPage();
    }

    private void RefreshTutorialPage()
    {
        if (gameManager == null || tutorialHeaderText == null) return;
        if (tutorialPage <= 0)
        {
            tutorialHeaderText.text = "I — ВЫЖИВАНИЕ";
            tutorialBodyText.text =
                "Движение: " + FormatKey(gameManager.MoveUpKey) + " / " + FormatKey(gameManager.MoveLeftKey) + " / " + FormatKey(gameManager.MoveDownKey) + " / " + FormatKey(gameManager.MoveRightKey) + ".\n\n" +
                "Цельтесь мышью. Бросок кинжала: " + FormatKey(gameManager.FireKey) + ".\n\n" +
                "Подбор и замена предметов: " + FormatKey(gameManager.InteractKey) + ".\n\n" +
                "Одновременно на экране может быть не больше трёх кинжалов.";
            tutorialButtonText.text = "ДАЛЕЕ";
        }
        else
        {
            tutorialHeaderText.text = "II — РИКОШЕТ";
            tutorialBodyText.text =
                "Использование активного предмета: " + FormatKey(gameManager.UseActiveItemKey) + ". Чтобы использовать его повторно, накапливайте заряды за убийства врагов.\n\n" +
                "Удерживайте TAB для просмотра характеристик и предметов.\n\n" +
                "Первый рикошет удваивает урон. Второй удар о стену уничтожает кинжал; отражённый кинжал опасен для героя.\n\n" +
                "Разбивайте горшки кинжалами — иногда из них выпадают сердца, которые восстанавливают здоровье.";
            tutorialButtonText.text = "НАЧАТЬ";
        }
    }

    private void AdvanceTutorialPage()
    {
        if (tutorialPage == 0)
        {
            tutorialPage = 1;
            RefreshTutorialPage();
            return;
        }

        tutorialDismissed = true;
        tutorialRoot.SetActive(false);
        Time.timeScale = 1f;
    }

    private static string FormatKey(KeyCode key)
    {
        if (key == KeyCode.Mouse0) return "ЛКМ";
        if (key == KeyCode.Mouse1) return "Мышь 2";
        if (key == KeyCode.Space) return "Пробел";
        return key.ToString();
    }

    private void ShowGameOver()
    {
        if (gameOverRoot == null) return;
        tutorialRoot.SetActive(false);
        gameOverRoot.SetActive(true);
        tabRoot.SetActive(false);
        tabWasVisible = false;
        bool victory = gameManager != null && gameManager.LastRunEndReason == GameManager.RunEndReason.Victory;
        gameOverTitleText.text = victory ? "ПОБЕДА" : "ИГРА ОКОНЧЕНА";
        gameOverTitleText.color = victory ? new Color(0.38f, 0.26f, 0.055f, 1f) : new Color(0.52f, 0.018f, 0.035f, 1f);
        gameOverStatsText.text = gameManager != null ? "ОЧКИ  " + gameManager.CurrentScore + "\n\nУБИЙСТВА  " + gameManager.TotalKills + "\n\nРЕКОРД  " + gameManager.HighScore : "ОЧКИ 0";
        Time.timeScale = 0f;
    }

    private void HideGameOver()
    {
        if (gameOverRoot != null) gameOverRoot.SetActive(false);
    }

    private void RestartGame()
    {
        Time.timeScale = 1f;
        HideGameOver();
        tutorialDismissed = true;
        if (gameManager != null) gameManager.RestartRun();
    }

    private void ReturnToMainMenu()
    {
        Time.timeScale = 1f;
        HideGameOver();
        if (gameManager != null) gameManager.ReturnToMainMenu(true);
    }

    private void HandleHealthChanged(float current, float max) { RefreshHealth(); RefreshTabContents(); }
    private void HandlePlayerStatsChanged() { RebuildHeartContainers(); RefreshHealth(); RefreshTabContents(); }
    private void HandleInventoryChanged() { RefreshActiveItems(); RefreshTabContents(); }
    private void HandleSelectedActiveSlotChanged(int slot) { RefreshActiveItems(); RefreshTabContents(); }
    private void HandlePassiveItemChanged(ItemSystem.ItemDefinition definition) { RefreshActiveItems(); RefreshTabContents(); }
    private void HandleActiveItemCollected(ItemSystem.ItemDefinition definition) { RefreshActiveItems(); RefreshTabContents(); }

    private void HandleProgressionChanged(int level, int room)
    {
        if (level == 0 && room == 0)
        {
            tutorialDismissed = false;
            tutorialPage = 0;
        }
        RefreshProgression();
        RefreshTutorial();
    }

    private void HandleScoreChanged(int score) { RefreshProgression(); }
    private void HandleKillCountChanged(int kills) { RefreshProgression(); RefreshActiveItems(); }
    private void HandleRunEnded(GameManager.RunEndReason reason, int score, int kills) { ShowGameOver(); }

    private void HandleGameStateChanged(GameManager.GameState state)
    {
        RefreshVisibility();
        if (state == GameManager.GameState.GAME_OVER) ShowGameOver();
        else
        {
            HideGameOver();
            if (state == GameManager.GameState.GAMEPLAY && !gameManager.IsGameplayPaused && !(tutorialRoot != null && tutorialRoot.activeSelf)) Time.timeScale = 1f;
        }
    }

    private void HandleSceneLoaded(Scene scene, LoadSceneMode mode)
    {
        UnbindPlayer();
        TryBindPlayer();
        TryBindItemSystem();
        if (gameManager != null && !gameManager.IsGameplayPaused && gameManager.CurrentState != GameManager.GameState.GAME_OVER && !(tutorialRoot != null && tutorialRoot.activeSelf)) Time.timeScale = 1f;
        RefreshAll();
    }

    private void RefreshAll()
    {
        RefreshVisibility();
        RefreshProgression();
        RefreshProjectileCounter();
        RefreshActiveItems();
        RefreshHealth();
        RefreshTutorial();
        RefreshTabContents();
    }

    private void RefreshVisibility()
    {
        if (gameplayHudRoot == null) return;
        if (gameManager == null)
        {
            gameplayHudRoot.SetActive(false);
            tutorialRoot.SetActive(false);
            tabRoot.SetActive(false);
            return;
        }

        bool visible = gameManager.CurrentState == GameManager.GameState.GAMEPLAY || gameManager.CurrentState == GameManager.GameState.GAME_OVER;
        gameplayHudRoot.SetActive(visible);
        if (!visible)
        {
            tutorialRoot.SetActive(false);
            tabRoot.SetActive(false);
            tabWasVisible = false;
        }
        RefreshTutorial();
    }

    private static Texture2D CreateHeartTexture(int size)
    {
        Texture2D texture = new Texture2D(size, size, TextureFormat.RGBA32, false) { name = "Runtime HUD Heart Texture", filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp };
        Color[] pixels = new Color[size * size];
        for (int y = 0; y < size; y++)
        for (int x = 0; x < size; x++)
        {
            float nx = ((x + 0.5f) / size) * 2f - 1f;
            float ny = ((y + 0.5f) / size) * 2f - 1f + 0.12f;
            float e = Mathf.Pow(nx * nx + ny * ny - 0.62f, 3f) - nx * nx * Mathf.Pow(ny, 3f);
            int index = y * size + x;
            if (e > 0f) pixels[index] = Color.clear;
            else
            {
                float inner = Mathf.Clamp01(Mathf.Abs(e) * 16f);
                pixels[index] = new Color(inner, inner, inner, 1f);
            }
        }
        texture.SetPixels(pixels);
        texture.Apply(false, false);
        return texture;
    }
}
