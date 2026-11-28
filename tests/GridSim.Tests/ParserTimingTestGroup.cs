using Xunit;

namespace GridSim.Tests;

/// <summary>
/// The non-parallel collection for Io05's parse timings (BugHunt1125HostileFileTests), as GR-881's layout timing has.
/// The large parse allocates tens of megabytes and so sets off full collections, whose cost grows with everything else
/// alive in the test process; run beside other tests holding large networks, the large timing took their collection
/// cost too and a linear parse measured 73x for a sixteenfold step (macos-latest, the 2.1.174 RC-5 release gate),
/// while the same test passed run on its own. Run alone, the timings measure the parse.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ParserTimingTestGroup
{
    /// <summary>The collection name.</summary>
    public const string Name = "Parser timing (runs alone)";
}
