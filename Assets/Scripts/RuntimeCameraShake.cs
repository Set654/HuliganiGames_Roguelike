using UnityEngine;

[DisallowMultipleComponent]
public sealed class RuntimeCameraShake : MonoBehaviour
{
    private static RuntimeCameraShake active;
    private static Vector3 home = new Vector3(0f, 0f, -10f);
    private float remaining;
    private float duration;
    private float strength;

    private void Awake() { active = this; }

    public static void SetHome(Vector3 position)
    {
        home = position;
    }
    private void OnDestroy() { if (active == this) active = null; }

    public static void Shake(float amplitude, float seconds)
    {
        if (active == null)
        {
            Camera main = Camera.main;
            if (main == null) return;
            active = main.GetComponent<RuntimeCameraShake>();
            if (active == null) active = main.gameObject.AddComponent<RuntimeCameraShake>();
        }
        active.strength = Mathf.Max(active.strength, Mathf.Max(0f, amplitude));
        active.duration = Mathf.Max(active.duration, Mathf.Max(0.01f, seconds));
        active.remaining = Mathf.Max(active.remaining, seconds);
    }

    private void LateUpdate()
    {
        // The game camera does not follow the player. Reset the position each frame
        // before adding shake so there is no accumulated displacement or drift.
        if (remaining > 0f && Time.timeScale > 0f)
        {
            float factor = Mathf.Clamp01(remaining / Mathf.Max(0.01f, duration));
            Vector2 jitter = Random.insideUnitCircle * strength * factor;
            transform.position = home + new Vector3(jitter.x, jitter.y, 0f);
            remaining = Mathf.Max(0f, remaining - Time.deltaTime);
            if (remaining <= 0f) { strength = 0f; duration = 0f; }
        }
        else
        {
            transform.position = home;
        }
    }
}