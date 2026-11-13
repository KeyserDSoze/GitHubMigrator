# Used by the classic build definition after VSBuild.
param([string]$Output = "$env:BUILD_ARTIFACTSTAGINGDIRECTORY")
Write-Host "Packaging legacy site into $Output"
