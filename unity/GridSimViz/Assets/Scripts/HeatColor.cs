using UnityEngine;

/// <summary>Maps a per-unit voltage to a green->yellow->red heat colour around 1.0 p.u.</summary>
public static class HeatColor
{
    private static readonly Color Green = new(0.20f, 0.90f, 0.35f);
    private static readonly Color Yellow = new(0.95f, 0.85f, 0.20f);
    private static readonly Color Red = new(0.95f, 0.25f, 0.20f);

    public static Color ForVoltage(float vm)
    {
        float t = Mathf.Clamp01(Mathf.Abs(vm - 1.0f) / 0.08f); // 0 at nominal, 1 at +/-0.08 p.u.
        return t < 0.5f ? Color.Lerp(Green, Yellow, t * 2f) : Color.Lerp(Yellow, Red, (t - 0.5f) * 2f);
    }
}
