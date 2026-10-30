namespace GridSim.Core.Dynamics.Emt;

/// <summary>
/// A passive EMT network element addressed by integer node ids (a bus, or <see cref="EmtNetwork.Ground"/>
/// for the reference). Elements are immutable; per-step history lives in the engine, keyed by
/// <see cref="Id"/>. Series capacitance (series compensation, a branch with X &lt; 0) is a
/// <see cref="SeriesCapacitorBranch"/> (R078); line charging and bus shunts are <see cref="ShuntElement"/>s.
/// </summary>
public abstract record EmtElement(int Id);

/// <summary>A series R-L branch between two nodes (a line's series arm, a source's internal impedance, or a
/// pure resistor when <c>L = 0</c>). Positive current is From-&gt;To. Values per-unit; L in seconds (X/w0).
/// <para><paramref name="Tap"/> (R078) is an ideal off-nominal transformer of ratio <c>Tap : 1</c> on the From
/// side, in the MATPOWER / <see cref="PowerFlow.AdmittanceStamp"/> convention: the series arm sees
/// <c>v_From / Tap</c> at its From end and the From bus carries <c>i / Tap</c>, which gives exactly the
/// power-flow stamp <c>Yff = ys/Tap^2</c>, <c>Yft = Ytf = -ys/Tap</c>, <c>Ytt = ys</c> for a real tap. The default
/// 1 is a plain line and leaves every untapped run bit-identical. A branch current probe on a tapped branch
/// reads the series-arm current <c>i</c> (the To-side current, before the To-end charging); the From-bus
/// current is <c>i / Tap</c>. The engine refuses <c>L &lt; 0</c> (use <see cref="SeriesCapacitorBranch"/>),
/// <c>R = L = 0</c> (an infinite conductance) and a non-positive or non-finite tap.</para></summary>
public sealed record SeriesBranch(int Id, int From, int To, double R, double L, double Tap = 1.0) : EmtElement(Id);

/// <summary>
/// A series R-C branch between two nodes (R078): the EMT model of a series-compensated branch, which the phasor
/// data carries as a negative series reactance <c>X &lt; 0</c>. The capacitance is <c>C = 1/(|X|*w0)</c>
/// (per-unit, in seconds), so at the system frequency its reactance is exactly <c>-1/(w0*C) = X</c>. The R and C
/// are in series inside one companion (see <see cref="EmtCompanionStamp.SeriesRc"/>), so no internal node is
/// needed. <paramref name="Tap"/> is the same From-side ideal transformer as on <see cref="SeriesBranch"/>.
/// Positive current is From-&gt;To. The engine refuses a non-positive or non-finite C or tap.
/// </summary>
public sealed record SeriesCapacitorBranch(int Id, int From, int To, double R, double C, double Tap = 1.0) : EmtElement(Id);

/// <summary>A shunt to ground at one bus, carrying any of a conductance <paramref name="G"/>, a capacitance
/// <paramref name="C"/> (line charging half-each-end, or a bus capacitor) and an inductance <paramref name="L"/>
/// (a reactor) in parallel. Each non-zero part becomes its own companion branch to ground.</summary>
public sealed record ShuntElement(int Id, int Bus, double G = 0, double C = 0, double L = 0) : EmtElement(Id);

/// <summary>A voltage source at a bus with time-varying EMF <paramref name="Emf"/>. A Thévenin source
/// (<paramref name="Rs"/> &gt; 0) is stamped as its Norton equivalent (a conductance 1/Rs plus a current
/// source Emf/Rs) and provides the network's ground reference; an ideal source (<paramref name="Rs"/> = 0)
/// makes its bus a Dirichlet node whose voltage is imposed directly.</summary>
public sealed record VoltageSource(int Id, int Bus, double Rs, Func<double, double> Emf) : EmtElement(Id);

/// <summary>An independent current source injecting <paramref name="Inj"/>(t) into a bus (returned from ground).
/// Also the seam a future converter/machine Norton model injects through.</summary>
public sealed record CurrentSource(int Id, int Bus, Func<double, double> Inj) : EmtElement(Id);

/// <summary>
/// A lossless distributed (travelling-wave / Bergeron) transmission line. Unlike a lumped pi section it models
/// finite wave-propagation speed: each end is a surge-impedance resistance (<paramref name="SurgeImpedance"/>
/// Zc) to ground plus a history current source carrying the wave that left the OTHER end one travel time
/// (<paramref name="TauSeconds"/>) ago. There is no instantaneous coupling between the ends - that is the whole
/// point of a travelling-wave model - so it stamps only a diagonal conductance at each end. Captures reflections
/// (open-end voltage doubling), matched-line absorption and propagation delay exactly.
/// </summary>
public sealed record BergeronLine(int Id, int From, int To, double SurgeImpedance, double TauSeconds) : EmtElement(Id);

/// <summary>An ideal switch between two nodes: a conductance that toggles between <paramref name="Ron"/>
/// (closed) and <paramref name="Roff"/> (open). It stays permanently in the matrix pattern (open = a tiny
/// conductance, not removed), so a switching event changes only the numeric matrix - the fill-reducing
/// ordering is computed once. A shunt fault is a switch from a bus to <see cref="EmtNetwork.Ground"/>: pass
/// <c>To = EmtNetwork.Ground</c>, <c>Ron = R_fault</c>, and close it with a <see cref="SwitchEvent"/>.</summary>
public sealed record SwitchElement(int Id, int From, int To, double Ron = EmtConstants.DefaultSwitchOnResistancePu,
    double Roff = EmtConstants.DefaultSwitchOffResistancePu, bool InitiallyClosed = true) : EmtElement(Id);
