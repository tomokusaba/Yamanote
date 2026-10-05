param(
    [double]$Minimum = 90,
    [string]$OutputDirectory = "artifacts\coverage",
    [switch]$NoBuild
)
$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
Push-Location $root
try {
    $env:DOTNET_CLI_TELEMETRY_OPTOUT = "1"
    $env:DOTNET_COVERAGE_TELEMETRY_OPTOUT = "1"
    if (-not $NoBuild) {
        dotnet build WalkLogger.slnx --verbosity quiet
        if ($LASTEXITCODE -ne 0) { throw "Build failed." }
    }
    $output = [IO.Path]::GetFullPath($OutputDirectory)
    New-Item -ItemType Directory -Path $output -Force | Out-Null
    dotnet tool run dotnet-coverage collect --settings tests\coverage.settings.xml --output "$output\coverage.cobertura.xml" --output-format cobertura --nologo pwsh -NoProfile -File tests\Run-CoverageScenarios.ps1 -OutputDirectory $output
    if ($LASTEXITCODE -ne 0) { throw "Coverage scenarios failed." }
    & "$PSScriptRoot\Check-Coverage.ps1" -Report "$output\coverage.cobertura.xml" -Minimum $Minimum
} finally { Pop-Location }
