# 22. Licensing: key format and trust model

This chapter documents the offline licence mechanism end to end: the byte layout of a licence key and its encoding, the
ECDSA P-256 signature and how the public key is embedded, the version and major-version rules, the tier, limit and
expiry fields, the verification steps with every failure mode, the trust model (what a signed key protects and what it
cannot), and the gate, `LicenceGate`, that every front end calls. The user-facing side (tiers, installing a key, what
each program prints) is [chapter 4](<../manual/0004 Licensing and activation.md>). The code is in
`src/GridSim.Core/Licensing/`; nothing in it touches a network.

## Model

### Trust model

A licence key is signed, not encrypted. Its fields are readable by anyone who can base64-decode the token. They cannot be
altered, and a new key cannot be made, without the private signing key. The mechanism protects integrity and authenticity,
not secrecy, because the software is source-available and a secret inside a binary the customer can read is not a secret.

- **Asymmetric.** An ECDSA P-256 key pair. The private key lives only in the issuing tool, which is outside this
  repository, and is never committed. The public key is embedded in `GridSim.Core` as a constant and can only verify.
- **Offline.** Verification is a local signature check. There is no activation call, no phone-home and no revocation list.
- **BCL only.** Everything uses `System.Security.Cryptography`; `GridSim.Core` takes no cryptography package.
- **Enforcement is readable code.** The gate is ordinary source with no anti-tamper layer. Patching it to ignore or
  misreport bus counts is a breach of `LICENSE.md` section 2, not something the code prevents.

### What the signature protects, and what it does not

Protected:

- Changing any field of an issued key (tier, bus limit, expiry, holder, licence id, major version) breaks the signature.
  Flipping the low bit of any byte of the blob is rejected. By the code, a flip in the signed region is caught by the
  signature and a flip in the header by the magic, version or length check; the tests assert the rejection and a
  non-empty reason, not which check fired.
- A key made with any other private key does not verify against the embedded public key.
- A key cut short, padded or concatenated is refused by the length checks before the signature is looked at.

Not protected, by design:

- **Sharing.** A key names a holder but is not bound to a machine, a user or a process. Anyone holding the token can use it.
- **Revocation.** Nothing can withdraw an issued key. Rotating the signing key invalidates every key signed by the old one.
- **The system clock.** Expiry compares the expiry date with `DateTime.UtcNow`; a user who sets the clock back extends a key.
- **The binary.** A rebuilt GridSim with a different embedded public key, or with the gate removed, runs unlicensed code.
  The licence terms, not the software, forbid it.
- **Secrecy of the holder.** Email address and organisation are in the clear in every key.
- **A deleted or ignored key.** Removing the key never makes GridSim worse than the free tier; that is the floor by design.

### Key format

A key is one line, in the style of an SSH authorized-keys entry:

```
GSK1 <base64url-blob>
```

The blob, all multi-byte integers big-endian:

```
offset  size  field
0       4     magic            ASCII "GSK1" (47 53 4B 31)
4       1     version          0x01 (the container version, not the payload schema)
5       2     payloadLen       u16 big-endian, number of payload bytes
7       n     payload          UTF-8, n = payloadLen
7+n     64    signature        ECDSA P-256 / SHA-256, IEEE-P1363 (r || s, 32 + 32 bytes)

signed bytes   = blob[0 .. 7+n)        magic | version | payloadLen | payload
total length   = 7 + n + 64
```

Encoding is base64url (RFC 4648 section 5) with the padding removed: `+` becomes `-`, `/` becomes `_`, trailing `=` is
dropped. The token is one line with no character that a shell, URL or configuration file rewrites. The decoder restores
the padding from the length; a length of 1 modulo 4 is malformed. Only the canonical spelling decodes: a character
outside `A-Z a-z 0-9 - _` (so `+`, `/` or `=`) or a last character with non-zero unused low bits is refused as "not
valid base64", so the text of a key is unique up to the optional prefix and whitespace. Because the magic `GSK1` is the first four bytes,
every blob begins `R1NLMQ` in base64, and the `GSK1 ` prefix in front is cosmetic: it is not signed, it is optional when
parsing, and any whitespace inside the token (a wrapped paste) is removed before decoding.

### Payload

The payload is canonical pipe-delimited UTF-8, nine fields in schema `v2`:

```
v2 | email | org | type | buslimit | issuedDays | expiresDays | licenceId | majorVersion
```

| Field | Encoding | Rule |
|---|---|---|
| `schema` | `v1` or `v2` | selects the field count: 8 or 9. Anything else is refused. |
| `email` | text | the holder's email address. |
| `org` | text | the organisation; empty for a personal key. |
| `type` | wire code | `personal`, `academic`, `code`, `deployment`, `operator`. Unknown code is refused. |
| `buslimit` | decimal integer, `>= 0` | the per-key cap; `0` means unlimited. |
| `issuedDays` | decimal integer | days since 1970-01-01 UTC. |
| `expiresDays` | decimal integer | days since 1970-01-01 UTC, inclusive. |
| `licenceId` | text | short opaque id printed by `licence status`. |
| `majorVersion` | unsigned decimal integer, `>= 1` | the major version the key was issued for. `v2` only. |

Text fields escape `\` as `\\` and `|` as `\|`, so an organisation name containing a pipe round-trips. All numbers are
written and read with the invariant culture, so a key is the same bytes on every machine. Dates are whole days, so a
key has no time of day: it expires at the end of its expiry date, UTC. A day number outside the range of `DateOnly` is
clamped to its minimum or maximum, so a very large expiry day reads as "no expiry".

A `v1` payload is the same eight fields without `majorVersion`. It is still read, as a key for major version 1, the line
on which every `v1` key was issued. A reader that predates `v2` refuses a `v2` key with `unsupported licence schema`
and so fails safe to the free tier. `Encode` writes only `v2`.

### Tier and limit fields

| Wire code | `LicenceTier` | Display name | Bus limit in a normal key |
|---|---|---|---|
| `personal` | `Personal` | Personal | 1,500 |
| `academic` | `Academic` | Academic | 1,500, or the larger allowance granted |
| `code` | `CommercialCode` | Commercial Code | 0 (unlimited) |
| `deployment` | `CommercialDeployment` | Commercial Deployment | 0 (unlimited) |
| `operator` | `NetworkOperator` | Network Operator | 0 (unlimited) |

The tier names the grant and is shown to the user. The gate enforces only the `buslimit` field, not a limit looked up
from the tier, so the tier column above states what the issuer writes, not something the verifier checks. A decoded
limit of 0 or less is `IsUnlimited`. `Entitlement.EffectiveBusLimit` is `int.MaxValue` in that case, but nothing calls it:
`Check` tests `IsUnlimited` and skips the comparison. The free default,
`Entitlement.FreeDefault`, is Personal with 1,500 buses, no holder, licence id `free` and no expiry (`DateOnly.MaxValue`).

### Verification algorithm

`LicenceKey.TryParse(token, verifier, ...)` never throws on bad input; it returns false and a one-line reason. Expiry and
major version are not judged here: parsing reports what the key says and the gate applies policy.

```
1  token empty or whitespace                    -> "empty licence key"
2  body = trim(token); if it starts with "GSK1 " remove that prefix; remove all whitespace
3  blob = base64url_decode(body)                -> "licence key is not valid base64"
4  len(blob) < 7 + 64                           -> "licence key too short"
5  blob[0..4) != "GSK1"                         -> "not a GridSim licence key"
6  blob[4] != 1                                 -> "unsupported licence version <b>"
7  n = u16_be(blob[5..7)); len(blob) != 7+n+64  -> "licence key length mismatch"
8  verify ECDSA-P256-SHA256(blob[0..7+n), blob[7+n..], IEEE-P1363)
       cannot be evaluated                      -> "licence signature could not be checked"
       false                                    -> "licence signature is invalid (key was altered or not issued by GridSim)"
9  parse the payload (only after the signature holds):
       schema not v1 or v2                      -> "unsupported licence schema '<s>'"
       wrong field count for the schema         -> "malformed licence payload"
       unknown tier code                        -> "unknown licence tier '<t>'"
       buslimit not an integer or < 0           -> "invalid bus limit in licence"
       issuedDays not an integer                -> "invalid issue date in licence"
       expiresDays not an integer               -> "invalid expiry date in licence"
       v2 majorVersion not unsigned or < 1      -> "invalid major version in licence"
10 return the Entitlement
```

The payload is parsed only after the signature verifies, so every field the parser reads was written by the signer. The
overload without a verifier uses the embedded public key. If the build carries no usable key (the sentinel
`PLACEHOLDER`, or malformed key bytes), that overload returns false with `no public key is embedded in this build` and
the gate runs at the free tier.

### Major-version rule

`BuildInfo.MajorVersion` is the first dot-separated component of the release number (the 1 of 1.x.y). `BuildInfo.MajorOf`
throws `FormatException` for anything not starting with a non-negative integer, so a build whose version cannot be read
never quietly accepts or refuses keys for the wrong line. A key fits a build when its major version equals the build's:

```
FitsMajorVersion(entitlement, buildMajor):  entitlement.MajorVersion == buildMajor
```

Equality, not "at most": a key for 1.x is refused by a 2.x build, and a key for 2.x is refused by a 1.x build. The
refusal reason is `this key was issued for GridSim <k>.x and this build is major version <b> (GridSim <b>.x); a licence
covers every release of the major version it was issued for (`LICENSE.md` section 1), so GridSim <b>.x needs a <b>.x key:
<contact>`. `Encode` refuses to issue a key whose major version is below 1.

### The gate

`LicenceGate` resolves the active entitlement once and answers whether a model may be used. The bus count is the only
quantity the licence measures.

**Resolution.** The first source whose text is not blank is used; the search never continues past it.

```
sources, in order:  explicit token  |  GRIDSIM_LICENCE  |  contents of licence.gridsim
for the first non-blank source:
    no verifier available            -> FreeDefault, note "no public key embedded in this build; running at the free tier"
    TryParse fails                   -> FreeDefault, note "licence key from <source> rejected: <reason>"
    FitsMajorVersion fails           -> FreeDefault, note "licence key from <source> rejected: <majorReason>"
    otherwise                        -> the key's Entitlement
no source                            -> FreeDefault, no note
```

The source names in notes are `explicit key`, `GRIDSIM_LICENCE` and `licence.gridsim`. The file is read with blank lines
and `#` comment lines skipped and the first remaining line taken, so a `.gridsim` file can carry a human-readable header;
the environment variable and the explicit token are taken whole, with no comment handling. A file that is absent,
unreadable or locked counts as absent. The file path is `Environment.SpecialFolder.LocalApplicationData` plus
`GridSim/licence.gridsim`: `%LOCALAPPDATA%` on Windows, `$XDG_DATA_HOME` or `~/.local/share` on Linux, and
on macOS, the folder .NET returns for that special folder.

**Check.** `Check(busCount, bundled)` returns allowed or denied with a user-facing message:

```
1  bundled                                              -> allowed, never counted
2  entitlement is not the FreeDefault object and
   its major version != the gate's build major version  -> free-tier rules, with the major-version message
3  entitlement.IsNotYetValid  (UtcNow date < Issued)    -> free-tier rules, with the "starts on <Issued>" message
4  entitlement.IsExpired  (UtcNow date > Expires)       -> free-tier rules, with the expiry message
5  not unlimited and busCount > BusLimit                -> denied: "This network has N buses; your <Tier> licence
                                                           allows L. A larger or commercial licence removes the limit: <contact>"
6  otherwise                                            -> allowed

free-tier rules:  busCount > 1500 -> denied;  busCount <= 1500 -> allowed
```

Step 2 is a second line of defence: `Resolve` never returns another major version's key, but a gate built directly from
one gets the free tier's rules. Step 3 makes a key dated in the future (a wrong clock, or a key issued ahead) behave
exactly as no key until its issue date. Step 4 makes an expired key behave exactly as no key. The comparisons in steps
3 to 5 are on whole dates and counts, so a model of exactly the limit is allowed, and the issue day and the expiry day
themselves are valid.
`Enforce` is `Check` that throws `LicenceException` with the same message. `Describe` renders the one-line status that
`licence status`, the web footer tooltip and the desktop log show.

**Install.** `Install(token)` extracts the first key line, verifies it with the embedded key, applies the major-version
rule, and only then creates the folder and writes `key + newline` to the licence file. A build with no embedded key
throws `LicenceException("This build has no embedded public key, so it cannot validate a licence.")`. A rejected key
throws `LicenceException("Licence key rejected: <reason>")` and writes nothing. `ImportFromFile` reads the file first
and wraps an unreadable file in `LicenceException("Could not read licence file '<path>': <reason>")`.

### Where the gate is called

| Front end | Call site | On denial |
|---|---|---|
| CLI | `Gated.Dir`, `Gated.Matpower`, `Gated.MatpowerDetailed`, `Gated.PypsaGbReduced`, `Gated.V2Case` and `Gated.Dist3ph` each call `_gate.Check(busCount, BundledNetworks.IsBundledPath(path))`. | `error: <message>` on standard error, `Environment.Exit(2)`. |
| Case editor | `CaseSession.OpenV1` and `OpenV2` call `CheckLicence("open")`; `SaveGate.Run` calls `session.Gate.Check` on the staged case with the real path's bundled flag. | Open throws `CaseLicenceDeniedException` (exit 2); a save records the problem `licence: <message>` and writes nothing. |
| Web | `CaseCatalog.LoadModel` calls `_license.Enforce(model.BusCount, IsBundled(entry))`. | `LicenceException`, returned as HTTP 403 `{"error": ...}` by `/api/solve` and `/api/topology`, shown on the dashboard load-error panel, sent as a WebSocket error frame. |
| Desktop | `MainWindow` calls `App.Licence.Check(model.BusCount, IsBundledSource(s))` after a model loads. | The message is logged and the model is not switched to. |

`Gated.ReadBackMatpower` re-reads a file the `convert` verb has just written, and is deliberately not counted: it is an
internal check of the verb's own output.

The CLI resolves with `LicenceGate.Resolve()` (no explicit token); the web host passes `Licence:Key`; the desktop app and
the case editor resolve with no explicit token. The `licence` verb and `--importLicence` are handled in `Program.cs`
before flag validation and verb dispatch; `--importLicence` is consumed before the gate is resolved.

### Bundled networks

`BundledNetworks.IsBundledPath(path)` is the one rule, shared by the CLI, the desktop app, the case editor and the web
catalogue. A path is bundled only when it is in place and its content is what shipped:

```
full  = GetFullPath(path)                       (unresolvable path -> not bundled)
roots = DataRoots: the install's own data/ folder (beside the running program), plus, for a program run from a build
        output under a GridSim source checkout's src/ or tests/ folder, that checkout's data/ folder
in place  iff  full is one of root/<folder>, or inside one, for some root in roots and folder in DataFolders
DataFolders = matpower, uk, gb-synthetic, gb-full, intl, dist3ph
shipped   iff  root/bundled.sha256 lists the file with a matching ContentHash; for a folder, it holds at least one
               file and every file in it, at any depth, is listed with a matching hash
bundled   iff  in place and shipped
```

The match is on whole segments, so `data/gb-fullish` and `data/ukx` are not bundled, and a `data/<folder>` segment
anywhere outside the data roots (a copy under `C:\work\data\uk\`) is not bundled. Case is ignored on Windows and macOS
and respected elsewhere. `ContentHash` is the SHA-256 of the file's bytes with every CR LF read as LF, so a Windows and a
Linux checkout of the same data hash alike. A root with no manifest, or one that cannot be parsed, has nothing bundled
under it, so the licence check runs. A file added to a shipped folder, or a shipped file edited, makes the folder a user
network. The GB reduced model lives in `data/uk` (the case id `gb-reduced` is an alias of `uk`).

The web catalogue applies the same rule to its file-backed entries: a JSON entry is bundled when its id is in
`JsonCaseIds` (`gb-synthetic`, `gb-reduced`, `gb-full`; `IsBundledJsonCase` is an exact, case-sensitive id match) and
its folder passes `IsBundledPath`; a MATPOWER entry when its file passes `IsBundledPath`. Built-in cases never call a
file loader and are exempt by construction.

## Parameters and defaults

| Name | Value | Unit | Meaning | Source symbol |
|---|---|---|---|---|
| Free bus cap | 1,500 | buses | Personal tier and the free default | `Entitlement.FreeBusLimit` |
| Token prefix | `GSK1 ` | text | cosmetic, optional on parse | `LicenceKey.Prefix` |
| Magic | `GSK1` | 4 bytes | first four blob bytes | `LicenceKey.Magic` |
| Container version | 1 | byte | blob offset 4 | `LicenceKey.Version` (private constant) |
| Header length | 7 | bytes | magic + version + payloadLen | `LicenceKey.HeaderLen` (private constant) |
| Payload length cap | 65,535 | bytes | u16 length field | `ushort.MaxValue` in `LicenceKey.Encode` |
| Signature length | 64 | bytes | P-256 IEEE-P1363 `r \|\| s` | `LicenceKey.SignatureLen` (private constant) |
| Schemas | `v1` (8 fields), `v2` (9 fields) | | payload schema, `v2` written | `SchemaV1`, `SchemaV2`, `FieldCountV1`, `FieldCountV2` in `LicenceKey` (private constants) |
| Legacy major version | 1 | | what a `v1` key stands for | `LicenceKey.LegacyMajorVersion` |
| Environment variable | `GRIDSIM_LICENCE` | | key for one run | `LicenceGate.EnvVar` |
| File extension | `.gridsim` | | one key per file | `LicenceGate.FileExtension` |
| Licence file | `GridSim/licence.gridsim` under local application data | path | where `Install` writes | `LicenceGate.LicenceFilePath` |
| Web setting | `Licence:Key` (`Licence__Key`) | | explicit token for the web host | `GridSim.Web/Program.cs` |
| Curve and hash | ECDSA P-256, SHA-256 | | signature scheme | `LicenceKey.Encode`, `TryParse` |
| Public key | 91-byte SubjectPublicKeyInfo, base64 below | | verifier | `LicencePublicKey.SpkiBase64` |
| Contact | in every denial message | | | `LicenceGate.ContactEmail` |

The embedded public key, which is public by construction:

```
MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEsufP7z424mRhSPiTqnzsJ6VZSdMT8RheAaBiPJ7q6hRTmNYDvkG2fy6RtT7lhThepf9M5C+a1cy6d8o3VnsKYQ==
```

It decodes to the 26-byte DER header of a P-256 key followed by the 65-byte uncompressed point. To rotate keys, the issuing
tool makes a new pair, the new SPKI replaces the constant, and the build is remade; every key signed by the old private
key then fails with `licence signature is invalid`.

## Invariants

| Invariant | Test |
|---|---|
| Every field survives sign and verify | `LicensingTests.Round_trips_every_field_through_sign_and_verify` |
| A key is one `GSK1 ` line | `LicensingTests.Key_looks_like_an_ssh_key_line` |
| An organisation name containing `\|` round-trips | `LicensingTests.An_org_name_containing_a_pipe_round_trips` |
| A tampered key fails verification; a key from another signer is rejected | `LicensingTests.A_tampered_key_fails_verification`, `A_key_from_a_different_signer_is_rejected` |
| Garbage input is refused without throwing | `LicensingTests.Garbage_input_is_rejected_without_throwing` |
| Flipping the low bit of each byte of a key is rejected | `LicensingReleaseTests.Every_single_byte_tamper_is_rejected`, `LicenceMajorVersionTests.Every_single_byte_tamper_of_a_v2_key_is_rejected` |
| The embedded key is a real P-256 public key that exports to exactly the embedded bytes and cannot sign | `LicensingReleaseTests.Embedded_production_key_is_a_real_p256_public_key` |
| The production verifier refuses a key from any other signer | `LicensingReleaseTests.Production_verifier_refuses_a_key_from_any_other_signer` |
| A foreign key in the environment falls back to free with a visible note | `LicensingReleaseTests.A_foreign_key_from_the_environment_falls_back_to_free_with_a_visible_note` |
| Resolution order is explicit, environment, file | `LicensingTests.Resolution_precedence_is_explicit_then_env_then_file` |
| No key anywhere gives the free default with no note; a rejected key gives free with a note | `LicensingTests.With_no_key_anywhere_resolution_falls_back_to_free`, `A_present_but_rejected_key_falls_back_to_free_with_a_note` |
| A refused key does not fall through to the next source | `LicenceMajorVersionTests.A_refused_key_does_not_fall_through_to_the_next_source` |
| The bus limit applies at the boundary; unlimited tiers allow any size; bundled networks bypass the limit | `LicensingTests.Bus_limit_is_enforced_at_the_boundary`, `Unlimited_tier_allows_any_size`, `Bundled_networks_bypass_the_limit_even_when_over` |
| The free default is Personal, 1,500 buses, no expiry | `LicensingTests.Free_default_is_personal_1500_and_never_expires` |
| The expiry day is valid and the day after falls back to the free tier, naming the expiry | `LicensingReleaseTests.Expiry_day_itself_is_valid_and_the_day_after_falls_back_to_free`, `LicensingTests.Expired_licence_is_denied_valid_one_is_allowed` |
| `Enforce` throws `LicenceException` with the user-facing message | `LicensingReleaseTests.Enforce_throws_a_licence_exception_with_the_user_facing_message` |
| The licence file is `GridSim/licence.gridsim` under `LocalApplicationData`; the test also checks the Windows and Linux forms, but only on the OS it runs on | `LicensingReleaseTests.Licence_file_lives_in_the_per_user_data_folder_on_every_os` |
| Base64url round-trips every length, never emits `+`, `/` or `=`, and refuses malformed input | `LicensingReleaseTests.Base64url_round_trips_every_length_and_never_emits_unsafe_characters`, `Base64url_refuses_malformed_input_with_a_format_exception` |
| Every tier round-trips through its wire code | `LicensingReleaseTests.Every_tier_round_trips_through_its_wire_code` |
| The build major version is the first component of the release number; an unreadable version fails loud | `LicenceMajorVersionTests.The_build_major_version_is_the_first_component_of_the_release_number` |
| An issued key carries its major version signed; one with no major version cannot be issued | `LicenceMajorVersionTests.An_issued_key_carries_its_major_version_signed_in_a_v2_payload`, `A_key_for_no_major_version_cannot_be_issued` |
| A 1.x key is accepted by a 1.x build and refused by a 2.x build naming both; a newer key is refused by an older build; the rule is exact equality | `LicenceMajorVersionTests.A_1x_key_is_accepted_by_a_1x_build_and_refused_by_a_2x_build_naming_both`, `A_key_for_a_newer_line_is_refused_by_an_older_build_too`, `FitsMajorVersion_is_exact_equality` |
| A gate built directly from another line's key applies the free tier | `LicenceMajorVersionTests.A_gate_built_directly_from_another_lines_key_applies_the_free_tier` |
| `Install` refuses a key it cannot verify with a `LicenceException` and leaves the licence file untouched | `LicenceMajorVersionTests.Installing_a_key_for_another_line_fails_with_the_reason_and_writes_nothing` |
| A legacy `v1` key reads as 1.x | `LicenceMajorVersionTests.A_legacy_v1_key_is_a_1x_key_it_works_on_1x_and_is_refused_on_2x` |
| Malformed schema and field counts are refused, naming why; editing the major version breaks the signature | `LicenceMajorVersionTests.Malformed_schema_and_field_counts_are_refused_naming_why`, `Editing_the_major_version_breaks_the_signature` |
| The shipped GB full case is bundled; every shipped folder resolves bundled; user paths, near-miss segments and null are not | `BundledNetworksTests.The_shipped_gb_full_case_is_bundled`, `Every_shipped_network_folder_on_disk_resolves_as_bundled`, `User_paths_are_not_bundled` |
| Matching ignores case and separator; JSON case ids are exact | `BundledNetworksTests.Matching_ignores_case_and_separator`, `Json_case_ids_are_exact` |
| No front end keeps its own bundled-folder list | `BundledNetworksTests.No_front_end_keeps_its_own_bundled_folder_list` |

Every key in these tests is signed by a throw-away key pair; the production private key is never involved. Because
`Install` verifies against the embedded production key, the test named `Installing_a_key_for_another_line_...` fails at
the signature check. It asserts only the exception and an unchanged file; the reason text is not asserted, and the
major-version branch of `Install` is not covered by any test. The major-version rule itself is pinned through `Resolve`
and `Check`, which take the build's major version as a test seam.

## Worked example

A key was issued for a test holder with a throw-away key pair (`user@example.com`, `Example Ltd`, tier `code`, unlimited,
issued 2026-10-04, expiring 2027-10-03, licence id `doc001`, major version 1) and decoded field by field. ECDSA signing is
randomised, so the signature, and with it the token, differs on every run; the payload does not.

```
key:
GSK1 R1NLMQEAO3YyfHVzZXJAZXhhbXBsZS5jb218RXhhbXBsZSBMdGR8Y29kZXwwfDIwNzMwfDIxMDk0fGRvYzAwMXwxKR6JdnL1Ik24gkxihZ6WqNs8JyP3SjhB8912i37GIBP_3-kdz25f3TC0-aiZ1mwhQUm9Cta7f3H8zPNzONIXhQ
key characters: 179
blob bytes: 130
magic        : GSK1 (47534B31)
version      : 1
payloadLen   : 59 (003B)
payload      : v2|user@example.com|Example Ltd|code|0|20730|21094|doc001|1
signature    : 64 bytes, starts 291E897672F5224D...
  field 0: schema       = v2
  field 1: email        = user@example.com
  field 2: org          = Example Ltd
  field 3: type         = code
  field 4: buslimit     = 0
  field 5: issuedDays   = 20730
  field 6: expiresDays  = 21094
  field 7: licenceId    = doc001
  field 8: majorVersion = 1
issued  20730 days after 1970-01-01 = 2026-10-04
expires 21094 days after 1970-01-01 = 2027-10-03
signed bytes: 66 (header 7 + payload 59); signature 64; total 130
verify with the signer's public key: True
status line: Commercial Code * Example Ltd * unlimited buses * expires 2027-10-03 * for GridSim 1.x * id doc001
verify with the embedded GridSim key: False (licence signature is invalid (key was altered or not issued by GridSim))
one bit flipped in byte 20: False (licence signature is invalid (key was altered or not issued by GridSim))
truncated key: False (licence key too short)
not a key: False (licence key is not valid base64)
fits a 1.x build: True
fits a 2.x build: False
reason: this key was issued for GridSim 1.x and this build is major version 2 (GridSim 2.x); a licence covers every release of the major version it was issued for (LICENSE.md section 1), so GridSim 2.x needs a 2.x key: gdatooling@proton.me
```

The capture comes from a scratch program that calls the public Core API (`LicenceKey.Encode`, `TryParse`,
`LicenceGate.Describe`, `LicenceGate.FitsMajorVersion`); the `$` header line and exit line of the capture file are left
out here. The program is not kept in the repository and its throw-away private key was discarded, and ECDSA signing is
randomised, so the token and the lines that depend on the signer's key (`verify with the signer's public key: True`, the
status line, the fit lines) cannot be regenerated. Everything deterministic can be recomputed from
`docs/_captures/licence/inputs/test-key.gridsim`: the lengths, the payload, the field values and the dates. The lines
against the embedded key are reproduced by `gridsim licence status` and `gridsim licence import` with that file. Reading it against the layout:

1. The token is `GSK1 ` plus 174 base64url characters, 179 characters in all. 130 bytes encode to 174 characters
   (43 groups of three bytes and one byte over); the decoder pads to 176 with `==` and recovers the 130-byte blob.
2. Bytes 0-3 are `47 53 4B 31`, the magic; byte 4 is `01`, the container version.
3. Bytes 5-6 are `00 3B`: the payload is 59 bytes. `v2|user@example.com|Example Ltd|code|0|20730|21094|doc001|1` is
   nine fields of 2, 16, 11, 4, 1, 5, 5, 6 and 1 characters plus eight `|` separators = 59 characters, all ASCII, so 59 bytes.
4. The signed region is 7 + 59 = 66 bytes, the signature occupies bytes 66 to 129, and 66 + 64 = 130 is the blob length.
   The length check in step 7 of the algorithm passes only for that exact total.
5. `buslimit` 0 reads as unlimited. `issuedDays` 20730 is 20,730 days after 1970-01-01, which is 2026-10-04, and
   `expiresDays` 21094 is 364 days later, 2027-10-03. The key is valid on 2027-10-03 and expired on 2027-10-04.
6. Verified against the signer's own public key the key is accepted and `Describe` renders the status line. Verified against
   the embedded GridSim key it is refused at step 8, which is what makes it a test key. One flipped bit inside the payload
   fails at step 8; truncation fails at step 4 and a non-key at step 3.
7. The major-version field is 1, so the gate accepts the key on a 1.x build and refuses it on a 2.x build with the
   reason shown.

The gate's decisions for the same kinds of entitlement:

```
free, 1500 buses : True
free, 1501 buses : False
This network has 1,501 buses; your Personal licence allows 1,500. A larger or commercial licence removes the limit: gdatooling@proton.me
free, 3539 buses, bundled: True
lapsed status: Commercial Code * Example Ltd * unlimited buses * EXPIRED 2025-12-31 * for GridSim 1.x * id doc002
lapsed, 1500 buses: True
lapsed, 9000 buses: False
Your Commercial Code licence expired on 2025-12-31, so the free Personal tier applies: this network has 9,000 buses and the free tier allows 1,500. Renew or ask about a new term: gdatooling@proton.me
academic status: Academic * user@example.com * 5,000 buses * expires 2027-10-03 * for GridSim 1.x * id doc003
This network has 5,001 buses; your Academic licence allows 5,000. A larger or commercial licence removes the limit: gdatooling@proton.me
```

The first and third lines are check 1 and the free-tier rule; the lapsed key is allowed at 1,500 buses and refused at 9,000
by step 4; the Academic key with a 5,000 cap is refused at 5,001 by step 5.

The status lines in these captures, and in the two above and below, are from an earlier build, in two ways. The fields
were separated by a middle dot (shown here as `*`); `Describe` now separates them with an ASCII ` | ` (`LicenceGate.Sep`).
And an expired key's line named its own lapsed limit; `Describe` now prints the limit `Check` applies, so a key that is
expired, not yet valid or for another major version shows `1,500 buses (the free tier applies)` (the lapsed line reads
`Commercial Code | Example Ltd | 1,500 buses (the free tier applies) | EXPIRED 2025-12-31 | for GridSim 1.x | id doc002`),
and a key dated in the future shows `NOT YET VALID (starts <date>)` in place of its expiry. The decisions and messages
are unchanged; the captures have not been retaken.

Captured from the command line, a rejected key reaches the same free-tier gate, here with the one-line note:

```
$ gridsim licence status
Personal * unregistered * 1,500 buses * no expiry  (licence key from GRIDSIM_LICENCE rejected: licence signature is invalid (key was altered or not issued by GridSim))
licence file: $LOCALAPPDATA\GridSim\licence.gridsim
[exit 0]
```

The environment variable in that run held the test key above. Captures: `docs/_captures/licence/key-decode.txt`,
`gate-decisions.txt` and `status-foreign-key.txt`. Timings do not appear; the key bytes differ per run, the payload does not.

## Source map

| File | Type | Responsibility |
|---|---|---|
| `src/GridSim.Core/Licensing/LicenceKey.cs` | `LicenceKey` | `Encode` (signer side), `TryParse` (verify and decode, both overloads), payload build, parse and escaping. |
| `src/GridSim.Core/Licensing/LicencePublicKey.cs` | `LicencePublicKey` | The embedded SPKI constant and the `PLACEHOLDER` sentinel. |
| `src/GridSim.Core/Licensing/Entitlement.cs` | `Entitlement` | A verified licence: fields, `MajorVersion`, `IsExpired`, `IsUnlimited`, `EffectiveBusLimit`, `FreeDefault`, `FreeBusLimit`. |
| `src/GridSim.Core/Licensing/LicenceTier.cs` | `LicenceTier`, `LicenceTierCodes` | Tier enum, wire codes and display names. |
| `src/GridSim.Core/Licensing/LicenceGate.cs` | `LicenceGate`, `GateResult` | `Resolve`, `Check`, `Enforce`, `Describe`, `Install`, `ImportFromFile`, `FitsMajorVersion`, file location, token extraction. |
| `src/GridSim.Core/Licensing/BundledNetworks.cs` | `BundledNetworks` | The one bundled-path and bundled-case-id rule. |
| `src/GridSim.Core/Licensing/Base64Url.cs` | `Base64Url` | Unpadded URL-safe base64. |
| `src/GridSim.Core/Licensing/LicenceException.cs` | `LicenceException` | The user-facing failure. |
| `src/GridSim.Core/BuildInfo.cs` | `BuildInfo.MajorVersion`, `MajorOf` | The build's major version. |
| `src/GridSim.Cli/Gated.cs` | `Gated` | The CLI's model-load choke point and exit 2. |
| `src/GridSim.Cli/LicenceCli.cs` | `LicenceCli` | `licence status`, `path`, `import` and `--importLicence`. |
| `src/GridSim.Cli/Program.cs` | top-level | Consumes `--importLicence`, resolves the gate, routes the `licence` verb. |
| `src/GridSim.Web/Program.cs`, `Model/CaseCatalog.cs`, `Api/RestApi.cs` | | Web resolution with `Licence:Key`, enforcement, HTTP 403. |
| `src/GridSim.Wpf/App.xaml.cs`, `MainWindow.xaml.cs` | | Desktop resolution, title bar, load gate. |
| `src/GridSim.Core/IO/Json/CaseEditing/CaseSession.cs`, `SaveGate.cs` | | Case-editor gate at open and at save. |
| `tests/GridSim.Tests/LicensingTests.cs`, `LicensingReleaseTests.cs`, `LicenceMajorVersionTests.cs`, `BundledNetworksTests.cs` | | The invariants above. |

## Limits

- A key is not bound to a machine or a user, and no issued key can be revoked short of rotating the signing key.
- Expiry compares against the local UTC clock, so a clock set back extends a key.
- The embedded public key is a constant: replacing it, or the gate, in a rebuilt binary defeats the check, and only the
  licence terms forbid that.
- A model's bus count is checked when it loads; the case editor is the only program that checks again at save.
- The macOS licence file location is whatever .NET returns for `LocalApplicationData`. The macOS branch of
  `Licence_file_lives_in_the_per_user_data_folder_on_every_os` (and `Doc13LicenceTests.D205_...`) expects a path under
  `Library/Application Support`; since D-DOC-205 the release workflow runs both tests on its macOS runner, beside the
  published-output tests. The CI test matrix itself is Linux and Windows only.
- Since D-DOC-086 (October 2026) `Entitlement.Issued` is checked against today's UTC date (`IsNotYetValid`): a key dated
  in the future gets the free tier, as an expired key does, until its issue day (inclusive). Like expiry, this trusts
  the local clock. Since D-DOC-152 `Describe` shows the bus limit that applies (`1,500 buses (the free tier applies)`)
  for an expired, not-yet-valid or other-major key, instead of the key's own limit.

## See also

- [Chapter 4, Licensing and activation](<../manual/0004 Licensing and activation.md>): installing a key and what each
  program prints.
- [The `licence` verb in the CLI reference](<../manual/0019 CLI reference.md#licence>).
- [Chapter 35, Deployment and security](<0035 Deployment and security.md>): the web host's licence resolution.
- `LICENSE.md`, the governing terms.
