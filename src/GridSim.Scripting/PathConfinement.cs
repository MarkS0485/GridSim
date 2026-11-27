namespace GridSim.Scripting;

/// <summary>
/// The path rule of a confined session (<see cref="Session.ConfineToBaseDirectory"/>): a path a script names must resolve inside the
/// base folder, must not be a network (UNC) or device path, and must not pass through a symbolic link or junction on the way.
/// The rule lives in Core (<see cref="GridSim.Core.Security.PathConfinement"/>, which the model plug-in loader also uses); this
/// class forwards to it with the same signatures.
/// </summary>
public static class PathConfinement
{
    /// <summary>Null when <paramref name="full"/> (the resolved form of <paramref name="written"/>) lies inside
    /// <paramref name="baseDirectory"/>, else the reason it does not, in a few words.</summary>
    public static string? Refusal(string written, string full, string baseDirectory)
        => GridSim.Core.Security.PathConfinement.Refusal(written, full, baseDirectory);

    /// <summary>True for <c>\\server\share</c>, <c>//server/share</c>, <c>\\?\</c> and <c>\\.\</c> forms.</summary>
    public static bool IsNetworkOrDevice(string path) => GridSim.Core.Security.PathConfinement.IsNetworkOrDevice(path);
}
