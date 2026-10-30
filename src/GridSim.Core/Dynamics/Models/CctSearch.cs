using System;
using System.Collections.Generic;

namespace GridSim.Core.Dynamics.Models;

/// <summary>
/// The step-grid search for a critical clearing time (CCT), shared by the RMS engine
/// (<see cref="RmsTransientStability.CriticalClearingBracket"/>) and the classical model
/// (<see cref="Distributed.ClassicalInit.CriticalClearingBracket"/>), so the two can never search differently.
///
/// <para><b>What it reports (N048, October 2026).</b> The CCT is defined as the FIRST loss of synchronism from below:
/// the largest whole number of steps m such that clearing after every step count from 0 to m holds synchronism, so
/// clearing after m + 1 steps is the first clearing time on the grid that loses it. A plain bisection finds that only
/// when stability is monotone in the clearing time (every clear shorter than a stable one is stable too). Multi-swing
/// cases break that: on WSCC-9 bus 2 (classical, CLI setup) clearing after 171 steps (342 ms) holds, 172 to 174 (344
/// to 348 ms) lose synchronism, 175 (350 ms) holds again, and 176 to 187 (the probe window) lose it (D-DOC-191: this
/// comment used to say 174 / 348 ms held, the pattern before N047; re-measured at 1.95.203 with `stability case9
/// --fault-bus 2 --clear N`, and `stability case9 --screen` reports the pocket at 350 ms); bus 8 and bus 9 have stable pockets 5, 11,
/// 13 and 14 steps above their first loss. A bisection can land on a pocket's upper edge and report a CCT above a
/// clearing time that already loses synchronism.</para>
///
/// <para><b>How (bounded cost).</b> Three stages, every trial memoised (a step count is never simulated twice):</para>
/// <list type="number">
/// <item>The unchanged integer bisection (R106 / N042): trial 0, trial M (the whole window), then halving until the
/// stable and unstable step counts are adjacent. For a monotone case this alone is the answer, exactly as before.</item>
/// <item>Downward verification: the <c>scanSteps</c> step counts just below the stable end are simulated. If one of
/// them loses synchronism, the bisection landed on a stable pocket: the search re-bisects between 0 and that lower
/// unstable step and verifies again. After <see cref="MaxRefinementRounds"/> such rounds it stops guessing and walks
/// up from step 1 to the lowest unstable step found, which gives the exact first loss (at most that many trials).</item>
/// <item>Upward probe: the <c>scanSteps</c> step counts just above the unstable end are simulated. Any that holds
/// synchronism is a stable pocket above the first loss; the CCT does not move (it is the first loss), but the result
/// says so (<see cref="CctBracket.NonMonotone"/>, <see cref="CctBracket.PocketStableSteps"/>).</item>
/// </list>
/// <para>Guarantee: the reported stable end is the first loss from below provided no stable pocket is wider than
/// <c>scanSteps</c> steps (a pocket that wide would hide the losses below it from the downward verification). Only a
/// linear scan from 0 removes that proviso, at about 170 to 380 trials per WSCC-9 bus instead of about 10.</para>
/// <para>Cost: at most <c>2 * scanSteps</c> trials above the bisection when the case is monotone near its CCT (fewer
/// when bisection trials are reused); with the default 16 steps that is 32 trials (64 ms of clearing time either side
/// at 2 ms). <see cref="CctBracket.Trials"/> reports the exact count of simulations a search ran. A scan depth of 0
/// gives the bisection alone, the pre-N048 search, trial for trial.</para>
/// </summary>
public static class CctSearch
{
    /// <summary>Default depth (whole steps) of the downward verification and of the upward pocket probe (N048): 16
    /// steps, 32 ms at the default 2 ms step. Deep enough for every stable pocket seen on WSCC-9 (the widest gap from
    /// a first loss to a pocket above it is 14 steps, bus 9).</summary>
    public const int DefaultScanSteps = 16;

    /// <summary>How many times the downward verification may send the search back to re-bisect below a newly found
    /// loss before it falls back to an exact upward walk from step 1 (N048).</summary>
    public const int MaxRefinementRounds = 4;

    /// <summary>
    /// Run the search over whole step counts 0 .. <paramref name="maxSteps"/>. <paramref name="survives"/> simulates a
    /// fault cleared after the given number of steps and returns whether synchronism held; it must be deterministic.
    /// <paramref name="iterations"/> caps each bisection (as before); <paramref name="scanSteps"/> is the verification
    /// and probe depth (0 turns both off).
    /// </summary>
    public static CctBracket Run(Func<int, bool> survives, int maxSteps, double stepSeconds, int iterations, int scanSteps)
    {
        // Pedantic gates: the callers have already validated the step and the window, but this is public.
        ArgumentNullException.ThrowIfNull(survives);
        if (maxSteps < 1) throw new ArgumentOutOfRangeException(nameof(maxSteps), $"The CCT window must hold at least one step (got {maxSteps}).");
        if (iterations < 1) throw new ArgumentOutOfRangeException(nameof(iterations), $"The CCT bisection needs at least one halving (got {iterations}).");
        if (scanSteps < 0) throw new ArgumentOutOfRangeException(nameof(scanSteps), $"The CCT scan depth must be 0 or more steps (got {scanSteps}).");

        // Every trial is memoised: the verification and the probe often revisit step counts the bisection tried.
        var memo = new Dictionary<int, bool>();
        bool S(int steps)
        {
            if (!memo.TryGetValue(steps, out bool ok)) { ok = survives(steps); memo[steps] = ok; }
            return ok;
        }

        // Stage 1: the unchanged bisection (R106 / N042).
        if (!S(0)) return new CctBracket(0, 0, stepSeconds, UnstableAtZero: true) { Trials = memo.Count };
        int lo;
        int? hi;
        if (S(maxSteps)) { lo = maxSteps; hi = null; }
        else (lo, hi) = Bisect(S, 0, maxSteps, iterations);

        // Stage 2: downward verification. Step 0 is known to hold, so the scan stops at step 1.
        int? pocket = null;
        int rounds = 0;
        while (scanSteps > 0)
        {
            int lowerLoss = -1;
            for (int s = lo - 1; s >= Math.Max(1, lo - scanSteps); s--)
                if (!S(s)) { lowerLoss = s; break; }
            if (lowerLoss < 0) break;   // nothing below the stable end loses synchronism within the scan depth

            // The stable end was a pocket above an earlier loss: remember it, and search again below that loss.
            pocket = Math.Max(pocket ?? lo, lo);
            rounds++;
            if (rounds > MaxRefinementRounds)
            {
                // Stop guessing: walk up from step 1 to the lowest loss found. Every step below the first unstable
                // one met holds, so this is the exact first loss from below.
                int first = lowerLoss;
                for (int s = 1; s < lowerLoss; s++)
                    if (!S(s)) { first = s; break; }
                lo = first - 1;
                hi = first;
                break;
            }
            (lo, hi) = Bisect(S, 0, lowerLoss, iterations);
        }

        // Stage 3: upward probe for stable pockets above the first loss (the CCT itself does not move).
        if (scanSteps > 0 && hi is int h)
            for (int s = h + 1; s <= Math.Min(maxSteps, h + scanSteps); s++)
                if (S(s)) pocket = Math.Max(pocket ?? s, s);

        return new CctBracket(lo, hi, stepSeconds, UnstableAtZero: false)
        {
            PocketStableSteps = pocket,
            ScanSteps = scanSteps,
            Trials = memo.Count,
        };
    }

    /// <summary>Integer bisection between a step count <paramref name="lo"/> that holds and one <paramref name="hi"/>
    /// that does not, stopping when they are adjacent or at the <paramref name="iterations"/> cap. The midpoint is
    /// written so it cannot overflow; no floating-point midpoint is ever formed, so every CPU visits the same trials.</summary>
    private static (int Lo, int Hi) Bisect(Func<int, bool> survives, int lo, int hi, int iterations)
    {
        for (int i = 0; i < iterations && hi - lo > 1; i++)
        {
            int mid = lo + (hi - lo) / 2;
            if (survives(mid)) lo = mid; else hi = mid;
        }
        return (lo, hi);
    }
}
