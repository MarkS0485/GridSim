namespace GridSim.Cli;

/// <summary>
/// The CLI's study defaults and display thresholds, named once (R151, October 2026 review: each of these used to be
/// a bare literal in <c>Program.cs</c>, several duplicated in up to five places). No value changed when they were
/// hoisted here; a value change is a physics change and must be made, justified and versioned on its own.
/// </summary>
internal static class CliDefaults
{
    // ---- Synthetic machine data (used only when a case carries no dynamic data) -------------------------------

    /// <summary>Transient reactance X'd (pu, machine base) assumed for a machine the case gives none for. Studies that
    /// use it say so and label the result INDICATIVE. (The WSCC-9 Anderson-Fouad values live in
    /// <c>IeeeCases.Wscc9TransientReactancePu</c>.)</summary>
    public const double SyntheticXdpPu = 0.30;

    /// <summary>Typical GENROU reactance ratios off X'd for a synthesised, clearly indicative machine:
    /// Xd = 3.0 X'd, X''d = 0.8 X'd, Xq = 2.5 X'd, X'q = 1.5 X'd, X''q = 0.8 X'd.</summary>
    public const double GenrouXdOverXdp = 3.0, GenrouXdppOverXdp = 0.8, GenrouXqOverXdp = 2.5,
        GenrouXqpOverXdp = 1.5, GenrouXqppOverXdp = 0.8;

    /// <summary>Typical GENROU open-circuit time constants (s): T'do 8.0, T''do 0.03, T'qo 0.4, T''qo 0.05.</summary>
    public const double GenrouTdopS = 8.0, GenrouTdoppS = 0.03, GenrouTqopS = 0.4, GenrouTqoppS = 0.05;

    /// <summary>N345 (CF3, October 2026): mechanical damping D of a synthesised machine (none: the RMS study's damping
    /// comes from the machine model and its controls). Was the literal 0.0 in Program.cs; same value.</summary>
    public const double SyntheticMachineDamping = 0.0;

    // ---- Typical exciter and stabiliser settings (--avr / --pss on machines the case gives none for) ------------
    // N345 (CF3, October 2026): these were literals in the RmsExciter.FromIeeet1 / FromSexs and new Pss1a calls in
    // Program.cs; named here, value for value, so the v2 kind specs (exciter, pss) cite them by symbol. Typical, clearly
    // indicative settings, as the GENROU data above; no number moved.

    /// <summary>IEEET1 (--avr ieeet1, the default): regulator gain KA and time constant TA (s), exciter constant KE and
    /// time constant TE (s), rate-feedback gain KF and time constant TF (s), regulator output limits VRmin / VRmax (pu).</summary>
    public const double Ieeet1RegulatorGain = 50.0, Ieeet1RegulatorTimeS = 0.02, Ieeet1ExciterConstant = 1.0,
        Ieeet1ExciterTimeS = 0.2, Ieeet1FeedbackGain = 0.05, Ieeet1FeedbackTimeS = 1.0,
        Ieeet1RegulatorMinPu = -10.0, Ieeet1RegulatorMaxPu = 10.0;

    /// <summary>SEXS (--avr sexs): lead TA (s), lag TB (s), gain K, exciter time constant TE (s), field limits EFDmin /
    /// EFDmax (pu).</summary>
    public const double SexsLeadS = 0.1, SexsLagS = 1.0, SexsGain = 100.0, SexsExciterTimeS = 0.05,
        SexsFieldMinPu = 0.0, SexsFieldMaxPu = 8.0;

    /// <summary>PSS1A (--pss): gain KS, washout TW (s), two lead-lag stages T1 / T2 and T3 / T4 (s), output limits
    /// VSTmin / VSTmax (pu).</summary>
    public const double Pss1aGain = 20.0, Pss1aWashoutS = 10.0, Pss1aLead1S = 0.15, Pss1aLag1S = 0.05,
        Pss1aLead2S = 0.15, Pss1aLag2S = 0.05, Pss1aOutputMinPu = -0.1, Pss1aOutputMaxPu = 0.1;

    // ---- Rotor-angle stability study timing ---------------------------------------------------------------------

    /// <summary>Time (s) at which the three-phase fault is applied in every stability study.</summary>
    public const double FaultOnsetS = GridSim.Core.Dynamics.Models.RmsTransientStability.CctDefaultOnsetSeconds;   // 0.1 s, the Core default

    /// <summary>RMS (subtransient) single-clearing study: integration step (s) and total simulated time (s).</summary>
    public const double RmsStepS = 0.005, RmsDurationS = 3.0;

    /// <summary>Classical single-clearing study: RK4 step (s) and time simulated after the clearing instant (s).</summary>
    public const double ClassicalStepS = 0.002, ClassicalPostClearS = 5.0;

    /// <summary>
    /// N266 (LF5, October 2026; Ruling R1): the longest fixed clearing time (ms) <c>stability --clear</c> accepts, on both
    /// engines: the CCT search window (<see cref="GridSim.Core.Dynamics.Distributed.ClassicalInit.CctDefaultMaxSeconds"/>,
    /// 1.0 s, the RMS search's too), which also covers the slowest clearance GB plant coordinates with (back-up protection
    /// within 800 ms in England and Wales, Grid Code CC.6.2.2.2.2, docs/reference/protection-tx/S160) and keeps the RMS
    /// single study's clear inside its fixed <see cref="RmsDurationS"/> run. Reasons in full at the flag's parse.
    /// </summary>
    public const double MaxClearMs = GridSim.Core.Dynamics.Distributed.ClassicalInit.CctDefaultMaxSeconds * 1000.0;

    /// <summary>Classical cross-validation export: step (s), duration (s) and the rotor-angle kick (rad) given to the
    /// lowest-inertia machine for the no-fault perturbed run.</summary>
    public const double ClassicalExportStepS = 0.001, ClassicalExportDurationS = 5.0, ClassicalExportKickRad = 0.10;

    // ---- Harmonics and converter placeholders --------------------------------------------------------------------

    /// <summary>Fundamental current (pu) of the synthetic six-pulse source the harmonic study uses without --spectra (its
    /// spectrum: the G5 Issue 5 Table B.3 six-pulse profile since N131, or the idealised 1/h with --synth-spectrum ideal).</summary>
    public const double DefaultHarmonicSourcePu = 0.5;

    /// <summary>Placeholder converter parameters for a flag-built impedance model (CuratedPlaceholder provenance):
    /// grid-forming virtual reactance (pu), PLL bandwidth (Hz) and PLL damping ratio.</summary>
    public const double PlaceholderGfmVirtualReactancePu = GridSim.Core.Dynamics.ConverterDefaults.GfmVirtualReactancePu,   // 0.20 pu
        PlaceholderPllBandwidthHz = GridSim.Core.Dynamics.ConverterDefaults.PllBandwidthHz,                              // 20 Hz (R138; was 5 Hz)
        PlaceholderPllDamping = 0.707;

    /// <summary>Floor (pu) on the terminal voltage when converting P/Q to dq currents for the converter operating
    /// point, so a collapsed bus cannot divide by ~0.</summary>
    public const double OperatingPointVmFloorPu = 0.05;

    // ---- Display thresholds (what is printed, never what is computed) -------------------------------------------

    /// <summary>PTDF / LODF factors below this magnitude are not listed.</summary>
    public const double SensitivityDisplayFloor = 1e-4;

    /// <summary>DC-OPF units dispatched at or below this (MW) are not listed.</summary>
    public const double DcopfDispatchDisplayFloorMw = 1e-3;

    /// <summary>batchsolve: largest |difference| allowed between the batched backend and the CPU reference before
    /// the run is reported as diverged (GPU arithmetic is outside the bit-identity contract).</summary>
    public const double BatchedSolveAgreementTolerance = 1e-6;

    /// <summary>transient: print a progress line every this many slices.</summary>
    public const long TransientProgressEverySlices = 100_000;

    // ---- EMT fault switch ----------------------------------------------------------------------------------------

    /// <summary>Open-state resistance (the EMT network's own units, as the engine's default) of the switch an --event fault inserts, and the first element id used for
    /// those switches (far above any network element id).</summary>
    public const double EmtSwitchOffResistance = GridSim.Core.Dynamics.Emt.EmtConstants.DefaultSwitchOffResistancePu;   // 1e9, the EMT engine default
    public const int EmtFaultSwitchIdBase = 1_000_000;

    // ---- Live demo loop ------------------------------------------------------------------------------------------

    /// <summary>Simulated clock start (minutes after midnight: 08:00) and step (minutes) of the live loop, and the
    /// wall-clock pause per step (ms) when interactive or when driven by a fixed tick count.</summary>
    public const double LiveLoopStartMinutes = 8 * 60, LiveLoopStepMinutes = 20;
    public const int LiveLoopInteractiveSleepMs = 700, LiveLoopBatchSleepMs = 30;

    /// <summary>UK teaching-model convention: each 400 kV zone bus has a 132 kV partner whose id is the 400 kV id +
    /// 100, and transmission buses are those at or above 200 kV.</summary>
    public const int UkModel132kVBusIdOffset = 100;
    public const double UkModelTransmissionKvFloor = 200;

    // ---- Frequency studies (named only; the values belong to the deferred inertia pass) --------------------------

    /// <summary>fleet-frequency: lower bound (MW) on the demand the operating-point frequency study uses.</summary>
    public const double FleetDemandFloorMw = 1000.0;

    /// <summary>scenario / evidence default device: 30 GVA.s rotational inertia, 1.0 GW fast response.</summary>
    public const double DefaultDeviceInertiaGvaS = 30.0, DefaultDeviceFfrGw = 1.0;

    /// <summary>fleet: default total device budget (40 GVA.s inertia, 1.5 GW FFR), onset stagger (s), FFR time
    /// constant (s) and deadband (Hz).</summary>
    public const double DefaultFleetInertiaGvaS = 40.0, DefaultFleetFfrGw = 1.5, DefaultFleetStaggerS = 10.0,
        DefaultFfrTimeConstantS = 0.5, DefaultFfrDeadbandHz = 0.015;

    /// <summary>evidence: value of lost load (GBP/MWh) and the default LFDD duration (h); policy inputs tagged
    /// CuratedPlaceholder until replaced with a cited figure.</summary>
    public const double DefaultVollGbpPerMwh = 6000.0, DefaultLfddDurationH = 0.5;

    /// <summary>grade: default coordination time interval (s) between a relay and its backup: the Core default
    /// <c>OvercurrentCoordination.DefaultCoordinationTimeIntervalS</c> (0.40 s, National Grid PS(T)010 Appendix A.5
    /// and ENWL EPD350 section 4.3; S3 G01, October 2026, was an unsourced 0.30 s).</summary>
    public const double GradeCtiS = GridSim.Core.Protection.Coordination.OvercurrentCoordination.DefaultCoordinationTimeIntervalS;
}
