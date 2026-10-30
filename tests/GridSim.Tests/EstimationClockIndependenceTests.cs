using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using GridSim.Core.Estimation;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// No estimation code reads the clock. An estimate is a function of (model, measurements, options) alone; the wall
/// clock may appear only in the forward-inference guard's horizon check and in audit-log run metadata, both outside
/// the estimation namespace. If estimation code read the clock, a re-run of the same window could differ from the
/// recorded estimate, and replay could no longer serve as forensic evidence.
///
/// <para>The check reads the compiled GridSim.Core.dll (System.Reflection.Metadata) and decodes the IL of every method
/// declared in a GridSim.Core.Estimation namespace (closures and local functions included, through their declaring
/// type), and fails on any direct call to a clock source: DateTime.Now / UtcNow / Today, DateTimeOffset.Now / UtcNow,
/// Environment.TickCount / TickCount64, any Stopwatch member, or any TimeProvider member. It uses the same IL-reading
/// technique as <see cref="DeterministicMathGuardTests"/>, with its own copy of the reader.</para>
/// </summary>
public class EstimationClockIndependenceTests
{
    private const string EstimationNamespace = "GridSim.Core.Estimation";

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

    private sealed record Call(string CallerNamespace, string CallerType, string TargetNamespace, string TargetType, string TargetMember);

    /// <summary>True for a call that reads the clock.</summary>
    private static bool IsClockRead(Call c) => (c.TargetNamespace, c.TargetType) switch
    {
        ("System", "DateTime") => c.TargetMember is "get_Now" or "get_UtcNow" or "get_Today",
        ("System", "DateTimeOffset") => c.TargetMember is "get_Now" or "get_UtcNow",
        ("System", "Environment") => c.TargetMember is "get_TickCount" or "get_TickCount64",
        ("System.Diagnostics", "Stopwatch") => true,
        ("System", "TimeProvider") => true,
        _ => false,
    };

    private static List<Call> ScanCore()
    {
        string path = typeof(WlsStateEstimator).Assembly.Location;
        Assert.True(File.Exists(path), $"assembly to scan not found: {path}");
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
            // Nested types (closures, local functions, iterators) report their outermost declaring type and namespace.
            TypeDefinition outer = t;
            while (outer.GetDeclaringType() is { IsNil: false } parent) outer = md.GetTypeDefinition(parent);
            string ns = md.GetString(outer.Namespace);
            string typeName = ns + "." + md.GetString(outer.Name);
            foreach (MethodDefinitionHandle mh in t.GetMethods())
            {
                MethodDefinition m = md.GetMethodDefinition(mh);
                if (m.RelativeVirtualAddress == 0) continue;
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
                        if (target is { } tg) calls.Add(new Call(ns, typeName, tg.Ns, tg.Type, tg.Member));
                    }
                    i += size;
                }
            }
        }
        return calls;
    }

    private static readonly Lazy<List<Call>> CoreCalls = new(ScanCore);

    private static bool InEstimation(Call c) =>
        c.CallerNamespace == EstimationNamespace || c.CallerNamespace.StartsWith(EstimationNamespace + ".", StringComparison.Ordinal);

    [Fact]
    public void No_estimation_code_reads_the_clock()
    {
        var bad = CoreCalls.Value
            .Where(c => InEstimation(c) && IsClockRead(c))
            .Select(c => $"{c.CallerType} calls {c.TargetNamespace}.{c.TargetType}.{c.TargetMember}")
            .Distinct()
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToList();
        Assert.True(bad.Count == 0, "Estimation code reads the clock (an estimate must depend only on its inputs):\n" + string.Join("\n", bad));
    }

    [Fact]
    public void The_scan_sees_estimation_code_and_does_detect_a_clock_read_elsewhere()
    {
        // Positive controls, so the test above cannot pass vacuously: the scan finds calls made by estimation code, and
        // it does find the clock read the forward-inference guard is documented to make.
        Assert.Contains(CoreCalls.Value, InEstimation);
        Assert.Contains(CoreCalls.Value, c => c.CallerType == "GridSim.Core.Diagnostics.ForwardInferenceGuard" && IsClockRead(c));
    }
}
