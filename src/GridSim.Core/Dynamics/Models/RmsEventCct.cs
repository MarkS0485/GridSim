using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;

namespace GridSim.Core.Dynamics.Models;

public static partial class RmsTransientStability
{
    /// <summary>
    /// N351 (CF5): the step-quantised critical-clearing bracket of a bolted fault at <paramref name="faultBusId"/> whose
    /// clearance also takes the <paramref name="clearingActions"/> (typically the branch trips that clear a line fault),
    /// with optional <paramref name="background"/> events at fixed times (a later reclose, a load step). Each trial with m
    /// steps runs <see cref="StudyEvents"/> on: the fault at <paramref name="onsetSeconds"/>, its clearance at
    /// onset + m x step, every clearing action re-timed to that same instant, and the background events as given, over
    /// onset + clearing + <see cref="PostClearWindowSeconds"/> (the window the plain search uses). The search is the
    /// shared one (<see cref="CctSearch"/>: integer bisection, the downward verification and the upward pocket probe), so
    /// the bracket means exactly what <see cref="CriticalClearingBracket"/>'s means; with no actions and no background
    /// events it is that search run through the event study.
    /// <para>Refused (ArgumentException): a clearing action that is a fault or a clearance (the search owns those), and
    /// every refusal of <see cref="StudyEvents"/> (named by its event); the gates of <see cref="CriticalClearingBracket"/>
    /// on the step, window, iterations and scan depth.</para>
    /// </summary>
    public static CctBracket CriticalClearingBracketEvents(
        GridModel model, PowerFlowResult result, IReadOnlyList<SubtransientInit.MachineData> data, int faultBusId,
        IReadOnlyList<RmsEvent> clearingActions, IReadOnlyList<RmsEvent>? background = null,
        double? f0 = null, double stepSeconds = CctDefaultStepSeconds,
        double maxSeconds = CctDefaultMaxSeconds, int iterations = CctDefaultBisectionIterations,
        double onsetSeconds = CctDefaultOnsetSeconds, double slipLimitRad = DefaultSlipLimitRad,
        IReadOnlyList<RmsExciter?>? exciters = null, int scanSteps = CctSearch.DefaultScanSteps)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(clearingActions);
        if (!(stepSeconds > 0.0) || !double.IsFinite(stepSeconds))
            throw new ArgumentOutOfRangeException(nameof(stepSeconds), $"The CCT step must be positive and finite (got {stepSeconds}).");
        if (!(maxSeconds > 0.0) || !double.IsFinite(maxSeconds))
            throw new ArgumentOutOfRangeException(nameof(maxSeconds), $"The CCT window must be positive and finite (got {maxSeconds}).");
        if (iterations < 1)
            throw new ArgumentOutOfRangeException(nameof(iterations), $"The CCT bisection needs at least one halving (got {iterations}).");
        if (scanSteps < 0)
            throw new ArgumentOutOfRangeException(nameof(scanSteps), $"The CCT scan depth must be 0 or more steps (got {scanSteps}).");
        if (!(onsetSeconds > 0.0) || !double.IsFinite(onsetSeconds))
            throw new ArgumentOutOfRangeException(nameof(onsetSeconds), $"The fault onset must be positive and finite (got {onsetSeconds}).");
        for (int i = 0; i < clearingActions.Count; i++)
        {
            RmsEvent? a = clearingActions[i] ?? throw new ArgumentException($"clearingActions[{i}] is null.", nameof(clearingActions));
            if (a is RmsBusFault or RmsFaultClear)
                throw new ArgumentException($"clearingActions[{i}] ({a.Describe()}): the search places the fault and its clearance itself; "
                    + "a clearing action is what else happens at the clearing instant (a branch trip, for example).", nameof(clearingActions));
        }
        if (background is not null)
            for (int i = 0; i < background.Count; i++)
                if (background[i] is null) throw new ArgumentException($"background[{i}] is null.", nameof(background));

        // The same step count as the plain search (and the same tolerance on the rounding of the window).
        double stepsInWindow = maxSeconds / stepSeconds + MultiMachineSubtransient.StageTimeToleranceSteps;
        if (stepsInWindow < 1.0)
            throw new ArgumentOutOfRangeException(nameof(maxSeconds), $"The CCT window {maxSeconds} s is shorter than "
                + $"one integration step ({stepSeconds} s); there is no clearing time to search.");
        if (stepsInWindow > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(maxSeconds), $"The CCT window {maxSeconds} s holds more than {int.MaxValue} steps.");
        int maxSteps = (int)Math.Floor(stepsInWindow);

        int unsettledTrials = 0;   // finding 24: trials whose converter iteration hit its cap (see CctBracket)
        bool Survives(int steps)
        {
            double clear = steps * stepSeconds;
            double at = onsetSeconds + clear;
            var events = new List<RmsEvent>(2 + clearingActions.Count + (background?.Count ?? 0))
            {
                new RmsBusFault(onsetSeconds, faultBusId),
                new RmsFaultClear(at, faultBusId),
            };
            foreach (RmsEvent a in clearingActions) events.Add(a with { AtSeconds = at });
            if (background is not null) events.AddRange(background);
            double duration = at + PostClearWindowSeconds;
            foreach (RmsEvent e in events) duration = Math.Max(duration, e.AtSeconds);
            MultiMachineSubtransient.Result trial = StudyEvents(model, result, data, events, f0, stepSeconds, duration, slipLimitRad, exciters);
            if (trial.ConverterIterationFailures > 0) unsettledTrials++;
            return trial.StayedInSynchronism;
        }

        return CctSearch.Run(Survives, maxSteps, stepSeconds, iterations, scanSteps) with { TrialsWithConverterIterationFailures = unsettledTrials };
    }
}

/// <summary>
/// N351 (CF5): the compact text form of an RMS event list, for the CLI (<c>stability --rms --events</c>). Events are
/// separated by commas; each is <c>kind:arguments@seconds</c>:
/// <list type="bullet">
/// <item><c>fault:BUS@t</c> a bolted fault at bus BUS (<see cref="RmsBusFault"/>); <c>clear:BUS@t</c> its clearance;</item>
/// <item><c>trip:K@t</c> branch K (0-based index into the case's branch list) trips; <c>reclose:K@t</c> it closes again;</item>
/// <item><c>load:BUS:P:Q@t</c> a load step of P MW and Q Mvar at bus BUS (negative to shed);</item>
/// <item><c>gentrip:G@t</c> generator G (0-based index into the case's generator list) trips.</item>
/// </list>
/// Numbers are invariant-culture. Anything else is refused (FormatException) naming the item and the expected form; the
/// physical checks (bus exists, branch in range, ...) are <see cref="RmsTransientStability.StudyEvents"/>'s.
/// </summary>
public static class RmsEventSpec
{
    /// <summary>The forms the parser accepts, for messages and help.</summary>
    public const string Forms = "fault:BUS@t, clear:BUS@t, trip:BRANCH@t, reclose:BRANCH@t, load:BUS:PMW:QMVAR@t, gentrip:GEN@t";

    /// <summary>Parse an event list (see the class note). At least one event.</summary>
    public static IReadOnlyList<RmsEvent> Parse(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) throw new FormatException($"an event list is needed ({Forms}).");
        var list = new List<RmsEvent>();
        string[] items = text.Split(',', StringSplitOptions.TrimEntries);
        for (int i = 0; i < items.Length; i++)
        {
            string item = items[i];
            int at = item.LastIndexOf('@');
            if (at <= 0 || at == item.Length - 1) throw new FormatException($"event {i + 1} '{item}' needs '@seconds' at its end ({Forms}).");
            double t = Number(item[(at + 1)..], i, item, "time");
            string[] parts = item[..at].Split(':');
            string kind = parts[0].Trim().ToLowerInvariant();
            int Need(int n)
            {
                if (parts.Length != n + 1) throw new FormatException($"event {i + 1} '{item}': '{kind}' takes {n} argument(s) ({Forms}).");
                return n;
            }
            switch (kind)
            {
                case "fault": Need(1); list.Add(new RmsBusFault(t, Int(parts[1], i, item, "bus"))); break;
                case "clear": Need(1); list.Add(new RmsFaultClear(t, Int(parts[1], i, item, "bus"))); break;
                case "trip": Need(1); list.Add(new RmsBranchTrip(t, Int(parts[1], i, item, "branch index"))); break;
                case "reclose": Need(1); list.Add(new RmsBranchReclose(t, Int(parts[1], i, item, "branch index"))); break;
                case "load": Need(3); list.Add(new RmsLoadStep(t, Int(parts[1], i, item, "bus"), Number(parts[2], i, item, "MW"), Number(parts[3], i, item, "Mvar"))); break;
                case "gentrip": Need(1); list.Add(new RmsGeneratorTrip(t, Int(parts[1], i, item, "generator index"))); break;
                default: throw new FormatException($"event {i + 1} '{item}': unknown kind '{kind}' ({Forms}).");
            }
        }
        return list;
    }

    /// <summary>Parse a comma list of branch indices (0-based), for <c>--clear-by-trip</c>; at least one, no repeats.</summary>
    public static IReadOnlyList<int> ParseIndices(string text, string what)
    {
        if (string.IsNullOrWhiteSpace(text)) throw new FormatException($"{what}: at least one index is needed.");
        var list = new List<int>();
        foreach (string s in text.Split(',', StringSplitOptions.TrimEntries))
        {
            if (!int.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out int k))
                throw new FormatException($"{what}: '{s}' is not a whole number of 0 or more.");
            if (list.Contains(k)) throw new FormatException($"{what}: index {k} is given twice.");
            list.Add(k);
        }
        return list;
    }

    private static double Number(string s, int i, string item, string what)
    {
        if (!double.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double v) || !double.IsFinite(v))
            throw new FormatException($"event {i + 1} '{item}': the {what} '{s}' is not a finite number.");
        return v;
    }

    private static int Int(string s, int i, string item, string what)
    {
        if (!int.TryParse(s.Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int v))
            throw new FormatException($"event {i + 1} '{item}': the {what} '{s}' is not a whole number.");
        return v;
    }
}
