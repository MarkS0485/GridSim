using System.Globalization;
using System.Text;
using System.Text.Json;
using GridSim.Core.Model;

namespace GridSim.Core.IO.Psse;

/// <summary>One record of a PSS/E DYR file as written: bus, model name, machine ID and the values after them.</summary>
/// <param name="Bus">IBUS.</param>
/// <param name="Model">The model name (for example GENROU), upper case.</param>
/// <param name="Id">The machine ID, trimmed.</param>
/// <param name="Values">The fields after the ID: integer parameters (ICONs) first where the model has them, then CONs.</param>
/// <param name="Line">The 1-based line the record starts on.</param>
public sealed record PsseDyrRecord(int Bus, string Model, string Id, IReadOnlyList<string> Values, int Line);

/// <summary>WP6 (RC5): a DC line model record of a PSS/E DYR file (<c>'DCNAME' 'MODEL' values... /</c>), read only with the
/// extended library: the quoted DC line name, the model name and the values after it.</summary>
/// <param name="DcName">The DC line name, trimmed.</param>
/// <param name="Model">The model name (for example CDC4T), upper case.</param>
/// <param name="Values">The fields after the model name.</param>
/// <param name="Line">The 1-based line the record starts on.</param>
public sealed record PsseDyrDcRecord(string DcName, string Model, IReadOnlyList<string> Values, int Line);

/// <summary>How <see cref="PsseDyrReader"/> treats data GridSim cannot honour exactly.</summary>
public sealed record PsseDyrOptions
{
    /// <summary>
    /// False (the default): a record of a model this reader does not map, a value GridSim's model has no place for
    /// (a non-zero transducer lag, a cross-compound shaft, a PSS filter, the HYGOV turbine gain, a renewable value out of range, ...)
    /// or a controller with no machine model refuses the file, listing every one. True: each is left out and listed in
    /// the notes, so the rest of the data can be used.
    /// </summary>
    public bool AllowUnmodelled { get; init; }

    /// <summary>The file name the provenance notes name (the DYR file's name). Null: "the DYR file".</summary>
    public string? SourceName { get; init; }

    /// <summary>
    /// WP3a (RC5): false (the default) maps the models of <see cref="PsseDyrReader.MappedModels"/> only, exactly as before.
    /// True also maps the extended library of <see cref="PsseDyrReader.ExtendedModels"/>: the GENCLS machine (GENTPJ is refused
    /// by name: its record order is not public), the
    /// ESST1A, EXST1, SCRX, ESAC1A and ESDC1A exciters and the IEEEST, PSS2A and PSS2B stabilisers (gridsim:
    /// <c>--dyr-extended</c>); WP19 adds the EXDC2, IEEEX1, ESAC8B and ESST3A exciters and the IEEEG3 governor, and refuses
    /// PSS2C, ESAC7B, HYGOV4 and GENQEC records by name (<see cref="PsseDyrReader.RefusedLibraryModels"/>).
    /// </summary>
    public bool ExtendedLibrary { get; init; }
}

/// <summary>The machine dynamic data a DYR file gives, as a <c>gridsim-dynamics/2</c> document, with the notes.</summary>
/// <param name="Json">The <c>gridsim-dynamics/2</c> document (UTF-8 JSON text) that <see cref="Json.MachineDynamicsLoader"/>
/// loads against the RAW case.</param>
/// <param name="Notes">What was derived, approximated or left out.</param>
/// <param name="Records">Every record of the file, in file order.</param>
/// <param name="Machines">How many machines the document binds.</param>
public sealed record PsseDyrResult(string Json, IReadOnlyList<string> Notes, IReadOnlyList<PsseDyrRecord> Records, int Machines)
{
    /// <summary>
    /// WECC (expansion): the converter units the DYR gives a WECC second-generation model chain (REGC_A with REEC_A,
    /// REEC_B or REEC_C), keyed by the unit's index in <c>model.Generators</c> (the in-service units, the index the RMS
    /// study's per-generator data uses). Empty when the file has none. These are not part of <see cref="Json"/> (the
    /// <c>gridsim-dynamics/2</c> document describes synchronous machines); the CLI carries them to the RMS study.
    /// </summary>
    public IReadOnlyDictionary<int, GridSim.Core.Dynamics.Models.Wecc.WeccPlant> Renewables { get; init; } =
        new Dictionary<int, GridSim.Core.Dynamics.Models.Wecc.WeccPlant>();

    /// <summary>WP3b (RC5): the dynamic loads the DYR gives (CIM5BL records, read with the extended library), in file order.
    /// Not part of <see cref="Json"/>; the CLI carries them to the RMS study (<see cref="GridSim.Core.Dynamics.Models.RmsLoadedMachineData"/>).</summary>
    public IReadOnlyList<GridSim.Core.Dynamics.Models.DynamicLoadData> DynamicLoads { get; init; } =
        Array.Empty<GridSim.Core.Dynamics.Models.DynamicLoadData>();

    /// <summary>WP6 (RC5): the SVC models the DYR gives units of the case (CSVGN1, read with the extended library), keyed by
    /// the unit's index in <c>model.Generators</c>. Not part of <see cref="Json"/>; the CLI carries them to the RMS study
    /// (<see cref="GridSim.Core.Dynamics.Models.SubtransientInit.MachineData.Facts"/>).</summary>
    public IReadOnlyDictionary<int, GridSim.Core.Dynamics.Models.RmsFactsData> Facts { get; init; } =
        new Dictionary<int, GridSim.Core.Dynamics.Models.RmsFactsData>();

    /// <summary>WP6 (RC5): the network device models the DYR gives (CDC4T DC line records, read with the extended library), in
    /// file order. Not part of <see cref="Json"/>; the CLI carries them to the RMS study
    /// (<see cref="GridSim.Core.Dynamics.Models.RmsLoadedMachineData.Devices"/>).</summary>
    public IReadOnlyList<GridSim.Core.Dynamics.Models.RmsNetworkDeviceData> NetworkDevices { get; init; } =
        Array.Empty<GridSim.Core.Dynamics.Models.RmsNetworkDeviceData>();

    /// <summary>WP6 (RC5): the DC line model records of the file (extended library only), in file order.</summary>
    public IReadOnlyList<PsseDyrDcRecord> DcRecords { get; init; } = Array.Empty<PsseDyrDcRecord>();
}

/// <summary>
/// Reads a PSS/E dynamic data (DYR) file and maps its records onto GridSim's machine dynamic data, written as a
/// <c>gridsim-dynamics/2</c> document bound to the case read from the matching RAW file (Tranche A, October 2026).
/// </summary>
/// <remarks>
/// <para><b>Records.</b> Free format: <c>IBUS 'MODEL' ID values... /</c>, over as many lines as needed, ended by a slash
/// (the rest of that line is ignored). Fields are separated by blanks or commas; <c>@!</c> heading lines are skipped.</para>
/// <para><b>What maps.</b> Machines: GENROU and GENSAL to the subtransient machine. Exciters: SEXS, IEEET1, EXAC1 and ESST4B (ESAC1A only from a gridsim-dynamics/2 file: its PSS/E CON order is not in public text). Stabiliser:
/// PSS1A. Governors: TGOV1, IEEEG1, HYGOV and GAST. Each value goes to the GridSim parameter of the same meaning; where GridSim
/// needs a value PSS/E does not give, or gives in another form, the derivation is exact and tagged DerivedFromRecorded
/// with a note (SEXS lead time = TA/TB x TB; IEEEG1 droop = 1 / K; GENSAL, which has no q-axis transient circuit,
/// X'q = Xq so that circuit carries nothing, and T'qo = T''qo, which then has no effect). Every other value is tagged
/// Recorded, naming the file and line.</para>
/// <para><b>Renewables</b> (WECC expansion): the WECC second-generation models REGC_A with REEC_A, REEC_B or REEC_C
/// (PSS/E REGCA1, REECA1, REECB1, REECCU1, their V33 user-model names, or the WECC names; see
/// <see cref="PsseDyrRenewables"/>) map onto a <see cref="GridSim.Core.Dynamics.Models.Wecc.WeccPlant"/> per unit, returned
/// in <see cref="PsseDyrResult.Renewables"/> for the RMS study (not in the JSON document). Every value is range-checked; a
/// unit with both a machine record and a renewable chain, an electrical control without its REGC_A, or an exciter,
/// stabiliser or governor on a renewable unit is refused.</para>
/// <para><b>What does not map is refused</b> unless <see cref="PsseDyrOptions.AllowUnmodelled"/> is set, and every case is
/// listed: models this reader does not know (the message lists each with its count and first line), renewable data with
/// a problem (above), and values a GridSim model has no
/// block for: IEEET1 with TR, SWITCH or KE = 0 (PSS/E then computes KE) or exciter saturation (PSS/E's curve is not
/// GridSim's exponential one); PSS1A with an input other than speed, a remote bus, the A1 / A2 filter, a washout with T5
/// not equal to T6, or the VCU / VCL cut-offs; IEEEG1 driving a second shaft (JBUS); HYGOV with a filter Tf, a gate
/// rate limit VELM, a turbine gain At other than 1, turbine damping or no-load flow; GENSAL saturation (PSS/E saturates
/// its d axis only); and a controller on a unit with no machine record. A record for a machine the RAW case does not
/// hold, or a second record of the same kind for one unit, is always refused.</para>
/// </remarks>
public static class PsseDyrReader
{
    /// <summary>The models this reader maps, by role.</summary>
    public static readonly IReadOnlyDictionary<string, string> MappedModels = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["GENROU"] = "machine", ["GENSAL"] = "machine", ["SEXS"] = "exciter", ["IEEET1"] = "exciter", ["EXAC1"] = "exciter", ["ESST4B"] = "exciter", ["PSS1A"] = "pss",
        ["TGOV1"] = "governor", ["IEEEG1"] = "governor", ["HYGOV"] = "governor", ["GAST"] = "governor",
    };

    /// <summary>WP3a (RC5): the models of the extended library, by role, mapped only with
    /// <see cref="PsseDyrOptions.ExtendedLibrary"/>. Record orders, each checked against two public sources: GENCLS (H D),
    /// EXST1, SCRX, ESAC1A, ESDC1A, ESST1A (ICONs UEL VOS), IEEEST (ICONs ICS IB), PSS2A and PSS2B (ICONs ICS1 REMBUS1 ICS2
    /// REMBUS2 M N) from the PSS/E model library data sheets (public copy, pdfcoffee.com/models-pdf-free.html) and from
    /// NREL PowerSystems.jl src/parsers/psse_dynamic_mapping.yaml (GENCLS, EXST1, SCRX, ESAC1A, ESDC1A, ESST1A, IEEEST) or
    /// the PowerWorld transient-model pages (ESST1A, ESAC1A, PSS2A, PSS2B, SCRX, IEEEST). GENTPJ's order is in neither, so a
    /// GENTPJ record is refused by name (a gridsim-dynamics/2 template carries it).</summary>
    public static readonly IReadOnlyDictionary<string, string> ExtendedModels = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["GENCLS"] = "machine", ["GENTPJ"] = "machine", ["ESST1A"] = "exciter", ["EXST1"] = "exciter", ["SCRX"] = "exciter",
        ["ESAC1A"] = "exciter", ["ESDC1A"] = "exciter", ["IEEEST"] = "pss", ["PSS2A"] = "pss", ["PSS2B"] = "pss",
        // WP3b (RC5). GGOV1 (ICONs Rselect Flag, CONs R Tpelec maxerr minerr Kpgov Kigov Kdgov Tdgov Vmax Vmin Tact Kturb Wfnl
        // Tb Tc Teng Tfload Kpload Kiload Ldref Dm Ropen Rclose Kimw Aset Ka Ta Trate db Tsa Tsb Rup Rdown): the PSS/E data
        // sheet 7.8 (public copy above), NREL PowerSystems.jl psse_dynamic_mapping.yaml GeneralGovModel (values 1 to 35 in this
        // order) and the PowerWorld GGOV1 page (the same names in the same order, Trate apart, which PowerWorld lists first as
        // PSLF does). WSIEG1 (JBUS M, then K T1 T2 T3 Uo Uc PMAX PMIN T4 K1 K2 T5 K3 K4 T6 K5 K6 T7 K7 K8 db1 err db2 GV1 PGV1 ..
        // GV5 PGV5 IBLOCK): the PSS/E data sheet 7.35 and the PowerWorld WSIEG1 page (the CONs in the same order).
        ["GGOV1"] = "governor", ["WSIEG1"] = "governor",
        // WP19 (RC5). Each record order is given in the same order by two public sources:
        // EXDC2 and IEEEX1 (TR KA TA TB TC VRMAX VRMIN KE TE KF TF1 SWITCH E1 SE(E1) E2 SE(E2)): the PSS/E 33 model library data
        //   sheets 6.30 and 6.44 (public copy above) and the CURENT ANDES andes/io/psse-dyr.yaml inputs lists (read for the
        //   order only); PowerWorld's IEEEX1 and EXDC2_PTI pages list the same names in the same order.
        // ESAC8B (TR KP KI KD TD KA TA VRMAX VRMIN TE KE E1 SE(E1) E2 SE(E2)): the PSS/E data sheet 6.15 and NREL PowerSystems.jl
        //   psse_dynamic_mapping.yaml (ESAC8B: values 1 to 15 onto its ESAC8B fields, VRMIN/VRMAX as (9, 8)); PowerWorld's
        //   ESAC8B_PTI page gives the same order.
        // ESST3A (TR VIMAX VIMIN KM TC TB KA TA VRMAX VRMIN KG KP KI VBMAX KC XL VGMAX THETAP TM VMMAX VMMIN): the PSS/E data
        //   sheet 6.20 and the ANDES psse-dyr.yaml inputs list.
        // IEEEG3 (TG TP Uo Uc PMAX PMIN sigma delta TR TW a11 a13 a21 a23): the PSS/E data sheet 7.15 and PowerWorld's
        //   "IEEEG3_PTI and IEEEG3D" page (Tg Tp Uo Uc Pmax Pmin Rperm Rtemp Tr Tw A11 A13 A21 A23).
        ["EXDC2"] = "exciter", ["IEEEX1"] = "exciter", ["ESAC8B"] = "exciter", ["ESST3A"] = "exciter", ["IEEEG3"] = "governor",
    };

    /// <summary>WP19 (RC5): the records the extended library refuses by name, with the reason: a model whose record order (or
    /// structure) is not given by two public sources. Without the extended library these are unmapped models, as before.</summary>
    public static readonly IReadOnlyDictionary<string, string> RefusedLibraryModels = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["PSS2C"] = "GridSim models PSS2C (a gridsim-dynamics/2 stabiliser template with model PSS2C), but its PSS/E record order is given by one public source only (the PowerWorld PSS2C page; the public PSS/E 33 model library predates the model), so it is not read with an unconfirmed order; write the stabiliser as a gridsim-dynamics/2 template",
        ["ESAC7B"] = "GridSim does not model ESAC7B: its PSS/E record order is given by one public source only (the PowerWorld AC7B and ESAC7B page; the public PSS/E 33 model library predates the model) and no public text found gives its block diagram, so it is not read",
        ["HYGOV4"] = "GridSim does not model HYGOV4: its parameter list is given by one public source only (the PowerWorld HYGOV4 page, whose block diagram is not in text; the public PSS/E 33 model library does not hold it), so it is not read",
        ["GENQEC"] = "GridSim does not model GENQEC: its record order and saturation function are not public, so it is not read",
    };

    /// <summary>WP3b (RC5): the dynamic load records the extended library reads (CIM5BL: I 'CIM5BL' LID IT, then RA XA XM R1
    /// X1 R2 X2 E1 SE(E1) E2 SE(E2) MBASE PMULT H VI TI TB D TNOM; the PSS/E data sheet 9.2 and the PowerWorld CIM5 page give
    /// this order) and the ones it refuses by name, with the reason.</summary>
    public static readonly IReadOnlyDictionary<string, string?> LoadModels = new Dictionary<string, string?>(StringComparer.Ordinal)
    {
        ["CIM5BL"] = null,
        ["CIMWBL"] = "the order of its last four values (A B D E) is given by one public source only (the PSS/E data sheet; PowerWorld's CIMW page says only that the model is CIM5's with another load torque), so it is not read with an unconfirmed order; give the motor through the API (InductionMotorLoadData with a polynomial load torque)",
        // WP20 part B (RC5): the CMLD parts are modelled through the API, so the reasons name what the DYR route still lacks.
        ["CMLDBLU1"] = "GridSim's CMLD is a documented subset built through the API (CompositeLoadData with its Extensions: motors A to D, the electronic and static loads, the relays, the LTC and DER_A distributed generation); CMLDBLU1 is the first CMLD version and is not read, so give the load through the API",
        ["CMLDBLU2"] = "GridSim's CMLD is a documented subset built through the API (CompositeLoadData with its Extensions: motors A to D, the electronic and static loads, the relays, the LTC and DER_A distributed generation); the record is not read because its value order is confirmed by one public source only (the PSS/E data sheet) and GridSim does not do the WECC initialisation it relies on (the substation shunt, the feeder compensation and the starting tap computed from the power flow are data in GridSim), so give the load through the API",
        ["CIM5OW"] = "the owner-wide form is not read; give one CIM5BL record per load",
        ["CIM5ZN"] = "the zone-wide form is not read; give one CIM5BL record per load",
        ["CIM5AR"] = "the area-wide form is not read; give one CIM5BL record per load",
        ["CIM5AL"] = "the system-wide form is not read; give one CIM5BL record per load",
    };

    /// <summary>WP6 (RC5): the SVC and STATCOM records the extended library reads on a unit (CSVGN1: IBUS 'CSVGN1' ID, then K T1
    /// T2 T3 T4 T5 RMIN VMAX VMIN CBASE, MBASE the machine's; the PowerWorld CSVGN1 page, NREL PowerSystems.jl's CSVGN1 and
    /// OpenIPSL's PSSE.CSVGN1 give these ten values in this order) and the ones it refuses by name, with the reason.</summary>
    public static readonly IReadOnlyDictionary<string, string?> FactsModels = new Dictionary<string, string?>(StringComparer.Ordinal)
    {
        ["CSVGN1"] = null,
        ["CSVGN3"] = UnconfirmedSvc,
        ["CSVGN4"] = UnconfirmedSvc,
        ["CSVGN5"] = UnconfirmedSvc,
        ["CSVGN6"] = UnconfirmedSvc,
        ["SVSMO1"] = "GridSim does not model the switched elements (TSC and MSS) or the slow susceptance regulator of SVSMO1, and its record order could not be confirmed from two public sources; give the compensator through the API (Csvgn1Data)",
        ["SVSMO2"] = "GridSim does not model the switched elements (TSC and MSS) or the slow susceptance regulator of SVSMO2, and its record order could not be confirmed from two public sources; give the compensator through the API (Csvgn1Data)",
        ["SVSMO3"] = "its record order is in one public source only (the WECC 2011 report, appendix D), so it is not read with an unconfirmed order, and GridSim does not model its switched elements; give the STATCOM through the API (Statcom1Data)",
        ["CSTCON"] = UnconfirmedStatcom,
        ["CSTATT"] = UnconfirmedStatcom,
    };

    private const string UnconfirmedSvc = "its record order (long ICON and CON sets) could not be confirmed from two public sources; give the compensator through the API (Csvgn1Data)";

    private const string UnconfirmedStatcom = "its record order (long ICON and CON sets) could not be confirmed from two public sources; give the STATCOM through the API (Statcom1Data)";

    private const string OnlyCdc4t = "GridSim models the CDC4T structure only (LCC1); give the link a CDC4T record or use the API (Lcc1Data)";

    /// <summary>WP6 (RC5): the DC line model records the extended library reads (CDC4T: 'DCNAME' 'CDC4T', then ALFDY GAMDY TVDC
    /// TIDC VBLOCK VUNBL TBLOCK VBYPAS VUNBY TBYPAS RSVOLT RSCUR VRAMP CRAMP C0 V1 C1 V2 C2 V3 C3 TCMODE; the PowerWorld page
    /// "DC Line Model: CDC1T, CDC4T, CDC6, CDC6T" and the PSS/E model library data sheet give these 22 values in this order) and
    /// the ones it refuses by name, with the reason.</summary>
    public static readonly IReadOnlyDictionary<string, string?> DcModels = new Dictionary<string, string?>(StringComparer.Ordinal)
    {
        ["CDC4T"] = null,
        ["CDC1T"] = OnlyCdc4t,
        ["CDC6"] = OnlyCdc4t,
        ["CDC6T"] = OnlyCdc4t,
        ["CDC7T"] = OnlyCdc4t,
        ["CDCMC"] = "GridSim models the CDC4T structure only (LCC1), with no capacitor-commutated converter; use the API (Lcc1Data)",
        ["VSCDCT"] = "record order not public (the PSS/E model library is not public text and no second source gives it); give the link through the API (Vsc1Data)",
    };

    // WP3a: the role of a model under these options, or null when it is not mapped.
    private static string? RoleOf(string model, PsseDyrOptions options)
        => MappedModels.TryGetValue(model, out string? role) ? role
            : options.ExtendedLibrary && ExtendedModels.TryGetValue(model, out string? ext) ? ext : null;

    /// <summary>The number of values (ICONs then CONs) each mapped model's record holds after the machine ID.</summary>
    private static readonly IReadOnlyDictionary<string, int> ValueCount = new Dictionary<string, int>(StringComparer.Ordinal)
    {
        ["GENROU"] = 14, ["GENSAL"] = 12, ["SEXS"] = 6, ["IEEET1"] = 14, ["PSS1A"] = 15, ["TGOV1"] = 7, ["IEEEG1"] = 22,
        ["HYGOV"] = 12, ["GAST"] = 9, ["EXAC1"] = 17, ["ESST4B"] = 17,
        ["GENCLS"] = 2, ["ESST1A"] = 20, ["EXST1"] = 12, ["SCRX"] = 8, ["ESAC1A"] = 19, ["ESDC1A"] = 16,
        ["IEEEST"] = 19, ["PSS2A"] = 23, ["PSS2B"] = 29,
        ["GGOV1"] = 35, ["WSIEG1"] = 36, ["CIM5BL"] = 20,
        ["CSVGN1"] = 10, ["CDC4T"] = 22,
        ["EXDC2"] = 16, ["IEEEX1"] = 16, ["ESAC8B"] = 15, ["ESST3A"] = 21, ["IEEEG3"] = 14,
    };

    /// <summary>Split DYR text into records. A record not ended by a slash before the end of the text, or one too short
    /// to hold a bus, a model and an ID, is refused naming its line.</summary>
    public static IReadOnlyList<PsseDyrRecord> ReadRecords(string text) => ReadRecords(text, null);

    /// <summary>The records, with (WP6, extended library only: <paramref name="dcRecords"/> not null) each record whose first
    /// field is a quoted name taken out as a DC line model record. With <paramref name="dcRecords"/> null this is exactly
    /// <see cref="ReadRecords(string)"/>, error for error.</summary>
    internal static IReadOnlyList<PsseDyrRecord> ReadRecords(string text, List<PsseDyrDcRecord>? dcRecords)
    {
        ArgumentNullException.ThrowIfNull(text);
        string[] lines = PsseText.Lines(text);
        var records = new List<PsseDyrRecord>();
        var fields = new List<string?>();
        int start = 0;
        bool quotedFirst = false;
        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i];
            if (PsseText.IsHeading(line)) continue;
            (List<string?> f, string? comment) = PsseText.Split(line, i + 1, "DYR");
            if (fields.Count == 0 && f.Count > 0)
            {
                start = i + 1;
                string lead = line.TrimStart();
                quotedFirst = lead.Length > 0 && (lead[0] == '\'' || lead[0] == '"');
            }
            fields.AddRange(f);
            if (comment is null) continue;   // no slash yet: the record goes on
            if (fields.Count == 0) continue;  // a line that is only a comment
            if (dcRecords is not null && quotedFirst)
            {
                // WP6: 'DCNAME' 'MODEL' values... /
                if (fields.Count < 2) throw new InvalidDataException($"PSS/E DYR line {start} (DYR data): a DC line model record needs the DC line name and the model name.");
                string dcModel = (fields[1] ?? "").Trim().ToUpperInvariant();
                if (dcModel.Length == 0) throw new InvalidDataException($"PSS/E DYR line {start} (DYR data): the model name is empty.");
                dcRecords.Add(new PsseDyrDcRecord((fields[0] ?? "").Trim(), dcModel, fields.Skip(2).Select(x => x ?? "").ToList(), start));
                fields.Clear();
                continue;
            }
            var r = new PsseRecord(fields.ToList(), start, "DYR", null);
            if (fields.Count < 3) throw r.Error($"a record needs IBUS, the model name and the machine ID; this one has {fields.Count} field(s)");
            int bus = r.Int(0, "IBUS");
            string model = (fields[1] ?? "").Trim().ToUpperInvariant();
            if (model.Length == 0) throw r.Error("the model name is empty");
            records.Add(new PsseDyrRecord(bus, model, (fields[2] ?? "").Trim(), fields.Skip(3).Select(x => x ?? "").ToList(), start));
            fields.Clear();
        }
        if (fields.Count > 0) throw new InvalidDataException($"PSS/E DYR line {start} (DYR data): the record is not ended by a slash before the end of the file.");
        return records;
    }

    /// <summary>Map the DYR text onto the machines of <paramref name="raw"/> (the case read from the matching RAW file).</summary>
    public static PsseDyrResult Read(string text, PsseParseResult raw, string name, PsseDyrOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(raw);
        ArgumentNullException.ThrowIfNull(name);
        options ??= new PsseDyrOptions();
        string source = options.SourceName ?? "the DYR file";
        // WP6 (RC5): with the extended library a record led by a quoted name is a DC line model record; without it the
        // records are read exactly as before.
        List<PsseDyrDcRecord>? dcRecords = options.ExtendedLibrary ? new List<PsseDyrDcRecord>() : null;
        IReadOnlyList<PsseDyrRecord> records = ReadRecords(text, dcRecords);
        var notes = new List<string>();
        var issues = new List<string>();

        // The case's units: the RAW record key of each generator, and its 0-based index among the in-service units of its bus.
        GridModel model = raw.Model;
        if (raw.MachineKeys.Count != model.AllGenerators.Count)
            throw new ArgumentException("the RAW parse result's machine keys do not line up with its model's generators.", nameof(raw));
        var unitOf = new Dictionary<(int, string), (Generator Gen, int Unit)>();
        var perBus = new Dictionary<int, int>();
        for (int k = 0; k < model.AllGenerators.Count; k++)
        {
            Generator g = model.AllGenerators[k];
            int unit = -1;
            if (g.InService) { unit = perBus.TryGetValue(g.Bus, out int c) ? c : 0; perBus[g.Bus] = unit + 1; }
            unitOf[(raw.MachineKeys[k].Bus, raw.MachineKeys[k].Id)] = (g, unit);
        }

        // Group the records by unit and role; refuse duplicates and machines the case does not hold.
        var byUnit = new Dictionary<(int, string), Dictionary<string, PsseDyrRecord>>();
        var loadRecords = new List<PsseDyrRecord>();   // WP3b
        var factsRecords = new List<PsseDyrRecord>();  // WP6
        var unknown = new SortedDictionary<string, (int Count, int First)>(StringComparer.Ordinal);
        int outOfService = 0;
        foreach (PsseDyrRecord r in records)
        {
            // WECC (expansion): the renewable models (REGC_A, REEC_A/B/C, under their PSS/E, V33 user-model or WECC names, or
            // as a USRMDL record) take their role from PsseDyrRenewables; their value counts are checked there.
            string? wecc = PsseDyrRenewables.ModelOf(r);
            string refusedKey = r.Model == "USRMDL" && r.Values.Count > 0 ? r.Values[0].Trim().Trim('\'').ToUpperInvariant() : r.Model;
            if (PsseDyrRenewables.RefusedOrder.TryGetValue(refusedKey, out string? modelled))
            {
                issues.Add($"{refusedKey} for machine '{r.Id}' at bus {r.Bus} (line {r.Line}): GridSim models {modelled}, but the order of the PSS/E {refusedKey} record "
                    + $"is not in any public source, so it is not read with a guessed order; write the unit as a '{modelled}' record "
                    + (modelled switch
                    {
                        "REGC_B" => "with the 11 values of Table 1 of the WECC REMWG memo Rev. 27b (2/16/23), in that order",
                        "REEC_D" => "with the six flags PfFlag VFlag QFlag PFlag PqFlag Vcmpflag and then the 78 values of Table 5 of the EPRI REGC_B/REEC_D test protocol (8/17/2020), in that order",
                        "REGFM_A1" => "with the 22 data values of Table 1 of PNNL-35110 (September 2023), in that order, without the three set points the power flow initialises",
                        _ => "with the 48 values of the WECC DER_A specification's Table 1, in that order",
                    }));
                continue;
            }
            if (options.ExtendedLibrary && LoadModels.TryGetValue(r.Model, out string? loadRefusal))
            {
                // WP3b (RC5): a load record names a load (bus and load ID), not a machine.
                if (loadRefusal is not null)
                    issues.Add($"{r.Model} for load '{r.Id}' at bus {r.Bus} (line {r.Line}): {loadRefusal}");
                else
                {
                    if (r.Values.Count != ValueCount[r.Model])
                        throw new InvalidDataException($"PSS/E DYR line {r.Line}: {r.Model} needs exactly {ValueCount[r.Model]} values after the load ID; the record has {r.Values.Count}.");
                    loadRecords.Add(r);
                }
                continue;
            }
            if (options.ExtendedLibrary && FactsModels.TryGetValue(r.Model, out string? factsRefusal))
            {
                // WP6 (RC5): an SVC or STATCOM record on a unit.
                if (factsRefusal is not null)
                    issues.Add($"{r.Model} for machine '{r.Id}' at bus {r.Bus} (line {r.Line}): {factsRefusal}");
                else
                {
                    if (r.Values.Count != ValueCount[r.Model])
                        throw new InvalidDataException($"PSS/E DYR line {r.Line}: {r.Model} needs exactly {ValueCount[r.Model]} values after the machine ID; the record has {r.Values.Count}.");
                    factsRecords.Add(r);
                }
                continue;
            }
            if (options.ExtendedLibrary && DcModels.ContainsKey(r.Model))
                throw new InvalidDataException($"PSS/E DYR line {r.Line}: {r.Model} is a DC line model, whose record starts with the quoted DC line name ('NAME' '{r.Model}' ...), not a bus number.");
            if (options.ExtendedLibrary && RefusedLibraryModels.TryGetValue(r.Model, out string? libraryRefusal))
            {
                // WP19 (RC5): refused by name, with the reason.
                issues.Add($"{r.Model} for machine '{r.Id}' at bus {r.Bus} (line {r.Line}): {libraryRefusal}");
                continue;
            }
            string? role = RoleOf(r.Model, options);
            if (role is null && wecc is null)
            {
                string key = r.Model == "USRMDL" && r.Values.Count > 0 ? $"USRMDL {r.Values[0].Trim()}" : r.Model;
                unknown.TryGetValue(key, out var u);
                unknown[key] = (u.Count + 1, u.First == 0 ? r.Line : u.First);
                continue;
            }
            if (wecc is not null) role = PsseDyrRenewables.Roles[wecc];
            if (role is null) throw new InvalidOperationException("internal error: a mapped record has no role");
            if (!unitOf.TryGetValue((r.Bus, r.Id), out var unit))
                throw new InvalidDataException($"PSS/E DYR line {r.Line}: {r.Model} names machine '{r.Id}' at bus {r.Bus}, which the RAW case does not hold.");
            // Each model's record has a fixed number of values: fewer, or more (two records run together because a slash is
            // missing), is refused rather than read short or long.
            if (ValueCount.TryGetValue(r.Model, out int need) && r.Values.Count != need)
                throw new InvalidDataException($"PSS/E DYR line {r.Line}: {r.Model} needs exactly {need} values after the machine ID; the record has {r.Values.Count}.");
            if (wecc is not null) PsseDyrRenewables.ValuesOf(r, wecc);   // the same count rule for the renewable records
            if (unit.Unit < 0) { outOfService++; continue; }
            if (!byUnit.TryGetValue((r.Bus, r.Id), out var roles)) byUnit[(r.Bus, r.Id)] = roles = new Dictionary<string, PsseDyrRecord>();
            if (roles.TryGetValue(role, out PsseDyrRecord? prior))
                throw new InvalidDataException($"PSS/E DYR line {r.Line}: machine '{r.Id}' at bus {r.Bus} already has a {role} record ({prior.Model}, line {prior.Line}); a unit takes one of each.");
            roles[role] = r;
        }
        if (unknown.Count > 0)
            issues.Add("models this reader does not map: " + string.Join(", ", unknown.Select(kv => $"{kv.Key} x{kv.Value.Count} (first at line {kv.Value.First})")));
        if (outOfService > 0) notes.Add($"{outOfService} record(s) for out-of-service machines left out (the RMS study does not integrate them).");

        // Build the document.
        using var ms = new MemoryStream();
        int machines = 0;
        var renewables = new Dictionary<int, GridSim.Core.Dynamics.Models.Wecc.WeccPlant>();
        // Names and notes hold quotes (X'd): written as they are, not escaped, since the document is a data file, not HTML; LF
        // line ends so the same records give the same bytes on every OS.
        using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions
        {
            Indented = true,
            NewLine = "\n",
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        }))
        {
            w.WriteStartObject();
            w.WriteString("schema", "gridsim-dynamics/2");
            w.WriteString("name", "PSS/E DYR " + name);
            w.WriteString("description", $"Machine dynamic data read from the PSS/E dynamic data file {source} for the case {model.Name}.");
            w.WriteStartArray("templates");
            var bindings = new List<(int Bus, int Unit, string Ref)>();
            foreach (Generator g in model.AllGenerators)
            {
                int k = IndexOf(model.AllGenerators, g);
                PsseMachineKey key = raw.MachineKeys[k];
                if (!byUnit.TryGetValue((key.Bus, key.Id), out var roles)) continue;
                string unitName = $"machine '{key.Id}' at bus {key.Bus}";
                string idBase = $"gridsim.psse.b{key.Bus.ToString(CultureInfo.InvariantCulture)}.m{Segment(key.Id)}";
                // WECC (expansion): a renewable model chain (REGC_A + REEC_A/B/C) for a converter unit.
                bool renewable = PsseDyrRenewables.AllRoles.Any(roles.ContainsKey);
                if (renewable)
                {
                    Renewable(roles, unitName, raw, g, source, renewables, notes, issues);
                    if (!roles.ContainsKey("machine")) continue;   // a renewable unit's controllers were judged there
                }
                if (!roles.TryGetValue("machine", out PsseDyrRecord? mrec))
                {
                    foreach (var kv in roles)
                        issues.Add($"{kv.Value.Model} for {unitName} (line {kv.Value.Line}) has no GENROU or GENSAL record for its unit; GridSim binds controllers to a machine model");
                    continue;
                }
                int before = issues.Count;
                var controllers = new List<(string Slot, string Ref)>();
                bool exciterMapped = false;   // DP-01: a stabiliser needs a MAPPED exciter, not just an exciter record
                foreach (string slot in new[] { "exciter", "pss", "governor" })
                {
                    if (!roles.TryGetValue(slot, out PsseDyrRecord? crec)) continue;
                    var p = new List<(string Name, object Value)>();
                    var derived = new Dictionary<string, string>(StringComparer.Ordinal);
                    if (!Controller(crec, unitName, p, derived, issues)) continue;
                    // DYN-02 (ruling 15): an exciter with one SE of 0 is read as the curve starting at that point; said here.
                    if (slot == "exciter" && SaturationStartsAtZeroPoint(p))
                        notes.Add($"{crec.Model} for {unitName} (line {crec.Line}): one saturation point has SE 0, read as the curve that starts there "
                            + "(A = E of that point, B through the other point).");
                    // DYN-05 (no model change): HYGOV is mapped to a linear ideal water column, said in the notes.
                    if (crec.Model == "HYGOV")
                        notes.Add($"HYGOV for {unitName} (line {crec.Line}) approximated: the penstock is the linear ideal water column "
                            + "(1 - TW s) / (1 + 0.5 TW s) at every loading. PSS/E's nonlinear penstock linearises to (1 - g0 TW s) / (1 + 0.5 g0 TW s) "
                            + "at gate g0, so the two agree at full gate and the water time is overstated by 1 / g0 below it (2x at half gate); "
                            + "the dashpot feedback is taken from the actual gate.");
                    if (slot != "governor" && roles.TryGetValue("machine", out PsseDyrRecord? cls) && cls.Model == "GENCLS")
                    {
                        // WP3a: the classical machine holds a constant voltage behind X'd, so it has no field to drive.
                        issues.Add($"{crec.Model} for {unitName} (line {crec.Line}): the unit's machine is GENCLS (line {cls.Line}), a constant voltage behind X'd with no field for an {slot} to drive");
                        continue;
                    }
                    if (slot == "pss" && !roles.ContainsKey("exciter"))
                    {
                        issues.Add($"{crec.Model} for {unitName} (line {crec.Line}) has no exciter record; the stabiliser's output joins the AVR summing junction");
                        continue;
                    }
                    if (slot == "pss" && !exciterMapped)
                    {
                        issues.Add($"{crec.Model} for {unitName} (line {crec.Line}): its exciter record ({roles["exciter"].Model}, line {roles["exciter"].Line}) was left out, so the stabiliser has no AVR to act on");
                        continue;
                    }
                    string id = $"{idBase}.{slot}";
                    WriteTemplate(w, id, slot, $"{crec.Model}, {unitName}", p, derived, source, crec);
                    controllers.Add((slot, id + "@1"));
                    if (slot == "exciter") exciterMapped = true;
                }
                var mp = new List<(string Name, object Value)>();
                var mderived = new Dictionary<string, string>(StringComparer.Ordinal);
                Machine(mrec, unitName, mp, mderived, issues,
                    k < raw.MachineSourceReactances.Count ? raw.MachineSourceReactances[k] : double.NaN);
                if (issues.Count > before && !options.AllowUnmodelled)
                    continue;   // refused below; nothing more is written for this unit
                string mid = $"{idBase}.machine";
                WriteTemplate(w, mid, "synchronousMachine", $"{mrec.Model}, {unitName}", mp, mderived, source, mrec, controllers);
                bindings.Add((g.Bus, unitOf[(key.Bus, key.Id)].Unit, mid + "@1"));
                machines++;
            }
            w.WriteEndArray();
            w.WriteStartArray("machines");
            foreach ((int bus, int unit, string tref) in bindings)
            {
                w.WriteStartObject();
                w.WriteNumber("bus", bus);
                if ((perBus.TryGetValue(bus, out int n) ? n : 0) > 1) w.WriteNumber("unit", unit);
                w.WriteString("template", tref);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
        }

        List<GridSim.Core.Dynamics.Models.DynamicLoadData> dynamicLoads = Loads(loadRecords, raw, issues, notes);
        // WP6 (RC5): the SVC records (on units with no machine record) and the DC line records; none without the extended library.
        Dictionary<int, GridSim.Core.Dynamics.Models.RmsFactsData> facts = Compensators(factsRecords, raw, unitOf, byUnit, renewables, issues, notes);
        List<GridSim.Core.Dynamics.Models.RmsNetworkDeviceData> devices = dcRecords is null ? new() : DcLines(dcRecords, model, issues, notes);

        // WECC (expansion): with unmodelled dynamics allowed a renewable chain that had a problem is left out (listed below);
        // the unit then runs on the case's own converter model.
        if (issues.Count > 0 && !options.AllowUnmodelled)
            throw new InvalidDataException($"PSS/E DYR {source}: {issues.Count} item(s) GridSim cannot represent exactly:" + Environment.NewLine + "  " +
                string.Join(Environment.NewLine + "  ", issues) + Environment.NewLine +
                "Read it with unmodelled dynamics allowed (gridsim: --allow-unmodelled) to leave them out and use the rest.");
        foreach (string issue in issues) notes.Add("left out (unmodelled dynamics allowed): " + issue + ".");
        if (machines == 0 && renewables.Count == 0 && dynamicLoads.Count == 0 && facts.Count == 0 && devices.Count == 0)
            throw new InvalidDataException($"PSS/E DYR {source}: no machine of the case has a GENROU or GENSAL record or a renewable model that GridSim can use, so there is no dynamic data.");
        return new PsseDyrResult(Encoding.UTF8.GetString(ms.ToArray()), notes, records, machines)
        {
            Renewables = renewables, DynamicLoads = dynamicLoads, Facts = facts, NetworkDevices = devices,
            DcRecords = dcRecords is null ? Array.Empty<PsseDyrDcRecord>() : dcRecords,
        };
    }

    /// <summary>
    /// WP3b (RC5): the CIM5BL records as third-order induction motor loads (<see cref="GridSim.Core.Dynamics.Models.InductionMotorLoadData"/>).
    /// The motor takes its load record's PL + IP and QL + IQ (its part of the bus load the RMS network holds; YP and YQ stay a
    /// bus shunt). Refused by name: a double cage (R2 or X2 not 0), saturation (SE(E1) or SE(E2) not 0), the under-voltage
    /// trip relay (VI not 0), a load the RAW case does not hold or holds out of service, a load with distributed generation
    /// (DGENF 1), a load with no active power, and a second record for one load. TNOM is used by PSS/E for motor starting
    /// only and is not read.
    /// </summary>
    private static List<GridSim.Core.Dynamics.Models.DynamicLoadData> Loads(List<PsseDyrRecord> records, PsseParseResult raw,
        List<string> issues, List<string> notes)
    {
        var loads = new List<GridSim.Core.Dynamics.Models.DynamicLoadData>();
        var seen = new HashSet<(int, string)>();
        foreach (PsseDyrRecord r in records)
        {
            string at = $"{r.Model} for load '{r.Id}' at bus {r.Bus} (line {r.Line})";
            int before = issues.Count;
            void Need(bool ok, string what) { if (!ok) issues.Add($"{at}: {what}"); }
            if (!seen.Add((r.Bus, r.Id))) { issues.Add($"{at}: the load already has a dynamic load record"); continue; }
            PsseLoadSource? src = raw.LoadSources.FirstOrDefault(l => l.Key.Bus == r.Bus && l.Key.Id == r.Id);
            if (src is null) { issues.Add($"{at}: the RAW case holds no load '{r.Id}' at bus {r.Bus}"); continue; }
            if (!src.InService || !src.BusRetained) { issues.Add($"{at}: the load is out of service or on a dropped bus"); continue; }
            double F(int k)
            {
                string? t = k < src.Fields.Count ? src.Fields[k] : null;
                if (t is null || t.Trim().Length == 0) return 0.0;
                return double.TryParse(t.Trim().Trim('\'').Replace('D', 'E').Replace('d', 'e'), NumberStyles.Float, CultureInfo.InvariantCulture, out double x) ? x : double.NaN;
            }
            double pmw = F(5) + F(7), qmvar = F(6) + F(8);
            Need(!(src.Fields.Count > 16 && F(16) == 1.0), "the load carries distributed generation (DGENF 1), which a motor record does not represent");
            Need(pmw > 0 && double.IsFinite(qmvar), $"the load's PL + IP is {pmw.ToString("R", CultureInfo.InvariantCulture)} MW; a motor needs an active power above 0");
            double it = V(r, 0, "IT");
            Need(it is 1.0 or 2.0, "IT not 1 or 2");
            double ra = V(r, 1, "RA"), xa = V(r, 2, "XA"), xm = V(r, 3, "XM"), r1 = V(r, 4, "R1"), x1 = V(r, 5, "X1"), r2 = V(r, 6, "R2"), x2 = V(r, 7, "X2");
            Need(r2 == 0 && x2 == 0, "a double cage (R2 or X2 not 0); GridSim's motor is the single-cage third-order model");
            Need(V(r, 9, "SE(E1)") == 0 && V(r, 11, "SE(E2)") == 0, "saturation (SE(E1) or SE(E2) not 0)");
            double mbase = V(r, 12, "MBASE"), pmult = V(r, 13, "PMULT"), h = V(r, 14, "H");
            Need(V(r, 15, "VI") == 0, "the under-voltage trip relay (VI not 0)");
            double d = V(r, 18, "D");
            double motorMva = mbase > 0 ? mbase : pmult * pmw;
            Need(motorMva > 0, "MBASE is 0 and PMULT x MW load is not above 0, so the motor has no base");
            if (issues.Count > before) continue;
            try
            {
                var motor = GridSim.Core.Dynamics.Models.InductionMotorParameters.FromCircuit(ra, xa, xm, r1, x1, h,
                    GridSim.Core.Dynamics.Models.InductionMotorTorque.PowerLaw(d), raw.Model.FrequencyHz);
                loads.Add(new GridSim.Core.Dynamics.Models.InductionMotorLoadData(r.Bus, pmw, qmvar, motor, motorMva)
                    { Name = $"CIM5BL load '{r.Id}' @ bus {r.Bus}" });
            }
            catch (ArgumentException ex) { issues.Add($"{at}: {ex.Message}"); continue; }
        }
        if (loads.Count > 0)
            notes.Add($"{loads.Count} CIM5BL record(s) read as third-order induction motor loads (each takes its load's PL + IP, QL + IQ; the reactive difference is a shunt at the bus).");
        return loads;
    }

    /// <summary>
    /// WP6 (RC5): the CSVGN1 records as SVC models on their units (<see cref="GridSim.Core.Dynamics.Models.Csvgn1Data"/>,
    /// MBASE the unit's), keyed by the unit's index in <c>model.Generators</c>. Refused: a unit the RAW case does not hold
    /// (always), a second record for one unit, and a unit that also has a machine or controller record or a renewable chain.
    /// </summary>
    private static Dictionary<int, GridSim.Core.Dynamics.Models.RmsFactsData> Compensators(List<PsseDyrRecord> records, PsseParseResult raw,
        Dictionary<(int, string), (Generator Gen, int Unit)> unitOf, Dictionary<(int, string), Dictionary<string, PsseDyrRecord>> byUnit,
        Dictionary<int, GridSim.Core.Dynamics.Models.Wecc.WeccPlant> renewables, List<string> issues, List<string> notes)
    {
        var facts = new Dictionary<int, GridSim.Core.Dynamics.Models.RmsFactsData>();
        var seen = new Dictionary<(int, string), int>();
        int outOfService = 0;
        foreach (PsseDyrRecord r in records)
        {
            if (!unitOf.TryGetValue((r.Bus, r.Id), out var unit))
                throw new InvalidDataException($"PSS/E DYR line {r.Line}: {r.Model} names machine '{r.Id}' at bus {r.Bus}, which the RAW case does not hold.");
            string at = $"{r.Model} for machine '{r.Id}' at bus {r.Bus} (line {r.Line})";
            if (seen.TryGetValue((r.Bus, r.Id), out int prior)) { issues.Add($"{at}: the unit already has a CSVGN1 record (line {prior})"); continue; }
            seen[(r.Bus, r.Id)] = r.Line;
            if (unit.Unit < 0) { outOfService++; continue; }
            if (byUnit.TryGetValue((r.Bus, r.Id), out var roles) && roles.Count > 0)
            {
                issues.Add($"{at}: the unit also has {string.Join(", ", roles.Values.OrderBy(x => x.Line).Select(x => $"a {x.Model} record (line {x.Line})"))}; an SVC unit takes no machine or controller model");
                continue;
            }
            int k = IndexOf(raw.Model.Generators, unit.Gen);
            if (renewables.ContainsKey(k)) { issues.Add($"{at}: the unit also has a renewable model chain"); continue; }
            var data = new GridSim.Core.Dynamics.Models.Csvgn1Data(V(r, 0, "K"), V(r, 1, "T1"), V(r, 2, "T2"), V(r, 3, "T3"), V(r, 4, "T4"),
                V(r, 5, "T5"), V(r, 6, "RMIN"), V(r, 7, "VMAX"), V(r, 8, "VMIN"), V(r, 9, "CBASE"), unit.Gen.MBase);
            try { data.Validate(at); }
            catch (ArgumentException ex) { issues.Add(ex.Message); continue; }
            facts[k] = data;
        }
        if (outOfService > 0) notes.Add($"{outOfService} CSVGN1 record(s) for out-of-service units left out.");
        if (facts.Count > 0) notes.Add($"{facts.Count} CSVGN1 record(s) read as SVC models on their units (MBASE the unit's).");
        return facts;
    }

    /// <summary>
    /// WP6 (RC5): the CDC4T records as LCC1 models of the case's two-terminal LCC links (matched by name). Refused: a name the
    /// case does not hold as a two-terminal DC line (always), a second record for one link, the DC line models GridSim does
    /// not read (by name), and a non-zero value of a part LCC1 does not model (VBYPAS, VUNBY, TBYPAS, RSVOLT, RSCUR, TCMODE),
    /// each listed; with unmodelled dynamics allowed those values are left out and the rest of the record is read.
    /// </summary>
    private static List<GridSim.Core.Dynamics.Models.RmsNetworkDeviceData> DcLines(List<PsseDyrDcRecord> records, GridModel model,
        List<string> issues, List<string> notes)
    {
        var devices = new List<GridSim.Core.Dynamics.Models.RmsNetworkDeviceData>();
        var seen = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (PsseDyrDcRecord r in records)
        {
            string at = $"{r.Model} for DC line '{r.DcName}' (line {r.Line})";
            if (!DcModels.TryGetValue(r.Model, out string? refusal))
            {
                issues.Add($"{at}: a record led by a quoted name is a DC line model record, and {r.Model} is not a DC line model this reader knows");
                continue;
            }
            if (refusal is not null) { issues.Add($"{at}: {refusal}"); continue; }
            if (r.Values.Count != ValueCount[r.Model])
                throw new InvalidDataException($"PSS/E DYR line {r.Line}: {r.Model} needs exactly {ValueCount[r.Model]} values after the DC line name; the record has {r.Values.Count}.");
            LccDcLink? link = model.LccLinks.FirstOrDefault(l => string.Equals(l.Name.Trim(), r.DcName, StringComparison.Ordinal));
            if (link is null)
                throw new InvalidDataException($"PSS/E DYR line {r.Line}: {r.Model} names DC line '{r.DcName}', which the RAW case does not hold as a two-terminal DC line.");
            if (seen.TryGetValue(r.DcName, out int prior)) { issues.Add($"{at}: the DC line already has a CDC4T record (line {prior})"); continue; }
            seen[r.DcName] = r.Line;
            var pr = new PsseDyrRecord(0, r.Model, r.DcName, r.Values, r.Line);
            foreach ((int k, string name) in new[] { (7, "VBYPAS"), (8, "VUNBY"), (9, "TBYPAS"), (10, "RSVOLT"), (11, "RSCUR"), (21, "TCMODE") })
                if (V(pr, k, name) != 0)
                    issues.Add($"{at}: {name} = {r.Values[k].Trim()}; LCC1 does not model the inverter bypass, the restart minimums or the forced current mode");
            var data = new GridSim.Core.Dynamics.Models.Lcc1Data(V(pr, 0, "ALFDY"), V(pr, 1, "GAMDY"), V(pr, 2, "TVDC"), V(pr, 3, "TIDC"),
                V(pr, 4, "VBLOCK"), V(pr, 5, "VUNBL"), V(pr, 6, "TBLOCK"), V(pr, 12, "VRAMP"), V(pr, 13, "CRAMP"), V(pr, 14, "C0"),
                V(pr, 15, "V1"), V(pr, 16, "C1"), V(pr, 17, "V2"), V(pr, 18, "C2"), V(pr, 19, "V3"), V(pr, 20, "C3"));
            try { data.Validate(at); }
            catch (ArgumentException ex) { issues.Add(ex.Message); continue; }
            devices.Add(new GridSim.Core.Dynamics.Models.LccLinkDynamicsData(link.Name, data));
        }
        if (devices.Count > 0) notes.Add($"{devices.Count} CDC4T record(s) read as LCC1 models of their DC lines.");
        return devices;
    }

    /// <summary>
    /// WECC (expansion): map one unit's renewable records onto a <see cref="GridSim.Core.Dynamics.Models.Wecc.WeccPlant"/>
    /// (every value range-checked by the parameter sets), or add the reasons it cannot be used to <paramref name="issues"/>.
    /// A unit takes a synchronous machine model or a renewable chain, not both; the chain is anchored on its REGC_A; a
    /// renewable unit has no exciter, stabiliser or governor.
    /// </summary>
    private static void Renewable(Dictionary<string, PsseDyrRecord> roles, string unitName, PsseParseResult raw, Generator g,
        string source, Dictionary<int, GridSim.Core.Dynamics.Models.Wecc.WeccPlant> renewables, List<string> notes, List<string> issues)
    {
        GridModel model = raw.Model;
        roles.TryGetValue(PsseDyrRenewables.ConverterRole, out PsseDyrRecord? conv);
        roles.TryGetValue(PsseDyrRenewables.ElectricalRole, out PsseDyrRecord? elec);
        roles.TryGetValue(PsseDyrRenewables.PlantRole, out PsseDyrRecord? plantRec);
        roles.TryGetValue(PsseDyrRenewables.DriveTrainRole, out PsseDyrRecord? driveRec);
        roles.TryGetValue(PsseDyrRenewables.AeroRole, out PsseDyrRecord? aeroRec);
        roles.TryGetValue(PsseDyrRenewables.PitchRole, out PsseDyrRecord? pitchRec);
        roles.TryGetValue(PsseDyrRenewables.TorqueRole, out PsseDyrRecord? torqueRec);
        roles.TryGetValue(PsseDyrRenewables.DerRole, out PsseDyrRecord? derRec);
        roles.TryGetValue(PsseDyrRenewables.GfmRole, out PsseDyrRecord? gfmRec);
        PsseDyrRecord first = gfmRec ?? derRec ?? conv ?? elec ?? plantRec ?? driveRec ?? aeroRec ?? pitchRec ?? torqueRec!;
        string firstName = PsseDyrRenewables.ModelOf(first)!;
        int before = issues.Count;
        bool hasMachine = roles.TryGetValue("machine", out PsseDyrRecord? mrec);
        if (hasMachine)
            issues.Add($"{firstName} for {unitName} (line {first.Line}): the unit also has a {mrec!.Model} machine record (line {mrec.Line}); "
                + "a unit takes either a synchronous machine model or a renewable converter model, not both");
        else
            foreach (string slot in new[] { "exciter", "pss", "governor" })
                if (roles.TryGetValue(slot, out PsseDyrRecord? c))
                    issues.Add($"{c.Model} for {unitName} (line {c.Line}): a renewable converter unit ({firstName}) has no field or shaft for a {slot}");
        if (gfmRec is not null)
        {
            // REGFM_A1 stands alone (a voltage source with its own controls).
            foreach (PsseDyrRecord? o in new[] { conv, elec, plantRec, driveRec, aeroRec, pitchRec, torqueRec, derRec })
                if (o is not null)
                    issues.Add($"{PsseDyrRenewables.ModelOf(o)} for {unitName} (line {o.Line}): the unit also has a REGFM_A1 record (line {gfmRec.Line}); REGFM_A1 stands alone");
            GridSim.Core.Dynamics.Models.Wecc.RegfmA1Parameters gp = PsseDyrRenewables.RegfmA1(gfmRec);
            foreach (string problem in gp.Check()) issues.Add($"{problem}, for {unitName} (line {gfmRec.Line})");
            if (issues.Count > before) return;
            renewables[IndexOf(model.Generators, g)] = GridSim.Core.Dynamics.Models.Wecc.WeccPlant.RegfmA1(gp,
                $"PSS/E DYR {source}, line(s) {gfmRec.Line.ToString(CultureInfo.InvariantCulture)}");
            return;
        }
        if (derRec is not null)
        {
            // DER_A stands alone.
            foreach (PsseDyrRecord? o in new[] { conv, elec, plantRec, driveRec, aeroRec, pitchRec, torqueRec })
                if (o is not null)
                    issues.Add($"{PsseDyrRenewables.ModelOf(o)} for {unitName} (line {o.Line}): the unit also has a DER_A record (line {derRec.Line}); DER_A stands alone");
            GridSim.Core.Dynamics.Models.Wecc.DeraParameters dp = PsseDyrRenewables.Dera(derRec);
            var der = GridSim.Core.Dynamics.Models.Wecc.WeccPlant.DerA(dp, $"PSS/E DYR {source}, line(s) {derRec.Line.ToString(CultureInfo.InvariantCulture)}");
            foreach (string problem in dp.Check()) issues.Add($"{problem}, for {unitName} (line {derRec.Line})");
            if (issues.Count > before) return;
            int kd = -1;
            for (int i = 0; i < model.Generators.Count; i++) if (ReferenceEquals(model.Generators[i], g)) { kd = i; break; }
            if (kd < 0) throw new InvalidOperationException("internal error: an in-service unit of the DYR is not among the model's generators");
            renewables[kd] = der;
            notes.Add($"DER_A for {unitName} (line {derRec.Line}): Xe = {dp.Xe.ToString("R", CultureInfo.InvariantCulture)} is the model's numerical network-interface "
                + "reactance; GridSim solves the current-source interface to its fixed point (1e-10 pu), so it has no effect on the answer");
            return;
        }
        if (conv is null)
        {
            issues.Add($"{firstName} for {unitName} (line {first.Line}) has no REGC_A record for its unit (nor REGC_B); the controls drive a generator/converter model");
            return;
        }
        bool isB = PsseDyrRenewables.ModelOf(conv) == "REGC_B";
        GridSim.Core.Dynamics.Models.Wecc.RegcaParameters? regc = isB ? null : PsseDyrRenewables.Regca(conv);
        GridSim.Core.Dynamics.Models.Wecc.RegcbParameters? regcb = isB ? PsseDyrRenewables.Regcb(conv) : null;
        string? elecName = elec is null ? null : PsseDyrRenewables.ModelOf(elec);
        GridSim.Core.Dynamics.Models.Wecc.ReecParameters? reec = elec is null || elecName == "REEC_D" ? null : PsseDyrRenewables.Reec(elec, elecName!);
        GridSim.Core.Dynamics.Models.Wecc.ReecdParameters? reecd = elecName == "REEC_D" ? PsseDyrRenewables.Reecd(elec!) : null;
        if (reecd is not null && reecd.Mvab != 0.0 && Math.Abs(reecd.Mvab - GridSim.Core.Dynamics.Models.MachineBase.EffectiveMva(g, model.BaseMva)) > 1e-9 * reecd.Mvab)
            issues.Add($"REEC_D for {unitName} (line {elec!.Line}): mvab = {reecd.Mvab.ToString("R", CultureInfo.InvariantCulture)} differs from the unit's MBASE "
                + $"{GridSim.Core.Dynamics.Models.MachineBase.EffectiveMva(g, model.BaseMva).ToString("R", CultureInfo.InvariantCulture)} MVA; GridSim works on MBASE, so mvab must be 0 or MBASE");
        GridSim.Core.Dynamics.Models.Wecc.RepcaParameters? repc = null;
        if (plantRec is not null)
        {
            repc = PsseDyrRenewables.Repca(plantRec);
            // The regulated bus and the flow branch must be the RAW case's (the branch by its buses and circuit identifier).
            if (repc.RegulatedBus != 0 && !model.Buses.Any(b => b.Id == repc.RegulatedBus))
                issues.Add($"REPC_A for {unitName} (line {plantRec.Line}): regulated bus {repc.RegulatedBus} is not a bus of the RAW case");
            if (repc.FlowFromBus != 0 && repc.FlowToBus != 0)
            {
                int bi = -1;
                for (int i = 0; i < raw.BranchKeys.Count; i++)
                {
                    PsseBranchKey bk = raw.BranchKeys[i];
                    bool ends = (bk.FromBus == repc.FlowFromBus && bk.ToBus == repc.FlowToBus) || (bk.FromBus == repc.FlowToBus && bk.ToBus == repc.FlowFromBus);
                    if (ends && bk.ThirdBus == 0 && string.Equals(bk.Circuit.Trim(), repc.FlowCircuit, StringComparison.OrdinalIgnoreCase)) { bi = i; break; }
                }
                if (bi < 0)
                    issues.Add($"REPC_A for {unitName} (line {plantRec.Line}): flow branch {repc.FlowFromBus}-{repc.FlowToBus} circuit '{repc.FlowCircuit}' is not an in-service two-terminal branch of the RAW case");
                else repc = repc with { FlowBranchIndex = bi };
            }
        }
        GridSim.Core.Dynamics.Models.Wecc.WindParameters? wind = null;
        if (driveRec is not null)
            wind = new GridSim.Core.Dynamics.Models.Wecc.WindParameters(PsseDyrRenewables.Wtgta(driveRec),
                aeroRec is null ? null : PsseDyrRenewables.Wtgara(aeroRec), pitchRec is null ? null : PsseDyrRenewables.Wtgpta(pitchRec),
                torqueRec is null ? null : PsseDyrRenewables.Wtgtrqa(torqueRec));
        else
            foreach (PsseDyrRecord? w in new[] { aeroRec, pitchRec, torqueRec })
                if (w is not null)
                    issues.Add($"{PsseDyrRenewables.ModelOf(w)} for {unitName} (line {w.Line}) has no WTGT_A drive train record for its unit; the turbine models act on its speeds");
        PsseDyrRecord?[] all = { conv, elec, plantRec, driveRec, aeroRec, pitchRec, torqueRec };
        string lines = string.Join(", ", all.Where(x => x is not null).Select(x => x!.Line.ToString(CultureInfo.InvariantCulture)));
        var plant = new GridSim.Core.Dynamics.Models.Wecc.WeccPlant(regc, reec, $"PSS/E DYR {source}, line(s) {lines}") { Repc = repc, Wind = wind, RegcB = regcb, ReecD = reecd };
        foreach (string problem in plant.Check())
        {
            // The record a problem belongs to, by the model name it starts with (the chain's own rules go to the REGC_A line).
            PsseDyrRecord? owner = all.FirstOrDefault(x => x is not null && problem.StartsWith(PsseDyrRenewables.ModelOf(x)! + " ", StringComparison.Ordinal));
            issues.Add($"{problem}, for {unitName} (line {(owner ?? conv).Line})");
        }
        if (issues.Count > before) return;
        int k = -1;
        for (int i = 0; i < model.Generators.Count; i++) if (ReferenceEquals(model.Generators[i], g)) { k = i; break; }
        if (k < 0) throw new InvalidOperationException("internal error: an in-service unit of the DYR is not among the model's generators");
        renewables[k] = plant;
        if (regc is not null)
        notes.Add($"{plant.Chain} for {unitName} (line(s) {lines}): REGC_A Accel = {regc.Accel.ToString("R", CultureInfo.InvariantCulture)} is a PSS/E "
            + "network-solution acceleration factor; GridSim solves the converter / network fixed point to 1e-10 pu, so it has no effect on the answer");
    }

    private static int IndexOf(IReadOnlyList<Generator> list, Generator g)
    {
        for (int i = 0; i < list.Count; i++) if (ReferenceEquals(list[i], g)) return i;
        throw new InvalidOperationException("generator not in the fleet");
    }

    /// <summary>A machine ID as a template id segment: letters, digits, '_' and '-' kept, anything else as _xx (hex).</summary>
    private static string Segment(string id)
    {
        var sb = new StringBuilder();
        foreach (char c in id)
        {
            if (char.IsAsciiLetterOrDigit(c) || c == '-') sb.Append(c);
            else sb.Append('_').Append(((int)c).ToString("x2", CultureInfo.InvariantCulture));
        }
        return sb.Length > 0 ? sb.ToString() : "_";
    }

    private static double V(PsseDyrRecord r, int k, string name)
    {
        string s = r.Values[k].Replace('D', 'E').Replace('d', 'e');
        if (!double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) || !double.IsFinite(v))
            throw new InvalidDataException($"PSS/E DYR line {r.Line}: {r.Model} {name} is '{r.Values[k]}'; it must be a finite number.");
        return v;
    }

    private static void Machine(PsseDyrRecord r, string unit, List<(string, object)> p, Dictionary<string, string> derived, List<string> issues,
        double zsorceX)
    {
        if (r.Model == "GENCLS")
        {
            // WP3a (RC5). H D; X'd is the RAW record's source reactance ZX (ZSORCE), on MBASE.
            double h = V(r, 0, "H");
            if (!(h > 0)) issues.Add($"GENCLS for {unit} (line {r.Line}): H = {h.ToString("R", CultureInfo.InvariantCulture)} is PSS/E's infinite bus, which GridSim's RMS study does not hold as a machine");
            if (!(zsorceX > 0) || !double.IsFinite(zsorceX))
                issues.Add($"GENCLS for {unit} (line {r.Line}): its X'd is the RAW record's ZX (ZSORCE), which is {zsorceX.ToString("R", CultureInfo.InvariantCulture)} here; it must be a number above 0");
            p.Add(("model", "GENCLS")); p.Add(("inertiaH", h)); p.Add(("damping", V(r, 1, "D")));
            p.Add(("xdpPu", double.IsFinite(zsorceX) && zsorceX > 0 ? zsorceX : 1.0));
            derived["xdpPu"] = "GENCLS takes X'd from the RAW generator record's source reactance ZX (ZSORCE), on MBASE.";
            return;
        }
        if (r.Model == "GENTPJ")
        {
            // WP3a (RC5): GridSim models GENTPJ (a gridsim-dynamics/2 machine template with model GENTPJ), but no public source
            // checked for this work (ANDES psse-dyr.yaml, NREL PowerSystems.jl psse_dynamic_mapping.yaml, the PowerWorld
            // model pages, the public copy of the PSS/E model library data sheets) gives the PSS/E GENTPJ record order, so
            // the record is not read with a guessed order.
            issues.Add($"GENTPJ for {unit} (line {r.Line}): GridSim models GENTPJ, but the order of the PSS/E GENTPJ record is not in any public source, so it is not read with a guessed order; write the machine as a gridsim-dynamics/2 template with model GENTPJ");
            return;
        }
        if (r.Model == "GENROU")
        {
            // T'do T''do T'qo T''qo H D Xd Xq X'd X'q X''d Xl S(1.0) S(1.2); X''q = X''d.
            p.Add(("tdopS", V(r, 0, "T'do"))); p.Add(("tdoppS", V(r, 1, "T''do"))); p.Add(("tqopS", V(r, 2, "T'qo")));
            p.Add(("tqoppS", V(r, 3, "T''qo"))); p.Add(("inertiaH", V(r, 4, "H"))); p.Add(("damping", V(r, 5, "D")));
            p.Add(("xdPu", V(r, 6, "Xd"))); p.Add(("xqPu", V(r, 7, "Xq"))); p.Add(("xdpPu", V(r, 8, "X'd")));
            p.Add(("xqpPu", V(r, 9, "X'q"))); p.Add(("xdppPu", V(r, 10, "X''d"))); p.Add(("xqppPu", V(r, 10, "X''d")));
            p.Add(("xlPu", V(r, 11, "Xl"))); p.Add(("saturationS10", V(r, 12, "S(1.0)"))); p.Add(("saturationS12", V(r, 13, "S(1.2)")));
            derived["xqppPu"] = "GENROU takes X''q = X''d.";
            // DYN-02 (ruling 15): S(1.0) = 0 with S(1.2) above 0 is the curve that starts at 1.0 pu.
            if (V(r, 12, "S(1.0)") == 0 && V(r, 13, "S(1.2)") > 0)
                derived["saturationS10"] = "S(1.0) is 0 and S(1.2) above 0: read as the curve that starts at 1.0 pu (A = 1, B through the 1.2 point).";
            return;
        }
        // GENSAL: T'do T''do T''qo H D Xd Xq X'd X''d Xl S(1.0) S(1.2).
        double tqopp = V(r, 2, "T''qo"), xq = V(r, 6, "Xq"), s10 = V(r, 10, "S(1.0)"), s12 = V(r, 11, "S(1.2)");
        p.Add(("tdopS", V(r, 0, "T'do"))); p.Add(("tdoppS", V(r, 1, "T''do"))); p.Add(("tqopS", tqopp)); p.Add(("tqoppS", tqopp));
        p.Add(("inertiaH", V(r, 3, "H"))); p.Add(("damping", V(r, 4, "D"))); p.Add(("xdPu", V(r, 5, "Xd"))); p.Add(("xqPu", xq));
        p.Add(("xdpPu", V(r, 7, "X'd"))); p.Add(("xqpPu", xq)); p.Add(("xdppPu", V(r, 8, "X''d"))); p.Add(("xqppPu", V(r, 8, "X''d")));
        p.Add(("xlPu", V(r, 9, "Xl")));
        derived["xqpPu"] = "GENSAL has no q-axis transient circuit: X'q = Xq, so that circuit carries nothing.";
        derived["tqopS"] = "GENSAL has no q-axis transient circuit: T'qo = T''qo, which has no effect because X'q = Xq.";
        derived["xqppPu"] = "GENSAL takes X''q = X''d.";
        if (s10 != 0 || s12 != 0)
            issues.Add($"GENSAL saturation for {unit} (line {r.Line}): PSS/E saturates the d axis only, GridSim's machine both axes");
    }

    /// <summary>DYN-02 (ruling 15): true when a mapped exciter's two saturation points have exactly one SE of 0 (the parameters
    /// whose names end in Se1 and Se2).</summary>
    private static bool SaturationStartsAtZeroPoint(List<(string Name, object Value)> p)
    {
        double? se1 = null, se2 = null;
        foreach ((string name, object value) in p)
        {
            if (value is not double d) continue;
            if (name.EndsWith("Se1", StringComparison.OrdinalIgnoreCase)) se1 = d;
            else if (name.EndsWith("Se2", StringComparison.OrdinalIgnoreCase)) se2 = d;
        }
        return se1 is double a && se2 is double b && (a == 0) != (b == 0);
    }

    /// <summary>A controller's parameters; false (with the reason in <paramref name="issues"/>) when it cannot be mapped.</summary>
    private static bool Controller(PsseDyrRecord r, string unit, List<(string, object)> p, Dictionary<string, string> derived, List<string> issues)
    {
        int before = issues.Count;
        string at = $"{r.Model} for {unit} (line {r.Line})";
        void Need(bool ok, string what) { if (!ok) issues.Add($"{at}: {what}"); }
        switch (r.Model)
        {
            case "SEXS":
            {
                // TA/TB TB K TE EMIN EMAX
                double ratio = V(r, 0, "TA/TB"), tb = V(r, 1, "TB");
                p.Add(("model", "SEXS")); p.Add(("sexsLeadS", ratio * tb)); p.Add(("sexsLagS", tb)); p.Add(("sexsGain", V(r, 2, "K")));
                p.Add(("sexsExciterTimeS", V(r, 3, "TE"))); p.Add(("sexsFieldMinPu", V(r, 4, "EMIN"))); p.Add(("sexsFieldMaxPu", V(r, 5, "EMAX")));
                derived["sexsLeadS"] = "TA = (TA/TB) x TB.";
                break;
            }
            case "IEEET1":
            {
                // TR KA TA VRMAX VRMIN KE TE KF TF SWITCH E1 SE(E1) E2 SE(E2)
                Need(V(r, 0, "TR") == 0, "a voltage transducer lag TR (GridSim's IEEET1 has none)");
                Need(V(r, 5, "KE") != 0, "KE = 0, which PSS/E replaces by a value computed at initialisation");
                Need(V(r, 9, "SWITCH") == 0, "SWITCH not 0");
                Need(V(r, 11, "SE(E1)") == 0 && V(r, 13, "SE(E2)") == 0, "exciter saturation (PSS/E's curve is not GridSim's exponential one)");
                p.Add(("model", "IEEET1")); p.Add(("ieeet1RegulatorGain", V(r, 1, "KA"))); p.Add(("ieeet1RegulatorTimeS", V(r, 2, "TA")));
                p.Add(("ieeet1RegulatorMaxPu", V(r, 3, "VRMAX"))); p.Add(("ieeet1RegulatorMinPu", V(r, 4, "VRMIN")));
                p.Add(("ieeet1ExciterConstant", V(r, 5, "KE"))); p.Add(("ieeet1ExciterTimeS", V(r, 6, "TE")));
                p.Add(("ieeet1FeedbackGain", V(r, 7, "KF"))); p.Add(("ieeet1FeedbackTimeS", V(r, 8, "TF")));
                break;
            }
            case "ESST4B":
                // TR KPR KIR VRMAX VRMIN TA KPM KIM VMMAX VMMIN KG KP KI VBMAX KC XL THETAP
                Need(V(r, 0, "TR") == 0, "a voltage transducer lag TR (GridSim's ESST4B has none)");
                p.Add(("model", "ESST4B")); p.Add(("st4bKpr", V(r, 1, "KPR"))); p.Add(("st4bKir", V(r, 2, "KIR")));
                p.Add(("st4bVrMaxPu", V(r, 3, "VRMAX"))); p.Add(("st4bVrMinPu", V(r, 4, "VRMIN"))); p.Add(("st4bTaS", V(r, 5, "TA")));
                p.Add(("st4bKpm", V(r, 6, "KPM"))); p.Add(("st4bKim", V(r, 7, "KIM"))); p.Add(("st4bVmMaxPu", V(r, 8, "VMMAX")));
                p.Add(("st4bVmMinPu", V(r, 9, "VMMIN"))); p.Add(("st4bKg", V(r, 10, "KG"))); p.Add(("st4bKp", V(r, 11, "KP")));
                p.Add(("st4bKi", V(r, 12, "KI"))); p.Add(("st4bVbMaxPu", V(r, 13, "VBMAX"))); p.Add(("st4bKc", V(r, 14, "KC")));
                p.Add(("st4bXlPu", V(r, 15, "XL"))); p.Add(("st4bThetapDeg", V(r, 16, "THETAP")));
                break;
            case "EXAC1":
                // TR TB TC KA TA VRMAX VRMIN TE KF TF KC KD KE E1 SE(E1) E2 SE(E2)
                Need(V(r, 0, "TR") == 0, "a voltage transducer lag TR (GridSim's EXAC1 has none)");
                p.Add(("model", "EXAC1")); p.Add(("acLagS", V(r, 1, "TB"))); p.Add(("acLeadS", V(r, 2, "TC")));
                p.Add(("acRegulatorGain", V(r, 3, "KA"))); p.Add(("acRegulatorTimeS", V(r, 4, "TA")));
                p.Add(("acRegulatorMaxPu", V(r, 5, "VRMAX"))); p.Add(("acRegulatorMinPu", V(r, 6, "VRMIN")));
                p.Add(("acExciterTimeS", V(r, 7, "TE"))); p.Add(("acFeedbackGain", V(r, 8, "KF"))); p.Add(("acFeedbackTimeS", V(r, 9, "TF")));
                p.Add(("acRectifierKc", V(r, 10, "KC"))); p.Add(("acDemagnetisingKd", V(r, 11, "KD"))); p.Add(("acExciterConstant", V(r, 12, "KE")));
                p.Add(("acSatE1", V(r, 13, "E1"))); p.Add(("acSatSe1", V(r, 14, "SE(E1)"))); p.Add(("acSatE2", V(r, 15, "E2")));
                p.Add(("acSatSe2", V(r, 16, "SE(E2)")));
                break;
            case "ESST1A":
            {
                // UEL VOS TR VIMAX VIMIN TC TB TC1 TB1 KA TA VAMAX VAMIN VRMAX VRMIN KC KF TF KLR ILR
                double uel = V(r, 0, "UEL"), vos = V(r, 1, "VOS");
                Need(uel is 1.0 or 2.0 or 3.0, "UEL not 1, 2 or 3");
                Need(vos is 1.0 or 2.0, "VOS not 1 or 2");
                p.Add(("model", "ESST1A")); p.Add(("st1TrS", V(r, 2, "TR"))); p.Add(("st1ViMaxPu", V(r, 3, "VIMAX"))); p.Add(("st1ViMinPu", V(r, 4, "VIMIN")));
                p.Add(("st1TcS", V(r, 5, "TC"))); p.Add(("st1TbS", V(r, 6, "TB"))); p.Add(("st1aTc1S", V(r, 7, "TC1"))); p.Add(("st1aTb1S", V(r, 8, "TB1")));
                p.Add(("st1Ka", V(r, 9, "KA"))); p.Add(("st1TaS", V(r, 10, "TA"))); p.Add(("st1aVaMaxPu", V(r, 11, "VAMAX"))); p.Add(("st1aVaMinPu", V(r, 12, "VAMIN")));
                p.Add(("st1VrMaxPu", V(r, 13, "VRMAX"))); p.Add(("st1VrMinPu", V(r, 14, "VRMIN"))); p.Add(("st1Kc", V(r, 15, "KC"))); p.Add(("st1Kf", V(r, 16, "KF")));
                p.Add(("st1TfS", V(r, 17, "TF"))); p.Add(("st1aKlr", V(r, 18, "KLR"))); p.Add(("st1aIlrPu", V(r, 19, "ILR"))); p.Add(("st1aVos", vos));
                break;
            }
            case "EXST1":
                // TR VIMAX VIMIN TC TB KA TA VRMAX VRMIN KC KF TF
                p.Add(("model", "EXST1")); p.Add(("st1TrS", V(r, 0, "TR"))); p.Add(("st1ViMaxPu", V(r, 1, "VIMAX"))); p.Add(("st1ViMinPu", V(r, 2, "VIMIN")));
                p.Add(("st1TcS", V(r, 3, "TC"))); p.Add(("st1TbS", V(r, 4, "TB"))); p.Add(("st1Ka", V(r, 5, "KA"))); p.Add(("st1TaS", V(r, 6, "TA")));
                p.Add(("st1VrMaxPu", V(r, 7, "VRMAX"))); p.Add(("st1VrMinPu", V(r, 8, "VRMIN"))); p.Add(("st1Kc", V(r, 9, "KC"))); p.Add(("st1Kf", V(r, 10, "KF")));
                p.Add(("st1TfS", V(r, 11, "TF")));
                break;
            case "SCRX":
            {
                // TA/TB TB K TE EMIN EMAX CSWITCH rc/rfd
                double ratio = V(r, 0, "TA/TB"), tb = V(r, 1, "TB"), cs = V(r, 6, "CSWITCH");
                Need(cs is 0.0 or 1.0, "CSWITCH not 0 (bus fed) or 1 (solid fed)");
                p.Add(("model", "SCRX")); p.Add(("scrxLeadS", ratio * tb)); p.Add(("scrxLagS", tb)); p.Add(("scrxGain", V(r, 2, "K")));
                p.Add(("scrxExciterTimeS", V(r, 3, "TE"))); p.Add(("scrxFieldMinPu", V(r, 4, "EMIN"))); p.Add(("scrxFieldMaxPu", V(r, 5, "EMAX")));
                p.Add(("scrxSourceSwitch", cs)); p.Add(("scrxCrowbarRatio", V(r, 7, "rc/rfd")));
                derived["scrxLeadS"] = "TA = (TA/TB) x TB.";
                break;
            }
            case "ESAC1A":
                // TR TB TC KA TA VAMAX VAMIN TE KF TF KC KD KE E1 SE(E1) E2 SE(E2) VRMAX VRMIN
                Need(V(r, 0, "TR") == 0, "a voltage transducer lag TR (GridSim's ESAC1A has none)");
                p.Add(("model", "ESAC1A")); p.Add(("acLagS", V(r, 1, "TB"))); p.Add(("acLeadS", V(r, 2, "TC")));
                p.Add(("acRegulatorGain", V(r, 3, "KA"))); p.Add(("acRegulatorTimeS", V(r, 4, "TA")));
                p.Add(("acRegulatorMaxPu", V(r, 5, "VAMAX"))); p.Add(("acRegulatorMinPu", V(r, 6, "VAMIN")));
                p.Add(("acExciterTimeS", V(r, 7, "TE"))); p.Add(("acFeedbackGain", V(r, 8, "KF"))); p.Add(("acFeedbackTimeS", V(r, 9, "TF")));
                p.Add(("acRectifierKc", V(r, 10, "KC"))); p.Add(("acDemagnetisingKd", V(r, 11, "KD"))); p.Add(("acExciterConstant", V(r, 12, "KE")));
                p.Add(("acSatE1", V(r, 13, "E1"))); p.Add(("acSatSe1", V(r, 14, "SE(E1)"))); p.Add(("acSatE2", V(r, 15, "E2")));
                p.Add(("acSatSe2", V(r, 16, "SE(E2)"))); p.Add(("acOutputMaxPu", V(r, 17, "VRMAX"))); p.Add(("acOutputMinPu", V(r, 18, "VRMIN")));
                break;
            case "ESDC1A":
            {
                // TR KA TA TB TC VRMAX VRMIN KE TE KF TF1 SWITCH E1 SE(E1) E2 SE(E2)
                Need(V(r, 7, "KE") != 0, "KE = 0, which PSS/E replaces by a value computed at initialisation");
                Need(V(r, 5, "VRMAX") != 0, "VRMAX = 0, which PSS/E replaces by a value computed at initialisation");
                Need(V(r, 11, "SWITCH") == 0, "SWITCH not 0");
                p.Add(("model", "ESDC1A")); p.Add(("dc1aTrS", V(r, 0, "TR"))); p.Add(("dc1aKa", V(r, 1, "KA"))); p.Add(("dc1aTaS", V(r, 2, "TA")));
                p.Add(("dc1aTbS", V(r, 3, "TB"))); p.Add(("dc1aTcS", V(r, 4, "TC"))); p.Add(("dc1aVrMaxPu", V(r, 5, "VRMAX"))); p.Add(("dc1aVrMinPu", V(r, 6, "VRMIN")));
                p.Add(("dc1aKe", V(r, 7, "KE"))); p.Add(("dc1aTeS", V(r, 8, "TE"))); p.Add(("dc1aKf", V(r, 9, "KF"))); p.Add(("dc1aTf1S", V(r, 10, "TF1")));
                p.Add(("dc1aSatE1", V(r, 12, "E1"))); p.Add(("dc1aSatSe1", V(r, 13, "SE(E1)"))); p.Add(("dc1aSatE2", V(r, 14, "E2"))); p.Add(("dc1aSatSe2", V(r, 15, "SE(E2)")));
                break;
            }
            case "EXDC2":
            case "IEEEX1":
            {
                // TR KA TA TB TC VRMAX VRMIN KE TE KF TF1 SWITCH E1 SE(E1) E2 SE(E2) (WP19; the ESDC1A parameters of the file route)
                Need(V(r, 7, "KE") != 0, "KE = 0, which PSS/E replaces by a value computed at initialisation");
                Need(V(r, 5, "VRMAX") != 0, "VRMAX = 0, which PSS/E replaces by a value computed at initialisation");
                Need(V(r, 11, "SWITCH") == 0, "SWITCH not 0");
                p.Add(("model", r.Model)); p.Add(("dc1aTrS", V(r, 0, "TR"))); p.Add(("dc1aKa", V(r, 1, "KA"))); p.Add(("dc1aTaS", V(r, 2, "TA")));
                p.Add(("dc1aTbS", V(r, 3, "TB"))); p.Add(("dc1aTcS", V(r, 4, "TC"))); p.Add(("dc1aVrMaxPu", V(r, 5, "VRMAX"))); p.Add(("dc1aVrMinPu", V(r, 6, "VRMIN")));
                p.Add(("dc1aKe", V(r, 7, "KE"))); p.Add(("dc1aTeS", V(r, 8, "TE"))); p.Add(("dc1aKf", V(r, 9, "KF"))); p.Add(("dc1aTf1S", V(r, 10, "TF1")));
                p.Add(("dc1aSatE1", V(r, 12, "E1"))); p.Add(("dc1aSatSe1", V(r, 13, "SE(E1)"))); p.Add(("dc1aSatE2", V(r, 14, "E2"))); p.Add(("dc1aSatSe2", V(r, 15, "SE(E2)")));
                break;
            }
            case "ESAC8B":
            {
                // TR KP KI KD TD KA TA VRMAX VRMIN TE KE E1 SE(E1) E2 SE(E2) (WP19)
                Need(V(r, 7, "VRMAX") != 0, "VRMAX = 0, which PSS/E replaces by a value computed at initialisation");
                Need(V(r, 10, "KE") != 0, "KE = 0 (refused: PSS/E's handling of KE = 0 for ESAC8B is not confirmed)");
                string[] names = { "ac8bTrS", "ac8bKp", "ac8bKi", "ac8bKd", "ac8bTdS", "ac8bKa", "ac8bTaS", "ac8bVrMaxPu", "ac8bVrMinPu", "ac8bTeS", "ac8bKe",
                    "ac8bSatE1", "ac8bSatSe1", "ac8bSatE2", "ac8bSatSe2" };
                string[] labels = { "TR", "KP", "KI", "KD", "TD", "KA", "TA", "VRMAX", "VRMIN", "TE", "KE", "E1", "SE(E1)", "E2", "SE(E2)" };
                p.Add(("model", "ESAC8B"));
                for (int k = 0; k < names.Length; k++) p.Add((names[k], V(r, k, labels[k])));
                break;
            }
            case "ESST3A":
            {
                // TR VIMAX VIMIN KM TC TB KA TA VRMAX VRMIN KG KP KI VBMAX KC XL VGMAX THETAP TM VMMAX VMMIN (WP19)
                string[] names = { "st3aTrS", "st3aViMaxPu", "st3aViMinPu", "st3aKm", "st3aTcS", "st3aTbS", "st3aKa", "st3aTaS", "st3aVrMaxPu", "st3aVrMinPu",
                    "st3aKg", "st3aKp", "st3aKi", "st3aVbMaxPu", "st3aKc", "st3aXlPu", "st3aVgMaxPu", "st3aThetapDeg", "st3aTmS", "st3aVmMaxPu", "st3aVmMinPu" };
                string[] labels = { "TR", "VIMAX", "VIMIN", "KM", "TC", "TB", "KA", "TA", "VRMAX", "VRMIN", "KG", "KP", "KI", "VBMAX", "KC", "XL", "VGMAX",
                    "THETAP", "TM", "VMMAX", "VMMIN" };
                p.Add(("model", "ESST3A"));
                for (int k = 0; k < names.Length; k++) p.Add((names[k], V(r, k, labels[k])));
                break;
            }
            case "IEEEST":
            {
                // ICS IB A1 A2 A3 A4 A5 A6 T1 T2 T3 T4 T5 T6 KS LSMAX LSMIN VCU VCL
                double ics = V(r, 0, "ICS");
                Need(ics is 1.0 or 3.0, "an input other than rotor speed deviation (ICS 1) or electrical power (ICS 3)");
                Need(V(r, 1, "IB") == 0, "a remote bus input (IB not 0)");
                Need(V(r, 3, "A2") == 0 && V(r, 5, "A4") == 0 && V(r, 7, "A6") == 0, "the second-order filter terms A2, A4, A6 (GridSim's IEEEST filter is first order)");
                p.Add(("model", "IEEEST")); p.Add(("ieeestInput", ics)); p.Add(("ieeestA1S", V(r, 2, "A1"))); p.Add(("ieeestA3S", V(r, 4, "A3")));
                p.Add(("ieeestA5S", V(r, 6, "A5"))); p.Add(("ieeestT1S", V(r, 8, "T1"))); p.Add(("ieeestT2S", V(r, 9, "T2"))); p.Add(("ieeestT3S", V(r, 10, "T3")));
                p.Add(("ieeestT4S", V(r, 11, "T4"))); p.Add(("ieeestT5S", V(r, 12, "T5"))); p.Add(("ieeestT6S", V(r, 13, "T6"))); p.Add(("ieeestKs", V(r, 14, "KS")));
                p.Add(("ieeestOutputMaxPu", V(r, 15, "LSMAX"))); p.Add(("ieeestOutputMinPu", V(r, 16, "LSMIN")));
                p.Add(("ieeestVcuPu", V(r, 17, "VCU"))); p.Add(("ieeestVclPu", V(r, 18, "VCL")));
                break;
            }
            case "PSS2A":
            case "PSS2B":
            {
                // ICS1 IB1 ICS2 IB2 M N TW1 TW2 T6 TW3 TW4 T7 KS2 KS3 T8 T9 KS1 T1 T2 T3 T4 [T10 T11 VS1MAX VS1MIN VS2MAX VS2MIN] VSTMAX VSTMIN
                bool b = r.Model == "PSS2B";
                double ics1 = V(r, 0, "ICS1"), ics2 = V(r, 2, "ICS2");
                Need(ics1 is 1.0 or 3.0 && ics2 is 1.0 or 3.0, "an input other than rotor speed deviation (1) or electrical power (3)");
                Need(V(r, 1, "IB1") == 0 && V(r, 3, "IB2") == 0, "a remote bus input (IB1 or IB2 not 0)");
                p.Add(("model", r.Model)); p.Add(("pss2Input1", ics1)); p.Add(("pss2Input2", ics2)); p.Add(("pss2M", V(r, 4, "M"))); p.Add(("pss2N", V(r, 5, "N")));
                p.Add(("pss2Tw1S", V(r, 6, "TW1"))); p.Add(("pss2Tw2S", V(r, 7, "TW2"))); p.Add(("pss2T6S", V(r, 8, "T6"))); p.Add(("pss2Tw3S", V(r, 9, "TW3")));
                p.Add(("pss2Tw4S", V(r, 10, "TW4"))); p.Add(("pss2T7S", V(r, 11, "T7"))); p.Add(("pss2Ks2", V(r, 12, "KS2"))); p.Add(("pss2Ks3", V(r, 13, "KS3")));
                p.Add(("pss2T8S", V(r, 14, "T8"))); p.Add(("pss2T9S", V(r, 15, "T9"))); p.Add(("pss2Ks1", V(r, 16, "KS1"))); p.Add(("pss2T1S", V(r, 17, "T1")));
                p.Add(("pss2T2S", V(r, 18, "T2"))); p.Add(("pss2T3S", V(r, 19, "T3"))); p.Add(("pss2T4S", V(r, 20, "T4")));
                if (b)
                {
                    p.Add(("pss2bT10S", V(r, 21, "T10"))); p.Add(("pss2bT11S", V(r, 22, "T11")));
                    p.Add(("pss2bInput1MaxPu", V(r, 23, "VS1MAX"))); p.Add(("pss2bInput1MinPu", V(r, 24, "VS1MIN")));
                    p.Add(("pss2bInput2MaxPu", V(r, 25, "VS2MAX"))); p.Add(("pss2bInput2MinPu", V(r, 26, "VS2MIN")));
                }
                p.Add(("pss2OutputMaxPu", V(r, b ? 27 : 21, "VSTMAX"))); p.Add(("pss2OutputMinPu", V(r, b ? 28 : 22, "VSTMIN")));
                break;
            }
            case "PSS1A":
            {
                // ICS IB A1 A2 T1 T2 T3 T4 T5 T6 KS LSMAX LSMIN VCU VCL
                Need(V(r, 0, "ICS") == 1, "an input other than rotor speed deviation (ICS not 1)");
                Need(V(r, 1, "IB") == 0, "a remote bus input (IB not 0)");
                Need(V(r, 2, "A1") == 0 && V(r, 3, "A2") == 0, "the A1 / A2 filter (GridSim's PSS1A has none)");
                double t5 = V(r, 8, "T5"), t6 = V(r, 9, "T6");
                Need(t5 == t6, "a washout sT5/(1+sT6) with T5 not equal to T6 (GridSim's washout is sTw/(1+sTw))");
                Need(V(r, 13, "VCU") == 0 && V(r, 14, "VCL") == 0, "the VCU / VCL voltage cut-offs");
                p.Add(("model", "PSS1A")); p.Add(("gain", V(r, 10, "KS"))); p.Add(("washoutS", t5));
                p.Add(("lead1S", V(r, 4, "T1"))); p.Add(("lag1S", V(r, 5, "T2"))); p.Add(("lead2S", V(r, 6, "T3"))); p.Add(("lag2S", V(r, 7, "T4")));
                p.Add(("outputMaxPu", V(r, 11, "LSMAX"))); p.Add(("outputMinPu", V(r, 12, "LSMIN")));
                break;
            }
            case "TGOV1":
                // R T1 VMAX VMIN T2 T3 Dt
                p.Add(("model", "TGOV1")); p.Add(("droopPu", V(r, 0, "R"))); p.Add(("tgov1ValveTimeS", V(r, 1, "T1")));
                p.Add(("valveMaxPu", V(r, 2, "VMAX"))); p.Add(("valveMinPu", V(r, 3, "VMIN"))); p.Add(("tgov1ReheatLeadS", V(r, 4, "T2")));
                p.Add(("tgov1ReheatLagS", V(r, 5, "T3"))); p.Add(("turbineDampingPu", V(r, 6, "Dt")));
                break;
            case "IEEEG1":
            {
                // JBUS M K T1 T2 T3 Uo Uc PMAX PMIN T4 K1 K2 T5 K3 K4 T6 K5 K6 T7 K7 K8
                Need(V(r, 0, "JBUS") == 0, "a second (low-pressure) shaft at another bus (JBUS not 0)");
                double k = V(r, 2, "K");
                Need(k > 0, "K not above 0, so there is no droop R = 1 / K");
                p.Add(("model", "IEEEG1")); p.Add(("droopPu", k > 0 ? 1.0 / k : 0.0));
                p.Add(("ieeeg1LagS", V(r, 3, "T1"))); p.Add(("ieeeg1LeadS", V(r, 4, "T2"))); p.Add(("ieeeg1ValveTimeS", V(r, 5, "T3")));
                p.Add(("ieeeg1UoPuPerS", V(r, 6, "Uo"))); p.Add(("ieeeg1UcPuPerS", V(r, 7, "Uc")));
                p.Add(("valveMaxPu", V(r, 8, "PMAX"))); p.Add(("valveMinPu", V(r, 9, "PMIN"))); p.Add(("ieeeg1ChestS", V(r, 10, "T4")));
                p.Add(("ieeeg1K1", V(r, 11, "K1"))); p.Add(("ieeeg1K2", V(r, 12, "K2"))); p.Add(("ieeeg1ReheatS", V(r, 13, "T5")));
                p.Add(("ieeeg1K3", V(r, 14, "K3"))); p.Add(("ieeeg1K4", V(r, 15, "K4"))); p.Add(("ieeeg1CrossoverS", V(r, 16, "T6")));
                p.Add(("ieeeg1K5", V(r, 17, "K5"))); p.Add(("ieeeg1K6", V(r, 18, "K6"))); p.Add(("ieeeg1LpS", V(r, 19, "T7")));
                p.Add(("ieeeg1K7", V(r, 20, "K7"))); p.Add(("ieeeg1K8", V(r, 21, "K8")));
                derived["droopPu"] = "R = 1 / K.";
                break;
            }
            case "GAST":
                // R T1 T2 T3 AT KT VMAX VMIN DTURB (every value has a GridSim parameter of the same meaning)
                p.Add(("model", "GAST")); p.Add(("droopPu", V(r, 0, "R"))); p.Add(("gastValveTimeS", V(r, 1, "T1")));
                p.Add(("gastTurbineTimeS", V(r, 2, "T2"))); p.Add(("gastExhaustTimeS", V(r, 3, "T3"))); p.Add(("gastAmbientLimitPu", V(r, 4, "AT")));
                p.Add(("gastTemperatureGain", V(r, 5, "KT"))); p.Add(("valveMaxPu", V(r, 6, "VMAX"))); p.Add(("valveMinPu", V(r, 7, "VMIN")));
                p.Add(("turbineDampingPu", V(r, 8, "DTURB")));
                break;
            case "GGOV1":
            {
                // Rselect Flag R Tpelec maxerr minerr Kpgov Kigov Kdgov Tdgov Vmax Vmin Tact Kturb Wfnl Tb Tc Teng Tfload Kpload Kiload
                // Ldref Dm Ropen Rclose Kimw Aset Ka Ta Trate db Tsa Tsb Rup Rdown (WP3b; sources at ExtendedModels)
                double rs = V(r, 0, "Rselect"), fl = V(r, 1, "Flag");
                Need(rs is 1.0 or 0.0 or -1.0 or -2.0, "Rselect not 1, 0, -1 or -2");
                Need(fl is 0.0 or 1.0, "Flag not 0 or 1");
                Need(V(r, 17, "Teng") == 0, "a transport delay Teng (GridSim's GGOV1 has none)");
                Need(V(r, 30, "db") == 0, "a speed deadband db (not modelled)");
                Need(V(r, 22, "Dm") >= 0, "a negative Dm (PSS/E's speed-dependent maximum fuel flow, not modelled)");
                string[] names = { "ggov1Rselect", "ggov1Flag", "droopPu", "ggov1TpelecS", "ggov1MaxErrPu", "ggov1MinErrPu", "ggov1Kpgov", "ggov1Kigov",
                    "ggov1Kdgov", "ggov1TdgovS", "valveMaxPu", "valveMinPu", "ggov1TactS", "ggov1Kturb", "ggov1WfnlPu", "ggov1TbS", "ggov1TcS", "ggov1TengS",
                    "ggov1TfloadS", "ggov1Kpload", "ggov1Kiload", "ggov1LdrefPu", "turbineDampingPu", "ggov1RopenPuPerS", "ggov1RclosePuPerS", "ggov1Kimw",
                    "ggov1AsetPuPerS", "ggov1Ka", "ggov1TaS", "ggov1TrateMw", "ggov1DbPu", "ggov1TsaS", "ggov1TsbS", "ggov1RupPuPerS", "ggov1RdownPuPerS" };
                string[] labels = { "Rselect", "Flag", "R", "Tpelec", "maxerr", "minerr", "Kpgov", "Kigov", "Kdgov", "Tdgov", "Vmax", "Vmin", "Tact", "Kturb",
                    "Wfnl", "Tb", "Tc", "Teng", "Tfload", "Kpload", "Kiload", "Ldref", "Dm", "Ropen", "Rclose", "Kimw", "Aset", "Ka", "Ta", "Trate", "db",
                    "Tsa", "Tsb", "Rup", "Rdown" };
                p.Add(("model", "GGOV1"));
                for (int k = 0; k < names.Length; k++) p.Add((names[k], V(r, k, labels[k])));
                break;
            }
            case "WSIEG1":
            {
                // JBUS M K T1 T2 T3 Uo Uc PMAX PMIN T4 K1 K2 T5 K3 K4 T6 K5 K6 T7 K7 K8 db1 err db2 GV1 PGV1 .. GV5 PGV5 IBLOCK (WP3b)
                Need(V(r, 0, "JBUS") == 0 && V(r, 1, "M") == 0, "a second (low-pressure) shaft at another machine (JBUS or M not 0)");
                Need(V(r, 22, "db1") == 0 && V(r, 23, "err") == 0, "the intentional deadband db1 with its hysteresis err (not modelled)");
                Need(V(r, 24, "db2") == 0, "the unintentional deadband db2 (not modelled)");
                double ib = V(r, 35, "IBLOCK");
                Need(ib is 0.0 or 1.0 or 2.0 or 3.0, "IBLOCK not 0, 1, 2 or 3");
                string[] names = { "wsieg1K", "wsieg1T1S", "wsieg1T2S", "wsieg1T3S", "wsieg1UoPuPerS", "wsieg1UcPuPerS", "valveMaxPu", "valveMinPu", "wsieg1T4S",
                    "wsieg1K1", "wsieg1K2", "wsieg1T5S", "wsieg1K3", "wsieg1K4", "wsieg1T6S", "wsieg1K5", "wsieg1K6", "wsieg1T7S", "wsieg1K7", "wsieg1K8",
                    "wsieg1Db1Pu", "wsieg1ErrPu", "wsieg1Db2Pu", "wsieg1Gv1", "wsieg1Pgv1", "wsieg1Gv2", "wsieg1Pgv2", "wsieg1Gv3", "wsieg1Pgv3",
                    "wsieg1Gv4", "wsieg1Pgv4", "wsieg1Gv5", "wsieg1Pgv5", "wsieg1Iblock" };
                string[] labels = { "K", "T1", "T2", "T3", "Uo", "Uc", "PMAX", "PMIN", "T4", "K1", "K2", "T5", "K3", "K4", "T6", "K5", "K6", "T7", "K7", "K8",
                    "db1", "err", "db2", "GV1", "PGV1", "GV2", "PGV2", "GV3", "PGV3", "GV4", "PGV4", "GV5", "PGV5", "IBLOCK" };
                p.Add(("model", "WSIEG1"));
                for (int k = 0; k < names.Length; k++) p.Add((names[k], V(r, k + 2, labels[k])));
                break;
            }
            case "IEEEG3":
            {
                // TG TP Uo Uc PMAX PMIN sigma delta TR TW a11 a13 a21 a23 (WP19)
                string[] names = { "ieeeg3TgS", "ieeeg3TpS", "ieeeg3UoPuPerS", "ieeeg3UcPuPerS", "valveMaxPu", "valveMinPu", "droopPu", "ieeeg3TransientDroopPu",
                    "ieeeg3TrS", "ieeeg3TwS", "ieeeg3A11", "ieeeg3A13", "ieeeg3A21", "ieeeg3A23" };
                string[] labels = { "TG", "TP", "Uo", "Uc", "PMAX", "PMIN", "sigma", "delta", "TR", "TW", "a11", "a13", "a21", "a23" };
                p.Add(("model", "IEEEG3"));
                for (int k = 0; k < names.Length; k++) p.Add((names[k], V(r, k, labels[k])));
                break;
            }
            case "HYGOV":
                // R r Tr Tf Tg VELM GMAX GMIN TW At Dturb qNL
                Need(V(r, 3, "Tf") == 0, "a filter time Tf (GridSim's hydro governor has none)");
                Need(V(r, 5, "VELM") == 0, "a gate rate limit VELM");
                Need(V(r, 9, "At") == 1, "a turbine gain At other than 1");
                Need(V(r, 10, "Dturb") == 0, "turbine damping Dturb");
                Need(V(r, 11, "qNL") == 0, "a no-load flow qNL");
                p.Add(("model", "HYGOV")); p.Add(("droopPu", V(r, 0, "R"))); p.Add(("hygovTransientDroopPu", V(r, 1, "r")));
                p.Add(("hygovResetTimeS", V(r, 2, "Tr"))); p.Add(("hygovGateTimeS", V(r, 4, "Tg"))); p.Add(("valveMaxPu", V(r, 6, "GMAX")));
                p.Add(("valveMinPu", V(r, 7, "GMIN"))); p.Add(("hygovWaterTimeS", V(r, 8, "TW")));
                break;
            default:
                throw new InvalidOperationException($"internal error: no mapping for {r.Model}");
        }
        return issues.Count == before;
    }

    private static void WriteTemplate(Utf8JsonWriter w, string id, string kind, string title, List<(string Name, object Value)> p,
        Dictionary<string, string> derived, string source, PsseDyrRecord rec, List<(string Slot, string Ref)>? controllers = null)
    {
        w.WriteStartObject();
        w.WriteString("id", id);
        w.WriteNumber("rev", 1);
        w.WriteString("kind", kind);
        w.WriteString("status", "active");
        w.WriteString("title", title);
        w.WriteStartObject("params");
        foreach ((string name, object value) in p)
        {
            if (value is string s) w.WriteString(name, s);
            else w.WriteNumber(name, (double)value);
        }
        w.WriteEndObject();
        if (controllers is { Count: > 0 })
        {
            w.WriteStartObject("controllers");
            foreach ((string slot, string tref) in controllers) w.WriteString(slot, tref);
            w.WriteEndObject();
        }
        w.WriteStartObject("provenance");
        w.WriteStartObject("_default");
        w.WriteString("tag", "Recorded");
        w.WriteString("note", $"PSS/E DYR {source}, line {rec.Line.ToString(CultureInfo.InvariantCulture)}: {rec.Model} record.");
        w.WriteEndObject();
        foreach (var kv in derived)
        {
            w.WriteStartObject(kv.Key);
            w.WriteString("tag", "DerivedFromRecorded");
            w.WriteString("note", $"PSS/E DYR {source}, line {rec.Line.ToString(CultureInfo.InvariantCulture)}: {kv.Value}");
            w.WriteEndObject();
        }
        w.WriteEndObject();
        w.WriteEndObject();
    }
}
