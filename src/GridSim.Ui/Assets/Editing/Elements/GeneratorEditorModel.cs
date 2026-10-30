using GridSim.Core.IO.Json.CaseEditing;
using GridSim.Core.IO.Json.Lossless;

namespace GridSim.Ui.Assets.Editing.Elements;

// UI programme Phase 4.3: the generator editor (plan: "generator + converter block"). One layout for v1
// (generation.json: the converter fields are flat, converterInterfaced, converterFaultCurrentPu, gridForming) and v2
// (network.json generators: one "converter" block with interfaced, faultCurrentPu and gridForming, whose fields are shown
// under the block's row once it is written; absent block: type it as JSON in the block's row, for example
// {"interfaced": true}).
//
// Derived read-outs (display only, never written back):
//   Real power:
//     Output (written, else capacity x factor) (MW) = outputMw if written, else capacityMva * capacityFactor when the
//       factor is written (absent factor: the loader takes it from the type, which is not read here: n/a)
//     Maximum output used (MW)                   = pMaxMw if written as a number, else capacityMva (the schema's absent rule)
//   Reactive power and voltage:
//     Apparent power at the P/Q corner (MVA)     S = sqrt(Pmax^2 + Qcorner^2), Qcorner = max(|qMinMvar|, |qMaxMvar|),
//       each limit as written (a number) or, absent, -/+ capacityMva (the schema's absent rule); n/a for an unbounded limit
//     Power factor at the corner                 = Pmax / S
//     Voltage set point (kV)                     = voltageSetpoint * baseKv(bus)
//   Dynamics:
//     Stored energy at rated MVA (MJ)            = inertiaH * capacityMva
//     Unit transformer reactance on the case base (pu) = unitTransformerXPu * baseMva / capacityMva
//     Subtransient reactance on the case base (pu)     = subtransientXdPu * baseMva / capacityMva (v2, when written)
//   Converter block:
//     Converter fault current (kA)               = k * capacityMva / (sqrt(3) * baseKv(bus)), k the fault current
//       multiple as written (v1 converterFaultCurrentPu, v2 converter.faultCurrentPu)

/// <summary>The per-element editor of a generating unit (v1 generation.json, v2 network generators).</summary>
public sealed class GeneratorEditorModel : ElementEditorModel
{
    /// <summary>The section titles, in order.</summary>
    public const string Identity = "Identity and role", RealPower = "Real power", Reactive = "Reactive power and voltage",
        Dynamics = "Dynamics parameters", Converter = "Converter block";

    internal static readonly SectionSpec[] Layout =
    {
        new(Identity, "name", "bus", "type", "role", "inService", "powerFlowOnly"),
        new(RealPower, "capacityMva", "outputMw", "capacityFactor", "pMinMw", "pMaxMw"),
        new(Reactive, "qMinMvar", "qMaxMvar", "qOutputMvar", "voltageSetpoint"),
        new(Dynamics, "inertiaH", "unitTransformerXPu", "subtransientXdPu", "negativeSeqXPu", "template", "dynamicsClass"),
        new(Converter, "converter", "converterInterfaced", "converterFaultCurrentPu", "gridForming"),
    };

    internal GeneratorEditorModel(EditorContext c) : base(c, "Generator", Layout, Derive) { }

    private static IReadOnlyList<DerivedReadout> Derive(EditorContext c, string section)
    {
        var list = new List<DerivedReadout>();
        (double? cap, string capWhy) = c.Num("capacityMva");
        (double? kv, string kvWhy) = c.BusKv("bus");
        (double? mva, string mvaWhy) = c.BaseMva();
        // Pmax: written number, else capacityMva (absent rule); a string ("Infinity") is unbounded.
        (double? pMax, string pMaxWhy) = c.Written("pMaxMw") ? c.Num("pMaxMw") : (cap, capWhy);
        if (section == RealPower)
        {
            const string outL = "Output (written, else capacity x factor)";
            if (c.Num("outputMw") is { Value: double o } && c.Written("outputMw"))
                list.Add(DerivedReadout.Of(outL, o, "MW", "outputMw as written"));
            else if (cap is double s0 && c.Written("capacityFactor") && c.Num("capacityFactor") is { Value: double cf })
                list.Add(DerivedReadout.Of(outL, s0 * cf, "MW", "capacityMva * capacityFactor"));
            else
                list.Add(DerivedReadout.NotShown(outL, "MW", cap is null ? capWhy : "capacityFactor absent: the loader takes it from the type", "capacityMva * capacityFactor"));
            list.Add(pMax is double pm
                ? DerivedReadout.Of("Maximum output used", pm, "MW", c.Written("pMaxMw") ? "pMaxMw as written" : "pMaxMw absent: capacityMva")
                : DerivedReadout.NotShown("Maximum output used", "MW", pMaxWhy, "pMaxMw, else capacityMva"));
        }
        else if (section == Reactive)
        {
            (double? qMin, string qMinWhy) = c.Written("qMinMvar") ? c.Num("qMinMvar") : (cap is double a ? -a : null, capWhy);
            (double? qMax, string qMaxWhy) = c.Written("qMaxMvar") ? c.Num("qMaxMvar") : (cap, capWhy);
            const string sL = "Apparent power at the P/Q corner";
            const string sF = "sqrt(Pmax^2 + max(|qMinMvar|, |qMaxMvar|)^2)";
            if (pMax is double p && qMin is double qn && qMax is double qx && double.IsFinite(p) && double.IsFinite(qn) && double.IsFinite(qx))
            {
                // S = sqrt(P^2 + Q^2) at the corner of the capability rectangle (Pmax, the larger reactive limit).
                double q = Math.Max(Math.Abs(qn), Math.Abs(qx));
                double sCorner = Math.Sqrt(p * p + q * q);
                list.Add(DerivedReadout.Of(sL, sCorner, "MVA", sF));
                list.Add(sCorner > 0
                    ? DerivedReadout.Of("Power factor at the corner", p / sCorner, "", "Pmax / S")
                    : DerivedReadout.NotShown("Power factor at the corner", "", "S is 0", "Pmax / S"));
            }
            else
                list.Add(DerivedReadout.NotShown(sL, "MVA", pMax is null ? pMaxWhy : qMin is null ? qMinWhy : qMax is null ? qMaxWhy : "a limit is unbounded", sF));
            (double? vs, string vsWhy) = c.Num("voltageSetpoint");
            list.Add(vs is double v && kv is double k
                ? DerivedReadout.Of("Voltage set point", v * k, "kV", "voltageSetpoint * baseKv(bus)")
                : DerivedReadout.NotShown("Voltage set point", "kV", vs is null ? vsWhy : kvWhy, "voltageSetpoint * baseKv(bus)"));
        }
        else if (section == Dynamics)
        {
            (double? h, string hWhy) = c.Num("inertiaH");
            // Kinetic energy at rated speed: E = H * S (MW s = MJ).
            list.Add(h is double hh && cap is double s1
                ? DerivedReadout.Of("Stored energy at rated MVA", hh * s1, "MJ", "inertiaH * capacityMva")
                : DerivedReadout.NotShown("Stored energy at rated MVA", "MJ", h is null ? hWhy : capWhy, "inertiaH * capacityMva"));
            // Change of MVA base: x(case) = x(unit) * S(case) / S(unit).
            OnCaseBase(list, c, "unitTransformerXPu", "Unit transformer reactance on the case base", cap, capWhy, mva, mvaWhy, writtenOnly: false);
            OnCaseBase(list, c, "subtransientXdPu", "Subtransient reactance on the case base", cap, capWhy, mva, mvaWhy, writtenOnly: true);
        }
        else if (section == Converter)
        {
            double? k = null;
            string kName = c.IsV2 ? "converter.faultCurrentPu" : "converterFaultCurrentPu";
            if (!c.IsV2 && c.Written("converterFaultCurrentPu")) k = c.Num("converterFaultCurrentPu").Value;
            if (c.IsV2 && c.Obj("converter") is JsonSourceNode block
                && RecordRead.Member(block, "faultCurrentPu", c.Doc.KeyComparer) is { Kind: JsonSourceKind.Number, NumberValue: double kk })
                k = kk;
            string f = kName + " * capacityMva / (sqrt(3) * baseKv(bus))";
            // I = k * S / (sqrt(3) * V): the converter's fault current limit in kA at the bus voltage.
            if (k is double kf)
                list.Add(cap is double s2 && kv is double kb && kb > 0
                    ? DerivedReadout.Of("Converter fault current", kf * s2 / (Math.Sqrt(3.0) * kb), "kA", f)
                    : DerivedReadout.NotShown("Converter fault current", "kA", cap is null ? capWhy : kv is null ? kvWhy : "baseKv is not above 0", f));
        }
        return list;
    }

    private static void OnCaseBase(List<DerivedReadout> list, EditorContext c, string field, string label, double? cap, string capWhy,
        double? mva, string mvaWhy, bool writtenOnly)
    {
        if (writtenOnly && !c.Written(field)) return;
        (double? xu, string xWhy) = c.Num(field);
        string formula = field + " * baseMva / capacityMva";
        list.Add(xu is double x && cap is double s && s > 0 && mva is double b
            ? DerivedReadout.Of(label, x * b / s, "pu", formula)
            : DerivedReadout.NotShown(label, "pu", xu is null ? xWhy : cap is null ? capWhy : cap <= 0 ? "capacityMva is 0" : mvaWhy, formula));
    }
}
