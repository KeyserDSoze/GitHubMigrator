param(
  [ValidateSet('up', 'lab', 'lab-services', 'down', 'reset', 'logs', 'ps', 'test', 'ado-server')]
  [string]$Action = 'up',
  [ValidateSet('local', 'test')]
  [string]$Mode = 'local'
)

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$envFile = Join-Path $root '.env.local'
if ($Mode -eq 'test') {
  $envFile = Join-Path $root '.env.test'
} elseif (-not (Test-Path $envFile)) {
  Write-Warning 'No .env.local found; using committed test values. Copy .env.local.example to .env.local to customize.'
  $envFile = Join-Path $root '.env.test'
}
if ($envFile -eq (Join-Path $root '.env.local') -and (Test-Path $envFile)) {
  $contents = [System.IO.File]::ReadAllText($envFile)
  if ($contents.Contains('LOCAL_ENCRYPTION_KEY=GENERATE_ON_FIRST_RUN')) {
    $bytes = New-Object byte[] 32
    $rng = [System.Security.Cryptography.RandomNumberGenerator]::Create()
    try { $rng.GetBytes($bytes) } finally { $rng.Dispose() }
    $key = [Convert]::ToBase64String($bytes)
    $contents = $contents.Replace('LOCAL_ENCRYPTION_KEY=GENERATE_ON_FIRST_RUN', "LOCAL_ENCRYPTION_KEY=$key")
    [System.IO.File]::WriteAllText($envFile, $contents)
    Write-Host 'Generated a unique local encryption key in .env.local.'
  }
}
$composeFile = Join-Path $root 'docker/docker-compose.yml'

function Invoke-Compose {
  & docker compose --env-file $envFile -f $composeFile @args
  if ($LASTEXITCODE -ne 0) { throw "Docker Compose failed (exit $LASTEXITCODE)" }
}

switch ($Action) {
  'up' {
    Invoke-Compose up -d --build postgres azurite observability ado-sim gh-sim llm-sim api frontend
    Invoke-Compose ps
  }
  { $_ -in 'lab', 'lab-services' } {
    # Whole lab: source = ado-sim (Azure DevOps Server, or lab-services: the Azure DevOps Services stand-in),
    # target = gh-sim (GitHub Enterprise Server shape).
    if ($Action -eq 'lab-services') {
      $env:ADO_SIM_FLAVOR = 'services'
      $env:ADO_SERVICES_CORE_ROOT = 'http://ado-sim:8080'
      $env:ADO_SERVICES_GRAPH_ROOT = 'http://ado-sim:8080/vssps'
      $env:ADO_SERVICES_GIT_ROOT = 'http://ado-sim:8080'
      $env:ADO_SERVICES_ALLOWED_HOSTS = 'ado-sim'
    }
    $env:GITHUB_API_ROOT = 'http://gh-sim:8080/api/v3'
    $env:GITHUB_WEB_ROOT = 'http://gh-sim:8080'
    $env:GITHUB_ALLOWED_HOSTS = 'gh-sim'
    # AI connections may point at llm-sim (http://llm-sim:8080/v1, key llm-sim-key, model gpt-sim).
    $env:AI_ALLOWED_HOSTS = 'llm-sim'
    Invoke-Compose up -d --build postgres azurite observability ado-sim gh-sim llm-sim api frontend
    Invoke-Compose ps
  }
  'down' { Invoke-Compose down }
  'reset' {
    Write-Warning 'This deletes ALL local Compose volumes.'
    Invoke-Compose down -v
  }
  'logs' { Invoke-Compose logs -f --tail=150 }
  'ps' { Invoke-Compose ps }
  'test' {
    Invoke-Compose up -d --build postgres azurite observability ado-sim gh-sim llm-sim api frontend
    Invoke-Compose --profile test run --build --rm backend-tests
    Invoke-Compose --profile test run --rm frontend-tests
    Invoke-Compose --profile test run --build --rm integration-tests
  }
  'ado-server' {
    Write-Host 'Starting the Azure DevOps Server VM (first boot downloads Windows and installs the server: ~1h).'
    Write-Host 'Console: http://localhost:8006  |  progress: docker compose logs -f ado-server'
    Invoke-Compose --profile ado-server up -d ado-server
  }
}
