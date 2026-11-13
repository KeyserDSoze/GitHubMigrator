param(
    [Parameter(Mandatory = $true)][string]$Folder,
    [Parameter(Mandatory = $true)][string]$Thumbprint
)

Get-ChildItem -Path $Folder -Recurse -Include *.dll, *.exe |
    ForEach-Object {
        Write-Host "Signing $($_.FullName) with $Thumbprint"
    }
