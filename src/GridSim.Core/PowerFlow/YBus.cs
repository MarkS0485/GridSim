using System.Numerics;
using GridSim.Core.Model;

namespace GridSim.Core.PowerFlow;

/// <summary>
/// The network admittance matrix Y. Each branch contributes a 2x2 stamp built from
/// its pi-model (series admittance + line charging) and any transformer tap/phase
/// shift; bus shunts land on the diagonal. Y is what ties the whole grid together:
/// the current injection at every bus is I = YV.
///
/// Dense (Complex[,]) for now  -  fine for the small IEEE cases. A sparse build is a
/// natural later step once networks get large.
/// </summary>
public sealed class YBus
{
    public Complex[,] Y { get; }
    public int N { get; }

    private readonly Dictionary<int, int> _index;

    private YBus(Complex[,] y, Dictionary<int, int> index)
    {
        Y = y;
        N = y.GetLength(0);
        _index = index;
    }

    /// <summary>Matrix row/column for a bus id.</summary>
    public int IndexOf(int busId) => _index[busId];

    public static YBus Build(GridModel model)
    {
        int n = model.BusCount;
        var index = new Dictionary<int, int>(n);
        for (int i = 0; i < n; i++) index[model.Buses[i].Id] = i;

        var y = new Complex[n, n];

        foreach (Branch br in model.Branches)
        {
            if (!br.InService) continue;   // open branch: contributes no admittance (bit-identical when all in service)
            int f = index[br.From];
            int t = index[br.To];

            BranchStamp s = AdmittanceStamp.Branch(br.R, br.X, br.B, br.TapRatio, br.PhaseShiftDeg);
            y[f, f] += s.Yff;
            y[t, t] += s.Ytt;
            y[f, t] += s.Yft;
            y[t, f] += s.Ytf;
        }

        // Bus shunts (MW/MVAr at 1 p.u. -> p.u. admittance on the system base).
        foreach (Bus b in model.Buses)
        {
            if (b.Gs == 0 && b.Bs == 0) continue;
            int i = index[b.Id];
            y[i, i] += new Complex(b.Gs, b.Bs) / model.BaseMva;
        }

        return new YBus(y, index);
    }

    /// <summary>
    /// Build the admittance CELLS without materialising the dense [n,n] array - the transient
    /// dense build costs ~200 MB at gb-full's 3,539 buses for consumers (the estimator's
    /// measurement model) that only ever read the nonzero pattern. The accumulation visits
    /// branches, then shunts, in EXACTLY the same order as <see cref="Build"/>, so each cell's
    /// floating-point sum is bit-for-bit the dense value.
    /// </summary>
    public static SparseYBus BuildSparse(GridModel model)
    {
        int n = model.BusCount;
        var index = new Dictionary<int, int>(n);
        for (int i = 0; i < n; i++) index[model.Buses[i].Id] = i;

        var cells = new Dictionary<(int Row, int Col), Complex>(model.Branches.Count * 4 + n);
        void Add(int r, int c, Complex v)
        {
            // Mirror Build's dense accumulator, which starts every cell at +0.0: its first stamp is
            // 0.0 + v, which canonicalises a -0.0 component to +0.0. Storing v verbatim on the first
            // write would leave a -0.0 where the dense array holds +0.0 - numerically identical but a
            // different bit pattern. Adding Complex.Zero keeps the two builds bit-for-bit equal (the
            // claim the sparse fault-injection walk relies on); subsequent adds are cur + v on both.
            cells[(r, c)] = cells.TryGetValue((r, c), out Complex cur) ? cur + v : Complex.Zero + v;
        }

        foreach (Branch br in model.Branches)
        {
            if (!br.InService) continue;   // open branch: no admittance stamp
            int f = index[br.From];
            int t = index[br.To];

            BranchStamp s = AdmittanceStamp.Branch(br.R, br.X, br.B, br.TapRatio, br.PhaseShiftDeg);
            Add(f, f, s.Yff);
            Add(t, t, s.Ytt);
            Add(f, t, s.Yft);
            Add(t, f, s.Ytf);
        }

        foreach (Bus b in model.Buses)
        {
            if (b.Gs == 0 && b.Bs == 0) continue;
            int i = index[b.Id];
            Add(i, i, new Complex(b.Gs, b.Bs) / model.BaseMva);
        }

        return new SparseYBus(n, index, cells);
    }
}

/// <summary>
/// The nonzero admittance cells of Y without the dense array (see <see cref="YBus.BuildSparse"/>).
/// Cell values are bit-for-bit the dense build's - same stamps, same accumulation order.
/// </summary>
public sealed class SparseYBus
{
    private readonly Dictionary<int, int> _index;

    internal SparseYBus(int n, Dictionary<int, int> index, Dictionary<(int Row, int Col), Complex> cells)
    {
        N = n;
        _index = index;
        Cells = cells;
    }

    public int N { get; }

    /// <summary>Nonzero (accumulated) cells keyed (row, col) by bus INDEX.</summary>
    public IReadOnlyDictionary<(int Row, int Col), Complex> Cells { get; }

    /// <summary>Matrix row/column for a bus id.</summary>
    public int IndexOf(int busId) => _index[busId];
}
