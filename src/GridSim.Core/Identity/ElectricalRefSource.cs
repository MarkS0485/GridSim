using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace GridSim.Core.Identity;

// =====================================================================================================================
// Where new electrical references come from (2026-10-24), beside the scatter (ElectricalScatter, for sequential asset
// numbers):
//
//   - NewRandom: a brand-new reference from the operating system's cryptographic random source
//     (System.Security.Cryptography.RandomNumberGenerator), checked against the references already taken. A taken one
//     is drawn again, up to MaxAttempts; reaching that means the space is nearly full for that word count, which is a
//     planning problem, so it fails loud instead of looping for ever.
//
//   - FromId: a reference that FOLLOWS from an existing identity (a CIM mRID, a GUID, a case key): the SHA-256 of the
//     id's UTF-8 bytes, its leading bytes taken as the value. The same id gives the same reference everywhere, for
//     ever. Different ids can share a reference (3 words is 16.7 million references, so collisions become likely past a
//     few thousand ids by the birthday bound); FromIds REPORTS every shared reference with the ids that share it and
//     never resolves one silently (it does not rehash, add a word or pick a winner: that is the caller's decision).
// =====================================================================================================================

/// <summary>Two or more ids whose references are the same (<see cref="ElectricalRefSource.FromIds"/>).</summary>
/// <param name="Ref">The shared reference.</param>
/// <param name="Ids">The ids that share it, in input order.</param>
public sealed record ElectricalRefCollision(string Ref, IReadOnlyList<string> Ids);

/// <summary>The references of a set of ids (<see cref="ElectricalRefSource.FromIds"/>), and every collision among them.</summary>
/// <param name="Refs">Each id's reference (collided ids included, each with the shared reference).</param>
/// <param name="Collisions">Every reference shared by two or more ids, in order of first appearance; empty when none.</param>
public sealed record ElectricalRefAssignment(IReadOnlyDictionary<string, string> Refs, IReadOnlyList<ElectricalRefCollision> Collisions)
{
    /// <summary>True when no two ids share a reference.</summary>
    public bool IsCollisionFree => Collisions.Count == 0;
}

/// <summary>New electrical references: random (checked against taken ones) or following from an existing id.</summary>
public static class ElectricalRefSource
{
    /// <summary>How many draws <see cref="NewRandom"/> makes before it fails loud (file header).</summary>
    public const int MaxAttempts = 1000;

    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>
    /// A new random reference of <paramref name="wordCount"/> words (plus the check word when
    /// <paramref name="withCheck"/>) that is not in <paramref name="taken"/>. The taken references are compared as
    /// written, so they must be of the same shape (word count, check word). Throws
    /// <see cref="InvalidOperationException"/> when no free reference turns up within <see cref="MaxAttempts"/> draws.
    /// </summary>
    public static string NewRandom(int wordCount, IReadOnlySet<string> taken, bool withCheck = false)
    {
        ArgumentNullException.ThrowIfNull(taken);
        ElectricalRef.MaxValue(wordCount);   // validates the word count (fail loud before drawing)
        Span<byte> buffer = stackalloc byte[ElectricalRef.MaxWords];
        for (int attempt = 0; attempt < MaxAttempts; attempt++)
        {
            RandomNumberGenerator.Fill(buffer[..wordCount]);
            ulong value = 0;
            for (int i = 0; i < wordCount; i++) value = (value << 8) | buffer[i];
            string candidate = ElectricalRef.Encode(value, wordCount, withCheck);
            if (!taken.Contains(candidate)) return candidate;
        }
        throw new InvalidOperationException(
            $"No free {wordCount.ToString(Inv)}-word reference in {MaxAttempts.ToString(Inv)} random draws against {taken.Count.ToString(Inv)} taken: "
            + "the space is nearly full for that word count; use more words.");
    }

    /// <summary>
    /// The reference that follows from <paramref name="id"/>: the leading <paramref name="wordCount"/> bytes of the
    /// SHA-256 of its UTF-8 bytes, big-endian. The same id always gives the same reference. Throws for an empty id.
    /// </summary>
    public static string FromId(string id, int wordCount, bool withCheck = false)
    {
        ArgumentNullException.ThrowIfNull(id);
        if (id.Length == 0) throw new ArgumentException("An empty id has no reference.", nameof(id));
        ElectricalRef.MaxValue(wordCount);   // validates the word count
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(id));
        ulong value = 0;
        for (int i = 0; i < wordCount; i++) value = (value << 8) | hash[i];
        return ElectricalRef.Encode(value, wordCount, withCheck);
    }

    /// <summary>
    /// The reference of every id in <paramref name="ids"/> (<see cref="FromId"/>) and every reference two or more of
    /// them share, reported, never resolved (file header). Throws when an id is empty or appears twice (the same id
    /// twice is the caller's mistake, not a collision).
    /// </summary>
    public static ElectricalRefAssignment FromIds(IEnumerable<string> ids, int wordCount, bool withCheck = false)
    {
        ArgumentNullException.ThrowIfNull(ids);
        var refs = new Dictionary<string, string>(StringComparer.Ordinal);
        var byRef = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var order = new List<string>();
        foreach (string id in ids)
        {
            ArgumentNullException.ThrowIfNull(id, nameof(ids));
            if (refs.ContainsKey(id)) throw new ArgumentException($"The id '{id}' is listed twice.", nameof(ids));
            string r = FromId(id, wordCount, withCheck);
            refs[id] = r;
            if (!byRef.TryGetValue(r, out List<string>? sharing))
            {
                byRef[r] = sharing = new List<string>();
                order.Add(r);
            }
            sharing.Add(id);
        }
        var collisions = order.Where(r => byRef[r].Count > 1).Select(r => new ElectricalRefCollision(r, byRef[r])).ToList();
        return new ElectricalRefAssignment(refs, collisions);
    }
}
