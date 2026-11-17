using GridSim.Core.Analysis.DynamicsStudy;
using GridSim.Core.IO.Psse;

namespace GridSim.Tests;

/// <summary>RC3 P6: shared fixtures and request builders for the dynamics study tests.</summary>
internal static class DynamicsStudyTestSupport
{
    internal static string Psse(string name) => Path.Combine(CliProcess.Root(), "tests", "GridSim.Tests", "fixtures", "psse", name);

    internal static string Study(string name) => Path.Combine(CliProcess.Root(), "tests", "GridSim.Tests", "fixtures", "dynamics-study", name);

    internal static string Schemas => Path.Combine(CliProcess.Root(), "schemas", "v2");

    internal static string Text(string path) => File.ReadAllText(path).Replace("\r\n", "\n", StringComparison.Ordinal);

    internal static PsseParseResult Raw(string path) => PsseRawParser.ParseWithDiagnostics(Text(path), Path.GetFileNameWithoutExtension(path));

    internal static PsseParseResult RawText(string text, string name) => PsseRawParser.ParseWithDiagnostics(text, name);

    internal static readonly DynamicsInitialisation Init = new(1e-8, 100, DynamicsUnmodelledPolicy.Refuse);

    internal static readonly DynamicsCriteria Criteria = new(2.0 * Math.PI, 180.0);

    internal static DynamicsBranchId Br(int from, int to) => new(from, to, "1");

    internal static DynamicsMachineId M(int bus, string id = "1") => new(bus, id);

    /// <summary>A bolted fault at bus 4 at 0.1 s, cleared at <paramref name="clear"/> by opening branch 4-5 (the
    /// default 50 ms clearing is inside this engine's 98 ms bracket on case14-study.dyr).</summary>
    internal static DynamicsCaseRequest Fault4(string name, double clear = 0.15) => new(name, DynamicsCaseKind.Events, new[]
    {
        new DynamicsEventRequest(DynamicsEventKind.BusFault, 0.1, Bus: 4),
        new DynamicsEventRequest(DynamicsEventKind.FaultClear, clear, Bus: 4),
        new DynamicsEventRequest(DynamicsEventKind.BranchTrip, clear, Branch: Br(4, 5)),
    });

    internal static IReadOnlyList<DynamicsChannelRequest> Channels(params DynamicsMachineId[] machines)
    {
        var list = new List<DynamicsChannelRequest>();
        foreach (DynamicsMachineId m in machines)
        {
            list.Add(new DynamicsChannelRequest(DynamicsChannelKind.RotorAngle, m));
            list.Add(new DynamicsChannelRequest(DynamicsChannelKind.Speed, m));
            list.Add(new DynamicsChannelRequest(DynamicsChannelKind.Frequency, m));
        }
        list.Add(new DynamicsChannelRequest(DynamicsChannelKind.BusVoltage, Bus: 4));
        return list;
    }

    internal static DynamicsStudyRequest Request(PsseParseResult raw, string dyr, IReadOnlyList<DynamicsCaseRequest> cases,
        IReadOnlyList<DynamicsChannelRequest>? channels = null, DynamicsInitialisation? init = null, double step = 0.005,
        double duration = 3.0, int parallelism = 1, string dyrName = "case14.dyr") =>
        new(raw, dyr, dyrName, Schemas, init ?? Init, step, duration, Criteria, M(1), channels ?? Channels(M(1), M(2)), cases, parallelism);

    internal static DynamicsStudyRequest Case14(IReadOnlyList<DynamicsCaseRequest> cases, double duration = 3.0, int parallelism = 1,
        DynamicsInitialisation? init = null, string? dyrText = null) =>
        Request(Raw(Psse("case14.raw")), dyrText ?? Text(Study("case14-study.dyr")), cases, duration: duration, parallelism: parallelism, init: init);
}
