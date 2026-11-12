# Release signing certificate (public part)

`gridsim-signing.cer` is the PUBLIC certificate that signs the Windows CLI binary (Authenticode) and the NuGet package. It is
self-signed (subject `CN=GridSim`, SHA-256, RSA 3072, valid to 2029-10-05, thumbprint
86DD43B731D2B8A5133972F7B7C91C0FDA8EB721). The private key is never in this repository. It is held by the owner,
outside the repository, on the machine that signs releases. The release workflow (`.github/workflows/release.yml`) is
written to read it from the repository secrets SIGNING_CERT_PFX_BASE64 and SIGNING_CERT_PASSWORD, but those secrets
are not set, so a release built by the workflow today would be published unsigned, with a warning. 2.1.000 RC-1 was
built and signed by the owner on that machine, outside the workflow: its three Windows executables carry the
Authenticode signature and its NuGet package the author signature (see `RC1.md`).

What a self-signed signature gives you: proof the binary was signed by the holder of this certificate's private key
and has not been changed since. It does not prove which build or workflow produced the binary (for RC-1 no
build-provenance attestation exists), and Windows SmartScreen still warns, because no public certificate authority
vouches for the certificate. A publicly trusted certificate can replace it later without changing anything else.

To check a downloaded binary, in PowerShell:

    Get-AuthenticodeSignature .\gridsim.exe | Format-List Status, SignerCertificate

The signer thumbprint must equal the one above. Status reads UnknownError ("not trusted") unless you import
`gridsim-signing.cer` into your Trusted Root store, which you need not do: the thumbprint match is the check.
