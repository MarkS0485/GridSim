using System.Globalization;

namespace GridSim.Core.Interpose;

/// <summary>
/// N346 (CF3, October 2026): the running count of decoded points behind the interposer's stop summary. Step positions
/// (IEC 60870-5 types 5 / 32) and bitstrings (types 7 / 33) are decoded since N337 but never become estimator inputs,
/// so counting them only inside the total hid a station that sends them; they are now also counted on their own.
/// <see cref="Points"/> keeps its old meaning: every decoded point, these two kinds included.
/// </summary>
public sealed class RtuPointTally
{
    /// <summary>Every decoded point (all types), as the stop summary's <c>points</c> field has always counted.</summary>
    public long Points { get; private set; }

    /// <summary>Decoded <see cref="RtuPointType.StepPosition"/> points (also counted in <see cref="Points"/>).</summary>
    public long StepPositions { get; private set; }

    /// <summary>Decoded <see cref="RtuPointType.Bitstring"/> points (also counted in <see cref="Points"/>).</summary>
    public long Bitstrings { get; private set; }

    /// <summary>Count the points one frame decoded to (an empty list adds nothing).</summary>
    public void Add(IReadOnlyList<RtuPoint> points)
    {
        ArgumentNullException.ThrowIfNull(points);
        Points += points.Count;
        foreach (RtuPoint p in points)
        {
            if (p.Type == RtuPointType.StepPosition) StepPositions++;
            else if (p.Type == RtuPointType.Bitstring) Bitstrings++;
        }
    }

    /// <summary>The console stop line's fields: <c>points P, step-positions S, bitstrings B</c> (invariant culture).</summary>
    public string ConsoleFields()
        => string.Create(CultureInfo.InvariantCulture, $"points {Points}, step-positions {StepPositions}, bitstrings {Bitstrings}");

    /// <summary>The audit outcome's fields: <c>points=P step-positions=S bitstrings=B</c> (invariant culture).</summary>
    public string AuditFields()
        => string.Create(CultureInfo.InvariantCulture, $"points={Points} step-positions={StepPositions} bitstrings={Bitstrings}");
}
