using System.Text;
using GridSim.Core.Interpose.Protocols;

namespace GridSim.Core.Interpose;

/// <summary>
/// The set of selectable RTU/SCADA protocol stacks, keyed by their lowercase <see cref="IRtuProtocol.Name"/>.
/// The <c>interpose --protocol &lt;name&gt;</c> verb resolves a stack here; <c>--list-protocols</c> renders
/// <see cref="Describe"/>. New stacks register in the static constructor as their object-model decoders land.
/// <para>GR-329: the registry holds a FACTORY per protocol and every <see cref="TryGet"/> returns a NEW instance. A
/// protocol object can be stateful (the Modbus stack remembers its outstanding requests by transaction id), and the
/// registry used to hand one shared instance to every caller, so two pipelines in one process (two interposers, or
/// two tests) shared that state. Each caller now owns its own.</para>
/// </summary>
public static class RtuProtocolRegistry
{
    private static readonly Dictionary<string, Func<IRtuProtocol>> ByName = new(StringComparer.OrdinalIgnoreCase);

    static RtuProtocolRegistry()
    {
        Register(() => new Iec104Protocol());
        Register(() => new ModbusProtocol());
    }

    // The name is read from one instance made for the purpose; the factory is what is kept.
    private static void Register(Func<IRtuProtocol> factory) => ByName[factory().Name] = factory;

    /// <summary>Resolve a protocol by name (case-insensitive). Each call returns a fresh instance (GR-329).</summary>
    public static bool TryGet(string name, out IRtuProtocol protocol)
    {
        if (ByName.TryGetValue(name, out Func<IRtuProtocol>? factory))
        {
            protocol = factory();
            return true;
        }
        protocol = null!;
        return false;
    }

    /// <summary>All registered protocols, in registration order, each a fresh instance (GR-329).</summary>
    public static IReadOnlyCollection<IRtuProtocol> All => ByName.Values.Select(f => f()).ToList();

    /// <summary>A human-readable table of the registered protocols for <c>--list-protocols</c>.</summary>
    public static string Describe()
    {
        var sb = new StringBuilder();
        sb.AppendLine("Registered RTU/SCADA protocols:");
        sb.AppendLine($"  {"name",-10} {"transport",-9} {"port",-6} description");
        foreach (IRtuProtocol p in All)
        {
            string port = p.DefaultPort > 0 ? p.DefaultPort.ToString(System.Globalization.CultureInfo.InvariantCulture) : "-";   // GR-830
            sb.AppendLine($"  {p.Name,-10} {p.Transport,-9} {port,-6} {p.DisplayName}");
        }
        // R089 (October 2026): say what is NOT here, so nobody reads the DNP3 / GOOSE codecs in the source as a protocol
        // they can select.
        sb.AppendLine("  Not selectable: DNP3 and IEC 61850 GOOSE exist only as link-framing scaffolding (no application layer).");
        return sb.ToString();
    }
}
