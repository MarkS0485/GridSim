using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Xunit;

namespace GridSim.Tests.JsonSourceGen;

/// <summary>
/// Phase 0.1 test helper: a structural, member-by-member comparison of two object graphs, used to prove that what the
/// source-generated JSON path loads is the same as what the old reflection path loaded.
/// <list type="bullet">
/// <item>doubles and floats are compared by their bits (so -0.0 differs from 0.0 and every NaN payload counts);</item>
/// <item>strings, enums, other primitives, dates, Guids and Types by Equals;</item>
/// <item>a JsonElement by its raw text;</item>
/// <item>any other collection element by element, in order (a dictionary as its ordered key/value pairs);</item>
/// <item>any other object by its runtime type (which must match) and then every public instance property and field.</item>
/// </list>
/// A property getter that throws must throw the same exception type and message on both sides. Delegates are not
/// compared (the loaders' lazily-read members are compared through what they produce instead).
/// </summary>
internal static class JsonSourceGenDeepEquality
{
    /// <summary>Fails (with the member path) at the first difference between <paramref name="expected"/> and
    /// <paramref name="actual"/>. Returns the number of leaf values compared, so a caller can assert it compared
    /// something.</summary>
    public static long AssertDeepEqual(object? expected, object? actual, string path)
    {
        var seen = new HashSet<(object, object)>(new RefPairComparer());
        long leaves = 0;
        Walk(expected, actual, path, 0, seen, ref leaves);
        return leaves;
    }

    private static void Walk(object? a, object? b, string path, int depth, HashSet<(object, object)> seen, ref long leaves)
    {
        // A real document is far shallower than this; a deeper graph means a cycle the reference check missed.
        if (depth > 128) throw new InvalidOperationException($"{path}: object graph deeper than 128 levels.");
        if (a is null || b is null)
        {
            Assert.True(a is null && b is null, $"{path}: one side is null (expected {Describe(a)}, actual {Describe(b)}).");
            leaves++;
            return;
        }
        Type t = a.GetType();
        Assert.True(t == b.GetType(), $"{path}: runtime types differ ({t.FullName} vs {b.GetType().FullName}).");

        switch (a)
        {
            case double da:
                Assert.True(BitConverter.DoubleToInt64Bits(da) == BitConverter.DoubleToInt64Bits((double)b),
                    $"{path}: doubles differ in their bits ({da:R} vs {(double)b:R}).");
                leaves++;
                return;
            case float fa:
                Assert.True(BitConverter.SingleToInt32Bits(fa) == BitConverter.SingleToInt32Bits((float)b),
                    $"{path}: floats differ in their bits ({fa:R} vs {(float)b:R}).");
                leaves++;
                return;
            case JsonElement ja:
                Assert.True(ja.GetRawText() == ((JsonElement)b).GetRawText(), $"{path}: JSON elements differ.");
                leaves++;
                return;
            case Delegate:
                return;
        }
        if (t.IsPrimitive || t.IsEnum || a is string || a is decimal || a is DateTime || a is DateTimeOffset
            || a is TimeSpan || a is Guid || a is Type)
        {
            Assert.True(a.Equals(b), $"{path}: values differ ({Describe(a)} vs {Describe(b)}).");
            leaves++;
            return;
        }
        if (!t.IsValueType && !seen.Add((a, b))) return;   // this pair is already being (or was) compared
        if (a is IEnumerable ea)
        {
            List<object?> la = ea.Cast<object?>().ToList();
            List<object?> lb = ((IEnumerable)b).Cast<object?>().ToList();
            Assert.True(la.Count == lb.Count, $"{path}: collection lengths differ ({la.Count} vs {lb.Count}).");
            for (int i = 0; i < la.Count; i++) Walk(la[i], lb[i], $"{path}[{i}]", depth + 1, seen, ref leaves);
            return;
        }
        foreach (PropertyInfo p in t.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!p.CanRead || p.GetIndexParameters().Length > 0) continue;
            (object? va, Exception? xa) = Get(p, a);
            (object? vb, Exception? xb) = Get(p, b);
            if (xa is not null || xb is not null)
            {
                Assert.True(xa is not null && xb is not null && xa.GetType() == xb.GetType() && xa.Message == xb.Message,
                    $"{path}.{p.Name}: the getter threw on one side only, or differently ({xa?.GetType().Name}: {xa?.Message} vs {xb?.GetType().Name}: {xb?.Message}).");
                leaves++;
                continue;
            }
            Walk(va, vb, $"{path}.{p.Name}", depth + 1, seen, ref leaves);
        }
        foreach (FieldInfo f in t.GetFields(BindingFlags.Public | BindingFlags.Instance))
            Walk(f.GetValue(a), f.GetValue(b), $"{path}.{f.Name}", depth + 1, seen, ref leaves);
    }

    private static (object? Value, Exception? Error) Get(PropertyInfo p, object o)
    {
        try { return (p.GetValue(o), null); }
        catch (TargetInvocationException ex) { return (null, ex.InnerException ?? ex); }
    }

    private static string Describe(object? o) => o switch
    {
        null => "null",
        string s => $"\"{s}\"",
        _ => o.ToString() ?? o.GetType().Name,
    };

    // Pairs of references compared by identity (never by the objects' own Equals, which a record overrides).
    private sealed class RefPairComparer : IEqualityComparer<(object, object)>
    {
        public bool Equals((object, object) x, (object, object) y) =>
            ReferenceEquals(x.Item1, y.Item1) && ReferenceEquals(x.Item2, y.Item2);

        public int GetHashCode((object, object) p) =>
            HashCode.Combine(RuntimeHelpers.GetHashCode(p.Item1), RuntimeHelpers.GetHashCode(p.Item2));
    }
}
