using UnityEngine;

/// <summary>
/// Procedurally generated UI blips, so the menu ships with sound without
/// dragging any audio assets into the project. Square-ish waves with a hard
/// exponential decay and a touch of bitcrush — the cheap arcade cabinet sound.
/// </summary>
public static class PauseMenuBleeps
{
    private const int SampleRate = 44100;

    /// <summary>Cursor moved one row.</summary>
    public static AudioClip Move()
    {
        return Build("PauseBlip_Move", 0.045f, 1180f, 1180f, 0.55f, 12f);
    }

    /// <summary>Entry confirmed.</summary>
    public static AudioClip Select()
    {
        return Build("PauseBlip_Select", 0.13f, 660f, 1320f, 0.5f, 9f);
    }

    /// <summary>Backed out of a sub page.</summary>
    public static AudioClip Back()
    {
        return Build("PauseBlip_Back", 0.10f, 900f, 460f, 0.5f, 11f);
    }

    /// <summary>Menu opening — falling sweep, like a machine spinning down.</summary>
    public static AudioClip Open()
    {
        return Build("PauseBlip_Open", 0.22f, 1500f, 380f, 0.35f, 6f);
    }

    /// <summary>Menu closing — rising sweep back to speed.</summary>
    public static AudioClip Close()
    {
        return Build("PauseBlip_Close", 0.18f, 420f, 1500f, 0.35f, 7f);
    }

    private static AudioClip Build(string name, float duration, float startHz, float endHz, float duty, float decay)
    {
        int samples = Mathf.Max(1, Mathf.RoundToInt(duration * SampleRate));
        float[] data = new float[samples];

        float phase = 0f;
        for (int i = 0; i < samples; i++)
        {
            float t = (float)i / samples;

            // Exponential pitch glide reads better than a linear one.
            float hz = Mathf.Lerp(startHz, endHz, t * t);
            phase += hz / SampleRate;
            phase -= Mathf.Floor(phase);

            float square = phase < duty ? 1f : -1f;

            // Thin sine underneath takes the harshest edge off the square.
            float body = square * 0.72f + Mathf.Sin(phase * Mathf.PI * 2f) * 0.28f;

            float envelope = Mathf.Exp(-decay * t) * (1f - Mathf.Exp(-t * 220f));

            // 5-bit crush for the cabinet grit.
            float v = body * envelope * 0.22f;
            data[i] = Mathf.Round(v * 32f) / 32f;
        }

        AudioClip clip = AudioClip.Create(name, samples, 1, SampleRate, false);
        clip.SetData(data, 0);
        return clip;
    }
}
