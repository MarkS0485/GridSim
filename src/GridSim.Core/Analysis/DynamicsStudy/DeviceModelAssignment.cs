using System.Globalization;
using GridSim.Core.Dynamics.Plugins;

namespace GridSim.Core.Analysis.DynamicsStudy;

/// <summary>
/// RC5 WP32: one injection, machine or load model of a dynamics study (<see cref="DynamicsStudyRequest.DeviceModels"/>), with
/// no DYR record of its own. An injection model (a plug-in, or a declarative <c>gridsim-model/1</c> document of role
/// <c>injection</c>) or a machine model (a contract 2 plug-in) drives one in-service unit, named by its native identity; the
/// unit becomes converter plant on its own bus (<see cref="Dynamics.Models.SubtransientInit.MachineData.UserInjection"/>) and
/// its DYR machine, exciter and governor data are not used. A load model (a plug-in, or a declarative document of role
/// <c>load</c>) takes <see cref="PMw"/> + j<see cref="QMvar"/> of the load at <see cref="Bus"/>
/// (<see cref="UserDynamicLoadData"/>).
/// </summary>
public sealed class DeviceModelAssignment
{
    private DeviceModelAssignment(UserDeviceModel device, DynamicsMachineId? machine, int bus, double pMw, double qMvar)
    {
        Device = device;
        Machine = machine;
        Bus = bus;
        PMw = pMw;
        QMvar = qMvar;
    }

    /// <summary>An injection or machine model driving the unit <paramref name="machine"/>.</summary>
    public static DeviceModelAssignment AtMachine(DynamicsMachineId machine, UserDeviceModel device)
    {
        ArgumentNullException.ThrowIfNull(machine);
        ArgumentNullException.ThrowIfNull(device);
        if (device.Role is not ("injection" or "machine"))
            throw new ArgumentException($"{device.Model} has role {device.Role}; a unit takes an injection or machine model.", nameof(device));
        return new DeviceModelAssignment(device, machine, 0, 0.0, 0.0);
    }

    /// <summary>A load model taking <paramref name="pMw"/> + j<paramref name="qMvar"/> of the load at bus <paramref name="bus"/>.</summary>
    public static DeviceModelAssignment AtBus(int bus, double pMw, double qMvar, UserDeviceModel device)
    {
        ArgumentNullException.ThrowIfNull(device);
        if (device.Role != "load")
            throw new ArgumentException($"{device.Model} has role {device.Role}; a bus takes a load model.", nameof(device));
        return new DeviceModelAssignment(device, null, bus, pMw, qMvar);
    }

    /// <summary>The model.</summary>
    public UserDeviceModel Device { get; }

    /// <summary>The unit an injection or machine model drives; null for a load model.</summary>
    public DynamicsMachineId? Machine { get; }

    /// <summary>The bus of a load model (0 for an injection or machine model).</summary>
    public int Bus { get; }

    /// <summary>The active power a load model takes, MW.</summary>
    public double PMw { get; }

    /// <summary>The reactive power a load model takes, Mvar.</summary>
    public double QMvar { get; }

    /// <summary>Where the model is bound, in words.</summary>
    public string Where => Machine is { } m ? m.ToString()
        : $"bus {Bus.ToString(CultureInfo.InvariantCulture)} ({PMw.ToString("R", CultureInfo.InvariantCulture)} MW, {QMvar.ToString("R", CultureInfo.InvariantCulture)} Mvar)";

    /// <summary>The coverage note naming the model and where it is bound: a plug-in's provenance (with
    /// <see cref="PluginContract.OutsideLabel"/>), or a declarative model's id, role and document hash.</summary>
    public string Note => Device.Plugin is { } p
        ? p.ProvenanceNote + "; bound to " + Where + "; DYR data for it, if any, are not used"
        : $"user model {Device.Id} ({Device.Role}) sha256:{Device.Hash} bound to {Where}; DYR data for it, if any, are not used";
}

/// <summary>
/// RC5 wp32b: one user relay of a dynamics study (<see cref="DynamicsStudyRequest.RelayModels"/>): a relay plug-in (contract 2) or
/// a declarative <c>gridsim-model/1</c> document of role <c>relay</c>, bound to one in-service unit (named by its native identity)
/// or to one in-service branch (named by its native identity, read at its <c>from</c> end as written). It acts in the full-model
/// companion run of each events row (<see cref="Dynamics.Models.RmsTransientStability.StudyEventsWithUserRelays"/>).
/// </summary>
public sealed class RelayModelAssignment
{
    private RelayModelAssignment(PluginModel? plugin, Dynamics.UserModels.UserModel? declared, DynamicsMachineId? machine, DynamicsBranchId? branch)
    {
        Plugin = plugin;
        Declared = declared;
        Machine = machine;
        Branch = branch;
    }

    /// <summary>A relay plug-in bound to the unit <paramref name="machine"/>.</summary>
    public static RelayModelAssignment AtMachine(DynamicsMachineId machine, PluginModel model)
    {
        ArgumentNullException.ThrowIfNull(machine);
        RequirePluginRelay(model);
        return new RelayModelAssignment(model, null, machine, null);
    }

    /// <summary>A declarative relay bound to the unit <paramref name="machine"/>.</summary>
    public static RelayModelAssignment AtMachine(DynamicsMachineId machine, Dynamics.UserModels.UserModel model)
    {
        ArgumentNullException.ThrowIfNull(machine);
        RequireDeclaredRelay(model);
        return new RelayModelAssignment(null, model, machine, null);
    }

    /// <summary>A relay plug-in bound to the branch <paramref name="branch"/>, read at its <c>From</c> end.</summary>
    public static RelayModelAssignment AtBranch(DynamicsBranchId branch, PluginModel model)
    {
        ArgumentNullException.ThrowIfNull(branch);
        RequirePluginRelay(model);
        return new RelayModelAssignment(model, null, null, branch);
    }

    /// <summary>A declarative relay bound to the branch <paramref name="branch"/>, read at its <c>From</c> end.</summary>
    public static RelayModelAssignment AtBranch(DynamicsBranchId branch, Dynamics.UserModels.UserModel model)
    {
        ArgumentNullException.ThrowIfNull(branch);
        RequireDeclaredRelay(model);
        return new RelayModelAssignment(null, model, null, branch);
    }

    private static void RequirePluginRelay(PluginModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        if (model.Role != UserModelRole.Relay)
            throw new ArgumentException($"{model.Id} has role {model.Definition.RoleText}; a relay model is needed.", nameof(model));
    }

    private static void RequireDeclaredRelay(Dynamics.UserModels.UserModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        if (model.Role != Dynamics.UserModels.DeclarativeRelay.Role)
            throw new ArgumentException($"{model.Id} has role '{model.Role}'; a relay model is needed.", nameof(model));
    }

    /// <summary>The relay plug-in, or null for a declarative relay.</summary>
    public PluginModel? Plugin { get; }

    /// <summary>The declarative relay, or null for a plug-in.</summary>
    public Dynamics.UserModels.UserModel? Declared { get; }

    /// <summary>The unit the relay is bound to; null for a branch relay.</summary>
    public DynamicsMachineId? Machine { get; }

    /// <summary>The branch the relay is bound to (read at its From end); null for a unit relay.</summary>
    public DynamicsBranchId? Branch { get; }

    /// <summary>True for a plug-in (its results are outside the verified contract).</summary>
    public bool IsPlugin => Plugin is not null;

    /// <summary>The model id.</summary>
    public string Id => Plugin?.Id ?? Declared!.Id;

    /// <summary>The SHA-256 of the plug-in assembly or of the declarative document.</summary>
    public string Hash => Plugin?.Hash ?? Declared!.Hash;

    /// <summary>The device name in the relay records: <c>PLUGIN:ID</c> or <c>USER:ID</c>.</summary>
    public string Device => (IsPlugin ? "PLUGIN:" : "USER:") + Id;

    /// <summary>Where the relay is bound, in words.</summary>
    public string Where => Machine is { } m ? m.ToString()
        : $"{Branch} read at bus {Branch!.From.ToString(CultureInfo.InvariantCulture)}";

    /// <summary>The coverage note naming the relay and where it is bound: a plug-in's provenance (with
    /// <see cref="PluginContract.OutsideLabel"/>), or a declarative relay's id and document hash.</summary>
    public string Note => Plugin is { } p
        ? p.ProvenanceNote + "; relay bound to " + Where + "; it acts in the full-model companion run only"
        : $"user model {Id} (relay) sha256:{Hash} bound to {Where}; it acts in the full-model companion run only";
}
