param(
    [Parameter(Mandatory)][string]$Report,
    [double]$Minimum = 90
)
$ErrorActionPreference = "Stop"
if (-not [double]::IsFinite($Minimum) -or $Minimum -lt 0 -or $Minimum -gt 100) {
    throw "Minimum must be between 0 and 100."
}
[xml]$document = Get-Content -LiteralPath $Report -Raw
$files = @{}
foreach ($class in $document.coverage.packages.package.classes.class) {
    $name = [string]$class.filename
    if (-not $files.ContainsKey($name)) { $files[$name] = @{} }
    foreach ($line in $class.lines.line) {
        $number = [int]$line.number
        $hit = [int]$line.hits -gt 0
        $files[$name][$number] = $hit -or $files[$name][$number]
    }
}
$total = 0
$covered = 0
$rows = foreach ($name in ($files.Keys | Sort-Object)) {
    $lines = $files[$name]
    $hits = @($lines.Values | Where-Object { $_ }).Count
    $total += $lines.Count
    $covered += $hits
    [pscustomobject]@{
        File = $name
        Covered = $hits
        Total = $lines.Count
        Percent = [math]::Round(100 * $hits / $lines.Count, 2)
        Missing = (($lines.Keys | Where-Object { -not $lines[$_] } | Sort-Object) -join ',')
    }
}
if ($total -eq 0) { throw "Report has no production lines." }
foreach ($project in @("App", "Domain", "Application", "Presentation", "Infrastructure", "Infrastructure.Windows")) {
    if (-not @($files.Keys | Where-Object { $_ -match "[\\/]WalkLogger\.$([regex]::Escape($project))[\\/]" }).Count) {
        throw "Report is missing production project WalkLogger.$project."
    }
}
$rows | Format-Table File,Covered,Total,Percent -AutoSize
$rows | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath ([IO.Path]::ChangeExtension($Report, ".summary.json"))
$covered = [long]$document.coverage.'lines-covered'
$total = [long]$document.coverage.'lines-valid'
if ($total -le 0 -or $covered -lt 0 -or $covered -gt $total) { throw "Invalid Cobertura totals." }
$percent = 100 * $covered / $total
Write-Host ("Overall Cobertura line coverage: {0:F2}% ({1}/{2})" -f $percent, $covered, $total)
if ($percent -lt $Minimum) { throw "Line coverage $percent% is below $Minimum%." }
