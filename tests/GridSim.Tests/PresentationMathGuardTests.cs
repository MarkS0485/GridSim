using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// GR-861 (2026-11-02, owner decision): a second determinism guard, over the PRESENTATION assemblies (GridSim.Ui, the
/// shared desktop and web view-model library, and GridSim.Web). <see cref="DeterministicMathGuardTests"/> guards the
/// numerical assemblies (Core, Gda, Gpu, the CLI) and treats presentation as out of scope, because display geometry
/// may use the host's <c>Math.Sin</c>/<c>Math.Cos</c>/<c>Math.Atan2</c>: a dial needle or a phasor arrow a pixel
/// different on another operating system changes no result. But presentation code also computes numbers people read
/// (totals, margins, percentages), and those must not quietly start using platform-dependent maths. So this guard reads
/// the compiled IL of every method in those assemblies and fails on any platform-dependent maths member, except in an
/// explicit allowlist of display-geometry types, each of which only draws:
/// <list type="bullet">
/// <item><c>GridSim.Ui.Visuals.PhasorGeometry</c>, <c>GridSim.Ui.Visuals.GaugeGeometry</c> and
/// <c>GridSim.Ui.Schematic.SchematicView</c> (named in the decision);</item>
/// <item><c>GridSim.Web.Components.Visuals.Gauge</c> and <c>GridSim.Web.Components.Visuals.Phasor</c>, the web's own
/// Razor drawings of the same dial and phasor, which compute the same screen coordinates.</item>
/// </list>
/// A new trigonometric or exponential call anywhere else in presentation code fails here, naming the caller, so it is
/// either routed through <c>GridSim.Core.Numerics.DetMath</c> or, if it only draws, added to the allowlist on purpose.
/// The forbidden-member lists are the same as the numerical guard's.
/// </summary>
public class PresentationMathGuardTests
{
    // The display-only types that may call the host's trigonometry (outermost declaring type, namespace-qualified).
    private static readonly HashSet<string> DisplayGeometryAllowlist = new(StringComparer.Ordinal)
    {
        "GridSim.Ui.Visuals.PhasorGeometry",
        "GridSim.Ui.Visuals.GaugeGeometry",
        "GridSim.Ui.Schematic.SchematicView",
        "GridSim.Web.Components.Visuals.Gauge",
        "GridSim.Web.Components.Visuals.Phasor",
    };

    // Kept identical to DeterministicMathGuardTests (D1, GR-861): platform-dependent maths.
    private static readonly HashSet<string> Transcendentals = new(StringComparer.Ordinal)
    {
        "Sin", "Cos", "Tan", "SinCos", "Asin", "Acos", "Atan", "Atan2", "Exp", "Log", "Log10", "Log2", "Pow",
        "Sinh", "Cosh", "Tanh", "Asinh", "Acosh", "Atanh", "Cbrt", "SinPi", "CosPi", "TanPi", "SinCosPi", "AsinPi",
        "AcosPi", "AtanPi", "Atan2Pi", "Exp2", "Exp10", "ExpM1", "Exp2M1", "Exp10M1", "LogP1", "Log2P1", "Log10P1",
        "Hypot", "RootN", "ReciprocalEstimate", "ReciprocalSqrtEstimate", "FusedMultiplyAdd", "MultiplyAddEstimate",
    };

    private static readonly HashSet<string> ComplexForbidden = new(StringComparer.Ordinal)
    {
        "get_Phase", "FromPolarCoordinates", "Exp", "Log", "Log10", "Pow", "Sin", "Cos", "Tan", "Asin", "Acos", "Atan",
        "Sinh", "Cosh", "Tanh",
    };

    private static readonly HashSet<string> VectorTypes = new(StringComparer.Ordinal)
    {
        "System.Numerics.Vector", "System.Runtime.Intrinsics.Vector64", "System.Runtime.Intrinsics.Vector128",
        "System.Runtime.Intrinsics.Vector256", "System.Runtime.Intrinsics.Vector512",
    };

    private static readonly HashSet<string> VectorForbidden = new(StringComparer.Ordinal)
    {
        "FusedMultiplyAdd", "MultiplyAddEstimate", "Sum", "Dot", "Sin", "Cos", "SinCos", "Exp", "Log", "Log2", "Hypot",
        "ReciprocalEstimate", "ReciprocalSqrtEstimate", "Lerp",
    };

    private static bool Forbidden(string ns, string type, string member)
    {
        string full = ns.Length == 0 ? type : ns + "." + type;
        return full switch
        {
            "System.Math" or "System.Double" => Transcendentals.Contains(member),
            "System.MathF" or "System.Single" => Transcendentals.Contains(member),
            "System.Numerics.Complex" => ComplexForbidden.Contains(member),
            "System.Numerics.Tensors.TensorPrimitives" => true,
            _ => VectorTypes.Contains(full) && VectorForbidden.Contains(member),
        };
    }

    private static readonly Dictionary<ushort, OpCode> OpCodesByValue = typeof(OpCodes)
        .GetFields(BindingFlags.Public | BindingFlags.Static)
        .Select(f => (OpCode)f.GetValue(null)!)
        .ToDictionary(o => (ushort)o.Value, o => o);

    private static int OperandSize(OpCode op, ReadOnlySpan<byte> il, int at) => op.OperandType switch
    {
        OperandType.InlineNone => 0,
        OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
        OperandType.InlineVar => 2,
        OperandType.InlineI8 or OperandType.InlineR => 8,
        OperandType.InlineSwitch => 4 + 4 * BitConverter.ToInt32(il.Slice(at, 4)),
        _ => 4,
    };

    private sealed record Call(string CallerType, string CallerMethod, string TargetNamespace, string TargetType, string TargetMember);

    // The presentation assemblies the test project references (both are copied into its output folder).
    private static IEnumerable<string> PresentationAssemblies()
    {
        string bin = AppContext.BaseDirectory;
        yield return Path.Combine(bin, "GridSim.Ui.dll");
        yield return Path.Combine(bin, "GridSim.Web.dll");
    }

    // The same IL walk as DeterministicMathGuardTests.Scan (calls and method-pointer loads, closures and local
    // functions reported against their outermost declaring type and outer method).
    private static List<Call> Scan(string path)
    {
        using var fs = File.OpenRead(path);
        using var pe = new PEReader(fs);
        MetadataReader md = pe.GetMetadataReader();
        var calls = new List<Call>();

        (string Ns, string Type, string Member)? Resolve(EntityHandle h)
        {
            switch (h.Kind)
            {
                case HandleKind.MethodSpecification:
                    return Resolve(md.GetMethodSpecification((MethodSpecificationHandle)h).Method);
                case HandleKind.MemberReference:
                {
                    MemberReference mr = md.GetMemberReference((MemberReferenceHandle)h);
                    string name = md.GetString(mr.Name);
                    if (mr.Parent.Kind == HandleKind.TypeReference)
                    {
                        TypeReference tr = md.GetTypeReference((TypeReferenceHandle)mr.Parent);
                        return (md.GetString(tr.Namespace), md.GetString(tr.Name), name);
                    }
                    return ("", "<typespec>", name);
                }
                case HandleKind.MethodDefinition:
                {
                    MethodDefinition d = md.GetMethodDefinition((MethodDefinitionHandle)h);
                    TypeDefinition t = md.GetTypeDefinition(d.GetDeclaringType());
                    return (md.GetString(t.Namespace), md.GetString(t.Name), md.GetString(d.Name));
                }
                default:
                    return null;
            }
        }

        foreach (TypeDefinitionHandle th in md.TypeDefinitions)
        {
            TypeDefinition t = md.GetTypeDefinition(th);
            TypeDefinition outer = t;
            while (outer.GetDeclaringType() is { IsNil: false } parent) outer = md.GetTypeDefinition(parent);
            string typeName = md.GetString(outer.Namespace) + "." + md.GetString(outer.Name);
            foreach (MethodDefinitionHandle mh in t.GetMethods())
            {
                MethodDefinition m = md.GetMethodDefinition(mh);
                if (m.RelativeVirtualAddress == 0) continue;
                string methodName = md.GetString(m.Name);
                if (methodName.StartsWith('<') && methodName.IndexOf('>') > 1) methodName = methodName[1..methodName.IndexOf('>')];
                ReadOnlySpan<byte> il = pe.GetMethodBody(m.RelativeVirtualAddress).GetILBytes();
                int i = 0;
                while (i < il.Length)
                {
                    ushort code = il[i];
                    i++;
                    if (code == 0xFE) { code = (ushort)(0xFE00 | il[i]); i++; }
                    OpCode op = OpCodesByValue[code];
                    int size = OperandSize(op, il, i);
                    if (op.OperandType == OperandType.InlineMethod)
                    {
                        int token = BitConverter.ToInt32(il.Slice(i, 4));
                        var target = Resolve(MetadataTokens.EntityHandle(token));
                        if (target is { } tg) calls.Add(new Call(typeName, methodName, tg.Ns, tg.Type, tg.Member));
                    }
                    i += size;
                }
            }
        }
        return calls;
    }

    private static readonly Lazy<List<Call>> Calls = new(() =>
    {
        var all = new List<Call>();
        foreach (string path in PresentationAssemblies())
        {
            Assert.True(File.Exists(path), $"presentation assembly to scan not found: {path}");
            all.AddRange(Scan(path));
        }
        return all;
    });

    [Fact]
    public void Presentation_code_calls_no_platform_dependent_maths_outside_the_display_geometry_allowlist()
    {
        var bad = Calls.Value
            .Where(c => Forbidden(c.TargetNamespace, c.TargetType, c.TargetMember) && !DisplayGeometryAllowlist.Contains(c.CallerType))
            .Select(c => $"{c.CallerType}.{c.CallerMethod} calls {c.TargetNamespace}.{c.TargetType}.{c.TargetMember}")
            .Distinct()
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToList();
        Assert.True(bad.Count == 0,
            "Platform-dependent maths in presentation code outside the display-geometry allowlist (route it through "
            + "GridSim.Core.Numerics.DetMath, or allowlist it on purpose if it only draws; see GR-861):\n" + string.Join("\n", bad));
    }

    [Fact]
    public void Every_allowlisted_type_exists_and_still_uses_the_hosts_trigonometry()
    {
        // An allowlist entry that no longer matches anything is stale and would silently widen the exemption if a type
        // of that name appeared later: each one must be present and must still make a forbidden call.
        var callers = Calls.Value
            .Where(c => Forbidden(c.TargetNamespace, c.TargetType, c.TargetMember))
            .Select(c => c.CallerType)
            .ToHashSet(StringComparer.Ordinal);
        var stale = DisplayGeometryAllowlist.Where(t => !callers.Contains(t)).OrderBy(t => t, StringComparer.Ordinal).ToList();
        Assert.True(stale.Count == 0, "allowlisted display-geometry types that make no forbidden call (remove them): " + string.Join(", ", stale));
    }
}
