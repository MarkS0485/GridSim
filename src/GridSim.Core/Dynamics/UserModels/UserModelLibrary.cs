
namespace GridSim.Core.Dynamics.UserModels;

/// <summary>
/// The shipped library of declarative models (R4-08): the data files under <c>Dynamics/UserModels/Library/</c>, embedded in
/// the assembly (a model is loaded from bytes compiled into the program, so nothing is read from disk at run time).
/// Each is a <c>gridsim-model/1</c> document with its source citation and typical default values; the models that
/// re-express a coded model are proved bit-identical to it by <c>UserModelBitIdentityTests</c>.
/// </summary>
public static class UserModelLibrary
{
    private const string Prefix = "GridSim.Core.usermodels.";
    private const string Suffix = ".model.json";

    /// <summary>The ids of the shipped models, ordinal-sorted.</summary>
    public static IReadOnlyList<string> Ids { get; } = typeof(UserModelLibrary).Assembly.GetManifestResourceNames()
        .Where(n => n.StartsWith(Prefix, StringComparison.Ordinal) && n.EndsWith(Suffix, StringComparison.Ordinal))
        .Select(n => n.Substring(Prefix.Length, n.Length - Prefix.Length - Suffix.Length))
        .OrderBy(n => n, StringComparer.Ordinal)
        .ToList();

    /// <summary>The raw bytes of a shipped document.</summary>
    public static byte[] Bytes(string id)
    {
        using Stream s = typeof(UserModelLibrary).Assembly.GetManifestResourceStream(Prefix + id + Suffix)
            ?? throw new ArgumentException($"There is no shipped user model '{id}' (shipped: {string.Join(", ", Ids)}).", nameof(id));
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }

    /// <summary>Parse a shipped document.</summary>
    public static ModelDocument Document(string id) => ModelDocument.Parse(Bytes(id));

    /// <summary>Parse, compile and bind a shipped model (document values, or <paramref name="parameters"/> where given).</summary>
    public static UserModel Load(string id, IReadOnlyDictionary<string, double>? parameters = null)
        => UserModel.FromDocument(Document(id), parameters);
}
