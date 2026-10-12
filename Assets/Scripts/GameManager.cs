using System;
using UnityEngine;
using UnityEngine.SceneManagement;

public sealed class GameManager : MonoBehaviour
{
    public enum GameState { MAIN_MENU, SETTINGS, GAMEPLAY, GAME_OVER }
    public enum BindingAction { MoveUp, MoveDown, MoveLeft, MoveRight, Interact, Fire, UseActiveItem, SwapActiveItem }
    public enum RunEndReason { None, PlayerDeath, Victory }

    public static GameManager Instance { get; private set; }

    [Header("Scene Configuration")]
    [SerializeField] private string mainMenuSceneName = "MainMenu";
    [SerializeField] private string gameplaySceneName = "Game";
    [SerializeField] private bool autoStartWhenGameplaySceneOpenedDirectly = true;

    [Header("Fixed Gameplay Camera")]
    [SerializeField] private float gameplayCameraOrthographicSize = 4.5f;
    [SerializeField] private Color gameplayCameraBackground = new Color(0.035f, 0.035f, 0.04f, 1f);

    [Header("Default Key Bindings")]
    [SerializeField] private KeyCode moveUpKey = KeyCode.W;
    [SerializeField] private KeyCode moveDownKey = KeyCode.S;
    [SerializeField] private KeyCode moveLeftKey = KeyCode.A;
    [SerializeField] private KeyCode moveRightKey = KeyCode.D;
    [SerializeField] private KeyCode interactKey = KeyCode.E;
    [SerializeField] private KeyCode fireKey = KeyCode.Mouse0;
    [SerializeField] private KeyCode useActiveItemKey = KeyCode.Space;
    [SerializeField] private KeyCode swapActiveItemKey = KeyCode.Q;

    [Header("Settings")]
    [Range(0f, 1f)] [SerializeField] private float screenBrightness = 1f;
    [Range(0f, 1f)] [SerializeField] private float masterVolume = 1f;
    [Range(0f, 1f)] [SerializeField] private float musicVolume = 0.4f;
    [Range(0f, 1f)] [SerializeField] private float sfxVolume = 1f;

    private GameState currentState = GameState.MAIN_MENU;
    private GameState stateBeforeSettings = GameState.MAIN_MENU;
    private RunEndReason lastRunEndReason = RunEndReason.None;
    private int currentLevelIndex;
    private int currentRoomIndex;
    private int currentScore;
    private int totalKills;
    private int comboCount;
    private float lastComboKillTime = -100f;
    private const float ComboTimeWindow = 1f;
    private int highScore;
    private int bestKillCount;
    private bool runActive;
    private bool gameplayPaused;

    private const int TutorialLevelIndex = 0;
    private const int FirstCombatLevelIndex = 1;
    private const int FinalLevelIndex = 2;
    private const int FirstRoomIndex = 1;
    private const int BossRoomIndex = 3;

    private const string KeyMoveUp = "Controls.MoveUp";
    private const string KeyMoveDown = "Controls.MoveDown";
    private const string KeyMoveLeft = "Controls.MoveLeft";
    private const string KeyMoveRight = "Controls.MoveRight";
    private const string KeyInteract = "Controls.Interact";
    private const string KeyFire = "Controls.Fire";
    private const string KeyUseActive = "Controls.UseActive";
    private const string KeySwapActive = "Controls.SwapActive";
    private const string KeyBrightness = "Settings.Brightness";
    private const string KeyMasterVolume = "Settings.MasterVolume";
    private const string KeyMusicVolume = "Settings.MusicVolume";
    private const string KeySfxVolume = "Settings.SfxVolume";
    private const string KeyHighScore = "Progress.HighScore";
    private const string KeyBestKills = "Progress.BestKills";
    private const string KeyRunActive = "Run.Active";
    private const string KeyRunLevel = "Run.Level";
    private const string KeyRunRoom = "Run.Room";
    private const string KeyRunScore = "Run.Score";
    private const string KeyRunKills = "Run.Kills";

    public GameState CurrentState => currentState;
    public RunEndReason LastRunEndReason => lastRunEndReason;
    public int CurrentLevelIndex => currentLevelIndex;
    public int CurrentRoomIndex => currentRoomIndex;
    public int CurrentScore => currentScore;
    public int TotalKills => totalKills;
    public int ComboCount => comboCount > 1 && Time.time - lastComboKillTime <= ComboTimeWindow ? comboCount : 0;
    public int HighScore => highScore;
    public int BestKillCount => bestKillCount;
    public bool RunActive => runActive;
    public bool IsGameplayPaused => gameplayPaused;
    public bool IsTutorial => currentLevelIndex == TutorialLevelIndex;
    public bool IsBossRoom => currentLevelIndex >= FirstCombatLevelIndex && currentRoomIndex == BossRoomIndex;
    public KeyCode MoveUpKey => moveUpKey;
    public KeyCode MoveDownKey => moveDownKey;
    public KeyCode MoveLeftKey => moveLeftKey;
    public KeyCode MoveRightKey => moveRightKey;
    public KeyCode InteractKey => interactKey;
    public KeyCode FireKey => fireKey;
    public KeyCode UseActiveItemKey => useActiveItemKey;
    public KeyCode SwapActiveItemKey => swapActiveItemKey;
    public float ScreenBrightness => screenBrightness;
    public float MasterVolume => masterVolume;
    public float MusicVolume => musicVolume;
    public float SfxVolume => sfxVolume;

    public event Action<GameState> StateChanged;
    public event Action<bool> PauseChanged;
    public event Action<int, int> ProgressionChanged;
    public event Action<int> ScoreChanged;
    public event Action<int> KillCountChanged;
    public event Action EnemyKilled;
    public event Action<int> ComboChanged;
    public event Action<RunEndReason, int, int> RunEnded;
    public event Action<GameState, int, int> AudioContextChanged;

    private void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(gameObject); return; }
        Instance = this;
        DontDestroyOnLoad(gameObject);
        LoadPersistentData();
        ApplyMasterVolume();
        SceneManager.sceneLoaded += HandleSceneLoaded;
    }

    private void Start()
    {
        Scene activeScene = SceneManager.GetActiveScene();
        if (autoStartWhenGameplaySceneOpenedDirectly && IsGameplayScene(activeScene) && !runActive)
        {
            BeginNewRun(false);
            return;
        }
        if (currentState == GameState.GAMEPLAY && IsGameplayScene(SceneManager.GetActiveScene())) ConfigureGameplayCamera();
        PublishAudioContext();
    }

    private void OnDestroy()
    {
        if (Instance != this) return;
        SceneManager.sceneLoaded -= HandleSceneLoaded;
        Instance = null;
    }

    private void OnApplicationQuit()
    {
        if (Instance == this) SaveAllPersistentData();
    }

    private void HandleSceneLoaded(Scene scene, LoadSceneMode mode)
    {
        if (IsGameplayScene(scene))
        {
            ConfigureGameplayCamera();
            if (autoStartWhenGameplaySceneOpenedDirectly && !runActive && currentState != GameState.GAME_OVER)
                BeginNewRun(false);
        }
        else if (IsMainMenuScene(scene) && currentState != GameState.SETTINGS)
        {
            SetState(GameState.MAIN_MENU);
        }
        PublishAudioContext();
    }

    public void BeginNewRun(bool loadGameplayScene = true)
    {
        GameplayRoomReveal.PrepareForNewRoom();
        ResetGameplayPause();
        currentLevelIndex = 0;
        currentRoomIndex = 0;
        currentScore = 0;
        totalKills = 0;
        comboCount = 0;
        lastComboKillTime = -100f;
        ComboChanged?.Invoke(0);
        runActive = true;
        lastRunEndReason = RunEndReason.None;
        SetState(GameState.GAMEPLAY);
        SaveRunSnapshot();
        ProgressionChanged?.Invoke(currentLevelIndex, currentRoomIndex);
        ScoreChanged?.Invoke(currentScore);
        KillCountChanged?.Invoke(totalKills);
        PublishAudioContext();
        if (loadGameplayScene) LoadSceneIfAvailable(gameplaySceneName); else ConfigureGameplayCamera();
    }

    public bool ContinueSavedRun()
    {
        if (PlayerPrefs.GetInt(KeyRunActive, 0) != 1) return false;
        GameplayRoomReveal.PrepareForNewRoom();
        currentLevelIndex = Mathf.Clamp(PlayerPrefs.GetInt(KeyRunLevel, 0), 0, 2);
        int defaultRoom = currentLevelIndex == 0 ? 0 : 1;
        currentRoomIndex = PlayerPrefs.GetInt(KeyRunRoom, defaultRoom);
        currentRoomIndex = currentLevelIndex == 0 ? 0 : Mathf.Clamp(currentRoomIndex, 1, 3);
        currentScore = Mathf.Max(0, PlayerPrefs.GetInt(KeyRunScore, 0));
        totalKills = Mathf.Max(0, PlayerPrefs.GetInt(KeyRunKills, 0));
        comboCount = 0;
        lastComboKillTime = -100f;
        ComboChanged?.Invoke(0);
        runActive = true;
        lastRunEndReason = RunEndReason.None;
        SetState(GameState.GAMEPLAY);
        ProgressionChanged?.Invoke(currentLevelIndex, currentRoomIndex);
        ScoreChanged?.Invoke(currentScore);
        KillCountChanged?.Invoke(totalKills);
        PublishAudioContext();
        LoadSceneIfAvailable(gameplaySceneName);
        return true;
    }

    public void RestartRun()
    {
        BeginCombatRunFromRoomOne(true);
    }

    private void BeginCombatRunFromRoomOne(bool loadGameplayScene)
    {
        ResetGameplayPause();
        currentLevelIndex = FirstCombatLevelIndex;
        currentRoomIndex = FirstRoomIndex;
        currentScore = 0;
        totalKills = 0;
        comboCount = 0;
        lastComboKillTime = -100f;
        ComboChanged?.Invoke(0);

        runActive = true;
        lastRunEndReason = RunEndReason.None;

        SetState(GameState.GAMEPLAY);
        SaveRunSnapshot();

        ProgressionChanged?.Invoke(currentLevelIndex, currentRoomIndex);
        ScoreChanged?.Invoke(currentScore);
        KillCountChanged?.Invoke(totalKills);
        PublishAudioContext();

        if (loadGameplayScene)
            LoadSceneIfAvailable(gameplaySceneName);
        else
            ConfigureGameplayCamera();
    }

    public void AdvanceRoom()
    {
        if (!runActive || currentState != GameState.GAMEPLAY) return;
        if (currentLevelIndex == 0)
        {
            currentLevelIndex = 1;
            currentRoomIndex = 1;
            CommitProgressionChange();
            return;
        }
        if (currentRoomIndex < 3)
        {
            currentRoomIndex++;
            CommitProgressionChange();
            return;
        }
        if (currentLevelIndex < 2)
        {
            currentLevelIndex++;
            currentRoomIndex = 1;
            CommitProgressionChange();
            return;
        }
        CompleteRun();
    }

    private void CommitProgressionChange()
    {
        SaveRunSnapshot();
        ProgressionChanged?.Invoke(currentLevelIndex, currentRoomIndex);
        PublishAudioContext();
    }

    public void CompleteRun() { if (runActive) FinishRun(RunEndReason.Victory); }
    public void HandlePlayerDeath() { if (runActive) FinishRun(RunEndReason.PlayerDeath); }

    private void FinishRun(RunEndReason reason)
    {
        runActive = false;
        lastRunEndReason = reason;
        UpdateHighScores();
        PlayerPrefs.SetInt(KeyRunActive, 0);
        PlayerPrefs.Save();
        comboCount = 0;
        ComboChanged?.Invoke(0);
        SetState(GameState.GAME_OVER);
        RunEnded?.Invoke(reason, currentScore, totalKills);
        PublishAudioContext();
    }

    public void AddScore(int amount)
    {
        if (amount <= 0 || !runActive) return;
        currentScore += amount;
        if (currentScore > highScore)
        {
            highScore = currentScore;
            PlayerPrefs.SetInt(KeyHighScore, highScore);
        }
        ScoreChanged?.Invoke(currentScore);
    }

    public void NotifyEnemyKilled(int scoreValue = 100)
    {
        if (!runActive || currentState != GameState.GAMEPLAY) return;
        // The first kill scores normally. Two or more kills no more than
        // one second apart form a combo with an increasing score multiplier.
        comboCount = Time.time - lastComboKillTime <= ComboTimeWindow
            ? Mathf.Min(5, comboCount + 1) : 1;
        lastComboKillTime = Time.time;
        ComboChanged?.Invoke(comboCount);
        totalKills++;
        if (scoreValue > 0) AddScore(scoreValue * comboCount);
        if (totalKills > bestKillCount)
        {
            bestKillCount = totalKills;
            PlayerPrefs.SetInt(KeyBestKills, bestKillCount);
        }
        KillCountChanged?.Invoke(totalKills);
        EnemyKilled?.Invoke();
    }

    private void UpdateHighScores()
    {
        bool changed = false;
        if (currentScore > highScore) { highScore = currentScore; PlayerPrefs.SetInt(KeyHighScore, highScore); changed = true; }
        if (totalKills > bestKillCount) { bestKillCount = totalKills; PlayerPrefs.SetInt(KeyBestKills, bestKillCount); changed = true; }
        if (changed) PlayerPrefs.Save();
    }

    public float GetScaledNormalEnemyHealth()
    {
        // Normal enemies have a fixed health pool throughout each level.
        // Level 1: 2 HP in both combat rooms.
        // Level 2: 3 HP in both combat rooms.
        // Bosses use their own health values in BossController.
        return currentLevelIndex >= 2 ? 3f : 2f;
    }

    public float GetScaledNormalEnemyDamage()
    {
        if (currentLevelIndex == 0) return 0.5f;
        float damage = 0.5f + ((Mathf.Clamp(currentRoomIndex, 1, 3) - 1) * 0.5f);
        return Mathf.Min(1f, damage);
    }

    public float GetEnemyMovementSpeedMultiplier() => currentLevelIndex >= 2 ? 1.2f : 1f;

    public void PauseGameplay()
    {
        if (currentState != GameState.GAMEPLAY || gameplayPaused) return;
        gameplayPaused = true;
        Time.timeScale = 0f;
        PauseChanged?.Invoke(true);
    }

    public void ResumeGameplay()
    {
        if (!gameplayPaused) return;
        gameplayPaused = false;
        Time.timeScale = 1f;
        PauseChanged?.Invoke(false);
    }

    private void ResetGameplayPause()
    {
        if (gameplayPaused)
        {
            gameplayPaused = false;
            PauseChanged?.Invoke(false);
        }
        Time.timeScale = 1f;
    }

    public void OpenSettings()
    {
        if (currentState != GameState.SETTINGS) stateBeforeSettings = currentState;
        SetState(GameState.SETTINGS);
        if (gameplayPaused) Time.timeScale = 0f;
    }

    public void CloseSettings()
    {
        GameState target = stateBeforeSettings == GameState.SETTINGS ? GameState.MAIN_MENU : stateBeforeSettings;
        SetState(target);
        if (gameplayPaused) Time.timeScale = 0f;
    }

    public void ReturnToMainMenu(bool abandonCurrentRun = true)
    {
        ResetGameplayPause();
        if (abandonCurrentRun)
        {
            runActive = false;
            PlayerPrefs.SetInt(KeyRunActive, 0);
            PlayerPrefs.Save();
        }
        SetState(GameState.MAIN_MENU);
        LoadSceneIfAvailable(mainMenuSceneName);
    }

    public void QuitGame()
    {
        SaveAllPersistentData();
        Application.Quit();
    }

    public void SetState(GameState newState)
    {
        if (currentState == newState) { PublishAudioContext(); return; }
        currentState = newState;
        if (currentState == GameState.GAMEPLAY && IsGameplayScene(SceneManager.GetActiveScene())) ConfigureGameplayCamera();
        StateChanged?.Invoke(currentState);
        PublishAudioContext();
    }

    public KeyCode GetBinding(BindingAction action)
    {
        switch (action)
        {
            case BindingAction.MoveUp: return moveUpKey;
            case BindingAction.MoveDown: return moveDownKey;
            case BindingAction.MoveLeft: return moveLeftKey;
            case BindingAction.MoveRight: return moveRightKey;
            case BindingAction.Interact: return interactKey;
            case BindingAction.Fire: return fireKey;
            case BindingAction.UseActiveItem: return useActiveItemKey;
            case BindingAction.SwapActiveItem: return swapActiveItemKey;
            default: throw new ArgumentOutOfRangeException(nameof(action), action, "Unsupported binding action.");
        }
    }

    public void SetBinding(BindingAction action, KeyCode key)
    {
        switch (action)
        {
            case BindingAction.MoveUp: moveUpKey = key; break;
            case BindingAction.MoveDown: moveDownKey = key; break;
            case BindingAction.MoveLeft: moveLeftKey = key; break;
            case BindingAction.MoveRight: moveRightKey = key; break;
            case BindingAction.Interact: interactKey = key; break;
            case BindingAction.Fire: fireKey = key; break;
            case BindingAction.UseActiveItem: useActiveItemKey = key; break;
            case BindingAction.SwapActiveItem: swapActiveItemKey = key; break;
            default: throw new ArgumentOutOfRangeException(nameof(action), action, "Unsupported binding action.");
        }
        SaveBindings();
    }

    public void ResetBindingsToDefaults()
    {
        moveUpKey = KeyCode.W; moveDownKey = KeyCode.S; moveLeftKey = KeyCode.A; moveRightKey = KeyCode.D;
        interactKey = KeyCode.E; fireKey = KeyCode.Mouse0; useActiveItemKey = KeyCode.Space; swapActiveItemKey = KeyCode.Q;
        SaveBindings();
    }

    public void SetScreenBrightness(float value) { screenBrightness = Mathf.Clamp01(value); PlayerPrefs.SetFloat(KeyBrightness, screenBrightness); PlayerPrefs.Save(); }
    public void SetMasterVolume(float value) { masterVolume = Mathf.Clamp01(value); PlayerPrefs.SetFloat(KeyMasterVolume, masterVolume); ApplyMasterVolume(); PlayerPrefs.Save(); }
    public void SetMusicVolume(float value) { musicVolume = Mathf.Clamp01(value); PlayerPrefs.SetFloat(KeyMusicVolume, musicVolume); PlayerPrefs.Save(); PublishAudioContext(); }
    public void SetSfxVolume(float value) { sfxVolume = Mathf.Clamp01(value); PlayerPrefs.SetFloat(KeySfxVolume, sfxVolume); PlayerPrefs.Save(); }
    private void ApplyMasterVolume() => AudioListener.volume = masterVolume;

    public void SaveRunSnapshot()
    {
        PlayerPrefs.SetInt(KeyRunActive, runActive ? 1 : 0);
        PlayerPrefs.SetInt(KeyRunLevel, currentLevelIndex);
        PlayerPrefs.SetInt(KeyRunRoom, currentRoomIndex);
        PlayerPrefs.SetInt(KeyRunScore, currentScore);
        PlayerPrefs.SetInt(KeyRunKills, totalKills);
        PlayerPrefs.Save();
    }

    private void SaveAllPersistentData()
    {
        SaveBindings();
        PlayerPrefs.SetFloat(KeyBrightness, screenBrightness);
        PlayerPrefs.SetFloat(KeyMasterVolume, masterVolume);
        PlayerPrefs.SetFloat(KeyMusicVolume, musicVolume);
        PlayerPrefs.SetFloat(KeySfxVolume, sfxVolume);
        PlayerPrefs.SetInt(KeyHighScore, highScore);
        PlayerPrefs.SetInt(KeyBestKills, bestKillCount);
        PlayerPrefs.SetInt(KeyRunActive, runActive ? 1 : 0);
        PlayerPrefs.SetInt(KeyRunLevel, currentLevelIndex);
        PlayerPrefs.SetInt(KeyRunRoom, currentRoomIndex);
        PlayerPrefs.SetInt(KeyRunScore, currentScore);
        PlayerPrefs.SetInt(KeyRunKills, totalKills);
        PlayerPrefs.Save();
    }

    private void SaveBindings()
    {
        PlayerPrefs.SetInt(KeyMoveUp, (int)moveUpKey); PlayerPrefs.SetInt(KeyMoveDown, (int)moveDownKey);
        PlayerPrefs.SetInt(KeyMoveLeft, (int)moveLeftKey); PlayerPrefs.SetInt(KeyMoveRight, (int)moveRightKey);
        PlayerPrefs.SetInt(KeyInteract, (int)interactKey); PlayerPrefs.SetInt(KeyFire, (int)fireKey);
        PlayerPrefs.SetInt(KeyUseActive, (int)useActiveItemKey); PlayerPrefs.SetInt(KeySwapActive, (int)swapActiveItemKey);
        PlayerPrefs.Save();
    }

    private void LoadPersistentData()
    {
        moveUpKey = LoadKeyCode(KeyMoveUp, KeyCode.W); moveDownKey = LoadKeyCode(KeyMoveDown, KeyCode.S);
        moveLeftKey = LoadKeyCode(KeyMoveLeft, KeyCode.A); moveRightKey = LoadKeyCode(KeyMoveRight, KeyCode.D);
        interactKey = LoadKeyCode(KeyInteract, KeyCode.E); fireKey = LoadKeyCode(KeyFire, KeyCode.Mouse0);
        useActiveItemKey = LoadKeyCode(KeyUseActive, KeyCode.Space); swapActiveItemKey = LoadKeyCode(KeySwapActive, KeyCode.Q);
        screenBrightness = Mathf.Clamp01(PlayerPrefs.GetFloat(KeyBrightness, 1f));
        masterVolume = Mathf.Clamp01(PlayerPrefs.GetFloat(KeyMasterVolume, 1f));
        musicVolume = Mathf.Clamp01(PlayerPrefs.GetFloat(KeyMusicVolume, 0.4f));
        sfxVolume = Mathf.Clamp01(PlayerPrefs.GetFloat(KeySfxVolume, 1f));
        highScore = Mathf.Max(0, PlayerPrefs.GetInt(KeyHighScore, 0));
        bestKillCount = Mathf.Max(0, PlayerPrefs.GetInt(KeyBestKills, 0));
        runActive = PlayerPrefs.GetInt(KeyRunActive, 0) == 1;
        currentLevelIndex = Mathf.Clamp(PlayerPrefs.GetInt(KeyRunLevel, 0), 0, 2);
        currentRoomIndex = PlayerPrefs.GetInt(KeyRunRoom, currentLevelIndex == 0 ? 0 : 1);
        currentScore = Mathf.Max(0, PlayerPrefs.GetInt(KeyRunScore, 0));
        totalKills = Mathf.Max(0, PlayerPrefs.GetInt(KeyRunKills, 0));
    }

    private static KeyCode LoadKeyCode(string key, KeyCode fallback)
    {
        int raw = PlayerPrefs.GetInt(key, (int)fallback);
        return Enum.IsDefined(typeof(KeyCode), raw) ? (KeyCode)raw : fallback;
    }

    private void PublishAudioContext() => AudioContextChanged?.Invoke(currentState, currentLevelIndex, currentRoomIndex);
    private bool IsGameplayScene(Scene scene) => !string.IsNullOrWhiteSpace(gameplaySceneName) && string.Equals(scene.name, gameplaySceneName, StringComparison.Ordinal);
    private bool IsMainMenuScene(Scene scene) => !string.IsNullOrWhiteSpace(mainMenuSceneName) && string.Equals(scene.name, mainMenuSceneName, StringComparison.Ordinal);

    private void LoadSceneIfAvailable(string sceneName)
    {
        if (string.IsNullOrWhiteSpace(sceneName)) { Debug.LogWarning("Scene name is empty.", this); return; }
        if (!Application.CanStreamedLevelBeLoaded(sceneName)) { Debug.LogWarning($"Scene '{sceneName}' is not available in Build Settings.", this); return; }
        SceneManager.LoadScene(sceneName);
    }

    public void ConfigureGameplayCamera()
    {
        Camera gameplayCamera = Camera.main;

        if (gameplayCamera == null)
        {
            Camera[] existingCameras = FindObjectsByType<Camera>(FindObjectsInactive.Include);
            if (existingCameras != null && existingCameras.Length > 0)
                gameplayCamera = existingCameras[0];
        }

        if (gameplayCamera == null)
        {
            GameObject cameraObject = new GameObject("Runtime Gameplay Camera");
            cameraObject.tag = "MainCamera";
            gameplayCamera = cameraObject.AddComponent<Camera>();
        }

        GameObject cameraGameObject = gameplayCamera.gameObject;
        if (!cameraGameObject.activeSelf) cameraGameObject.SetActive(true);
        cameraGameObject.tag = "MainCamera";

        gameplayCamera.enabled = true;
        gameplayCamera.orthographic = true;
        gameplayCamera.orthographicSize = Mathf.Max(0.1f, gameplayCameraOrthographicSize);
        gameplayCamera.clearFlags = CameraClearFlags.SolidColor;
        gameplayCamera.backgroundColor = gameplayCameraBackground;
        gameplayCamera.cullingMask = ~0;
        gameplayCamera.targetTexture = null;
        gameplayCamera.targetDisplay = 0;
        gameplayCamera.depth = 0f;
        gameplayCamera.nearClipPlane = 0.01f;
        gameplayCamera.farClipPlane = 100f;

        cameraGameObject.transform.position = new Vector3(0f, 0f, -10f);
        cameraGameObject.transform.rotation = Quaternion.identity;

        // Unity 6.6 hardening: the gameplay camera always owns the active listener.
        AudioListener listener = cameraGameObject.GetComponent<AudioListener>();
        if (listener == null) listener = cameraGameObject.AddComponent<AudioListener>();
        listener.enabled = true;

        // Reserve an even slimmer upper band for the HUD while giving more
        // space back to the room view. The world is never rendered behind it.
        gameplayCamera.rect = new Rect(0f, 0f, 1f, 0.90f);
        if (gameplayCamera.GetComponent<RuntimeCameraShake>() == null)
            gameplayCamera.gameObject.AddComponent<RuntimeCameraShake>();

        Debug.Log($"Gameplay camera ready: {gameplayCamera.name}, enabled={gameplayCamera.enabled}, cullingMask={gameplayCamera.cullingMask}", gameplayCamera);
    }
}
