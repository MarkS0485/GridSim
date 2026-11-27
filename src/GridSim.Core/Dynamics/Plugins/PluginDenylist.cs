using System.Collections.Immutable;

namespace GridSim.Core.Dynamics.Plugins;

/// <summary>
/// The references a model plug-in may not make (the determinism lint of contract version 1). Each entry is one of:
/// a namespace prefix ending in <c>.</c> (every type in it and below), a type's full name (every member of it, and every
/// type nested in it), or <c>Type::Member</c> (that member only). Besides the lists: GridSim.Core may be used only in the
/// namespaces Dynamics.Models, Dynamics.Plugins and Numerics; <c>GetHashCode</c> only on the hash-stable primitives; and of
/// <c>System.Math</c> and <c>System.Double</c> only the members the allow lists name. The lists are private, immutable and
/// ordinal-sorted, searched in a fixed order; a test checks the order. SEC-28: a framework type must also lie in a namespace
/// of <see cref="FrameworkNamespaces"/> (or be an attribute of <see cref="AttributeOnlyNamespaces"/>), and a referenced assembly
/// must be on <see cref="AssembliesAllowed"/>, so a route the deny lists do not name is refused by default. This is a lint
/// against accidents, not a security boundary: .NET has no in-process sandbox, and a plug-in is trusted code.
/// </summary>
public static class PluginDenylist
{
    /// <summary>Namespace prefixes (each ends in a dot).</summary>
    private static readonly ImmutableArray<string> Namespaces =
    [
        "System.Collections.Concurrent.",
        "System.Collections.Frozen.",
        "System.Collections.Specialized.",
        "System.IO.",
        "System.Linq.",
        "System.Net.",
        "System.Numerics.",
        "System.Reflection.",
        "System.Runtime.InteropServices.",
        "System.Runtime.Intrinsics.",
        "System.Runtime.Loader.",
        "System.Runtime.Serialization.",
        "System.Security.Cryptography.",
        "System.Text.Json.",
        "System.Threading.",
        "System.Timers.",
        "System.Xml.",
    ];

    /// <summary>Whole types (and every type nested in one).</summary>
    private static readonly ImmutableArray<string> Types =
    [
        "GridSim.Core.Dynamics.Plugins.PluginConfig",
        "GridSim.Core.Dynamics.Plugins.PluginDenylist",
        "GridSim.Core.Dynamics.Plugins.PluginLoader",
        "GridSim.Core.Dynamics.Plugins.PluginReplay",
        "GridSim.Core.Dynamics.Plugins.PluginSet",
        "GridSim.Core.Dynamics.Plugins.PluginVerifier",
        "GridSim.Core.Numerics.SimdCapabilities",
        "GridSim.Core.Numerics.SimdTier",
        "System.Activator",
        "System.AppContext",
        "System.AppDomain",
        "System.Collections.Generic.Dictionary`2+Enumerator",
        "System.Collections.Generic.Dictionary`2+KeyCollection",
        "System.Collections.Generic.Dictionary`2+ValueCollection",
        "System.Collections.Generic.HashSet`1+Enumerator",
        "System.Collections.Generic.ICollection`1",
        "System.Collections.Generic.IDictionary`2",
        "System.Collections.Generic.IEnumerable`1",
        "System.Collections.Generic.IReadOnlyDictionary`2",
        "System.Collections.Generic.IReadOnlySet`1",
        "System.Collections.Generic.ISet`1",
        "System.Collections.Hashtable",
        "System.Collections.ICollection",
        "System.Collections.IDictionary",
        "System.Collections.IEnumerable",
        "System.Collections.Immutable.ImmutableDictionary",
        "System.Collections.Immutable.ImmutableDictionary`2",
        "System.Collections.Immutable.ImmutableHashSet",
        "System.Collections.Immutable.ImmutableHashSet`1",
        "System.Console",
        "System.Convert",
        "System.DateTime",
        "System.DateTimeOffset",
        "System.Decimal",
        "System.Diagnostics.Debugger",
        "System.Diagnostics.Process",
        "System.Diagnostics.Stopwatch",
        "System.Environment",
        "System.GC",
        "System.Half",
        "System.HashCode",
        "System.MathF",
        "System.OperatingSystem",
        "System.Random",
        "System.Runtime.CompilerServices.AsyncTaskMethodBuilder",
        "System.Runtime.CompilerServices.AsyncTaskMethodBuilder`1",
        "System.Runtime.CompilerServices.AsyncValueTaskMethodBuilder",
        "System.Runtime.CompilerServices.AsyncValueTaskMethodBuilder`1",
        "System.Runtime.CompilerServices.AsyncVoidMethodBuilder",
        "System.Runtime.CompilerServices.ConditionalWeakTable`2",
        "System.Runtime.CompilerServices.IAsyncStateMachine",
        "System.Runtime.CompilerServices.IgnoresAccessChecksToAttribute",
        "System.Runtime.CompilerServices.RuntimeFeature",
        "System.Runtime.CompilerServices.TaskAwaiter",
        "System.Runtime.CompilerServices.TaskAwaiter`1",
        "System.Runtime.CompilerServices.Unsafe",
        "System.Runtime.CompilerServices.UnsafeAccessorAttribute",
        "System.Runtime.CompilerServices.UnsafeAccessorTypeAttribute",
        "System.Runtime.GCSettings",
        "System.Runtime.JitInfo",
        "System.Security.UnverifiableCodeAttribute",
        "System.Single",
        "System.TimeProvider",
        "System.TimeZone",
        "System.TimeZoneInfo",
        "System.Type",
        "System.WeakReference",
        "System.WeakReference`1",
    ];

    /// <summary>Single members.</summary>
    private static readonly ImmutableArray<string> Members =
    [
        "System.Byte::Parse",
        "System.Byte::TryParse",
        "System.Collections.Generic.Dictionary`2::GetEnumerator",
        "System.Collections.Generic.Dictionary`2::get_Keys",
        "System.Collections.Generic.Dictionary`2::get_Values",
        "System.Collections.Generic.HashSet`1::CopyTo",
        "System.Collections.Generic.HashSet`1::GetEnumerator",
        "System.Delegate::DynamicInvoke",
        "System.Globalization.CultureInfo::get_CurrentCulture",
        "System.Globalization.CultureInfo::get_CurrentUICulture",
        "System.Globalization.CultureInfo::get_DefaultThreadCurrentCulture",
        "System.Globalization.CultureInfo::get_DefaultThreadCurrentUICulture",
        "System.Globalization.CultureInfo::get_InstalledUICulture",
        "System.Globalization.CultureInfo::set_CurrentCulture",
        "System.Globalization.CultureInfo::set_CurrentUICulture",
        "System.Globalization.CultureInfo::set_DefaultThreadCurrentCulture",
        "System.Globalization.CultureInfo::set_DefaultThreadCurrentUICulture",
        "System.Globalization.DateTimeFormatInfo::get_CurrentInfo",
        "System.Globalization.NumberFormatInfo::get_CurrentInfo",
        "System.Globalization.RegionInfo::get_CurrentRegion",
        "System.Guid::NewGuid",
        "System.Int16::Parse",
        "System.Int16::TryParse",
        "System.Int32::Parse",
        "System.Int32::TryParse",
        "System.Int64::Parse",
        "System.Int64::TryParse",
        "System.Object::GetHashCode",
        "System.Runtime.CompilerServices.RuntimeHelpers::GetHashCode",
        "System.SByte::Parse",
        "System.SByte::TryParse",
        "System.UInt16::Parse",
        "System.UInt16::TryParse",
        "System.UInt32::Parse",
        "System.UInt32::TryParse",
        "System.UInt64::Parse",
        "System.UInt64::TryParse",
    ];

    /// <summary>The <c>System.Math</c> members a plug-in may call; every other member of <c>System.Math</c> is refused
    /// (transcendental functions go through <see cref="GridSim.Core.Numerics.DetMath"/>).</summary>
    private static readonly ImmutableArray<string> MathAllowed = ["Abs", "Ceiling", "Clamp", "CopySign", "Floor", "Max", "Min", "Round", "Sign", "Sqrt"];

    /// <summary>The <c>System.Double</c> members a plug-in may call (comparison, classification and the exact operations);
    /// every other member is refused: the transcendental and fused functions (<c>double.Sin</c>, <c>double.Exp</c>,
    /// <c>double.FusedMultiplyAdd</c> and the like), the estimates, and parsing and formatting.</summary>
    private static readonly ImmutableArray<string> DoubleAllowed =
    [
        "Abs", "BitDecrement", "BitIncrement", "Ceiling", "Clamp", "CompareTo", "CopySign", "Equals", "Floor", "GetHashCode",
        "IsFinite", "IsInfinity", "IsInteger", "IsNaN", "IsNegative", "IsNegativeInfinity", "IsNormal", "IsPositive",
        "IsPositiveInfinity", "IsSubnormal", "Max", "Min", "Round", "Sign", "Sqrt", "Truncate", "op_Equality", "op_GreaterThan",
        "op_GreaterThanOrEqual", "op_Inequality", "op_LessThan", "op_LessThanOrEqual",
    ];

    /// <summary>The types whose <c>GetHashCode</c> is a pure function of the value (the same bits in every process); a
    /// <c>GetHashCode</c> of any other type is refused (string, comparer, tuple and object hash codes are randomised per process
    /// or follow the object's identity).</summary>
    private static readonly ImmutableArray<string> HashStable =
    [
        "System.Boolean", "System.Byte", "System.Char", "System.Double", "System.Enum", "System.Int16", "System.Int32", "System.Int64",
        "System.SByte", "System.UInt16", "System.UInt32", "System.UInt64",
    ];

    /// <summary>SEC-28: the framework namespaces a plug-in may use, each exactly (not the namespaces below it). A framework type
    /// (its name begins <c>System.</c>, <c>Microsoft.</c>, <c>Internal.</c> or <c>Windows.</c>) in any other namespace is
    /// refused, so a file or network route in a namespace no list names (<c>System.Formats.Tar</c>, <c>System.Data</c>,
    /// <c>System.Resources</c>) is outside the contract by default. The deny lists above still apply inside these
    /// namespaces.</summary>
    private static readonly ImmutableArray<string> FrameworkNamespaces =
    [
        "System", "System.Collections.Generic", "System.Collections.Immutable", "System.Collections.ObjectModel",
        "System.Diagnostics.CodeAnalysis", "System.Globalization", "System.Runtime.CompilerServices", "System.Runtime.Versioning",
        "System.Text",
    ];

    /// <summary>SEC-28: framework namespaces of which a plug-in may use the attribute types only (a type whose name ends in
    /// <c>Attribute</c>, and the types nested in one): the compiler and the SDK write <c>DebuggableAttribute</c> and the like,
    /// and the rest of <c>System.Diagnostics</c> holds file routes (<c>TextWriterTraceListener</c>,
    /// <c>FileVersionInfo</c>).</summary>
    private static readonly ImmutableArray<string> AttributeOnlyNamespaces = ["System.ComponentModel", "System.Diagnostics"];

    /// <summary>SEC-28: the enum types those attributes take as arguments (the compiler writes
    /// <c>DebuggerBrowsable(DebuggerBrowsableState.Never)</c> on generated members), allowed by full name.</summary>
    private static readonly ImmutableArray<string> AttributeArgumentTypes =
        ["System.ComponentModel.EditorBrowsableState", "System.Diagnostics.DebuggerBrowsableState"];

    /// <summary>SEC-28: the assemblies a plug-in may reference by simple name: GridSim.Core and the framework assemblies that
    /// hold the allowed namespaces.</summary>
    private static readonly ImmutableArray<string> AssembliesAllowed =
    [
        "GridSim.Core", "System", "System.Collections", "System.Collections.Immutable", "System.Memory", "System.ObjectModel",
        "System.Private.CoreLib", "System.Runtime", "System.Runtime.Extensions", "System.Text.Encoding.Extensions", "mscorlib",
        "netstandard",
    ];

    /// <summary>The GridSim.Core namespaces a plug-in may use (each with the namespaces below it); every other GridSim.Core type
    /// is refused, and so are the loader's own types in the plug-ins namespace (the type list).</summary>
    private static readonly ImmutableArray<string> CoreNamespaces =
        ["GridSim.Core.Dynamics.Models.", "GridSim.Core.Dynamics.Plugins.", "GridSim.Core.Numerics."];

    /// <summary>The four lists and the two allow lists, by name, for the order check (copies; the lists themselves cannot be
    /// changed).</summary>
    internal static IReadOnlyList<(string Name, ImmutableArray<string> List)> Lists =>
        [("Namespaces", Namespaces), ("Types", Types), ("Members", Members), ("MathAllowed", MathAllowed), ("DoubleAllowed", DoubleAllowed),
         ("HashStable", HashStable), ("CoreNamespaces", CoreNamespaces), ("FrameworkNamespaces", FrameworkNamespaces),
         ("AttributeOnlyNamespaces", AttributeOnlyNamespaces), ("AttributeArgumentTypes", AttributeArgumentTypes),
         ("AssembliesAllowed", AssembliesAllowed)];

    /// <summary>True when <paramref name="type"/> (a full name) is a GridSim.Core type in a namespace open to plug-ins.</summary>
    public static bool IsAllowedCoreType(string type)
    {
        ArgumentNullException.ThrowIfNull(type);
        foreach (string ns in CoreNamespaces)
            if (type.StartsWith(ns, StringComparison.Ordinal)) return true;
        return false;
    }

    /// <summary>The assemblies a plug-in may reference (by simple name): GridSim.Core and the framework assemblies that hold the
    /// allowed namespaces (SEC-28: an allow list; it was every <c>System.*</c> assembly but a few). Every other reference is
    /// refused, so a plug-in has no dependencies of its own.</summary>
    public static bool AllowedAssemblyReference(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return Listed(AssembliesAllowed, name);
    }

    /// <summary>SEC-28: null when <paramref name="type"/> (a full name) is not a framework type or lies in a namespace a plug-in
    /// may use, else the reason it is refused.</summary>
    private static string? FrameworkRefusal(string type)
    {
        if (!(type.StartsWith("System.", StringComparison.Ordinal) || type.StartsWith("Microsoft.", StringComparison.Ordinal)
              || type.StartsWith("Internal.", StringComparison.Ordinal) || type.StartsWith("Windows.", StringComparison.Ordinal)))
            return null;
        int plus = type.IndexOf('+', StringComparison.Ordinal);
        string outer = plus < 0 ? type : type[..plus];
        int bracket = outer.IndexOfAny(['[', '<']);
        if (bracket > 0) outer = outer[..bracket];
        int dot = outer.LastIndexOf('.');
        string ns = dot < 0 ? "" : outer[..dot];
        if (Listed(FrameworkNamespaces, ns)) return null;
        if (Listed(AttributeOnlyNamespaces, ns) && (outer.EndsWith("Attribute", StringComparison.Ordinal) || Listed(AttributeArgumentTypes, outer))) return null;
        return $"the namespace {ns} is outside the plug-in contract (a plug-in uses only {string.Join(", ", FrameworkNamespaces)}, and the attributes of {string.Join(" and ", AttributeOnlyNamespaces)})";
    }

    private static bool Listed(ImmutableArray<string> list, string value) => list.BinarySearch(value, StringComparer.Ordinal) >= 0;

    /// <summary>The reason a reference to <paramref name="member"/> of <paramref name="type"/> (a full type name, nested types
    /// joined with <c>+</c>; member may be null for a type reference) is refused, or null when it is allowed.</summary>
    public static string? Refusal(string type, string? member)
    {
        ArgumentNullException.ThrowIfNull(type);
        // The modifier the compiler writes on every 'in' parameter (modreq InAttribute): a signature marker, not an API.
        if (type == "System.Runtime.InteropServices.InAttribute") return null;
        if (type.StartsWith("GridSim.Core.", StringComparison.Ordinal) && !IsAllowedCoreType(type))
            return $"{type} is outside the plug-in contract (a plug-in uses GridSim.Core only in GridSim.Core.Dynamics.Models, GridSim.Core.Dynamics.Plugins and GridSim.Core.Numerics)";
        // The assembly-level attributes the compiler and the SDK write (AssemblyTitleAttribute and the like) are data, not
        // reflection: an attribute type directly in System.Reflection is allowed.
        bool reflectionAttribute = type.StartsWith("System.Reflection.", StringComparison.Ordinal) && type.EndsWith("Attribute", StringComparison.Ordinal)
            && type.IndexOf('.', "System.Reflection.".Length) < 0 && type.IndexOf('+', StringComparison.Ordinal) < 0;
        if (!reflectionAttribute)
            foreach (string ns in Namespaces)
                if (type.StartsWith(ns, StringComparison.Ordinal)) return $"the namespace {ns.TrimEnd('.')} is outside the plug-in contract";
        if (Listed(Types, type)) return $"{type} is outside the plug-in contract";
        for (int plus = type.IndexOf('+', StringComparison.Ordinal); plus > 0; plus = type.IndexOf('+', plus + 1))
            if (Listed(Types, type[..plus])) return $"{type} is outside the plug-in contract";
        // SEC-28: then the allow list of framework namespaces (the reflection attributes above stay allowed).
        if (!reflectionAttribute && FrameworkRefusal(type) is { } outside) return outside;
        if (member is not null)
        {
            if (Listed(Members, type + "::" + member)) return $"{type}.{member} is outside the plug-in contract";
            if (member == "GetHashCode" && !Listed(HashStable, type))
                return $"{type}.GetHashCode is randomised per process or follows object identity; a plug-in uses no such hash code";
            if (type == "System.Math" && !Listed(MathAllowed, member))
                return $"System.Math.{member} is not deterministic across platforms or not allowed; use DetMath";
            if (type == "System.Double" && !Listed(DoubleAllowed, member))
                return $"System.Double.{member} is not deterministic across platforms or not allowed; use DetMath";
        }
        return null;
    }
}
