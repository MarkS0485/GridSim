using System.Globalization;

namespace GridSim.Scripting;

/// <summary>The unit types of grammar v1. A unit is a type: a quantity of the wrong unit is refused at validation.</summary>
public enum Unit { Mw, Mvar, Mva, Pu, Kv, Hz, Ms, S, Deg }

public static class Units
{
    /// <summary>The keyword for a unit, upper case.</summary>
    public static string Keyword(Unit u) => u switch
    {
        Unit.Mw => "MW", Unit.Mvar => "MVAR", Unit.Mva => "MVA", Unit.Pu => "PU", Unit.Kv => "KV",
        Unit.Hz => "HZ", Unit.Ms => "MS", Unit.S => "S", Unit.Deg => "DEG",
        _ => throw new ArgumentOutOfRangeException(nameof(u)),
    };

    public static bool TryParse(string word, out Unit unit)
    {
        foreach (Unit u in All)
            if (string.Equals(word, Keyword(u), StringComparison.OrdinalIgnoreCase)) { unit = u; return true; }
        unit = default;
        return false;
    }

    public static IReadOnlyList<Unit> All { get; } =
        [Unit.Mw, Unit.Mvar, Unit.Mva, Unit.Pu, Unit.Kv, Unit.Hz, Unit.Ms, Unit.S, Unit.Deg];

    /// <summary>True for the two time units, which convert into each other.</summary>
    public static bool IsTime(Unit u) => u is Unit.Ms or Unit.S;

    /// <summary>Two units can be added or compared: the same unit, or both times.</summary>
    public static bool Compatible(Unit a, Unit b) => a == b || (IsTime(a) && IsTime(b));

    /// <summary>A value in unit <paramref name="to"/>, given in <paramref name="from"/> (only MS and S convert).</summary>
    public static decimal Convert(decimal value, Unit from, Unit to) =>
        from == to ? value : from == Unit.S ? value * 1000m : value / 1000m;

    public static string List(IEnumerable<Unit> units) => string.Join(" or ", units.Select(Keyword));
}

/// <summary>A number with its unit. The value is a decimal, so a time written as 20 MS is exactly 20000 microseconds.</summary>
public readonly record struct Quantity(decimal Value, Unit Unit)
{
    /// <summary>Canonical text: the number without trailing zeros, a space, the unit keyword.</summary>
    public override string ToString() => Canonical(Value) + " " + Units.Keyword(Unit);

    public static string Canonical(decimal v) => v.ToString("0.############################", CultureInfo.InvariantCulture);

    /// <summary>The time in whole microseconds (zero allowed). Only MS and S are times; a finer value than 1 microsecond is refused.</summary>
    public bool TryToMicroseconds(out long micros)
    {
        micros = 0;
        decimal scale = Unit switch { Unit.Ms => 1000m, Unit.S => 1_000_000m, _ => 0m };
        if (scale == 0m) return false;
        decimal us = Value * scale;
        if (us != decimal.Truncate(us) || us < 0m || us > 4_000_000_000_000m) return false;
        micros = (long)us;
        return true;
    }
}
