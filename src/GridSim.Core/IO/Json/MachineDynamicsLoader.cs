using System.Globalization;
using System.Text.Json;
using GridSim.Core.Diagnostics;
using GridSim.Core.Dynamics.Models;
using GridSim.Core.IO.Json.Schema;
using GridSim.Core.Model;

namespace GridSim.Core.IO.Json;

/// <summary>
/// The exciter (and optional stabiliser) of one machine from a <c>gridsim-dynamics/2</c> file: immutable parameters, turned
/// into a FRESH <see cref="RmsExciter"/> by <see cref="Create"/> (an exciter is stateful; each machine needs its own).
/// </summary>
/// <param name="Model">"SEXS", "IEEET1", "EXAC1", "ESAC1A", "ESST4B", "ESST1A", "EXST1", "SCRX" or "ESDC1A" (the exciter
/// kind's <c>model</c>).</param>
/// <param name="Params">The exciter template's numeric parameters by kind-spec name.</param>
/// <param name="Pss">The stabiliser parameters by kind-spec name (of the model <see cref="PssModel"/>), or null when the
/// machine has no stabiliser.</param>
public sealed record ExciterData(string Model, IReadOnlyDictionary<string, double> Params, IReadOnlyDictionary<string, double>? Pss)
{
    /// <summary>WP3a (RC5): the stabiliser's model: "PSS1A" (the default, the historical one), "IEEEST", "PSS2A" or
    /// "PSS2B".</summary>
    public string PssModel { get; init; } = "PSS1A";

    /// <summary>A new exciter (with a new stabiliser when one is given). The model constructors check the values.</summary>
    public RmsExciter Create()
    {
        IRmsStabiliser? library = Pss is null || PssModel == "PSS1A" ? null : LibraryStabiliser(PssModel, Pss);
        RmsExciter ex = CreateExciter(library is null ? Pss : null);
        if (library is null || ex.LibraryStabiliser is not null) return ex;
        return ex.WithLibraryStabiliser(library);
    }

    // WP3a: the library stabilisers, from their kind-spec parameters.
    private static IRmsStabiliser LibraryStabiliser(string model, IReadOnlyDictionary<string, double> p)
    {
        double O(string k, double d) => p.GetValueOrDefault(k, d);
        int I(string k) => (int)p[k];
        return model switch
        {
            "IEEEST" => new Ieeest(I("ieeestInput"), O("ieeestA1S", 0.0), O("ieeestA3S", 0.0), O("ieeestA5S", 0.0),
                p["ieeestT1S"], p["ieeestT2S"], p["ieeestT3S"], p["ieeestT4S"], p["ieeestT5S"], p["ieeestT6S"], p["ieeestKs"],
                O("ieeestOutputMaxPu", double.PositiveInfinity), O("ieeestOutputMinPu", double.NegativeInfinity),
                O("ieeestVcuPu", 0.0), O("ieeestVclPu", 0.0)),
            // WP19 (RC5): PSS2C is PSS2B plus the T12/T13 stage and the output logic.
            "PSS2C" => new Pss2c(I("pss2Input1"), I("pss2Input2"), I("pss2M"), I("pss2N"), p["pss2Tw1S"], p["pss2Tw2S"], p["pss2T6S"],
                p["pss2Tw3S"], p["pss2Tw4S"], p["pss2T7S"], p["pss2Ks2"], p["pss2Ks3"], p["pss2T8S"], p["pss2T9S"], p["pss2Ks1"],
                p["pss2T1S"], p["pss2T2S"], p["pss2T3S"], p["pss2T4S"], p["pss2bT10S"], p["pss2bT11S"],
                O("pss2bInput1MaxPu", double.PositiveInfinity), O("pss2bInput1MinPu", double.NegativeInfinity),
                O("pss2bInput2MaxPu", double.PositiveInfinity), O("pss2bInput2MinPu", double.NegativeInfinity),
                O("pss2OutputMaxPu", double.PositiveInfinity), O("pss2OutputMinPu", double.NegativeInfinity),
                p["pss2cT12S"], p["pss2cT13S"], O("pss2cPssOnPu", double.NaN), O("pss2cPssOffPu", double.NaN)),
            "PSS2A" => Pss2.Pss2a(I("pss2Input1"), I("pss2Input2"), I("pss2M"), I("pss2N"), p["pss2Tw1S"], p["pss2Tw2S"], p["pss2T6S"],
                p["pss2Tw3S"], p["pss2Tw4S"], p["pss2T7S"], p["pss2Ks2"], p["pss2Ks3"], p["pss2T8S"], p["pss2T9S"], p["pss2Ks1"],
                p["pss2T1S"], p["pss2T2S"], p["pss2T3S"], p["pss2T4S"],
                O("pss2OutputMaxPu", double.PositiveInfinity), O("pss2OutputMinPu", double.NegativeInfinity)),
            _ => Pss2.Pss2b(I("pss2Input1"), I("pss2Input2"), I("pss2M"), I("pss2N"), p["pss2Tw1S"], p["pss2Tw2S"], p["pss2T6S"],
                p["pss2Tw3S"], p["pss2Tw4S"], p["pss2T7S"], p["pss2Ks2"], p["pss2Ks3"], p["pss2T8S"], p["pss2T9S"], p["pss2Ks1"],
                p["pss2T1S"], p["pss2T2S"], p["pss2T3S"], p["pss2T4S"], p["pss2bT10S"], p["pss2bT11S"],
                O("pss2bInput1MaxPu", double.PositiveInfinity), O("pss2bInput1MinPu", double.NegativeInfinity),
                O("pss2bInput2MaxPu", double.PositiveInfinity), O("pss2bInput2MinPu", double.NegativeInfinity),
                O("pss2OutputMaxPu", double.PositiveInfinity), O("pss2OutputMinPu", double.NegativeInfinity)),
        };
    }

    // The exciter with the PSS1A of pss1a (null: none).
    private RmsExciter CreateExciter(IReadOnlyDictionary<string, double>? Pss)
    {
        Pss1a? pss = Pss is null ? null : new Pss1a(Pss["gain"], Pss["washoutS"], Pss["lead1S"], Pss["lag1S"], Pss["lead2S"],
            Pss["lag2S"], Pss.GetValueOrDefault("outputMinPu", double.NegativeInfinity), Pss.GetValueOrDefault("outputMaxPu", double.PositiveInfinity));
        if (Model == "ESST4B")
            return RmsExciter.FromEsst4b(new Esst4b(Params["st4bKpr"], Params["st4bKir"], Params["st4bVrMinPu"], Params["st4bVrMaxPu"],
                Params["st4bTaS"], Params["st4bKpm"], Params["st4bKim"], Params["st4bVmMinPu"], Params["st4bVmMaxPu"], Params["st4bKg"],
                Params["st4bKp"], Params["st4bKi"], Params["st4bVbMaxPu"], Params["st4bKc"], Params["st4bXlPu"], Params["st4bThetapDeg"]), pss);
        if (Model is "EXAC1" or "ESAC1A")
        {
            bool a1a = Model == "ESAC1A";
            var ac = new AcExciter(a1a ? AcExciterVariant.Esac1a : AcExciterVariant.Exac1, Params["acLeadS"], Params["acLagS"],
                Params["acRegulatorGain"], Params["acRegulatorTimeS"], Params["acRegulatorMinPu"], Params["acRegulatorMaxPu"],
                a1a ? Params["acOutputMinPu"] : double.NegativeInfinity, a1a ? Params["acOutputMaxPu"] : double.PositiveInfinity,
                Params["acExciterTimeS"], Params["acFeedbackGain"], Params["acFeedbackTimeS"], Params["acRectifierKc"],
                Params["acDemagnetisingKd"], Params["acExciterConstant"], Params["acSatE1"], Params["acSatSe1"], Params["acSatE2"], Params["acSatSe2"]);
            return RmsExciter.FromAc(ac, pss);
        }
        if (Model is "ESST1A" or "EXST1")
        {
            double tr = Params.GetValueOrDefault("st1TrS", 0.0);
            St1Exciter st1 = Model == "EXST1"
                ? St1Exciter.Exst1(tr, Params["st1ViMaxPu"], Params["st1ViMinPu"], Params["st1TcS"], Params["st1TbS"], Params["st1Ka"],
                    Params["st1TaS"], Params["st1VrMaxPu"], Params["st1VrMinPu"], Params["st1Kc"], Params["st1Kf"], Params["st1TfS"])
                : St1Exciter.Esst1a(tr, Params["st1ViMaxPu"], Params["st1ViMinPu"], Params["st1TcS"], Params["st1TbS"], Params["st1aTc1S"],
                    Params["st1aTb1S"], Params["st1Ka"], Params["st1TaS"], Params["st1aVaMaxPu"], Params["st1aVaMinPu"], Params["st1VrMaxPu"],
                    Params["st1VrMinPu"], Params["st1Kc"], Params["st1Kf"], Params["st1TfS"], Params["st1aKlr"], Params["st1aIlrPu"],
                    (int)Params.GetValueOrDefault("st1aVos", 1.0));
            return RmsExciter.FromModel(st1, pss);
        }
        if (Model == "SCRX")
            return RmsExciter.FromModel(new Scrx(Params["scrxLeadS"], Params["scrxLagS"], Params["scrxGain"], Params["scrxExciterTimeS"],
                Params["scrxFieldMinPu"], Params["scrxFieldMaxPu"], Params["scrxSourceSwitch"] == 0.0, Params.GetValueOrDefault("scrxCrowbarRatio", 0.0)), pss);
        // WP19 (RC5): EXDC2 and IEEEX1 take the ESDC1A parameters (the same PSS/E data); ESAC8B and ESST3A their own.
        if (Model is "EXDC2" or "IEEEX1")
        {
            var a = (Params.GetValueOrDefault("dc1aTrS", 0.0), Params["dc1aKa"], Params["dc1aTaS"], Params["dc1aTbS"], Params["dc1aTcS"],
                Params["dc1aVrMaxPu"], Params["dc1aVrMinPu"], Params["dc1aKe"], Params["dc1aTeS"], Params["dc1aKf"], Params["dc1aTf1S"],
                Params.GetValueOrDefault("dc1aSatE1", 0.0), Params.GetValueOrDefault("dc1aSatSe1", 0.0),
                Params.GetValueOrDefault("dc1aSatE2", 0.0), Params.GetValueOrDefault("dc1aSatSe2", 0.0));
            Exdc2 dc = Model == "EXDC2"
                ? Exdc2.Exdc2Model(a.Item1, a.Item2, a.Item3, a.Item4, a.Item5, a.Item6, a.Item7, a.Item8, a.Item9, a.Item10, a.Item11, a.Item12, a.Item13, a.Item14, a.Item15)
                : Exdc2.Ieeex1(a.Item1, a.Item2, a.Item3, a.Item4, a.Item5, a.Item6, a.Item7, a.Item8, a.Item9, a.Item10, a.Item11, a.Item12, a.Item13, a.Item14, a.Item15);
            return RmsExciter.FromModel(dc, pss);
        }
        if (Model == "ESAC8B")
            return RmsExciter.FromModel(new Esac8b(Params.GetValueOrDefault("ac8bTrS", 0.0), Params["ac8bKp"], Params["ac8bKi"], Params["ac8bKd"],
                Params["ac8bTdS"], Params["ac8bKa"], Params["ac8bTaS"], Params["ac8bVrMaxPu"], Params["ac8bVrMinPu"], Params["ac8bTeS"], Params["ac8bKe"],
                Params.GetValueOrDefault("ac8bSatE1", 0.0), Params.GetValueOrDefault("ac8bSatSe1", 0.0), Params.GetValueOrDefault("ac8bSatE2", 0.0),
                Params.GetValueOrDefault("ac8bSatSe2", 0.0), Params.GetValueOrDefault("ac8bKc", 0.0), Params.GetValueOrDefault("ac8bKdExc", 0.0)), pss);
        if (Model == "ESST3A")
            return RmsExciter.FromModel(new Esst3a(Params.GetValueOrDefault("st3aTrS", 0.0), Params["st3aViMaxPu"], Params["st3aViMinPu"], Params["st3aKm"],
                Params["st3aTcS"], Params["st3aTbS"], Params["st3aKa"], Params["st3aTaS"], Params["st3aVrMaxPu"], Params["st3aVrMinPu"], Params["st3aKg"],
                Params["st3aKp"], Params["st3aKi"], Params["st3aVbMaxPu"], Params["st3aKc"], Params["st3aXlPu"], Params["st3aVgMaxPu"],
                Params["st3aThetapDeg"], Params["st3aTmS"], Params["st3aVmMaxPu"], Params["st3aVmMinPu"]), pss);
        if (Model == "ESDC1A")
            return RmsExciter.FromModel(new Esdc1a(Params.GetValueOrDefault("dc1aTrS", 0.0), Params["dc1aKa"], Params["dc1aTaS"], Params["dc1aTbS"],
                Params["dc1aTcS"], Params["dc1aVrMaxPu"], Params["dc1aVrMinPu"], Params["dc1aKe"], Params["dc1aTeS"], Params["dc1aKf"],
                Params["dc1aTf1S"], Params.GetValueOrDefault("dc1aSatE1", 0.0), Params.GetValueOrDefault("dc1aSatSe1", 0.0),
                Params.GetValueOrDefault("dc1aSatE2", 0.0), Params.GetValueOrDefault("dc1aSatSe2", 0.0)), pss);
        return Model == "SEXS"
            ? RmsExciter.FromSexs(Params["sexsLeadS"], Params["sexsLagS"], Params["sexsGain"], Params["sexsExciterTimeS"],
                Params.GetValueOrDefault("sexsFieldMinPu", double.NegativeInfinity), Params.GetValueOrDefault("sexsFieldMaxPu", double.PositiveInfinity), pss)
            : RmsExciter.FromIeeet1(Params["ieeet1RegulatorGain"], Params["ieeet1RegulatorTimeS"], Params["ieeet1ExciterConstant"],
                Params["ieeet1ExciterTimeS"], Params["ieeet1FeedbackGain"], Params["ieeet1FeedbackTimeS"],
                Params.GetValueOrDefault("ieeet1RegulatorMinPu", double.NegativeInfinity), Params.GetValueOrDefault("ieeet1RegulatorMaxPu", double.PositiveInfinity),
                Params.GetValueOrDefault("ieeet1SaturationA", 0.0), Params.GetValueOrDefault("ieeet1SaturationB", 0.0), pss);
    }
}

/// <summary>One bound machine of a <c>gridsim-dynamics/2</c> file.</summary>
/// <param name="GeneratorIndex">Index into <c>model.Generators</c>.</param>
/// <param name="GeneratorLabel">The generator as the file named it, for reports.</param>
/// <param name="TemplateRef">The machine template, <c>id@rev</c>.</param>
/// <param name="Data">The RMS machine data built from the template (governor included when the template names one).</param>
/// <param name="Exciter">The exciter (and stabiliser), or null when the template names none.</param>
/// <param name="Provenance">Every provenance tag behind this machine's numbers (its templates' defaults and any
/// per-parameter tags), distinct, in enum order.</param>
/// <param name="Notes">What the loader filled in from the case rather than the file (for example the inertia), in
/// words.</param>
public sealed record MachineDynamicsEntry(int GeneratorIndex, string GeneratorLabel, string TemplateRef,
    SubtransientInit.MachineData Data, ExciterData? Exciter, IReadOnlyList<ProvenanceTag> Provenance, IReadOnlyList<string> Notes);

/// <summary>A loaded <c>gridsim-dynamics/2</c> file, bound to one case's generators.</summary>
public sealed class MachineDynamicsSet
{
    private readonly Dictionary<int, MachineDynamicsEntry> _byGenerator;

    internal MachineDynamicsSet(string name, string path, IReadOnlyList<MachineDynamicsEntry> entries)
    {
        Name = name;
        Path = path;
        Entries = entries;
        _byGenerator = entries.ToDictionary(e => e.GeneratorIndex);
    }

    /// <summary>The file's <c>name</c>.</summary>
    public string Name { get; }

    /// <summary>The file path it was read from.</summary>
    public string Path { get; }

    /// <summary>Every bound machine, in file order.</summary>
    public IReadOnlyList<MachineDynamicsEntry> Entries { get; }

    /// <summary>The entry for generator <paramref name="generatorIndex"/>, or null when the file gives it no data.</summary>
    public MachineDynamicsEntry? For(int generatorIndex) => _byGenerator.GetValueOrDefault(generatorIndex);

    /// <summary>
    /// WECC (expansion): the converter units the data gives a WECC renewable model chain (from a PSS/E DYR file), keyed by
    /// the unit's index in <c>model.Generators</c>; empty for a <c>gridsim-dynamics/2</c> file (which describes synchronous
    /// machines only). The RMS study puts each into that unit's data (<c>SubtransientInit.MachineData.Renewable</c>).
    /// </summary>
    public IReadOnlyDictionary<int, GridSim.Core.Dynamics.Models.Wecc.WeccPlant> Renewables { get; private init; } =
        new Dictionary<int, GridSim.Core.Dynamics.Models.Wecc.WeccPlant>();

    /// <summary>WECC (expansion): this set with <paramref name="renewables"/> as its renewable units. A unit may not be
    /// both a bound machine and a renewable unit (refused).</summary>
    public MachineDynamicsSet WithRenewables(IReadOnlyDictionary<int, GridSim.Core.Dynamics.Models.Wecc.WeccPlant> renewables)
    {
        ArgumentNullException.ThrowIfNull(renewables);
        foreach (int k in renewables.Keys)
            if (_byGenerator.ContainsKey(k))
                throw new InvalidDataException($"{System.IO.Path.GetFileName(Path)}: generator {k} has both machine data and a renewable model chain.");
        return new MachineDynamicsSet(Name, Path, Entries) { Renewables = new Dictionary<int, GridSim.Core.Dynamics.Models.Wecc.WeccPlant>(renewables), DynamicLoads = DynamicLoads, Facts = Facts, NetworkDevices = NetworkDevices };
    }

    /// <summary>WP3b (RC5): the dynamic loads the data gives (from a PSS/E DYR file's load records); empty for a
    /// <c>gridsim-dynamics/2</c> file. The RMS study attaches them through <see cref="RmsLoadedMachineData"/>.</summary>
    public IReadOnlyList<DynamicLoadData> DynamicLoads { get; private init; } = Array.Empty<DynamicLoadData>();

    /// <summary>WP3b (RC5): this set with <paramref name="loads"/> as its dynamic loads (the renewables kept).</summary>
    public MachineDynamicsSet WithDynamicLoads(IReadOnlyList<DynamicLoadData> loads)
    {
        ArgumentNullException.ThrowIfNull(loads);
        return new MachineDynamicsSet(Name, Path, Entries) { Renewables = Renewables, DynamicLoads = loads.ToArray(), Facts = Facts, NetworkDevices = NetworkDevices };
    }

    /// <summary>WP6 (RC5): the SVC models the data gives units of the case (from a PSS/E DYR file's CSVGN1 records), keyed by the
    /// unit's index in <c>model.Generators</c>; empty for a <c>gridsim-dynamics/2</c> file. The RMS study puts each into that
    /// unit's data (<c>SubtransientInit.MachineData.Facts</c>).</summary>
    public IReadOnlyDictionary<int, RmsFactsData> Facts { get; private init; } = new Dictionary<int, RmsFactsData>();

    /// <summary>WP6 (RC5): the network device models the data gives (from a PSS/E DYR file's CDC4T records); empty for a
    /// <c>gridsim-dynamics/2</c> file. The RMS study attaches them through <see cref="RmsLoadedMachineData"/>.</summary>
    public IReadOnlyList<RmsNetworkDeviceData> NetworkDevices { get; private init; } = Array.Empty<RmsNetworkDeviceData>();

    /// <summary>WP6 (RC5): this set with <paramref name="facts"/> and <paramref name="devices"/> (the renewables and dynamic loads
    /// kept). A unit may not be both a bound machine and an SVC (refused).</summary>
    public MachineDynamicsSet WithDevices(IReadOnlyDictionary<int, RmsFactsData> facts, IReadOnlyList<RmsNetworkDeviceData> devices)
    {
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(devices);
        foreach (int k in facts.Keys)
            if (_byGenerator.ContainsKey(k))
                throw new InvalidDataException($"{System.IO.Path.GetFileName(Path)}: generator {k} has both machine data and an SVC model.");
        return new MachineDynamicsSet(Name, Path, Entries)
        {
            Renewables = Renewables, DynamicLoads = DynamicLoads,
            Facts = new Dictionary<int, RmsFactsData>(facts), NetworkDevices = devices.ToArray(),
        };
    }

    /// <summary>WECC (expansion): a set with no machine entries (a DYR file that gives only renewable units; the RMS study
    /// then keeps its default data for every machine).</summary>
    public static MachineDynamicsSet Empty(string name, string path) => new(name, path, Array.Empty<MachineDynamicsEntry>());

    /// <summary>True when every number of every bound machine is tagged <see cref="ProvenanceTag.Recorded"/> or
    /// <see cref="ProvenanceTag.DerivedFromRecorded"/>: the only case in which a study built on it may drop its
    /// INDICATIVE label (and then only if every machine of the study is bound).</summary>
    public bool AllRecorded => Entries.All(e => e.Provenance.All(t => t is ProvenanceTag.Recorded or ProvenanceTag.DerivedFromRecorded));
}

/// <summary>
/// F23 (October 2026 review, stream B3; Ruling R1: Mark delegated EE calls 2026-10-04): reads a machine dynamic data file,
/// <c>gridsim-dynamics/2</c> (<c>schemas/v2/dynamics.schema.json</c>), and turns it into the RMS study's per-machine
/// <see cref="SubtransientInit.MachineData"/> (reactances, time constants, inertia, damping, saturation, leakage, governor)
/// and exciters, replacing the CLI's synthetic GENROU defaults for every machine the file covers. JSON first; a PSS/E
/// <c>.dyr</c> reader is a later step and would produce the same objects.
///
/// <para><b>Fit with the v2 family.</b> The file carries its own templates in exactly the library template shape, so the
/// same kind specs, closed unit vocabulary, value forms and provenance tags apply: <c>synchronousMachine</c> for the
/// machine (whose <c>controllers</c> slots name its <c>exciter</c>, <c>pss</c> and <c>governor</c> templates),
/// <c>exciter</c> (SEXS, IEEET1, EXAC1, ESAC1A or ESST4B), <c>pss</c> (PSS1A) and <c>governor</c> (TGOV1, IEEEG1, HYGOV, GAST). A <c>machines</c> list
/// binds machine templates to the case's generators. Validation is fail loud and collects every problem:
/// <list type="number">
/// <item>the document against <c>dynamics.schema.json</c>, then its templates as one library
/// (<see cref="V2DocumentValidator"/>: kind specs, units, ranges, unique id@rev, controller targets);</item>
/// <item>here: each binding names exactly one generator of the case; no generator is bound twice; a bound unit must be a
/// synchronous machine (not converter plant, not power-flow-only); a bound machine template must be active;</item>
/// <item>here: each model's REQUIRED parameters are present (a GENROU machine needs all six reactances and four time
/// constants; SEXS, IEEET1, EXAC1, ESAC1A, ESST4B, PSS1A, TGOV1, IEEEG1, HYGOV and GAST need theirs, listed in <see cref="RequiredFor"/>) and no
/// parameter is given that the model would ignore (another model's parameters, the lumped governor's, or machine
/// parameters that belong to the case such as <c>unitTransformerXPu</c>): a silently ignored number is refused.</item>
/// </list>
/// Absent optional values take the documented defaults: damping 0, Xl 0 (the leakage-free saturation scale), no
/// saturation, unlimited exciter / governor limits except the valve floor of 0, IEEEG1 fractions K1 = 1 and the rest 0,
/// unlimited valve rates. An absent <c>inertiaH</c> takes the CASE's own H for that unit (case data, not a synthetic
/// default), and the entry's notes say so.</para>
/// </summary>
public static class MachineDynamicsLoader
{
    /// <summary>The machine parameters a GENROU (six-state) machine must have.</summary>
    public static readonly IReadOnlyList<string> MachineRequired = new[]
        { "xdPu", "xdpPu", "xdppPu", "xqPu", "xqpPu", "xqppPu", "tdopS", "tdoppS", "tqopS", "tqoppS" };

    /// <summary>The machine parameters a dynamics file may also give.</summary>
    public static readonly IReadOnlyList<string> MachineOptional = new[] { "inertiaH", "damping", "xlPu", "saturationS10", "saturationS12" };

    /// <summary>WP3a (RC5): the machine models a dynamics file names in a machine template's <c>model</c> (absent: GENROU,
    /// the six-state machine that also carries GENSAL data).</summary>
    public static readonly IReadOnlyList<string> MachineModels = new[] { "GENROU", "GENTPJ", "GENCLS" };

    /// <summary>WP3a (RC5): what a GENTPJ machine may give besides the GENROU parameters.</summary>
    public static readonly IReadOnlyList<string> GentpjOptional = new[] { "saturationKis" };

    /// <summary>WP3a (RC5): the parameters a GENCLS machine must have, and may also give.</summary>
    public static readonly IReadOnlyList<string> GenclsRequired = new[] { "xdpPu" };

    /// <summary>See <see cref="GenclsRequired"/>.</summary>
    public static readonly IReadOnlyList<string> GenclsOptional = new[] { "inertiaH", "damping" };

    /// <summary>WP3a (RC5): the exciter and stabiliser models a dynamics file takes.</summary>
    public static readonly IReadOnlyList<string> ExciterModels = new[] { "SEXS", "IEEET1", "EXAC1", "ESAC1A", "ESST4B", "ESST1A", "EXST1", "SCRX", "ESDC1A",
        "EXDC2", "IEEEX1", "ESAC8B", "ESST3A" };

    /// <summary>See <see cref="ExciterModels"/>.</summary>
    public static readonly IReadOnlyList<string> StabiliserModels = new[] { "PSS1A", "IEEEST", "PSS2A", "PSS2B", "PSS2C" };

    private static readonly string[] St1Common = { "st1ViMaxPu", "st1ViMinPu", "st1TcS", "st1TbS", "st1Ka", "st1TaS", "st1VrMaxPu", "st1VrMinPu", "st1Kc", "st1Kf", "st1TfS" };

    /// <summary>WP3b (RC5): the per-machine governor models a dynamics file takes.</summary>
    public static readonly IReadOnlyList<string> GovernorModels = new[] { "TGOV1", "IEEEG1", "HYGOV", "GAST", "GGOV1", "WSIEG1", "IEEEG3" };

    // WP3b: GGOV1 (PSS/E order) and WSIEG1 (the gain table points ride as wsieg1Gv1..5 and wsieg1Pgv1..5).
    private static readonly string[] Ggov1Required = { "ggov1Rselect", "ggov1Flag", "droopPu", "ggov1TpelecS", "ggov1MaxErrPu", "ggov1MinErrPu",
        "ggov1Kpgov", "ggov1Kigov", "ggov1Kdgov", "ggov1TdgovS", "valveMaxPu", "valveMinPu", "ggov1TactS", "ggov1Kturb", "ggov1WfnlPu",
        "ggov1TbS", "ggov1TcS", "ggov1TfloadS", "ggov1Kpload", "ggov1Kiload", "ggov1LdrefPu", "ggov1RopenPuPerS", "ggov1RclosePuPerS" };

    private static readonly string[] Ggov1Optional = { "ggov1TengS", "turbineDampingPu", "ggov1Kimw", "ggov1AsetPuPerS", "ggov1Ka", "ggov1TaS",
        "ggov1TrateMw", "ggov1DbPu", "ggov1TsaS", "ggov1TsbS", "ggov1RupPuPerS", "ggov1RdownPuPerS" };

    private static readonly string[] Wsieg1Required = { "wsieg1K", "wsieg1T1S", "wsieg1T2S", "wsieg1T3S", "wsieg1UoPuPerS", "wsieg1UcPuPerS",
        "valveMaxPu", "valveMinPu", "wsieg1T4S", "wsieg1T5S", "wsieg1T6S", "wsieg1T7S" };

    private static readonly string[] Wsieg1Optional = { "wsieg1K1", "wsieg1K2", "wsieg1K3", "wsieg1K4", "wsieg1K5", "wsieg1K6", "wsieg1K7", "wsieg1K8",
        "wsieg1Db1Pu", "wsieg1ErrPu", "wsieg1Db2Pu", "wsieg1Gv1", "wsieg1Pgv1", "wsieg1Gv2", "wsieg1Pgv2", "wsieg1Gv3", "wsieg1Pgv3", "wsieg1Gv4",
        "wsieg1Pgv4", "wsieg1Gv5", "wsieg1Pgv5", "wsieg1Iblock" };

    // WP19 (RC5): the ESAC8B, ESST3A and IEEEG3 parameter lists (PSS/E order).
    private static readonly string[] Esac8bRequired = { "ac8bKp", "ac8bKi", "ac8bKd", "ac8bTdS", "ac8bKa", "ac8bTaS", "ac8bVrMaxPu", "ac8bVrMinPu",
        "ac8bTeS", "ac8bKe" };

    private static readonly string[] Esac8bOptional = { "ac8bTrS", "ac8bSatE1", "ac8bSatSe1", "ac8bSatE2", "ac8bSatSe2", "ac8bKc", "ac8bKdExc" };

    private static readonly string[] Esst3aRequired = { "st3aViMaxPu", "st3aViMinPu", "st3aKm", "st3aTcS", "st3aTbS", "st3aKa", "st3aTaS",
        "st3aVrMaxPu", "st3aVrMinPu", "st3aKg", "st3aKp", "st3aKi", "st3aVbMaxPu", "st3aKc", "st3aXlPu", "st3aVgMaxPu", "st3aThetapDeg", "st3aTmS",
        "st3aVmMaxPu", "st3aVmMinPu" };

    private static readonly string[] Ieeeg3Required = { "ieeeg3TgS", "ieeeg3TpS", "ieeeg3UoPuPerS", "ieeeg3UcPuPerS", "valveMaxPu", "valveMinPu",
        "droopPu", "ieeeg3TransientDroopPu", "ieeeg3TrS", "ieeeg3TwS" };

    private static readonly string[] Ieeeg3Optional = { "ieeeg3A11", "ieeeg3A13", "ieeeg3A21", "ieeeg3A23" };

    private static readonly string[] Pss2Common = { "pss2Input1", "pss2Input2", "pss2M", "pss2N", "pss2Tw1S", "pss2Tw2S", "pss2T6S", "pss2Tw3S",
        "pss2Tw4S", "pss2T7S", "pss2Ks2", "pss2Ks3", "pss2T8S", "pss2T9S", "pss2Ks1", "pss2T1S", "pss2T2S", "pss2T3S", "pss2T4S" };

    /// <summary>The required parameters of each controller model (besides <c>model</c>).</summary>
    public static IReadOnlyList<string> RequiredFor(string model) => model switch
    {
        "SEXS" => new[] { "sexsLeadS", "sexsLagS", "sexsGain", "sexsExciterTimeS" },
        "IEEET1" => new[] { "ieeet1RegulatorGain", "ieeet1RegulatorTimeS", "ieeet1ExciterConstant", "ieeet1ExciterTimeS", "ieeet1FeedbackGain", "ieeet1FeedbackTimeS" },
        "ESST4B" => new[] { "st4bKpr", "st4bKir", "st4bVrMaxPu", "st4bVrMinPu", "st4bTaS", "st4bKpm", "st4bKim", "st4bVmMaxPu", "st4bVmMinPu", "st4bKg", "st4bKp", "st4bKi", "st4bVbMaxPu", "st4bKc", "st4bXlPu", "st4bThetapDeg" },
        "EXAC1" => new[] { "acLeadS", "acLagS", "acRegulatorGain", "acRegulatorTimeS", "acRegulatorMinPu", "acRegulatorMaxPu", "acExciterTimeS", "acFeedbackGain", "acFeedbackTimeS", "acRectifierKc", "acDemagnetisingKd", "acExciterConstant", "acSatE1", "acSatSe1", "acSatE2", "acSatSe2" },
        "ESAC1A" => new[] { "acLeadS", "acLagS", "acRegulatorGain", "acRegulatorTimeS", "acRegulatorMinPu", "acRegulatorMaxPu", "acExciterTimeS", "acFeedbackGain", "acFeedbackTimeS", "acRectifierKc", "acDemagnetisingKd", "acExciterConstant", "acSatE1", "acSatSe1", "acSatE2", "acSatSe2", "acOutputMinPu", "acOutputMaxPu" },
        "PSS1A" => new[] { "gain", "washoutS", "lead1S", "lag1S", "lead2S", "lag2S" },
        "TGOV1" => new[] { "droopPu", "tgov1ValveTimeS", "tgov1ReheatLeadS", "tgov1ReheatLagS" },
        "IEEEG1" => new[] { "droopPu", "ieeeg1LagS", "ieeeg1LeadS", "ieeeg1ValveTimeS", "ieeeg1ChestS", "ieeeg1ReheatS", "ieeeg1CrossoverS", "ieeeg1LpS" },
        "HYGOV" => new[] { "droopPu", "hygovTransientDroopPu", "hygovResetTimeS", "hygovGateTimeS", "hygovWaterTimeS" },
        "GAST" => new[] { "droopPu", "gastValveTimeS", "gastTurbineTimeS", "gastExhaustTimeS", "gastAmbientLimitPu", "gastTemperatureGain" },
        "EXST1" => St1Common,
        "ESST1A" => St1Common.Concat(new[] { "st1aTc1S", "st1aTb1S", "st1aVaMaxPu", "st1aVaMinPu", "st1aKlr", "st1aIlrPu" }).ToArray(),
        "SCRX" => new[] { "scrxLeadS", "scrxLagS", "scrxGain", "scrxExciterTimeS", "scrxFieldMinPu", "scrxFieldMaxPu", "scrxSourceSwitch" },
        "ESDC1A" or "EXDC2" or "IEEEX1" => new[] { "dc1aKa", "dc1aTaS", "dc1aTbS", "dc1aTcS", "dc1aVrMaxPu", "dc1aVrMinPu", "dc1aKe", "dc1aTeS", "dc1aKf", "dc1aTf1S" },
        "ESAC8B" => Esac8bRequired,
        "ESST3A" => Esst3aRequired,
        "PSS2C" => Pss2Common.Concat(new[] { "pss2bT10S", "pss2bT11S", "pss2cT12S", "pss2cT13S" }).ToArray(),
        "IEEEG3" => Ieeeg3Required,
        "IEEEST" => new[] { "ieeestInput", "ieeestT1S", "ieeestT2S", "ieeestT3S", "ieeestT4S", "ieeestT5S", "ieeestT6S", "ieeestKs" },
        "PSS2A" => Pss2Common,
        "PSS2B" => Pss2Common.Concat(new[] { "pss2bT10S", "pss2bT11S" }).ToArray(),
        "GGOV1" => Ggov1Required,
        "WSIEG1" => Wsieg1Required,
        _ => throw new ArgumentOutOfRangeException(nameof(model), model, "not a per-machine controller model of a dynamics file"),
    };

    /// <summary>The optional parameters of each controller model.</summary>
    public static IReadOnlyList<string> OptionalFor(string model) => model switch
    {
        "SEXS" => new[] { "sexsFieldMinPu", "sexsFieldMaxPu" },
        "IEEET1" => new[] { "ieeet1RegulatorMinPu", "ieeet1RegulatorMaxPu", "ieeet1SaturationA", "ieeet1SaturationB" },
        "EXAC1" or "ESAC1A" or "ESST4B" => Array.Empty<string>(),
        "PSS1A" => new[] { "outputMinPu", "outputMaxPu" },
        "TGOV1" => new[] { "turbineDampingPu", "valveMinPu", "valveMaxPu" },
        "IEEEG1" => new[] { "ieeeg1K1", "ieeeg1K2", "ieeeg1K3", "ieeeg1K4", "ieeeg1K5", "ieeeg1K6", "ieeeg1K7", "ieeeg1K8",
            "valveMinPu", "valveMaxPu", "ieeeg1UoPuPerS", "ieeeg1UcPuPerS" },
        "HYGOV" => new[] { "valveMinPu", "valveMaxPu" },
        "GAST" => new[] { "valveMinPu", "valveMaxPu", "turbineDampingPu" },
        "EXST1" => new[] { "st1TrS" },
        "ESST1A" => new[] { "st1TrS", "st1aVos" },
        "SCRX" => new[] { "scrxCrowbarRatio" },
        "ESDC1A" or "EXDC2" or "IEEEX1" => new[] { "dc1aTrS", "dc1aSatE1", "dc1aSatSe1", "dc1aSatE2", "dc1aSatSe2" },
        "ESAC8B" => Esac8bOptional,
        "ESST3A" => new[] { "st3aTrS" },
        "PSS2C" => new[] { "pss2OutputMaxPu", "pss2OutputMinPu", "pss2bInput1MaxPu", "pss2bInput1MinPu", "pss2bInput2MaxPu", "pss2bInput2MinPu",
            "pss2cPssOnPu", "pss2cPssOffPu" },
        "IEEEG3" => Ieeeg3Optional,
        "IEEEST" => new[] { "ieeestA1S", "ieeestA3S", "ieeestA5S", "ieeestOutputMaxPu", "ieeestOutputMinPu", "ieeestVcuPu", "ieeestVclPu" },
        "PSS2A" => new[] { "pss2OutputMaxPu", "pss2OutputMinPu" },
        "PSS2B" => new[] { "pss2OutputMaxPu", "pss2OutputMinPu", "pss2bInput1MaxPu", "pss2bInput1MinPu", "pss2bInput2MaxPu", "pss2bInput2MinPu" },
        "GGOV1" => Ggov1Optional,
        "WSIEG1" => Wsieg1Optional,
        _ => throw new ArgumentOutOfRangeException(nameof(model), model, "not a per-machine controller model of a dynamics file"),
    };

    private sealed record Tpl(string Ref, string Kind, bool Placeholder, JsonElement Params, JsonElement Provenance, JsonElement? Controllers, string Pointer);

    /// <summary>
    /// Read and validate <paramref name="path"/> against the v2 schemas in <paramref name="schemasDir"/> (with the kind
    /// specs in its <c>kinds</c> folder), and bind it to <paramref name="model"/>'s generators. Throws
    /// <see cref="InvalidDataException"/> listing every problem when anything is wrong; nothing is half-loaded.
    /// </summary>
    public static MachineDynamicsSet Load(string path, string schemasDir, GridModel model)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(schemasDir);
        ArgumentNullException.ThrowIfNull(model);
        if (!File.Exists(path)) throw new FileNotFoundException($"Machine dynamic data file not found: {path}", path);
        string shown = System.IO.Path.GetFileName(path);

        // Layer 1 and 2: the schema and the templates as one library (V2DocumentValidator).
        V2DocumentReport report = V2DocumentValidator.ValidateFiles(new[] { path }, schemasDir)[0];
        if (report.SchemaTag != V2DocumentValidator.DynamicsTag)
            throw new InvalidDataException($"{shown}: not a machine dynamic data file: its \"schema\" is '{report.SchemaTag ?? "(none)"}', "
                + $"expected '{V2DocumentValidator.DynamicsTag}'." + (report.Errors.Count > 0 ? " " + report.Errors[0] : ""));
        SchemaValidator.ThrowIfAny(report.Errors, shown);

        using JsonDocument doc = JsonSchemaSubset.ParseFile(path);
        JsonElement root = doc.RootElement;
        var problems = new List<string>();

        // Every template by id@rev.
        var templates = new Dictionary<string, Tpl>(StringComparer.Ordinal);
        int ti = 0;
        foreach (JsonElement t in root.GetProperty("templates").EnumerateArray())
        {
            string r = t.GetProperty("id").GetString() + "@" + ((long)t.GetProperty("rev").GetDouble()).ToString(CultureInfo.InvariantCulture);
            templates[r] = new Tpl(r, t.GetProperty("kind").GetString()!, t.GetProperty("status").GetString() == "placeholder",
                t.GetProperty("params"), t.GetProperty("provenance"),
                t.TryGetProperty("controllers", out JsonElement c) ? c : (JsonElement?)null, "/templates/" + ti.ToString(CultureInfo.InvariantCulture));
            ti++;
        }

        // Layer 3: the bindings and the model-specific parameter rules.
        var entries = new List<MachineDynamicsEntry>();
        var bound = new Dictionary<int, int>();   // generator index -> machines entry index
        int mi = 0;
        foreach (JsonElement m in root.GetProperty("machines").EnumerateArray())
        {
            string at = $"{shown}: /machines/{mi.ToString(CultureInfo.InvariantCulture)}";
            int entryIndex = mi++;
            int? gi = ResolveGenerator(m, model, at, problems, out string label);
            string tref = m.GetProperty("template").GetString()!;
            if (!templates.TryGetValue(tref, out Tpl? mt))
            {
                problems.Add($"{at}/template: {tref} is not a template of this file.");
                continue;
            }
            if (mt.Kind != "synchronousMachine")
            {
                problems.Add($"{at}/template: {tref} is a '{mt.Kind}' template; a machine binds a 'synchronousMachine' template.");
                continue;
            }
            if (mt.Placeholder)
            {
                problems.Add($"{at}/template: {tref} is a placeholder (every value null); a machine needs an active template.");
                continue;
            }
            if (gi is not int g) continue;
            if (!bound.TryAdd(g, entryIndex))
            {
                problems.Add($"{at}: generator {label} is already bound by /machines/{bound[g].ToString(CultureInfo.InvariantCulture)}; each unit takes one entry.");
                continue;
            }
            Generator gen = model.Generators[g];
            if (gen.PowerFlowOnly || gen.IsConverterInterfaced)
            {
                problems.Add($"{at}: generator {label} is {(gen.PowerFlowOnly ? "a power-flow-only unit" : "converter plant")}, which the RMS "
                    + "study does not integrate as a synchronous machine; its machine data would be ignored.");
                continue;
            }
            MachineDynamicsEntry? e = BuildEntry(g, label, mt, templates, gen, $"{shown}: {mt.Pointer}", problems);
            if (e is not null) entries.Add(e);
        }

        if (problems.Count > 0)
            throw new InvalidDataException($"{shown}: {problems.Count} problem(s) in the machine dynamic data:" + Environment.NewLine + "  "
                + string.Join(Environment.NewLine + "  ", problems));
        return new MachineDynamicsSet(root.GetProperty("name").GetString()!, path, entries);
    }

    // The generator a machines entry names: by name (exactly one unit of that name), or by bus (+ unit index).
    private static int? ResolveGenerator(JsonElement m, GridModel model, string at, List<string> problems, out string label)
    {
        IReadOnlyList<Generator> gens = model.Generators;
        if (m.TryGetProperty("generator", out JsonElement nameEl))
        {
            string name = nameEl.GetString()!;
            label = $"'{name}'";
            if (m.TryGetProperty("unit", out _)) problems.Add($"{at}/unit: 'unit' goes with 'bus', not with 'generator'.");
            var hits = new List<int>();
            for (int i = 0; i < gens.Count; i++) if (string.Equals(gens[i].Name, name, StringComparison.Ordinal)) hits.Add(i);
            if (hits.Count == 1) return hits[0];
            problems.Add(hits.Count == 0
                ? $"{at}/generator: the case has no in-service generator named '{name}'."
                : $"{at}/generator: {hits.Count} generators of the case are named '{name}'; name the unit by bus and unit index instead.");
            return null;
        }
        int bus = JsonWhole.Int(m.GetProperty("bus"), "bus");
        var atBus = new List<int>();
        for (int i = 0; i < gens.Count; i++) if (gens[i].Bus == bus) atBus.Add(i);
        if (m.TryGetProperty("unit", out JsonElement u))
        {
            int unit = JsonWhole.Int(u, "unit");
            label = $"at bus {bus.ToString(CultureInfo.InvariantCulture)} unit {unit.ToString(CultureInfo.InvariantCulture)}";
            // Bug hunt 2026-10-29, finding 43: 'unit' counts the bus's units in CASE order, out-of-service units included,
            // as the message and the author's file (generation.json / the network file) read. It used to count the
            // in-service units only, so an out-of-service unit earlier at the same bus silently shifted the binding to
            // the next machine. The case-order unit is mapped to its in-service position, and a unit that is out of
            // service is refused by name (it has no machine to bind). With every unit in service the two counts are
            // the same, so such a case binds exactly as before.
            IReadOnlyList<Generator> all = model.AllGenerators;
            var allAtBus = new List<int>();
            for (int j = 0; j < all.Count; j++) if (all[j].Bus == bus) allAtBus.Add(j);
            if (unit < 0 || unit >= allAtBus.Count)
            {
                problems.Add($"{at}/unit: bus {bus.ToString(CultureInfo.InvariantCulture)} has {allAtBus.Count} unit(s) in the case, so there is no unit {unit.ToString(CultureInfo.InvariantCulture)}.");
                return null;
            }
            int target = allAtBus[unit];
            if (!all[target].InService)
            {
                problems.Add($"{at}/unit: unit {unit.ToString(CultureInfo.InvariantCulture)} at bus {bus.ToString(CultureInfo.InvariantCulture)} is out of service, so it has no machine to bind dynamics to.");
                return null;
            }
            int inServiceIndex = 0;
            for (int j = 0; j < target; j++) if (all[j].InService) inServiceIndex++;
            if (inServiceIndex >= gens.Count || gens[inServiceIndex].Bus != bus)
                throw new InvalidOperationException($"{at}/unit: the in-service fleet is not the case fleet in order (internal error).");
            return inServiceIndex;
        }
        label = $"at bus {bus.ToString(CultureInfo.InvariantCulture)}";
        if (atBus.Count == 1) return atBus[0];
        problems.Add(atBus.Count == 0
            ? $"{at}/bus: the case has no in-service generator at bus {bus.ToString(CultureInfo.InvariantCulture)}."
            : $"{at}/bus: bus {bus.ToString(CultureInfo.InvariantCulture)} has {atBus.Count} units; give 'unit' (0-based, in case order) to say which.");
        return null;
    }

    // One machine: its template's parameters, its controllers, the provenance behind them.
    private static MachineDynamicsEntry? BuildEntry(int g, string label, Tpl mt, Dictionary<string, Tpl> templates, Generator gen,
        string at, List<string> problems)
    {
        int before = problems.Count;
        var tags = new SortedSet<ProvenanceTag>();
        var notes = new List<string>();
        // WP3a (RC5): the machine model (absent: GENROU) picks the parameter lists.
        string machineModel = "GENROU";
        if (mt.Params.TryGetProperty("model", out JsonElement mmv))
        {
            machineModel = Value(mmv).GetString()!;
            if (!MachineModels.Contains(machineModel, StringComparer.Ordinal))
            {
                problems.Add($"{at}/params/model: '{machineModel}' is not a machine model of a dynamics file (allowed: {string.Join(", ", MachineModels)}).");
                return null;
            }
        }
        bool classical = machineModel == "GENCLS";
        Dictionary<string, double> mp = classical
            ? Numbers(mt, GenclsRequired, GenclsOptional, at, problems, tags)
            : Numbers(mt, MachineRequired, machineModel == "GENTPJ" ? MachineOptional.Concat(GentpjOptional).ToArray() : MachineOptional, at, problems, tags);

        GovernorData? governor = null;
        ExciterData? exciter = null;
        Dictionary<string, double>? pss = null;
        string pssModel = "PSS1A";
        string? exciterModel = null;
        Dictionary<string, double>? exciterParams = null;
        if (mt.Controllers is JsonElement ctl)
        {
            foreach (JsonProperty slot in ctl.EnumerateObject())
            {
                string cref = slot.Value.GetString()!;
                Tpl ct = templates[cref];   // resolved and of the slot's kind: checked by the library layer
                string cat = $"{at.Split(':')[0]}: {ct.Pointer}";
                switch (slot.Name)
                {
                    case "exciter":
                        exciterModel = Model(ct, cat, problems, ExciterModels.ToArray());
                        if (exciterModel is not null) exciterParams = Numbers(ct, RequiredFor(exciterModel), OptionalFor(exciterModel), cat, problems, tags);
                        break;
                    case "pss":
                        string? pm = Model(ct, cat, problems, StabiliserModels.ToArray());
                        if (pm is not null) { pss = Numbers(ct, RequiredFor(pm), OptionalFor(pm), cat, problems, tags); pssModel = pm; }
                        break;
                    case "governor":
                        string? gm = Model(ct, cat, problems, GovernorModels.ToArray());
                        if (gm is not null)
                        {
                            Dictionary<string, double> gp = Numbers(ct, RequiredFor(gm), OptionalFor(gm), cat, problems, tags);
                            if (problems.Count == before) governor = Governor(gm, gp, cat, problems, gen.MBase);
                        }
                        break;
                }
            }
        }
        if (pss is not null && exciterModel is null)
            problems.Add($"{at}/controllers: a pss is given with no exciter; the stabiliser's output joins the AVR summing junction, so it needs one.");
        if (classical && exciterModel is not null)
            problems.Add($"{at}/controllers: a GENCLS machine holds a constant voltage behind X'd and has no field to drive, so it takes no exciter or stabiliser.");
        if (problems.Count > before) return null;

        // Inertia: the template's, else the case's own (case data, said in the notes).
        double h;
        if (mp.TryGetValue("inertiaH", out double th)) h = th;
        else
        {
            h = gen.H;
            notes.Add($"inertia H = {gen.H.ToString("R", CultureInfo.InvariantCulture)} s from the case (the template gives none)");
        }
        if (!(h > 0.0))
        {
            problems.Add($"{at}: generator {label} has no inertia (H = {h.ToString("R", CultureInfo.InvariantCulture)} s), so the RMS study "
                + "cannot integrate it as a machine and its data would be ignored; give inertiaH or drop the binding.");
            return null;
        }

        if (classical)
        {
            // WP3a (RC5): the classical machine, every reactance at X'd and the flux states held (Gencls).
            try { return new MachineDynamicsEntry(g, label, mt.Ref, Gencls.Data(h, mp.GetValueOrDefault("damping", 0.0), mp["xdpPu"], governor), null, tags.ToList(), notes); }
            catch (ArgumentOutOfRangeException ex) { problems.Add($"{at}/params: {ex.Message}"); return null; }
        }

        SubtransientMachine.SaturationCurve? sat = null;
        bool s10 = mp.TryGetValue("saturationS10", out double sa10), s12 = mp.TryGetValue("saturationS12", out double sa12);
        if (s10 != s12)
            problems.Add($"{at}/params: saturationS10 and saturationS12 go together (the curve is fitted through both points).");
        else if (s10)
        {
            try { sat = new SubtransientMachine.SaturationCurve(sa10, sa12); }
            catch (ArgumentOutOfRangeException ex) { problems.Add($"{at}/params: saturation: {ex.Message}"); }
            // R7-06 (ruling 15, DYN-02): said here as the DYR reader says it.
            if (sat is not null && sa10 == 0 && sa12 > 0)
                notes.Add("saturationS10 is 0 and saturationS12 above 0: read as the curve that starts at 1.0 pu (A = 1, B through the 1.2 point)");
        }
        double xl = mp.GetValueOrDefault("xlPu", 0.0);
        if (!(xl < mp["xdppPu"])) problems.Add($"{at}/params/xlPu: the leakage reactance must be below X''d ({mp["xdppPu"].ToString("R", CultureInfo.InvariantCulture)}).");
        if (problems.Count > before) return null;

        double kis = mp.GetValueOrDefault("saturationKis", 0.0);
        if (kis != 0.0 && sat is null)
            problems.Add($"{at}/params/saturationKis: Kis scales the saturation input, so it needs saturationS10 and saturationS12.");
        if (problems.Count > before) return null;

        if (exciterModel is not null) exciter = new ExciterData(exciterModel, exciterParams!, pss) { PssModel = pssModel };
        // R7-06 (ruling 15, DYN-02): an exciter with exactly one saturation SE of 0 is read as the curve that starts at that
        // point; said here as the DYR reader says it.
        if (exciterModel is not null && SaturationStartsAtZeroPoint(exciterParams!))
            notes.Add($"exciter {exciterModel}: one saturation point has SE 0, read as the curve that starts there (A = E of that point, B through the other point)");
        var data = new SubtransientInit.MachineData(h, mp.GetValueOrDefault("damping", 0.0),
            mp["xdPu"], mp["xdpPu"], mp["xdppPu"], mp["xqPu"], mp["xqpPu"], mp["xqppPu"],
            mp["tdopS"], mp["tdoppS"], mp["tqopS"], mp["tqoppS"], sat, governor, xl) { SaturationKis = kis };
        // Fail loud on the exciter's own constructor checks now, not at study time.
        if (exciter is not null)
        {
            try { exciter.Create(); }
            catch (ArgumentException ex) { problems.Add($"{at}/controllers/exciter: {ex.Message}"); return null; }
        }
        return new MachineDynamicsEntry(g, label, mt.Ref, data, exciter, tags.ToList(), notes);
    }

    // R7-06: true when the exciter's two saturation points (the parameters whose names end in Se1 and Se2) are both given and
    // exactly one is 0, as PsseDyrReader.SaturationStartsAtZeroPoint reads a DYR record.
    private static bool SaturationStartsAtZeroPoint(IReadOnlyDictionary<string, double> p)
    {
        double? se1 = null, se2 = null;
        foreach (KeyValuePair<string, double> kv in p)
        {
            if (kv.Key.EndsWith("Se1", StringComparison.OrdinalIgnoreCase)) se1 = kv.Value;
            else if (kv.Key.EndsWith("Se2", StringComparison.OrdinalIgnoreCase)) se2 = kv.Value;
        }
        return se1 is double a && se2 is double b && (a == 0) != (b == 0);
    }

    // The 'model' of a controller template, which must be one of the allowed per-machine models.
    private static string? Model(Tpl t, string at, List<string> problems, string[] allowed)
    {
        if (!t.Params.TryGetProperty("model", out JsonElement mv))
        {
            problems.Add($"{at}/params: a {t.Kind} template used by a machine must say its 'model' ({string.Join(", ", allowed)}).");
            return null;
        }
        string model = Value(mv).GetString()!;
        if (!allowed.Contains(model, StringComparer.Ordinal))
        {
            problems.Add($"{at}/params/model: '{model}' is not a per-machine {t.Kind} model of a dynamics file (allowed: {string.Join(", ", allowed)})"
                + (model == "lumped" ? "; the lumped governor is the frequency model's centre-of-inertia response, not a machine's." : "."));
            return null;
        }
        return model;
    }

    // The numeric parameters of a template: every required one present, nothing outside required + optional (+ model),
    // each value read from its bare or object form, its provenance tag collected.
    private static Dictionary<string, double> Numbers(Tpl t, IReadOnlyList<string> required, IReadOnlyList<string> optional,
        string at, List<string> problems, SortedSet<ProvenanceTag> tags)
    {
        var values = new Dictionary<string, double>(StringComparer.Ordinal);
        tags.Add(Tag(t.Provenance.GetProperty("_default")));
        foreach (JsonProperty p in t.Params.EnumerateObject())
        {
            if (p.Name == "model") continue;
            if (!required.Contains(p.Name, StringComparer.Ordinal) && !optional.Contains(p.Name, StringComparer.Ordinal))
            {
                problems.Add($"{at}/params/{p.Name}: not read for this {t.Kind} in a dynamics file"
                    + (t.Kind == "synchronousMachine" ? " (case data such as unitTransformerXPu or fuelType stays in the case; the GENROU synthesis ratios are for synthesis only)" : " (it belongs to another model)")
                    + "; a number that would be ignored is refused.");
                continue;
            }
            JsonElement v = Value(p.Value);
            values[p.Name] = v.GetDouble();
            if (p.Value.ValueKind == JsonValueKind.Object && p.Value.TryGetProperty("prov", out JsonElement prov)) tags.Add(Tag(prov));
            if (t.Provenance.TryGetProperty(p.Name, out JsonElement pp)) tags.Add(Tag(pp));
        }
        foreach (string r in required)
            if (!values.ContainsKey(r)) problems.Add($"{at}/params: '{r}' is required for this {t.Kind} and is missing.");
        return values;
    }

    // The governor data object of a model, constructed (its constructor checks the values).
    private static GovernorData? Governor(string model, Dictionary<string, double> p, string at, List<string> problems, double machineBaseMva)
    {
        double Opt(string k, double d) => p.GetValueOrDefault(k, d);
        // WP3b: an integer flag given as a number must be a whole number (the schema types it integer; checked again here).
        int Int(string k, int d) => p.TryGetValue(k, out double v) ? (v == Math.Floor(v) && Math.Abs(v) < 1e9 ? (int)v : int.MinValue) : d;
        GovernorData gd;
        try
        {
        gd = model switch
        {
            "GGOV1" => new Ggov1Data(Int("ggov1Rselect", 1), Int("ggov1Flag", 1), p["droopPu"], p["ggov1TpelecS"], p["ggov1MaxErrPu"], p["ggov1MinErrPu"],
                p["ggov1Kpgov"], p["ggov1Kigov"], p["ggov1Kdgov"], p["ggov1TdgovS"], p["valveMaxPu"], p["valveMinPu"], p["ggov1TactS"], p["ggov1Kturb"],
                p["ggov1WfnlPu"], p["ggov1TbS"], p["ggov1TcS"], Opt("ggov1TengS", 0.0), p["ggov1TfloadS"], p["ggov1Kpload"], p["ggov1Kiload"],
                p["ggov1LdrefPu"], Opt("turbineDampingPu", 0.0), p["ggov1RopenPuPerS"], p["ggov1RclosePuPerS"], Opt("ggov1Kimw", 0.0),
                Opt("ggov1AsetPuPerS", 0.0), Opt("ggov1Ka", 0.0), Opt("ggov1TaS", 0.0), Opt("ggov1TrateMw", 0.0), Opt("ggov1DbPu", 0.0),
                Opt("ggov1TsaS", 0.0), Opt("ggov1TsbS", 0.0), Opt("ggov1RupPuPerS", 99.0), Opt("ggov1RdownPuPerS", -99.0), machineBaseMva),
            "WSIEG1" => new Wsieg1Data(p["wsieg1K"], p["wsieg1T1S"], p["wsieg1T2S"], p["wsieg1T3S"], p["wsieg1UoPuPerS"], p["wsieg1UcPuPerS"],
                p["valveMaxPu"], p["valveMinPu"], p["wsieg1T4S"], Opt("wsieg1K1", 1.0), Opt("wsieg1K2", 0.0), p["wsieg1T5S"], Opt("wsieg1K3", 0.0),
                Opt("wsieg1K4", 0.0), p["wsieg1T6S"], Opt("wsieg1K5", 0.0), Opt("wsieg1K6", 0.0), p["wsieg1T7S"], Opt("wsieg1K7", 0.0),
                Opt("wsieg1K8", 0.0), Opt("wsieg1Db1Pu", 0.0), Opt("wsieg1ErrPu", 0.0), Opt("wsieg1Db2Pu", 0.0),
                new[] { Opt("wsieg1Gv1", 0.0), Opt("wsieg1Gv2", 0.0), Opt("wsieg1Gv3", 0.0), Opt("wsieg1Gv4", 0.0), Opt("wsieg1Gv5", 0.0) },
                new[] { Opt("wsieg1Pgv1", 0.0), Opt("wsieg1Pgv2", 0.0), Opt("wsieg1Pgv3", 0.0), Opt("wsieg1Pgv4", 0.0), Opt("wsieg1Pgv5", 0.0) },
                Int("wsieg1Iblock", 0)),
            "IEEEG3" => new Ieeeg3Data(p["ieeeg3TgS"], p["ieeeg3TpS"], p["ieeeg3UoPuPerS"], p["ieeeg3UcPuPerS"], p["valveMaxPu"], p["valveMinPu"],
                p["droopPu"], p["ieeeg3TransientDroopPu"], p["ieeeg3TrS"], p["ieeeg3TwS"], Opt("ieeeg3A11", 0.5), Opt("ieeeg3A13", 1.0),
                Opt("ieeeg3A21", 1.5), Opt("ieeeg3A23", 1.0)),
            "TGOV1" => new Tgov1Data(p["droopPu"], p["tgov1ValveTimeS"], p["tgov1ReheatLeadS"], p["tgov1ReheatLagS"],
                Opt("turbineDampingPu", 0.0), Opt("valveMinPu", 0.0), Opt("valveMaxPu", double.PositiveInfinity)),
            "IEEEG1" => new Ieeeg1Data(p["droopPu"], p["ieeeg1LeadS"], p["ieeeg1LagS"], p["ieeeg1ValveTimeS"],
                p["ieeeg1ChestS"], p["ieeeg1ReheatS"], p["ieeeg1CrossoverS"], p["ieeeg1LpS"],
                Opt("ieeeg1K1", 1.0), Opt("ieeeg1K3", 0.0), Opt("ieeeg1K5", 0.0), Opt("ieeeg1K7", 0.0),
                Opt("valveMinPu", 0.0), Opt("valveMaxPu", double.PositiveInfinity),
                Opt("ieeeg1K2", 0.0), Opt("ieeeg1K4", 0.0), Opt("ieeeg1K6", 0.0), Opt("ieeeg1K8", 0.0),
                Opt("ieeeg1UoPuPerS", double.PositiveInfinity), Opt("ieeeg1UcPuPerS", double.NegativeInfinity)),
            "GAST" => new GastData(p["droopPu"], p["gastValveTimeS"], p["gastTurbineTimeS"], p["gastExhaustTimeS"],
                p["gastAmbientLimitPu"], p["gastTemperatureGain"], Opt("valveMinPu", 0.0), Opt("valveMaxPu", double.PositiveInfinity),
                Opt("turbineDampingPu", 0.0)),
            _ => new HydroGovernorData(p["droopPu"], p["hygovTransientDroopPu"], p["hygovResetTimeS"], p["hygovGateTimeS"],
                p["hygovWaterTimeS"], Opt("valveMinPu", 0.0), Opt("valveMaxPu", double.PositiveInfinity)),
        };
        }
        catch (ArgumentException ex) { problems.Add($"{at}/params: {model}: {ex.Message}"); return null; }
        try { gd.Create(); }   // the model constructors' checks (droop > 0, fractions summing to 1, rate signs, ...)
        catch (ArgumentException ex) { problems.Add($"{at}/params: {model}: {ex.Message}"); return null; }
        return gd;
    }

    // A value's bare form or the v of its object form.
    private static JsonElement Value(JsonElement v) => v.ValueKind == JsonValueKind.Object ? v.GetProperty("v") : v;

    private static ProvenanceTag Tag(JsonElement provenance) => Enum.Parse<ProvenanceTag>(provenance.GetProperty("tag").GetString()!);
}
