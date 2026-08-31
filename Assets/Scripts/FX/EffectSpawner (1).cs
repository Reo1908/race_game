using System.Collections;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Spawns a configurable list of effect prefabs when this object loads.
/// Each entry can be parented or unparented, positioned at this object's
/// transform (with offset) or at a fixed world offset, delayed, and
/// auto-destroyed after a duration. Optionally, this object itself can be
/// destroyed once every effect has finished playing out.
/// </summary>
public class EffectSpawner : MonoBehaviour
{
    [System.Serializable]
    public class EffectEntry
    {
        [Header("Prefab")]
        [Tooltip("The prefab to spawn.")]
        public GameObject prefab;

        [Tooltip("Optional name shown in the list for readability (Inspector only).")]
        public string label;

        [Header("Parenting")]
        [Tooltip("If true, the spawned instance becomes a child of this object and moves with it. If false, it's spawned independently in the world.")]
        public bool parentToObject = false;

        [Tooltip("Only used if parented. If false, the instance keeps its prefab's local scale instead of inheriting this object's scale.")]
        public bool inheritScale = true;

        [Header("Position / Rotation")]
        [Tooltip("If true, spawn at this object's position (plus offset). If false, offset is treated as a raw world position.")]
        public bool matchPosition = true;

        [Tooltip("If true, spawn using this object's rotation (plus offset). If false, offset is treated as a raw world rotation.")]
        public bool matchRotation = true;

        [Tooltip("Offset added to the base position (local to this object's rotation if matching position).")]
        public Vector3 positionOffset;

        [Tooltip("Euler angle offset added to the base rotation.")]
        public Vector3 rotationOffset;

        [Header("Timing")]
        [Tooltip("Seconds to wait before spawning this effect.")]
        [Min(0f)]
        public float delay = 0f;

        [Tooltip("Seconds before the spawned instance is automatically destroyed. 0 = never auto-destroy.")]
        [Min(0f)]
        public float duration = 0f;

        [Tooltip("If true, delay/duration use unscaled time (ignores Time.timeScale, e.g. for pause menus).")]
        public bool useUnscaledTime = false;

        // Runtime reference to the last spawned instance (not serialized).
        [System.NonSerialized] public GameObject spawnedInstance;
    }

    [Header("Effects")]
    [Tooltip("Expandable list of effects to spawn. Add as many as you like.")]
    public List<EffectEntry> effects = new List<EffectEntry>();

    [Header("Playback")]
    [Tooltip("If true, all effects start automatically when this object loads (Start).")]
    public bool playOnStart = true;

    [Tooltip("Global multiplier applied to every entry's delay and duration. Useful for quickly speeding up/slowing down all effects.")]
    [Min(0f)]
    public float timeScaleMultiplier = 1f;

    [Header("Cleanup")]
    [Tooltip("If true, this GameObject (the one this script is on) destroys itself once every entry has finished (delay elapsed + duration elapsed). Handy as a safety net for effect prefabs that don't clean themselves up. Ignored if any entry has duration = 0 (infinite), since that entry never 'finishes'.")]
    public bool destroySelfWhenAllEffectsFinished = false;

    [Tooltip("Extra buffer (seconds) added after the last effect finishes before this object is destroyed.")]
    [Min(0f)]
    public float extraDelayBeforeSelfDestroy = 0f;

    private readonly List<Coroutine> _running = new List<Coroutine>();

    private void Start()
    {
        if (playOnStart)
        {
            Play();
        }
    }

    /// <summary>
    /// Starts spawning all effects from scratch. Safe to call multiple times
    /// (e.g. re-triggering the effect set from another script/event).
    /// </summary>
    public void Play()
    {
        StopAll();
        StartCoroutine(PlayAllRoutine());

        if (destroySelfWhenAllEffectsFinished)
        {
            TryScheduleSelfDestroy();
        }
    }

    /// <summary>
    /// Spawns a single entry immediately by index, ignoring its configured delay.
    /// </summary>
    public void PlayEntryImmediate(int index)
    {
        if (index < 0 || index >= effects.Count) return;
        SpawnEntry(effects[index]);
    }

    /// <summary>
    /// Stops any pending (not-yet-spawned) effects. Already-spawned instances are untouched.
    /// </summary>
    public void StopAll()
    {
        foreach (var c in _running)
        {
            if (c != null) StopCoroutine(c);
        }
        _running.Clear();
    }

    /// <summary>
    /// Destroys every instance currently spawned by this spawner.
    /// </summary>
    public void DestroyAllSpawned()
    {
        foreach (var entry in effects)
        {
            if (entry.spawnedInstance != null)
            {
                Destroy(entry.spawnedInstance);
                entry.spawnedInstance = null;
            }
        }
    }

    private IEnumerator PlayAllRoutine()
    {
        foreach (var entry in effects)
        {
            var c = StartCoroutine(SpawnEntryDelayed(entry));
            _running.Add(c);
        }

        yield break;
    }

    /// <summary>
    /// Works out when the last effect will finish (its delay + duration) and schedules
    /// this object to be destroyed at that point. Skips scheduling (with a warning) if
    /// any entry has duration = 0, since an infinite-duration effect never "finishes".
    /// </summary>
    private void TryScheduleSelfDestroy()
    {
        if (effects.Count == 0) return;

        float latestFinish = 0f;
        bool usesUnscaledTime = false;

        foreach (var entry in effects)
        {
            if (entry.prefab == null) continue;

            if (entry.duration <= 0f)
            {
                Debug.LogWarning($"[EffectSpawner] '{name}' has 'Destroy Self When All Effects Finished' enabled, but entry '{entry.label}' has duration = 0 (infinite). Skipping self-destroy scheduling.", this);
                return;
            }

            float finish = (entry.delay + entry.duration) * Mathf.Max(0f, timeScaleMultiplier);
            if (finish > latestFinish) latestFinish = finish;

            // If any entry uses unscaled time, play it safe and time the whole wait unscaled too.
            if (entry.useUnscaledTime) usesUnscaledTime = true;
        }

        StartCoroutine(SelfDestroyRoutine(latestFinish + extraDelayBeforeSelfDestroy, usesUnscaledTime));
    }

    private IEnumerator SelfDestroyRoutine(float seconds, bool useUnscaledTime)
    {
        float t = 0f;
        while (t < seconds)
        {
            t += useUnscaledTime ? Time.unscaledDeltaTime : Time.deltaTime;
            yield return null;
        }

        if (this != null) Destroy(gameObject);
    }

    private IEnumerator SpawnEntryDelayed(EffectEntry entry)
    {
        if (entry.delay > 0f)
        {
            float t = 0f;
            float target = entry.delay * Mathf.Max(0f, timeScaleMultiplier);
            while (t < target)
            {
                t += entry.useUnscaledTime ? Time.unscaledDeltaTime : Time.deltaTime;
                yield return null;
            }
        }

        SpawnEntry(entry);
    }

    private void SpawnEntry(EffectEntry entry)
    {
        if (entry.prefab == null)
        {
            Debug.LogWarning($"[EffectSpawner] Entry '{entry.label}' on {name} has no prefab assigned; skipping.", this);
            return;
        }

        Vector3 basePos = entry.matchPosition ? transform.position : Vector3.zero;
        Quaternion baseRot = entry.matchRotation ? transform.rotation : Quaternion.identity;

        Vector3 finalPos = basePos + baseRot * entry.positionOffset;
        Quaternion finalRot = baseRot * Quaternion.Euler(entry.rotationOffset);

        Transform parent = entry.parentToObject ? transform : null;
        GameObject instance = Instantiate(entry.prefab, finalPos, finalRot, parent);

        if (entry.parentToObject && !entry.inheritScale)
        {
            instance.transform.localScale = entry.prefab.transform.localScale;
        }

        entry.spawnedInstance = instance;

        if (entry.duration > 0f)
        {
            float scaledDuration = entry.duration * Mathf.Max(0f, timeScaleMultiplier);
            if (entry.useUnscaledTime)
            {
                StartCoroutine(DestroyAfterUnscaled(instance, scaledDuration));
            }
            else
            {
                Destroy(instance, scaledDuration);
            }
        }
    }

    private IEnumerator DestroyAfterUnscaled(GameObject instance, float seconds)
    {
        float t = 0f;
        while (t < seconds)
        {
            t += Time.unscaledDeltaTime;
            yield return null;
        }
        if (instance != null) Destroy(instance);
    }

#if UNITY_EDITOR
    private void OnDrawGizmosSelected()
    {
        if (effects == null) return;
        Gizmos.color = Color.cyan;
        foreach (var entry in effects)
        {
            Vector3 basePos = entry.matchPosition ? transform.position : Vector3.zero;
            Quaternion baseRot = entry.matchRotation ? transform.rotation : Quaternion.identity;
            Vector3 finalPos = basePos + baseRot * entry.positionOffset;
            Gizmos.DrawWireSphere(finalPos, 0.15f);
        }
    }
#endif
}
