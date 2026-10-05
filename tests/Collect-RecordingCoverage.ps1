param(
    [double]$Minimum = 90,
    [string]$OutputDirectory = "artifacts\android",
    [switch]$NoBuild
)
$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
Push-Location $root
try {
    $env:DOTNET_CLI_TELEMETRY_OPTOUT = "1"
    $env:DOTNET_COVERAGE_TELEMETRY_OPTOUT = "1"
    if (-not $NoBuild) {
        dotnet build tests\WalkLogger.Recording.Tests\WalkLogger.Recording.Tests.csproj --verbosity quiet
        if ($LASTEXITCODE -ne 0) { throw "Recording tests build failed." }
    }
    $output = [IO.Path]::GetFullPath($OutputDirectory)
    New-Item -ItemType Directory -Path $output -Force | Out-Null
    $report = Join-Path $output "recording.cobertura.xml"
    dotnet tool run dotnet-coverage collect --settings tests\recording.coverage.settings.xml --output $report --output-format cobertura --nologo dotnet tests\WalkLogger.Recording.Tests\bin\Debug\net10.0\WalkLogger.Recording.Tests.dll
    if ($LASTEXITCODE -ne 0) { throw "Recording coverage scenarios failed." }
    [xml]$xml = Get-Content -LiteralPath $report
    $covered = [double]::Parse($xml.coverage.GetAttribute("lines-covered"), [Globalization.CultureInfo]::InvariantCulture)
    $total = [double]::Parse($xml.coverage.GetAttribute("lines-valid"), [Globalization.CultureInfo]::InvariantCulture)
    if ($total -le 0 -or $covered -lt 0 -or $covered -gt $total) { throw "Invalid coverage totals." }
    $files = $xml.SelectNodes("//class").filename
    foreach ($source in @("Recording.cs", "FileRecordingStore.cs")) {
        if (-not ($files | Where-Object { [IO.Path]::GetFileName($_) -eq $source })) {
            throw "Missing recording coverage source: $source"
        }
    }
    $percent = $covered / $total * 100
    Write-Host ("Portable recording line coverage: {0:F2}% ({1}/{2})" -f $percent, $covered, $total)
    if ($percent -lt $Minimum) { throw "Recording coverage is below $Minimum%." }
} finally { Pop-Location }
