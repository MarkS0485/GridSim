using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using GridSim.Core.Security;
using Microsoft.AspNetCore.DataProtection;

namespace GridSim.Web.Site;

/// <summary>
/// D-DOC-064: encryption at rest of the data-protection key ring (the folder <c>Auth:KeysDirectory</c> names).
/// <para>
/// The key ring holds the master keys that protect the sign-in cookie, the antiforgery tokens and the SSO tickets.
/// Before D-DOC-064 the host persisted it with <c>PersistKeysToFileSystem</c> and no key encryptor, so every key sat in
/// the folder as plain XML: anyone who could read the folder (a backup, a mis-set volume permission, another container
/// on the same mount) could forge a signed-in cookie. ASP.NET Core encrypts a key ring only when told how, and the right
/// "how" depends on the platform, so it is now a deployment setting, chosen by name and checked at start-up:
/// </para>
/// <list type="table">
/// <item><term><c>auto</c> (default)</term><description>Windows: DPAPI for the account the app runs as. Elsewhere: an
/// X.509 certificate when <c>Auth:KeyCertificatePath</c> is set, otherwise no encryption, with a start-up warning that
/// names the settings (Linux has no machine secret store ASP.NET Core can use on its own, and refusing to start would
/// break the replay-only container, whose key ring protects nothing but antiforgery tokens).</description></item>
/// <item><term><c>dpapi</c></term><description>Windows DPAPI, current account. Refused at start-up on any other OS.</description></item>
/// <item><term><c>dpapi-machine</c></term><description>Windows DPAPI, any account on this machine (for a ring shared by
/// several app-pool identities on one Windows host). Refused at start-up on any other OS.</description></item>
/// <item><term><c>certificate</c></term><description>Every new key is encrypted to the certificate in the PKCS#12 file at
/// <c>Auth:KeyCertificatePath</c> (its password, if any, in <c>Auth:KeyCertificatePassword</c>, which belongs in an
/// environment variable or secret store, never in a committed file). The file must exist and carry its private key, or
/// start-up stops. Works on every OS; every app sharing an estate key ring must be given the same certificate.</description></item>
/// <item><term><c>none</c></term><description>No encryption, chosen on purpose (for example a ring that an estate's other
/// apps must read and that is protected by its file permissions instead). Logged at start-up.</description></item>
/// </list>
/// <para>
/// Keys already in the folder are not rewritten: encryption applies to keys created from now on, and an unencrypted
/// old key stays readable. That keeps an existing shared estate ring working; the operator retires old plain keys by
/// letting them expire (90 days by default) or by removing them.
/// </para>
/// <para>
/// N346 (CF3, October 2026): <b>certificate rotation.</b> <c>Auth:KeyPreviousCertificatePath</c> (and
/// <c>Auth:KeyPreviousCertificatePassword</c>) name a second PKCS#12 file whose certificate is used ONLY to decrypt keys
/// written before the rotation; nothing new is ever encrypted to it. To rotate: put the new certificate in
/// <c>Auth:KeyCertificatePath</c>, the old one in <c>Auth:KeyPreviousCertificatePath</c>, and restart every app sharing
/// the ring. New keys are encrypted to the new certificate and the old keys still read, so no signed-in cookie or SSO
/// ticket is lost. Remove the previous setting (and the old certificate) once every key encrypted to it has expired
/// (90 days after the rotation by default, or when those key files are deleted). The previous certificate must carry
/// its private key and differ from the current one; anything else stops start-up naming the setting. It is honoured in
/// every mode, so a ring moved from a certificate to DPAPI (or to none) keeps reading its old keys.
/// </para>
/// </summary>
internal static class KeyRingProtection
{
    /// <summary>Configuration key of the mode (environment variable <c>Auth__KeyProtection</c>).</summary>
    public const string ModeSetting = "Auth:KeyProtection";

    /// <summary>Configuration key of the PKCS#12 certificate file (environment variable <c>Auth__KeyCertificatePath</c>).</summary>
    public const string CertificatePathSetting = "Auth:KeyCertificatePath";

    /// <summary>Configuration key of that file's password (environment variable <c>Auth__KeyCertificatePassword</c>).</summary>
    public const string CertificatePasswordSetting = "Auth:KeyCertificatePassword";

    /// <summary>N346: configuration key of the PREVIOUS certificate's PKCS#12 file, decrypt-only, for a rotation
    /// (environment variable <c>Auth__KeyPreviousCertificatePath</c>).</summary>
    public const string PreviousCertificatePathSetting = "Auth:KeyPreviousCertificatePath";

    /// <summary>N346: configuration key of that file's password (environment variable
    /// <c>Auth__KeyPreviousCertificatePassword</c>).</summary>
    public const string PreviousCertificatePasswordSetting = "Auth:KeyPreviousCertificatePassword";

    /// <summary>The protector the host ends up using.</summary>
    public enum Protector
    {
        /// <summary>Keys stored as plain XML.</summary>
        None,
        /// <summary>Windows DPAPI, current account.</summary>
        DpapiCurrentUser,
        /// <summary>Windows DPAPI, local machine.</summary>
        DpapiLocalMachine,
        /// <summary>An X.509 certificate.</summary>
        Certificate,
    }

    /// <summary>The decision, made once at start-up: which protector, the certificate when there is one, and the
    /// one-line text the host writes to its log (a warning when the ring ends up unencrypted without being asked).</summary>
    /// <remarks>N346: <c>PreviousCertificate</c> is the decrypt-only certificate of a rotation, or null.</remarks>
    public sealed record Plan(Protector Protector, X509Certificate2? Certificate, string LogLine, bool IsWarning,
        X509Certificate2? PreviousCertificate = null);

    /// <summary>
    /// Read the settings and decide, including the N346 previous (decrypt-only) certificate. The mode rules are
    /// <see cref="ResolveMode"/>'s; when <see cref="PreviousCertificatePathSetting"/> is set, its certificate is loaded and
    /// checked (exists, PKCS#12 with its password, private key, not the current certificate) and the log line says it is
    /// read. Throws <see cref="InvalidOperationException"/> naming the setting for every value that cannot be honoured.
    /// </summary>
    /// <remarks>
    /// B3 secrets management: the certificate passwords are resolved through <paramref name="secrets"/> first and
    /// configuration only as a fallback, so a deployment can keep them in an OS keychain / vault rather than in
    /// plain configuration. Null uses <see cref="EnvironmentSecretStore"/>, which matches the previous behaviour
    /// (the password was read from configuration, which already surfaces environment variables).
    /// </remarks>
    public static Plan Resolve(IConfiguration config, bool isWindows, ISecretStore? secrets = null)
    {
        secrets ??= EnvironmentSecretStore.Instance;
        Plan plan = ResolveMode(config, isWindows, secrets);
        string? previousPath = config[PreviousCertificatePathSetting];
        if (string.IsNullOrWhiteSpace(previousPath)) return plan;
        X509Certificate2 previous = LoadWithPrivateKey(config, previousPath, PreviousCertificatePathSetting, PreviousCertificatePasswordSetting, secrets);
        if (plan.Certificate is not null && string.Equals(previous.Thumbprint, plan.Certificate.Thumbprint, StringComparison.OrdinalIgnoreCase))
        {
            previous.Dispose();
            throw new InvalidOperationException(
                $"GridSim.Web: configuration setting {PreviousCertificatePathSetting} names the same certificate as {CertificatePathSetting} (thumbprint {plan.Certificate.Thumbprint}); "
                + "the previous certificate is the one being rotated out, so it must be a different one (or leave the setting empty).");
        }
        return plan with
        {
            PreviousCertificate = previous,
            LogLine = plan.LogLine + $" Keys encrypted to the previous certificate in {PreviousCertificatePathSetting} (thumbprint {previous.Thumbprint}) are still read; nothing new is encrypted to it.",
        };
    }

    /// <summary>
    /// Read the mode settings and decide. <paramref name="isWindows"/> is passed in (rather than read here) so a test can
    /// check both platforms' rules on one machine; only the actual protector call needs the real OS.
    /// Throws <see cref="InvalidOperationException"/> naming the setting for every value that cannot be honoured.
    /// </summary>
    private static Plan ResolveMode(IConfiguration config, bool isWindows, ISecretStore secrets)
    {
        string mode = (config[ModeSetting] ?? "").Trim().ToLowerInvariant();
        if (mode.Length == 0) mode = "auto";
        string? certPath = config[CertificatePathSetting];

        switch (mode)
        {
            case "auto":
                if (isWindows)
                    return new Plan(Protector.DpapiCurrentUser, null,
                        $"GridSim.Web: data-protection keys are encrypted at rest with Windows DPAPI (current account; {ModeSetting}=auto).", false);
                if (!string.IsNullOrWhiteSpace(certPath))
                    return CertificatePlan(config, certPath, "auto", secrets);
                return new Plan(Protector.None, null,
                    $"GridSim.Web: WARNING: data-protection keys are stored UNENCRYPTED at rest: this OS has no built-in key protector. "
                    + $"Set {CertificatePathSetting} (and {CertificatePasswordSetting} if the file has one) to encrypt them with a certificate, "
                    + $"or set {ModeSetting}=none to record that plain keys protected by file permissions are intended.", true);

            case "dpapi":
            case "dpapi-machine":
                if (!isWindows)
                    throw new InvalidOperationException(
                        $"GridSim.Web: configuration setting {ModeSetting}={mode} needs Windows (DPAPI); on this OS use 'certificate' with {CertificatePathSetting}, or 'none'.");
                bool machine = mode == "dpapi-machine";
                return new Plan(machine ? Protector.DpapiLocalMachine : Protector.DpapiCurrentUser, null,
                    $"GridSim.Web: data-protection keys are encrypted at rest with Windows DPAPI ({(machine ? "local machine" : "current account")}; {ModeSetting}={mode}).", false);

            case "certificate":
                if (string.IsNullOrWhiteSpace(certPath))
                    throw new InvalidOperationException(
                        $"GridSim.Web: configuration setting {ModeSetting}=certificate needs {CertificatePathSetting}, the path of a PKCS#12 (.pfx) file holding the certificate and its private key.");
                return CertificatePlan(config, certPath, "certificate", secrets);

            case "none":
                return new Plan(Protector.None, null,
                    $"GridSim.Web: data-protection keys are stored unencrypted at rest, as configured ({ModeSetting}=none).", false);

            default:
                throw new InvalidOperationException(
                    $"GridSim.Web: configuration setting {ModeSetting} is '{config[ModeSetting]}'; use one of auto, dpapi, dpapi-machine, certificate, none.");
        }
    }

    /// <summary>Load and check the certificate: the file must exist, parse as PKCS#12 with the given password, and
    /// carry a private key (without one the host could encrypt new keys but never read them back).</summary>
    private static Plan CertificatePlan(IConfiguration config, string certPath, string mode, ISecretStore secrets)
    {
        X509Certificate2 cert = LoadWithPrivateKey(config, certPath, CertificatePathSetting, CertificatePasswordSetting, secrets);
        return new Plan(Protector.Certificate, cert,
            $"GridSim.Web: data-protection keys are encrypted at rest with the certificate in {CertificatePathSetting} (thumbprint {cert.Thumbprint}; {ModeSetting}={mode}).", false);
    }

    /// <summary>Load a PKCS#12 file named by <paramref name="pathSetting"/> with the password in
    /// <paramref name="passwordSetting"/>; the file must exist, parse and carry a private key. The current certificate
    /// and (N346) the previous one share these rules and messages, each naming its own setting.</summary>
    private static X509Certificate2 LoadWithPrivateKey(IConfiguration config, string certPath, string pathSetting,
        string passwordSetting, ISecretStore secrets)
    {
        string path = certPath.Trim();
        if (!File.Exists(path))
            throw new InvalidOperationException(
                $"GridSim.Web: configuration setting {pathSetting} names a file that does not exist ('{path}').");
        // B3: prefer the secret store (OS keychain / vault / environment), fall back to configuration. The
        // password itself is never logged or echoed in any error.
        string? password = secrets.Get(passwordSetting) ?? config[passwordSetting];
        X509Certificate2 cert;
        try
        {
            cert = X509CertificateLoader.LoadPkcs12FromFile(path, password);
        }
        catch (CryptographicException ex)
        {
            // The password itself is never echoed; the reason from the platform says whether it was wrong.
            throw new InvalidOperationException(
                $"GridSim.Web: the certificate file in {pathSetting} could not be read as PKCS#12 with the password in {passwordSetting}: {ex.Message}", ex);
        }
        if (!cert.HasPrivateKey)
        {
            cert.Dispose();
            throw new InvalidOperationException(
                $"GridSim.Web: the certificate in {pathSetting} has no private key, so keys encrypted to it could never be read back; export it with its private key.");
        }
        return cert;
    }

    /// <summary>Apply the plan to the data-protection builder. Call once, after <c>PersistKeysToFileSystem</c>.</summary>
    public static void Apply(IDataProtectionBuilder builder, Plan plan)
    {
        switch (plan.Protector)
        {
            case Protector.DpapiCurrentUser:
            case Protector.DpapiLocalMachine:
                // Resolve only returns a DPAPI plan when told the host is Windows; this check is the real one, and it
                // also tells the platform analyser the Windows-only call below is guarded.
                if (!OperatingSystem.IsWindows())
                    throw new InvalidOperationException("GridSim.Web: DPAPI key protection was chosen on a host that is not Windows.");
                builder.ProtectKeysWithDpapi(protectToLocalMachine: plan.Protector == Protector.DpapiLocalMachine);
                break;
            case Protector.Certificate:
                // Also registers the certificate for decryption, so the ring reads back without the certificate being
                // in an OS certificate store.
                builder.ProtectKeysWithCertificate(plan.Certificate!);
                break;
            case Protector.None:
                break;
        }
        // N346: the previous certificate of a rotation decrypts the keys written before it and never encrypts. (The
        // current certificate, when there is one, is already registered for decryption by ProtectKeysWithCertificate.)
        if (plan.PreviousCertificate is not null)
            builder.UnprotectKeysWithAnyCertificate(plan.PreviousCertificate);
    }
}
