using GridSim.Core.Dynamics.Models;

namespace GridSim.Core.Dynamics.UserModels;

/// <summary>
/// A compiled declarative model with the parameter values it is bound with (R4-08). Immutable: it makes a fresh,
/// independent <see cref="ModelInstance"/> for every machine and every trial, which is what a stateful controller needs.
/// </summary>
public sealed class UserModel
{
    /// <summary>The compiled model.</summary>
    public CompiledModel Plan { get; }

    /// <summary>Parameter overrides by name (null or empty: the document's own values).</summary>
    public IReadOnlyDictionary<string, double>? Parameters { get; }

    /// <summary>Bind a compiled model. The values are checked now (a refusal here is before any study).</summary>
    public UserModel(CompiledModel plan, IReadOnlyDictionary<string, double>? parameters = null)
    {
        Plan = plan ?? throw new ArgumentNullException(nameof(plan));
        Parameters = parameters is null ? null : new SortedDictionary<string, double>(parameters.ToDictionary(k => k.Key, v => v.Value), StringComparer.Ordinal);
        _ = ModelInstance.Create(plan, Parameters);
    }

    /// <summary>Parse, validate and compile a document and bind it.</summary>
    public static UserModel FromDocument(ModelDocument doc, IReadOnlyDictionary<string, double>? parameters = null)
        => new(ModelCompiler.Compile(doc), parameters);

    /// <summary>The document id.</summary>
    public string Id => Plan.Document.Id;

    /// <summary>The document role.</summary>
    public string Role => Plan.Document.Role;

    /// <summary>The document hash (SHA-256, lower-case hex).</summary>
    public string Hash => Plan.Document.Sha256;

    /// <summary>A fresh independent instance.</summary>
    public ModelInstance NewInstance() => ModelInstance.Create(Plan, Parameters);
}

/// <summary>A governor-role declarative model as an <see cref="IRmsGovernor"/>: speed deviation in, mechanical power out.</summary>
public sealed class UserModelGovernor : IRmsGovernor
{
    private readonly ModelInstance _m;

    /// <summary>Wrap an instance of a governor-role model.</summary>
    public UserModelGovernor(ModelInstance instance)
    {
        ArgumentNullException.ThrowIfNull(instance);
        if (instance.Document.Role != "governor")
            throw new ArgumentException($"{instance.Document.Id} has role '{instance.Document.Role}', not 'governor'.", nameof(instance));
        _m = instance;
    }

    /// <inheritdoc />
    public void Initialize(double mechanicalPowerPu) => _m.Initialize(mechanicalPowerPu);

    /// <inheritdoc />
    public double Step(double speedDeviationPu, double dtSeconds) => _m.Step(speedDeviationPu, dtSeconds);
}

/// <summary>The governor data of a user model: plugs into <c>SubtransientInit.MachineData.Governor</c> like the coded governor data.</summary>
public sealed record UserModelGovernorData(UserModel UserModel) : GovernorData
{
    /// <inheritdoc />
    public override string Model => "USER:" + UserModel.Id;

    /// <inheritdoc />
    public override IRmsGovernor Create()
    {
        if (UserModel.Role != "governor")
            throw new ArgumentException($"{UserModel.Id} has role '{UserModel.Role}', not 'governor'.");
        return new UserModelGovernor(UserModel.NewInstance());
    }
}

/// <summary>
/// An exciter-role declarative model as an <see cref="IRmsFieldController"/>: the regulator error and speed deviation in, the
/// field voltage out. Plugs into the kernel's field-controller list directly, and into the studies through
/// <see cref="RmsExciter.FromUser"/>.
/// </summary>
public sealed class UserModelFieldController : IRmsFieldController
{
    private readonly UserModel _model;
    private readonly ModelInstance _m;
    private ModelInstance? _scratch;

    /// <summary>Make a controller (with its own fresh instance) from an exciter-role model.</summary>
    public UserModelFieldController(UserModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        if (model.Role != "exciter") throw new ArgumentException($"{model.Id} has role '{model.Role}', not 'exciter'.", nameof(model));
        _model = model;
        _m = model.NewInstance();
    }

    /// <inheritdoc />
    public void Initialize(double fieldVoltagePu)
    {
        if (!double.IsFinite(fieldVoltagePu))
            throw new ArgumentOutOfRangeException(nameof(fieldVoltagePu), $"Initial field {fieldVoltagePu} is not finite.");
        _m.Initialize(fieldVoltagePu);
    }

    /// <inheritdoc />
    public double EquilibriumErrorPu(double fieldVoltagePu)
    {
        // Pure: computed on a private scratch instance so the live states are never disturbed.
        _scratch ??= _model.NewInstance();
        _scratch.Initialize(fieldVoltagePu);
        return _scratch.EquilibriumError;
    }

    /// <inheritdoc />
    public double Step(double voltageErrorPu, double speedDeviationPu, double dtSeconds)
        => _m.Step(voltageErrorPu, speedDeviationPu, dtSeconds);
}

/// <summary>A stabiliser-role declarative model: speed deviation (or other input) in, stabilising signal out.</summary>
public sealed class UserModelStabiliser
{
    private readonly ModelInstance _m;

    /// <summary>Wrap an instance of a stabiliser-role model.</summary>
    public UserModelStabiliser(ModelInstance instance)
    {
        ArgumentNullException.ThrowIfNull(instance);
        if (instance.Document.Role != "stabiliser")
            throw new ArgumentException($"{instance.Document.Id} has role '{instance.Document.Role}', not 'stabiliser'.", nameof(instance));
        _m = instance;
    }

    /// <summary>Initialise at a steady input.</summary>
    public void Initialize(double steadyInputPu) => _m.Initialize(steadyInputPu);

    /// <summary>One step.</summary>
    public double Step(double inputPu, double dtSeconds) => _m.Step(inputPu, dtSeconds);
}
