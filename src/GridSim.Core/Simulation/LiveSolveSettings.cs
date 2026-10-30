using GridSim.Core.PowerFlow;

namespace GridSim.Core.Simulation;

/// <summary>
/// The tuning shared by the two per-tick solve loops, <see cref="LiveSim"/> (the animated live view) and
/// <see cref="GdaReplayLoop"/> (the GDA time-series replay) (October 2026 review, WS13 R195). Before this both
/// files carried their own copies of 1500 / 40 / 25; the copies agreed, but nothing kept them agreeing. Every
/// value is exactly the literal it replaced, so no frame moves.
/// </summary>
public static class LiveSolveSettings
{
    /// <summary>Bus count ABOVE which the loops pick the sparse Newton engine (strictly greater: a 1,500-bus case
    /// still solves dense, a 1,501-bus case sparse, because <see cref="SolverSelector"/> tests
    /// <c>BusCount &gt; sparseThreshold</c>). Above about 1500 buses a dense per-tick solve is too slow for a smooth
    /// animation.</summary>
    public const int SparseThresholdBuses = 1500;

    /// <summary>Newton iteration cap of a sparse per-tick solve (single pass, no Q-limit switching), which keeps
    /// each live solve on a large GDA network to about a second.</summary>
    public const int SparseMaxIterations = 40;

    /// <summary>OLTC outer-loop passes of a small dense per-tick solve: the steady-state solver's own default.
    /// (Sparse and radial-sweep solves run no OLTC loop.)</summary>
    public const int OltcPasses = SteadyStateSolver.DefaultMaxOltcPasses;

    // ---- LiveSim only

    /// <summary>Seed of the live view's ambient-load RNG: fixed, so a live session is reproducible run to run.</summary>
    public const int LiveSimSeed = 20260605;

    /// <summary>Minute of the day the live clock starts at (08:00).</summary>
    public const double StartMinuteOfDay = 8 * 60;

    /// <summary>Default live step (minutes of simulated time per tick).</summary>
    public const double DefaultStepMinutes = 20;

    /// <summary>System stored energy (MW.s) below which a case is treated as carrying no inertia data, and the
    /// inertia is substituted from <see cref="FallbackInertiaH"/> (never under grid-forming).</summary>
    public const double MinInertiaMwS = 100.0;

    /// <summary>Inertia constant H (s) assumed on the fleet's MVA (or the base demand) when a case carries no
    /// usable H data.</summary>
    public const double FallbackInertiaH = 4.0;

    /// <summary>Amplitude of the ambient load-imbalance jitter as a fraction of base demand, so the frequency dial
    /// moves a little even in steady state.</summary>
    public const double AmbientNoiseFraction = 0.0008;

    /// <summary>A decaying trip deficit below this (MW) is cleared to zero.</summary>
    public const double TripDeficitZeroMw = 0.5;

    // ---- GdaReplayLoop only

    /// <summary>Storage step (hours) assumed for a single-step GDA series, which has no gap to measure.</summary>
    public const double GdaSingleStepHours = 0.5;

    /// <summary>Floor (hours) on a GDA storage step, so two equal timestamps never give a zero-length step.</summary>
    public const double GdaMinStepHours = 1e-6;
}
