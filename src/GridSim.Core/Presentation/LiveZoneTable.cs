using System.Globalization;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;

namespace GridSim.Core.Presentation;

/// <summary>
/// N056: the zone table of the interactive live dashboard (<c>gridsim [case]</c> with no <c>--ticks</c>), for ANY case.
///
/// <para>Before N056 the table was hard-wired to the GB 10-zone teaching model (<c>data/uk</c>): it listed every bus at
/// or above 200 kV and read its "132 kV" column and demand from the bus whose id is that bus's id + 100. On any other
/// case that is wrong in two ways: where no such bus exists the 132 kV column printed NaN, and where one happens to
/// exist (IEEE 118 has buses 1-118, so bus 8 "pairs" with bus 108) it printed an unrelated bus's voltage and demand as
/// if it were the zone's lower-voltage side.</para>
///
/// <para>The rule now (pure presentation: it reads the model and the solved state, computes nothing new):</para>
/// <list type="number">
/// <item><description>Rows are the buses at the case's top voltage level: at or above
/// <see cref="TransmissionKvFloor"/> (200 kV) when the case has any; otherwise the highest <c>BaseKv</c> present. A
/// case with no bus voltages at all (MATPOWER case14 carries baseKV 0) has no rows and says so in
/// <see cref="Note"/>.</description></item>
/// <item><description>A row's lower-voltage partner is a bus joined to it by an in-service branch with a lower, known
/// <c>BaseKv</c>. The GB teaching model's convention (partner id = id + <see cref="UkPartnerIdOffset"/>) is honoured
/// first when that bus really is such a neighbour, so <c>data/uk</c> reads exactly as before; otherwise the partner is
/// the unique lower-voltage neighbour, and with none or several there is no partner (shown as not applicable, never
/// as another bus's value).</description></item>
/// <item><description>Demand is the zone's own demand: the row bus's demand plus its partner's (for <c>data/uk</c>
/// the 400 kV buses carry none, so the figure is the 132 kV demand exactly as before).</description></item>
/// <item><description>At most <see cref="MaxRows"/> rows, in bus-id order; <see cref="Omitted"/> counts the rest so the
/// display can say so.</description></item>
/// </list>
/// </summary>
public sealed class LiveZoneTable
{
    /// <summary>Buses at or above this nominal voltage (kV) are transmission (GB: 275 kV and 400 kV; never 132 kV).</summary>
    public const double TransmissionKvFloor = 200.0;

    /// <summary>The GB 10-zone teaching model's convention: a 400 kV zone bus's 132 kV partner has id + 100.</summary>
    public const int UkPartnerIdOffset = 100;

    /// <summary>The most rows the dashboard lists (it redraws the console every tick; a 300-bus case would scroll).</summary>
    public const int MaxRows = 24;

    /// <summary>One zone row. <see cref="LowerVmPu"/> is NaN when the row has no lower-voltage partner.</summary>
    public sealed record Row(int BusId, string Zone, double UpperVmPu, int? PartnerBusId, double LowerVmPu, double DemandMw);

    /// <summary>The rows shown, in bus-id order.</summary>
    public IReadOnlyList<Row> Rows { get; }

    /// <summary>How many top-level buses were not shown because of <see cref="MaxRows"/>.</summary>
    public int Omitted { get; }

    /// <summary>Column header for the row buses: "400kV" when every row bus has the same voltage, else "HV".</summary>
    public string UpperHeader { get; }

    /// <summary>Column header for the partners: "132kV" when every partner has the same voltage, else "LV".</summary>
    public string LowerHeader { get; }

    /// <summary>A one-line explanation when the table is empty or partly unpaired; null when there is nothing to say.</summary>
    public string? Note { get; }

    private LiveZoneTable(IReadOnlyList<Row> rows, int omitted, string upper, string lower, string? note)
    {
        Rows = rows; Omitted = omitted; UpperHeader = upper; LowerHeader = lower; Note = note;
    }

    /// <summary>Build the table for a model and its solved state.</summary>
    public static LiveZoneTable Build(GridModel model, PowerFlowResult result)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(result);

        var vmById = new Dictionary<int, double>(result.BusIds.Count);
        for (int i = 0; i < result.BusIds.Count; i++) vmById[result.BusIds[i]] = result.Vm[i];
        var busById = new Dictionary<int, Bus>(model.Buses.Count);
        foreach (Bus b in model.Buses) busById[b.Id] = b;

        // The top voltage level: transmission when the case has it, else the highest voltage present.
        double maxKv = model.Buses.Count == 0 ? 0.0 : model.Buses.Max(b => b.BaseKv);
        if (!(maxKv > 0.0))
            return new LiveZoneTable(Array.Empty<Row>(), 0, "HV", "LV",
                "Zone table: bus voltages (kV) are not in the data for this case.");
        double floor = maxKv >= TransmissionKvFloor ? TransmissionKvFloor : maxKv;
        List<Bus> top = model.Buses.Where(b => b.BaseKv >= floor).OrderBy(b => b.Id).ToList();

        // Lower-voltage neighbours of each top bus over in-service branches.
        var lowerNeighbours = new Dictionary<int, SortedSet<int>>();
        foreach (Branch br in model.Branches)
        {
            if (!br.InService) continue;
            Link(br.From, br.To);
            Link(br.To, br.From);
        }
        void Link(int upperId, int otherId)
        {
            if (!busById.TryGetValue(upperId, out Bus? u) || !busById.TryGetValue(otherId, out Bus? o)) return;
            if (u.BaseKv < floor || !(o.BaseKv > 0.0) || o.BaseKv >= u.BaseKv) return;
            if (!lowerNeighbours.TryGetValue(upperId, out SortedSet<int>? set)) lowerNeighbours[upperId] = set = new SortedSet<int>();
            set.Add(otherId);
        }

        var rows = new List<Row>();
        int unpaired = 0;
        foreach (Bus b in top.Take(MaxRows))
        {
            int? partner = null;
            if (lowerNeighbours.TryGetValue(b.Id, out SortedSet<int>? lower))
            {
                if (lower.Contains(b.Id + UkPartnerIdOffset)) partner = b.Id + UkPartnerIdOffset;   // GB convention
                else if (lower.Count == 1) partner = lower.Min;
            }
            if (partner is null) unpaired++;
            double demand = partner is int p ? b.Pd + busById[p].Pd : b.Pd;
            rows.Add(new Row(
                b.Id,
                ZoneName(b),
                vmById.GetValueOrDefault(b.Id, double.NaN),
                partner,
                partner is int q ? vmById.GetValueOrDefault(q, double.NaN) : double.NaN,
                demand));
        }

        string upperHeader = Header(rows.Select(r => busById[r.BusId].BaseKv), "HV");
        string lowerHeader = Header(rows.Where(r => r.PartnerBusId is not null).Select(r => busById[r.PartnerBusId!.Value].BaseKv), "LV");
        string? note = unpaired == 0 ? null
            : unpaired == rows.Count
                ? $"Zone table: no single lower-voltage partner for these buses ({lowerHeader} column not applicable)."
                : $"Zone table: {unpaired} of {rows.Count} buses have no single lower-voltage partner ({lowerHeader} not applicable there).";
        return new LiveZoneTable(rows, Math.Max(0, top.Count - MaxRows), upperHeader, lowerHeader, note);
    }

    /// <summary>"North Scotland 400kV" -> "North Scotland": the bus name without its own voltage suffix
    /// (" 400kV", " 400 kV"), or "Bus N" when it has no name.</summary>
    private static string ZoneName(Bus b)
    {
        if (string.IsNullOrWhiteSpace(b.Name)) return $"Bus {b.Id}";
        string kv = b.BaseKv.ToString("0.###", CultureInfo.InvariantCulture);
        return b.Name.Replace($" {kv}kV", "", StringComparison.Ordinal).Replace($" {kv} kV", "", StringComparison.Ordinal);
    }

    /// <summary>"400kV" when every voltage in the column is the same known value, else the generic label.</summary>
    private static string Header(IEnumerable<double> kvs, string generic)
    {
        var distinct = kvs.Distinct().ToList();
        return distinct.Count == 1 && distinct[0] > 0.0
            ? distinct[0].ToString("0.###", CultureInfo.InvariantCulture) + "kV"
            : generic;
    }
}
