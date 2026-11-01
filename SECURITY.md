# Security policy

This policy covers GridSim: the source in this repository and the builds published on its
[Releases](https://github.com/MarkS0485/GridSim/releases) page, including the components those builds ship
(the bundled .NET runtime and the NuGet dependencies).

## Supported versions

| Version | Security fixes |
|---|---|
| The latest release on the Releases page | Yes |
| Any earlier release | No: a fix ships only in a new release |

The builds are self-contained and carry the .NET runtime, so a .NET runtime security fix also reaches you only in a
new GridSim release.

## Reporting a vulnerability

Report it privately. Do not open a public issue, pull request or discussion for it.

- **Preferred:** GitHub private vulnerability reporting, from this repository's **Security** tab,
  **Report a vulnerability** (<https://github.com/MarkS0485/GridSim/security/advisories/new>). The report stays
  private between you and the maintainer and becomes the draft security advisory.
- **Alternative:** email **gdatooling@proton.me**.

Please include the GridSim version (`gridsim --version`), the operating system, the steps or input that show the
problem, and what an attacker could do with it. If you would like to be credited in the advisory, say how.

## Assessment

Every report is assessed objectively, from the facts of the vulnerability as it affects GridSim, using the CVSS
**base** metrics: CVSS v4.0, and CVSS v3.1 as well. The assessment records, in the security advisory:

- the CVSS vector string and base score for each version used;
- the date and time (UTC) the assessment was recorded;
- a short statement of the reasoning for each metric that is not obvious from the report.

The base score is used because it describes the vulnerability itself; temporal, threat and environmental metrics
depend on each deployment and are left to the deployer. A score published elsewhere (for example by another CVE
numbering authority) is considered, and where the GridSim assessment differs, the reason is recorded.

A report that turns out not to be a vulnerability in GridSim is closed with the reason recorded.

## Remediation target

**Where the objective assessment gives a CVSS base score of 7.0 or above (in either version used), the target is
remediation within 48 hours of the assessment being recorded.** Remediation means a fixed GridSim release is
published on the Releases page and the security advisory is published with it.

Vulnerabilities assessed below 7.0 are fixed in a release in the normal way; no time target is stated for them.

## How fixes ship

1. The fix is made with a test that reproduces the vulnerability, and the CHANGELOG gains a **Security** entry that
   names the advisory id (`GHSA-...`).
2. A new release is published on the Releases page, with the CHANGELOG entry in its notes.
3. The repository security advisory is published. Where the vulnerability qualifies, a CVE id is requested through
   GitHub (a CVE numbering authority) when the advisory is published, and recorded in the advisory.
4. The automatic notice below goes out.

## Automatic notice to subscribers

Publishing a security release triggers notices automatically, through GitHub, with nothing to sign up to outside
GitHub:

- GitHub notifies everyone watching this repository for **Releases** that the fixed release is out.
- The [security notice workflow](.github/workflows/security-notice.yml) runs on publication and posts an
  announcement in the [Announcements](https://github.com/MarkS0485/GridSim/discussions/categories/announcements)
  category of Discussions, naming the advisory, its severity and score, the CVE id and the fixed release. GitHub
  notifies everyone watching for **Discussions**.

**To sign up:** on this repository choose **Watch**, then **Custom**, and tick **Releases** and **Discussions**
(or choose **All activity**). GitHub sends these notifications by email when email delivery is on in your GitHub
notification settings. Without a GitHub account, the release feed
<https://github.com/MarkS0485/GridSim/releases.atom> carries the same releases.

## Scope notes

- GridSim never reads present telemetry or predicts the future; that rule is a code invariant
  (`UnderstandingRestrictions.md`). A report showing a way to make it emit a grid claim newer than its horizon is in
  scope and treated as a vulnerability.
- Deploying parties operate their own hosts, networks and incident processes; the operator's runbook is
  `docs/assurance/incident-response.md`.
