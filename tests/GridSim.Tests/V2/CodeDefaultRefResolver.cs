using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace GridSim.Tests.V2;

/// <summary>
/// Resolves the <c>ref</c> of a kind file's <c>codeDefaults</c> entry to today's C# value and compares it, EXACTLY,
/// with the entry's <c>value</c>. Four reference forms are defined:
///
/// <list type="number">
/// <item><b><c>member:&lt;Assembly&gt;:&lt;Full.Type.Name&gt;.&lt;Member&gt;[.&lt;Member&gt;...]</c></b> - a const, static
///   readonly field or static property, read by reflection. After the static member, further dotted segments walk
///   instance members (for example <c>BatteryChemistryLibrary.Lfp.Ageing.CyclesTo80Percent</c>), and a segment may
///   pick one element out of a list with <c>Name[Prop=Value]</c> (for example
///   <c>HarmonicSourceLibrary.PvInverter[Order=3].MagnitudePercent</c>); exactly one element must match. Nested types
///   are written with <c>+</c>. The type is found as the longest dotted prefix that names a type in the assembly.</item>
/// <item><b><c>instance:&lt;Assembly&gt;:&lt;Full.Type.Name&gt;.&lt;Member&gt;[...]</c></b> - the value a freshly constructed
///   object carries (a property initialiser or a record default): the type is built with its parameterless
///   constructor (public or not), then the member path is walked as above.</item>
/// <item><b><c>param:&lt;Assembly&gt;:&lt;Full.Type.Name&gt;:&lt;Method or .ctor&gt;:&lt;parameter&gt;</c></b> - the compile-time default
///   of a method or constructor parameter (record positional defaults, optional arguments). Exactly one overload
///   of that name may declare a parameter of that name, and it must have a default.</item>
/// <item><b><c>source:&lt;repo-relative path&gt;:&lt;exact snippet&gt;</c></b> - a literal inside a method body, which no
///   reflection can reach. The file (line endings normalised to \n) must contain the snippet EXACTLY ONCE, and the
///   snippet must END in the value: for a number, the LAST numeric literal in the snippet must parse (invariant
///   culture) to exactly the value, and <c>"+Infinity"</c> requires the snippet to end in
///   <c>double.PositiveInfinity</c>; for a string, the snippet must contain it quoted; for a boolean, the last
///   <c>true</c>/<c>false</c> token must match. Parsing the literal is stricter than looking for the value's
///   round-trip text as a substring (that would accept 0.35 for 0.3, and could not see 1e-4 as 0.0001), so the
///   round-trip text requirement is met in its strong form.</item>
/// </list>
///
/// <para><b>How the CLI is read (N345, CF3, October 2026).</b> The test project does not reference GridSim.Cli (an
/// executable) as a library, so a <c>member:GridSim.Cli:...</c> reference is read from the CLI the build put next to
/// this test assembly (<c>src/GridSim.Cli/bin/&lt;Configuration&gt;/net10.0/gridsim.dll</c>, found by
/// <see cref="CliProcess.BuiltCliDll"/>, the same binary every CLI process test runs). The test project's
/// <c>ProjectReference</c> to the CLI (ReferenceOutputAssembly=false) makes the build compile the CLI first, so the copy
/// is the one built from this tree; under <c>--no-build</c> it is the one the process tests run, and a missing binary is
/// reported by name, never skipped. Only constants and static members are read (no CLI code runs). Before N345 a CLI
/// reference had to quote its source line (<c>source:</c>); every CLI value the kind specs cite is now a named constant
/// on <c>GridSim.Cli.CliDefaults</c>.</para>
///
/// <para>Comparison is exact (the same equality as <c>Assert.Equal(double, double)</c> with no precision): machine
/// epsilon is the point of GridSim, so a code default either is the code's number or it is not.</para>
/// </summary>
internal static class CodeDefaultRefResolver
{
    /// <summary>Assemblies a reflected reference may name: those the test project references.</summary>
    private static readonly string[] ReflectableAssemblies = { "GridSim.Core", "GridSim.Gda" };

    /// <summary>N345: the assembly label a reference uses for the CLI (its project name; the binary is gridsim.dll).</summary>
    internal const string CliAssemblyLabel = "GridSim.Cli";

    /// <summary>N345: the built CLI, loaded once for reflection. A missing binary throws FileNotFoundException, which
    /// <see cref="Mismatch"/> reports as the reason (it names the path), so a CLI reference never passes unread.</summary>
    private static readonly Lazy<Assembly> BuiltCli = new(() => Assembly.LoadFrom(CliProcess.BuiltCliDll()));

    private const BindingFlags AnyStatic = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.FlattenHierarchy;
    private const BindingFlags AnyInstance = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

    /// <summary>A C# numeric literal: optional sign, digits with optional separators, fraction, exponent and type
    /// suffix, not glued to an identifier or another number.</summary>
    private static readonly Regex NumericLiteral =
        new(@"(?<![\w.])-?\d[\d_]*(?:\.\d[\d_]*)?(?:[eE][+-]?\d+)?[dDfFmM]?(?![\w.])", RegexOptions.CultureInvariant);

    private static readonly Regex BoolLiteral = new(@"\b(true|false)\b", RegexOptions.CultureInvariant);

    /// <summary>
    /// Null when <paramref name="reference"/> resolves and today's code value equals <paramref name="expected"/>
    /// exactly; otherwise a plain-English reason. Never throws for a bad reference: the reason is the report.
    /// </summary>
    internal static string? Mismatch(string reference, JsonElement expected)
    {
        try
        {
            if (reference.StartsWith("source:", StringComparison.Ordinal)) return SourceMismatch(reference, expected);
            object? actual = reference switch
            {
                _ when reference.StartsWith("member:", StringComparison.Ordinal) => ResolveMember(reference["member:".Length..], staticRoot: true),
                _ when reference.StartsWith("instance:", StringComparison.Ordinal) => ResolveMember(reference["instance:".Length..], staticRoot: false),
                _ when reference.StartsWith("param:", StringComparison.Ordinal) => ResolveParameterDefault(reference["param:".Length..]),
                _ => throw new InvalidDataException("unknown reference form (expected member:, instance:, param: or source:)"),
            };
            return ValueMismatch(actual, expected);
        }
        catch (Exception e) when (e is InvalidDataException or TargetInvocationException or MissingMemberException
                                      or InvalidOperationException or FileNotFoundException or FileLoadException
                                      or ArgumentException or FormatException)
        {
            return e.Message;
        }
    }

    // ------------------------------------------------------------------------------------------ reflection forms

    /// <summary>Resolve <c>&lt;Assembly&gt;:&lt;Type.Member...&gt;</c> (member: and instance: forms).</summary>
    private static object? ResolveMember(string body, bool staticRoot)
    {
        (Assembly asm, string path) = SplitAssembly(body);
        string[] segments = path.Split('.');
        // The longest dotted prefix that names a type in the assembly is the type; the rest is the member path.
        for (int cut = segments.Length - 1; cut >= 1; cut--)
        {
            Type? type = asm.GetType(string.Join('.', segments[..cut]), throwOnError: false);
            if (type is null) continue;
            string[] members = segments[cut..];
            object? current = staticRoot ? null : Construct(type);
            for (int i = 0; i < members.Length; i++)
            {
                bool isStatic = staticRoot && i == 0;
                Type owner = isStatic ? type : current?.GetType()
                    ?? throw new InvalidDataException($"'{path}': member '{members[i]}' was reached through a null value");
                current = ReadMember(owner, isStatic ? null : current, members[i], isStatic, path);
            }
            return current;
        }
        throw new InvalidDataException($"'{path}': no prefix of it names a type in {asm.GetName().Name}");
    }

    private static (Assembly, string) SplitAssembly(string body)
    {
        int colon = body.IndexOf(':', StringComparison.Ordinal);
        if (colon <= 0) throw new InvalidDataException($"'{body}': expected <Assembly>:<path>");
        string asmName = body[..colon];
        // N345: the CLI is read from its built binary (see the class remarks), not from the test project's references.
        if (asmName == CliAssemblyLabel) return (BuiltCli.Value, body[(colon + 1)..]);
        if (!ReflectableAssemblies.Contains(asmName, StringComparer.Ordinal))
            throw new InvalidDataException($"assembly '{asmName}' is not one the tests reference ({string.Join(", ", ReflectableAssemblies)})");
        return (Assembly.Load(new AssemblyName(asmName)), body[(colon + 1)..]);
    }

    private static object Construct(Type type)
    {
        if (type.IsAbstract || type.GetConstructor(AnyInstance, Type.EmptyTypes) is null)
            throw new InvalidDataException($"'{type.FullName}' has no parameterless constructor, so the instance: form cannot build it");
        return Activator.CreateInstance(type, nonPublic: true)
            ?? throw new InvalidDataException($"'{type.FullName}' could not be constructed");
    }

    /// <summary>Read one member segment, honouring an optional <c>[Prop=Value]</c> list selector.</summary>
    private static object? ReadMember(Type owner, object? target, string segment, bool isStatic, string path)
    {
        string name = segment;
        string? selectProp = null, selectValue = null;
        int open = segment.IndexOf('[', StringComparison.Ordinal);
        if (open >= 0)
        {
            if (!segment.EndsWith(']')) throw new InvalidDataException($"'{path}': malformed selector in '{segment}'");
            name = segment[..open];
            string[] kv = segment[(open + 1)..^1].Split('=');
            if (kv.Length != 2) throw new InvalidDataException($"'{path}': a selector must read [Prop=Value]");
            (selectProp, selectValue) = (kv[0], kv[1]);
        }

        BindingFlags flags = isStatic ? AnyStatic : AnyInstance;
        object? value;
        FieldInfo? f = owner.GetField(name, flags);
        if (f is not null) value = f.IsLiteral ? f.GetRawConstantValue() : f.GetValue(target);
        else
        {
            PropertyInfo? p = owner.GetProperty(name, flags);
            if (p is null || p.GetIndexParameters().Length != 0)
                throw new InvalidDataException($"'{path}': {owner.FullName} has no {(isStatic ? "static" : "instance")} field or property '{name}'");
            value = p.GetValue(target);
        }
        if (selectProp is null) return value;

        if (value is not IEnumerable items || value is string)
            throw new InvalidDataException($"'{path}': '{name}' is not a list, so [{selectProp}={selectValue}] cannot select from it");
        var matches = new List<object>();
        foreach (object? item in items)
        {
            if (item is null) continue;
            object? key = ReadMember(item.GetType(), item, selectProp, isStatic: false, path);
            if (Convert.ToString(key, CultureInfo.InvariantCulture) == selectValue) matches.Add(item);
        }
        if (matches.Count != 1)
            throw new InvalidDataException($"'{path}': [{selectProp}={selectValue}] matched {matches.Count} elements, not exactly one");
        return matches[0];
    }

    /// <summary>Resolve <c>&lt;Assembly&gt;:&lt;Type&gt;:&lt;Method or .ctor&gt;:&lt;parameter&gt;</c> to the parameter's default.</summary>
    private static object? ResolveParameterDefault(string body)
    {
        (Assembly asm, string rest) = SplitAssembly(body);
        string[] parts = rest.Split(':');
        if (parts.Length != 3) throw new InvalidDataException($"'{rest}': expected <Type>:<Method or .ctor>:<parameter>");
        (string typeName, string method, string param) = (parts[0], parts[1], parts[2]);
        Type type = asm.GetType(typeName, throwOnError: false)
            ?? throw new InvalidDataException($"'{typeName}' is not a type in {asm.GetName().Name}");
        IEnumerable<MethodBase> candidates = method == ".ctor"
            ? type.GetConstructors(AnyInstance)
            : type.GetMethods(AnyStatic | AnyInstance).Where(m => m.Name == method);
        var holders = candidates
            .Select(m => m.GetParameters().SingleOrDefault(p => p.Name == param))
            .Where(p => p is not null)
            .ToList();
        if (holders.Count != 1)
            throw new InvalidDataException($"'{typeName}.{method}': {holders.Count} overloads declare a parameter '{param}', not exactly one");
        ParameterInfo pi = holders[0]!;
        if (!pi.HasDefaultValue) throw new InvalidDataException($"'{typeName}.{method}({param})' has no default value");
        return pi.DefaultValue;
    }

    /// <summary>Exact comparison of a reflected value with the JSON value.</summary>
    private static string? ValueMismatch(object? actual, JsonElement expected)
    {
        switch (expected.ValueKind)
        {
            case JsonValueKind.Number:
                double want = expected.GetDouble();
                double? got = actual switch
                {
                    double d => d,
                    float f => f,
                    int i => i,
                    long l => l,
                    short s => s,
                    byte b => b,
                    _ => null,
                };
                if (got is null) return $"expected the number {Show(want)}, but the code value is {Describe(actual)}";
                return want.Equals(got.Value) ? null : $"expected {Show(want)} but the code holds {Show(got.Value)}";
            case JsonValueKind.String when expected.GetString() is "+Infinity" or "-Infinity":
                double inf = expected.GetString() == "+Infinity" ? double.PositiveInfinity : double.NegativeInfinity;
                return actual is double di && di.Equals(inf) ? null : $"expected {expected.GetString()} but the code value is {Describe(actual)}";
            case JsonValueKind.String:
                return actual is string str && string.Equals(str, expected.GetString(), StringComparison.Ordinal)
                    ? null : $"expected \"{expected.GetString()}\" but the code value is {Describe(actual)}";
            case JsonValueKind.True:
            case JsonValueKind.False:
                return actual is bool bv && bv == (expected.ValueKind == JsonValueKind.True)
                    ? null : $"expected {expected} but the code value is {Describe(actual)}";
            default:
                return $"a code default value must be a number, string or boolean, not {expected.ValueKind}";
        }
    }

    // ------------------------------------------------------------------------------------------ source form

    private static string? SourceMismatch(string reference, JsonElement expected)
    {
        string body = reference["source:".Length..];
        int colon = body.IndexOf(':', StringComparison.Ordinal);
        if (colon <= 0) return "a source: reference must read source:<repo-relative path>:<snippet>";
        string rel = body[..colon];
        string snippet = body[(colon + 1)..];
        if (snippet.Length == 0) return "empty snippet";
        if (Path.IsPathRooted(rel) || rel.Contains("..", StringComparison.Ordinal) || rel.Contains('\\', StringComparison.Ordinal))
            return $"'{rel}' must be a repo-relative path with forward slashes";

        string path = Path.Combine(new[] { KindFileSet.RepoRoot() }.Concat(rel.Split('/')).ToArray());
        if (!File.Exists(path)) return $"source file not found: {rel}";
        string text = File.ReadAllText(path).Replace("\r\n", "\n", StringComparison.Ordinal);

        int count = CountOccurrences(text, snippet);
        if (count != 1) return $"the snippet occurs {count} times in {rel}, not exactly once: \"{snippet}\"";

        switch (expected.ValueKind)
        {
            case JsonValueKind.Number:
            {
                MatchCollection lits = NumericLiteral.Matches(snippet);
                if (lits.Count == 0) return $"the snippet holds no numeric literal: \"{snippet}\"";
                string lit = lits[^1].Value;
                double parsed = double.Parse(lit.TrimEnd('d', 'D', 'f', 'F', 'm', 'M').Replace("_", "", StringComparison.Ordinal),
                    NumberStyles.Float, CultureInfo.InvariantCulture);
                double want = expected.GetDouble();
                return want.Equals(parsed) ? null : $"the snippet's last literal {lit} is {Show(parsed)}, not {Show(want)}";
            }
            case JsonValueKind.String when expected.GetString() is "+Infinity" or "-Infinity":
            {
                string tail = expected.GetString() == "+Infinity" ? "double.PositiveInfinity" : "double.NegativeInfinity";
                return snippet.EndsWith(tail, StringComparison.Ordinal) ? null : $"an infinite value needs the snippet to end in {tail}";
            }
            case JsonValueKind.String:
                return snippet.Contains("\"" + expected.GetString() + "\"", StringComparison.Ordinal)
                    ? null : $"the snippet does not contain \"{expected.GetString()}\" as a string literal";
            case JsonValueKind.True:
            case JsonValueKind.False:
            {
                MatchCollection bools = BoolLiteral.Matches(snippet);
                string want = expected.ValueKind == JsonValueKind.True ? "true" : "false";
                return bools.Count > 0 && bools[^1].Value == want ? null : $"the snippet's last boolean literal is not {want}";
            }
            default:
                return $"a code default value must be a number, string or boolean, not {expected.ValueKind}";
        }
    }

    /// <summary>Overlapping occurrences, so "aa" in "aaa" counts twice: the strictest reading of "exactly once".</summary>
    private static int CountOccurrences(string text, string snippet)
    {
        int n = 0;
        for (int i = text.IndexOf(snippet, StringComparison.Ordinal); i >= 0; i = text.IndexOf(snippet, i + 1, StringComparison.Ordinal)) n++;
        return n;
    }

    private static string Show(double d) => d.ToString("R", CultureInfo.InvariantCulture);

    private static string Describe(object? o) => o switch
    {
        null => "null",
        double d => Show(d),
        string s => "\"" + s + "\"",
        _ => $"{Convert.ToString(o, CultureInfo.InvariantCulture)} ({o.GetType().Name})",
    };
}
