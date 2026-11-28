using Xunit;

namespace GridSim.Tests;

/// <summary>
/// Tests that touch the built program's own data/matpower folder. N229 (Lf5RemainingTests) places a case copy inside it
/// for a moment, and D039 (Doc7ValidationSidecarTests) runs conformance, which sweeps every case in it. Run at the same
/// time, the sweep listed the copy and then found it gone ("1 could not be run: gate FAILED", windows-11-arm, 2.1.174).
/// Classes in one collection never run at the same time; the collection still runs in parallel with every other test.
/// </summary>
[CollectionDefinition(Name)]
public sealed class InstallDataFolderTestGroup
{
    public const string Name = "Install data folder";
}
