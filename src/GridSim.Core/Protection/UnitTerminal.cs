using System;
using System.Globalization;
using System.Numerics;
using GridSim.Core.Dynamics.Models;
using GridSim.Core.Model;

namespace GridSim.Core.Protection;

/// <summary>
/// RC5: the machine terminal of a unit whose unit transformer is folded into the machine
/// (<see cref="Generator.UnitTransformerXPu"/>, on the unit rating). The network bus is then the transformer's HV side; the
/// unit's own relays whose VTs are on the generator side (27 / 59, 40) read the terminal V_t = V_hv + j X_t I, with I the
/// unit's current into the network (generator convention) and X_t and I on the same base. One definition shared by the
/// passive assessment, the adaptive RMS loop and the full-model RMS loop.
/// </summary>
internal static class UnitTerminal
{
    /// <summary>V_t = V_hv + j X_t I (X_t and I on one base).</summary>
    internal static Complex Voltage(Complex vHv, Complex i, double xt) =>
        new(vHv.Real - xt * i.Imaginary, vHv.Imaginary + xt * i.Real);

    /// <summary>|V_t| = sqrt(Re^2 + Im^2) of <see cref="Voltage"/>.</summary>
    internal static double Magnitude(Complex vHv, Complex i, double xt)
    {
        Complex v = Voltage(vHv, i, xt);
        return Math.Sqrt(v.Real * v.Real + v.Imaginary * v.Imaginary);
    }

    /// <summary>The unit transformer reactance on the system base, X_t BaseMva / MBase, with the machine-base rule the
    /// dynamic initialisation folds it with (<see cref="MachineBase.EffectiveMva"/>: the rating, else the system base).</summary>
    internal static double SystemBaseXt(Generator gen, double baseMva) =>
        gen.UnitTransformerXPu * (baseMva / MachineBase.EffectiveMva(gen, baseMva));

    /// <summary>The text a 27 / 59 record or verdict carries when it read the machine terminal.</summary>
    internal static string TerminalPhrase(double xtPu, int hvBus) =>
        $"the machine terminal, behind the {xtPu.ToString("0.###", CultureInfo.InvariantCulture)} pu unit transformer "
        + $"(V_t = V_hv + j X_t I; not the network bus {hvBus}, which is its HV side)";
}
