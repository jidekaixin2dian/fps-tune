[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Executable,
    [Parameter(Mandatory = $true)][string[]]$CliArguments,
    [switch]$RequireSuccessJson
)
$ErrorActionPreference = 'Stop'

function ConvertTo-NativeArgument {
    param([AllowEmptyString()][string]$Value)
    # Windows command-line quoting: escape quotes and double trailing slashes.
    $escaped = [regex]::Replace($Value, '(\\*)"', '$1$1\"')
    $escaped = [regex]::Replace($escaped, '(\\+)$', '$1$1')
    return '"' + $escaped + '"'
}

# GUI-subsystem executables do not reliably set LASTEXITCODE in Windows
# PowerShell. Read stdout, stderr and exit status from this exact process.
$process = New-Object System.Diagnostics.Process
$process.StartInfo.FileName = (Get-Item -LiteralPath $Executable).FullName
$process.StartInfo.Arguments = ($CliArguments | ForEach-Object { ConvertTo-NativeArgument $_ }) -join ' '
$process.StartInfo.UseShellExecute = $false
$process.StartInfo.CreateNoWindow = $true
$process.StartInfo.RedirectStandardOutput = $true
$process.StartInfo.RedirectStandardError = $true
$process.StartInfo.StandardOutputEncoding = [Text.Encoding]::UTF8
$process.StartInfo.StandardErrorEncoding = [Text.Encoding]::UTF8
try {
    [void]$process.Start()
    $stdout = $process.StandardOutput.ReadToEndAsync()
    $stderr = $process.StandardError.ReadToEndAsync()
    $process.WaitForExit()
    $output = $stdout.GetAwaiter().GetResult()
    $errors = $stderr.GetAwaiter().GetResult()
    if ($process.ExitCode -ne 0) {
        throw ('CLI failed (' + $process.ExitCode + '): ' + ($CliArguments -join ' ') + "`n" + $output + $errors)
    }
    if ($RequireSuccessJson) {
        $result = $output | ConvertFrom-Json
        if ($result.ok -ne $true) { throw ('CLI did not report success: ' + ($CliArguments -join ' ') + "`n" + $output) }
    }
    if ($errors) { Write-Verbose $errors }
    Write-Output $output
} finally { $process.Dispose() }
