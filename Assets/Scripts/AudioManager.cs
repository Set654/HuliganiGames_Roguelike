using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

[DisallowMultipleComponent]
public sealed class AudioManager : MonoBehaviour
{
    public enum SfxId { Shoot, Ricochet, PlayerHurt, Heal, EnemyDeath, Pickup, Explosion, Portal, BossDeath, PlayerDeath }
    private enum MusicContext { None, Menu, Battle, Boss, Victory, Defeat }
    public static AudioManager Instance { get; private set; }

    // All four supplied tracks are included in Assets/Resources/Music.
    // These Inspector fields are optional overrides; no manual setup is needed.
    [Header("Music clips (auto-loaded from Resources/Music)")]
    [SerializeField] private AudioClip menuLoopMp3;
    [SerializeField] private AudioClip battleLoopMp3;
    [SerializeField] private AudioClip wraithBossLoopMp3;
    [SerializeField] private AudioClip cyclopsBossLoopMp3;
    [SerializeField] private AudioClip victoryLoopMp3;
    [SerializeField] private AudioClip defeatLoopMp3;

    [Header("Optional SFX Overrides")]
    [SerializeField] private AudioClip shootSfx, ricochetSfx, playerHurtSfx, healSfx, enemyDeathSfx, pickupSfx, explosionSfx, portalSfx, bossDeathSfx, playerDeathSfx;
    [Header("Playback")]
    [SerializeField] private float musicCrossfadeDuration = 0.85f;
    [SerializeField] private float generatedMusicVolumeMultiplier = 0.62f;
    [SerializeField] private float externalMusicVolumeMultiplier = 0.68f;
    [SerializeField] private float sfxVolumeMultiplier = 0.90f;

    private AudioSource musicSourceA, musicSourceB, sfxSource, activeMusicSource;
    private float sourceAWeight, sourceBWeight;
    private Coroutine crossfadeCoroutine;
    private GameManager gameManager;
    private PlayerController player;
    private ItemSystem itemSystem;
    private MusicContext currentMusicContext = MusicContext.None;
    private readonly Dictionary<SfxId, AudioClip> generatedSfx = new Dictionary<SfxId, AudioClip>();
    private AudioClip generatedMenuMusic, generatedBattleMusic, generatedBossMusic;
    private float lastKnownPlayerHealth = -1f;
    private int previousLevel = -1, previousRoom = -1;
    private bool bossRoomClearedMusicFallback;
    private const int SampleRate = 22050;

    private void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(gameObject); return; }
        Instance = this;
        DontDestroyOnLoad(gameObject);
        CreateAudioSources();
        LoadIncludedMusic();
        GenerateFallbackAudio();
    }
    private void OnEnable() => BossController.BossRewardRequested += HandleBossRewardRequested;
    private void Start() { TryBindGameManager(); TryBindPlayer(); TryBindItemSystem(); RefreshMusicForCurrentContext(true); }
    private void Update() { if (gameManager == null) TryBindGameManager(); if (player == null) TryBindPlayer(); if (itemSystem == null) TryBindItemSystem(); ApplyMixerVolumes(); }
    private void OnDisable() => BossController.BossRewardRequested -= HandleBossRewardRequested;
    private void OnDestroy()
    {
        if (Instance == this) Instance = null; UnbindGameManager(); UnbindPlayer(); UnbindItemSystem(); DestroyGeneratedClip(generatedMenuMusic); DestroyGeneratedClip(generatedBattleMusic); DestroyGeneratedClip(generatedBossMusic); foreach (var p in generatedSfx) DestroyGeneratedClip(p.Value); generatedSfx.Clear();
    }

    private void LoadIncludedMusic()
    {
        if (menuLoopMp3 == null)
            menuLoopMp3 = Resources.Load<AudioClip>("Music/MenuTheme");
        if (battleLoopMp3 == null)
            battleLoopMp3 = Resources.Load<AudioClip>("Music/NormalRooms");
        if (wraithBossLoopMp3 == null)
            wraithBossLoopMp3 = Resources.Load<AudioClip>("Music/WraithKing");
        if (cyclopsBossLoopMp3 == null)
            cyclopsBossLoopMp3 = Resources.Load<AudioClip>("Music/CyclopsKing");
        if (victoryLoopMp3 == null)
            victoryLoopMp3 = Resources.Load<AudioClip>("Music/Victory");
        if (defeatLoopMp3 == null)
            defeatLoopMp3 = Resources.Load<AudioClip>("Music/Defeat");
        if (playerDeathSfx == null)
            playerDeathSfx = Resources.Load<AudioClip>("Sfx/PlayerDeath");

        if (menuLoopMp3 == null || battleLoopMp3 == null ||
            wraithBossLoopMp3 == null || cyclopsBossLoopMp3 == null ||
            victoryLoopMp3 == null || defeatLoopMp3 == null)
        {
            Debug.LogWarning("One or more included MP3 tracks could not be loaded; procedural music will be used as fallback.", this);
        }
    }

    private void CreateAudioSources()
    {
        musicSourceA = gameObject.AddComponent<AudioSource>(); musicSourceB = gameObject.AddComponent<AudioSource>(); sfxSource = gameObject.AddComponent<AudioSource>(); ConfigureMusicSource(musicSourceA); ConfigureMusicSource(musicSourceB); sfxSource.playOnAwake = false; sfxSource.loop = false; sfxSource.spatialBlend = 0f; activeMusicSource = musicSourceA;
    }
    private static void ConfigureMusicSource(AudioSource source) { source.playOnAwake = false; source.loop = true; source.spatialBlend = 0f; source.volume = 0f; }

    private void TryBindGameManager()
    {
        if (gameManager == GameManager.Instance && gameManager != null) return; UnbindGameManager(); gameManager = GameManager.Instance; if (gameManager == null) return;
        gameManager.AudioContextChanged += HandleAudioContextChanged; gameManager.EnemyKilled += HandleEnemyKilled; gameManager.ProgressionChanged += HandleProgressionChanged; previousLevel = gameManager.CurrentLevelIndex; previousRoom = gameManager.CurrentRoomIndex; RefreshMusicForCurrentContext(true);
    }
    private void UnbindGameManager() { if (gameManager == null) return; gameManager.AudioContextChanged -= HandleAudioContextChanged; gameManager.EnemyKilled -= HandleEnemyKilled; gameManager.ProgressionChanged -= HandleProgressionChanged; gameManager = null; }
    private void TryBindPlayer()
    {
        PlayerController found = FindAnyObjectByType<PlayerController>(); if (found == null || found == player) return; UnbindPlayer(); player = found; lastKnownPlayerHealth = player.CurrentHealth; player.ProjectileFired += HandleProjectileFired; player.ProjectileFirstRicochet += HandleProjectileRicochet; player.HealthChanged += HandleHealthChanged;
    }
    private void UnbindPlayer() { if (player == null) return; player.ProjectileFired -= HandleProjectileFired; player.ProjectileFirstRicochet -= HandleProjectileRicochet; player.HealthChanged -= HandleHealthChanged; player = null; lastKnownPlayerHealth = -1f; }
    private void TryBindItemSystem()
    {
        if (itemSystem == ItemSystem.Instance && itemSystem != null) return; UnbindItemSystem(); itemSystem = ItemSystem.Instance; if (itemSystem == null) return; itemSystem.ActiveItemCollected += HandleItemCollected; itemSystem.PassiveItemCollected += HandleItemCollected;
    }
    private void UnbindItemSystem() { if (itemSystem == null) return; itemSystem.ActiveItemCollected -= HandleItemCollected; itemSystem.PassiveItemCollected -= HandleItemCollected; itemSystem = null; }

    private void HandleAudioContextChanged(GameManager.GameState state, int level, int room)
    {
        if (state != GameManager.GameState.GAMEPLAY)
            bossRoomClearedMusicFallback = false;
        RefreshMusicForCurrentContext(false);
    }
    private void RefreshMusicForCurrentContext(bool immediate)
    {
        MusicContext wanted = ResolveMusicContext();
        bool fallback;
        AudioClip clip = GetMusicClip(wanted, out fallback);
        if (clip == null)
        {
            StopMusic();
            currentMusicContext = MusicContext.None;
            return;
        }

        // Also compare the exact clip: Wraith King and Cyclops King are both
        // boss contexts, but intentionally have DIFFERENT music tracks.
        if (!immediate && wanted == currentMusicContext && activeMusicSource != null &&
            activeMusicSource.isPlaying && activeMusicSource.clip == clip)
        {
            return;
        }

        currentMusicContext = wanted;
        StartMusicCrossfade(clip, fallback, immediate);
    }
    private MusicContext ResolveMusicContext()
    {
        if (gameManager == null) return MusicContext.Menu;
        if (gameManager.CurrentState == GameManager.GameState.GAMEPLAY)
        {
            if (bossRoomClearedMusicFallback)
                return MusicContext.Battle;
            return gameManager.IsBossRoom ? MusicContext.Boss : MusicContext.Battle;
        }
        if (gameManager.CurrentState == GameManager.GameState.GAME_OVER)
            return gameManager.LastRunEndReason == GameManager.RunEndReason.Victory ? MusicContext.Victory : MusicContext.Defeat;
        return MusicContext.Menu;
    }
    private AudioClip GetMusicClip(MusicContext context, out bool generatedFallback)
    {
        generatedFallback = false;
        if (context == MusicContext.Menu) { if (menuLoopMp3 != null) return menuLoopMp3; generatedFallback = true; return generatedMenuMusic; }
        if (context == MusicContext.Battle) { if (battleLoopMp3 != null) return battleLoopMp3; generatedFallback = true; return generatedBattleMusic; }
        if (context == MusicContext.Boss)
        {
            bool isCyclopsKing = gameManager != null && gameManager.CurrentLevelIndex >= 2;
            AudioClip bossClip = isCyclopsKing ? cyclopsBossLoopMp3 : wraithBossLoopMp3;
            if (bossClip != null) return bossClip;
            generatedFallback = true;
            return generatedBossMusic;
        }
        if (context == MusicContext.Victory)
        {
            if (victoryLoopMp3 != null) return victoryLoopMp3;
            generatedFallback = false;
            return menuLoopMp3 != null ? menuLoopMp3 : generatedMenuMusic;
        }
        if (context == MusicContext.Defeat)
        {
            if (defeatLoopMp3 != null) return defeatLoopMp3;
            generatedFallback = false;
            return menuLoopMp3 != null ? menuLoopMp3 : generatedMenuMusic;
        }
        return null;
    }
    private void StartMusicCrossfade(AudioClip clip, bool generatedFallback, bool immediate)
    {
        if (clip == null) return; if (crossfadeCoroutine != null) { StopCoroutine(crossfadeCoroutine); crossfadeCoroutine = null; }
        AudioSource next = activeMusicSource == musicSourceA ? musicSourceB : musicSourceA; next.Stop(); next.clip = clip; next.loop = true; next.Play(); float target = generatedFallback ? generatedMusicVolumeMultiplier : externalMusicVolumeMultiplier; if (currentMusicContext == MusicContext.Victory || currentMusicContext == MusicContext.Defeat) target *= 1.4f;
        if (immediate)
        {
            musicSourceA.Stop(); musicSourceB.Stop(); next.Play(); activeMusicSource = next; sourceAWeight = next == musicSourceA ? target : 0f; sourceBWeight = next == musicSourceB ? target : 0f; ApplyMixerVolumes(); return;
        }
        crossfadeCoroutine = StartCoroutine(CrossfadeRoutine(next, target));
    }
    private IEnumerator CrossfadeRoutine(AudioSource next, float target)
    {
        AudioSource previous = activeMusicSource; float startA = sourceAWeight, startB = sourceBWeight, targetA = next == musicSourceA ? target : 0f, targetB = next == musicSourceB ? target : 0f; float elapsed = 0f, duration = Mathf.Max(0.05f, musicCrossfadeDuration);
        while (elapsed < duration) { elapsed += Time.unscaledDeltaTime; float t = Mathf.Clamp01(elapsed / duration); t = t * t * (3f - 2f * t); sourceAWeight = Mathf.Lerp(startA, targetA, t); sourceBWeight = Mathf.Lerp(startB, targetB, t); ApplyMixerVolumes(); yield return null; }
        sourceAWeight = targetA; sourceBWeight = targetB; activeMusicSource = next; if (previous != null && previous != next) previous.Stop(); ApplyMixerVolumes(); crossfadeCoroutine = null;
    }
    private void StopMusic() { if (crossfadeCoroutine != null) StopCoroutine(crossfadeCoroutine); sourceAWeight = sourceBWeight = 0f; musicSourceA.Stop(); musicSourceB.Stop(); ApplyMixerVolumes(); }
    private void ApplyMixerVolumes()
    {
        float music = gameManager != null ? gameManager.MusicVolume : 0.4f, sfx = gameManager != null ? gameManager.SfxVolume : 1f; musicSourceA.volume = Mathf.Clamp01(sourceAWeight * music); musicSourceB.volume = Mathf.Clamp01(sourceBWeight * music); sfxSource.volume = Mathf.Clamp01(sfx * sfxVolumeMultiplier);
    }

    public void PlaySfx(SfxId sound)
    {
        AudioClip clip = GetExternalSfx(sound); if (clip == null) generatedSfx.TryGetValue(sound, out clip); if (clip != null) sfxSource.PlayOneShot(clip, sound == SfxId.Explosion || sound == SfxId.BossDeath || sound == SfxId.PlayerDeath ? 1f : 0.75f);
    }
    private AudioClip GetExternalSfx(SfxId sound)
    {
        switch (sound) { case SfxId.Shoot: return shootSfx; case SfxId.Ricochet: return ricochetSfx; case SfxId.PlayerHurt: return playerHurtSfx; case SfxId.Heal: return healSfx; case SfxId.EnemyDeath: return enemyDeathSfx; case SfxId.Pickup: return pickupSfx; case SfxId.Explosion: return explosionSfx; case SfxId.Portal: return portalSfx; case SfxId.BossDeath: return bossDeathSfx; case SfxId.PlayerDeath: return playerDeathSfx; default: return null; }
    }

    private void HandleProjectileFired(Projectile p) => PlaySfx(SfxId.Shoot);
    private void HandleProjectileRicochet(Projectile p, Vector2 d) => PlaySfx(SfxId.Ricochet);
    private void HandleHealthChanged(float current, float max)
    {
        if (lastKnownPlayerHealth < 0f) { lastKnownPlayerHealth = current; return; } if (current < lastKnownPlayerHealth - 0.001f) PlaySfx(SfxId.PlayerHurt); else if (current > lastKnownPlayerHealth + 0.001f) PlaySfx(SfxId.Heal); lastKnownPlayerHealth = current;
    }
    private void HandleEnemyKilled() => PlaySfx(SfxId.EnemyDeath);
    private void HandleItemCollected(ItemSystem.ItemDefinition d) => PlaySfx(SfxId.Pickup);
    private void HandleProgressionChanged(int level, int room)
    {
        bool changed = previousLevel >= 0 && (previousLevel != level || previousRoom != room);
        previousLevel = level;
        previousRoom = room;
        if (changed)
        {
            bossRoomClearedMusicFallback = false;
            PlaySfx(SfxId.Portal);
            RefreshMusicForCurrentContext(false);
        }
    }
    private void HandleBossRewardRequested(BossController b, Vector3 p)
    {
        PlaySfx(SfxId.BossDeath);
        bossRoomClearedMusicFallback = true;
        RefreshMusicForCurrentContext(false);
    }

    private void GenerateFallbackAudio()
    {
        generatedMenuMusic = CreateMusicLoop("Generated Menu Loop", 8f, 55f, 82.5f, 0.13f);
        generatedBattleMusic = CreateMusicLoop("Generated Battle Loop", 8f, 64f, 96f, 0.18f);
        generatedBossMusic = CreateMusicLoop("Generated Boss Loop", 8f, 48f, 72f, 0.22f);
        foreach (SfxId id in Enum.GetValues(typeof(SfxId))) generatedSfx[id] = CreateGeneratedSfx(id);
    }

    private static AudioClip CreateMusicLoop(string name, float duration, float f1, float f2, float amplitude)
    {
        int count = Mathf.CeilToInt(SampleRate * duration); float[] data = new float[count];
        for (int i = 0; i < count; i++) { float t = i / (float)SampleRate; float pulse = 0.75f + Mathf.Sin(Mathf.PI * 2f * t / duration) * 0.2f; data[i] = (Mathf.Sin(Mathf.PI * 2f * f1 * t) * amplitude + Mathf.Sin(Mathf.PI * 2f * f2 * t) * amplitude * 0.45f) * pulse; }
        AudioClip clip = AudioClip.Create(name, count, 1, SampleRate, false); clip.SetData(data, 0); return clip;
    }
    private static AudioClip CreateGeneratedSfx(SfxId id)
    {
        float duration = id == SfxId.Explosion ? 0.48f : id == SfxId.BossDeath ? 0.75f : 0.20f; int count = Mathf.CeilToInt(SampleRate * duration); float[] data = new float[count];
        for (int i = 0; i < count; i++)
        {
            float t = i / (float)SampleRate, n = i / (float)Mathf.Max(1, count - 1), env = Mathf.Pow(1f - n, id == SfxId.Explosion ? 2f : 1.6f); float freq = 300f;
            switch (id) { case SfxId.Shoot: freq = Mathf.Lerp(520f, 150f, n); break; case SfxId.Ricochet: freq = 1100f; break; case SfxId.Heal: freq = Mathf.Lerp(380f, 760f, n); env = Mathf.Sin(Mathf.PI * n); break; case SfxId.Pickup: freq = n < 0.5f ? 520f : 780f; env = Mathf.Sin(Mathf.PI * n); break; case SfxId.Explosion: freq = Mathf.Lerp(82f, 33f, n); break; case SfxId.BossDeath: freq = Mathf.Lerp(110f, 28f, n); break; case SfxId.Portal: freq = Mathf.Lerp(220f, 920f, n); env = Mathf.Sin(Mathf.PI * n); break; case SfxId.PlayerHurt: freq = Mathf.Lerp(180f, 65f, n); break; case SfxId.EnemyDeath: freq = Mathf.Lerp(130f, 48f, n); break; }
            float tone = Mathf.Sin(Mathf.PI * 2f * freq * t); float noise = Mathf.Sin(t * 12437.137f) * Mathf.Sin(t * 7343.711f + 1.37f); data[i] = (tone * 0.55f + (id == SfxId.Explosion || id == SfxId.EnemyDeath || id == SfxId.BossDeath ? noise * 0.28f : 0f)) * env * 0.65f;
        }
        AudioClip clip = AudioClip.Create("Generated " + id, count, 1, SampleRate, false); clip.SetData(data, 0); return clip;
    }
    private static void DestroyGeneratedClip(AudioClip clip) { if (clip != null) Destroy(clip); }
}
