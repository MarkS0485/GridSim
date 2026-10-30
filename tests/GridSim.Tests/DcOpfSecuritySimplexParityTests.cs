using System.Security.Cryptography;
using System.Text;
using GridSim.Core.Analysis;
using GridSim.Core.IO.Json;
using GridSim.Core.IO.Matpower;
using GridSim.Core.Model;
using GridSim.Core.Numerics;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// R005 (October 2026 review): the security-constrained DC-OPF no longer builds a dense LP. Its LP is solved by
/// <see cref="DcOpfSecuritySimplex"/>, which performs the dense <see cref="BoundedSimplex"/>'s arithmetic without
/// the dense matrices. These tests pin that the answer is unchanged BIT FOR BIT:
///   1. against the original dense implementation, kept callable as <c>DcOptimalPowerFlow.SolveDenseReference</c>,
///      on every bundled case it can solve quickly;
///   2. against results recorded from the original code (main 3617255, version 1.95.060) before the change,
///      including case1354pegase and gb-synthetic, whose dense solves take 73 s and 397 s;
///   3. at the LP level, against <see cref="BoundedSimplex.Solve"/> on hundreds of randomised, deliberately
///      degenerate LPs of the DC-OPF's shape (tied costs, shared columns, zero sensitivities, fixed variables,
///      negated rows, infeasible right-hand sides);
///   4. the storage class <see cref="DcOpfHybridColumns"/> against a dense matrix under random row operations.
/// The only difference ever allowed is the SIGN OF A ZERO (+0 versus -0) in the LP-level and storage-level
/// checks, where the structured code skips operations of the form x - f * 0 that can flip only that sign; the
/// dispatch-level checks compare every bit, zero signs included.
/// </summary>
public class DcOpfSecuritySimplexParityTests
{
    private static string DataDir() => Path.Combine(PresentationConstantsAntiDriftTests.RepoRoot(), "data");

    private static GridModel Load(string rel)
    {
        string path = Path.Combine(DataDir(), rel.Replace('/', Path.DirectorySeparatorChar));
        if (rel.EndsWith(".m", StringComparison.OrdinalIgnoreCase))
        {
            Assert.True(File.Exists(path), $"bundled case file missing: {path}");
            // Exactly the CLI's load path (Gated.Matpower): parse, then repair raw engineering units.
            return MatpowerUnitNormalizer.Normalize(MatpowerParser.ParseFile(path));
        }
        Assert.True(Directory.Exists(path), $"bundled case directory missing: {path}");
        return JsonGridLoader.LoadDirectory(path);
    }

    private static long Bits(double v) => BitConverter.DoubleToInt64Bits(v);

    private static string Sha256(string s) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s)));

    private static string DispatchHash(DcOptimalPowerFlow.DcOpfResult r)
        => Sha256(string.Join(",", r.Dispatch.Select(d => Bits(d.Pg).ToString("X16"))));

    private static string BindingHash(DcOptimalPowerFlow.DcOpfResult r)
        => Sha256(string.Join(";", r.BindingLines.Select(b => $"{b.Branch}:{Bits(b.FlowMw):X16}")));

    // ---------------------------------------------------------------------------------------------------------
    // 1. Structured solver versus the dense reference, in process, every bit.
    // ---------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("matpower/lib/case9.m")]
    [InlineData("matpower/lib/case24_ieee_rts.m")]
    [InlineData("matpower/lib/case30.m")]
    [InlineData("matpower/lib/case145.m")]
    [InlineData("matpower/rts/pglib_opf_case73_ieee_rts.m")]
    [InlineData("matpower/nesta/pglib_opf_case3_lmbd.m")]
    [InlineData("matpower/nesta/pglib_opf_case5_pjm.m")]
    [InlineData("matpower/nesta/pglib_opf_case30_as.m")]
    [InlineData("matpower/nesta/pglib_opf_case162_ieee_dtc.m")]
    [InlineData("matpower/nesta/pglib_opf_case240_pserc.m")]
    [InlineData("matpower/nesta/pglib_opf_case588_sdet.m")]
    [InlineData("matpower/dist/case533mt_hi.m")]
    [InlineData("matpower/benchmarks/cigre_mv.m")]
    [InlineData("intl/ireland-all-island")]
    public void Structured_security_dispatch_is_bit_identical_to_the_dense_reference(string rel)
    {
        GridModel m = Load(rel);
        DcOptimalPowerFlow.DcOpfResult dense = DcOptimalPowerFlow.SolveDenseReference(m, enforceLineLimits: true);
        DcOptimalPowerFlow.DcOpfResult fast = DcOptimalPowerFlow.Solve(m, enforceLineLimits: true);

        Assert.True(dense.SecurityLpStatus.HasValue, $"{rel}: the security LP did not run"); // every case listed runs it
        Assert.Equal(dense.SecurityLpStatus, fast.SecurityLpStatus);
        Assert.Equal(dense.Feasible, fast.Feasible);
        Assert.Equal(dense.NetworkConstrained, fast.NetworkConstrained);
        Assert.Equal(Bits(dense.TotalCost), Bits(fast.TotalCost));
        Assert.Equal(Bits(dense.TotalGenMw), Bits(fast.TotalGenMw));
        Assert.Equal(Bits(dense.TotalLoadMw), Bits(fast.TotalLoadMw));
        Assert.Equal(dense.Dispatch.Count, fast.Dispatch.Count);
        for (int i = 0; i < dense.Dispatch.Count; i++)
        {
            Assert.Equal(dense.Dispatch[i].GenBus, fast.Dispatch[i].GenBus);
            Assert.True(Bits(dense.Dispatch[i].Pg) == Bits(fast.Dispatch[i].Pg),
                $"{rel} generator {i}: dense {dense.Dispatch[i].Pg:R}, structured {fast.Dispatch[i].Pg:R}");
            Assert.Equal(Bits(dense.Dispatch[i].CostPerMwh), Bits(fast.Dispatch[i].CostPerMwh));
        }
        Assert.Equal(dense.BindingLines.Count, fast.BindingLines.Count);
        for (int k = 0; k < dense.BindingLines.Count; k++)
        {
            Assert.Equal(dense.BindingLines[k].Branch, fast.BindingLines[k].Branch);
            Assert.Equal(Bits(dense.BindingLines[k].FlowMw), Bits(fast.BindingLines[k].FlowMw));
            Assert.Equal(Bits(dense.BindingLines[k].RateMw), Bits(fast.BindingLines[k].RateMw));
        }
    }

    // ---------------------------------------------------------------------------------------------------------
    // 2. Structured solver versus results RECORDED from the original dense code (main 3617255, 1.95.060), so the
    //    pin holds even though the reference is no longer what Solve runs. Hashes are SHA-256 over the dispatch
    //    bit patterns ("X16", comma-separated, generator order) and over "branch:flowBits" of the binding lines
    //    (semicolon-separated, in reported order).
    // ---------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("matpower/lib/case9.m", "Optimal", true, false, "40CEC30000000000", "4073B00000000000", 3, 0,
        "A5F99B94454CFECB3F06DE9CD197715E583A25C8D408F4D064A0E6A1484570D8", "E3B0C44298FC1C149AFBF4C8996FB92427AE41E4649B934CA495991B7852B855")]
    [InlineData("matpower/lib/case24_ieee_rts.m", "Optimal", true, true, "4101652000000000", "40A6440000000001", 33, 1,
        "4584CA3159627BDCB67967405B4E19C5234C1FA9F21B30B344DBE10783ED4A09", "6AF0F781BA0C8D34C787B5B321E34D5EFB43512DDCF5DE8724CFD175BD389A44")]
    [InlineData("matpower/lib/case30.m", "Optimal", true, false, "40C279FFFFFFFFFF", "4067A66666666665", 6, 0,
        "4D52D51556E87A44C892D1BDEE9545241DC5570BEF422970EB512C75D18C9AE8", "E3B0C44298FC1C149AFBF4C8996FB92427AE41E4649B934CA495991B7852B855")]
    // Moved 2026-10 (D-DOC-026, docs-session finding, Ruling R1: Mark delegated 2026-10-04): CsrMatrix.Builder now sums a duplicated
    // cell in insertion order (the PTDF matrix's diagonals were summed in the introsort's arbitrary order), so the PTDFs move in
    // their last bits and the LP result with them.
    // costBits and genBits unchanged; 3 of 50 units move, by at most 1.2483951650210656E-09 MW; same 3 binding lines, flows
    // within 5.491074261954054E-11 MW. dispatchHash 75F630A4... -> 89DE3748...; bindingHash 7550D2AF... -> 29BF1C9D...
    [InlineData("matpower/lib/case145.m", "Optimal", true, true, "416AFE6DB0000000", "411146AC9999999A", 50, 3,
        "89DE3748C931896E756257A4981B3FE2AA5DA0664DC281BFC7B80344D9D3BDE9", "29BF1C9DA929ACCFD8FD268D05AFF1B78E94E04D7EDCABC3DDE0151899178DB5")]
    [InlineData("matpower/rts/pglib_opf_case73_ieee_rts.m", "Optimal", true, true, "411A17B000000000", "40C0B30000000000", 99, 2,
        "5CEDBAF86CEABB6AEFB1D9D1F5761AC9540B413FEFDECAA8EC980972B1B916BF", "907B359C5A5D8D6F8B38F60D09631A774959A0AC13F761CA8E10A8CC8F6CF769")]
    [InlineData("matpower/nesta/pglib_opf_case3_lmbd.m", "Optimal", true, false, "40CEC30000000000", "4073B00000000000", 3, 0,
        "50AE1EADFE31FA104AD694E56F57A511C526F189A95D30A693B56A681D5517D7", "E3B0C44298FC1C149AFBF4C8996FB92427AE41E4649B934CA495991B7852B855")]
    [InlineData("matpower/nesta/pglib_opf_case5_pjm.m", "Optimal", true, false, "40E86A0000000000", "408F400000000000", 5, 0,
        "8BEB6985283406BC367707D28B61EE909696A3F08A88A7EFF4054557762D8A7E", "E3B0C44298FC1C149AFBF4C8996FB92427AE41E4649B934CA495991B7852B855")]
    [InlineData("matpower/nesta/pglib_opf_case30_as.m", "Optimal", true, true, "40CBAD0000000000", "4071B66666666666", 6, 1,
        "8ECF3ED079DADE076303DFDB49303F1B4DBFA94C859DC365534496EF3EDC16A9", "A47B3DFE9ADEBC44E8E742ACBB567E1A5F83C98B9713A584CE14235F763C1683")]
    // Moved 2026-10 (D-DOC-026, docs-session finding, Ruling R1: Mark delegated 2026-10-04): CsrMatrix.Builder now sums a duplicated
    // cell in insertion order (the PTDF matrix's diagonals were summed in the introsort's arbitrary order), so the PTDFs move in
    // their last bits and the LP result with them.
    // costBits "4116178400000001" (361953.00000000006) -> "4116178400000000" (361953); genBits "40BC470F5C28F5C3" (7239.06)
    // -> "40BC470F5C28F5C2" (7239.0599999999995); 8 of 12 units move, by at most 1.5575096767861396E-11 MW; same 7 binding
    // lines, flows within 3.865352482534945E-12 MW. dispatchHash AB7F2B72... -> 3D8F8F0F...; bindingHash A8974D24... -> C83E4DA6...
    [InlineData("matpower/nesta/pglib_opf_case162_ieee_dtc.m", "Optimal", true, true, "4116178400000000", "40BC470F5C28F5C2", 12, 7,
        "3D8F8F0F5B8BA0BC5F2842D1E11CED3AE57DF4269EC8C96DB58970B727E3AF0D", "C83E4DA69F3DAD0500208CFF5253E5B09975B68875E29C196ED3EEDB0B23F0E2")]
    // Moved 2026-10 (D-DOC-026, docs-session finding, Ruling R1: Mark delegated 2026-10-04): CsrMatrix.Builder now sums a duplicated
    // cell in insertion order (the PTDF matrix's diagonals were summed in the introsort's arbitrary order), so the PTDFs move in
    // their last bits and the LP result with them.
    // costBits "415B80069A3D70A5" (7208986.410000001) -> "415B80069A3D70A4" (7208986.41); genBits unchanged; 11 of 143 units
    // move, by at most 6.575646693818271E-10 MW; same 12 binding lines, flows within 8.776623872108757E-11 MW. dispatchHash
    // E4E7D2FE... -> F44B3728...; bindingHash 1D8F5D43... -> 55F84CE7...
    [InlineData("matpower/nesta/pglib_opf_case240_pserc.m", "Optimal", true, true, "415B80069A3D70A4", "4101999DD35A8589", 143, 12,
        "F44B37283BC7400A558965900EA31970AB8D238F7E9DF351378E752BB0C33829", "55F84CE76ACD52D11DBC055755E209E78BAEEEBA24041AB32AD4BA769F63E89A")]
    // Moved 2026-10 (D-DOC-026, docs-session finding, Ruling R1: Mark delegated 2026-10-04): CsrMatrix.Builder now sums a duplicated
    // cell in insertion order (the PTDF matrix's diagonals were summed in the introsort's arbitrary order), so the PTDFs move in
    // their last bits and the LP result with them.
    // genBits "40C4D28E147AE143" (10661.109999999991) -> "40C4D28E147AE144" (10661.109999999993); costBits unchanged
    // "4120447EFFFFFFFC" (533055.4999999995). The LP is degenerate here: the optimum is not unique, and 44.060364160726806 MW
    // moves between two generators of equal cost (gen 64, bus 366: 175.33036416072667 -> 131.27; gen 70, bus 414: 287.44 ->
    // 331.5003641607268) at the same total cost bits; 7 other units move by less than 6.4e-12 MW. Same 7 binding lines, their
    // flows within 4.177991286269389E-12 MW. dispatchHash 7D2EAD03... -> F48099EF...; bindingHash D3728F5E... -> 26413F0C...
    // Moved 2026-10 (N240, docs-session finding DOC-6 NF-1, Ruling R1: Mark delegated 2026-10-04; main ruled a fixed,
    // documented tie-break in the DC-OPF simplex): the row above records one vertex of a DEGENERATE LP (every unit costs
    // 50 GBP/MWh, so a whole face of dispatches is optimal) chosen by the last bits of PTDF sums; PTDF entries scaled by
    // random factors within 1e-13 moved it by up to 251.7 MW in 17 of 20 trials (N240DcOpfTieBreakTests). With the
    // lowest-index pricing tie-break (BoundedSimplex.DcOpfPricingTieMargin, 1e-10 relative; 3 pivots decided by it) the
    // simplex stops at another optimal vertex, which the same trials move by at most 2.2e-10 MW. Before -> after: costBits
    // "4120447EFFFFFFFC" (533055.4999999995) -> "4120447EFFFFFFFD" (533055.4999999997, the same optimal cost to one unit in
    // the last place); genBits "40C4D28E147AE144" (10661.109999999993) -> "40C4D28E147AE145" (10661.109999999995); binding
    // 7 -> 8 (line 389 binds at 224.1 MW); 15 of 95 units move, the largest gen 94 (bus 580) 261 -> 9.254958102566718 MW
    // (-251.74504189743328), gen 63 (bus 365) 5.13 -> 200.17279325769925, gen 57 (bus 296) 570.7940854049456 ->
    // 713.8564781392051, gen 18 (bus 75) 221.79 -> 78.08, gen 1 (bus 10) 221.79 -> 97.5, gen 70 (bus 414)
    // 331.5003641607268 -> 431.17, gen 64 (bus 366) 131.27 -> 196.9 (full list in the DOC-11 notes). dispatchHash
    // F48099EF... -> 6FD2F333...; bindingHash 26413F0C... -> FB9FE466...
    [InlineData("matpower/nesta/pglib_opf_case588_sdet.m", "Optimal", true, true, "4120447EFFFFFFFD", "40C4D28E147AE145", 95, 8,
        "6FD2F333919AE31B05C4F3BF9451FAC9429C7FD03EF33391A45F1517919FDAD9", "FB9FE466364C9FD22CA1C6EE2F949A965512324ED8BBB4FA7B48BE330D29A389")]
    [InlineData("matpower/dist/case533mt_hi.m", "Optimal", true, false, "40873D6ABBECAABC", "402DBF40F08B17FB", 1, 0,
        "6BB190107FCD0FC61AC245BCE0A587A0CFD96DFEAC4F1B0D90F0ED671D7A2098", "E3B0C44298FC1C149AFBF4C8996FB92427AE41E4649B934CA495991B7852B855")]
    [InlineData("matpower/benchmarks/cigre_mv.m", "Optimal", true, false, "40A17A370A3D70A8", "40465EFEC56D5D00", 1, 0,
        "9B5F55E6503940B0A25C2DF5F863040120C1C89E8901471CA97C694779EABECB", "E3B0C44298FC1C149AFBF4C8996FB92427AE41E4649B934CA495991B7852B855")]
    // Moved 2026-10 (N240, Ruling R1: Mark delegated 2026-10-04; main ruled a fixed, documented tie-break in the DC-OPF
    // simplex): 3 entering columns of this LP's path were decided between reduced costs equal to within round-off; the
    // lowest-index tie-break takes a different path (Phase I / II 99+18 -> 114+28 iterations) to the same optimal vertex.
    // One unit moves in its last bit: gen 20 (bus 12) 898.4450018655533 -> 898.4450018655532 (-1.1368683772161603E-13 MW).
    // costBits "40E59017E9E94629" (44160.747303616554) -> "40E59017E9E94628" (44160.74730361655); genBits "40A27DE3D6F3165E"
    // -> "40A27DE3D6F3165D" (2366.9449993099975); dispatchHash D8E7B20C... -> 95D5A84B...; no binding line either way.
    [InlineData("intl/ireland-all-island", "Optimal", true, false, "40E59017E9E94628", "40A27DE3D6F3165D", 51, 0,
        "95D5A84B6CC280ACBAEF317A1E056979B9EBC415BE483E9FA2FE58D9D27F7EF3", "E3B0C44298FC1C149AFBF4C8996FB92427AE41E4649B934CA495991B7852B855")]
    // Moved 2026-10 (D-DOC-026, docs-session finding, Ruling R1: Mark delegated 2026-10-04): CsrMatrix.Builder now sums a duplicated
    // cell in insertion order (the PTDF matrix's diagonals were summed in the introsort's arbitrary order), so the PTDFs move in
    // their last bits and the LP result with them.
    // costBits "414BDEBBC0000003" (3652983.5000000014) -> "414BDEBBC0000004" (3652983.500000002); genBits "40F1D63AB851EB88"
    // (73059.67000000004) -> "40F1D63AB851EB87" (73059.67000000003); 11 of 260 units move, by at most 2.4510882212780416E-10 MW;
    // same 10 binding lines, flows within 1.4210854715202004E-10 MW. dispatchHash 674C3B67... -> C50533FB...; bindingHash
    // C1DEB0D4... -> 54FEA9E8...
    // Moved 2026-10 (N240, Ruling R1: Mark delegated 2026-10-04; main ruled a fixed, documented tie-break in the DC-OPF
    // simplex): this LP's path met 12 near-ties in pricing, and under the strict rule PTDF entries scaled by random factors
    // within 1e-13 moved its dispatch by up to 1,333.33 MW in 20 of 20 trials (a degenerate optimum); with the tie-break the
    // same trials move it by at most 6.5e-10 MW. Here it ends at the same vertex as before to round-off: 9 of 260 units
    // move, by at most 3.410605131648481E-12 MW (gen 246, bus 8872). costBits unchanged "414BDEBBC0000004"; genBits
    // "40F1D63AB851EB87" (73059.67000000003) -> "40F1D63AB851EB88" (73059.67000000004); the same 10 binding lines (flows in
    // their last bits). dispatchHash C50533FB... -> 9DF1077E...; bindingHash 54FEA9E8... -> 7B475CA2...
    [InlineData("matpower/pegase/case1354pegase.m", "Optimal", true, true, "414BDEBBC0000004", "40F1D63AB851EB88", 260, 10,
        "9DF1077EE91F00E871DE7EE6CB3A8BA2475A445099107C12BC54B5EB80747FD3", "7B475CA20401270F899BFCB11CADE6797F03FFFA38755415F2F2912201DEEC67")]
    // Moved 2026-10 (R305 / N020, authorised by Mark's 2026-10-02 best-judgement ruling): this row priced gb-synthetic's
    // onshore / offshore wind (types "onshore", "offshore") at the 50 GBP/MWh unknown-fuel default, because the old
    // substring cost table knew wind only as "wind". With the one fuel taxonomy they cost 1, like every other wind type.
    // Only the total cost moves; the status, dispatch, generation total and binding lines are the same bits.
    // Before: costBits "4135B1D22855720B" (1421778.1575537946). After: "4120085D7EB87C99" (525358.7475012719).
    // Moved 2026-10 (N109, Ruling R1, Mark delegated 2026-10-04): gb-synthetic's LP was Infeasible only because 53 rated
    // lines cannot be relieved by any dispatch; they are now left out (UnrelievableLines) and the LP over the rest is
    // Optimal. Before: status Infeasible, feasible false, costBits "4120085D7EB87C99" (525358.7475012719, the merit-order
    // dispatch), dispatchHash D492AD03..., bindingHash 97CC7AC9... After: status Optimal, feasible true, costBits
    // "41226681E4468721" (602944.9458505848), dispatchHash 9370BCE0..., bindingHash E0CA0E9E... Unchanged: constrained
    // true, genBits "40E32F5E6241BEA7" (39290.94949424016 MW, the demand), 74 generators, 55 binding lines. Every other
    // row (each an Optimal LP before N109) is untouched and still passes bit for bit.
    // Moved 2026-10 (N204, Ruling R1 follow-up, Mark delegated 2026-10-04): the gb-synthetic builder now sizes its radial
    // ladder to its peak demand (84 radial branches become two parallel units), so 51 of the 53 left-out lines and 51 of
    // the 55 binding lines go. Before -> after: costBits "41226681E4468721" (602944.9458505848) -> "41226681E4468722"
    // (602944.9458505849, one unit in the last place: the same merit-order dispatch summed over a dispatch whose binding
    // set changed); binding 55 -> 4; dispatchHash 9370BCE0... -> 89A6C8DD...; bindingHash E0CA0E9E... -> D8B84BEF...
    // Unchanged: status Optimal, feasible true, constrained true, genBits "40E32F5E6241BEA7", 74 generators.
    // Moved 2026-10 (N240, Ruling R1: Mark delegated 2026-10-04; main ruled a fixed, documented tie-break in the DC-OPF
    // simplex): a degenerate LP (units of the same fuel cost the same); 21 pivots are decided by the lowest-index tie-break
    // and the simplex stops at another optimal vertex at the same cost bits. costBits and genBits unchanged; 6 of 74 units
    // move: gen 7 (bus 12) 1400 -> 0, gen 20 (bus 50) 0 -> 1000, gen 19 (bus 49) 415.50550535473485 -> 1000, gen 12 (bus 29)
    // 500 -> 315.50550535473417, gen 0 and gen 9 in their last bits (4.5e-13 and 1.6e-12 MW); still 4 binding lines.
    // dispatchHash 89A6C8DD... -> 21F9FCD4...; bindingHash D8B84BEF... -> 54CB6707...
    [InlineData("gb-synthetic", "Optimal", true, true, "41226681E4468722", "40E32F5E6241BEA7", 74, 4,
        "21F9FCD4FA80F7472D5CD781683E43B965BF14DDA3949B28694C94BC4A917E49", "54CB67079C69543D7D0E2B3D5A2E59B0CE70EBEFDD14103E694FB3AB7E1D37F9")]
    public void Structured_security_dispatch_reproduces_the_recorded_original_results(
        string rel, string status, bool feasible, bool constrained, string costBits, string genBits,
        int generators, int binding, string dispatchHash, string bindingHash)
    {
        DcOptimalPowerFlow.DcOpfResult r = DcOptimalPowerFlow.Solve(Load(rel), enforceLineLimits: true);
        Assert.Equal((LpStatus?)Enum.Parse<LpStatus>(status), r.SecurityLpStatus);
        Assert.Equal(feasible, r.Feasible);
        Assert.Equal(constrained, r.NetworkConstrained);
        Assert.Equal(Convert.ToInt64(costBits, 16), Bits(r.TotalCost));
        Assert.Equal(Convert.ToInt64(genBits, 16), Bits(r.TotalGenMw));
        Assert.Equal(generators, r.Dispatch.Count);
        Assert.Equal(binding, r.BindingLines.Count);
        Assert.Equal(dispatchHash, DispatchHash(r));
        Assert.Equal(bindingHash, BindingHash(r));
    }

    // ---------------------------------------------------------------------------------------------------------
    // 3. LP level: DcOpfSecuritySimplex versus BoundedSimplex on randomised degenerate LPs of the DC-OPF shape.
    // ---------------------------------------------------------------------------------------------------------

    /// <summary>The dense matrix the reference would be handed for a structured problem (the DC-OPF's layout).</summary>
    private static double[,] DenseA(DcOpfSecuritySimplex.Problem p)
    {
        int g = p.Generators, lines = p.Lines;
        var a = new double[1 + lines, g + lines];
        for (int i = 0; i < g; i++) a[0, i] = 1.0;
        for (int r = 0; r < lines; r++)
        {
            for (int i = 0; i < g; i++) a[1 + r, i] = p.Ptdf[r * p.DistinctColumns + p.GenColumn[i]];
            a[1 + r, g + r] = 1.0;
        }
        return a;
    }

    /// <summary>Equal bit for bit, except that +0 and -0 count as the same value (see the class note).</summary>
    private static void SameValue(double expected, double actual, string what)
    {
        if (expected == 0.0 && actual == 0.0) return;
        Assert.True(Bits(expected) == Bits(actual), $"{what}: expected {expected:R}, got {actual:R}");
    }

    private static DcOpfSecuritySimplex.Problem RandomProblem(Random rng)
    {
        int g = rng.Next(1, 9);
        int lines = rng.Next(0, 9);
        int u = rng.Next(1, g + 1);
        var genCol = new int[g];
        for (int i = 0; i < g; i++) genCol[i] = i < u ? i : rng.Next(u); // every column used, some shared
        // Sensitivities from a small set so that ties and exact zeros are common (degeneracy on purpose).
        double[] palette = { 0.0, 0.0, 0.25, -0.25, 0.5, -0.5, 0.125, 1.0 / 3.0, -0.75, 0.6 };
        var ptdf = new double[lines * u];
        for (int k = 0; k < ptdf.Length; k++) ptdf[k] = palette[rng.Next(palette.Length)];
        int n = g + lines;
        var c = new double[n];
        var lo = new double[n];
        var hi = new double[n];
        double[] costs = { 10.0, 10.0, 20.0, 30.0, 45.5 };
        double loSum = 0, hiSum = 0;
        for (int i = 0; i < g; i++)
        {
            c[i] = costs[rng.Next(costs.Length)];
            int kind = rng.Next(6);
            lo[i] = kind == 0 ? -rng.Next(1, 40) : kind == 1 ? rng.Next(0, 20) : 0.0; // some can absorb power
            hi[i] = kind == 2 ? lo[i] : lo[i] + rng.Next(5, 120);                     // some fixed (lo == hi)
            loSum += lo[i]; hiSum += hi[i];
        }
        var b = new double[1 + lines];
        // Demand mostly inside the dispatchable range; sometimes outside it (an infeasible LP).
        b[0] = rng.Next(10) == 0 ? hiSum + 7.0 : loSum + (hiSum - loSum) * rng.NextDouble();
        for (int r = 0; r < lines; r++)
        {
            double rate = rng.Next(5, 80);
            c[g + r] = 0.0; lo[g + r] = 0.0; hi[g + r] = 2.0 * rate;
            double f0 = rng.Next(-60, 61) * 0.5;
            b[1 + r] = rate - f0; // may be negative: the reference then negates the row
        }
        return new DcOpfSecuritySimplex.Problem(g, lines, u, genCol, ptdf, b, c, lo, hi);
    }

    [Fact]
    public void Structured_simplex_matches_the_dense_simplex_on_randomised_degenerate_lps()
    {
        var rng = new Random(20261002);
        var seen = new HashSet<LpStatus>();
        for (int trial = 0; trial < 600; trial++)
        {
            DcOpfSecuritySimplex.Problem p = RandomProblem(rng);
            (LpStatus ds, double[] dx, double _) = BoundedSimplex.Solve(DenseA(p), p.B, p.C, p.Lo, p.Hi);
            (LpStatus ss, double[] sx, DcOpfSecuritySimplex.Diagnostics _) = DcOpfSecuritySimplex.Solve(p);
            Assert.True(ds == ss, $"trial {trial}: dense {ds}, structured {ss}");
            Assert.Equal(dx.Length, sx.Length);
            for (int j = 0; j < dx.Length; j++) SameValue(dx[j], sx[j], $"trial {trial} x[{j}]");
            seen.Add(ds);
        }
        // The randomised set must exercise both the optimal and the infeasible outcome.
        Assert.Contains(LpStatus.Optimal, seen);
        Assert.Contains(LpStatus.Infeasible, seen);
    }

    [Fact]
    public void Structured_simplex_matches_the_dense_simplex_through_many_refactorisations()
    {
        // Bigger random LPs (71 to 120 rows) so a phase runs past the 64-iteration refactorisation interval (each
        // row's artificial has to be pivoted out) and exercises the structured Gauss-Jordan rebuild, its row swaps
        // and its fill-in.
        var rng = new Random(5005);
        int refactorised = 0;
        for (int trial = 0; trial < 25; trial++)
        {
            int g = rng.Next(20, 50);
            int lines = rng.Next(70, 120);
            int u = rng.Next(5, g + 1);
            var genCol = new int[g];
            for (int i = 0; i < g; i++) genCol[i] = i < u ? i : rng.Next(u);
            var ptdf = new double[lines * u];
            for (int k = 0; k < ptdf.Length; k++) ptdf[k] = rng.Next(4) == 0 ? 0.0 : Math.Round(rng.NextDouble() * 2.0 - 1.0, 3);
            int n = g + lines;
            var c = new double[n]; var lo = new double[n]; var hi = new double[n];
            double hiSum = 0;
            for (int i = 0; i < g; i++) { c[i] = 5.0 * rng.Next(1, 6); lo[i] = 0.0; hi[i] = rng.Next(20, 200); hiSum += hi[i]; }
            var b = new double[1 + lines];
            b[0] = 0.6 * hiSum;
            for (int r = 0; r < lines; r++)
            {
                double rate = rng.Next(30, 300);
                lo[g + r] = 0.0; hi[g + r] = 2.0 * rate;
                b[1 + r] = rate - rng.Next(-100, 101);
            }
            var p = new DcOpfSecuritySimplex.Problem(g, lines, u, genCol, ptdf, b, c, lo, hi);
            (LpStatus ds, double[] dx, double _) = BoundedSimplex.Solve(DenseA(p), p.B, p.C, p.Lo, p.Hi);
            (LpStatus ss, double[] sx, DcOpfSecuritySimplex.Diagnostics diag) = DcOpfSecuritySimplex.Solve(p);
            Assert.True(ds == ss, $"trial {trial}: dense {ds}, structured {ss}");
            for (int j = 0; j < dx.Length; j++) SameValue(dx[j], sx[j], $"trial {trial} x[{j}]");
            if (diag.Refactorisations > 0) refactorised++;
        }
        Assert.True(refactorised > 0, "no trial reached a refactorisation, so the rebuild path was not exercised");
    }

    [Fact]
    public void Structured_simplex_rejects_an_inconsistent_problem()
    {
        var bad = new DcOpfSecuritySimplex.Problem(2, 1, 1, new[] { 0, 1 }, new[] { 0.5 }, new[] { 1.0, 1.0 },
            new double[3], new double[3], new double[3]);
        Assert.Throws<ArgumentException>(() => DcOpfSecuritySimplex.Solve(bad)); // GenColumn 1 outside 0..0
        var shortB = new DcOpfSecuritySimplex.Problem(1, 1, 1, new[] { 0 }, new[] { 0.5 }, new[] { 1.0 },
            new double[2], new double[2], new double[2]);
        Assert.Throws<ArgumentException>(() => DcOpfSecuritySimplex.Solve(shortB)); // B needs 1 + Lines entries
    }

    // ---------------------------------------------------------------------------------------------------------
    // 4. The storage class against a dense matrix under the reference's row operations.
    // ---------------------------------------------------------------------------------------------------------

    [Fact]
    public void Hybrid_columns_track_a_dense_matrix_through_swaps_scalings_and_eliminations()
    {
        var rng = new Random(77);
        for (int trial = 0; trial < 200; trial++)
        {
            int m = rng.Next(2, 9);
            int cols = rng.Next(1, 2 * m + 1);
            var dense = new double[m, cols];
            var hybrid = new DcOpfHybridColumns(m, cols);
            for (int j = 0; j < cols; j++)
            {
                if (rng.Next(2) == 0)
                {
                    int row = rng.Next(m);
                    double single = rng.Next(1, 9) * (rng.Next(2) == 0 ? 1.0 : -0.5);
                    dense[row, j] = single;
                    hybrid.SetSingle(j, row, single);
                }
                else
                {
                    var col = new double[m];
                    for (int i = 0; i < m; i++) { col[i] = rng.Next(3) == 0 ? 0.0 : rng.NextDouble() - 0.5; dense[i, j] = col[i]; }
                    hybrid.SetDense(j, col);
                }
            }
            for (int op = 0; op < 12; op++)
            {
                int kind = rng.Next(3);
                if (kind == 0)
                {
                    int a = rng.Next(m), b = rng.Next(m);
                    for (int j = 0; j < cols; j++) (dense[a, j], dense[b, j]) = (dense[b, j], dense[a, j]);
                    hybrid.SwapRows(a, b);
                }
                else if (kind == 1)
                {
                    int row = rng.Next(m);
                    double d = rng.NextDouble() + 0.25;
                    for (int j = 0; j < cols; j++) dense[row, j] /= d;
                    hybrid.DivideRow(row, d);
                }
                else
                {
                    int piv = rng.Next(m);
                    var f = new double[m];
                    var rows = new List<int>();
                    for (int r = 0; r < m; r++)
                        if (r != piv && rng.Next(2) == 0) { f[r] = rng.NextDouble() - 0.5; if (f[r] != 0.0) rows.Add(r); }
                    // The reference: for r != piv with f != 0, m[r, j] -= f * m[piv, j] for every j.
                    for (int r = 0; r < m; r++)
                    {
                        if (r == piv || f[r] == 0.0) continue;
                        for (int j = 0; j < cols; j++) dense[r, j] -= f[r] * dense[piv, j];
                    }
                    hybrid.Eliminate(piv, f, rows, denseF: rng.Next(2) == 0);
                }
                for (int i = 0; i < m; i++)
                    for (int j = 0; j < cols; j++)
                        SameValue(dense[i, j], hybrid.Entry(i, j), $"trial {trial} op {op} entry ({i},{j})");
            }

            // MultiplyVector and WeightedColumnSum against the reference's sums.
            var v = new double[cols];
            for (int k = 0; k < cols; k++) v[k] = rng.Next(3) == 0 ? 0.0 : rng.NextDouble();
            var got = new double[m];
            hybrid.MultiplyVector(v, got);
            for (int i = 0; i < m; i++)
            {
                double sum = 0;
                for (int k = 0; k < cols; k++) sum += dense[i, k] * v[k];
                SameValue(sum, got[i], $"trial {trial} product row {i}");
            }
            var w = new double[m];
            var wRows = new List<int>();
            for (int i = 0; i < m; i++) { w[i] = rng.Next(2) == 0 ? 0.0 : rng.NextDouble(); if (w[i] != 0.0) wRows.Add(i); }
            for (int j = 0; j < cols; j++)
            {
                double sum = 0;
                for (int i = 0; i < m; i++) sum += w[i] * dense[i, j];
                SameValue(sum, hybrid.WeightedColumnSum(j, w, wRows), $"trial {trial} weighted column {j}");
            }
        }
    }

    [Fact]
    public void Hybrid_columns_fail_loud_on_misuse()
    {
        var h = new DcOpfHybridColumns(3, 2);
        h.SetSingle(0, 1, 2.0);
        Assert.Throws<InvalidOperationException>(() => h.SetSingle(0, 2, 1.0));          // column already set
        Assert.Throws<ArgumentException>(() => h.SetDense(1, new double[2]));             // wrong length
        h.SetDense(1, new double[3]);
        Assert.Throws<InvalidOperationException>(() => h.Eliminate(1, new[] { 0.0, 1.0, 0.0 }, new List<int>(), false)); // f at pivot row
        Assert.Throws<ArgumentOutOfRangeException>(() => new DcOpfHybridColumns(0, 1));
    }

    // ---------------------------------------------------------------------------------------------------------
    // 5. gb-full: the case the dense LP could not hold (about 1.9 GB of dense arrays).
    // ---------------------------------------------------------------------------------------------------------

    // Skip reason updated 2026-10 (N072; the reason text only, no assertion touched): the run takes about 136 s on the
    // i3 (N034 notes), and its outcome is Infeasible because of the data (three circuits no dispatch can secure,
    // pinned in S4SolverConvergenceTests.N072_*), so it stays on demand.
    [Fact(Skip = "R005 / N072: gb-full security DC-OPF, about 136 s on the i3; the main island's LP is Infeasible because of the data (3 circuits no dispatch can secure, see S4SolverConvergenceTests). Run on demand.")]
    public void Gb_full_security_dispatch_runs_in_bounded_memory_and_reports_a_definite_outcome()
    {
        GridModel m = Load("gb-full");
        long before = GC.GetTotalAllocatedBytes(true);
        DcOptimalPowerFlow.DcOpfResult r = DcOptimalPowerFlow.Solve(m, enforceLineLimits: true);
        long allocated = GC.GetTotalAllocatedBytes(true) - before;
        Assert.True(r.SecurityLpStatus.HasValue, "gb-full: the security LP did not run");
        // An Optimal LP must be a secure dispatch that balances; anything else must be reported not feasible.
        if (r.SecurityLpStatus == LpStatus.Optimal)
        {
            Assert.True(r.Feasible);
            Assert.True(Math.Abs(r.TotalGenMw - r.TotalLoadMw) < 1e-6 * Math.Max(1.0, r.TotalLoadMw));
            // Moved 2026-10 (N109, Ruling R1, Mark delegated 2026-10-04): lines no dispatch can relieve are left out of
            // the LP and reported in UnrelievableLines; they stay over their rating at an Optimal dispatch. Before: every
            // binding line within its rating. After: every binding line that the LP kept within its rating.
            var leftOut = r.UnrelievableLines.Select(u => u.Branch).ToHashSet();
            foreach (DcOptimalPowerFlow.LineBinding b in r.BindingLines.Where(b => !leftOut.Contains(b.Branch)))
                Assert.True(Math.Abs(b.FlowMw) <= b.RateMw + 1e-6);
        }
        else Assert.False(r.Feasible);
        // The dense reference would need about 1.9 GB live; total allocation over the whole solve stays far below.
        Assert.True(allocated < 1_500_000_000L, $"allocated {allocated} bytes");
    }
}
