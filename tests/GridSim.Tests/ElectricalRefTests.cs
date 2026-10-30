using System.Collections;
using System.Security.Cryptography;
using System.Text;
using GridSim.Core.Identity;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// The electrical reference (GridSim.Core.Identity, 2026-10-24): the 256-word list and its contract, encode / decode
/// and the check word, the keyed scatter and the reference sources. Every pinned value below was computed by an
/// independent implementation (Python, from the same published definitions), not read back from this code.
/// </summary>
public sealed class ElectricalRefTests
{
    // ======================================== the word list ========================================

    // The exclusion list (switching / safety, phases and colours, model asset types, fuels, alarm states, misleading
    // everyday meanings, confusables): no word of the list may be one of these.
    private static readonly string[] Excluded =
    (
        "open close closed trip earth earthed ground isolate isolated isolator live dead safe safety danger " +
        "test permit energise energised deenergise switch switched discharge prove lock locked " +
        "phase star delta wye neutral red yellow blue brown black grey gray green " +
        "transformer cable busbar bus breaker generator line feeder shunt reactor capacitor relay fuse recloser inverter " +
        "battery load demand substation tower pylon pole meter " +
        "wind solar hydro nuclear gas coal biomass tidal wave oil " +
        "alarm warning fault critical normal alert " +
        "current charge real argument factor gear governor " +
        "lead led"
    ).Split(' ', StringSplitOptions.RemoveEmptyEntries);

    [Fact]
    public void The_list_is_256_unique_lower_case_ASCII_words_of_3_to_10_letters_with_no_prefix_clashes()
    {
        IReadOnlyList<string> w = ElectricalWords.Words;
        Assert.Equal(256, w.Count);
        Assert.Equal(ElectricalWords.Count, w.Count);
        Assert.Equal(256, w.Distinct(StringComparer.Ordinal).Count());
        Assert.All(w, x =>
        {
            Assert.InRange(x.Length, ElectricalWords.MinLength, ElectricalWords.MaxLength);
            Assert.All(x, c => Assert.InRange(c, 'a', 'z'));
        });
        foreach (string a in w)
            foreach (string b in w)
                Assert.False(a != b && b.StartsWith(a, StringComparison.Ordinal), $"'{a}' is the start of '{b}'");
    }

    [Fact]
    public void No_excluded_word_is_in_the_list()
    {
        Assert.Equal(Excluded.Length, Excluded.Distinct(StringComparer.Ordinal).Count());
        foreach (string x in Excluded)
            Assert.False(ElectricalWords.TryByteOf(x, out _), $"the excluded word '{x}' is in the list");
    }

    [Fact]
    public void The_order_is_the_contract_pinned_by_its_SHA256_and_known_positions()
    {
        // Any change to the list (a word, its spelling, its position) changes this hash: it has to be deliberate.
        string joined = string.Join("\n", ElectricalWords.Words);
        Assert.Equal("6006e42b45969720484c80584b3eafbd9d4527c08f32e601c51b17c2053de349",
            Convert.ToHexStringLower(SHA256.HashData(Encoding.ASCII.GetBytes(joined))));
        Assert.Equal("lovelace", ElectricalWords.Words[25]);
        Assert.Equal("ohm", ElectricalWords.WordOf(0));
        Assert.Equal("leyden", ElectricalWords.WordOf(255));
        Assert.Equal("copper", ElectricalWords.WordOf(60));
        Assert.Equal("kron", ElectricalWords.WordOf(44));
    }

    [Fact]
    public void Every_word_maps_to_its_own_byte_and_nothing_else_is_a_word()
    {
        for (int b = 0; b < 256; b++)
        {
            Assert.True(ElectricalWords.TryByteOf(ElectricalWords.WordOf((byte)b), out byte back));
            Assert.Equal(b, back);
        }
        foreach (string x in new[] { "Lovelace", "LOVELACE", "lovelac", "lovelacee", "", " ohm", "ohm " })
            Assert.False(ElectricalWords.TryByteOf(x, out _), $"'{x}' must not be a word");
    }

    // ======================================== encode / decode ========================================

    [Fact]
    public void Encode_gives_the_pinned_words_big_endian()
    {
        Assert.Equal("copper.tesla.kron", ElectricalRef.Encode(0x3C042C, 3));
        Assert.Equal("ohm.ohm.ohm", ElectricalRef.Encode(0, 3));
        Assert.Equal("leyden.leyden.leyden", ElectricalRef.Encode(0xFFFFFF, 3));
        Assert.Equal("watt.volt.joule.tesla", ElectricalRef.Encode(0x01020304, 4));
        Assert.Equal("copper.tesla.kron.sparse", ElectricalRef.Encode(0x3C042C, 3, withCheck: true));
        Assert.Equal(16_777_215UL, ElectricalRef.MaxValue(3));
        Assert.Equal(4_294_967_295UL, ElectricalRef.MaxValue(4));
        Assert.Equal(ulong.MaxValue, ElectricalRef.MaxValue(8));
    }

    [Fact]
    public void The_check_word_is_CRC8_SAE_J1850()
    {
        // The published check value of CRC-8/SAE-J1850 over the ASCII bytes "123456789" is 0x4B.
        Assert.Equal(0x4B, ElectricalRef.Crc8("123456789"u8));
        Assert.Equal(0x00, ElectricalRef.Crc8(ReadOnlySpan<byte>.Empty));   // no data: the initial 0xFF cancelled by the final XOR 0xFF
    }

    [Fact]
    public void Encode_and_decode_round_trip_at_the_edges_and_over_a_large_sample()
    {
        for (int n = ElectricalRef.MinWords; n <= ElectricalRef.MaxWords; n++)
        {
            ulong max = ElectricalRef.MaxValue(n);
            var values = new List<ulong> { 0, 1, max, max - 1, max >> 1, (max >> 1) + 1 };
            for (int bit = 0; bit < 8 * n; bit++) values.Add(1UL << bit);
            var rnd = new Random(20261024 + n);
            for (int i = 0; i < 20_000; i++) values.Add((ulong)rnd.NextInt64() & max);
            foreach (ulong v in values)
                foreach (bool check in new[] { false, true })
                {
                    string text = ElectricalRef.Encode(v, n, check);
                    Assert.Equal(n + (check ? 1 : 0), text.Split('.').Length);
                    Assert.Equal(v, ElectricalRef.Decode(text, n, check));
                }
        }
    }

    [Fact]
    public void Encode_refuses_a_value_that_does_not_fit_and_a_word_count_outside_1_to_8()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ElectricalRef.Encode(0x1000000, 3));
        Assert.Throws<ArgumentOutOfRangeException>(() => ElectricalRef.Encode(256, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => ElectricalRef.Encode(1, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => ElectricalRef.Encode(1, 9));
        Assert.Throws<ArgumentOutOfRangeException>(() => ElectricalRef.Decode("ohm", 0));
    }

    [Theory]
    [InlineData("", "empty")]
    [InlineData("Copper.tesla.kron", "upper-case")]
    [InlineData("copper.TESLA.kron", "upper-case")]
    [InlineData("copper tesla kron", "white space")]
    [InlineData("copper.tesla.kron ", "white space")]
    [InlineData("copper,tesla,kron", "the character ','")]
    [InlineData("copper-tesla-kron", "the character '-'")]
    [InlineData("copper..kron", "empty word")]
    [InlineData(".copper.tesla", "empty word")]
    [InlineData("copper.tesla.", "empty word")]
    [InlineData("copper.tesla", "has 2 word(s): expected 3 word(s)")]
    [InlineData("copper.tesla.kron.ohm", "has 4 word(s): expected 3 word(s)")]
    [InlineData("coper.tesla.kron", "'coper' (word 1) is not an electrical word. Nearest: copper")]
    [InlineData("copper.tesla.krom", "'krom' (word 3) is not an electrical word. Nearest:")]
    public void Malformed_references_are_refused_with_a_clear_reason(string text, string reason)
    {
        var ex = Assert.Throws<ElectricalRefException>(() => ElectricalRef.Decode(text, 3));
        Assert.Contains(reason, ex.Message, StringComparison.Ordinal);
        Assert.False(ElectricalRef.TryDecode(text, 3, false, out _, out string? error));
        Assert.Equal(ex.Message, error);
    }

    [Fact]
    public void An_unknown_word_names_its_nearest_words_by_edit_distance()
    {
        Assert.Equal("copper", ElectricalRef.NearestWords("coper")[0]);
        Assert.Equal("lovelace", ElectricalRef.NearestWords("lovelase")[0]);
        Assert.Equal(ElectricalRef.Suggestions, ElectricalRef.NearestWords("zzzzzz").Count);
        Assert.Equal(0, ElectricalRef.EditDistance("kron", "kron"));
        Assert.Equal(1, ElectricalRef.EditDistance("kron", "krom"));
        Assert.Equal(3, ElectricalRef.EditDistance("kitten", "sitting"));
    }

    [Fact]
    public void Decoding_with_the_check_word_refuses_a_wrong_one_and_a_missing_one()
    {
        Assert.Equal(0x3C042CUL, ElectricalRef.Decode("copper.tesla.kron.sparse", 3, withCheck: true));
        var bad = Assert.Throws<ElectricalRefException>(() => ElectricalRef.Decode("copper.tesla.kron.ohm", 3, withCheck: true));
        Assert.Contains("fails its check word", bad.Message, StringComparison.Ordinal);
        var missing = Assert.Throws<ElectricalRefException>(() => ElectricalRef.Decode("copper.tesla.kron", 3, withCheck: true));
        Assert.Contains("expected 4 word(s) (3 and a check word)", missing.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(3)]
    [InlineData(4)]
    public void The_check_word_catches_every_single_word_substitution_and_every_adjacent_swap(int n)
    {
        var rnd = new Random(1000 + n);
        for (int sample = 0; sample < 400; sample++)
        {
            ulong v = (ulong)rnd.NextInt64() & ElectricalRef.MaxValue(n);
            string[] words = ElectricalRef.Encode(v, n, withCheck: true).Split('.');
            // every other word in every position, the check word's position included
            for (int pos = 0; pos <= n; pos++)
                for (int b = 0; b < 256; b++)
                {
                    string w = ElectricalWords.WordOf((byte)b);
                    if (w == words[pos]) continue;
                    string[] changed = (string[])words.Clone();
                    changed[pos] = w;
                    Assert.False(ElectricalRef.TryDecode(string.Join('.', changed), n, true, out _, out _),
                        $"substituting '{w}' at {pos} in {string.Join('.', words)} passed the check");
                }
            // every swap of two adjacent different words, with the check word too
            for (int pos = 0; pos < n; pos++)
            {
                if (words[pos] == words[pos + 1]) continue;
                string[] swapped = (string[])words.Clone();
                (swapped[pos], swapped[pos + 1]) = (swapped[pos + 1], swapped[pos]);
                Assert.False(ElectricalRef.TryDecode(string.Join('.', swapped), n, true, out _, out _),
                    $"swapping words {pos} and {pos + 1} of {string.Join('.', words)} passed the check");
            }
        }
    }

    // ======================================== the scatter ========================================

    [Fact]
    public void The_scatter_is_a_bijection_over_the_whole_24_bit_space_and_its_inverse_is_exact()
    {
        var s = new ElectricalScatter(0x0123456789ABCDEFUL, 3);
        Assert.Equal(16_777_216UL, s.Count);
        var seen = new BitArray(1 << 24);
        for (ulong x = 0; x < s.Count; x++)
        {
            ulong y = s.Permute(x);
            Assert.True(y < s.Count);
            Assert.False(seen[(int)y], $"two numbers scatter to {y}");
            seen[(int)y] = true;
            if (s.Unpermute(y) != x) Assert.Fail($"Unpermute(Permute({x})) = {s.Unpermute(y)}");
        }
    }

    [Fact]
    public void The_scatter_is_a_bijection_over_16_bits_and_its_inverse_is_exact_over_a_32_bit_sample()
    {
        var two = new ElectricalScatter(42, 2);
        Assert.Equal(65_536, Enumerable.Range(0, 65_536).Select(x => two.Permute((ulong)x)).Distinct().Count());
        var four = new ElectricalScatter(42, 4);
        var rnd = new Random(7);
        for (int i = 0; i < 200_000; i++)
        {
            ulong x = (ulong)rnd.NextInt64() & 0xFFFFFFFFUL;
            Assert.Equal(x, four.Unpermute(four.Permute(x)));
            Assert.Equal(x, four.Permute(four.Unpermute(x)));
        }
    }

    [Fact]
    public void A_known_seed_gives_the_pinned_references_and_the_same_seed_always_the_same()
    {
        var s3 = new ElectricalScatter(0x0123456789ABCDEFUL, 3);
        Assert.Equal(new[] { "sine.flux.klystron", "spark.laplacian.cathode", "aluminium.copper.admittance", "galvani.aerial.kelvin", "toroid.crank.imaginary" },
            Enumerable.Range(0, 5).Select(i => s3.RefOf((ulong)i)));
        var s4 = new ElectricalScatter(0x0123456789ABCDEFUL, 4);
        Assert.Equal(new[] { "commutator.steel.ayrton.particle", "klystron.nickel.neon.photon", "vacuum.thyristor.brass.lumen",
                "aluminium.hopkinson.franklin.eigen", "vector.lacquer.solenoid.rubber" },
            Enumerable.Range(0, 5).Select(i => s4.RefOf((ulong)i)));
        Assert.Equal(2UL, s3.NumberOf("aluminium.copper.admittance"));

        var again = new ElectricalScatter(0x0123456789ABCDEFUL, 3);
        var other = new ElectricalScatter(0x0123456789ABCDEEUL, 3);
        int differ = 0;
        for (ulong i = 0; i < 1000; i++)
        {
            Assert.Equal(s3.Permute(i), again.Permute(i));
            if (s3.Permute(i) != other.Permute(i)) differ++;
        }
        Assert.True(differ > 990, $"a different seed changed only {differ} of 1000 references");
    }

    [Fact]
    public void The_scatter_refuses_an_unsupported_word_count_and_an_out_of_range_number()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ElectricalScatter(1, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ElectricalScatter(1, 5));
        var s = new ElectricalScatter(1, 3);
        Assert.Throws<ArgumentOutOfRangeException>(() => s.Permute(1UL << 24));
        Assert.Throws<ArgumentOutOfRangeException>(() => s.Unpermute(1UL << 24));
    }

    // ======================================== the sources ========================================

    [Fact]
    public void FromId_follows_from_the_SHA256_of_the_id_and_is_stable()
    {
        Assert.Equal("crank.magnet.rhythm", ElectricalRefSource.FromId("_1D2F3A4B-0000-4000-8000-000000000001", 3));
        Assert.Equal("crank.magnet.rhythm.sine", ElectricalRefSource.FromId("_1D2F3A4B-0000-4000-8000-000000000001", 4));
        Assert.Equal("diode.fourier.cyclotron", ElectricalRefSource.FromId("GB-SUB-0001", 3));
        Assert.Equal("diode.fourier.cyclotron.swan", ElectricalRefSource.FromId("GB-SUB-0001", 4));
        Assert.Throws<ArgumentException>(() => ElectricalRefSource.FromId("", 3));
    }

    [Fact]
    public void FromIds_reports_every_collision_and_never_resolves_one()
    {
        // 300 ids into 256 one-word references: collisions are certain (pigeonhole).
        string[] ids = Enumerable.Range(0, 300).Select(i => "asset-" + i).ToArray();
        ElectricalRefAssignment a = ElectricalRefSource.FromIds(ids, 1);
        Assert.False(a.IsCollisionFree);
        Assert.Equal(300, a.Refs.Count);
        Assert.All(ids, id => Assert.Equal(ElectricalRefSource.FromId(id, 1), a.Refs[id]));   // every id keeps its own ref
        Assert.Equal(a.Refs.Values.GroupBy(r => r).Count(g => g.Count() > 1), a.Collisions.Count);
        foreach (ElectricalRefCollision c in a.Collisions)
        {
            Assert.True(c.Ids.Count > 1);
            Assert.All(c.Ids, id => Assert.Equal(c.Ref, a.Refs[id]));
        }
        Assert.Equal(a.Refs.Values.GroupBy(r => r).Where(g => g.Count() > 1).Sum(g => g.Count()), a.Collisions.Sum(c => c.Ids.Count));

        Assert.Throws<ArgumentException>(() => ElectricalRefSource.FromIds(new[] { "x", "x" }, 3));
    }

    [Fact]
    public void NewRandom_never_returns_a_taken_reference_and_fails_loud_when_the_space_is_full()
    {
        // All one-word references taken but 16: every draw that returns must be one of the 16 free ones.
        var all = Enumerable.Range(0, 256).Select(b => ElectricalRef.Encode((ulong)b, 1)).ToList();
        var free = all.Take(16).ToHashSet(StringComparer.Ordinal);
        var taken = all.Skip(16).ToHashSet(StringComparer.Ordinal);
        for (int i = 0; i < 200; i++)
        {
            string r = ElectricalRefSource.NewRandom(1, taken);
            Assert.Contains(r, free);
        }
        var full = all.ToHashSet(StringComparer.Ordinal);
        var ex = Assert.Throws<InvalidOperationException>(() => ElectricalRefSource.NewRandom(1, full));
        Assert.Contains("nearly full", ex.Message, StringComparison.Ordinal);

        string three = ElectricalRefSource.NewRandom(3, new HashSet<string>(), withCheck: true);
        Assert.Equal(4, three.Split('.').Length);
        ElectricalRef.Decode(three, 3, withCheck: true);   // a valid reference with a valid check word
    }
}
