using System.Globalization;
using System.Numerics;
using GridSim.Core.Dynamics.Models;
using GridSim.Core.Dynamics.UserModels;

namespace GridSim.Core.Dynamics.Plugins;

/// <summary>The refusal of a plug-in's non-finite output: names the model id and the time.</summary>
internal static class PluginGuard
{
    internal static double Finite(string id, double v, double t, string what)
        => double.IsFinite(v) ? v
            : throw new InvalidOperationException($"plug-in model {id}: {what} is {v.ToString("R", CultureInfo.InvariantCulture)} at t = {t.ToString("R", CultureInfo.InvariantCulture)} s; the step is refused.");
}

/// <summary>A governor plug-in as the kernel's <see cref="IRmsGovernor"/>: each call is forwarded with the same arguments, so
/// the route adds no arithmetic; every output is checked finite.</summary>
public sealed class PluginGovernor : IRmsGovernor
{
    private readonly IUserGovernor _g;
    private readonly string _id;
    private double _t;

    internal PluginGovernor(PluginModel model)
    {
        _id = model.Id;
        _g = model.New<IUserGovernor>();
    }

    /// <inheritdoc />
    public void Initialize(double mechanicalPowerPu) { _t = 0.0; _g.Initialize(mechanicalPowerPu); }

    /// <inheritdoc />
    public double Step(double speedDeviationPu, double dtSeconds)
    {
        double v = PluginGuard.Finite(_id, _g.Step(speedDeviationPu, dtSeconds), _t, "the mechanical power");
        _t += dtSeconds;
        return v;
    }
}

/// <summary>The governor data of a plug-in model: plugs into <c>SubtransientInit.MachineData.Governor</c> like coded data.</summary>
public sealed record PluginGovernorData(PluginModel PluginModel) : GovernorData
{
    /// <inheritdoc />
    public override string Model => "PLUGIN:" + PluginModel.Id;

    /// <inheritdoc />
    public override IRmsGovernor Create()
    {
        if (PluginModel.Role != UserModelRole.Governor)
            throw new ArgumentException($"{PluginModel.Id} has role {PluginModel.Definition.RoleText}, not governor.");
        return new PluginGovernor(PluginModel);
    }
}

/// <summary>An exciter plug-in as the kernel's <see cref="IRmsFieldController"/> (wrap it with <see cref="RmsExciter.FromUser"/>).
/// The equilibrium error is read from a private scratch instance, so the live states are never disturbed.</summary>
public sealed class PluginFieldController : IRmsFieldController
{
    private readonly PluginModel _model;
    private readonly IUserExciter _e;
    private IUserExciter? _scratch;
    private double _t;

    /// <summary>A controller with its own fresh instance of an exciter plug-in.</summary>
    public PluginFieldController(PluginModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        if (model.Role != UserModelRole.Exciter) throw new ArgumentException($"{model.Id} has role {model.Definition.RoleText}, not exciter.", nameof(model));
        _model = model;
        _e = model.New<IUserExciter>();
    }

    /// <inheritdoc />
    public void Initialize(double fieldVoltagePu)
    {
        if (!double.IsFinite(fieldVoltagePu))
            throw new ArgumentOutOfRangeException(nameof(fieldVoltagePu), $"Initial field {fieldVoltagePu} is not finite.");
        _t = 0.0;
        _e.Initialize(fieldVoltagePu);
    }

    /// <inheritdoc />
    public double EquilibriumErrorPu(double fieldVoltagePu)
    {
        _scratch ??= _model.New<IUserExciter>();
        return PluginGuard.Finite(_model.Id, _scratch.EquilibriumErrorPu(fieldVoltagePu), 0.0, "the equilibrium error");
    }

    /// <inheritdoc />
    public double Step(double voltageErrorPu, double speedDeviationPu, double dtSeconds)
    {
        double v = PluginGuard.Finite(_model.Id, _e.Step(voltageErrorPu, speedDeviationPu, dtSeconds), _t, "the field voltage");
        _t += dtSeconds;
        return v;
    }
}

/// <summary>A stabiliser plug-in as the kernel's <see cref="IRmsStabiliser"/> (attach it with
/// <see cref="RmsExciter.WithLibraryStabiliser"/>).</summary>
public sealed class PluginStabiliser : IRmsStabiliser
{
    private readonly IUserStabiliser _s;
    private readonly string _id;
    private double _t;

    /// <summary>A stabiliser with its own fresh instance.</summary>
    public PluginStabiliser(PluginModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        if (model.Role != UserModelRole.Stabiliser) throw new ArgumentException($"{model.Id} has role {model.Definition.RoleText}, not stabiliser.", nameof(model));
        _id = model.Id;
        _s = model.New<IUserStabiliser>();
        Kind = "PLUGIN:" + model.Id;
    }

    /// <inheritdoc />
    public string Kind { get; }

    /// <inheritdoc />
    public bool NeedsTerminalQuantities => _s.NeedsTerminalQuantities;

    /// <inheritdoc />
    public void Initialize() { _t = 0.0; _s.Initialize(); }

    /// <inheritdoc />
    public double Step(FieldControlInputs inputs, double dtSeconds)
    {
        double v = PluginGuard.Finite(_id, _s.Step(inputs.SpeedDeviationPu, inputs.TerminalVoltagePu, inputs.ElectricalPowerPu, dtSeconds), _t, "the stabiliser output");
        _t += dtSeconds;
        return v;
    }
}

/// <summary>
/// An injection or load model for the RMS network, from a plug-in or from a declarative <c>gridsim-model/1</c> document of
/// role <c>injection</c> or <c>load</c> (<see cref="DeclarativeInjection"/>, <see cref="DeclarativeLoad"/>). Both reach the
/// kernel by the same route: an <see cref="IUserInjection"/> per run behind one adapter.
/// </summary>
public sealed class UserDeviceModel
{
    private readonly PluginModel? _plugin;
    private readonly UserModel? _declared;

    private UserDeviceModel(PluginModel? plugin, UserModel? declared, string role)
    {
        _plugin = plugin;
        _declared = declared;
        Role = role;
    }

    /// <summary>A plug-in model of role Injection or Load, or (RC5 WP32, contract 2) Machine.</summary>
    public static UserDeviceModel FromPlugin(PluginModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        // RC5 WP32: a machine model (contract 2) takes the injection route too (a Norton source on its own bus).
        if (model.Role is not (UserModelRole.Injection or UserModelRole.Load or UserModelRole.Machine))
            throw new ArgumentException($"{model.Id} has role {model.Definition.RoleText}; an injection, load or machine model is needed.", nameof(model));
        return new UserDeviceModel(model, null, model.Definition.RoleText);
    }

    /// <summary>A declarative model of role <c>injection</c> or <c>load</c>.</summary>
    public static UserDeviceModel FromDeclarative(UserModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        if (model.Role is not ("injection" or "load"))
            throw new ArgumentException($"{model.Id} has role '{model.Role}'; an injection or load model is needed.", nameof(model));
        return new UserDeviceModel(null, model, model.Role);
    }

    /// <summary><c>injection</c> or <c>load</c> (RC5 WP32: or <c>machine</c>, a contract 2 plug-in).</summary>
    public string Role { get; }

    /// <summary>The model id.</summary>
    public string Id => _plugin?.Id ?? _declared!.Id;

    /// <summary>The trace name of the model: <c>PLUGIN:ID</c> or <c>USER:ID</c>.</summary>
    public string Model => (_plugin is not null ? "PLUGIN:" : "USER:") + Id;

    /// <summary>The SHA-256 of the plug-in assembly or of the declarative document.</summary>
    public string Hash => _plugin?.Hash ?? _declared!.Hash;

    /// <summary>True for a plug-in (its results are outside the verified contract).</summary>
    public bool IsPlugin => _plugin is not null;

    /// <summary>RC5 WP32: the plug-in model, or null for a declarative one.</summary>
    public PluginModel? Plugin => _plugin;

    internal IUserInjection NewInstance() => _plugin is not null
        ? (Role == "load" ? _plugin.New<IUserLoad>()
            : Role == "machine" ? new UserMachineInjection(_plugin.New<IUserMachine>(), _plugin.Id)
            : _plugin.New<IUserInjection>())
        : Role == "load" ? new DeclarativeLoad(_declared!.NewInstance()) : new DeclarativeInjection(_declared!.NewInstance());
}

/// <summary>
/// RC5 WP32: a contract 2 machine model (<see cref="IUserMachine"/>) on the injection route. The Norton admittance Y is stamped
/// once; the net current the unit exports at trial voltage v is <c>I_N - Y v</c>, with the source current I_N held across the
/// step (it is read from the machine's states after <see cref="IUserMachine.Initialise"/> and after each
/// <see cref="IUserMachine.Step"/>). The admittance must stay the same bits for the whole run, and every number the machine
/// gives must be finite; either failure stops the step, naming the model.
/// </summary>
internal sealed class UserMachineInjection(IUserMachine machine, string id) : IUserInjection
{
    private double _yr, _yi, _ir, _ii, _ip, _iq, _t;
    private bool _started;

    public (double Re, double Im) NortonAdmittance => _started ? (_yr, _yi) : machine.NortonAdmittancePu;

    public void Initialize(in InjectionStart s)
    {
        machine.Initialise(new MachineInit(s.P0, s.Q0, s.V0Re, s.V0Im, s.RatingPu));
        (_yr, _yi) = machine.NortonAdmittancePu;
        PluginGuard.Finite(id, _yr, 0.0, "the Norton admittance (real part)");
        PluginGuard.Finite(id, _yi, 0.0, "the Norton admittance (imaginary part)");
        _started = true;
        _t = 0.0;
        ReadSource();
    }

    private void ReadSource()
    {
        (double ir, double ii) = machine.NortonCurrentPu;
        _ir = PluginGuard.Finite(id, ir, _t, "the Norton current (real part)");
        _ii = PluginGuard.Finite(id, ii, _t, "the Norton current (imaginary part)");
    }

    public (double Re, double Im) Current(double vRe, double vIm)
        => (_ir - (_yr * vRe - _yi * vIm), _ii - (_yr * vIm + _yi * vRe));

    public void Commit(double vRe, double vIm)
    {
        (double ir, double ii) = Current(vRe, vIm);
        double vm = Math.Sqrt(vRe * vRe + vIm * vIm);
        _ip = vm > 0.0 ? (ir * vRe + ii * vIm) / vm : 0.0;
        _iq = vm > 0.0 ? (ir * vIm - ii * vRe) / vm : 0.0;
    }

    public void Step(double t, double vRe, double vIm, double dt)
    {
        machine.Step(dt, new MachineStepInputs(t, vRe, vIm));
        _t = t + dt;
        (double yr, double yi) = machine.NortonAdmittancePu;
        if (BitConverter.DoubleToInt64Bits(yr) != BitConverter.DoubleToInt64Bits(_yr) || BitConverter.DoubleToInt64Bits(yi) != BitConverter.DoubleToInt64Bits(_yi))
            throw new InvalidOperationException($"plug-in model {id}: the Norton admittance changed at t = {_t.ToString("R", CultureInfo.InvariantCulture)} s; "
                + "a machine model's admittance is constant over the run (carry any change in the Norton current).");
        PluginGuard.Finite(id, machine.SpeedPu, _t, "the speed");
        PluginGuard.Finite(id, machine.AngleRad, _t, "the rotor angle");
        PluginGuard.Finite(id, machine.FieldCurrentPu, _t, "the field current");
        PluginGuard.Finite(id, machine.ElectricalPowerPu, _t, "the electrical power");
        ReadSource();
    }

    public double ActiveCurrent => _ip;

    public double ReactiveCurrent => _iq;

    public bool InDip => false;
}

/// <summary>
/// RC5 WP32: a contract 2 relay plug-in (<see cref="IUserRelay"/>) bound to a unit by the unit's name (a generator name, or a RAW
/// machine's key bus:id, as unit protection binds). Run by <see cref="RmsTransientStability.StudyEventsWithUserRelays"/>: each
/// run gets a fresh instance, evaluated at every step start from that step's solve, and a trip goes through the built-in unit
/// relays' trip path. Its record names the device <c>PLUGIN:ID</c> and carries the plug-in provenance with
/// <see cref="PluginContract.OutsideLabel"/>.
/// <para>
/// RC5 wp32b: the relay may also be a declarative <c>gridsim-model/1</c> document of role <c>relay</c>
/// (<see cref="DeclarativeRelay"/>; device <c>USER:ID</c>, provenance without the label: it is inside the verified block
/// catalogue), and either kind may be bound to a branch (<see cref="ForBranch(int, int, PluginModel, string)"/>) instead of a
/// unit: it then reads one end of the branch and its trip opens the branch.
/// </para>
/// </summary>
public sealed class UserRelayBinding
{
    private readonly UserModel? _declared;

    /// <summary>Bind <paramref name="model"/> (role relay) to the unit named <paramref name="unit"/>.</summary>
    public UserRelayBinding(string unit, PluginModel model)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(unit);
        ArgumentNullException.ThrowIfNull(model);
        if (model.Role != UserModelRole.Relay)
            throw new ArgumentException($"{model.Id} has role {model.Definition.RoleText}; a relay model is needed.", nameof(model));
        Unit = unit;
        Model = model;
    }

    private UserRelayBinding(string? unit, int? branch, int measuredAtBus, string? label, PluginModel? plugin, UserModel? declared)
    {
        Unit = unit;
        BranchIndex = branch;
        MeasuredAtBus = measuredAtBus;
        BranchLabel = label;
        Model = plugin;
        _declared = declared;
    }

    /// <summary>RC5 wp32b: bind a declarative relay (<paramref name="model"/>, role <c>relay</c>) to the unit named
    /// <paramref name="unit"/>.</summary>
    public static UserRelayBinding ForUnit(string unit, UserModel model)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(unit);
        RequireDeclaredRelay(model);
        return new UserRelayBinding(unit, null, 0, null, null, model);
    }

    /// <summary>RC5 wp32b: bind a relay plug-in to branch <paramref name="branchIndex"/> (its index in <c>model.Branches</c>),
    /// measured at its end at bus <paramref name="measuredAtBus"/> (one of its two buses). <paramref name="label"/> names the
    /// branch in the records (for example its native identity); null gives "branch &lt;index&gt;".</summary>
    public static UserRelayBinding ForBranch(int branchIndex, int measuredAtBus, PluginModel model, string? label = null)
    {
        ArgumentNullException.ThrowIfNull(model);
        if (model.Role != UserModelRole.Relay)
            throw new ArgumentException($"{model.Id} has role {model.Definition.RoleText}; a relay model is needed.", nameof(model));
        ArgumentOutOfRangeException.ThrowIfNegative(branchIndex);
        return new UserRelayBinding(null, branchIndex, measuredAtBus, label, model, null);
    }

    /// <summary>RC5 wp32b: bind a declarative relay to branch <paramref name="branchIndex"/>, measured at bus
    /// <paramref name="measuredAtBus"/>. A branch has no machine, so a document that reads <c>f</c>, <c>dfdt</c> or
    /// <c>speed</c> is refused here, naming the port.</summary>
    public static UserRelayBinding ForBranch(int branchIndex, int measuredAtBus, UserModel model, string? label = null)
    {
        RequireDeclaredRelay(model);
        ArgumentOutOfRangeException.ThrowIfNegative(branchIndex);
        foreach (string port in DeclarativeRelay.MachinePorts)
            if (model.Plan.ReadsInput(port))
                throw new ArgumentException($"{model.Id} reads the input '{port}', which a branch relay does not have (a branch has no machine); bind it to a unit, or do not read '{port}'.", nameof(model));
        return new UserRelayBinding(null, branchIndex, measuredAtBus, label, null, model);
    }

    private static void RequireDeclaredRelay(UserModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        if (model.Role != DeclarativeRelay.Role)
            throw new ArgumentException($"{model.Id} has role '{model.Role}'; a relay model is needed.", nameof(model));
    }

    /// <summary>The unit's name; null for a branch relay (RC5 wp32b).</summary>
    public string? Unit { get; }

    /// <summary>RC5 wp32b: the branch index in <c>model.Branches</c>; null for a unit relay.</summary>
    public int? BranchIndex { get; }

    /// <summary>RC5 wp32b: the bus at the measuring end of a branch relay (0 for a unit relay).</summary>
    public int MeasuredAtBus { get; }

    /// <summary>RC5 wp32b: the branch's name in the records, or null for "branch &lt;index&gt;".</summary>
    public string? BranchLabel { get; }

    /// <summary>The bound relay plug-in; null for a declarative relay (RC5 wp32b).</summary>
    public PluginModel? Model { get; }

    /// <summary>RC5 wp32b: the bound declarative relay; null for a plug-in.</summary>
    public UserModel? Declared => _declared;

    /// <summary>RC5 wp32b: true for a plug-in (its results are outside the verified contract).</summary>
    public bool IsPlugin => Model is not null;

    /// <summary>The model id.</summary>
    public string Id => Model?.Id ?? _declared!.Id;

    /// <summary>The SHA-256 of the plug-in assembly or of the declarative document.</summary>
    public string Hash => Model?.Hash ?? _declared!.Hash;

    /// <summary>The device name in the relay records: <c>PLUGIN:ID</c> (RC5 wp32b: <c>USER:ID</c> for a declarative relay).</summary>
    public string Device => (Model is not null ? "PLUGIN:" : "USER:") + Id;

    /// <summary>RC5 wp32b: the provenance in the relay records: the plug-in's (with <see cref="PluginContract.OutsideLabel"/>), or
    /// a declarative relay's id, document hash and compiled order digest (without it).</summary>
    public string ProvenanceNote => Model is { } p ? p.ProvenanceNote : DeclaredNote(_declared!);

    private static string DeclaredNote(UserModel d)
        => $"user model {d.Id} (relay) sha256:{d.Hash} order-digest:{d.Plan.OrderDigest}; parameters: "
           + string.Join(", ", d.NewInstance().ParameterValues.Select(kv => kv.Key + "=" + kv.Value.ToString("R", CultureInfo.InvariantCulture)));

    internal IUserRelay NewInstance() => Model is not null ? Model.New<IUserRelay>() : new DeclarativeRelay(_declared!.NewInstance());
}

/// <summary>
/// The initialised form of an injection or load model at one bus: the start it reproduces, its Norton admittance, and a
/// factory of fresh run states. The start is checked when the plan is made: the model's current at V0 must give P0 + jQ0
/// within <see cref="StartTolerancePu"/> (1 + |S0|), or it is refused (a start that is not an equilibrium of the network).
/// </summary>
internal sealed class UserDevicePlan : DynamicLoadPlan
{
    /// <summary>The start check's relative tolerance (system pu).</summary>
    internal const double StartTolerancePu = 1e-10;

    private readonly UserDeviceModel _model;
    private readonly InjectionStart _start;
    private readonly bool _load;
    private readonly Complex _y;

    internal UserDevicePlan(UserDeviceModel model, InjectionStart start, string name)
    {
        _model = model;
        _start = start;
        _load = model.Role == "load";
        IUserInjection probe = model.NewInstance();
        probe.Initialize(start);
        (double yr, double yi) = probe.NortonAdmittance;
        if (!double.IsFinite(yr) || !double.IsFinite(yi))
            throw new ArgumentException($"{model.Model} '{name}': the Norton admittance is not finite.");
        _y = new Complex(yr, yi);
        (double ir, double ii) = probe.Current(start.V0Re, start.V0Im);
        if (!double.IsFinite(ir) || !double.IsFinite(ii))
            throw new ArgumentException($"{model.Model} '{name}': the current at the start voltage is not finite.");
        // S = V conj(I): the power the model exports (injection) or draws (load) at the start.
        double sp = start.V0Re * ir + start.V0Im * ii, sq = start.V0Im * ir - start.V0Re * ii;
        double scale = 1.0 + Math.Sqrt(start.P0 * start.P0 + start.Q0 * start.Q0);
        if (!(Math.Abs(sp - start.P0) <= StartTolerancePu * scale) || !(Math.Abs(sq - start.Q0) <= StartTolerancePu * scale))
            throw new ArgumentException($"{model.Model} '{name}': at the solved voltage the model gives {ControlBlocks.Num(sp)} + j{ControlBlocks.Num(sq)} pu, "
                + $"not the power-flow {ControlBlocks.Num(start.P0)} + j{ControlBlocks.Num(start.Q0)} pu (tolerance {ControlBlocks.Num(StartTolerancePu * scale)}); its Initialize must reproduce the start.");
    }

    internal override Complex NortonAdmittance => _y;

    internal override IRmsConverterModel CreateModel()
    {
        IUserInjection m = _model.NewInstance();
        m.Initialize(_start);
        return new UserDeviceState(m, _y, _load, _model.Id);
    }
}

/// <summary>
/// The kernel side of an injection or load model (<see cref="IRmsConverterModel"/>): its Norton admittance Y is in the
/// network, and it injects <c>I(v) + Y v</c> (injection) or <c>Y v - I(v)</c> (load), so the network sees exactly the model's
/// current. The trace currents are taken from the terminal current, as for the coded dynamic loads.
/// </summary>
internal sealed class UserDeviceState(IUserInjection model, Complex y, bool load, string id) : IRmsConverterModel
{
    private double _ip, _iq, _t;

    public Complex TrialCurrent(Complex v)
    {
        (double re, double im) = model.Current(v.Real, v.Imaginary);
        PluginGuard.Finite(id, re, _t, "the current (real part)");
        PluginGuard.Finite(id, im, _t, "the current (imaginary part)");
        var c = new Complex(re, im);
        if (y == Complex.Zero) return load ? -c : c;
        return load ? y * v - c : c + y * v;
    }

    public Complex Commit(Complex v)
    {
        Complex inj = TrialCurrent(v);
        model.Commit(v.Real, v.Imaginary);
        Complex terminal = TerminalCurrent(inj, v);
        double mag = v.Magnitude;
        if (mag > 0.0)
        {
            Complex rel = terminal * Complex.Conjugate(v / mag);
            _ip = rel.Real;
            _iq = -rel.Imaginary;
        }
        else { _ip = 0.0; _iq = 0.0; }
        return inj;
    }

    public Complex TerminalCurrent(Complex injection, Complex v) => y == Complex.Zero ? injection : injection - y * v;

    public void Step(double t, Complex v, Complex[] nodeVoltages, double dt)
    {
        _t = t;
        model.Step(t, v.Real, v.Imaginary, dt);
    }

    public double ActiveCurrent => _ip;

    public double ReactiveCurrent => _iq;

    public bool InDip => model.InDip;
}

/// <summary>
/// RC5 WP12: a bus load driven by a user model (a plug-in of role Load, or a declarative document of role <c>load</c>). It
/// joins <see cref="RmsLoadedMachineData.Loads"/> like any dynamic load and takes <see cref="DynamicLoadData.PMw"/> +
/// j<see cref="DynamicLoadData.QMvar"/> of the bus load. Unlike the coded loads it may take 0 MW (a purely reactive load),
/// but not 0 MVA.
/// </summary>
public sealed record UserDynamicLoadData(int BusId, double PMw, double QMvar, UserDeviceModel Device) : DynamicLoadData(BusId, PMw, QMvar)
{
    /// <inheritdoc />
    public override string Model => Device.Model;

    internal override DynamicLoadPlan Plan(Complex v0, double baseMva, double nominalHz)
    {
        if (Device.Role != "load") throw new ArgumentException($"{Label}: the model {Device.Id} has role {Device.Role}, not load.");
        double rating = new Complex(PMw, QMvar).Magnitude / baseMva;
        return new UserDevicePlan(Device, new InjectionStart(PMw / baseMva, QMvar / baseMva, v0.Real, v0.Imaginary, rating), Label);
    }
}

/// <summary>
/// The replay check run when a plug-in model is bound (<see cref="PluginModel"/>). Each instance is stepped 200 times at the
/// given step, its input rising by 0.01 pu at step 100, from one of two operating points of the role (point A: Pm0 = 0.5;
/// Efd0 = 1 with its own equilibrium error; a stabiliser at Vt = 1, Pe = 0.5; an injection at 0.5 pu, a load at 0.5 + j0.1 pu.
/// Point B: Pm0 = 0.6; Efd0 = 1.2; Vt = 1.02, Pe = 0.6; an injection at 0.4 + j0.05 pu, a load at 0.4 + j0.08 pu; all at
/// V = 1). First one instance runs alone from A, then one alone from B; then two fresh instances run side by side, one from A
/// and one from B, initialised one after the other and stepped in turn. Each paired output must equal its solo run's output
/// bit for bit, every output must be finite, and an injection's or load's current asked twice at the same voltage must give
/// the same bits. A model that fails is refused as "not deterministic (replay differs at step k)" (k counted from 1).
/// Because the paired instances start at different points, static state shared between instances that changes an output
/// within the 200 steps is caught: a value one instance writes (in Initialize or a step) and the other reads, or a counter
/// both advance. So are clocks and randomness that reach an output. It cannot prove determinism: sharing that changes no
/// output in these 200 steps from these two points passes. A relay (RC5 WP32, wp32b) is replayed twice: with a unit's inputs,
/// and with a branch's (no frequency, rate of change or speed: NaN), since it may be bound to either.
/// </summary>
public static class PluginReplay
{
    /// <summary>Steps in the replay.</summary>
    public const int Steps = 200;

    /// <summary>One instance of the model under replay, driven from one of the two points.</summary>
    private abstract class Probe
    {
        public abstract void Initialize(int point);

        /// <summary>Step k: its outputs (two at most), in a fixed order; the second is 0 for single-output roles.</summary>
        public abstract (double First, double Second) Step(int k, double dt, Action<double, double, int> same);
    }

    private static double U(int k) => k < Steps / 2 ? 0.0 : 0.01;

    private sealed class GovernorProbe(IUserGovernor g) : Probe
    {
        public override void Initialize(int point) => g.Initialize(point == 0 ? 0.5 : 0.6);

        public override (double, double) Step(int k, double dt, Action<double, double, int> same) => (g.Step(U(k), dt), 0.0);
    }

    private sealed class ExciterProbe(IUserExciter x) : Probe
    {
        private double _e;

        public override void Initialize(int point)
        {
            double efd = point == 0 ? 1.0 : 1.2;
            _e = x.EquilibriumErrorPu(efd);
            x.Initialize(efd);
        }

        public override (double, double) Step(int k, double dt, Action<double, double, int> same) => (x.Step(_e + U(k), 0.0, dt), k == 0 ? _e : 0.0);
    }

    private sealed class StabiliserProbe(IUserStabiliser p) : Probe
    {
        private double _vt, _pe;

        public override void Initialize(int point)
        {
            (_vt, _pe) = point == 0 ? (1.0, 0.5) : (1.02, 0.6);
            p.Initialize();
        }

        public override (double, double) Step(int k, double dt, Action<double, double, int> same) => (p.Step(U(k), _vt, _pe, dt), 0.0);
    }

    private sealed class InjectionProbe(IUserInjection m, bool load) : Probe
    {
        public override void Initialize(int point)
            => m.Initialize(point == 0 ? new InjectionStart(0.5, load ? 0.1 : 0.0, 1.0, 0.0, 1.0) : new InjectionStart(0.4, load ? 0.08 : 0.05, 1.0, 0.0, 1.0));

        public override (double, double) Step(int k, double dt, Action<double, double, int> same)
        {
            double vr = 1.0 - U(k), t = k * dt;
            (double ir, double ii) = m.Current(vr, 0.0);
            (double ir2, double ii2) = m.Current(vr, 0.0);
            same(ir, ir2, k);
            same(ii, ii2, k);
            m.Commit(vr, 0.0);
            m.Step(t, vr, 0.0, dt);
            return (ir, ii);
        }
    }

    /// <summary>RC5 WP32: a machine from point A (P0 = 0.5, Q0 = 0) or B (P0 = 0.4, Q0 = 0.05), at V = 1 then 0.99 from step
    /// 100; the outputs are the Norton current, read twice (the same bits), and every other output must be finite.</summary>
    private sealed class MachineProbe(IUserMachine m) : Probe
    {
        public override void Initialize(int point)
            => m.Initialise(point == 0 ? new MachineInit(0.5, 0.0, 1.0, 0.0, 1.0) : new MachineInit(0.4, 0.05, 1.0, 0.0, 1.0));

        public override (double First, double Second) Step(int k, double dt, Action<double, double, int> same)
        {
            (double ir, double ii) = m.NortonCurrentPu;
            (double ir2, double ii2) = m.NortonCurrentPu;
            same(ir, ir2, k);
            same(ii, ii2, k);
            (double yr, double yi) = m.NortonAdmittancePu;
            same(yr, yr, k);
            same(yi, yi, k);
            double w = m.SpeedPu, a = m.AngleRad, f = m.FieldCurrentPu, pe = m.ElectricalPowerPu;
            same(w, w, k);
            same(a, a, k);
            same(f, f, k);
            same(pe, pe, k);
            m.Step(dt, new MachineStepInputs(k * dt, 1.0 - U(k), 0.0));
            return (ir, ii);
        }
    }

    /// <summary>RC5 WP32: a relay at P = 0.5 (A) or 0.6 (B) pu, V = 1 and F0 = 50 Hz, then from step 100 V = 0.7 and the speed
    /// 0.01 pu below; the outputs are the trip flag and the reason code. With <paramref name="branch"/> it is given what a relay
    /// bound to a branch is given (RC5 wp32b): no frequency, rate of change or speed (NaN), the rest as for a unit.</summary>
    private sealed class RelayProbe(IUserRelay r, bool branch) : Probe
    {
        private double _p;

        public override void Initialize(int point)
        {
            _p = point == 0 ? 0.5 : 0.6;
            r.Initialise();
        }

        public override (double First, double Second) Step(int k, double dt, Action<double, double, int> same)
        {
            double u = U(k);
            RelayDecision d = branch
                ? r.Evaluate(new RelayInputs(k, k * dt, dt, 1.0 - 30.0 * u, double.NaN, double.NaN, _p, 0.0, double.NaN, _p + 20.0 * u))
                : r.Evaluate(new RelayInputs(k, k * dt, dt, 1.0 - 30.0 * u, 50.0 * (1.0 - u), 0.0, _p, 0.0, -u, _p + 20.0 * u));
            return (d.Trip ? 1.0 : 0.0, d.ReasonCode);
        }
    }

    private static Probe NewProbe(PluginModel model, bool branchInputs) => model.Role switch
    {
        UserModelRole.Machine => new MachineProbe(model.New<IUserMachine>()),
        UserModelRole.Relay => new RelayProbe(model.New<IUserRelay>(), branchInputs),
        UserModelRole.Governor => new GovernorProbe(model.New<IUserGovernor>()),
        UserModelRole.Exciter => new ExciterProbe(model.New<IUserExciter>()),
        UserModelRole.Stabiliser => new StabiliserProbe(model.New<IUserStabiliser>()),
        UserModelRole.Load => new InjectionProbe(model.New<IUserLoad>(), load: true),
        _ => new InjectionProbe(model.New<IUserInjection>(), load: false),
    };

    /// <summary>Run the check; throws <see cref="PluginRefusedException"/> on a difference.</summary>
    public static void Check(PluginModel model, double dt)
    {
        ArgumentNullException.ThrowIfNull(model);
        if (!(dt > 0.0) || !double.IsFinite(dt)) throw new ArgumentOutOfRangeException(nameof(dt), dt, "The replay step must be above 0.");
        string id = model.Id;
        void Same(double a, double b, int k)
        {
            if (!double.IsFinite(a) || !double.IsFinite(b))
                throw new PluginRefusedException($"plug-in model {id}: a non-finite output in the replay at step {(k + 1).ToString(CultureInfo.InvariantCulture)}.");
            if (BitConverter.DoubleToInt64Bits(a) != BitConverter.DoubleToInt64Bits(b))
                throw new PluginRefusedException($"plug-in model {id}: not deterministic (replay differs at step {(k + 1).ToString(CultureInfo.InvariantCulture)}).");
        }
        try
        {
            Replay(model, dt, Same, branchInputs: false);
            // RC5 wp32b: a relay may be bound to a branch, where it is given no frequency, rate or speed; it is replayed so too.
            if (model.Role == UserModelRole.Relay) Replay(model, dt, Same, branchInputs: true);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException && ex is not PluginRefusedException)
        {
            throw new PluginRefusedException($"plug-in model {id}: the replay check failed: {ex.Message}");
        }
    }

    /// <summary>One replay: the solo runs from A and from B, then the paired run, each output compared by <paramref name="same"/>.</summary>
    private static void Replay(PluginModel model, double dt, Action<double, double, int> same, bool branchInputs)
    {
        // The solo runs: one instance from A, then one from B.
        var solo = new (double, double)[2][];
        for (int point = 0; point < 2; point++)
        {
            Probe p = NewProbe(model, branchInputs);
            p.Initialize(point);
            solo[point] = new (double, double)[Steps];
            for (int k = 0; k < Steps; k++) solo[point][k] = p.Step(k, dt, same);
        }
        // The paired run: two fresh instances, from A and from B, side by side; each must repeat its solo run.
        Probe a = NewProbe(model, branchInputs), b = NewProbe(model, branchInputs);
        a.Initialize(0);
        b.Initialize(1);
        for (int k = 0; k < Steps; k++)
        {
            (double a1, double a2) = a.Step(k, dt, same);
            (double b1, double b2) = b.Step(k, dt, same);
            same(a1, solo[0][k].Item1, k);
            same(a2, solo[0][k].Item2, k);
            same(b1, solo[1][k].Item1, k);
            same(b2, solo[1][k].Item2, k);
        }
    }
}
