using System.Globalization;
using GridSim.Core.Dynamics.Plugins;

namespace GridSim.Core.Dynamics.UserModels;

/// <summary>
/// RC5 wp32b: a declarative model of role <c>relay</c> as an <see cref="IUserRelay"/>, so the kernel runs it by the same route as
/// a relay plug-in (<see cref="UserRelayBinding"/>). The input ports, in this order, are <c>vm</c> (|V|, pu), <c>f</c> (the
/// machine's electrical frequency in Hz, declared <c>1</c>), <c>p</c> and <c>q</c> (system pu), <c>dfdt</c> (Hz/s, declared
/// <c>1/s</c>), <c>speed</c> (the speed deviation, pu) and <c>i</c> (|I|, system pu): exactly the fields of
/// <see cref="RelayInputs"/>, with no conversion. The one output port is <c>trip</c>: 0 is no action, and any other value trips,
/// the value being recorded as the reason code (it must then be a whole number). The model has no initialisation inputs; its
/// stateful blocks (for example <c>ontimer</c> and <c>latch</c>) are initialised with literal values. Each step is one
/// <see cref="ModelInstance.Step(ReadOnlySpan{double}, double)"/> with the step's inputs and the fixed step, so a timer counts
/// whole steps.
/// </summary>
public sealed class DeclarativeRelay : IUserRelay
{
    /// <summary>The role text.</summary>
    public const string Role = "relay";

    /// <summary>The input ports a branch relay does not have (a branch has no machine).</summary>
    public static readonly IReadOnlyList<string> MachinePorts = new[] { "f", "dfdt", "speed" };

    private readonly ModelInstance _m;
    private readonly double[] _in = new double[7];

    /// <summary>Wrap an instance of a relay-role model.</summary>
    public DeclarativeRelay(ModelInstance instance)
    {
        ArgumentNullException.ThrowIfNull(instance);
        if (instance.Document.Role != Role)
            throw new ArgumentException($"{instance.Document.Id} has role '{instance.Document.Role}', not '{Role}'.", nameof(instance));
        _m = instance;
    }

    /// <inheritdoc />
    public void Initialise() => _m.Initialize(ReadOnlySpan<double>.Empty);

    /// <inheritdoc />
    public RelayDecision Evaluate(in RelayInputs inputs)
    {
        _in[0] = inputs.VoltagePu;
        _in[1] = inputs.FrequencyHz;
        _in[2] = inputs.ActivePowerPu;
        _in[3] = inputs.ReactivePowerPu;
        _in[4] = inputs.FrequencyRateHzPerSecond;
        _in[5] = inputs.SpeedPu;
        _in[6] = inputs.CurrentPu;
        // A refusal inside the model (a non-finite expression, a timer input that is not a number) stops the run, naming the model.
        try { _m.Step(_in, inputs.StepSeconds); }
        catch (UserModelException ex)
        {
            throw new InvalidOperationException($"user model {_m.Document.Id} at t = {inputs.TimeSeconds.ToString("R", CultureInfo.InvariantCulture)} s: {ex.Message}", ex);
        }
        double trip = _m.Output(0);
        if (trip == 0.0) return RelayDecision.None;
        if (!double.IsFinite(trip) || trip != Math.Floor(trip) || trip < int.MinValue || trip > int.MaxValue)
            throw new InvalidOperationException($"user model {_m.Document.Id}: the trip output is {trip.ToString("R", CultureInfo.InvariantCulture)} "
                + $"at t = {inputs.TimeSeconds.ToString("R", CultureInfo.InvariantCulture)} s; it must be 0 (no action) or a whole number (the reason code).");
        return RelayDecision.TripWith((int)trip);
    }
}
