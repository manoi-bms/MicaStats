param(
    [string]$Filter,
    [switch]$NoBuild,
    [switch]$Detail,
    [ValidateRange(1, 32)][int]$Threads = 2
)

$ErrorActionPreference = 'Stop'
$runner = [System.Diagnostics.Process]::GetCurrentProcess()
$previousPriority = $runner.PriorityClass
$localSdk = Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet\dotnet.exe'
$sdk = if (Test-Path -LiteralPath $localSdk) { $localSdk } else { (Get-Command dotnet -ErrorAction Stop).Source }
$testProject = Join-Path $PSScriptRoot 'tests\Kil0bitSystemMonitor.Tests\Kil0bitSystemMonitor.Tests.csproj'
$arguments = @('test', $testProject, '--nologo', '-m:1', '-p:BuildInParallel=false')
if ($Filter) { $arguments += @('--filter', $Filter) }
if ($NoBuild) { $arguments += '--no-build' }
if ($Detail) { $arguments += @('--logger', 'console;verbosity=normal') }
$arguments += @('--', 'RunConfiguration.MaxCpuCount=1', "xUnit.MaxParallelThreads=$Threads")

try {
    # Child processes inherit this priority, including MSBuild and testhost.
    $runner.PriorityClass = 'BelowNormal'
    & $sdk @arguments
    $testExitCode = $LASTEXITCODE
}
finally {
    $runner.PriorityClass = $previousPriority
}
exit $testExitCode
