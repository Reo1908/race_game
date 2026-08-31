using UnityEngine;

/// <summary>
/// Randomizes the pitch of the attached AudioSource once, when this instance loads.
/// Useful for effect sounds (footsteps, impacts, gunshots, etc.) so repeated
/// instances don't all sound identical.
/// </summary>
[RequireComponent(typeof(AudioSource))]
public class RandomPitch : MonoBehaviour
{
    [Header("Pitch Range")]
    [Tooltip("Minimum possible pitch.")]
    public float minPitch = 0.9f;

    [Tooltip("Maximum possible pitch.")]
    public float maxPitch = 1.1f;

    private void Awake()
    {
        AudioSource source = GetComponent<AudioSource>();
        source.pitch = Random.Range(minPitch, maxPitch);
    }
}
