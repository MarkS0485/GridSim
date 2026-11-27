using System.Globalization;
using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using GridSim.Core.Numerics;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// Determinism audit of 2026-11-21, added beside the existing guards (which are unchanged):
/// <list type="bullet">
/// <item>the same platform-dependent maths rules as <see cref="DeterministicMathGuardTests"/>, over the assemblies neither
/// that guard nor <see cref="PresentationMathGuardTests"/> reads: GridSim.Scripting, the gridsim-script console,
/// GridSim.App, GridSim.App.Desktop, GridSim.Wpf and GridSim.SchemaEditor (no allowlist: none of them draws with the
/// host's trigonometry today);</item>
/// <item>no direct call of a CPU-specific intrinsic (System.Runtime.Intrinsics.X86 / Arm, Fma.MultiplyAdd and the rest)
/// in any GridSim assembly, other than the IsSupported probes the SIMD tier report reads;</item>
/// <item>System.Numerics.Complex.Abs (and Magnitude, which calls it) is built in this runtime from correctly rounded IEEE
/// operations only (no fused multiply-add, no C runtime call), so |z| has the same bits on every CPU and operating
/// system; it is not routed through DetMath, because DetMath has no |z| of its own and a different algorithm would move
/// the last bit of every pinned magnitude;</item>
/// <item>refusal text that carries a number is written in the invariant culture, and gridsim-script pins the invariant
/// culture at start-up as the gridsim command line does.</item>
/// </list>
/// The wider assemblies are read from their own build output (src/&lt;project&gt;/bin/&lt;configuration&gt;/&lt;tfm&gt;),
/// as the numerical guard reads the CLI's, so the solution must be built first (dotnet build GridSim.slnx).
/// </summary>
public class DeterminismScopeGuardTests
{
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

    // A CPU-specific intrinsic class (Fma, Avx2, Avx512F, Sse2, AdvSimd and their nested X64 / Arm64 classes): its
    // arithmetic members are the instruction itself, so their bits are the CPU's. Only the IsSupported probe is allowed.
    private static bool CpuIntrinsic(string ns, string member) =>
        (ns == "System.Runtime.Intrinsics.X86" || ns == "System.Runtime.Intrinsics.Arm" || ns == "System.Runtime.Intrinsics.Wasm")
        && member != "get_IsSupported";

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

    // A type reference's namespace and name; a nested reference (Fma.X64) reports its outermost type's namespace and
    // "Outer+Inner" as its name, so the intrinsic rule sees System.Runtime.Intrinsics.X86 for it too.
    private static (string Ns, string Type) TypeRefName(MetadataReader md, TypeReferenceHandle h)
    {
        TypeReference tr = md.GetTypeReference(h);
        string name = md.GetString(tr.Name);
        if (tr.ResolutionScope.Kind == HandleKind.TypeReference)
        {
            (string Ns, string Type) outer = TypeRefName(md, (TypeReferenceHandle)tr.ResolutionScope);
            return (outer.Ns, outer.Type + "+" + name);
        }
        return (md.GetString(tr.Namespace), name);
    }

    private static (string Ns, string Type, string Member)? Resolve(MetadataReader md, EntityHandle h)
    {
        switch (h.Kind)
        {
            case HandleKind.MethodSpecification:
                return Resolve(md, md.GetMethodSpecification((MethodSpecificationHandle)h).Method);
            case HandleKind.MemberReference:
            {
                MemberReference mr = md.GetMemberReference((MemberReferenceHandle)h);
                string name = md.GetString(mr.Name);
                if (mr.Parent.Kind == HandleKind.TypeReference)
                {
                    (string Ns, string Type) t = TypeRefName(md, (TypeReferenceHandle)mr.Parent);
                    return (t.Ns, t.Type, name);
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

    // The calls (and method-pointer loads) in one method body.
    private static IEnumerable<(string Ns, string Type, string Member)> Callees(PEReader pe, MetadataReader md, MethodDefinition m)
    {
        var found = new List<(string Ns, string Type, string Member)>();
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
                if (Resolve(md, MetadataTokens.EntityHandle(token)) is { } tg) found.Add(tg);
            }
            i += size;
        }
        return found;
    }

    // The same IL walk as DeterministicMathGuardTests.Scan (closures and local functions reported against their
    // outermost declaring type and outer method).
    private static List<Call> Scan(string path)
    {
        using var fs = File.OpenRead(path);
        using var pe = new PEReader(fs);
        MetadataReader md = pe.GetMetadataReader();
        var calls = new List<Call>();
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
                foreach ((string Ns, string Type, string Member) tg in Callees(pe, md, m))
                    calls.Add(new Call(typeName, methodName, tg.Ns, tg.Type, tg.Member));
            }
        }
        return calls;
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "GridSim.slnx"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    // The configuration this test assembly was built in; the solution build puts every project in the same one.
    private static string Configuration() =>
        AppContext.BaseDirectory.Contains($"{Path.DirectorySeparatorChar}Debug{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ? "Debug" : "Release";

    private static string Built(string project, string tfm, string assembly) =>
        Path.Combine(RepoRoot(), "src", project, "bin", Configuration(), tfm, assembly);

    // The assemblies the two existing guards do not read.
    private static IEnumerable<string> WiderAssemblies()
    {
        yield return Built("GridSim.Scripting", "net10.0", "GridSim.Scripting.dll");
        yield return Built("GridSim.Scripting.Console", "net10.0", "gridsim-script.dll");
        yield return Built("GridSim.App", "net10.0", "GridSim.App.dll");
        yield return Built("GridSim.App.Desktop", "net10.0", "GridSim.App.Desktop.dll");
        yield return Built("GridSim.Wpf", "net10.0-windows", "GridSim.Wpf.dll");
        yield return Path.Combine(AppContext.BaseDirectory, "GridSim.SchemaEditor.dll");
    }

    // Every GridSim assembly: the numerical ones, the presentation ones and the wider ones above.
    private static IEnumerable<string> AllAssemblies()
    {
        string bin = AppContext.BaseDirectory;
        yield return typeof(DetMath).Assembly.Location;
        yield return Path.Combine(bin, "GridSim.Gda.dll");
        yield return Path.Combine(bin, "GridSim.Gpu.dll");
        yield return Built("GridSim.Cli", "net10.0", "gridsim.dll");
        yield return Path.Combine(bin, "GridSim.Ui.dll");
        yield return Path.Combine(bin, "GridSim.Web.dll");
        foreach (string p in WiderAssemblies()) yield return p;
    }

    private static List<(string Path, List<Call> Calls)> ScanEach(IEnumerable<string> paths)
    {
        var all = new List<(string Path, List<Call> Calls)>();
        foreach (string path in paths)
        {
            Assert.True(File.Exists(path), $"assembly to scan not found (build GridSim.slnx first): {path}");
            all.Add((path, Scan(path)));
        }
        return all;
    }

    private static readonly Lazy<List<(string Path, List<Call> Calls)>> WiderCalls = new(() => ScanEach(WiderAssemblies()));
    private static readonly Lazy<List<(string Path, List<Call> Calls)>> AllCalls = new(() => ScanEach(AllAssemblies()));

    [Fact]
    public void Scripting_the_script_console_App_Desktop_Wpf_and_SchemaEditor_call_no_platform_dependent_maths()
    {
        var bad = WiderCalls.Value
            .SelectMany(a => a.Calls.Select(c => (a.Path, c)))
            .Where(x => Forbidden(x.c.TargetNamespace, x.c.TargetType, x.c.TargetMember))
            .Select(x => $"{Path.GetFileName(x.Path)}: {x.c.CallerType}.{x.c.CallerMethod} calls {x.c.TargetNamespace}.{x.c.TargetType}.{x.c.TargetMember}")
            .Distinct()
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToList();
        Assert.True(bad.Count == 0,
            "Platform-dependent maths in a GridSim assembly (route it through GridSim.Core.Numerics.DetMath; see D1):\n" + string.Join("\n", bad));
    }

    [Fact]
    public void The_wider_scan_reads_every_assembly_it_names()
    {
        // Not vacuous: each assembly is found and its method bodies are decoded (every one of them calls something).
        foreach ((string path, List<Call> calls) in WiderCalls.Value)
            Assert.True(calls.Count > 0, $"no call decoded in {path}");
        Assert.Equal(6, WiderCalls.Value.Count);
    }

    [Fact]
    public void No_GridSim_assembly_calls_a_cpu_specific_intrinsic_other_than_its_IsSupported_probe()
    {
        var bad = AllCalls.Value
            .SelectMany(a => a.Calls.Select(c => (a.Path, c)))
            .Where(x => CpuIntrinsic(x.c.TargetNamespace, x.c.TargetMember))
            .Select(x => $"{Path.GetFileName(x.Path)}: {x.c.CallerType}.{x.c.CallerMethod} calls {x.c.TargetNamespace}.{x.c.TargetType}.{x.c.TargetMember}")
            .Distinct()
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToList();
        Assert.True(bad.Count == 0,
            "CPU-specific intrinsic called directly (its bits are the CPU's; use the portable Vector arithmetic without fused multiply-add):\n"
            + string.Join("\n", bad));

        // Not vacuous: the SIMD tier report's FMA probe is seen, under the intrinsic namespace this rule matches.
        Assert.Contains(AllCalls.Value.SelectMany(a => a.Calls), c => c.CallerType == "GridSim.Core.Numerics.SimdCapabilities"
            && c.TargetNamespace == "System.Runtime.Intrinsics.X86" && c.TargetType == "Fma" && c.TargetMember == "get_IsSupported");
        Assert.True(CpuIntrinsic("System.Runtime.Intrinsics.X86", "MultiplyAdd"));
        Assert.True(CpuIntrinsic("System.Runtime.Intrinsics.Arm", "FusedMultiplyAdd"));
        Assert.False(CpuIntrinsic("System.Runtime.Intrinsics.X86", "get_IsSupported"));
        Assert.False(CpuIntrinsic("System.Runtime.Intrinsics", "Add"));
    }

    // The members the runtime's |z| may be built from: exact (abs, classification, bit moves) or correctly rounded (sqrt).
    private static readonly HashSet<string> CorrectlyRoundedLeaves = new(StringComparer.Ordinal)
    {
        "System.Double.IsFinite", "System.Double.IsInfinity", "System.Double.IsNaN", "System.Double.IsPositiveInfinity",
        "System.Double.IsNegativeInfinity", "System.Double.Abs", "System.Double.Sqrt", "System.Math.Abs", "System.Math.Sqrt",
        "System.BitConverter.DoubleToUInt64Bits", "System.BitConverter.UInt64BitsToDouble",
        "System.BitConverter.DoubleToInt64Bits", "System.BitConverter.Int64BitsToDouble",
    };

    // The callees of every method called 'method' with an IL body on the top-level type ns.type of the assembly at 'path'.
    private static List<string> CalleesOf(string path, string ns, string type, string method)
    {
        using var fs = File.OpenRead(path);
        using var pe = new PEReader(fs);
        MetadataReader md = pe.GetMetadataReader();
        var found = new List<string>();
        bool any = false;
        foreach (TypeDefinitionHandle th in md.TypeDefinitions)
        {
            TypeDefinition t = md.GetTypeDefinition(th);
            if (!t.GetDeclaringType().IsNil || md.GetString(t.Namespace) != ns || md.GetString(t.Name) != type) continue;
            foreach (MethodDefinitionHandle mh in t.GetMethods())
            {
                MethodDefinition m = md.GetMethodDefinition(mh);
                if (md.GetString(m.Name) != method) continue;
                Assert.True(m.RelativeVirtualAddress != 0 && (m.ImplAttributes & MethodImplAttributes.InternalCall) == 0,
                    $"{ns}.{type}.{method} has no IL body in {path} (a runtime or C-library call)");
                any = true;
                found.AddRange(Callees(pe, md, m).Select(c => c.Ns + "." + c.Type + "." + c.Member));
            }
        }
        Assert.True(any, $"{ns}.{type}.{method} not found in {path}");
        return found;
    }

    [Fact]
    public void Complex_Abs_and_Magnitude_are_built_from_correctly_rounded_IEEE_operations_in_this_runtime()
    {
        // GridSim takes |z| from Complex.Magnitude in hundreds of places (the numerical guard allows it) and from
        // Complex.Abs in the EMT line fitter. Both are deterministic on every CPU and operating system only while the
        // runtime builds them from exact and correctly rounded IEEE operations (no fused multiply-add, no C runtime
        // call; the JIT does not contract a * b + c on its own). This reads the runtime's own IL and fails if that changes.
        string numerics = typeof(System.Numerics.Complex).Assembly.Location;
        string coreLib = typeof(double).Assembly.Location;

        List<string> magnitude = CalleesOf(numerics, "System.Numerics", "Complex", "get_Magnitude");
        Assert.All(magnitude, c => Assert.True(c == "System.Numerics.Complex.Abs" || CorrectlyRoundedLeaves.Contains(c),
            $"Complex.Magnitude calls {c}"));

        var bad = new List<string>();
        foreach (string c in CalleesOf(numerics, "System.Numerics", "Complex", "Abs"))
        {
            if (CorrectlyRoundedLeaves.Contains(c)) continue;
            List<string> inner;
            if (c == "System.Double.Hypot") inner = CalleesOf(coreLib, "System", "Double", "Hypot");
            else if (c.StartsWith("System.Numerics.Complex.", StringComparison.Ordinal))
                inner = CalleesOf(numerics, "System.Numerics", "Complex", c["System.Numerics.Complex.".Length..]);
            else { bad.Add("Complex.Abs calls " + c); continue; }
            bad.AddRange(inner.Where(x => !CorrectlyRoundedLeaves.Contains(x)).Select(x => c + " calls " + x));
        }
        Assert.True(bad.Count == 0, "the runtime's |z| is no longer built from correctly rounded operations only:\n" + string.Join("\n", bad));
    }

    [Fact]
    public void Complex_Abs_and_Magnitude_are_the_same_function()
    {
        // The fitter's Complex.Abs and the rest of GridSim's Complex.Magnitude give the same bits, so the two spellings
        // are one deterministic function, over magnitudes from 1e-300 to 1e300 and both signs.
        ulong s = 0x9E3779B97F4A7C15UL;
        double Next()
        {
            s = s * 6364136223846793005UL + 1442695040888963407UL;
            double u = (s >> 11) * (1.0 / (1UL << 53));
            s = s * 6364136223846793005UL + 1442695040888963407UL;
            int e = (int)((s >> 33) % 601) - 300;
            return (u - 0.5) * DetMath.Pow(10.0, e);
        }
        for (int k = 0; k < 20000; k++)
        {
            var z = new System.Numerics.Complex(Next(), k % 7 == 0 ? 0.0 : Next());
            Assert.Equal(BitConverter.DoubleToInt64Bits(z.Magnitude), BitConverter.DoubleToInt64Bits(System.Numerics.Complex.Abs(z)));
        }
    }

    // Run 'body' with de-DE as the current culture on this thread, then put the old culture back.
    private static T UnderGerman<T>(Func<T> body)
    {
        CultureInfo old = CultureInfo.CurrentCulture, oldUi = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("de-DE");
            return body();
        }
        finally { CultureInfo.CurrentCulture = old; CultureInfo.CurrentUICulture = oldUi; }
    }

    [Fact]
    public void A_distribution_refusal_writes_its_number_invariantly_on_a_comma_decimal_machine()
    {
        ArgumentException ex = UnderGerman(() => Assert.Throws<ArgumentException>(
            () => new GridSim.Core.Analysis.Probabilistic.NormalDistribution(0.0, -1.5)));
        Assert.Contains("standard deviation must be above 0 (got -1.5).", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_correlation_matrix_refusal_writes_its_pivot_invariantly_on_a_comma_decimal_machine()
    {
        ArgumentException ex = UnderGerman(() => Assert.Throws<ArgumentException>(
            () => GridSim.Core.Stochastic.Cholesky.Factor(new double[,] { { -2.5 } })));
        Assert.Contains("(pivot 0 is -2.5)", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_script_console_pins_the_invariant_culture_before_it_runs_anything()
    {
        // As the gridsim command line, the web host, the desktop apps and the schema editor do at start-up.
        string src = File.ReadAllText(Path.Combine(RepoRoot(), "src", "GridSim.Scripting.Console", "Program.cs"));
        int main = src.IndexOf("public static int Main(string[] args)", StringComparison.Ordinal);
        int dispatch = src.IndexOf("switch (args[0])", StringComparison.Ordinal);
        int pinDefault = src.IndexOf("CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;", StringComparison.Ordinal);
        int pinCurrent = src.IndexOf("CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;", StringComparison.Ordinal);
        Assert.True(main >= 0 && dispatch > main, "gridsim-script's Main and its command dispatch not found");
        Assert.True(pinDefault > main && pinDefault < dispatch, "gridsim-script does not pin DefaultThreadCurrentCulture at the start of Main");
        Assert.True(pinCurrent > main && pinCurrent < dispatch, "gridsim-script does not pin CurrentCulture at the start of Main");
    }
}
