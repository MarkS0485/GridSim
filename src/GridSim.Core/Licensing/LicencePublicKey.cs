namespace GridSim.Core.Licensing;

/// <summary>
/// The embedded ECDSA P-256 <b>public</b> key (SubjectPublicKeyInfo, base64) that verifies licence
/// keys entirely offline - no network call, ever. This is safe to commit and ship: a public key can
/// only <i>verify</i> a signature, never mint one. The matching <b>private</b> key lives only in the
/// out-of-repo forge (<c>W:\Keys\gridsim-signing-key.pem</c>, used by the forge in <c>W:\Code\GridSimLicensing</c>; rotated 2026-10-25) and is never committed.
///
/// To rotate keys: run the forge's <c>keygen</c> and paste the new public SPKI here. Keys signed by
/// the old private key stop verifying, so rotate deliberately.
/// </summary>
internal static class LicencePublicKey
{
    /// <summary>
    /// Placeholder until the forge's <c>keygen</c> is run. When this equals the sentinel, no verifier is
    /// available and every front-end falls back to the free Personal tier (never crashes).
    /// </summary>
    public const string Placeholder = "PLACEHOLDER";

    /// <summary>Base64 SubjectPublicKeyInfo of the production signing key's public half.</summary>
    public const string SpkiBase64 =
        "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEsufP7z424mRhSPiTqnzsJ6VZSdMT" +
        "8RheAaBiPJ7q6hRTmNYDvkG2fy6RtT7lhThepf9M5C+a1cy6d8o3VnsKYQ==";
}
