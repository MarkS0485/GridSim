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
    // Moved 2026-10-31 (GitHub issue #1, data correction): 25 Northern Powergrid 132 kV cable rows of gb-full carried
    // line charging of 202 to 542 uS/km, above any 132 kV cable (about 160 uS/km at most); each now carries 126 uS/km
    // (C = 0.40 uF/km) times its published length, 354.6 -> 148.1 MVAr at 1 pu (data/gb-full/README.md). The network
    // changed, so its solved numbers do. The untapped-engine identity this test guards is unchanged (the same network
    // through both engines); only the network moved. Before 429D50FDF0B03D0F..., after 39376F24789CCCC4....
    [InlineData("gb-full", "lumped", "232AA652DCDF9DECF45F6920D6E8B605F75DB98E2ECE4A6F08B1F99165C8FB06")]
    // Moved 2026-10 (D-DOC-026, docs-session finding, Ruling R1: Mark delegated 2026-10-04): CsrMatrix.Builder now sums a
    // duplicated cell in insertion order; this digest recorded the introsort's arbitrary order in the node rows over
    // 16 entries. Same network and elements; only the rounding of the nodal-matrix diagonals moves. Before
    // 20E1B8BB9840BB3C..., after F963983E884043B4...; 323026 of 350175 differ, max |diff| 4.222655558550059E-11, max relative 5.758866834941773E-10.
    // Moved 2026-10-31 (GitHub issue #1, data correction): 25 Northern Powergrid 132 kV cable rows of gb-full carried
    // line charging of 202 to 542 uS/km, above any 132 kV cable (about 160 uS/km at most); each now carries 126 uS/km
    // (C = 0.40 uF/km) times its published length, 354.6 -> 148.1 MVAr at 1 pu (data/gb-full/README.md). The network
    // changed, so its solved numbers do. The untapped-engine identity this test guards is unchanged (the same network
    // through both engines); only the network moved. Before F963983E884043B4..., after 618B0F32C4A8207B....
    [InlineData("gb-full", "lines", "03334BF15CE25754061D18963A61E837F5A35489BF5654D6FAA3BB1FC965F89B")]
    // Moved 2026-10 (D-DOC-026, docs-session finding, Ruling R1: Mark delegated 2026-10-04): CsrMatrix.Builder now sums a
    // duplicated cell in insertion order; this digest recorded the introsort's arbitrary order in the node rows over
    // 16 entries. Same network and elements; only the rounding of the nodal-matrix diagonals moves. Before
    // 7C90AEDD42652249..., after 5735D48C27A4BFEF...; 339540 of 350175 differ, max |diff| 6.635980653868501E-11, max relative 2.190349190378535E-06.
    // Moved 2026-10-31 (GitHub issue #1, data correction): 25 Northern Powergrid 132 kV cable rows of gb-full carried
    // line charging of 202 to 542 uS/km, above any 132 kV cable (about 160 uS/km at most); each now carries 126 uS/km
    // (C = 0.40 uF/km) times its published length, 354.6 -> 148.1 MVAr at 1 pu (data/gb-full/README.md). The network
    // changed, so its solved numbers do. The untapped-engine identity this test guards is unchanged (the same network
    // through both engines); only the network moved. Before 5735D48C27A4BFEF..., after 36C2A9D05C1F60BF....
    [InlineData("gb-full", "fault", "1C6C93CBEF54215423360AF2975389502035FAF36357315756DD85F7A04C4809")]
    // Moved 2026-10 (D-DOC-026, docs-session finding, Ruling R1: Mark delegated 2026-10-04): CsrMatrix.Builder now sums a
    // duplicated cell in insertion order; this digest recorded the introsort's arbitrary order in the node rows over
    // 16 entries. Same network and elements; only the rounding of the nodal-matrix diagonals moves. Before
    // 7909948DF1462332..., after 106FCEDC89DA5A43...; 69317 of 82041 differ, max |diff| 1.2656542480726785E-14, max relative 2.4438490876708074E-12.
    [InlineData("uk", "lumped", "FBEB359BFF6794B76B54864CD3F2AC088D453E32BD4FC13E8CDBA27741A5829F")]
    [InlineData("uk", "lines", "5457D946469D15A8270667820F7C5214526CB546227E2550069B5FAEC514ECE8")]
    // Moved 2026-10 (D-DOC-026, docs-session finding, Ruling R1: Mark delegated 2026-10-04): CsrMatrix.Builder now sums a
    // duplicated cell in insertion order; this digest recorded the introsort's arbitrary order in the node rows over
    // 16 entries. Same network and elements; only the rounding of the nodal-matrix diagonals moves. Before
    // 13CDA28E8C45C6CC..., after 772541A290AD5184...; 69626 of 82041 differ, max |diff| 1.4210854715202004E-14, max relative 2.105197329703632E-12.
    [InlineData("uk", "fault", "A8B696F41C933A68BCCF3C8328459DA561B7D377567D81340D36E257297D818A")]
    [InlineData("case9", "lumped", "9BC6117306D21537061BE2F17A1E34B51FDC3272CD165E0FDD6038BEF4DB3204")]
    [InlineData("case9", "lines", "F32E4DA9B163A33AC51702DAE9C5B6A245B6F143A4F83070CD5F05060693BD32")]
    [InlineData("case9", "fault", "1A3944618F2367CCD4B53181C77EEF087D3E5E6DEEFE1BEF3B2D5D97D3BE84A4")]
    // Moved 2026-10 (D-DOC-026, docs-session finding, Ruling R1: Mark delegated 2026-10-04): CsrMatrix.Builder now sums a
    // duplicated cell in insertion order; this digest recorded the introsort's arbitrary order in the node rows over
    // 16 entries. Same network and elements; only the rounding of the nodal-matrix diagonals moves. Before
    // A0AE80CACE01B3CD..., after 25ABA1F1E4153FD5...; 92851 of 102051 differ, max |diff| 3.2799318816501E-12 (a relative difference up to 2 only on values at rounding noise around 0).
    [InlineData("case30", "lumped", "EA777A4FAFC6E837794FD1175C576E5FF4414D4D177A421700F80C0B29087CA1")]
    [InlineData("case30", "lines", "A9BDB0132C7CF25D181540AFBECE4F1B68D1048EA14417207B25CBB9B9A7B296")]
    // Moved 2026-10 (D-DOC-026, docs-session finding, Ruling R1: Mark delegated 2026-10-04): CsrMatrix.Builder now sums a
    // duplicated cell in insertion order; this digest recorded the introsort's arbitrary order in the node rows over
    // 16 entries. Same network and elements; only the rounding of the nodal-matrix diagonals moves. Before
    // 51537BEDE1C8D0F4..., after D264882A7724ABC3...; 92099 of 102051 differ, max |diff| 2.5867086250741522E-12 (a relative difference up to 2 only on values at rounding noise around 0).
    [InlineData("case30", "fault", "D2D0F0589C86FA6EFAEE1148478132A266AE09CCAD9E79A70CB0D56F9DB1768B")]
    [InlineData("case33bw", "lumped", "E057A46399CD52BBBB738F7FA00E93DFA4484981E221D1C66670195D10DBFA5C")]
    public void Untapped_case_emt_trajectory_is_bit_identical_to_the_pre_R078_engine(string caseName, string mode, string expected)
    {
        string actual = TrajectoryDigest(Load(caseName), mode);
        Assert.True(expected == actual, $"{caseName} [{mode}]: EMT trajectory digest {actual} differs from the pinned pre-R078 digest {expected}.");
    }
}
