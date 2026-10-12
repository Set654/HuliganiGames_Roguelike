using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;

[DefaultExecutionOrder(-10000)]
public sealed class GameBootstrap : MonoBehaviour
{
    private static GameBootstrap instance;
    private AudioListener persistentAudioListener;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    private static void BootstrapBeforeSceneLoad()
    {
        EnsureBootstrapHost();
        EnsurePersistentComponent<GameManager>("GameManager");
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void BootstrapAfterSceneLoad()
    {
        EnsureBootstrapHost();
        EnsurePersistentComponent<GameManager>("GameManager");
        EnsurePersistentComponent<ItemSystem>("Runtime Item System");
        EnsurePersistentComponent<AudioManager>("Runtime AudioManager");
        EnsurePersistentComponent<MenuUIManager>("Runtime Menu UI Manager");
        EnsurePersistentComponent<HUDManager>("Runtime HUD Manager");
        EnsurePersistentComponent<BossHealthBarUI>("Runtime Boss Health Bar UI");

        if (SceneManager.GetActiveScene().name == "Game")
        {
            EnsureSceneComponent<LevelRoomManager>("Runtime Level Room Manager");
        }

        RuntimeUIFactory.EnsureEventSystem();
        if (instance != null)
        {
            instance.ApplyGlobalRuntimeSettings();
            if (SceneManager.GetActiveScene().name == "MainMenu") instance.EnsureMenuCamera();
            instance.EnsureSingleAudioListener();
        }
    }

    private static void EnsureBootstrapHost()
    {
        if (instance != null) return;
        GameBootstrap existing = FindAnyObjectByType<GameBootstrap>();
        if (existing != null) { instance = existing; return; }
        instance = new GameObject("Game Bootstrap").AddComponent<GameBootstrap>();
    }

    private static T EnsurePersistentComponent<T>(string objectName) where T : Component
    {
        T existing = FindAnyObjectByType<T>();
        if (existing != null) return existing;
        GameObject obj = new GameObject(objectName); T component = obj.AddComponent<T>(); DontDestroyOnLoad(obj); return component;
    }

    private static T EnsureSceneComponent<T>(string objectName) where T : Component
    {
        T existing = FindAnyObjectByType<T>();
        if (existing != null) return existing;
        return new GameObject(objectName).AddComponent<T>();
    }

    private void Awake()
    {
        if (instance != null && instance != this) { Destroy(gameObject); return; }
        instance = this; DontDestroyOnLoad(gameObject); ApplyGlobalRuntimeSettings(); EnsureOwnAudioListener(); SceneManager.sceneLoaded += HandleSceneLoaded;
    }

    private void OnDestroy()
    {
        if (instance != this) return; SceneManager.sceneLoaded -= HandleSceneLoaded; instance = null;
    }

    private void HandleSceneLoaded(Scene scene, LoadSceneMode mode)
    {
        ApplyGlobalRuntimeSettings();
        RuntimeUIFactory.EnsureEventSystem();

        if (scene.name == "Game")
        {
            // RuntimeInitializeOnLoadMethod(AfterSceneLoad) only runs for the initial scene.
            // When PLAY loads the Game scene from MainMenu, the old scene-local room
            // manager is destroyed. Recreate it here for every Game scene load.
            EnsureSceneComponent<LevelRoomManager>("Runtime Level Room Manager");

            if (GameManager.Instance != null)
            {
                GameManager.Instance.ConfigureGameplayCamera();
            }
        }
        else if (scene.name == "MainMenu")
        {
            EnsureMenuCamera();
        }

        EnsureSingleAudioListener();
    }

    private void EnsureMenuCamera()
    {
        Camera currentCamera = Camera.main;
        if (currentCamera == null)
        {
            GameObject cameraObject = new GameObject("Runtime Main Menu Camera");
            cameraObject.tag = "MainCamera";
            currentCamera = cameraObject.AddComponent<Camera>();
        }
        currentCamera.enabled = true;
        currentCamera.orthographic = true;
        currentCamera.orthographicSize = 5f;
        currentCamera.cullingMask = ~0;
        currentCamera.targetTexture = null;
        currentCamera.clearFlags = CameraClearFlags.SolidColor;
        currentCamera.backgroundColor = new Color(0.025f, 0.015f, 0.018f, 1f);
        currentCamera.transform.position = new Vector3(0f, 0f, -10f);
        AudioListener listener = currentCamera.GetComponent<AudioListener>();
        if (listener == null) listener = currentCamera.gameObject.AddComponent<AudioListener>();
        listener.enabled = true;
    }

    private void ApplyGlobalRuntimeSettings()
    {
        Physics2D.gravity = Vector2.zero; Physics2D.queriesHitTriggers = true; Application.runInBackground = true; QualitySettings.vSyncCount = 0; Application.targetFrameRate = 120;
    }

    private void EnsureOwnAudioListener()
    {
        // In gameplay, prefer the listener attached to the active gameplay camera.
        Camera mainCamera = Camera.main;
        if (mainCamera != null && mainCamera.enabled)
        {
            AudioListener cameraListener = mainCamera.GetComponent<AudioListener>();
            if (cameraListener == null) cameraListener = mainCamera.gameObject.AddComponent<AudioListener>();
            cameraListener.enabled = true;
            persistentAudioListener = cameraListener;
            return;
        }

        persistentAudioListener = GetComponent<AudioListener>();
        if (persistentAudioListener == null) persistentAudioListener = gameObject.AddComponent<AudioListener>();
        persistentAudioListener.enabled = true;
    }

    private void EnsureSingleAudioListener()
    {
        EnsureOwnAudioListener();
        AudioListener[] listeners = FindObjectsByType<AudioListener>(FindObjectsInactive.Include);
        for (int i = 0; i < listeners.Length; i++)
        {
            if (listeners[i] != null) listeners[i].enabled = listeners[i] == persistentAudioListener;
        }
        if (persistentAudioListener != null) persistentAudioListener.enabled = true;
    }
}
