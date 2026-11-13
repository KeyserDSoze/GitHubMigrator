param([int]$Port = 8080, [string]$SeedPath = 'C:\OEM\seed\default-seed.json')
# Populates a fresh Azure DevOps Server from the same seed the simulator uses (tools/AdoSim/seed).
# Every item is created best-effort: a failure is logged and the rest continues, so a partial estate
# is still usable. Not covered here (REST cannot do it): TFVC branch content -> use the simulator.
$ErrorActionPreference = 'Stop'
$seed = Get-Content $SeedPath -Raw | ConvertFrom-Json
$collection = $seed.collection
$base = "http://localhost:$Port/tfs/$collection"
$api = '7.0'
$labPassword = 'Lab#User2026!'
$adminUser = "$env:COMPUTERNAME\$env:USERNAME"
$work = 'C:\OEM\seed-work'; New-Item -ItemType Directory -Force $work | Out-Null
$gitExe = 'C:\Program Files\Git\cmd\git.exe'
$failures = New-Object System.Collections.Generic.List[string]

function Try-Step([string]$Name, [scriptblock]$Body) {
    try { & $Body } catch { $failures.Add("$Name : $($_.Exception.Message)"); Write-Warning "$Name failed: $($_.Exception.Message)" }
}
function Rest($Method, $Url, $Body = $null, [string]$ContentType = 'application/json') {
    $args = @{ Method = $Method; Uri = $Url; UseDefaultCredentials = $true; ContentType = $ContentType }
    if ($null -ne $Body) { $args.Body = if ($Body -is [string]) { $Body } else { $Body | ConvertTo-Json -Depth 12 } }
    Invoke-RestMethod @args
}
function Git([string]$Dir, [hashtable]$Env, [string[]]$Arguments) {
    $saved = @{}; foreach ($k in $Env.Keys) { $saved[$k] = [Environment]::GetEnvironmentVariable($k); [Environment]::SetEnvironmentVariable($k, $Env[$k]) }
    try { & $gitExe -C $Dir @Arguments; if ($LASTEXITCODE -ne 0) { throw "git $($Arguments -join ' ') failed" } }
    finally { foreach ($k in $saved.Keys) { [Environment]::SetEnvironmentVariable($k, $saved[$k]) } }
}
function Stamp([int]$Day) { $d = "2024-01-{0:00}T10:00:00Z" -f $Day; @{ GIT_AUTHOR_NAME = 'Sim Author'; GIT_AUTHOR_EMAIL = 'sim@example.test'; GIT_COMMITTER_NAME = 'Sim Author'; GIT_COMMITTER_EMAIL = 'sim@example.test'; GIT_AUTHOR_DATE = $d; GIT_COMMITTER_DATE = $d } }

# --- local users (ADO Server resolves Windows accounts) -------------------------------------
foreach ($u in $seed.users) {
    Try-Step "user $($u.name)" {
        if (-not (Get-LocalUser -Name $u.name -ErrorAction SilentlyContinue)) {
            New-LocalUser -Name $u.name -FullName $u.display -Password (ConvertTo-SecureString $labPassword -AsPlainText -Force) -PasswordNeverExpires | Out-Null
        }
    }
}

# --- projects ---------------------------------------------------------------------------------
$agileTemplate = 'adcc42ab-9882-485e-a3ed-7678f01f66bc'
foreach ($p in $seed.projects) {
    Try-Step "project $($p.name)" {
        $existing = (Rest Get "$base/_apis/projects?api-version=$api").value | Where-Object { $_.name -eq $p.name }
        if ($existing) { return }
        $body = @{ name = $p.name; description = 'AdoSim seed'; capabilities = @{
            versioncontrol = @{ sourceControlType = $p.sourceControl }
            processTemplate = @{ templateTypeId = $agileTemplate } } }
        $op = Rest Post "$base/_apis/projects?api-version=$api" $body
        for ($i = 0; $i -lt 60; $i++) {
            Start-Sleep -Seconds 3
            $state = (Invoke-RestMethod $op.url -UseDefaultCredentials).status
            if ($state -eq 'succeeded') { return }
            if ($state -in 'failed', 'cancelled') { throw "project creation $state" }
        }
        throw 'project creation timed out'
    }
}

# --- git repositories + content -----------------------------------------------------------------
function New-RepoContent($spec, [string]$dir) {
    if (Test-Path $dir) { Remove-Item -Recurse -Force $dir }
    New-Item -ItemType Directory -Force $dir | Out-Null
    Git $dir @{} @('init', '-b', $spec.defaultBranch ?? 'main')
    Git $dir @{} @('config', 'core.autocrlf', 'false')
    $commits = if ($null -ne $spec.commits) { $spec.commits } else { 3 }
    for ($i = 1; $i -le $commits; $i++) {
        Set-Content "$dir\README.md" "# $($spec.name)`nrevision $i`n" -NoNewline:$false
        New-Item -ItemType Directory -Force "$dir\src" | Out-Null
        Set-Content "$dir\src\file$i.txt" "$($spec.name) content $i"
        if ($i -eq 1 -and $spec.files) {
            foreach ($f in $spec.files.PSObject.Properties) {
                $full = Join-Path $dir $f.Name.TrimStart('/'); New-Item -ItemType Directory -Force (Split-Path $full) | Out-Null
                Set-Content $full $f.Value -NoNewline
            }
        }
        Git $dir @{} @('add', '-A'); Git $dir (Stamp $i) @('commit', '-m', "Commit $i of $($spec.name)")
    }
    $default = if ($spec.defaultBranch) { $spec.defaultBranch } else { 'main' }
    $day = $commits + 1
    foreach ($b in @($spec.branches)) {
        if (-not $b) { continue }
        Git $dir @{} @('checkout', '-q', '-b', $b, $default)
        Set-Content "$dir\src\branch.txt" $b
        Git $dir @{} @('add', '-A'); Git $dir (Stamp ($day++ % 28 + 1)) @('commit', '-m', "Work on $b")
    }
    Git $dir @{} @('checkout', '-q', $default)
    foreach ($t in @($spec.tags)) { if ($t) { Git $dir @{} @('tag', $t) } }
    foreach ($t in @($spec.annotatedTags)) { if ($t) { Git $dir (Stamp ($day % 28 + 1)) @('tag', '-a', $t, '-m', "Release $t") } }
    for ($i = 0; $i -lt [int]$spec.extraBranches; $i++) { Git $dir @{} @('branch', ('bulk/branch-{0:0000}' -f $i)) }
    for ($i = 0; $i -lt [int]$spec.extraTags; $i++) { Git $dir @{} @('tag', ('bulk-tag-{0:0000}' -f $i)) }
}

$repoIds = @{}
foreach ($p in $seed.projects | Where-Object { $_.sourceControl -eq 'Git' }) {
    $projectRepos = (Rest Get "$base/$([uri]::EscapeDataString($p.name))/_apis/git/repositories?api-version=$api").value
    foreach ($r in @($p.repos)) {
        Try-Step "repo $($p.name)/$($r.name)" {
            $repo = $projectRepos | Where-Object { $_.name -eq $r.name }
            if (-not $repo) {
                $repo = Rest Post "$base/$([uri]::EscapeDataString($p.name))/_apis/git/repositories?api-version=$api" @{ name = $r.name }
            }
            $repoIds["$($p.name)/$($r.name)"] = $repo.id
            if ($r.empty -or $r.disabled) { return }
            $dir = Join-Path $work ($repo.id)
            New-RepoContent $r $dir
            # Integrated Windows auth (SSPI) with the account running this script: no password needed.
            $remote = "http://localhost:$Port/tfs/$collection/$([uri]::EscapeDataString($p.name))/_git/$([uri]::EscapeDataString($r.name))"
            Git $dir @{} @('-c', 'http.emptyAuth=true', '-c', 'http.sslBackend=schannel', 'push', '--mirror', '--quiet', $remote)
        }
    }
    # The project's auto-created default repository is not part of the seed.
    $unwanted = $projectRepos | Where-Object { $_.name -eq $p.name -and -not ($p.repos | Where-Object { $_.name -eq $p.name }) }
    foreach ($d in $unwanted) { Try-Step "drop default repo $($p.name)" { Rest Delete "$base/$([uri]::EscapeDataString($p.name))/_apis/git/repositories/$($d.id)?api-version=$api" | Out-Null } }
}

# --- group membership ---------------------------------------------------------------------------
$tfsSecurity = Get-ChildItem 'C:\Program Files\Azure DevOps Server*\Tools\TfsSecurity.exe' -ErrorAction SilentlyContinue | Select-Object -First 1
foreach ($g in $seed.groups) {
    foreach ($member in $g.members) {
        Try-Step "membership $member -> $($g.name)" {
            $who = if ($member -like '*\*') { $member } else { "n:$env:COMPUTERNAME\$member" }
            & $tfsSecurity.FullName /g+ $g.name $who /collection:$base | Out-Null
        }
    }
}

# --- work items -----------------------------------------------------------------------------------
$itemIds = @{}
foreach ($w in $seed.workItems) {
    Try-Step "work item $($w.id)" {
        $patch = @(
            @{ op = 'add'; path = '/fields/System.Title'; value = $w.title },
            @{ op = 'add'; path = '/fields/System.Description'; value = [string]$w.description }
        ) | ConvertTo-Json -Depth 6
        $type = [uri]::EscapeDataString($w.type)
        $created = Rest Post "$base/$([uri]::EscapeDataString($w.project))/_apis/wit/workitems/`$$type?api-version=$api" $patch 'application/json-patch+json'
        $itemIds[[int]$w.id] = $created.id
    }
}
foreach ($w in $seed.workItems) {
    foreach ($l in @($w.links)) {
        if (-not $l -or $l.kind -notin 'child', 'parent', 'related') { continue }
        Try-Step "link $($w.id)->$($l.to)" {
            $rel = @{ child = 'System.LinkTypes.Hierarchy-Forward'; parent = 'System.LinkTypes.Hierarchy-Reverse'; related = 'System.LinkTypes.Related' }[$l.kind]
            $patch = ConvertTo-Json -Depth 6 @(@{ op = 'add'; path = '/relations/-'; value = @{ rel = $rel; url = "$base/_apis/wit/workItems/$($itemIds[[int]$l.to])" } })
            Rest Patch "$base/_apis/wit/workitems/$($itemIds[[int]$w.id])?api-version=$api" $patch 'application/json-patch+json' | Out-Null
        }
    }
}

# --- lab PAT (the migrator authenticates with a PAT, never Windows credentials) -------------------
Try-Step 'lab PAT' {
    $end = (Get-Date).AddDays(90).ToUniversalTime().ToString('o')
    $body = @{ displayName = 'lab-migrator'; scope = 'app_token'; validTo = $end; allOrgs = $false }
    $token = $null
    foreach ($url in @("http://localhost:$Port/tfs/_apis/tokens/pats?api-version=7.0-preview.1", "$base/_apis/tokens/pats?api-version=7.0-preview.1")) {
        try { $token = (Rest Post $url $body).patToken.token; if ($token) { break } } catch { }
    }
    if (-not $token) { throw 'PAT API not available: create a PAT manually (User settings > Personal access tokens, scopes: Code read, Project read, Graph read, Security manage).' }
    Set-Content C:\OEM\lab-pat.txt $token
}
if ($failures.Count) { Set-Content C:\OEM\seed-failures.txt $failures; Write-Warning "$($failures.Count) seed steps failed; see C:\OEM\seed-failures.txt" }
