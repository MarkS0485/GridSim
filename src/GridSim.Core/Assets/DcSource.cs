using GridSim.Core.Storage;

namespace GridSim.Core.Assets;

/// <summary>
/// What sits behind an inverter's DC link: how much power it can give or take right now, and what it actually
/// delivers when asked. The inverter controllers draw through this seam, so a storage asset's grid response is
/// bounded by its energy store - from the simple state-of-charge bucket to a full chemistry model.
/// Powers are AC-side MW (discharge / injection positive).
/// </summary>
public interface IDcSource
{
    /// <summary>State of charge (fraction).</summary>
    double SocFraction { get; }

    /// <summary>The largest injection (Up) and absorption (Down, positive) available now (MW, AC side).</summary>
    (double UpMw, double DownMw) Headroom();

    /// <summary>Deliver <paramref name="acMw"/> for <paramref name="dt"/> seconds; returns what was delivered.</summary>
    double Draw(double acMw, double dt);

    /// <summary>Back to the initial state.</summary>
    void Reset();
}

/// <summary>
/// The linear energy bucket the inverters have always carried: <c>SoC -= P dt / 3600 / capacity</c>, no losses,
/// no rate limits, headroom closed only at the SoC bounds. A step inside the SoC window is arithmetically as it has
/// always been; the step that reaches a bound delivers only the energy left to it (GR-627).
/// </summary>
public sealed class SocBucket : IDcSource
{
    private readonly double _capacityMwh, _socMin, _socMax, _socInit;
    private double _soc;

    public SocBucket(double capacityMwh, double socMin, double socMax, double socInit)
    {
        _capacityMwh = capacityMwh; _socMin = socMin; _socMax = socMax; _socInit = socInit; _soc = socInit;
    }

    public double SocFraction => _soc;

    public (double UpMw, double DownMw) Headroom() =>
        (_soc <= _socMin ? 0.0 : double.PositiveInfinity, _soc >= _socMax ? 0.0 : double.PositiveInfinity);

    public double Draw(double acMw, double dt)
    {
        double before = _soc;
        double after = before - acMw * dt / 3600.0 / _capacityMwh;   // MWh drawn / capacity
        // GR-627 (October 2026): the step that crosses a SoC bound delivers only the energy the store held. It used to
        // clamp the SoC and still return the full request, so the crossing step created (or, charging, destroyed) energy;
        // and it clamped at 0 and 1, so a discharge ran past socMin within the step although the headroom closes there.
        // The bounds are the [socMin, socMax] window (widened to the SoC itself when that starts outside it, so a step
        // never pushes it further out, and never past 0 or 1). A step that stays inside returns acMw exactly as before.
        double lo = Math.Max(0.0, Math.Min(_socMin, before)), hi = Math.Min(1.0, Math.Max(_socMax, before));
        if (after >= lo && after <= hi)
        {
            _soc = after;
            return acMw;
        }
        _soc = Math.Clamp(after, lo, hi);
        return dt > 0.0 ? (before - _soc) * _capacityMwh * 3600.0 / dt : 0.0;   // the energy that actually moved, as an average MW
    }

    public void Reset() => _soc = _socInit;
}

/// <summary>
/// A battery pack (<see cref="PackModel"/>) behind a converter of efficiency <c>eta</c>: headroom is the pack's
/// instantaneous BMS limit (current, cut-off voltage, SoC window, temperature), and a draw steps the pack's
/// electrochemical and thermal state - so sustained delivery sags, heats and stops exactly where the chemistry
/// says, and charging carries the conversion losses (DC = AC / eta on discharge, AC * eta on charge).
/// </summary>
public sealed class BatteryDcSource : IDcSource
{
    private readonly PackModel _initial;
    private readonly double _eta;

    /// <summary>Default converter efficiency (R198).</summary>
    public const double DefaultConverterEfficiency = 0.98;

    /// <summary>Lowest accepted converter efficiency: below 50 % a converter is not a credible device (R198). GR-627: an
    /// efficiency outside [this, 1] (or not finite) is refused; it used to be clamped into the band without a word.</summary>
    public const double MinConverterEfficiency = 0.5;

    public BatteryDcSource(PackModel pack, double converterEfficiency = DefaultConverterEfficiency)
    {
        // R231 (B2): the snapshot keeps the pack's ageing state, so a reset restores it rather than dropping it.
        _initial = (pack ?? throw new ArgumentNullException(nameof(pack))).Clone(keepAgeing: true);
        Pack = pack;
        if (!double.IsFinite(converterEfficiency) || converterEfficiency < MinConverterEfficiency || converterEfficiency > 1.0)
            throw new ArgumentOutOfRangeException(nameof(converterEfficiency), converterEfficiency,
                $"The battery converter efficiency must be a finite fraction from {MinConverterEfficiency.ToString("R", System.Globalization.CultureInfo.InvariantCulture)} to 1.");
        _eta = converterEfficiency;
    }

    public PackModel Pack { get; private set; }
    public double SocFraction => Pack.Soc;

    public (double UpMw, double DownMw) Headroom()
    {
        (double disW, double chgW) = Pack.InstantaneousLimitsW();
        return (disW * _eta / 1e6, chgW / _eta / 1e6);
    }

    public double Draw(double acMw, double dt)
    {
        double dcW = acMw >= 0 ? acMw * 1e6 / _eta : acMw * 1e6 * _eta;
        PackStepResult r = Pack.Step(dcW, dt);
        LastStep = r;
        return r.DcPowerW >= 0 ? r.DcPowerW * _eta / 1e6 : r.DcPowerW / _eta / 1e6;
    }

    /// <summary>The pack's result for the most recent <see cref="Draw"/> (auxiliary load, heat, limit).</summary>
    public PackStepResult? LastStep { get; private set; }

    /// <summary>The converter efficiency.</summary>
    public double ConverterEfficiency => _eta;

    /// <summary>
    /// Back to the state the pack had when this source was built.
    /// <para>R231 (October 2026 review, stream B2; Ruling R1: Mark delegated EE calls 2026-10-04): this used to be
    /// <c>Pack = _initial.Clone()</c>. <see cref="PackModel.Clone()"/> is the look-ahead copy and builds its pack with
    /// ageing tracking OFF, and both the batch asset solve and the live asset-aware integrator reset the fleet before the
    /// first step, so every chemistry pack behind an inverter lost its ageing model before it ever ran: over the live
    /// loop's days of simulated time an inverter battery never faded or grew its resistance, unlike the same chemistry at a
    /// StorageFleet site. The swap also replaced the Pack object, so a holder of the old reference saw a pack that no
    /// longer moved. The pack is now restored IN PLACE from a snapshot that kept its ageing state
    /// (<see cref="PackModel.RestoreFrom"/>): same object, ageing on, fade back to its value at construction.</para>
    /// <para>What moves: an inverter backed by a chemistry pack (<c>batteryChemistry</c> set) now ages after a reset,
    /// exactly as it already did before its first reset, so its capacity and resistance follow the cell's fade (the
    /// calendar and cycle laws of <see cref="AgeingModel"/>). No bundled asset class sets <c>batteryChemistry</c>, so no
    /// bundled case moves; the SoC-bucket store is untouched. Every other part of the restored state (charge, RC voltages,
    /// temperature, ambient) is the same clone of the same cell as before.</para>
    /// </summary>
    public void Reset() => Pack.RestoreFrom(_initial);
}

/// <summary>Builds an asset class's DC source: a chemistry pack when <c>batteryChemistry</c> is set (with
/// <c>capacityMwh</c> as the usable energy), else the SoC bucket when <c>capacityMwh</c> is set, else none
/// (unbounded - the historical default).</summary>
public static class DcSources
{
    // N345 (CF3, October 2026): the fall-back value of each class parameter this code reads when the class does not
    // set it, named so the v2 kind specs (schemas/v2/kinds) cite it by symbol instead of quoting the source line.
    // Each constant is the literal it replaces, character for character, so no number moves.
    internal const double DefaultAmbientC = 25.0;   // AssetParams.AmbientC
    internal const double DefaultCapacityMwh = 0.0;   // AssetParams.CapacityMwh
    internal const double DefaultConverterEfficiency = 0.98;   // AssetParams.ConverterEfficiency
    internal const double DefaultDcVoltageV = 1000.0;   // AssetParams.DcVoltageV
    internal const double DefaultSocInit = 0.5;   // AssetParams.SocInit
    internal const double DefaultSocMax = 1.0;   // AssetParams.SocMax
    internal const double DefaultSocMin = 0.0;   // AssetParams.SocMin

    /// <summary>The <c>batteryChemistry</c> parameter codes: 0 (the default when the parameter is absent) is "no
    /// chemistry" (the lossless state-of-charge bucket), and 1 to 6 name a chemistry. Any other value (a non-integer, a
    /// negative code, a code with no chemistry) is refused rather than read as "no chemistry", which silently gave the
    /// asset the lossless bucket.</summary>
    public static ChemistryParameters? ChemistryFromCode(double code) => code == 0.0 ? null
        : code != Math.Floor(code) || code is < 1 or > 6
        ? throw new InvalidDataException($"batteryChemistry {code} is not one of the chemistry codes 1 to 6 (or 0 for none).")
        : (int)code switch
    {
        1 => BatteryChemistryLibrary.Lfp,
        2 => BatteryChemistryLibrary.Nmc,
        3 => BatteryChemistryLibrary.Lto,
        4 => BatteryChemistryLibrary.SodiumIon,
        5 => BatteryChemistryLibrary.VanadiumFlow,
        6 => BatteryChemistryLibrary.LeadAcid,
        _ => null,
    };

    public static IDcSource? FromClass(AssetClass cls, double ratedMw)
    {
        double capacityMwh = cls.Param(AssetParams.CapacityMwh, DefaultCapacityMwh);
        if (capacityMwh <= 0) return null;
        double socInit = cls.Param(AssetParams.SocInit, DefaultSocInit);
        ChemistryParameters? chem = ChemistryFromCode(cls.Param(AssetParams.BatteryChemistry, 0.0));
        if (chem is null)
            return new SocBucket(capacityMwh, cls.Param(AssetParams.SocMin, DefaultSocMin), cls.Param(AssetParams.SocMax, DefaultSocMax), socInit);
        PackModel pack = PackModel.Sized(chem, capacityMwh * 1e6, cls.Param(AssetParams.DcVoltageV, DefaultDcVoltageV), socInit,
            cls.Param(AssetParams.AmbientC, DefaultAmbientC), ratedPowerW: ratedMw > 0 ? ratedMw * 1e6 : null);
        return new BatteryDcSource(pack, cls.Param(AssetParams.ConverterEfficiency, DefaultConverterEfficiency));
    }
}
