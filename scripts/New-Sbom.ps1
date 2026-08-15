[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$OutputPath,
    [string]$Version = "dev",
    [string]$Solution = "OeXYZ.ConsoleClient.slnx",
    [string]$DotnetPath = "dotnet"
)

$ErrorActionPreference = "Stop"
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$solutionPath = if ([IO.Path]::IsPathRooted($Solution)) { $Solution } else { Join-Path $repositoryRoot $Solution }
$resolvedOutput = [IO.Path]::GetFullPath((Join-Path (Get-Location) $OutputPath))
$components = @{}

function Add-Component {
    param(
        [string]$Type,
        [string]$Name,
        [string]$ComponentVersion,
        [string]$Purl,
        [string]$Scope,
        [string]$License
    )

    if ([string]::IsNullOrWhiteSpace($Name) -or [string]::IsNullOrWhiteSpace($ComponentVersion)) { return }
    $component = [ordered]@{
        type = $Type
        name = $Name
        version = $ComponentVersion
        "bom-ref" = $Purl
        purl = $Purl
    }
    if ($Scope) { $component.scope = $Scope }
    if ($License) { $component.licenses = @(@{ license = @{ id = $License } }) }
    $components[$Purl] = $component
}

Push-Location $repositoryRoot
try {
    $packageJson = (& $DotnetPath list $solutionPath package --include-transitive --format json | Out-String)
    if ($LASTEXITCODE -ne 0) { throw "dotnet package inventory failed with exit code $LASTEXITCODE." }
    $inventory = $packageJson | ConvertFrom-Json -Depth 100
    foreach ($project in $inventory.projects) {
        foreach ($framework in $project.frameworks) {
            foreach ($package in @($framework.topLevelPackages) + @($framework.transitivePackages)) {
                $resolved = [string]$package.resolvedVersion
                $id = [string]$package.id
                $escaped = [Uri]::EscapeDataString($id)
                Add-Component -Type library -Name $id -ComponentVersion $resolved `
                    -Purl "pkg:nuget/$escaped@$resolved" -Scope required -License ""
            }
        }
    }

    $npmLockPath = Join-Path $repositoryRoot "package-lock.json"
    if (Test-Path -LiteralPath $npmLockPath) {
        $npm = Get-Content -LiteralPath $npmLockPath -Raw | ConvertFrom-Json -Depth 100 -AsHashtable
        foreach ($entry in $npm.packages.GetEnumerator()) {
            if (-not $entry.Key -or -not $entry.Value.version) { continue }
            $marker = "node_modules/"
            $position = $entry.Key.LastIndexOf($marker, [StringComparison]::Ordinal)
            if ($position -lt 0) { continue }
            $name = $entry.Key.Substring($position + $marker.Length)
            $npmVersion = [string]$entry.Value.version
            $npmName = [Uri]::EscapeDataString($name).Replace("%2F", "/", [StringComparison]::OrdinalIgnoreCase)
            $license = if ($entry.Value.license -is [string]) { [string]$entry.Value.license } else { "" }
            Add-Component -Type library -Name $name -ComponentVersion $npmVersion `
                -Purl "pkg:npm/$npmName@$npmVersion" -Scope optional -License $license
        }
    }
}
finally {
    Pop-Location
}

$document = [ordered]@{
    "bomFormat" = "CycloneDX"
    "specVersion" = "1.6"
    "version" = 1
    "metadata" = [ordered]@{
        "component" = [ordered]@{
            "type" = "application"
            "name" = "OeXYZ Minecraft Console Client"
            "version" = $Version
            "bom-ref" = "pkg:github/Oexyz/OeXYZ-Minecraft-Console-Client@$Version"
            "purl" = "pkg:github/Oexyz/OeXYZ-Minecraft-Console-Client@$Version"
        }
    }
    "components" = @($components.Values | Sort-Object { $_.purl })
}

$directory = Split-Path -Parent $resolvedOutput
if ($directory) { New-Item -ItemType Directory -Path $directory -Force | Out-Null }
$document | ConvertTo-Json -Depth 100 | Set-Content -LiteralPath $resolvedOutput -Encoding utf8NoBOM
Write-Host "Wrote CycloneDX SBOM with $($components.Count) components to $resolvedOutput"
