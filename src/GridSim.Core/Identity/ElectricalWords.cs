namespace GridSim.Core.Identity;

// =====================================================================================================================
// The electrical word list (2026-10-24): 256 words, one per byte value, for an open, what3words-style electrical
// reference that a person can read aloud, write down or type, and that a machine turns back into the same number.
//
// THE CONTRACT. A word's position in Words IS its byte value: Words[0] is byte 0x00, Words[255] is byte 0xFF. The order
// is fixed for good: a reference written on a drawing, a label or a log today must decode to the same number in every
// later version. Changing, inserting, removing or reordering a word silently re-numbers every reference ever made, so
// the test suite pins the SHA-256 of the joined list (any change to it has to be deliberate and seen).
//
// THE SIZE. Each word carries exactly one byte, so n words carry 8n bits:
//     2 words = 65,536 refs;  3 words = 16,777,216 refs;  4 words = 4,294,967,296 refs.
//
// THE RULES every word obeys (pinned by the tests):
//   - plain ASCII lower case, letters only, 3 to 10 letters long;
//   - all 256 different;
//   - no word is the start of another (a word clipped short on a label or a bad line must never read as a different
//     valid word);
//   - drawn from electrical units, pioneers, materials, physics, signals, quantities, devices, Greek letters,
//     mathematics and electrical heritage, so the list reads as the electrical world's own.
//
// THE EXCLUSIONS, and why. These references are read aloud to and from control rooms, where certain words are
// instructions, states or names in their own right. A reference must never sound like one of them, so the list has:
//   - no switching or safety words (open, close, trip, earth, isolate, live, dead, safe, permit, energise, switch,
//     lock, prove, ...): a reference must never be mistaken for an instruction;
//   - no phase names or colours (phase, star, delta, neutral, red, yellow, blue, brown, black, grey, green, ...): they
//     name conductors;
//   - no model asset types (transformer, cable, busbar, breaker, generator, line, feeder, reactor, relay, fuse,
//     battery, load, substation, tower, pylon, meter, ...): a reference names an asset, it must not claim to be one;
//   - no fuels (wind, solar, hydro, nuclear, gas, coal, biomass, oil, ...): they classify plant;
//   - no alarm states (alarm, warning, fault, critical, normal, alert): they are states, not names;
//   - no words with an everyday electrical meaning that would mislead (current, charge, real, argument, factor, gear,
//     governor) and no confusable pairs (lead / led).
// =====================================================================================================================

/// <summary>
/// The 256 electrical words of the electrical reference (<see cref="ElectricalRef"/>), one per byte value, in their
/// fixed contract order (file header), and the lookup from a word back to its byte.
/// </summary>
public static class ElectricalWords
{
    /// <summary>The number of words: one per byte value.</summary>
    public const int Count = 256;

    /// <summary>The shortest word, in letters.</summary>
    public const int MinLength = 3;

    /// <summary>The longest word, in letters.</summary>
    public const int MaxLength = 10;

    // The list itself, index = byte value. Never edit: see the file header (the tests pin its SHA-256).
    private static readonly string[] List =
    {
        /*   0 */ "ohm", "watt", "volt", "joule", "tesla", "weber", "henry", "farad",
        /*   8 */ "coulomb", "siemens", "hertz", "ampere", "kelvin", "lumen", "newton", "pascal",
        /*  16 */ "gauss", "maxwell", "oersted", "erg", "mho", "decibel", "radian", "candela",
        /*  24 */ "lux", "lovelace", "edison", "galvani", "franklin", "kirchhoff", "thevenin", "norton",
        /*  32 */ "steinmetz", "fleming", "lenz", "heaviside", "wheatstone", "ferranti", "parsons", "swan",
        /*  40 */ "ayrton", "fortescue", "clarke", "blondel", "kron", "raphson", "seidel", "jacobi",
        /*  48 */ "laplace", "fourier", "nyquist", "bode", "hopkinson", "tellegen", "thomson", "armstrong",
        /*  56 */ "marconi", "bell", "morse", "baird", "copper", "aluminium", "porcelain", "mica",
        /*  64 */ "silicon", "ferrite", "graphite", "carbon", "glass", "resin", "quartz", "ceramic",
        /*  72 */ "bronze", "steel", "iron", "zinc", "nickel", "cobalt", "tungsten", "silver",
        /*  80 */ "gold", "platinum", "tin", "rubber", "enamel", "varnish", "lacquer", "bakelite",
        /*  88 */ "paper", "cotton", "slate", "cork", "wax", "brass", "chrome", "titanium",
        /*  96 */ "lithium", "sodium", "argon", "neon", "xenon", "helium", "hydrogen", "oxygen",
        /* 104 */ "flux", "field", "phasor", "vector", "harmonic", "ripple", "corona", "plasma",
        /* 112 */ "ion", "electron", "proton", "photon", "quantum", "magnet", "dipole", "eddy",
        /* 120 */ "hysteresis", "resonance", "inertia", "torque", "momentum", "rotor", "armature", "commutator",
        /* 128 */ "dynamo", "coil", "winding", "toroid", "solenoid", "diode", "triode", "valve",
        /* 136 */ "cathode", "anode", "electrode", "filament", "crystal", "lattice", "wafer", "spark",
        /* 144 */ "impulse", "kinetic", "potential", "orbit", "nucleus", "atom", "particle", "sine",
        /* 152 */ "cosine", "pulse", "crest", "trough", "cycle", "period", "peak", "ramp",
        /* 160 */ "carrier", "echo", "beat", "chirp", "tone", "octave", "sweep", "damping",
        /* 168 */ "decay", "jitter", "noise", "sample", "aliasing", "bandwidth", "spectrum", "envelope",
        /* 176 */ "cadence", "tempo", "rhythm", "impedance", "reactance", "admittance", "resistance", "inductance",
        /* 184 */ "insulation", "dielectric", "quadrature", "skin", "ampacity", "coercive", "remanence", "thyristor",
        /* 192 */ "rectifier", "transistor", "klystron", "cyclotron", "vacuum", "halogen", "laser", "radar",
        /* 200 */ "radio", "rheostat", "solder", "lamina", "shellac", "antenna", "aerial", "chopper",
        /* 208 */ "alpha", "beta", "gamma", "epsilon", "zeta", "theta", "kappa", "lambda",
        /* 216 */ "sigma", "omega", "upsilon", "omicron", "matrix", "tensor", "scalar", "integral",
        /* 224 */ "gradient", "divergence", "curl", "laplacian", "eigen", "sparse", "pivot", "norm",
        /* 232 */ "modulus", "complex", "imaginary", "conjugate", "polar", "cartesian", "telegraph", "lamp",
        /* 240 */ "beacon", "lantern", "spindle", "loom", "forge", "anvil", "lathe", "piston",
        /* 248 */ "crank", "flywheel", "dial", "gauge", "needle", "compass", "lodestone", "leyden",
    };

    // word -> byte, built once from List (ordinal: the words are ASCII lower case and compared exactly).
    private static readonly Dictionary<string, byte> Index = BuildIndex();

    /// <summary>The words in contract order: <c>Words[b]</c> is the word for byte <c>b</c>.</summary>
    public static IReadOnlyList<string> Words => List;

    /// <summary>The word for byte <paramref name="value"/>.</summary>
    public static string WordOf(byte value) => List[value];

    /// <summary>
    /// The byte of <paramref name="word"/>, or false when it is not one of the 256 words. Exact and case-sensitive:
    /// only the lower-case word is a word (the decoder says so when it refuses another case).
    /// </summary>
    public static bool TryByteOf(string word, out byte value)
    {
        ArgumentNullException.ThrowIfNull(word);
        return Index.TryGetValue(word, out value);
    }

    private static Dictionary<string, byte> BuildIndex()
    {
        // Fail loud at type load if the list ever breaks its own contract (count or duplicates): a broken list must
        // never encode or decode anything.
        if (List.Length != Count)
            throw new InvalidOperationException($"The electrical word list has {List.Length} words, not {Count}.");
        var index = new Dictionary<string, byte>(Count, StringComparer.Ordinal);
        for (int i = 0; i < List.Length; i++)
            if (!index.TryAdd(List[i], (byte)i))
                throw new InvalidOperationException($"The electrical word list has '{List[i]}' twice (at {index[List[i]]} and {i}).");
        return index;
    }
}
