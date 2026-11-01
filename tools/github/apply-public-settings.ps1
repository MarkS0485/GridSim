<#
.SYNOPSIS
    Applies the public GitHub presence settings to MarkS0485/GridSim: description, homepage, topics, wiki,
    Discussions, Pages (built by Actions), default workflow permissions, branch ruleset on main, and the free
    security features (secret scanning, push protection, Dependabot alerts, private vulnerability reporting).

.DESCRIPTION
    Dry run by default: it prints every call it would make and changes nothing. Pass -Apply to make the changes.
    After applying, it reads each setting back and fails loudly (exit 1) if any did not take.

    Requirements:
      * GitHub CLI (gh) on PATH, authenticated as an administrator of the repository. gh reads the token from the
        GH_TOKEN environment variable (or its own login); this script never reads, prints or stores a token.
      * Windows PowerShell 5.1 or PowerShell 7.

    Everything here uses features available on the GitHub Free plan for a public repository.

    Things the API cannot do, which this script prints as manual steps at the end:
      * Create the wiki's first page (the .wiki.git repository only exists after that, so the wiki-sync workflow
        cannot push until it is done).
      * Create or rename Discussions categories (there is no API for categories; GitHub seeds Announcements,
        General, Ideas, Polls, Q&A and Show and tell when Discussions is enabled).
      * Upload the social preview image (Settings > General > Social preview; 1280 x 640 PNG, from brand/).

    Idempotent: running it twice leaves the same state.

.PARAMETER Repo
    owner/name of the repository. Defaults to the public repository.

.PARAMETER Apply
    Make the changes. Without it, nothing is changed.

.PARAMETER SeedDiscussions
    With -Apply, also post one pinned-style welcome discussion in the Announcements category (skipped if a
    discussion with the same title already exists).

.EXAMPLE
    powershell -File tools/github/apply-public-settings.ps1                 # dry run
    powershell -File tools/github/apply-public-settings.ps1 -Apply          # make the changes
#>
[CmdletBinding()]
param(
    [string]$Repo = 'MarkS0485/GridSim',
    [switch]$Apply,
    [switch]$SeedDiscussions
)

# Stop on the first PowerShell error; native command failures are checked by hand through $LASTEXITCODE below.
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# --------------------------------------------------------------------------------------------------------------
# The settings themselves. Change them here, not in the calls further down.
# --------------------------------------------------------------------------------------------------------------

# GitHub limits the description to 350 characters. Plain ASCII.
$Description = 'A physics-first power-grid simulator in C# / .NET 10: AC power flow, contingency screening, ' +
               'state estimation, frequency and transient dynamics, faults, protection and harmonics. ' +
               'Deterministic to the bit and cross-validated against independent tools.'

# The Pages site built by .github/workflows/pages.yml.
$Homepage = 'https://marks0485.github.io/GridSim/'

# Topics: lower case, digits and hyphens, at most 50 characters each, at most 20 in all.
$Topics = @(
    'power-systems', 'power-grid', 'power-flow', 'load-flow', 'grid-simulation', 'state-estimation',
    'newton-raphson', 'contingency-analysis', 'transient-stability', 'short-circuit', 'harmonics',
    'frequency-response', 'electrical-engineering', 'energy', 'gb-grid', 'csharp', 'dotnet'
)

# The welcome discussion posted only with -SeedDiscussions.
$WelcomeTitle = 'Welcome to GridSim Discussions'
$WelcomeBody  = @'
This is the place for questions, ideas and results from using GridSim.

* Questions about installing or running GridSim: the Q&A category.
* Ideas for features or studies: the Ideas category.
* Studies or results you would like to share: Show and tell.

Before asking, the documentation site may already have the answer: https://marks0485.github.io/GridSim/

Bugs go in Issues. Security problems must not be posted here: please use the repository's private
vulnerability reporting (Security tab > Report a vulnerability).
'@

# --------------------------------------------------------------------------------------------------------------
# Helpers
# --------------------------------------------------------------------------------------------------------------

$script:Failures = New-Object System.Collections.Generic.List[string]

function Write-Step([string]$Text) { Write-Host ''; Write-Host "== $Text" -ForegroundColor Cyan }

# Runs gh with the given arguments. In a dry run it only prints them. Fails loudly on a non-zero exit code.
# -AllowExit lists exit codes that are expected and handled by the caller (the output is still returned).
function Invoke-Gh {
    param([string[]]$GhArgs, [switch]$ReadOnly, [int[]]$AllowExit = @())
    $shown = 'gh ' + ($GhArgs -join ' ')
    if (-not $Apply -and -not $ReadOnly) {
        Write-Host "  [dry run] $shown"
        return $null
    }
    Write-Host "  $shown"
    # Windows PowerShell 5.1 turns a native command's stderr into an error record; with 'Stop' that would abort
    # before the exit code is checked. Relax it inside this function only; the exit code check below fails loud.
    $ErrorActionPreference = 'Continue'
    $out = & gh @GhArgs 2>&1
    $code = $LASTEXITCODE
    if ($code -ne 0 -and ($AllowExit -notcontains $code)) {
        throw "gh failed (exit $code): $shown`n$out"
    }
    return $out
}

# Runs a read-only gh call whose non-zero exit is an expected answer (for example 404 = not configured).
# Returns an object with the exit code and the combined output; never throws on a non-zero exit.
function Get-GhProbe([string[]]$GhArgs) {
    $ErrorActionPreference = 'Continue'
    $out = & gh @GhArgs 2>&1
    return [pscustomobject]@{ Code = $LASTEXITCODE; Output = ($out | Out-String) }
}

# Writes a JSON body to a temporary UTF-8 file without a byte order mark (gh rejects a BOM), for gh api --input.
function New-JsonBody([object]$Object) {
    $path = [System.IO.Path]::GetTempFileName()
    $json = $Object | ConvertTo-Json -Depth 10 -Compress
    [System.IO.File]::WriteAllText($path, $json, (New-Object System.Text.UTF8Encoding $false))
    return $path
}

# Records a read-back mismatch; all mismatches are reported together at the end and make the script exit 1.
function Assert-Setting([string]$Name, $Actual, $Expected) {
    if ("$Actual" -ne "$Expected") {
        $script:Failures.Add("$Name is '$Actual', expected '$Expected'")
        Write-Host "  FAIL $Name = '$Actual' (expected '$Expected')" -ForegroundColor Red
    } else {
        Write-Host "  ok   $Name = '$Actual'" -ForegroundColor Green
    }
}

# --------------------------------------------------------------------------------------------------------------
# Preflight: gh present and authenticated, the repository exists and is public, we are an administrator.
# --------------------------------------------------------------------------------------------------------------

Write-Step "Preflight ($Repo; $(if ($Apply) { 'APPLY' } else { 'dry run, nothing will change' }))"
if (-not (Get-Command gh -ErrorAction SilentlyContinue)) { throw 'GitHub CLI (gh) is not on PATH.' }
$null = Invoke-Gh -ReadOnly -GhArgs @('auth', 'status')
$info = (Invoke-Gh -ReadOnly -GhArgs @('api', "repos/$Repo")) | Out-String | ConvertFrom-Json
if ($info.visibility -ne 'public') {
    throw "$Repo is '$($info.visibility)', not public. Pages, rulesets and the free security features in this script assume a public repository."
}
if (-not $info.permissions.admin) { throw "The authenticated account is not an administrator of $Repo." }
Write-Host "  $Repo is public and you are an administrator."

# --------------------------------------------------------------------------------------------------------------
# 1. Description, homepage, topics, wiki, Discussions.
# --------------------------------------------------------------------------------------------------------------

Write-Step 'Description, homepage, wiki and Discussions'
$null = Invoke-Gh -GhArgs @('repo', 'edit', $Repo, '--description', $Description, '--homepage', $Homepage,
                            '--enable-wiki', '--enable-discussions')

Write-Step 'Topics (replaces the whole topic list)'
$topicsBody = New-JsonBody @{ names = $Topics }
try {
    $null = Invoke-Gh -GhArgs @('api', '-X', 'PUT', "repos/$Repo/topics", '--input', $topicsBody)
} finally { Remove-Item $topicsBody -ErrorAction SilentlyContinue }

# --------------------------------------------------------------------------------------------------------------
# 2. Pages, built and deployed by GitHub Actions (.github/workflows/pages.yml).
# --------------------------------------------------------------------------------------------------------------

Write-Step 'Pages source = GitHub Actions'
# GET returns 404 (gh exit 1) when no Pages site exists yet: then create it, otherwise update it.
$pagesExists = $false
$probe = Get-GhProbe @('api', "repos/$Repo/pages")
if ($probe.Code -eq 0) { $pagesExists = $true }
elseif ($probe.Output -notmatch '404') { throw "Could not read the Pages configuration: $($probe.Output)" }
if ($pagesExists) {
    $null = Invoke-Gh -GhArgs @('api', '-X', 'PUT', "repos/$Repo/pages", '-f', 'build_type=workflow')
} else {
    $null = Invoke-Gh -GhArgs @('api', '-X', 'POST', "repos/$Repo/pages", '-f', 'build_type=workflow')
}

# --------------------------------------------------------------------------------------------------------------
# 3. Actions: the default GITHUB_TOKEN is read-only; each workflow asks for what it needs (least privilege).
# --------------------------------------------------------------------------------------------------------------

Write-Step 'Default workflow token permissions = read'
$null = Invoke-Gh -GhArgs @('api', '-X', 'PUT', "repos/$Repo/actions/permissions/workflow",
                            '-f', 'default_workflow_permissions=read', '-F', 'can_approve_pull_request_reviews=false')

# --------------------------------------------------------------------------------------------------------------
# 4. Security features (free for public repositories).
# --------------------------------------------------------------------------------------------------------------

Write-Step 'Secret scanning and push protection'
$secBody = New-JsonBody @{ security_and_analysis = @{
    secret_scanning                 = @{ status = 'enabled' }
    secret_scanning_push_protection = @{ status = 'enabled' }
} }
try {
    $null = Invoke-Gh -GhArgs @('api', '-X', 'PATCH', "repos/$Repo", '--input', $secBody)
} finally { Remove-Item $secBody -ErrorAction SilentlyContinue }

Write-Step 'Dependabot alerts'
# Alerts only. Dependabot security-update pull requests are left as they are: version updates are paused for the
# release in .github/dependabot.yml and updates are merged by hand.
$null = Invoke-Gh -GhArgs @('api', '-X', 'PUT', "repos/$Repo/vulnerability-alerts")

Write-Step 'Private vulnerability reporting'
$null = Invoke-Gh -GhArgs @('api', '-X', 'PUT', "repos/$Repo/private-vulnerability-reporting")

# --------------------------------------------------------------------------------------------------------------
# 5. Ruleset on the default branch: no deletion, no force push.
#    Pull requests are NOT required: main is the only branch and is pushed to directly.
#    Repository administrators may bypass, so a deliberate history reset at a release remains possible.
# --------------------------------------------------------------------------------------------------------------

Write-Step 'Ruleset "Protect main" (no deletion, no force push; admins may bypass)'
$rulesetName = 'Protect main'
$ruleset = @{
    name          = $rulesetName
    target        = 'branch'
    enforcement   = 'active'
    conditions    = @{ ref_name = @{ include = @('~DEFAULT_BRANCH'); exclude = @() } }
    rules         = @( @{ type = 'deletion' }, @{ type = 'non_fast_forward' } )
    bypass_actors = @( @{ actor_id = 5; actor_type = 'RepositoryRole'; bypass_mode = 'always' } )  # 5 = Admin
}
$existing = (Invoke-Gh -ReadOnly -GhArgs @('api', "repos/$Repo/rulesets")) | Out-String | ConvertFrom-Json
$match = @($existing | Where-Object { $_.name -eq $rulesetName })
if ($match.Count -gt 1) { throw "More than one ruleset is named '$rulesetName'; resolve that by hand first." }
$rsBody = New-JsonBody $ruleset
try {
    if ($match.Count -eq 1) {
        $null = Invoke-Gh -GhArgs @('api', '-X', 'PUT', "repos/$Repo/rulesets/$($match[0].id)", '--input', $rsBody)
    } else {
        $null = Invoke-Gh -GhArgs @('api', '-X', 'POST', "repos/$Repo/rulesets", '--input', $rsBody)
    }
} finally { Remove-Item $rsBody -ErrorAction SilentlyContinue }

# --------------------------------------------------------------------------------------------------------------
# 6. Optional: welcome discussion in Announcements.
# --------------------------------------------------------------------------------------------------------------

if ($SeedDiscussions) {
    Write-Step 'Seed: welcome discussion in Announcements'
    if (-not $Apply) {
        Write-Host "  [dry run] would post '$WelcomeTitle' in Announcements"
    } else {
        $owner, $name = $Repo.Split('/')
        $q = 'query($o:String!,$n:String!){repository(owner:$o,name:$n){id discussionCategories(first:25){nodes{id name}} discussions(first:50){nodes{title}}}}'
        $r = (Invoke-Gh -ReadOnly -GhArgs @('api', 'graphql', '-f', "query=$q", '-f', "o=$owner", '-f', "n=$name")) |
             Out-String | ConvertFrom-Json
        $repoNode = $r.data.repository
        $cat = @($repoNode.discussionCategories.nodes | Where-Object { $_.name -eq 'Announcements' })
        if ($cat.Count -ne 1) { throw 'No Announcements category found; is Discussions enabled?' }
        if (@($repoNode.discussions.nodes | Where-Object { $_.title -eq $WelcomeTitle }).Count -gt 0) {
            Write-Host '  already posted; skipped'
        } else {
            $m = 'mutation($r:ID!,$c:ID!,$t:String!,$b:String!){createDiscussion(input:{repositoryId:$r,categoryId:$c,title:$t,body:$b}){discussion{url}}}'
            $null = Invoke-Gh -GhArgs @('api', 'graphql', '-f', "query=$m", '-f', "r=$($repoNode.id)",
                                        '-f', "c=$($cat[0].id)", '-f', "t=$WelcomeTitle", '-f', "b=$WelcomeBody")
        }
    }
}

# --------------------------------------------------------------------------------------------------------------
# 7. Read everything back (apply only) and fail loudly on any mismatch.
# --------------------------------------------------------------------------------------------------------------

if ($Apply) {
    Write-Step 'Read-back'
    $info = (Invoke-Gh -ReadOnly -GhArgs @('api', "repos/$Repo")) | Out-String | ConvertFrom-Json
    Assert-Setting 'description'      $info.description $Description
    Assert-Setting 'homepage'         $info.homepage    $Homepage
    Assert-Setting 'has_wiki'         $info.has_wiki    $true
    Assert-Setting 'has_discussions'  $info.has_discussions $true
    Assert-Setting 'secret_scanning'  $info.security_and_analysis.secret_scanning.status 'enabled'
    Assert-Setting 'push_protection'  $info.security_and_analysis.secret_scanning_push_protection.status 'enabled'
    Assert-Setting 'topics'           (($info.topics | Sort-Object) -join ',') (($Topics | Sort-Object) -join ',')

    $pages = (Invoke-Gh -ReadOnly -GhArgs @('api', "repos/$Repo/pages")) | Out-String | ConvertFrom-Json
    Assert-Setting 'pages.build_type' $pages.build_type 'workflow'

    $wf = (Invoke-Gh -ReadOnly -GhArgs @('api', "repos/$Repo/actions/permissions/workflow")) | Out-String | ConvertFrom-Json
    Assert-Setting 'default_workflow_permissions' $wf.default_workflow_permissions 'read'

    # vulnerability-alerts answers 204 when enabled and 404 when disabled.
    $va = Get-GhProbe @('api', "repos/$Repo/vulnerability-alerts")
    if ($va.Code -ne 0 -and $va.Output -notmatch '404') { throw "Could not read Dependabot alerts: $($va.Output)" }
    Assert-Setting 'dependabot_alerts' ($(if ($va.Code -eq 0) { 'enabled' } else { 'disabled' })) 'enabled'

    $pvr = (Invoke-Gh -ReadOnly -GhArgs @('api', "repos/$Repo/private-vulnerability-reporting")) | Out-String | ConvertFrom-Json
    Assert-Setting 'private_vulnerability_reporting' $pvr.enabled $true

    $rs = @((Invoke-Gh -ReadOnly -GhArgs @('api', "repos/$Repo/rulesets")) | Out-String | ConvertFrom-Json |
            Where-Object { $_.name -eq $rulesetName })
    Assert-Setting "ruleset '$rulesetName'" $(if ($rs.Count -eq 1) { $rs[0].enforcement } else { 'missing' }) 'active'
}

# --------------------------------------------------------------------------------------------------------------
# Manual steps that have no API.
# --------------------------------------------------------------------------------------------------------------

Write-Step 'Manual steps (no API for these)'
Write-Host "  1. Wiki: open https://github.com/$Repo/wiki and create the first page (any text; the wiki-sync"
Write-Host '     workflow replaces it). Then run the "Wiki sync" workflow once from the Actions tab.'
Write-Host "  2. Discussions categories: review https://github.com/$Repo/discussions/categories (defaults are fine)."
Write-Host '  3. Social preview: Settings > General > Social preview, upload a 1280 x 640 PNG exported from brand/.'
Write-Host '  4. Pages: the first deploy happens on the next push to main, or run the "Docs site" workflow by hand.'

if ($script:Failures.Count -gt 0) {
    Write-Host ''
    Write-Host "$($script:Failures.Count) setting(s) did not take:" -ForegroundColor Red
    $script:Failures | ForEach-Object { Write-Host "  $_" -ForegroundColor Red }
    exit 1
}
Write-Host ''
Write-Host $(if ($Apply) { 'All settings applied and verified.' } else { 'Dry run complete; nothing was changed. Re-run with -Apply.' })
exit 0
