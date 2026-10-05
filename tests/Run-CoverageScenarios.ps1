param([Parameter(Mandatory)][string]$OutputDirectory)
$ErrorActionPreference = "Stop"
function Remove-OwnedTestDirectory([string]$Path) {
    $resolved = [IO.Path]::GetFullPath($Path)
    if ((Split-Path $resolved -Parent) -ne ([IO.Path]::GetTempPath()).TrimEnd('\') -or
        (Split-Path $resolved -Leaf) -notmatch '^WalkLogger-(windows-tests|smoke)-[0-9a-f]{32}$') {
        throw "Unexpected test directory; cleanup refused."
    }
    if (-not (Test-Path -LiteralPath $resolved)) { return }
    for ($attempt = 0; $attempt -lt 100; $attempt++) {
        try { [IO.Directory]::Delete($resolved, $true); return }
        catch [IO.IOException] {
            if ($attempt -eq 99) { throw }
            Start-Sleep -Milliseconds 100
        }
    }
}
dotnet tests\WalkLogger.Tests\bin\Debug\net10.0\WalkLogger.Tests.dll
if ($LASTEXITCODE -ne 0) { throw "Regression scenarios failed." }
$windows = [IO.Path]::GetFullPath("tests\WalkLogger.Windows.Tests\bin\Debug\net10.0-windows10.0.19041.0\WalkLogger.Windows.Tests.exe")
foreach ($case in @("ui", "start-fail", "stop-fail", "dispose-fail")) {
    $testRoot = Join-Path ([IO.Path]::GetTempPath()) ("WalkLogger-windows-tests-" + [guid]::NewGuid().ToString("N"))
    $arguments = @("--test-root=`"$testRoot`"")
    if ($case -ne "ui") {
        $caseOutput = Join-Path $OutputDirectory "$case.json"
        $arguments += @("--case=$case", "--smoke", "--smoke-output=`"$caseOutput`"")
    }
    try {
        $test = Start-Process -FilePath $windows -ArgumentList $arguments -PassThru -NoNewWindow
        if (-not $test.WaitForExit(120000)) { Stop-Process -Id $test.Id; throw "Windows test timed out: $case" }
        if ($test.ExitCode -ne 0) { throw "Windows test failed: $case ($($test.ExitCode))" }
    } finally {
        Remove-OwnedTestDirectory $testRoot
    }
}
$smoke = Join-Path $OutputDirectory "wpf-smoke.json"
$app = [IO.Path]::GetFullPath("src\WalkLogger.App\bin\Debug\net10.0-windows10.0.19041.0\WalkLogger.App.exe")
$process = Start-Process -FilePath $app -ArgumentList "--smoke", "--smoke-output=`"$smoke`"" -PassThru
if (-not $process.WaitForExit(60000)) {
    Stop-Process -Id $process.Id
    throw "WPF smoke timed out."
}
if ($process.ExitCode -ne 0) { throw "WPF smoke failed: $($process.ExitCode)." }
$result = Get-Content -LiteralPath $smoke -Raw | ConvertFrom-Json
if (-not ($result.success -and $result.hostStarted -and $result.hostStopped -and $result.hostDisposed)) {
    throw "WPF lifecycle verification failed."
}
if ($result.root) {
    $temporary = [IO.Path]::GetFullPath($result.root)
    $expectedParent = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\')
    if ((Split-Path $temporary -Parent) -ne $expectedParent -or (Split-Path $temporary -Leaf) -notmatch '^WalkLogger-smoke-[0-9a-f]{32}$') {
        throw "Unexpected smoke archive location; cleanup refused."
    }
    Remove-OwnedTestDirectory $temporary
}
