namespace GridSim.Core.Frequency;

/// <summary>
/// Drives playback of a fine (20 ms) frequency timeline: a movable cursor over the
/// frames plus a playback rate in <em>slices per second</em>. 50 slices/s replays
/// 20 ms data in real time; 100 is 2x fast-forward; 5 is slow-motion. UI-agnostic
/// and fully testable  -  a console view, a chart, or a test just reads
/// <see cref="Current"/> and calls <see cref="Advance"/>, pacing itself by
/// <see cref="SlicePeriod"/>.
/// </summary>
public sealed class ReplayController
{
    public const double MinSlicesPerSecond = 0.25;
    public const double MaxSlicesPerSecond = 2000.0;

    private readonly IReadOnlyList<FrequencyPoint> _frames;

    public ReplayController(IReadOnlyList<FrequencyPoint> frames, double slicesPerSecond = 50.0)
    {
        _frames = frames ?? throw new ArgumentNullException(nameof(frames));
        SlicesPerSecond = Clamp(slicesPerSecond);
    }

    public int Index { get; private set; }
    public int Count => _frames.Count;
    public bool Paused { get; set; }
    public double SlicesPerSecond { get; private set; }

    public bool AtEnd => Index >= Count - 1;
    public FrequencyPoint Current => Count == 0 ? default : _frames[Math.Clamp(Index, 0, Count - 1)];
    public double ProgressFraction => Count <= 1 ? 1.0 : (double)Index / (Count - 1);

    /// <summary>Wall-clock time each emitted slice should occupy at the current rate.</summary>
    public TimeSpan SlicePeriod => TimeSpan.FromSeconds(1.0 / SlicesPerSecond);

    public void Advance(int n = 1) => Index = Math.Clamp(Index + n, 0, Math.Max(0, Count - 1));
    public void Rewind(int n = 1) => Advance(-n);
    public void Seek(int index) => Index = Math.Clamp(index, 0, Math.Max(0, Count - 1));
    public void SeekFraction(double f) => Seek((int)Math.Round(Math.Clamp(f, 0, 1) * (Count - 1)));
    public void Restart() => Index = 0;

    public void SetSpeed(double slicesPerSecond) => SlicesPerSecond = Clamp(slicesPerSecond);
    public void Faster() => SetSpeed(SlicesPerSecond * 2);
    public void Slower() => SetSpeed(SlicesPerSecond / 2);

    private static double Clamp(double s) => Math.Clamp(s, MinSlicesPerSecond, MaxSlicesPerSecond);
}
