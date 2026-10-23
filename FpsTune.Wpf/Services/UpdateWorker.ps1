param([Parameter(Mandatory=$true)][string]$Receipt)
# Runs independently after the app exits. Inputs are an immutable, hash-checked local receipt.
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2
$lease = $null
$inputLease = $null
$gate = $null
$held = $false
$attempt = $null
$stage = 'verify-package'
$restart = $false
$workerTicks = [Diagnostics.Process]::GetCurrentProcess().StartTime.ToUniversalTime().Ticks
function Write-Outcome([string]$status, [string]$phase, $code, [string]$message) {
    $record = @{ Id=$attempt.Id; Status=$status; Stage=$phase; ExitCode=$code; Error=$message; WorkerId=$PID; WorkerStartedTicks=$workerTicks }
    $target = Join-Path $attemptDir 'outcome.json'
    $temporary = Join-Path $attemptDir ([Guid]::NewGuid().ToString('N') + '.tmp')
    $bytes = (New-Object Text.UTF8Encoding($false)).GetBytes(($record | ConvertTo-Json -Compress))
    $stream = [IO.File]::Open($temporary, 'CreateNew', 'Write', 'None')
    try { $stream.Write($bytes, 0, $bytes.Length); $stream.Flush($true) } finally { $stream.Dispose() }
    if ([IO.File]::Exists($target)) {
        # Windows PowerShell 5 converts a null string argument to an empty path in File.Replace.
        $previous = $temporary + '.previous'
        [IO.File]::Replace($temporary, $target, $previous)
        [IO.File]::Delete($previous)
    }
    else { [IO.File]::Move($temporary, $target) }
}
function Parent-Alive {
    try {
        $parentProcess = [Diagnostics.Process]::GetProcessById($attempt.ParentId)
        try { return !$parentProcess.HasExited -and $parentProcess.StartTime.ToUniversalTime().Ticks -eq $attempt.ParentStartedTicks }
        finally { $parentProcess.Dispose() }
    } catch { return $false }
}
try {
    $Receipt = [IO.Path]::GetFullPath($Receipt)
    $attemptDir = [IO.Path]::GetDirectoryName($Receipt)
    $root = [IO.Path]::GetDirectoryName([IO.Path]::GetDirectoryName($attemptDir))
    $inputLease = [IO.File]::Open($Receipt, 'Open', 'Read', 'Read')
    if ($inputLease.Length -gt 65536) { throw 'Invalid receipt size.' }
    $reader = New-Object IO.StreamReader($inputLease, [Text.Encoding]::UTF8, $true, 4096, $true)
    try { $attempt = $reader.ReadToEnd() | ConvertFrom-Json } finally { $reader.Dispose() }
    $parsed = [Guid]::Empty
    if (![Guid]::TryParseExact($attempt.Id, 'N', [ref]$parsed) -or [IO.Path]::GetFileName($attemptDir) -ne $attempt.Id -or
        [IO.Path]::GetFileName($Receipt) -ne 'launch.json' -or
        [IO.Path]::GetFileName([IO.Path]::GetDirectoryName($attemptDir)) -ne 'attempts' -or
        ![Guid]::TryParseExact($attempt.PackageId, 'N', [ref]$parsed) -or
        $attempt.Target.Version -notmatch '^\d+\.\d+\.\d+$' -or
        $attempt.PackageHash -notmatch '^[a-fA-F0-9]{64}$' -or
        ![IO.Path]::IsPathRooted($attempt.InstallDirectory)) { throw 'Invalid update receipt.' }
    # /DIR 参数由字符串拼接构造；引号或 NUL 会截断参数注入额外安装器开关。
    # C# 侧已拒绝同样的字符，这里是 worker 独立的第二道防线。
    if ($attempt.InstallDirectory.IndexOfAny([char[]]@([char]34, [char]0)) -ge 0) { throw 'Invalid install directory.' }
    $installer = Join-Path (Join-Path (Join-Path $root 'packages') $attempt.PackageId) ('FpsTune-Setup-' + $attempt.Target.Version + '.exe')
    # Reject filesystem redirects in every existing cache path component.
    $current = $installer
    while ($current.Length -ge $root.Length) {
        if (([IO.File]::GetAttributes($current) -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'Linked update cache.' }
        if ($current -eq $root) { break }
        $current = [IO.Path]::GetDirectoryName($current)
    }
    $lease = [IO.File]::Open($installer, 'Open', 'Read', 'Read')
    $sha = [Security.Cryptography.SHA256]::Create()
    try { $actual = [BitConverter]::ToString($sha.ComputeHash($lease)).Replace('-', '') } finally { $sha.Dispose() }
    if ($actual -ne $attempt.PackageHash) { throw 'Installer hash mismatch.' }
    $stage = 'wait-for-exit'
    Write-Outcome 'waiting-for-exit' $stage $null ''
    [IO.File]::WriteAllText((Join-Path $attemptDir 'ready'), 'ready')
    $deadline = [DateTime]::UtcNow.AddSeconds(60)
    while (Parent-Alive) {
        if ([IO.File]::Exists((Join-Path $attemptDir 'abort'))) { Write-Outcome 'cancelled' $stage $null ''; exit 0 }
        if ([DateTime]::UtcNow -gt $deadline) { throw 'Application did not exit before the update.' }
        Start-Sleep -Milliseconds 100
    }
    if ([IO.File]::Exists((Join-Path $attemptDir 'abort'))) { Write-Outcome 'cancelled' $stage $null ''; exit 0 }
    $restart = $true
    $stage = 'operation-lock'
    $gate = New-Object Threading.Mutex($false, 'Local\FpsTune.RestoreAll')
    try { $held = $gate.WaitOne(0) } catch [Threading.AbandonedMutexException] { $held = $true }
    if (!$held) { throw 'Another system operation is running.' }
    $stage = 'run-installer'
    Write-Outcome 'installing' $stage $null ''
    $start = New-Object Diagnostics.ProcessStartInfo
    $start.FileName = $installer
    $start.UseShellExecute = $true
    # SILENT retains Inno's cancellable progress window, pins /DIR, and skips postinstall app launches in older installers too.
    $start.Arguments = '/SILENT /FpsTuneUpdate=1 /NORESTART /NORESTARTAPPLICATIONS /DIR="' + $attempt.InstallDirectory + '" /LOG="' + (Join-Path $attemptDir 'installer.log') + '"'
    $installation = [Diagnostics.Process]::Start($start)
    if ($null -eq $installation) { throw 'Installer did not start.' }
    try { $installation.WaitForExit(); $code = $installation.ExitCode } finally { $installation.Dispose() }
    $stage = 'installer-exit'
    if ($code -eq 0) {
        if ($attempt.Mode -eq 'rollback') {
            # Older released versions do not understand recovery receipts; verify their installed file here.
            $installed = Join-Path $attempt.InstallDirectory 'FpsTune.exe'
            $product = [Diagnostics.FileVersionInfo]::GetVersionInfo($installed).ProductVersion.Split('+')[0]
            $wanted = $attempt.Target.ReleaseTag.TrimStart([char[]]'vV')
            if ($product -ne $wanted) { throw 'Installed rollback version mismatch.' }
            Write-Outcome 'succeeded' 'verify-installed-file' $code ''
        } else { Write-Outcome 'awaiting-startup' $stage $code '' }
    }
    elseif ($code -eq 2 -or $code -eq 5) { Write-Outcome 'cancelled' $stage $code '' }
    else { Write-Outcome 'failed' $stage $code ('Installer exited with code ' + $code + '.') }
} catch {
    try { [IO.File]::WriteAllText((Join-Path $attemptDir 'worker-error.txt'), $_.Exception.Message) } catch { }
    if ($null -ne $attempt) {
        $cause = $_.Exception.GetBaseException()
        try {
            if ($cause -is [ComponentModel.Win32Exception] -and $cause.NativeErrorCode -eq 1223) {
                Write-Outcome 'cancelled' $stage $null ''
            } else { Write-Outcome 'failed' $stage $null ('Update worker failed during ' + $stage + '.') }
        } catch { }
    }
    exit 1
} finally {
    if ($held) { $gate.ReleaseMutex() }
    if ($null -ne $gate) { $gate.Dispose() }
    if ($null -ne $lease) { $lease.Dispose() }
    if ($null -ne $inputLease) { $inputLease.Dispose() }
    if ($restart) {
        $applicationPath = Join-Path $attempt.InstallDirectory 'FpsTune.exe'
        if ([IO.File]::Exists($applicationPath)) {
            try { Start-Process -FilePath $applicationPath -WorkingDirectory $attempt.InstallDirectory } catch { }
        }
    }
}
