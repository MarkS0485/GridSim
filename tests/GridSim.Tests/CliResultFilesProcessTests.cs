using System.Globalization;
using System.Text.Json;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// F10 (October 2026): end-to-end checks of the shared result writer on the real gridsim binary.
///
/// The console output of every verb with NO flag must be byte-identical to what it was before the writer existed. The
/// fixtures in tests/fixtures/cli-stdout were captured from the CLI built at 1.95.114 (commit 8292f57, the commit this
/// work branched from; no CLI or Core change between it and the branch point), before any F10 code was written, on
/// case9 / case14 / the bundled GB reduced model / the IEEE 4-node feeder. Each is compared with the current binary's
/// stdout (line endings normalised, nothing else), so a report line that leaked into the console, or a changed number,
/// fails here.
///
/// The same commands are then run WITH --json and --csv: stdout must still equal the fixture (every writer message goes
/// to stderr), the exit code must be the same, and the files must exist and parse, with the exit code inside. That also
/// runs every verb's report code once, so a defect there (a key set twice, a table row of the wrong width) fails here.
/// </summary>
public sealed class CliResultFilesProcessTests
{
    // name | expected exit code | arguments ({FEEDER} / {MEAS} are the bundled dist3ph files).
    public static readonly TheoryData<string, int, string> Commands = new()
    {
        // Moved 2026-10 (N199, Ruling R1 follow-up, Mark delegated 2026-10-04): the built-in case9 now reads case9.m, whose
        // generators carry Pmin 10 MW (the hand-coded copy had 0). Fixture before -> after (nothing else changed; demand
        // 315 MW and cost 15,750 GBP/h unchanged, every unit at 50 GBP/MWh): dispatch "gen bus 3 270.0 MW, gen bus 2 45.0
        // MW" -> "gen bus 3 270.0 MW, gen bus 2 35.0 MW, gen bus 1 10.0 MW".
        { "dcopf-case9", 0, "dcopf case9" },
        { "dcopf-case14-nolimits", 0, "dcopf case14 --no-limits --top 5" },
        // Moved 2026-10 (N199, Ruling R1 follow-up, Mark delegated 2026-10-04): the same Pmin 10 MW. Before -> after:
        // "converged in 5 iteration(s)  losses 12.242 MW  cost 16,362 GBP/h" -> "losses 11.615 MW  cost 16,331 GBP/h";
        // dispatch "gen bus 2 57.2 MW" -> "gen bus 2 46.6 MW" plus a new row "gen bus 1 10.0 MW" (gen bus 3 270.0 MW
        // unchanged); still 5 iterations, exit 0.
        { "acopf-case9", 0, "acopf case9" },
        { "cpf-case9", 0, "cpf case9" },
        { "taps-uk", 0, "taps uk" },
        { "ptdf-case9", 0, "ptdf case9 --bus 5" },
        { "lodf-case14", 0, "lodf case14 --branch 3" },
        // Moved 2026-10 (D1, Mark's determinism rule 2026-10-04: identical bits on every CPU tier and OS): the two
        // iec60909 fixtures moved because kappa = 1.02 + 0.98 exp(-3 R/X), m and idc(tb) now use GridSim's deterministic fdlibm exp
        // instead of the Windows C runtime. Before -> after (both fixtures): kappa 1.7351642985814113 -> 1.7351642985814115, m
        // 0.03250327876465887 -> 0.0325032787646589; idc(tb) 0.07666210893263964 -> 0.07666210893263962 (three-phase) and
        // 0.060355758039414426 -> 0.06035575803941442 (line-to-line min). Ik'', ip, Ib, Ith and R/X print unchanged.
        // Moved 2026-10 (D-DOC-098, docs-session finding, Ruling R1: Mark delegated 2026-10-04): case9 is a meshed network
        // (a ring fed by three machines), where IEC 60909-0 Method B multiplies kappa by 1.15 (capped at 2.0); the fixture
        // pinned the series kappa(R/X), the defect. Before -> after (iec60909-case9, three-phase max, bus 5): "ip    =
        // 3.6032937549618294 kA  (kappa 1.7351642985814115, R/X 0.10501342238071008)" -> "ip    = 4.143787818206103 kA  (kappa
        // 1.995438943368623, R/X 0.10501342238071008, meshed network: 1.15 x kappa(R/X), Method B)". Ik'', Ib, Ik, Ith,
        // m, idc(tb) and Zth print unchanged (m keeps kappa(R/X)); exit 0 unchanged.
        // Moved 2026-10 (N233, Mark's determinism rule 2026-10-04): the fault network is now the sparse engine at every
        // size (case9 was factored on the dense complex LU), so the last digits moved. Before -> after: "Ik''  =
        // 1.4683989584292976 kA  (8.774528526143634 pu)" -> "1.4683989584292985 kA  (8.774528526143639 pu)"; "ip    =
        // 4.143787818206103 kA" -> "4.143787818206106 kA" (kappa, R/X unchanged); "Ib    = 1.4683989584292976 kA    Ik =
        // 1.4683989584292976 kA" -> both 1.4683989584292985; "Ith   = 1.4920720239184604 kA" -> "1.4920720239184613 kA"
        // (m unchanged); "idc(tb) = 0.07666210893263962 kA" -> "0.07666210893263968 kA"; "Zth   = 0.013092788564404883 +
        // j0.12467728665140522 pu" -> "0.013092788564404878 + j0.12467728665140516 pu". Exit 0 unchanged.
        // Moved 2026-10 (A1, final batch; D2 N017, Ruling R1 and Mark's go on the final-batch moves 2026-10-10):
        // case9 (WSCC) is a 60 Hz system, so the header reads 60 Hz and m / idc(tb) use f = 60 (m = m50 x 5/6 exactly, as D2
        // derived). Before -> after: "Maximum (50 Hz" -> "Maximum (60 Hz"; "Ith   = 1.4920720239184613 kA  (m 0.0325032787646589)
        // idc(tb) = 0.07666210893263968 kA" -> "Ith   = 1.4881526648788939 kA  (m 0.027086065637215752)    idc(tb) =
        // 0.03963009392407432 kA". Ik'', ip, Ib, Ik, kappa, R/X and Zth unchanged. Identical on Linux and Windows.
        { "iec60909-case9", 0, "iec60909 case9 --bus 5" },
        // Moved 2026-10 (D-DOC-098 and D-DOC-097, docs-session findings, Ruling R1: Mark delegated 2026-10-04): the same
        // meshed kappa, and the Ik line now says which IEC 60909-0 rule the steady-state current follows. Before -> after:
        // "ip    = 2.8368581173588763 kA  (kappa 1.7351642985814115, R/X 0.10501342238071008)" -> "ip    =
        // 3.2623868349627076 kA  (kappa 1.995438943368623, R/X 0.10501342238071008, meshed network: 1.15 x kappa(R/X),
        // Method B)"; "Ib    = 1.1560643644458013 kA    Ik = 1.1560643644458013 kA" gains the note "  (unbalanced fault:
        // IEC 60909-0 takes no decay, Ik = Ib = Ik'')". Every other line unchanged; exit 0 unchanged.
        // Moved 2026-10 (N233, Mark's determinism rule 2026-10-04): the same engine change. Before -> after: Ik''
        // 1.1560643644458013 kA (6.908149645337832 pu) -> 1.1560643644458017 kA (6.908149645337835 pu); ip
        // 3.2623868349627076 -> 3.262386834962709 kA; Ib = Ik 1.1560643644458013 -> 1.1560643644458017 kA; Ith
        // 1.1747020699904083 -> 1.1747020699904087 kA; idc(tb) 0.06035575803941442 -> 0.06035575803941443 kA; Zth as in
        // iec60909-case9. kappa, R/X, m and exit 0 unchanged.
        // Moved 2026-10 (A2, final batch; D2 N017, Ruling R1 and Mark's go on the final-batch moves 2026-10-10):
        // the same 60 Hz rule. Before -> after: "Minimum (50 Hz" -> "Minimum (60 Hz"; "Ith   = 1.1747020699904087 kA  (m
        // 0.0325032787646589)    idc(tb) = 0.06035575803941443 kA" -> "Ith   = 1.17161637499512 kA  (m 0.027086065637215752)
        // idc(tb) = 0.031200607356919714 kA". Every other line unchanged.
        { "iec60909-case9-ll-min", 0, "iec60909 case9 --bus 5 --type ll --objective min" },
        // Moved 2026-10 (D-DOC-158 and D-DOC-166, docs-session findings, Ruling R1: Mark delegated 2026-10-04): the relays
        // were sorted by their raw margin, whose unit differs per element, and the busbar differential carried no
        // operating time. Before -> after: the header "Zf=0.000 pu" -> "Zf=j0.000 pu" (--zf is a reactance, D-DOC-166);
        // the 87 row's time "-" -> "inst" (an instantaneous unit protection); the three 21-Z3 rows (0.80 s: branches 1-5,
        // 5-6, 2-5) move up to follow the five 21-Z2 rows (0.30 s), ahead of the fourteen 27 rows, which carry no time
        // (the operating list is now fastest first); the closing line "... - primary clearance by 87 (Differential) on
        // bus 4." -> "... - primary clearance by 87 (Differential) on bus 4, instantaneously.". The same 23 rows, each
        // byte for byte; every number unchanged; exit 0 unchanged.
        // Moved 2026-10 (N233, Mark's determinism rule 2026-10-04): the fault network is now the sparse engine. The five
        // 21-Z2 rows all operate in 0.30 s with the same margin in exact arithmetic (a far-end relay of a bolted fault sees
        // apparent Z = line Z exactly, so (zone-3 reach x Zline - Zapp) / Zline is the same for every branch); which of them comes
        // first was decided by the last bit of each margin, and those bits moved. Before -> after: the "21-Z2 | Distance |
        // branch 4-9" row moves from third to first among the 21-Z2 rows (order 4-7, 4-5, 4-9, 3-4, 2-4 -> 4-9, 4-7,
        // 4-5, 3-4, 2-4). Every row byte for byte, every printed number unchanged, exit 0 unchanged. (The tie itself is a
        // new finding in the DOC-16 notes: the order of mathematically equal margins depends on rounding.)
        // Moved 2026-10 (LF1 N270): elements with the same operating time are now ordered by row index (relay registry
        // order, then branch order), not by the rounding-dependent margin. Before -> after: the five 21-Z2 rows (0.30 s)
        // 4-9, 4-7, 4-5, 3-4, 2-4 -> 2-4, 3-4, 4-5, 4-7, 4-9; the three 21-Z3 rows (0.80 s) 1-5, 5-6, 2-5 -> 1-5, 2-5,
        // 5-6. Every row byte for byte, every printed number, the count (23) and the primary clearance (87 at bus 4)
        // unchanged; exit 0 unchanged.
        { "fault-case14", 0, "fault case14 --bus 4" },
        { "protection-check-case14", 0, "protection-check case14" },
        // Moved 2026-10 (D-DOC-158 and D-DOC-166, docs-session findings, Ruling R1: Mark delegated 2026-10-04): the same
        // reorder as fault-case14 (header "Zf=0.000 pu" -> "Zf=j0.000 pu"; 87 time "-" -> "inst"; the three 21-Z3 rows
        // move up after the 21-Z2 rows). The closing "e.g." line names the first row, 87 at bus 4, before and after:
        // unchanged. The same 23 rows; every number unchanged; exit 0 unchanged.
        // Moved 2026-10 (N233, Mark's determinism rule 2026-10-04): the same reorder of the tied 21-Z2 rows as
        // fault-case14 (branch 4-9 moves to the first 21-Z2 row). Every row byte for byte; exit 0 unchanged.
        // Moved 2026-10 (LF1 N270): the same reorder as fault-case14 (21-Z2 rows -> 2-4, 3-4, 4-5, 4-7, 4-9; 21-Z3 rows
        // -> 1-5, 2-5, 5-6). The closing "e.g." line names the first row, 87 at bus 4, before and after: unchanged.
        // Every row byte for byte; exit 0 unchanged.
        { "protection-check-case14-fault", 0, "protection-check case14 --fault-bus 4" },
        { "protection-check-case9-cascade", 0, "protection-check case9 --cascade" },
        // Moved 2026-10 (G01, authorised by Mark's 2026-10-02 best-judgement ruling; source National Grid PS(T)010
        // Issue 1 (docs/reference S158) Appendix A.5 p.11 and ENWL EPD350 Issue 6 (S165) s.4.3 p.10): the grade-chain
        // and grade-fail fixtures print the default CTI, which moved from an unsourced 0.3 s to 0.4 s. Before: "CTI
        // 0.3 s" (header, and the GRADED line of grade-chain); after: "CTI 0.4 s". Every time, margin, verdict and
        // exit code is unchanged (binding margin 0.5632 s >= 0.4 s; the failing margin 0.0650 s fails either CTI).
        { "grade-chain", 0, "grade --chain iec-si:100:0.1,iec-si:150:0.3 --current 1000,2000" },
        { "grade-fail", 3, "grade --chain iec-si:100:0.1,iec-si:150:0.1 --current 1000" },
        // Moved 2026-10 (N107, authorised by Mark's 2026-10-02 best-judgement ruling): these four contingency fixtures
        // pinned the defect itself, every non-converged outage labelled "islanded / no solution" whatever the cause.
        // Before -> after, and nothing else in the files changed:
        //   nminus1-case9, nminus2-case9, n-1-1-case9: every "ISLANDED      islanded / no solution" row and every
        //     excluded-single row "islanded / no solution" -> "islanded" (each of those outages splits the network:
        //     1-4, 3-6 and 8-2 cut off a generator bus, and any two ring branches cut the case9 ring); counts unchanged.
        //   nminus1-case14: "1-2  ISLANDED      islanded / no solution" -> "1-2  NO SOLUTION   no solution" (case14
        //     stays connected without 1-2: the N082 reactive collapse), and the count line "... 3 with violations
        //     1 islanded" -> "... 3 with violations  0 islanded  1 not solved". Exit code 3 and the verdict unchanged.
        // Re-captured 2026-10-19 (GR-125, worsened base-case violations): after the "base only" note, two lines are added,
        // "  4-7 (tx): makes base-case violation(s) worse: overvoltage 1.068 p.u." and "  7-9: makes base-case violation(s)
        // worse: overvoltage 1.067 p.u." (a bus already above the band in the base case pushed more than 0.005 pu further
        // out). Nothing else in the file changed; counts, verdict and exit code 3 unchanged.
        { "nminus1-case14", 3, "nminus1 case14" },
        // Moved 2026-10 (N199, Ruling R1 follow-up, Mark delegated 2026-10-04): the built-in case9 now carries case9.m's
        // 0.9-1.1 pu per-bus limits, which nminus1 judges by default since N092, so it reports what nminus1 case9.m
        // always did. Before -> after: outage 8-9 (min V 0.938 pu) "VIOLATIONx1" -> "secure"; "9 outages screened  4
        // secure  2 with violations  3 islanded" -> "5 secure  1 with violations  3 islanded"; "NOT SECURE, 5 insecure
        // outage(s)" -> "4 insecure outage(s)"; exit 3 unchanged. The --global-vlimits fixture (the band view) is unchanged.
        { "nminus1-case9", 3, "nminus1 case9" },
        { "nminus2-case9", 3, "nminus2 case9 --top 5" },
        { "n-1-1-case9", 3, "n-1-1 case9 --candidates 4" },
        { "domains-case14", 0, "domains case14" },
        { "domains-uk", 0, "domains uk" },
        { "dist3ph-sweep", 0, "dist3ph {FEEDER}" },
        // Moved 2026-10 (D-DOC-166, docs-session finding, Ruling R1: Mark delegated 2026-10-04): dist3ph's --zf is a fault
        // RESISTANCE while fault / protection-check read --zf as a reactance, and nothing on screen said which. Before ->
        // after: "fault at bus 3 (4.16 kV LL), Zf = 0 pu, prefault E1 = 1.0 pu, infinite source;" -> "fault at bus 3
        // (4.16 kV LL), Zf = 0 pu (resistive), prefault E1 = 1.0 pu, infinite source;". Every other line and every number
        // unchanged; exit 0 unchanged.
        { "dist3ph-fault", 0, "dist3ph {FEEDER} fault --bus 3" },
        // Moved 2026-10 (D-DOC-217, docs-session finding, Ruling R1: Mark delegated 2026-10-04): dist3ph estimate now prints
        // a per-measurement residual table after the voltages. The fixture was re-captured at the 1.95.206 batch: the old
        // 1,191-character output is an exact prefix of the new 2,378-character output; only the table is appended.
        { "dist3ph-estimate", 0, "dist3ph {FEEDER} estimate --measurements {MEAS}" },
        // Moved 2026-10 (N105, authorised by Mark's 2026-10-02 best-judgement ruling): the losses-case14 fixture pinned the
        // missing header itself (the report started at "Total losses", naming no case). The fixture gains exactly one
        // first line, "Loss allocation - IEEE 14-bus (14 buses, 20 branches)"; every other line is byte-identical to
        // the 1.95.114 capture.
        { "losses-case14", 0, "losses case14" },
        // Moved 2026-10 (D-DOC-125, D-DOC-190 and D-DOC-188, docs-session findings, Ruling R1: Mark delegated 2026-10-04):
        // the two fixtures pinned three defects in the text: the absolute 1e-4 MVA default tolerance (now the one rule,
        // BaseMva x 1e-8, which is 1e-6 on these 100 MVA cases), the law 8 detail naming "laws 1 and 6" (law 8 shares
        // its residual with law 6 only) and a law 9 detail that did not say the law was not scored. Before -> after,
        // three lines each, every residual and status byte-identical, exit 0 unchanged:
        // header "converged=True, tol 1.0E-004 MVA)" -> "converged=True, tol 1.0E-006 MVA = BaseMva 100 x 1e-8)";
        // law 8 "same residual as laws 1 and 6, reactive component included (PQ buses)" -> "same residual as law 6,
        // reactive component included at 6 PQ bus(es), by the bus types the solve ended with" (case9; 9 PQ buses on
        // case14); law 9 "steady-state snapshot at 50 Hz; synchronism is a dynamics property (see StabilityMetrics /
        // ClassicalMultiMachine)" -> "NOT SCORED: steady-state snapshot at 50 Hz with no trajectory; law 9 is scored
        // only when a dynamics caller supplies a SynchronismSample, and no verb or export does".
        // Moved 2026-10 (END-PASS CF2 rows 5 and 6, end pass; N246, stability --clear now scores law 9 from its own run,
        // Ruling R1 and Mark's go on the end-pass moves 2026-10-11): the law 9 note said no verb scores it, which N246
        // made untrue. Before -> after, fixture line 12 of both physics-check-case9.txt and physics-check-case14.txt
        // (also read by CliResultFilesFlaggedProcessTests): "..., and no verb or export does |" -> "...: stability --clear
        // (classical or --rms) scores it from its own run, no steady-state verb or export does |". Observed: the
        // 1.95.273-275 runs show the change at position 1483 (case9) / 1490 (case14), "a SynchronismSample: stability
        // --clear ("; the rest of the line is the PhysicsLaws.cs law 9 detail string that prints it (the log truncates).
        // Every other line, the residuals and exit 0 unchanged.
        { "physics-check-case9", 0, "physics-check case9" },
        { "physics-check-case14", 0, "physics-check case14" },
        { "experimental-reciprocity-case9", 0, "experimental reciprocity case9" },
        { "experimental-trace-case9", 0, "experimental trace case9" },
        { "experimental-trace-losses-case9", 0, "experimental trace case9 --losses" },
        { "experimental-carbon-case14", 0, "experimental carbon case14" },
        { "experimental-boundaries-case9", 0, "experimental boundaries case9" },
        // Moved 2026-10 (B5 R035, Ruling R1: Mark delegated EE calls 2026-10-04; source: R035 in FULL-REVIEW-2026-10.md, the
        // symmetric Jacobi solver was handed the asymmetric synchronising Laplacian; it now gets the symmetric part, and the
        // stiffness is reported in the MW/rad its name states). This fixture also carries D2's pending move (case9 is a
        // 60 Hz system since 1.95.210; at 50 Hz it printed 0.8048 / 0.805, 1.532, and D2's notes expected 0.8816 / 0.882,
        // 1.678 at the batch). Before (1.95.114 capture, 50 Hz, asymmetric) -> after (60 Hz, symmetric part): dominant
        // "0.8048 Hz" -> "0.9013 Hz" (60 Hz with the old operator: 0.8816); all modes "0.805, 1.532" -> "0.901, 1.683"
        // (old operator at 60 Hz: 0.882, 1.678); mean K "1.2 MW/rad" (the per-unit 1.165) -> "112.1 MW/rad" (symmetric
        // part, x BaseMva 100); a new line "coupling asymmetry set aside : 22.7 MW/rad (...)". COI line, mean H 24.88 s and
        // the mode-shape split (bus 2, bus 3 against bus 1) unchanged. Values derived by a double-precision replica of the
        // power flow, ClassicalInit, the Jacobi solver and Analyze (dominant 0.90133330 Hz, the closest to a printed rounding
        // edge at 1.7e-5, far above the replica's round-off); CONFIRM at the batch.
        { "experimental-wave-case9", 0, "experimental wave case9" },
        // Moved 2026-10 (A3, final batch; D2 N017, Ruling R1 and Mark's go on the final-batch moves 2026-10-10):
        // at case9's 60 Hz the COI null-mode residual is eigen-solver noise of another size, predicted by D2 to move and still far
        // below the 1e-6 tolerance. Before -> after: "residual 8.501E-008 rad/s ... min |eigenvalue| = 8.501E-008 rad/s" ->
        // "residual 2.593E-008 rad/s ... = 2.593E-008 rad/s". Same bits on Linux and Windows.
        { "experimental-modal-consistency-case9", 0, "experimental modal-consistency case9" },
        // Moved 2026-10 (N131, Ruling R1, Mark delegated 2026-10-04): the default synthetic harmonic source is the G5
        // Issue 5 Table B.3 six-pulse profile. Fixture before -> after: header "6-pulse source at bus 9" -> "6-pulse-g5
        // source at bus 9"; residual "3.469E-017 pu ... balanced to 3.47E-017 pu" -> "4.196E-017 pu ... balanced to
        // 4.20E-017 pu". Verdict [Holds] and exit 0 unchanged.
        { "experimental-harmonic-kcl-case9", 0, "experimental harmonic-kcl case9" },
        // Moved 2026-10 (A4, final batch; D2 N017, Ruling R1 and Mark's go on the final-batch moves 2026-10-10):
        // the passivity sweep is in h = f / f0, so at f0 = 60 Hz the minimum driving-point resistance moves; still positive-real.
        // Before -> after: "Re(Z) >= 7.99E-006 pu at bus 2" -> "Re(Z) >= 2.19E-005 pu at bus 2". Same bits on Linux and Windows.
        { "experimental-passivity-case9", 0, "experimental passivity case9" },
        { "dcpf-case14", 0, "dcpf case14" },
        { "dcpf-case9-top3", 0, "dcpf case9 --top 3" },
        { "freq-default", 0, "freq" },
    };

    internal static string Feeder() => Path.Combine(CliProcess.Root(), "data", "dist3ph", "ieee4-gyGy-stepdown-unbalanced.json");

    internal static string Measurements() => Path.Combine(CliProcess.Root(), "data", "dist3ph", "ieee4-gyGy-stepdown-unbalanced.measurements.json");

    internal static string[] Args(string line) =>
        line.Replace("{FEEDER}", Feeder(), StringComparison.Ordinal).Replace("{MEAS}", Measurements(), StringComparison.Ordinal)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries);

    // The fixture text, and the run's stdout with the feeder path put back to its placeholder and line endings unified.
    internal static string Fixture(string name) =>
        File.ReadAllText(Path.Combine(CliProcess.Root(), "tests", "GridSim.Tests", "fixtures", "cli-stdout", name + ".txt")).Replace("\r\n", "\n");

    internal static string Normalise(string stdout) => stdout.Replace("\r\n", "\n").Replace(Feeder(), "{FEEDER}", StringComparison.Ordinal);

    internal static string TempDir()
    {
        string d = Path.Combine(Path.GetTempPath(), "gridsim-result-files-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        return d;
    }

    [Theory]
    [MemberData(nameof(Commands))]
    public void Console_output_without_a_flag_is_byte_identical_to_before_f10(string name, int exitCode, string line)
    {
        CliProcess.Result r = CliProcess.RunBuilt(Args(line));
        Assert.True(r.ExitCode == exitCode, r.ToString());
        Assert.Equal(Fixture(name), Normalise(r.StdOut));
        Assert.DoesNotContain("result file", r.StdErr, StringComparison.Ordinal);   // the writer is silent when not asked
    }

    [Fact]
    public void Dcpf_out_csv_still_writes_the_f18_files_byte_for_byte()
    {
        // The files dcpf --out wrote before F10, captured from the 1.95.114 CLI (tests/fixtures/cli-files). The shared
        // writer must reproduce them exactly (line endings aside), and only add the islands and summary files.
        string dir = TempDir();
        try
        {
            string outCsv = Path.Combine(dir, "dcpf-case14.csv");
            CliProcess.Result r = CliProcess.RunBuilt("dcpf", "case14", "--out", outCsv);
            Assert.True(r.ExitCode == 0, r.ToString());
            Assert.Equal(Fixture("dcpf-case14"), Normalise(r.StdOut));
            string fixtures = Path.Combine(CliProcess.Root(), "tests", "GridSim.Tests", "fixtures", "cli-files");
            foreach (string f in new[] { "dcpf-case14.csv", "dcpf-case14-buses.csv" })
                Assert.Equal(File.ReadAllText(Path.Combine(fixtures, f)).Replace("\r\n", "\n"), File.ReadAllText(Path.Combine(dir, f)).Replace("\r\n", "\n"));
            Assert.True(File.Exists(Path.Combine(dir, "dcpf-case14-islands.csv")));
            Assert.True(File.Exists(Path.Combine(dir, "dcpf-case14-summary.csv")));
            Assert.Equal(4, Directory.GetFiles(dir).Length);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void Json_numbers_are_the_exact_bits_the_console_prints_in_round_trip_form()
    {
        // iec60909 prints its duties in round-trip ("R") form, so the console text itself is the exact value.
        string dir = TempDir();
        try
        {
            string json = Path.Combine(dir, "iec.json");
            CliProcess.Result r = CliProcess.RunBuilt("iec60909", "case9", "--bus", "5", "--out", json);
            Assert.True(r.ExitCode == 0, r.ToString());
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(json));
            JsonElement s = doc.RootElement.GetProperty("summary");
            string ikpp = r.StdOut.Split('\n').First(l => l.TrimStart().StartsWith("Ik''", StringComparison.Ordinal));
            string printed = ikpp.Split('=')[1].Trim().Split(' ')[0];
            double fromConsole = double.Parse(printed, NumberStyles.Float, CultureInfo.InvariantCulture);
            double fromJson = double.Parse(s.GetProperty("ikpp_ka").GetRawText(), NumberStyles.Float, CultureInfo.InvariantCulture);
            Assert.Equal(BitConverter.DoubleToInt64Bits(fromConsole), BitConverter.DoubleToInt64Bits(fromJson));
            Assert.Equal(printed, s.GetProperty("ikpp_ka").GetRawText());
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void Json_and_csv_carry_the_same_bits_and_every_row_not_just_the_printed_top()
    {
        string dir = TempDir();
        try
        {
            string json = Path.Combine(dir, "p.json"), csv = Path.Combine(dir, "p.csv");
            CliProcess.Result r = CliProcess.RunBuilt("ptdf", "case14", "--bus", "9", "--top", "2", "--json", json, "--csv", csv);
            Assert.True(r.ExitCode == 0, r.ToString());
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(json));
            JsonElement[] rows = doc.RootElement.GetProperty("tables").GetProperty("factors").EnumerateArray().ToArray();
            string[] csvLines = File.ReadAllLines(csv);
            Assert.Equal("branch,from,to,ptdf", csvLines[0]);
            Assert.Equal(20, rows.Length);                 // every branch of case14, though --top 2 printed two
            Assert.Equal(rows.Length + 1, csvLines.Length);
            for (int i = 0; i < rows.Length; i++)
            {
                string fromCsv = csvLines[i + 1].Split(',')[3];
                Assert.Equal(rows[i].GetProperty("ptdf").GetRawText(), fromCsv);
            }
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void A_run_that_stops_before_its_result_writes_no_file_and_says_so()
    {
        string dir = TempDir();
        try
        {
            string json = Path.Combine(dir, "x.json");
            // taps on a case with no tap changers is an input error (exit 1) before any schedule exists.
            CliProcess.Result r = CliProcess.RunBuilt("taps", "case14", "--json", json);
            Assert.True(r.ExitCode == 1, r.ToString());
            Assert.False(File.Exists(json));
            Assert.Contains("[taps] no result file written", r.StdErr, StringComparison.Ordinal);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void A_verdict_that_fails_is_still_written_with_its_exit_code()
    {
        string dir = TempDir();
        try
        {
            string json = Path.Combine(dir, "n1.json");
            CliProcess.Result r = CliProcess.RunBuilt("n-1", "case9", "--json", json);
            Assert.True(r.ExitCode == 3, r.ToString());
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(json));
            Assert.Equal("nminus1", doc.RootElement.GetProperty("verb").GetString());   // the alias is written canonically
            Assert.Equal(3, doc.RootElement.GetProperty("exitCode").GetInt32());
            Assert.Equal("NOT SECURE", doc.RootElement.GetProperty("summary").GetProperty("verdict").GetString());
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Theory]
    [InlineData("dcopf case9 --json r.txt")]                 // wrong extension
    [InlineData("dcopf case9 --json")]                       // no value
    [InlineData("dcopf case9 --csv a.csv --csv b.csv")]      // the same flag twice
    [InlineData("stability case9 --json r.json")]            // a verb that writes no result file
    [InlineData("convert case9 x.m --out r.json")]           // convert takes --json / --csv, not --out
    public void Bad_result_flags_are_usage_errors(string line)
    {
        CliProcess.Result r = CliProcess.RunBuilt(line.Split(' '));
        Assert.True(r.ExitCode == 2, r.ToString());
        Assert.StartsWith("error: ", r.StdErr.TrimStart(), StringComparison.Ordinal);
    }

    [Fact]
    public void Cycle_validate_and_convert_write_their_results_too()
    {
        string dir = TempDir();
        try
        {
            string cyc = Path.Combine(dir, "cycle.json");
            CliProcess.Result c = CliProcess.RunBuilt("cycle", "case9", "--cycles", "2", "--workers", "1", "--window-ms", "200", "--json", cyc);
            Assert.True(c.ExitCode == 0, c.ToString());
            using (JsonDocument doc = JsonDocument.Parse(File.ReadAllText(cyc)))
                Assert.Equal(2, doc.RootElement.GetProperty("tables").GetProperty("cycles").GetArrayLength());

            string converted = Path.Combine(dir, "case9-out.m"), conv = Path.Combine(dir, "convert.json");
            CliProcess.Result k = CliProcess.RunBuilt("convert", "case9", converted, "--json", conv);
            Assert.True(k.ExitCode == 0, k.ToString());
            using (JsonDocument doc = JsonDocument.Parse(File.ReadAllText(conv)))
                Assert.True(doc.RootElement.GetProperty("summary").GetProperty("round_trip_identical").GetBoolean());

            string valid = Path.Combine(dir, "validate.csv");
            string schemaDir = Path.Combine(CliProcess.Root(), "schemas", "v2");
            string kindFile = Path.Combine(CliProcess.Root(), "tests", "GridSim.Tests", "fixtures", "v2", "kinds", "exciter.kind.json");
            CliProcess.Result v = CliProcess.RunBuilt("validate", kindFile, "--schemas", schemaDir, "--csv", valid);
            Assert.True(v.ExitCode is 0 or 3, v.ToString());
            string[] lines = File.ReadAllLines(valid);
            Assert.Equal("path,schema_tag,valid,errors", lines[0]);
            Assert.Equal(2, lines.Length);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}

/// <summary>
/// F10: the flagged half of <see cref="CliResultFilesProcessTests"/> (the same commands with --json and --csv), in a
/// class of its own so xUnit runs the two halves in parallel.
/// </summary>
public sealed class CliResultFilesFlaggedProcessTests
{
    [Theory]
    [MemberData(nameof(CliResultFilesProcessTests.Commands), MemberType = typeof(CliResultFilesProcessTests))]
    public void With_json_and_csv_stdout_is_unchanged_and_the_files_hold_the_result(string name, int exitCode, string line)
    {
        string dir = CliResultFilesProcessTests.TempDir();
        try
        {
            string json = Path.Combine(dir, name + ".json");
            string csv = Path.Combine(dir, name + ".csv");
            CliProcess.Result r = CliProcess.RunBuilt(CliResultFilesProcessTests.Args(line).Concat(new[] { "--json", json, "--csv", csv }).ToArray());
            Assert.True(r.ExitCode == exitCode, r.ToString());
            Assert.Equal(CliResultFilesProcessTests.Fixture(name), CliResultFilesProcessTests.Normalise(r.StdOut));

            Assert.True(File.Exists(json), "no JSON written\n" + r);
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(json));
            JsonElement root = doc.RootElement;
            Assert.Equal("gridsim-cli-result/1", root.GetProperty("schema").GetString());
            Assert.Equal(exitCode, root.GetProperty("exitCode").GetInt32());
            Assert.StartsWith("gridsim ", root.GetProperty("tool").GetString(), StringComparison.Ordinal);
            Assert.True(root.GetProperty("summary").EnumerateObject().Any(), "empty summary");
            // Every table row carries every column of its table (the writer refuses ragged rows; this checks the shape).
            foreach (JsonProperty table in root.GetProperty("tables").EnumerateObject())
            {
                var columns = new HashSet<string>(StringComparer.Ordinal);
                foreach (JsonElement row in table.Value.EnumerateArray())
                {
                    var names = row.EnumerateObject().Select(p => p.Name).ToList();
                    if (columns.Count == 0) columns.UnionWith(names);
                    Assert.True(columns.SetEquals(names), $"table {table.Name} has rows of different shapes");
                }
            }

            Assert.True(File.Exists(csv), "no CSV written\n" + r);
            Assert.Contains($"wrote {json}", r.StdErr, StringComparison.Ordinal);
            Assert.Contains($"wrote {csv}", r.StdErr, StringComparison.Ordinal);
            string summaryCsv = root.GetProperty("tables").EnumerateObject().Any() ? Path.Combine(dir, name + "-summary.csv") : csv;
            Assert.Contains($"exit_code,{exitCode}", File.ReadAllText(summaryCsv), StringComparison.Ordinal);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
