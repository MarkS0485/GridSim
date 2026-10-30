namespace GridSim.Core.Licensing;

/// <summary>
/// Thrown when a licence action fails in a way the caller should surface to the user - installing a
/// rejected key, or a front-end choosing to hard-block an over-limit model. The message is
/// user-facing (it names the tier, the limit, and the contact email).
/// </summary>
public sealed class LicenceException : Exception
{
    public LicenceException() { }

    public LicenceException(string message) : base(message) { }

    public LicenceException(string message, Exception innerException) : base(message, innerException) { }
}
