using System.Globalization;
using GridSim.Core.Model;

namespace GridSim.Core.PowerFlow;

/// <summary>One converter's solved state in a multi-terminal DC link: whether it rectifies, its DC current (A, positive
/// into the DC network), the DC voltage at its DC bus (kV), its firing (rectifier) or extinction (inverter) angle
/// (degrees), its tap, and the active power it draws from (positive) or delivers to (negative) its AC bus (MW) and the
/// reactive power it absorbs (Mvar).</summary>
public sealed record MtdcConverterResult(int Bus, bool Rectifier, double CurrentA, double DcKv, double AngleDeg, double Tap, double DrawnMw, double AbsorbedMvar);

/// <summary>One multi-terminal DC link's solved state: every converter (in file order) and every DC bus voltage (kV,
/// in file order).</summary>
public sealed record MtdcLinkResult(string Name, IReadOnlyList<MtdcConverterResult> Converters, IReadOnlyList<double> DcBusKv);

/// <summary>
/// The steady-state model of a PSS/E multi-terminal DC link: line-commutated converters (the two-terminal converter
/// equations of <see cref="LccDcLinks"/>) on a resistive DC network.
///
/// <para><b>Control.</b> The DC voltage converter (VCONV) runs at its minimum angle with its tap stepped (from the file's
/// tap, by its step, within its limits) to bring its DC voltage nearest its schedule SETVL (kV); every other converter
/// holds its order: SETVL MW (MDC 1) or A (MDC 2), positive for a rectifier, negative for an inverter, a power order
/// turned into a current by its DC bus voltage. The DC network (link resistances RDC, grounding resistances RGRND) is
/// solved with the voltage converter's DC bus as the reference; the voltage converter takes whatever current balances the
/// network, and rectifies or inverts by its sign. Each order-holding converter's angle then follows from its DC bus
/// voltage and current, its tap stepped to keep the angle within its limits.</para>
///
/// <para><b>Not applied.</b> The mode switch voltage VCMOD, the alternate voltage converter VCONVN, the current margin
/// MARG and the participation factor DCPF (which govern what happens when a converter runs out of angle or tap range)
/// are not applied: a converter that would need them fails the solve, naming the converter, rather than be solved with a
/// guess. A converter connected to two DC buses (IDC2) is refused when the file is read.</para>
/// </summary>
public static class MtdcLinks
{
    private static readonly double K = 3.0 * Math.Sqrt(2.0) / Math.PI;
    private const double KX = 3.0 / Math.PI;

    /// <summary>Solve <paramref name="link"/> at the AC bus voltages <paramref name="vPu"/> (bus id to pu). Throws
    /// <see cref="InvalidOperationException"/> naming the link and converter when no state within the angle and tap limits
    /// meets the orders.</summary>
    public static MtdcLinkResult Solve(MtdcLink link, IReadOnlyDictionary<int, double> vPu)
    {
        ArgumentNullException.ThrowIfNull(link);
        ArgumentNullException.ThrowIfNull(vPu);
        int nb = link.DcBuses.Count, nc = link.Converters.Count;
        var busIx = new Dictionary<int, int>(nb);
        for (int b = 0; b < nb; b++) busIx[link.DcBuses[b].Number] = b;
        int vc = -1;
        for (int k = 0; k < nc; k++) if (link.Converters[k].Bus == link.VoltageConverterBus) vc = k;
        if (vc < 0) throw Fail(link, $"its voltage converter bus {link.VoltageConverterBus} is not one of its converters");
        int refBus = busIx[link.Converters[vc].DcBus];

        // Conductance matrix of the DC network (kA per kV), links and grounds, in file order.
        var g = new double[nb, nb];
        foreach (MtdcDcLine l in link.Lines)
        {
            int i = busIx[l.FromDcBus], j = busIx[l.ToDcBus];
            double y = 1.0 / l.ResistanceOhms;
            g[i, i] += y; g[j, j] += y; g[i, j] -= y; g[j, i] -= y;
        }
        foreach (MtdcDcBus b in link.DcBuses) if (b.GroundOhms > 0) g[busIx[b.Number], busIx[b.Number]] += 1.0 / b.GroundOhms;

        MtdcConverter vconv = link.Converters[vc];
        double vv = vconv.Setpoint, tapV = vconv.Tap;
        var v = new double[nb];
        var cur = new double[nc];          // kA into the network
        for (int pass = 0; pass < 200; pass++)
        {
            // Order-holding converters' currents from the last DC voltages (a power order needs the voltage at its bus).
            Array.Fill(v, vv);
            for (int inner = 0; inner < 200; inner++)
            {
                for (int k = 0; k < nc; k++)
                {
                    if (k == vc) continue;
                    MtdcConverter c = link.Converters[k];
                    double vb = v[busIx[c.DcBus]];
                    if (!(vb > 0)) throw Fail(link, $"the DC voltage at converter {c.Bus}'s DC bus is {F(vb)} kV");
                    cur[k] = link.Mode == LccMode.Current ? c.Setpoint / 1000.0 : c.Setpoint / vb;
                }
                double[] vn = NetworkVoltages(g, nb, refBus, vv, link, cur, busIx);
                double dv = 0;
                for (int b = 0; b < nb; b++) dv = Math.Max(dv, Math.Abs(vn[b] - v[b]));
                Array.Copy(vn, v, nb);
                if (dv <= 1e-12 * (1 + vv)) break;
                if (inner == 199) throw Fail(link, "its power orders and DC network voltages do not settle");
            }
            // The voltage converter's current balances the network: what its bus sends into the lines and ground.
            double iv = 0;
            for (int j = 0; j < nb; j++) iv += g[refBus, j] * v[j];
            for (int k = 0; k < nc; k++) if (k != vc && busIx[link.Converters[k].DcBus] == refBus) iv -= cur[k];
            cur[vc] = iv;

            // The voltage converter at its minimum angle: the tap that brings its DC voltage nearest the schedule.
            double ia = Math.Abs(iv);
            double drop = vconv.Bridges * (KX * vconv.XcOhms + 2 * vconv.RcOhms);
            double cosMin = DetMath.Cos(Deg(vconv.AngleMinDeg));
            double vAc = Pu(vPu, vconv.Bus, link);
            double VAt(double tap) => vconv.Bridges * K * vAc * vconv.BaseKv * vconv.TransformerRatio / tap * cosMin - drop * ia;
            double best = tapV, err = Math.Abs(VAt(tapV) - vconv.Setpoint);
            foreach (double dir in new[] { -1.0, 1.0 })
            {
                bool moved = false;
                while (vconv.TapStep > 0)
                {
                    double next = best + dir * vconv.TapStep;
                    if (next < vconv.TapMin - 1e-12 || next > vconv.TapMax + 1e-12) break;
                    double e = Math.Abs(VAt(next) - vconv.Setpoint);
                    if (!(e < err)) break;
                    best = next; err = e; moved = true;
                }
                if (moved) break;
            }
            double vvNew = VAt(best);
            bool done = best == tapV && Math.Abs(vvNew - vv) <= 1e-12 * (1 + vv);
            tapV = best;
            vv = vvNew;
            if (done) break;
            if (pass == 199) throw Fail(link, "its voltage converter's tap and the DC network do not settle");
        }

        // Each converter's angle, tap and AC powers.
        var res = new MtdcConverterResult[nc];
        for (int k = 0; k < nc; k++)
        {
            MtdcConverter c = link.Converters[k];
            bool rect = cur[k] > 0;
            double ia = Math.Abs(cur[k]);
            double vdc = v[busIx[c.DcBus]];
            double vAc = Pu(vPu, c.Bus, link);
            double drop = c.Bridges * (KX * c.XcOhms + 2 * c.RcOhms);
            double tap, ang;
            if (k == vc) { tap = tapV; ang = c.AngleMinDeg; }
            else
            {
                double cosMin = DetMath.Cos(Deg(c.AngleMinDeg)), cosMax = DetMath.Cos(Deg(c.AngleMaxDeg));
                double CosAt(double t) => (vdc + drop * ia) / (c.Bridges * K * vAc * c.BaseKv * c.TransformerRatio / t);
                tap = c.Tap;
                while (CosAt(tap) > cosMin && tap - c.TapStep >= c.TapMin - 1e-12 && c.TapStep > 0) tap -= c.TapStep;
                while (CosAt(tap) < cosMax && tap + c.TapStep <= c.TapMax + 1e-12 && c.TapStep > 0) tap += c.TapStep;
                double cs = CosAt(tap);
                if (cs > cosMin + 1e-12 || cs < cosMax - 1e-12)
                    throw Fail(link, $"converter {c.Bus} needs a{(rect ? " firing" : "n extinction")} angle of {F(DetMath.Acos(Math.Clamp(cs, -1, 1)) * 180 / Math.PI)} degrees, outside its limits {F(c.AngleMinDeg)}..{F(c.AngleMaxDeg)} with its tap at the limit (VCMOD, VCONVN, MARG and DCPF are not applied)");
                ang = DetMath.Acos(cs) * 180 / Math.PI;
            }
            double p = vdc * ia;
            double vd0 = K * vAc * c.BaseKv * c.TransformerRatio / tap;
            double cosPhi = Math.Clamp((vd0 * DetMath.Cos(Deg(ang)) - KX * c.XcOhms * ia) / vd0, -1, 1);
            double q = p * Math.Sqrt(1 - cosPhi * cosPhi) / cosPhi;
            res[k] = new MtdcConverterResult(c.Bus, rect, cur[k] * 1000, vdc, ang, tap, rect ? p : -p, q);
        }
        return new MtdcLinkResult(link.Name, res, v.ToArray());
    }

    /// <summary>The DC bus voltages with the reference bus held at <paramref name="vRef"/> and the converters' currents
    /// injected (a Gaussian elimination with partial pivoting on the reduced conductance matrix, fixed order).</summary>
    private static double[] NetworkVoltages(double[,] g, int nb, int refBus, double vRef, MtdcLink link, double[] cur, Dictionary<int, int> busIx)
    {
        var inj = new double[nb];
        for (int k = 0; k < link.Converters.Count; k++)
            if (link.Converters[k].Bus != link.VoltageConverterBus) inj[busIx[link.Converters[k].DcBus]] += cur[k];
        var idx = Enumerable.Range(0, nb).Where(b => b != refBus).ToArray();
        int n = idx.Length;
        var a = new double[n, n + 1];
        for (int r = 0; r < n; r++)
        {
            for (int c = 0; c < n; c++) a[r, c] = g[idx[r], idx[c]];
            a[r, n] = inj[idx[r]] - g[idx[r], refBus] * vRef;
        }
        for (int col = 0; col < n; col++)
        {
            int piv = col;
            for (int r = col + 1; r < n; r++) if (Math.Abs(a[r, col]) > Math.Abs(a[piv, col])) piv = r;
            if (Math.Abs(a[piv, col]) < 1e-14) throw Fail(link, "its DC network is not connected to the voltage converter's DC bus");
            if (piv != col) for (int c = col; c <= n; c++) (a[col, c], a[piv, c]) = (a[piv, c], a[col, c]);
            for (int r = col + 1; r < n; r++)
            {
                double f = a[r, col] / a[col, col];
                if (f == 0) continue;
                for (int c = col; c <= n; c++) a[r, c] -= f * a[col, c];
            }
        }
        var x = new double[n];
        for (int r = n - 1; r >= 0; r--)
        {
            double s = a[r, n];
            for (int c = r + 1; c < n; c++) s -= a[r, c] * x[c];
            x[r] = s / a[r, r];
        }
        var v = new double[nb];
        v[refBus] = vRef;
        for (int r = 0; r < n; r++) v[idx[r]] = x[r];
        return v;
    }

    private static double Pu(IReadOnlyDictionary<int, double> vPu, int bus, MtdcLink link) =>
        vPu.TryGetValue(bus, out double v) ? v : throw Fail(link, $"converter bus {bus} is not in the model");

    private static double Deg(double d) => d * Math.PI / 180.0;

    private static string F(double v) => v.ToString("G6", CultureInfo.InvariantCulture);

    private static InvalidOperationException Fail(MtdcLink link, string why) =>
        new($"multi-terminal DC link {link.Name}: {why}; no steady state within its angle and tap limits meets its orders.");
}
