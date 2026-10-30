using Xunit;

namespace GridSim.Tests;

/// <summary>
/// Stream LF6 of the October 2026 programme (calibration and setting bounds). Items: N318 (the protection frequency
/// setting rule is direction-aware: UF / LFDD strictly below the settings nominal, OF strictly above it, each within
/// 10 Hz on its own side; replaces N304's symmetric 5 Hz) and N317 (the 2019-08-09 event model carries the recorded
/// response holdings and demand effect through the response-service machinery instead of the generic governor and
/// 1 %/Hz load damping). Ruling R1 (Mark delegated EE calls 2026-10-04) covers every behaviour change. One partial file
/// per item (Lf6CalibrationBoundsTests.&lt;item&gt;.cs).
/// </summary>
public sealed partial class Lf6CalibrationBoundsTests
{
}
