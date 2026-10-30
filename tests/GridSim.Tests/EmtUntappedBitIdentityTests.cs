using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using GridSim.Core.Dynamics.Emt;
using GridSim.Core.IO;
using GridSim.Core.IO.Json;
using GridSim.Core.IO.Matpower;
using GridSim.Core.Model;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// R078 (October 2026 review) bit-identity guard. The tap companion and the series-capacitor element change the
/// EMT build only for branches with an off-nominal tap or a negative series reactance. Every bundled case with
/// neither (gb-full, uk, IEEE 9 and 30, case33bw) must produce EXACTLY the same EMT trajectory as before the fix.
/// The expected SHA-256 digests below were computed with the pre-R078 engine (commit 285eb1b, version 1.95.059)
/// by this very procedure, and are pinned here so any later drift on an untapped network fails loud.
/// <para>The run is the CLI's default shape: an ideal DC source (the CLI default, which also keeps a
/// transcendental library function out of the digest) at the first generator bus, loads on, dt = 20 us, 40 ms,
/// in three modes: lumped pi sections, travelling-wave lines, and lumped with a bus fault closed at 10 ms and
/// cleared at 20 ms (which exercises the switch path and the Critical Damping Adjustment burst).</para>
/// </summary>
public class EmtUntappedBitIdentityTests
{
    private static string DataDir => Path.Combine(AppContext.BaseDirectory, "data");

    private static GridModel Load(string name) => name switch
    {
        // D2 N017 (Ruling R1: Mark delegated EE calls 2026-10-04; source: Anderson and Fouad 2003, Example 2.6): the built-in
        // case9 is now the WSCC system's 60 Hz, and EMT branch L and C scale with 1 / (2 pi f0), so its trajectory moves.
        // This test pins the ENGINE against the pre-R078 engine on an untapped case (frequency is only an input), so the
        // case9 rows keep the 50 Hz network their digests were captured on: the same model at its old nominal frequency.
        // Before: IeeeCases.Case9() (then 50 Hz). After: IeeeCases.Case9().CopyWith(frequencyHz: 50.0); digests unchanged.
        // The 60 Hz case9 EMT run is covered by the D2 notes' batch list (new digests to add there once captured).
        "case9" => IeeeCases.Case9().CopyWith(frequencyHz: 50.0),
        "case30" => IeeeCases.Case30(),
        // Moved 2026-10 (N165, authorised by Mark's 2026-10-02 best-judgement ruling): ParseFile now normalises a raw
        // feeder by default; the pinned digest is of the model as written (the pre-fix trajectory this test proves
        // unchanged), so the as-written parse is asked for explicitly. Before: ParseFile(path). After: ParseFile(path,
        // MatpowerUnitHandling.AsWritten). The digest is unchanged.
        "case33bw" => MatpowerParser.ParseFile(Path.Combine(DataDir, "matpower", "dist", "case33bw.m"), MatpowerUnitHandling.AsWritten),
        _ => JsonGridLoader.LoadDirectory(Path.Combine(DataDir, name)),
    };

    /// <summary>The digest of one standard run: SHA-256 over every recorded instant and probe value, as raw
    /// IEEE-754 bytes, so a change in the last bit of any sample changes the digest.</summary>
    internal static string TrajectoryDigest(GridModel model, string mode)
    {
        int src = model.Generators.Count > 0 ? model.Generators[0].Bus : model.Buses[0].Id;
        bool lines = mode == "lines";
        EmtNetwork net = EmtNetwork.FromGridModel(model,
            new EmtBuildOptions(new[] { new EmtSourceSpec(src, Rs: 0, AmplitudePu: 1.0, FrequencyHz: 0.0) }, IncludeLoads: true, UseTravellingWaveLines: lines));

        var events = new List<EmtEvent>();
        if (mode == "fault")
        {
            const int faultId = 9_000_000;
            var elements = new List<EmtElement>(net.Elements)
            {
                new SwitchElement(faultId, net.NodeCount - 1, EmtNetwork.Ground, Ron: 0.01, Roff: 1e9, InitiallyClosed: false),
            };
            events.Add(new SwitchEvent(0.01, faultId, Close: true));
            events.Add(new SwitchEvent(0.02, faultId, Close: false));
            net = new EmtNetwork(net.NodeCount, elements, net.BusToNode, net.SeriesBranchIds);
        }

        // At most ~150 node voltages (evenly strided) plus the first 20 series-branch currents.
        int stride = Math.Max(1, net.NodeCount / 150);
        var probes = new List<EmtProbe>();
        for (int i = 0; i < net.NodeCount; i += stride) probes.Add(new EmtProbe("v" + i, EmtProbeKind.NodeVoltage, i));
        for (int k = 0; k < Math.Min(20, net.SeriesBranchIds.Count); k++)
            probes.Add(new EmtProbe("i" + k, EmtProbeKind.BranchCurrent, net.SeriesBranchIds[k].ElementId));

        const double dt = 20e-6;
        EmtResult res = new EmtEngine(net, dt).Simulate(new EmtCase(net, dt, 0.04, probes, events));

        var bytes = new List<byte>();
        foreach (EmtPoint p in res.Points)
        {
            bytes.AddRange(BitConverter.GetBytes(p.TSeconds));
            foreach (double v in p.Probe) bytes.AddRange(BitConverter.GetBytes(v));
        }
        return Convert.ToHexString(SHA256.HashData(bytes.ToArray()));
    }

    [Theory]
    // Moved 2026-10 (D-DOC-026, docs-session finding, Ruling R1: Mark delegated 2026-10-04): CsrMatrix.Builder now sums a
    // duplicated cell in insertion order; this digest recorded the introsort's arbitrary order in the node rows over
    // 16 entries. Same network and elements; only the rounding of the nodal-matrix diagonals moves. Before
    // AD13FBE9D232CAD9..., after 429D50FDF0B03D0F...; 340008 of 350175 recorded values differ, max |diff| 9.650646948244912E-11, max relative 1.4148059495374372E-06.
    [InlineData("gb-full", "lumped", "429D50FDF0B03D0F300031F914A3E02E3C4AE7CF5C4BDE1C48D24E2E1224090E")]
    // Moved 2026-10 (D-DOC-026, docs-session finding, Ruling R1: Mark delegated 2026-10-04): CsrMatrix.Builder now sums a
    // duplicated cell in insertion order; this digest recorded the introsort's arbitrary order in the node rows over
    // 16 entries. Same network and elements; only the rounding of the nodal-matrix diagonals moves. Before
    // 20E1B8BB9840BB3C..., after F963983E884043B4...; 323026 of 350175 differ, max |diff| 4.222655558550059E-11, max relative 5.758866834941773E-10.
    [InlineData("gb-full", "lines", "F963983E884043B452460FB516DEDDD6752BB00FF37355BE2FD43E04ED87F2DF")]
    // Moved 2026-10 (D-DOC-026, docs-session finding, Ruling R1: Mark delegated 2026-10-04): CsrMatrix.Builder now sums a
    // duplicated cell in insertion order; this digest recorded the introsort's arbitrary order in the node rows over
    // 16 entries. Same network and elements; only the rounding of the nodal-matrix diagonals moves. Before
    // 7C90AEDD42652249..., after 5735D48C27A4BFEF...; 339540 of 350175 differ, max |diff| 6.635980653868501E-11, max relative 2.190349190378535E-06.
    [InlineData("gb-full", "fault", "5735D48C27A4BFEF7957463DBAB20B62BF2F6E87797E35B70FDB597537274A3E")]
    // Moved 2026-10 (D-DOC-026, docs-session finding, Ruling R1: Mark delegated 2026-10-04): CsrMatrix.Builder now sums a
    // duplicated cell in insertion order; this digest recorded the introsort's arbitrary order in the node rows over
    // 16 entries. Same network and elements; only the rounding of the nodal-matrix diagonals moves. Before
    // 7909948DF1462332..., after 106FCEDC89DA5A43...; 69317 of 82041 differ, max |diff| 1.2656542480726785E-14, max relative 2.4438490876708074E-12.
    [InlineData("uk", "lumped", "106FCEDC89DA5A4312CBBA1C5B4AAEED09A8CA17A59ED9A4693D974E2F64B10D")]
    [InlineData("uk", "lines", "2E32DE669F7F29E9A05CD7DBF00AA3335CD61C3872D22490E9BD9FABAB7FF472")]
    // Moved 2026-10 (D-DOC-026, docs-session finding, Ruling R1: Mark delegated 2026-10-04): CsrMatrix.Builder now sums a
    // duplicated cell in insertion order; this digest recorded the introsort's arbitrary order in the node rows over
    // 16 entries. Same network and elements; only the rounding of the nodal-matrix diagonals moves. Before
    // 13CDA28E8C45C6CC..., after 772541A290AD5184...; 69626 of 82041 differ, max |diff| 1.4210854715202004E-14, max relative 2.105197329703632E-12.
    [InlineData("uk", "fault", "772541A290AD51847760CCB007DE9D7023A89A629AF91913F29A8F44F6E886F3")]
    [InlineData("case9", "lumped", "54EE694E45D3F09472C022A9494A20A77CCF36D08E80904F29EA4D7808112ED6")]
    [InlineData("case9", "lines", "810618C2D31FE242207E5A563161EBBD26D7C79BF134C489AE359335CD06EE58")]
    [InlineData("case9", "fault", "816748BC0EF6C1D54E55107188C4768B4BBCC726F80C27E53EDA1EF10440B5EE")]
    // Moved 2026-10 (D-DOC-026, docs-session finding, Ruling R1: Mark delegated 2026-10-04): CsrMatrix.Builder now sums a
    // duplicated cell in insertion order; this digest recorded the introsort's arbitrary order in the node rows over
    // 16 entries. Same network and elements; only the rounding of the nodal-matrix diagonals moves. Before
    // A0AE80CACE01B3CD..., after 25ABA1F1E4153FD5...; 92851 of 102051 differ, max |diff| 3.2799318816501E-12 (a relative difference up to 2 only on values at rounding noise around 0).
    [InlineData("case30", "lumped", "25ABA1F1E4153FD57424A9B85E3112C1F18A9A9CA62428142400FA94632AA23E")]
    [InlineData("case30", "lines", "28D2B0D5A67553910A1B15AF7F03A68E0CB7DBA732569E1073462E2F7A3981C0")]
    // Moved 2026-10 (D-DOC-026, docs-session finding, Ruling R1: Mark delegated 2026-10-04): CsrMatrix.Builder now sums a
    // duplicated cell in insertion order; this digest recorded the introsort's arbitrary order in the node rows over
    // 16 entries. Same network and elements; only the rounding of the nodal-matrix diagonals moves. Before
    // 51537BEDE1C8D0F4..., after D264882A7724ABC3...; 92099 of 102051 differ, max |diff| 2.5867086250741522E-12 (a relative difference up to 2 only on values at rounding noise around 0).
    [InlineData("case30", "fault", "D264882A7724ABC3076B87D21251110CF70BA999A26C0CA7239348850391D98A")]
    [InlineData("case33bw", "lumped", "82DB6CB9E427E1D238F133F7633A2873EEED88EC76D71EE43D20E020D3AB7CDB")]
    public void Untapped_case_emt_trajectory_is_bit_identical_to_the_pre_R078_engine(string caseName, string mode, string expected)
    {
        string actual = TrajectoryDigest(Load(caseName), mode);
        Assert.True(expected == actual, $"{caseName} [{mode}]: EMT trajectory digest {actual} differs from the pinned pre-R078 digest {expected}.");
    }
}
