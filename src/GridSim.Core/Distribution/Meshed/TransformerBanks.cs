using System.Numerics;

namespace GridSim.Core.Distribution.Meshed;

// RC4 R4-15b: three-phase transformer banks for the meshed phase frame, built from single-phase units of the N-winding unit
// model (PhaseFrameModel.cs FrameTransformerUnit). Nothing here changes the solver: a bank is a list of ordinary units (and, for
// an ungrounded wye, one internal star node), so every bank is solved by the same Newton iteration as any other element.

/// <summary>The connection of a three-phase transformer bank.</summary>
public enum BankConnection
{
    /// <summary>Grounded wye / grounded wye, no phase shift.</summary>
    GroundedWyeGroundedWye,
    /// <summary>Delta (From) / grounded wye (To). ANSI: the higher-voltage side leads by 30 degrees.</summary>
    DeltaGroundedWye,
    /// <summary>Ungrounded wye (From) / delta (To), a three-wire secondary. ANSI: the higher-voltage side leads by 30 degrees.</summary>
    WyeDelta,
    /// <summary>Delta / delta, no phase shift, a three-wire secondary.</summary>
    DeltaDelta,
    /// <summary>Open (grounded) wye / open delta, two units (A and B feed the secondaries ab and bc), the step-down connection:
    /// the To side lags by 30 degrees. The ca side of the delta is open.</summary>
    OpenWyeOpenDelta,
    /// <summary>Open delta / open delta, two units across A-B and B-C feeding ab and bc, no phase shift.</summary>
    OpenDeltaOpenDelta,
}

/// <summary>
/// A three-phase bank between two nodes. <paramref name="KvLLFrom"/> / <paramref name="KvLLTo"/> are the rated line-to-line
/// voltages of the two sides (kV; the winding turns are proportional to the winding rated voltages: line-to-neutral for a wye
/// winding, line-to-line for a delta winding). <paramref name="UnitKva"/> is the rating of ONE single-phase unit (kVA).
/// <paramref name="ZFromPu"/> and <paramref name="ZToPu"/> are the leakage impedances of the From and To windings in per unit on
/// each winding's own rated voltage and the unit rating (neither may be zero). <paramref name="MagnetisingPu"/> (default
/// zero: none) is the magnetising admittance in per unit on the first (From) winding's base: g - j b, so a no-load loss and an
/// excitation current can be given. <paramref name="FromReturn"/> / <paramref name="ToReturn"/> name the return wire of a
/// grounded wye side at its node (null: earth).
/// </summary>
public sealed record BankSpec(string Name, string FromNode, string ToNode, BankConnection Connection, double KvLLFrom,
    double KvLLTo, double UnitKva, Complex ZFromPu, Complex ZToPu, Complex MagnetisingPu = default,
    string? FromReturn = null, string? ToReturn = null)
{
    /// <summary>A spec whose total per-unit impedance <paramref name="rPercent"/> + j <paramref name="xPercent"/> is split equally
    /// between the two windings.</summary>
    public static BankSpec Split(string name, string from, string to, BankConnection c, double kvLLFrom, double kvLLTo, double unitKva,
        double rPercent, double xPercent, Complex magnetisingPu = default)
    {
        var half = new Complex(rPercent / 200.0, xPercent / 200.0);
        return new BankSpec(name, from, to, c, kvLLFrom, kvLLTo, unitKva, half, half, magnetisingPu);
    }

    /// <summary>The magnetising branch as an excitation current and a no-load loss, both in percent of the unit rating:
    /// g = loss / 100, |y| = excitation / 100, b = sqrt(|y|^2 - g^2); the admittance is g - j b (inductive).</summary>
    public static Complex MagnetisingFromPercent(double noLoadLossPercent, double excitationPercent)
    {
        if (!(noLoadLossPercent >= 0 && excitationPercent >= noLoadLossPercent && double.IsFinite(excitationPercent)))
            throw new ArgumentException("the excitation current must be finite and not below the no-load loss (both in percent, not negative).");
        double g = noLoadLossPercent / 100.0, y = excitationPercent / 100.0;
        return new Complex(g, -Math.Sqrt(y * y - g * g));
    }
}

/// <summary>What a builder adds to a model: units, series branches and internal nodes (a star point), with notes.</summary>
public sealed class FrameParts
{
    /// <summary>Extra nodes (an ungrounded wye's star point).</summary>
    public List<FrameNode> Nodes { get; } = new();
    /// <summary>Single-phase units, in a fixed order.</summary>
    public List<FrameTransformerUnit> Units { get; } = new();
    /// <summary>Series branches (a bypassed line of an open-delta regulator).</summary>
    public List<FrameBranch> Branches { get; } = new();
    /// <summary>Recorded modelling decisions.</summary>
    public List<string> Notes { get; } = new();
}

/// <summary>Builds the units of a <see cref="BankSpec"/>.</summary>
public static class TransformerBanks
{
    private static readonly string[] Abc = ["a", "b", "c"];
    private static readonly double Sqrt3 = Math.Sqrt(3.0);

    /// <summary>The units of <paramref name="s"/> and any internal node. Throws <see cref="ArgumentException"/> for an unusable spec.</summary>
    public static FrameParts Build(BankSpec s)
    {
        if (string.IsNullOrWhiteSpace(s.Name)) throw new ArgumentException("a bank needs a name.");
        if (!(double.IsFinite(s.KvLLFrom) && s.KvLLFrom > 0 && double.IsFinite(s.KvLLTo) && s.KvLLTo > 0))
            throw new ArgumentException($"bank '{s.Name}' needs finite rated voltages above 0.");
        if (!(double.IsFinite(s.UnitKva) && s.UnitKva > 0)) throw new ArgumentException($"bank '{s.Name}' needs a unit rating above 0.");
        if (s.ZFromPu == Complex.Zero || s.ZToPu == Complex.Zero)
            throw new ArgumentException($"bank '{s.Name}': a winding leakage impedance must not be zero (give a small non-zero value).");
        bool needsDirection = s.Connection is BankConnection.DeltaGroundedWye or BankConnection.WyeDelta or BankConnection.OpenWyeOpenDelta;
        if (needsDirection && s.KvLLFrom == s.KvLLTo)
            throw new ArgumentException($"bank '{s.Name}': the ANSI 30 degree shift is defined by which side is the higher voltage, so equal rated voltages are refused.");
        bool stepDown = s.KvLLFrom > s.KvLLTo;
        if (s.Connection == BankConnection.OpenWyeOpenDelta && !stepDown)
            throw new ArgumentException($"bank '{s.Name}': the open wye / open delta is modelled as the step-down connection only.");

        var parts = new FrameParts();
        bool fromWye = s.Connection is BankConnection.GroundedWyeGroundedWye or BankConnection.WyeDelta or BankConnection.OpenWyeOpenDelta;
        bool toWye = s.Connection is BankConnection.GroundedWyeGroundedWye or BankConnection.DeltaGroundedWye;
        bool fromGrounded = s.Connection != BankConnection.WyeDelta;
        string starNode = s.Name + ".star";
        if (fromWye && !fromGrounded)
        {
            parts.Nodes.Add(new FrameNode(starNode, ["n"]));
            parts.Notes.Add($"bank '{s.Name}': ungrounded wye primary, star point is the internal node '{starNode}'.");
        }

        double sUnit = s.UnitKva * 1000.0;
        double vWyeFrom = s.KvLLFrom * 1000.0 / Sqrt3, vDeltaFrom = s.KvLLFrom * 1000.0;
        double vWyeTo = s.KvLLTo * 1000.0 / Sqrt3, vDeltaTo = s.KvLLTo * 1000.0;
        double vFrom = fromWye ? vWyeFrom : vDeltaFrom;
        double vTo = toWye ? vWyeTo : vDeltaTo;
        Complex zFrom = s.ZFromPu * (vFrom * vFrom / sUnit);
        Complex zTo = s.ZToPu * (vTo * vTo / sUnit);
        Complex magY = s.MagnetisingPu == Complex.Zero ? Complex.Zero : s.MagnetisingPu / (vFrom * vFrom / sUnit);

        // Limb k: (From winding, To winding) as (wire, return wire) pairs at their nodes.
        // Pair table: index of the second wire of a delta winding across (x, y).
        static (string X, string Y) Across(string[] w, int x, int y) => (w[x], w[y]);

        int limbs = s.Connection is BankConnection.OpenWyeOpenDelta or BankConnection.OpenDeltaOpenDelta ? 2 : 3;
        for (int k = 0; k < limbs; k++)
        {
            string w = Abc[k];
            FrameWinding hv;
            FrameWinding lv;
            // From winding.
            if (fromWye)
            {
                hv = fromGrounded
                    ? new FrameWinding(s.FromNode, w, s.FromReturn, vWyeFrom, zFrom)
                    : new FrameWinding(s.FromNode, w, "n", vWyeFrom, zFrom, starNode);
            }
            else
            {
                // delta limb k of the From side. Delta/delta and open delta: across (k, k+1). Delta/grounded wye: the limb
                // that feeds To phase k: step-down (k, k-1) (V_a ~ V_A - V_C), step-up (k, k+1).
                (string x, string y) = s.Connection == BankConnection.DeltaGroundedWye && stepDown
                    ? Across(Abc, k, (k + 2) % 3)
                    : Across(Abc, k, (k + 1) % 3);
                hv = new FrameWinding(s.FromNode, x, y, vDeltaFrom, zFrom);
            }
            // To winding.
            if (toWye)
                lv = new FrameWinding(s.ToNode, w, s.ToReturn, vWyeTo, zTo);
            else
            {
                // delta limb on the To side. Wye/delta step-down: across (k, k+1); step-up: across (k, k+2) so that
                // V_ab ~ -V_B-type pairing: limb A (a,c), B (b,a), C (c,b). Delta/delta and open banks: across (k, k+1).
                (string x, string y) = s.Connection == BankConnection.WyeDelta && !stepDown
                    ? Across(Abc, k, (k + 2) % 3)
                    : Across(Abc, k, (k + 1) % 3);
                lv = new FrameWinding(s.ToNode, x, y, vDeltaTo, zTo);
            }
            parts.Units.Add(new FrameTransformerUnit($"{s.Name}.{w}", [hv, lv], magY));
        }
        if (limbs == 2) parts.Notes.Add($"bank '{s.Name}': open bank, two units; the third limb is absent (the ca side of the delta is open).");
        return parts;
    }
}
