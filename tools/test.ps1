#Requires -Version 5.1
<#
Runs the project tests with a loopback preflight and checks the TRX result.
No elevation, firewall changes, permission changes, or automatic retries.
Examples:
  powershell -NoProfile -ExecutionPolicy Bypass -File tools/test.ps1
  powershell -NoProfile -ExecutionPolicy Bypass -File tools/test.ps1 -NoBuild
  powershell -NoProfile -ExecutionPolicy Bypass -File tools/test.ps1 -Filter FullyQualifiedName~UpdateFlowTests
ExecutionPolicy applies only to this PowerShell process; no persistent policy is changed.
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Release',
    [switch]$NoBuild,
    [string]$Filter = ''
)

$ErrorActionPreference = 'Stop'
$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$projectPath = Join-Path $repoRoot 'FpsTune.Wpf.Tests\FpsTune.Wpf.Tests.csproj'

function Assert-TestTransport {
    $listener = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, 0)
    $client = [System.Net.Sockets.TcpClient]::new()
    $accepted = $null
    $pending = $null
    try {
        $listener.Start(1)
        $port = ([System.Net.IPEndPoint]$listener.LocalEndpoint).Port
        $pending = $client.BeginConnect([System.Net.IPAddress]::Loopback, $port, $null, $null)
        if (-not $pending.AsyncWaitHandle.WaitOne(3000)) {
            throw 'Local test transport did not connect within 3 seconds.'
        }
        $client.EndConnect($pending)
        $accepted = $listener.AcceptTcpClient()
    }
    catch {
        $cause = $_.Exception
        while ($cause.InnerException) { $cause = $cause.InnerException }
        if ($cause -is [System.Net.Sockets.SocketException] -and $cause.NativeErrorCode -eq 10013) {
            throw ('Testhost cannot connect to localhost: socket access denied (10013). ' +
                'Run this command in a normal local execution context approved by the runtime. ' +
                'Do not increase VSTEST_CONNECTION_TIMEOUT or disable the firewall. Tests were not started.')
        }
        throw ('Local test transport preflight failed: ' + $cause.Message + ' Tests were not started.')
    }
    finally {
        if ($pending) { $pending.AsyncWaitHandle.Close() }
        if ($accepted) { $accepted.Dispose() }
        $client.Dispose()
        $listener.Stop()
    }
}

try {
    Assert-TestTransport
    $runName = [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss-fff') + '-' + [Guid]::NewGuid().ToString('N')
    $resultDir = Join-Path $repoRoot ('work\test-results\' + $runName)
    [void][System.IO.Directory]::CreateDirectory($resultDir)
    $trxPath = Join-Path $resultDir 'results.trx'
    $diagPath = Join-Path $resultDir 'vstest.log'
    $testArgs = @('test', $projectPath, '-c', $Configuration, '-p:UseSharedCompilation=false',
        '--logger', 'trx;LogFileName=results.trx', '--results-directory', $resultDir, '--diag', $diagPath)
    if ($NoBuild) { $testArgs += '--no-build' }
    if ($Filter) { $testArgs += @('--filter', $Filter) }
    & dotnet @testArgs
    $testExitCode = $LASTEXITCODE
    Write-Host ('Test evidence: ' + $resultDir)
    if ($testExitCode -ne 0) { throw ('dotnet test failed with exit code ' + $testExitCode + '.') }
    if (-not [System.IO.File]::Exists($trxPath)) {
        throw 'No TRX result was produced. Exit code 0 alone is not a successful test run.'
    }
    [xml]$trx = [System.IO.File]::ReadAllText($trxPath)
    $summary = $trx.SelectSingleNode('//*[local-name()="ResultSummary"]')
    $counters = $trx.SelectSingleNode('//*[local-name()="ResultSummary"]/*[local-name()="Counters"]')
    if ($null -eq $summary -or $null -eq $counters) { throw 'The TRX result has no test summary.' }
    $executed = [int]$counters.GetAttribute('executed')
    $passed = [int]$counters.GetAttribute('passed')
    $failed = [int]$counters.GetAttribute('failed')
    if ($executed -le 0 -or $passed -ne $executed -or $failed -ne 0 -or
        $summary.GetAttribute('outcome') -notin @('Completed', 'Passed')) {
        throw ('Test result is not a complete pass: executed=' + $executed +
            ', passed=' + $passed + ', failed=' + $failed + ', outcome=' + $summary.GetAttribute('outcome'))
    }
    Write-Host ('VERIFIED: ' + $passed + '/' + $executed + ' executed tests passed (TRX checked).')
    exit 0
}
catch {
    [Console]::Error.WriteLine('FAIL: ' + $_.Exception.Message)
    exit 1
}
