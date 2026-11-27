using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using System.Linq.Expressions;
using System.Runtime;
using GridSim.Core.Dynamics.Plugins;

[assembly: GridSimModelPlugin(1)]

namespace GridSim.ModelPlugin.HazardSample;

/// <summary>HAZARDS: a governor whose class holds one determinism hazard per method (each named after it), so the lint's
/// refusal list shows each hazard caught in compiled IL, named with the method that holds it.</summary>
public sealed class HazardsFactory : IUserModelFactory
{
    public string Id => "HAZARDS";

    public UserModelRole Role => UserModelRole.Governor;

    public IReadOnlyList<PluginParameter> Parameters { get; } = Array.Empty<PluginParameter>();

    public object Create(IReadOnlyList<double> parameters) => new HazardsGovernor();
}

// Each hazard sits in an instance method of the governor so the refusal names it there; most need no instance state.
#pragma warning disable CA1822
public sealed class HazardsGovernor : IUserGovernor
{
    private readonly Dictionary<int, double> _map = new() { [1] = 1.0, [2] = 2.0 };
    private readonly HashSet<int> _set = new() { 1, 2 };
    private double _pm0;

    public void Initialize(double pm0) => _pm0 = pm0;

    public double Step(double speedDev, double dt) => _pm0;

    public double DoubleSin(double x) => double.Sin(x);

    public double DoubleExp(double x) => double.Exp(x);

    public double DoubleFma(double x) => double.FusedMultiplyAdd(x, x, x);

    public double HalfPrecision(double x) => (double)(Half)x;

    public int StringComparerHash(string s) => StringComparer.Ordinal.GetHashCode(s);

    public int EqualityComparerHash(string s) => EqualityComparer<string>.Default.GetHashCode(s);

    public double DictionaryAsEnumerable()
    {
        double sum = 0.0;
        foreach (KeyValuePair<int, double> kv in (IEnumerable<KeyValuePair<int, double>>)_map) sum += kv.Value;
        return sum;
    }

    public double HashSetThroughLinq() => _set.Sum(x => (double)x);

    public KeyValuePair<int, double>[] DictionaryToArray() => _map.ToArray();

    public bool ImmutableHashSetRead(int x) => ImmutableHashSet.Create(1, 2).Contains(x);

    public bool FrozenSetRead(int x) => _set.ToFrozenSet().Contains(x);

    public bool OperatingSystemRead() => OperatingSystem.IsWindows();

    public string CurrentCultureRead() => CultureInfo.CurrentCulture.Name;

    public double TimeZoneRead() => TimeZoneInfo.Local.BaseUtcOffset.TotalHours;

    public long JitInfoRead() => JitInfo.GetCompiledILBytes();

    public object? TypeGetTypeRead() => Type.GetType("System.String")?.GetProperty("Length")?.GetValue("abc");

    public double ExpressionCompile() => Expression.Lambda<Func<double>>(Expression.Constant(1.0)).Compile()();
}
#pragma warning restore CA1822
