using System.Globalization;
using GridSim.Core.Dynamics.Models.Wecc;

namespace GridSim.Core.IO.Psse;

/// <summary>
/// The PSS/E DYR records of the WECC second-generation renewable models that GridSim maps (WECC expansion), and their
/// mapping onto <see cref="WeccPlant"/> parameter sets. Each model has a fixed record: its integer parameters (ICONs)
/// then its real parameters (CONs), in the PSS/E order listed with it below.
///
/// <para><b>Names.</b> The PSS/E library names (REGCA1, REECA1, ...; V34 and later), the PSS/E V33 user-model names
/// (REGCAU1, REECAU1, ...) and the WECC / PowerWorld names (REGC_A, REEC_A, ...) all name the same model. A V33 user model
/// may also be written as a <c>USRMDL</c> record (<c>IBUS 'USRMDL' ID 'REECCU1' IC IT NI NC NS NV ICONs CONs</c>); the six
/// header integers are checked (NI and NC must match the model's counts) and dropped.</para>
///
/// <para><b>Record order sources</b> (WECC expansion, DECISIONS in the branch notes): REGCA1, REECA1 and REECB1 as the
/// open-source ANDES PSS/E DYR map lists them, which agrees with the EPRI 3002006525 parameter lists and the reader's
/// historical REGC_A value count (15); REECCU1 as the open-source OpenIPSL REECCU1 model declares its parameters (the one
/// public source found; its order is flagged as single-source).</para>
///
/// <para><b>Nothing is dropped silently.</b> A value out of its range (a flag not in its set, a negative time constant,
/// unordered breakpoints, ...) is listed by the parameter set's own check and the file is refused with every problem.
/// REGC_A's Accel (a PSS/E network-solution acceleration factor, not a model dynamic) is range-checked and named in the
/// notes: GridSim solves the converter / network fixed point to 1e-10 pu, so its answer does not depend on it.</para>
/// </summary>
public static class PsseDyrRenewables
{
    /// <summary>The role each mapped renewable model plays on its unit.</summary>
    public const string ConverterRole = "converter", ElectricalRole = "electrical", PlantRole = "plant",
        DriveTrainRole = "drive train", AeroRole = "aerodynamics", PitchRole = "pitch", TorqueRole = "torque", DerRole = "der",
        GfmRole = "grid forming";

    /// <summary>Every renewable role, for the reader's bookkeeping.</summary>
    public static readonly IReadOnlyList<string> AllRoles = new[] { ConverterRole, ElectricalRole, PlantRole, DriveTrainRole, AeroRole, PitchRole, TorqueRole, DerRole, GfmRole };

    /// <summary>
    /// Renewable models whose PSS/E record order no public source gives, refused by name with the reason (never read with a
    /// guessed order): PSS/E's DERA1 / DERAU1. DER_A itself is modelled; write it as a <c>'DER_A'</c> record with its 48
    /// parameters in the order of the WECC specification's Table 1 (see <see cref="Dera"/>).
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> RefusedOrder = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["DERA1"] = "DER_A", ["DERAU1"] = "DER_A",
        // WECC expansion slice 6/7: the PSS/E record orders of REGCB1, REECDU1 and REGFMA1 are not in any public source either;
        // GridSim reads 'REGC_B', 'REEC_D' and 'REGFM_A1' records in the orders of the public specifications (DECISIONS D30).
        ["REGCB1"] = "REGC_B", ["REGCBU1"] = "REGC_B",
        ["REECD1"] = "REEC_D", ["REECDU1"] = "REEC_D",
        ["REGFMA1"] = "REGFM_A1", ["REGFMAU1"] = "REGFM_A1", ["REGFM_A"] = "REGFM_A1",
    };

    /// <summary>Every accepted record name, mapped to its WECC model name.</summary>
    public static readonly IReadOnlyDictionary<string, string> Names = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["REGCA1"] = "REGC_A", ["REGCAU1"] = "REGC_A", ["REGC_A"] = "REGC_A",
        ["REECA1"] = "REEC_A", ["REECAU1"] = "REEC_A", ["REEC_A"] = "REEC_A",
        ["REECB1"] = "REEC_B", ["REECBU1"] = "REEC_B", ["REEC_B"] = "REEC_B",
        ["REECCU1"] = "REEC_C", ["REECC1"] = "REEC_C", ["REEC_C"] = "REEC_C",
        ["REPCA1"] = "REPC_A", ["REPCAU1"] = "REPC_A", ["REPC_A"] = "REPC_A",
        ["WTDTA1"] = "WTGT_A", ["WTDTAU1"] = "WTGT_A", ["WTGT_A"] = "WTGT_A",
        ["WTARA1"] = "WTGAR_A", ["WTARAU1"] = "WTGAR_A", ["WTGAR_A"] = "WTGAR_A", ["WTGA_A"] = "WTGAR_A",
        ["WTPTA1"] = "WTGPT_A", ["WTPTAU1"] = "WTGPT_A", ["WTGPT_A"] = "WTGPT_A", ["WTGP_A"] = "WTGPT_A",
        ["WTTQA1"] = "WTGTRQ_A", ["WTTQAU1"] = "WTGTRQ_A", ["WTGTRQ_A"] = "WTGTRQ_A", ["WTGQ_A"] = "WTGTRQ_A",
        ["DER_A"] = "DER_A",
        ["REGC_B"] = "REGC_B", ["REEC_D"] = "REEC_D", ["REGFM_A1"] = "REGFM_A1",
    };

    /// <summary>The role of each WECC model.</summary>
    public static readonly IReadOnlyDictionary<string, string> Roles = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["REGC_A"] = ConverterRole, ["REEC_A"] = ElectricalRole, ["REEC_B"] = ElectricalRole, ["REEC_C"] = ElectricalRole,
        ["REPC_A"] = PlantRole, ["WTGT_A"] = DriveTrainRole, ["WTGAR_A"] = AeroRole, ["WTGPT_A"] = PitchRole, ["WTGTRQ_A"] = TorqueRole,
        ["DER_A"] = DerRole,
        ["REGC_B"] = ConverterRole, ["REEC_D"] = ElectricalRole, ["REGFM_A1"] = GfmRole,
    };

    /// <summary>The (ICON, CON) counts of each WECC model's PSS/E record.</summary>
    public static readonly IReadOnlyDictionary<string, (int Icons, int Cons)> Counts = new Dictionary<string, (int, int)>(StringComparer.Ordinal)
    {
        ["REGC_A"] = (1, 14), ["REEC_A"] = (6, 45), ["REEC_B"] = (5, 25), ["REEC_C"] = (5, 45), ["REPC_A"] = (7, 27),
        ["WTGT_A"] = (0, 5), ["WTGAR_A"] = (0, 2), ["WTGPT_A"] = (0, 10), ["WTGTRQ_A"] = (1, 15), ["DER_A"] = (0, 48),
        ["REGC_B"] = (0, 11), ["REEC_D"] = (6, 78), ["REGFM_A1"] = (0, 22),
    };

    /// <summary>The WECC model a record names, or null when it is not a mapped renewable model. A USRMDL record names its
    /// model in its first value.</summary>
    public static string? ModelOf(PsseDyrRecord r)
    {
        ArgumentNullException.ThrowIfNull(r);
        if (r.Model == "USRMDL")
            return r.Values.Count > 0 && Names.TryGetValue(r.Values[0].Trim().Trim('\'').ToUpperInvariant(), out string? u) ? u : null;
        return Names.TryGetValue(r.Model, out string? m) ? m : null;
    }

    /// <summary>The parameter values of a renewable record (ICONs then CONs), the USRMDL header removed and checked.
    /// Throws, naming the line, when the count is wrong.</summary>
    public static IReadOnlyList<string> ValuesOf(PsseDyrRecord r, string model)
    {
        (int ni, int nc) = Counts[model];
        IReadOnlyList<string> v = r.Values;
        if (r.Model == "USRMDL")
        {
            // 'name' IC IT NI NC NS NV, then the NI ICONs and NC CONs.
            if (v.Count < 7)
                throw new InvalidDataException($"PSS/E DYR line {r.Line}: USRMDL {model} needs the model name and six header integers (IC IT NI NC NS NV) before its parameters.");
            int hni = Whole(r, v[3], "NI"), hnc = Whole(r, v[4], "NC");
            if (hni != ni || hnc != nc)
                throw new InvalidDataException($"PSS/E DYR line {r.Line}: USRMDL {model} declares NI = {hni} and NC = {hnc}; GridSim's {model} record has {ni} ICONs and {nc} CONs.");
            v = v.Skip(7).ToList();
        }
        if (v.Count != ni + nc)
            throw new InvalidDataException($"PSS/E DYR line {r.Line}: {model} needs exactly {ni + nc} values after the machine ID ({ni} ICONs, {nc} CONs); the record has {v.Count}.");
        return v;
    }

    private static int Whole(PsseDyrRecord r, string s, string name)
    {
        double x = Num(r, s, name);
        if (x != Math.Floor(x) || Math.Abs(x) > int.MaxValue)
            throw new InvalidDataException($"PSS/E DYR line {r.Line}: {r.Model} {name} is '{s}'; it must be a whole number.");
        return (int)x;
    }

    private static double Num(PsseDyrRecord r, string s, string name)
    {
        string t = s.Replace('D', 'E').Replace('d', 'e');
        if (!double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) || !double.IsFinite(v))
            throw new InvalidDataException($"PSS/E DYR line {r.Line}: {r.Model} {name} is '{s}'; it must be a finite number.");
        return v;
    }

    /// <summary>
    /// REPCA1: ICONs BUSR BUS1 BUS2 CKTID VCFlag RefFlag Fflag (CKTID is the flow branch's circuit identifier, a string);
    /// CONs Tfltr Kp Ki Tft Tfv Vfrz Rc Xc Kc emax emin dbd1 dbd2 Qmax Qmin Kpg Kig Tp fdbd1 fdbd2 femax femin Pmax Pmin Tg Ddn
    /// Dup. The flow branch index is resolved by the caller against the RAW case (<see cref="RepcaParameters.FlowBranchIndex"/>).
    /// </summary>
    public static RepcaParameters Repca(PsseDyrRecord r)
    {
        IReadOnlyList<string> v = ValuesOf(r, "REPC_A");
        int ci = 7;
        double C(string n) => Num(r, v[ci++], n);
        var p = new RepcaParameters
        {
            RegulatedBus = Whole(r, v[0], "BUSR"), FlowFromBus = Whole(r, v[1], "BUS1"), FlowToBus = Whole(r, v[2], "BUS2"),
            FlowCircuit = v[3].Trim().Trim('\'').Trim(), VcFlag = Whole(r, v[4], "VCFlag"), RefFlag = Whole(r, v[5], "RefFlag"), Fflag = Whole(r, v[6], "Fflag"),
        };
        return p with
        {
            Tfltr = C("Tfltr"), Kp = C("Kp"), Ki = C("Ki"), Tft = C("Tft"), Tfv = C("Tfv"), Vfrz = C("Vfrz"), Rc = C("Rc"), Xc = C("Xc"),
            Kc = C("Kc"), Emax = C("emax"), Emin = C("emin"), Dbd1 = C("dbd1"), Dbd2 = C("dbd2"), Qmax = C("Qmax"), Qmin = C("Qmin"),
            Kpg = C("Kpg"), Kig = C("Kig"), Tp = C("Tp"), Fdbd1 = C("fdbd1"), Fdbd2 = C("fdbd2"), Femax = C("femax"), Femin = C("femin"),
            Pmax = C("Pmax"), Pmin = C("Pmin"), Tg = C("Tg"), Ddn = C("Ddn"), Dup = C("Dup"),
        };
    }

    /// <summary>
    /// DER_A (a GridSim-readable record in the WECC specification's order; PSS/E's DERA1 order is not public): 48 values
    /// Trv Trf dbd1 dbd2 Kqv Vref0 Tp Tiq Ddn Dup fdbd1 fdbd2 femax femin Pmax Pmin dPmax dPmin Tpord Kpg Kig Imax vl0 vl1
    /// vh0 vh1 tvl0 tvl1 tvh0 tvh1 Vrfrac fl fh tfl tfh Tg rrpwr Tv Vpr Pflag Pqflag Freq_flag Ftripflag Vtripflag typeflag Xe
    /// Iqh1 Iql1 (Pourbeik, "Proposal for DER_A model", WECC REMTF memo, revised 6/19/19, Table 1).
    /// </summary>
    public static DeraParameters Dera(PsseDyrRecord r)
    {
        IReadOnlyList<string> v = ValuesOf(r, "DER_A");
        int k = 0;
        double C(string n) => Num(r, v[k++], n);
        int F(string n) => Whole(r, v[k++], n);
        return new DeraParameters
        {
            Trv = C("Trv"), Trf = C("Trf"), Dbd1 = C("dbd1"), Dbd2 = C("dbd2"), Kqv = C("Kqv"), Vref0 = C("Vref0"), Tp = C("Tp"), Tiq = C("Tiq"),
            Ddn = C("Ddn"), Dup = C("Dup"), Fdbd1 = C("fdbd1"), Fdbd2 = C("fdbd2"), Femax = C("femax"), Femin = C("femin"), Pmax = C("Pmax"),
            Pmin = C("Pmin"), DPmax = C("dPmax"), DPmin = C("dPmin"), Tpord = C("Tpord"), Kpg = C("Kpg"), Kig = C("Kig"), Imax = C("Imax"),
            Vl0 = C("vl0"), Vl1 = C("vl1"), Vh0 = C("vh0"), Vh1 = C("vh1"), Tvl0 = C("tvl0"), Tvl1 = C("tvl1"), Tvh0 = C("tvh0"), Tvh1 = C("tvh1"),
            Vrfrac = C("Vrfrac"), Fl = C("fl"), Fh = C("fh"), Tfl = C("tfl"), Tfh = C("tfh"), Tg = C("Tg"), Rrpwr = C("rrpwr"), Tv = C("Tv"), Vpr = C("Vpr"),
            Pflag = F("Pflag"), Pqflag = F("Pqflag"), FreqFlag = F("Freq_flag"), FtripFlag = F("Ftripflag"), VtripFlag = F("Vtripflag"),
            TypeFlag = C("typeflag"), Xe = C("Xe"), Iqh1 = C("Iqh1"), Iql1 = C("Iql1"),
        };
    }

    /// <summary>
    /// REGC_B (GridSim record in the order of Table 1 of the WECC REMWG memo Rev. 27b, 2/16/23): 11 values Tg Iqrmax Iqrmin
    /// Tfltr rrpwr RateFlag Te Imax pqflag re Xe.
    /// </summary>
    public static RegcbParameters Regcb(PsseDyrRecord r)
    {
        IReadOnlyList<string> v = ValuesOf(r, "REGC_B");
        int k = 0;
        double C(string n) => Num(r, v[k++], n);
        int F(string n) => Whole(r, v[k++], n);
        return new RegcbParameters
        {
            Tg = C("Tg"), Iqrmax = C("Iqrmax"), Iqrmin = C("Iqrmin"), Tfltr = C("Tfltr"), Rrpwr = C("rrpwr"), RateFlag = F("RateFlag"),
            Te = C("Te"), Imax = C("Imax"), PqFlag = F("pqflag"), Re = C("re"), Xe = C("Xe"),
        };
    }

    /// <summary>
    /// REEC_D (GridSim record; see <see cref="ReecdParameters"/>): six flags PfFlag VFlag QFlag PFlag PqFlag Vcmpflag, then
    /// the 78 values of Table 5 of the EPRI REGC_B/REEC_D test protocol (8/17/2020) in its order.
    /// </summary>
    public static ReecdParameters Reecd(PsseDyrRecord r)
    {
        IReadOnlyList<string> v = ValuesOf(r, "REEC_D");
        int k = 0;
        double C(string n) => Num(r, v[k++], n);
        int F(string n) => Whole(r, v[k++], n);
        var p = new ReecdParameters
        {
            PfFlag = F("PfFlag"), VFlag = F("VFlag"), QFlag = F("QFlag"), PFlag = F("PFlag"), PqFlag = F("PqFlag"), VcmpFlag = F("Vcmpflag"),
        };
        p = p with
        {
            Mvab = C("mvab"), Vdip = C("Vdip"), Vup = C("Vup"), Trv = C("Trv"), Dbd1 = C("dbd1"), Dbd2 = C("dbd2"), Kqv = C("Kqv"), Iqh1 = C("Iqh1"),
            Iql1 = C("Iql1"), Vref0 = C("Vref0"), Iqfrz = C("Iqfrz"), Thld = C("Thld"), Thld2 = C("Thld2"), Tp = C("Tp"), Qvmax = C("qvmax"),
            Qvmin = C("qvmin"), Vmax = C("Vmax"), Vmin = C("Vmin"), Kqp = C("Kqp"), Kqi = C("Kqi"), Kvp = C("Kvp"), Kvi = C("Kvi"), Vref1 = C("Vref1"),
            Tiq = C("Tiq"), DPmax = C("dPmax"), DPmin = C("dPmin"), Pmax = C("Pmax"), Pmin = C("Pmin"), Imax = C("Imax"), Tpord = C("Tpord"),
            Rc = C("rc"), Xc = C("Xc"), Tr1 = C("Tr1"), Kc = C("Kc"), Ke = C("Ke"),
        };
        var vq = new double[10]; var iq = new double[10]; var vp = new double[10]; var ip = new double[10];
        for (int i = 0; i < 10; i++) { vq[i] = C($"vq{i + 1}"); iq[i] = C($"iq{i + 1}"); }
        for (int i = 0; i < 10; i++) { vp[i] = C($"vp{i + 1}"); ip[i] = C($"ip{i + 1}"); }
        return p with { Vq = vq, Iq = iq, Vp = vp, Ip = ip, Vblkl = C("vblkl"), Vblkh = C("vblkh"), TblkDelay = C("Tblk_delay") };
    }

    /// <summary>
    /// REGFM_A1 (GridSim record in the order of Table 1 of PNNL-35110, the three power-flow-initialised set points left out):
    /// 22 values XL mq kpv kiv Emax Emin mp Pmax Pmin kppmax kipmax omega0 TPf TQf TVf Qmax Qmin VFlag QVFlag kpqmax kiqmax ImaxF.
    /// </summary>
    public static RegfmA1Parameters RegfmA1(PsseDyrRecord r)
    {
        IReadOnlyList<string> v = ValuesOf(r, "REGFM_A1");
        int k = 0;
        double C(string n) => Num(r, v[k++], n);
        int F(string n) => Whole(r, v[k++], n);
        return new RegfmA1Parameters
        {
            XL = C("XL"), Mq = C("mq"), Kpv = C("kpv"), Kiv = C("kiv"), Emax = C("Emax"), Emin = C("Emin"), Mp = C("mp"), Pmax = C("Pmax"), Pmin = C("Pmin"),
            Kppmax = C("kppmax"), Kipmax = C("kipmax"), Omega0 = C("omega0"), TPf = C("TPf"), TQf = C("TQf"), TVf = C("TVf"), Qmax = C("Qmax"),
            Qmin = C("Qmin"), VFlag = F("VFlag"), QVFlag = F("QVFlag"), Kpqmax = C("kpqmax"), Kiqmax = C("kiqmax"), ImaxF = C("ImaxF"),
        };
    }

    /// <summary>WTDTA1: CONs H DAMP Htfrac Freq1 Dshaft.</summary>
    public static WtgtaParameters Wtgta(PsseDyrRecord r)
    {
        IReadOnlyList<string> v = ValuesOf(r, "WTGT_A");
        return new WtgtaParameters { H = Num(r, v[0], "H"), Damp = Num(r, v[1], "DAMP"), Htfrac = Num(r, v[2], "Htfrac"), Freq1 = Num(r, v[3], "Freq1"), Dshaft = Num(r, v[4], "Dshaft") };
    }

    /// <summary>WTARA1: CONs Ka theta0.</summary>
    public static WtgaraParameters Wtgara(PsseDyrRecord r)
    {
        IReadOnlyList<string> v = ValuesOf(r, "WTGAR_A");
        return new WtgaraParameters { Ka = Num(r, v[0], "Ka"), Theta0 = Num(r, v[1], "theta0") };
    }

    /// <summary>WTPTA1: CONs Kiw Kpw Kic Kpc Kcc Tp TetaMax TetaMin RTetaMax RTetaMin.</summary>
    public static WtgptaParameters Wtgpta(PsseDyrRecord r)
    {
        IReadOnlyList<string> v = ValuesOf(r, "WTGPT_A");
        return new WtgptaParameters
        {
            Kiw = Num(r, v[0], "Kiw"), Kpw = Num(r, v[1], "Kpw"), Kic = Num(r, v[2], "Kic"), Kpc = Num(r, v[3], "Kpc"), Kcc = Num(r, v[4], "Kcc"),
            Tp = Num(r, v[5], "Tp"), ThetaMax = Num(r, v[6], "TetaMax"), ThetaMin = Num(r, v[7], "TetaMin"),
            RThetaMax = Num(r, v[8], "RTetaMax"), RThetaMin = Num(r, v[9], "RTetaMin"),
        };
    }

    /// <summary>WTTQA1: ICON Tflag; CONs Kpp Kip Tp Twref Temax Temin p1 spd1 p2 spd2 p3 spd3 p4 spd4 TRATE.</summary>
    public static WtgtrqaParameters Wtgtrqa(PsseDyrRecord r)
    {
        IReadOnlyList<string> v = ValuesOf(r, "WTGTRQ_A");
        var pts = new double[4];
        var spd = new double[4];
        for (int k = 0; k < 4; k++) { pts[k] = Num(r, v[7 + 2 * k], $"p{k + 1}"); spd[k] = Num(r, v[8 + 2 * k], $"spd{k + 1}"); }
        return new WtgtrqaParameters
        {
            Tflag = Whole(r, v[0], "Tflag"), Kpp = Num(r, v[1], "Kpp"), Kip = Num(r, v[2], "Kip"), Tp = Num(r, v[3], "Tp"),
            Twref = Num(r, v[4], "Twref"), Temax = Num(r, v[5], "Temax"), Temin = Num(r, v[6], "Temin"), P = pts, Spd = spd,
            Trate = Num(r, v[15], "TRATE"),
        };
    }

    /// <summary>REGCA1: ICON Lvplsw; CONs Tg Rrpwr Brkpt Zerox Lvpl1 Volim Lvpnt1 Lvpnt0 Iolim Tfltr Khv Iqrmax Iqrmin Accel.</summary>
    public static RegcaParameters Regca(PsseDyrRecord r)
    {
        IReadOnlyList<string> v = ValuesOf(r, "REGC_A");
        double C(int k, string n) => Num(r, v[1 + k], n);
        return new RegcaParameters
        {
            Lvplsw = Whole(r, v[0], "Lvplsw"),
            Tg = C(0, "Tg"), Rrpwr = C(1, "Rrpwr"), Brkpt = C(2, "Brkpt"), Zerox = C(3, "Zerox"), Lvpl1 = C(4, "Lvpl1"),
            Volim = C(5, "Volim"), Lvpnt1 = C(6, "Lvpnt1"), Lvpnt0 = C(7, "Lvpnt0"), Iolim = C(8, "Iolim"), Tfltr = C(9, "Tfltr"),
            Khv = C(10, "Khv"), Iqrmax = C(11, "Iqrmax"), Iqrmin = C(12, "Iqrmin"), Accel = C(13, "Accel"),
        };
    }

    /// <summary>
    /// REECA1: ICONs BUSR PFFLAG VFLAG QFLAG PFLAG PQFLAG; CONs Vdip Vup Trv dbd1 dbd2 Kqv Iqh1 Iql1 Vref0 Iqfrz Thld Thld2
    /// Tp QMax QMin VMAX VMIN Kqp Kqi Kvp Kvi Vref1 Tiq dPmax dPmin PMAX PMIN Imax Tpord Vq1 Iq1 .. Vq4 Iq4 Vp1 Ip1 .. Vp4 Ip4.
    /// REECB1: ICONs BUSR PFFLAG VFLAG QFLAG PQFLAG; CONs Vdip .. Vref0 Tp QMax .. Kvi Tiq dPmax .. Tpord (no Iqfrz, Thld,
    /// Thld2, Vref1, VDL tables). REECCU1: as REECB1, then the VDL tables and T SOCini SOCmax SOCmin.
    /// </summary>
    public static ReecParameters Reec(PsseDyrRecord r, string model)
    {
        IReadOnlyList<string> v = ValuesOf(r, model);
        int ni = Counts[model].Icons;
        int ci = 0;
        double C(string n) => Num(r, v[ni + ci++], n);
        int I(int k, string n) => Whole(r, v[k], n);
        ReecVariant variant = model == "REEC_A" ? ReecVariant.A : model == "REEC_B" ? ReecVariant.B : ReecVariant.C;
        int busr = I(0, "BUSR"), pf = I(1, "PFFLAG"), vf = I(2, "VFLAG"), qf = I(3, "QFLAG");
        int pflag = variant == ReecVariant.A ? I(4, "PFLAG") : 0;
        int pq = I(variant == ReecVariant.A ? 5 : 4, "PQFLAG");
        double vdip = C("Vdip"), vup = C("Vup"), trv = C("Trv"), dbd1 = C("dbd1"), dbd2 = C("dbd2"), kqv = C("Kqv"),
            iqh1 = C("Iqh1"), iql1 = C("Iql1"), vref0 = C("Vref0");
        double iqfrz = 0, thld = 0, thld2 = 0;
        if (variant == ReecVariant.A) { iqfrz = C("Iqfrz"); thld = C("Thld"); thld2 = C("Thld2"); }
        double tp = C("Tp"), qmax = C("QMax"), qmin = C("QMin"), vmax = C("VMAX"), vmin = C("VMIN"),
            kqp = C("Kqp"), kqi = C("Kqi"), kvp = C("Kvp"), kvi = C("Kvi");
        double vref1 = variant == ReecVariant.A ? C("Vref1") : 0.0;
        double tiq = C("Tiq"), dpmax = C("dPmax"), dpmin = C("dPmin"), pmax = C("PMAX"), pmin = C("PMIN"), imax = C("Imax"), tpord = C("Tpord");
        var p = new ReecParameters
        {
            Variant = variant, RemoteBus = busr, PfFlag = pf, VFlag = vf, QFlag = qf, PFlag = pflag, PqFlag = pq,
            Vdip = vdip, Vup = vup, Trv = trv, Dbd1 = dbd1, Dbd2 = dbd2, Kqv = kqv, Iqh1 = iqh1, Iql1 = iql1, Vref0 = vref0,
            Iqfrz = iqfrz, Thld = thld, Thld2 = thld2, Tp = tp, Qmax = qmax, Qmin = qmin, Vmax = vmax, Vmin = vmin,
            Kqp = kqp, Kqi = kqi, Kvp = kvp, Kvi = kvi, Vref1 = vref1, Tiq = tiq, DPmax = dpmax, DPmin = dpmin,
            Pmax = pmax, Pmin = pmin, Imax = imax, Tpord = tpord,
        };
        if (variant != ReecVariant.B)
        {
            var vq = new double[4]; var iq = new double[4]; var vp = new double[4]; var ip = new double[4];
            for (int k = 0; k < 4; k++) { vq[k] = C($"Vq{k + 1}"); iq[k] = C($"Iq{k + 1}"); }
            for (int k = 0; k < 4; k++) { vp[k] = C($"Vp{k + 1}"); ip[k] = C($"Ip{k + 1}"); }
            p = p with { Vq = vq, Iq = iq, Vp = vp, Ip = ip };
        }
        if (variant == ReecVariant.C)
            p = p with { T = C("T"), SocIni = C("SOCini"), SocMax = C("SOCmax"), SocMin = C("SOCmin") };
        return p;
    }
}
