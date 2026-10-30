using Xunit;
using Xunit.Sdk;

namespace GridSim.Tests;

/// <summary>
/// Bus counts of the bundled cases the RAM gate (<see cref="MachineCapacity"/>) cares about, each in one place.
/// <c>MachineCapacityTests</c> loads every one of them and checks the number, so these cannot drift from the data.
/// </summary>
public static class CaseSizes
{
    /// <summary>data/gb-full: the 8 GB anchor (Mark, 2026-10-12: about 1.5 GB free RAM to study it).</summary>
    public const int GbFull = 3539;

    /// <summary>data/intl/continental-europe.</summary>
    public const int ContinentalEurope = 5824;

    /// <summary>data/matpower/pegase/case9241pegase.m: the largest bundled case.</summary>
    public const int Pegase9241 = 9241;

    /// <summary>Bundled MATPOWER files larger than <see cref="MachineCapacity.AnchorBuses"/> (relative to data/matpower,
    /// forward slashes). Every other bundled file is at or below the anchor, which the tests check.</summary>
    public static readonly IReadOnlyDictionary<string, int> LargeMatpowerFiles = new Dictionary<string, int>(StringComparer.Ordinal)
    {
        ["pegase/case9241pegase.m"] = Pegase9241,
    };
}

/// <summary>Which Newton engine a gated test runs: dense storage grows with n squared, sparse roughly with n.</summary>
public enum Engine
{
    Sparse,
    Dense,
}

/// <summary>
/// The RAM gate (Mark, 2026-10-12): the installed memory decides, at test discovery, the largest case this machine runs.
/// A test of a case above the cap is reported as SKIPPED with the reason, never as passed. The test itself, and every
/// assertion in it, is unchanged: this decides WHEN a test applies (a machine that physically cannot hold the case does
/// not run it), not what it checks, and the validation stands wherever the hardware can run it.
///
/// The two anchors, both Mark's: an 8 GB machine runs every case up to gb-full (3,539 buses, about 1.5 GB free), and a
/// 16 GB machine runs the whole suite (slowly, with some swapping, but everything passes). Between them the sparse cap
/// rises linearly in RAM and the dense cap along n squared (dense storage is about 32 n^2 bytes a matrix); below 8 GB
/// both fall the same way. The machine is classed by INSTALLED memory, rounded up to whole GB (an "8 GB" laptop reports
/// about 7.7 GB once the firmware and graphics have their share), never by free memory, so one machine always runs the
/// same set of tests.
///
/// GRIDSIM_TEST_MAX_BUSES overrides the cap for both engines: "all" runs everything (the release workflow sets it, so a
/// release is never validated with tests left out), a whole number sets the cap. Anything else fails loud at discovery.
/// </summary>
public static class MachineCapacity
{
    /// <summary>The override environment variable.</summary>
    public const string Variable = "GRIDSIM_TEST_MAX_BUSES";

    /// <summary>The minimum system: runs everything up to gb-full.</summary>
    public const int MinimumGb = 8;

    /// <summary>The cap at <see cref="MinimumGb"/>, both engines: gb-full with headroom.</summary>
    public const int AnchorBuses = 3600;

    /// <summary>From this much installed RAM the whole suite runs.</summary>
    public const int FullSuiteGb = 16;

    /// <summary>No cap.</summary>
    public const int Unlimited = int.MaxValue;

    private static readonly Lazy<(int Gb, int Sparse, int Dense, string Source)> Current = new(Measure);

    /// <summary>Installed RAM as this process sees it, rounded up to whole GB.</summary>
    public static int InstalledGb => Current.Value.Gb;

    /// <summary>This machine's cap for <paramref name="engine"/>, after any override.</summary>
    public static int Cap(Engine engine) => engine == Engine.Dense ? Current.Value.Dense : Current.Value.Sparse;

    /// <summary>One line describing this machine's tier, for test output.</summary>
    public static string Describe()
    {
        var c = Current.Value;
        return $"GridSim test RAM gate: {c.Gb} GB installed, cap {Show(c.Sparse)} sparse / {Show(c.Dense)} dense ({c.Source})";
    }

    /// <summary>Bytes to whole GB, rounded up (7.7 GB reads as 8).</summary>
    public static int NominalGb(long bytes) => (int)Math.Ceiling(bytes / (double)(1L << 30));

    /// <summary>The cap for a machine with <paramref name="gb"/> GB installed (no override).</summary>
    public static int MaxBuses(int gb, Engine engine)
    {
        if (gb >= FullSuiteGb) return Unlimited;
        if (engine == Engine.Sparse)
        {
            if (gb <= MinimumGb) return (int)((long)AnchorBuses * gb / MinimumGb);
            double t = (gb - MinimumGb) / (double)(FullSuiteGb - MinimumGb);
            return (int)(AnchorBuses + t * (CaseSizes.Pegase9241 - AnchorBuses));
        }
        if (gb <= MinimumGb) return (int)(AnchorBuses * Math.Sqrt(gb / (double)MinimumGb));
        double u = (gb - MinimumGb) / (double)(FullSuiteGb - MinimumGb);
        double a2 = (double)AnchorBuses * AnchorBuses, b2 = (double)CaseSizes.Pegase9241 * CaseSizes.Pegase9241;
        return (int)Math.Sqrt(a2 + u * (b2 - a2));
    }

    /// <summary>The override's cap: null when <paramref name="value"/> is unset or blank, <see cref="Unlimited"/> for
    /// "all", the number for a positive whole number. Anything else throws (fail loud, never a silent default).</summary>
    public static int? ParseOverride(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        string v = value.Trim();
        if (string.Equals(v, "all", StringComparison.OrdinalIgnoreCase)) return Unlimited;
        if (int.TryParse(v, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int n) && n > 0)
            return n;
        throw new InvalidOperationException($"{Variable}='{value}' is not 'all' or a positive whole number of buses.");
    }

    /// <summary>The skip reason for a test of a <paramref name="buses"/>-bus case on <paramref name="engine"/>, or null
    /// when this machine runs it.</summary>
    public static string? SkipReason(int buses, Engine engine)
    {
        int cap = Cap(engine);
        if (buses <= cap) return null;
        return $"RAM gate: a {buses:N0}-bus case on the {engine.ToString().ToLowerInvariant()} engine is above this machine's cap of "
            + $"{cap:N0} buses ({Current.Value.Gb} GB installed, {Current.Value.Source}; 8 GB runs up to gb-full, 16 GB runs everything). "
            + $"Set {Variable}=all to run it anyway.";
    }

    private static string Show(int cap) => cap == Unlimited ? "unlimited" : cap.ToString("N0", System.Globalization.CultureInfo.InvariantCulture);

    private static (int, int, int, string) Measure()
    {
        int gb = NominalGb(GC.GetGCMemoryInfo().TotalAvailableMemoryBytes);
        int? forced = ParseOverride(Environment.GetEnvironmentVariable(Variable));
        if (forced is int f) return (gb, f, f, $"{Variable}={Environment.GetEnvironmentVariable(Variable)!.Trim()}");
        return (gb, MaxBuses(gb, Engine.Sparse), MaxBuses(gb, Engine.Dense), "from installed RAM");
    }
}

/// <summary>A fact about one large case: skipped, with the reason, when the case is above this machine's RAM cap.</summary>
public sealed class LargeCaseFactAttribute : FactAttribute
{
    public LargeCaseFactAttribute(int buses, Engine engine)
    {
        Buses = buses;
        Engine = engine;
        Skip = MachineCapacity.SkipReason(buses, engine);
    }

    /// <summary>The case's bus count.</summary>
    public int Buses { get; }

    /// <summary>The engine the test runs.</summary>
    public Engine Engine { get; }
}

/// <summary>A theory whose every row is one large case: skipped, with the reason, above this machine's RAM cap.</summary>
public sealed class LargeCaseTheoryAttribute : TheoryAttribute
{
    public LargeCaseTheoryAttribute(int buses, Engine engine)
    {
        Buses = buses;
        Engine = engine;
        Skip = MachineCapacity.SkipReason(buses, engine);
    }

    /// <summary>The case's bus count.</summary>
    public int Buses { get; }

    /// <summary>The engine the test runs.</summary>
    public Engine Engine { get; }
}

/// <summary>One theory row for a large case: that row alone is skipped, with the reason, above this machine's RAM cap;
/// the theory's other rows run as before. (xUnit 2's InlineDataAttribute is sealed, so this is the same one-row data
/// attribute built on DataAttribute.)</summary>
public sealed class LargeCaseInlineDataAttribute : DataAttribute
{
    private readonly object[] _data;

    public LargeCaseInlineDataAttribute(int buses, Engine engine, params object[] data)
    {
        _data = data;
        Buses = buses;
        Engine = engine;
        Skip = MachineCapacity.SkipReason(buses, engine);
    }

    /// <summary>The case's bus count.</summary>
    public int Buses { get; }

    /// <summary>The engine the test runs.</summary>
    public Engine Engine { get; }

    /// <summary>The row's arguments.</summary>
    public IReadOnlyList<object> Data => _data;

    /// <inheritdoc />
    public override IEnumerable<object[]> GetData(System.Reflection.MethodInfo testMethod) => new[] { _data };
}
