# Release signing certificate (public part)

`gridsim-signing.cer` is the PUBLIC certificate that signs the Windows release binary (Authenticode, F13). It is
self-signed (subject `CN=GridSim`, SHA-256, RSA 3072, valid to 2029-10-05, thumbprint
86DD43B731D2B8A5133972F7B7C91C0FDA8EB721). The private key is never in this repository: it lives only in the
repository secrets SIGNING_CERT_PFX_BASE64 and SIGNING_CERT_PASSWORD read by `.github/workflows/release.yml`.

What a self-signed signature gives you: proof the binary is the one the release workflow built and has not been changed
since. What it does not give you: Windows SmartScreen still warns, because no public certificate authority vouches for
the certificate. A publicly trusted certificate can replace it later without changing anything else.

To check a downloaded binary, in PowerShell:

    Get-AuthenticodeSignature .\gridsim.exe | Format-List Status, SignerCertificate

The signer thumbprint must equal the one above. Status reads UnknownError ("not trusted") unless you import
`gridsim-signing.cer` into your Trusted Root store, which you need not do: the thumbprint match is the check.
