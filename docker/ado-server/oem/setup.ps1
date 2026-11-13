# Installs Azure DevOps Server Express unattended, then seeds it. Idempotent: every stage drops a
# marker in C:\OEM\state, so re-running (or a reboot) resumes where it stopped. Log: C:\OEM\setup.log
# NOTE: written from Microsoft's documented installer/TfsConfig behaviour; it has not been run against a
# live VM in this repository's CI. If a stage fails, read C:\OEM\setup.log and adjust that stage only.
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
Start-Transcript -Path C:\OEM\setup.log -Append | Out-Null
New-Item -ItemType Directory -Force C:\OEM\state | Out-Null

# Latest "Azure DevOps Server Express" EXE (fwlink from learn.microsoft.com/azure/devops/server/download).
$AdoExpressUrl = $env:ADO_EXPRESS_URL
if (-not $AdoExpressUrl) { $AdoExpressUrl = 'https://go.microsoft.com/fwlink/?LinkId=2330454' }
$Port = 8080

function Stage([string]$Name, [scriptblock]$Body) {
    $marker = "C:\OEM\state\$Name.done"
    if (Test-Path $marker) { Write-Host "== $Name (already done)"; return }
    Write-Host "== $Name"
    & $Body
    New-Item -ItemType File -Force $marker | Out-Null
}

Stage 'git' {
    $release = Invoke-RestMethod 'https://api.github.com/repos/git-for-windows/git/releases/latest' -Headers @{ 'User-Agent' = 'ado-lab' }
    $asset = $release.assets | Where-Object { $_.name -match '^Git-.*-64-bit\.exe$' } | Select-Object -First 1
    Invoke-WebRequest $asset.browser_download_url -OutFile C:\OEM\git-setup.exe
    Start-Process C:\OEM\git-setup.exe -ArgumentList '/VERYSILENT', '/NORESTART', '/SP-' -Wait
}

Stage 'download' {
    Invoke-WebRequest $AdoExpressUrl -OutFile C:\OEM\azuredevops-express.exe
}

Stage 'install-bits' {
    # Installs the product only; configuration is a separate, scriptable step (TfsConfig unattend).
    $p = Start-Process C:\OEM\azuredevops-express.exe -ArgumentList '/Full', '/Silent' -Wait -PassThru
    Write-Host "installer exit code: $($p.ExitCode)"
}

Stage 'configure' {
    $tfsConfig = Get-ChildItem 'C:\Program Files\Azure DevOps Server*\Tools\TfsConfig.exe' -ErrorAction SilentlyContinue |
        Sort-Object FullName -Descending | Select-Object -First 1
    if (-not $tfsConfig) { throw 'TfsConfig.exe not found: the installer stage did not complete.' }
    $ini = 'C:\OEM\ado-unattend.ini'
    & $tfsConfig.FullName unattend /create /type:NewServerBasic /unattendfile:$ini `
        "/inputs:InstallSqlExpress=True;SendFeedback=False;WebSiteVDirName=tfs;SiteBindings=http:*:$Port:"
    & $tfsConfig.FullName unattend /configure /unattendfile:$ini
    if ($LASTEXITCODE -ne 0) { throw "TfsConfig unattend failed with exit code $LASTEXITCODE" }
}

Stage 'firewall' {
    New-NetFirewallRule -DisplayName 'Azure DevOps Server' -Direction Inbound -Protocol TCP -LocalPort $Port -Action Allow | Out-Null
}

Stage 'wait-ready' {
    for ($i = 0; $i -lt 90; $i++) {
        try {
            Invoke-RestMethod "http://localhost:$Port/tfs/DefaultCollection/_apis/projects?api-version=7.0" -UseDefaultCredentials | Out-Null
            return
        } catch { Start-Sleep -Seconds 10 }
    }
    throw 'Azure DevOps Server did not answer within 15 minutes.'
}

Stage 'seed' { & C:\OEM\seed-ado.ps1 -Port $Port }

'ready' | Set-Content C:\OEM\ready.txt
if (Test-Path Z:\) { Copy-Item C:\OEM\ready.txt, C:\OEM\setup.log, C:\OEM\lab-pat.txt Z:\ -ErrorAction SilentlyContinue }
Stop-Transcript | Out-Null
