using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using GridSim.Core.Numerics;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// D1 (October 2026, Mark's determinism rule 2026-10-04): the analyser that keeps GridSim's numerics deterministic. It
/// reads the COMPILED GridSim.Core.dll (and GridSim.Gda, GridSim.Gpu and the gridsim CLI) (System.Reflection.Metadata), decodes the IL of every method body, and fails if any
/// call or method-pointer load targets a platform-dependent maths member:
/// <list type="bullet">
/// <item>System.Math / System.MathF / System.Double transcendentals (Sin, Cos, Tan, SinCos, Asin, Acos, Atan, Atan2,
/// Exp, Log, Log10, Log2, Pow, Sinh, Cosh, Tanh and the rest): these call the host operating system's C runtime,
/// which rounds differently on Windows, Linux and macOS (N175);</item>
/// <item>System.Numerics.Complex members built on them (Phase, FromPolarCoordinates, Exp, Log, Pow, Sin, Cos, Tan,
/// Asin, Acos, Atan, Sinh, Cosh, Tanh);</item>
/// <item>fused multiply-add (Math.FusedMultiplyAdd, Vector*.FusedMultiplyAdd, MultiplyAddEstimate) and horizontal
/// vector reductions (Vector*.Sum, Vector*.Dot) and vector transcendentals, whose bits depend on the CPU tier.</item>
/// </list>
/// The one allowed exception is SimdKernels.AddScaledRow's opt-in FMA arm (no numerical path selects it; the
/// source check below enforces that). Working on IL rather than source text means the property form
/// (<c>z.Phase</c>), a fully qualified name or a <c>using static</c> cannot slip past.
/// </summary>
public class DeterministicMathGuardTests
{
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
        // GR-861 (October 2026): the reciprocal estimates are approximations whose bits are the CPU's (they differ
        // between x64 and Arm and between instruction sets), and Lerp may be lowered to a fused multiply-add; neither
        // is used by GridSim today, and this keeps it that way.
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

    // The only places a forbidden member may appear: the tested, opt-in FMA arm of the scaled-row add.
    private static bool Allowed(string callerType, string callerMethod, string member)
        => callerType == "GridSim.Core.Numerics.SimdKernels" && callerMethod == "AddScaledRow" && member == "FusedMultiplyAdd";

    // Operand size of every IL opcode, read from System.Reflection.Emit.OpCodes.
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

    // The assemblies that carry numerical work: Core (all capability), Gda (builds models Core solves), Gpu (solver
    // backends) and the CLI (which drives them). Web, the desktop app and the schema editor are presentation only.
    private static IEnumerable<string> NumericalAssemblies()
    {
        string bin = AppContext.BaseDirectory;
        yield return typeof(DetMath).Assembly.Location;
        yield return Path.Combine(bin, "GridSim.Gda.dll");
        yield return Path.Combine(bin, "GridSim.Gpu.dll");
        var dir = new DirectoryInfo(bin);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "GridSim.slnx"))) dir = dir.Parent;
        string config = bin.Contains($"{Path.DirectorySeparatorChar}Debug{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ? "Debug" : "Release";
        yield return Path.Combine(dir!.FullName, "src", "GridSim.Cli", "bin", config, "net10.0", "gridsim.dll");
    }

    private static List<Call> ScanAll()
    {
        var all = new List<Call>();
        foreach (string path in NumericalAssemblies())
        {
            Assert.True(File.Exists(path), $"assembly to scan not found: {path}");
            all.AddRange(Scan(path));
        }
        return all;
    }

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
            // Nested types (closures, local functions, iterators) report their outermost declaring type.
            TypeDefinition outer = t;
            while (outer.GetDeclaringType() is { IsNil: false } parent) outer = md.GetTypeDefinition(parent);
            string typeName = md.GetString(outer.Namespace) + "." + md.GetString(outer.Name);
            foreach (MethodDefinitionHandle mh in t.GetMethods())
            {
                MethodDefinition m = md.GetMethodDefinition(mh);
                if (m.RelativeVirtualAddress == 0) continue;
                string methodName = md.GetString(m.Name);
                // A local function or lambda compiles to "<Outer>g__Name|x_y"; keep the outer method's name.
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

    private static readonly Lazy<List<Call>> CoreCalls = new(ScanAll);

    [Fact]
    public void GridSim_Core_Gda_Gpu_and_Cli_call_no_platform_dependent_maths_outside_the_deterministic_module()
    {
        var bad = CoreCalls.Value
            .Where(c => Forbidden(c.TargetNamespace, c.TargetType, c.TargetMember) && !Allowed(c.CallerType, c.CallerMethod, c.TargetMember))
            .Select(c => $"{c.CallerType}.{c.CallerMethod} calls {c.TargetNamespace}.{c.TargetType}.{c.TargetMember}")
            .Distinct()
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToList();
        Assert.True(bad.Count == 0,
            "Platform-dependent maths in a numerical assembly (route it through GridSim.Core.Numerics.DetMath; see D1):\n" + string.Join("\n", bad));
    }

    [Fact]
    public void The_scanner_sees_calls_it_must_see()
    {
        // Not vacuous: the scan finds the allowed System.Math members Core does use, and the DetMath routines.
        var calls = CoreCalls.Value;
        Assert.Contains(calls, c => c.TargetNamespace == "System" && c.TargetType == "Math" && c.TargetMember == "Sqrt");
        Assert.Contains(calls, c => c.TargetType == "DetMath" && c.TargetMember == "SinCos");
        Assert.Contains(calls, c => c.TargetType == "DetMath" && c.TargetMember == "Phase");
        Assert.Contains(calls, c => c.CallerType == "GridSim.Core.Numerics.SimdKernels" && c.TargetMember == "FusedMultiplyAdd");
        // And the forbidden list really matches what it is meant to: the fdlibm module itself uses Math.Sqrt only.
        Assert.True(Forbidden("System", "Math", "Atan2"));
        Assert.True(Forbidden("System.Numerics", "Complex", "get_Phase"));
        Assert.True(Forbidden("System.Runtime.Intrinsics", "Vector256", "Sum"));
        Assert.False(Forbidden("System", "Math", "Sqrt"));
        Assert.False(Forbidden("System.Numerics", "Complex", "get_Magnitude"));
    }

    [Fact]
    public void No_numerical_path_selects_the_fused_multiply_add_arm()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "GridSim.slnx"))) dir = dir.Parent;
        Assert.NotNull(dir);
        var hits = Directory.EnumerateFiles(Path.Combine(dir!.FullName, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(p => !p.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            .SelectMany(p => File.ReadLines(p).Select((line, i) => (p, i, line)))
            .Where(x => x.line.Contains("fastFma: true", StringComparison.Ordinal) && !x.line.TrimStart().StartsWith("///", StringComparison.Ordinal))
            .Select(x => $"{Path.GetRelativePath(dir.FullName, x.p)}:{x.i + 1}")
            .ToList();
        Assert.True(hits.Count == 0, "fastFma: true on a source line (CPU-dependent last bit; D1):\n" + string.Join("\n", hits));
    }

    [Fact]
    public void Every_AddScaledRow_call_outside_the_kernel_names_fastFma_false()
    {
        // GR-393: the text search above misses a positional argument, a variable or a constant. This one is strict
        // the other way: every call of AddScaledRow in src (other than the kernel's own forwarding overloads) must
        // carry the literal "fastFma: false" on the same line. A call written over several lines fails here too,
        // which is deliberate: keep the call on one line so the guard can read it.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "GridSim.slnx"))) dir = dir.Parent;
        Assert.NotNull(dir);
        var calls = Directory.EnumerateFiles(Path.Combine(dir!.FullName, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(p => !p.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            .Where(p => !string.Equals(Path.GetFileName(p), "SimdKernels.cs", StringComparison.Ordinal))
            .SelectMany(p => File.ReadLines(p).Select((line, i) => (p, i, line)))
            .Where(x => x.line.Contains("AddScaledRow(", StringComparison.Ordinal) && !x.line.TrimStart().StartsWith("//", StringComparison.Ordinal))
            .ToList();
        // The guard must be looking at something: the estimator steps call the kernel today.
        Assert.NotEmpty(calls);
        var bad = calls.Where(x => !x.line.Contains("fastFma: false", StringComparison.Ordinal))
            .Select(x => $"{Path.GetRelativePath(dir.FullName, x.p)}:{x.i + 1}")
            .ToList();
        Assert.True(bad.Count == 0, "AddScaledRow call without a literal fastFma: false (CPU-dependent last bit; D1):\n" + string.Join("\n", bad));
    }

    [Fact]
    public void Enumerable_Sum_over_doubles_adds_left_to_right_in_this_runtime_and_mode()
    {
        // GridSim sums double arrays with LINQ's Sum in places. That is deterministic only while the runtime keeps
        // floating-point Sum sequential (it vectorises Sum for integers only). This pins the assumption in every
        // SIMD mode the suite runs: a change in the runtime fails here instead of silently moving numbers.
        ulong s = 12345;
        for (int n = 0; n <= 200; n++)
        {
            var a = new double[n];
            for (int i = 0; i < n; i++)
            {
                s = s * 6364136223846793005UL + 1442695040888963407UL;
                a[i] = ((s >> 11) * (1.0 / (1UL << 53)) - 0.5) * DetMath.Pow(10.0, (i % 9) - 4);
            }
            double seq = 0.0;
            foreach (double v in a) seq += v;
            Assert.Equal(BitConverter.DoubleToInt64Bits(seq), BitConverter.DoubleToInt64Bits(a.Sum()));
            Assert.Equal(BitConverter.DoubleToInt64Bits(seq), BitConverter.DoubleToInt64Bits(a.AsEnumerable().Sum()));
            Assert.Equal(BitConverter.DoubleToInt64Bits(seq), BitConverter.DoubleToInt64Bits(a.ToList().Sum()));
        }
    }
}
