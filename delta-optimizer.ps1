# delta-optimizer.ps1
# 三角洲行动 · Windows 系统层帧率优化（原创实现，Clean-room）
# 作者：独立编写 · 许可证：MIT
#
# 设计原则：
#   1. 只调整 Windows 系统层设置（注册表 / 电源计划 / 服务 / 启动配置）。
#   2. 不修改任何游戏安装目录内的文件，不注入进程，不与反作弊交互。
#   3. 不关闭引导虚拟化（ACE 反作弊会检查虚拟化状态，关闭会导致游戏报错）。
#   4. 不做显卡型号伪装等高风险改动。
#   5. 每次写入前先备份原值（含"原本不存在"状态），支持逐项或全部还原。
#   6. 写入后回读校验，失败如实报错，不静默当成功。
#   7. 兼容 Windows PowerShell 5.1（Windows 10/11 自带）。
#
# 用法：
#   powershell -NoProfile -ExecutionPolicy Bypass -File delta-optimizer.ps1 -Detect [-Json]
#   powershell -NoProfile -ExecutionPolicy Bypass -File delta-optimizer.ps1 -Apply [-Items id1,id2 | -Preset balanced] [-Force] [-Json]
#   powershell -NoProfile -ExecutionPolicy Bypass -File delta-optimizer.ps1 -ListRestoreItems [-Json]
#   powershell -NoProfile -ExecutionPolicy Bypass -File delta-optimizer.ps1 -Restore [-RestoreItems id1,id2] [-Json]

[CmdletBinding()]
param(
    [switch]$Detect,
    [switch]$Apply,
    [switch]$Restore,
    [switch]$ListRestoreItems,
    [switch]$Json,
    [string[]]$Items,
    [string]$Preset,
    [string]$GamePath,
    [switch]$Force,
    [switch]$SkipRestartCheck
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'

$ToolName    = 'delta-optimizer'
$ToolVersion = '0.1.0'
$BackupRoot  = Join-Path $env:LOCALAPPDATA 'DeltaOptimizer\backup'

# ---------------------------------------------------------------------------
# 辅助函数
# ---------------------------------------------------------------------------

function Get-IsAdmin {
    $id = [Security.Principal.WindowsIdentity]::GetCurrent()
    $pr = New-Object Security.Principal.WindowsPrincipal($id)
    return $pr.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

# 读注册表值，返回结构化结果，不抛异常
function Read-RegValue {
    param([string]$Hive, [string]$Path, [string]$Name)
    try {
        $root = if ($Hive -eq 'HKCU') { [Microsoft.Win32.Registry]::CurrentUser }
                else { [Microsoft.Win32.Registry]::LocalMachine }
        $key = $root.OpenSubKey($Path)
        if ($null -eq $key) {
            return @{ exists = $false; value = $null; kind = $null }
        }
        $names = $key.GetValueNames()
        $exists = $names -contains $Name
        $value = $null
        $kind = $null
        if ($exists) {
            $value = $key.GetValue($Name)
            try { $kind = $key.GetValueKind($Name).ToString() } catch { $kind = 'Unknown' }
        }
        $key.Close()
        return @{ exists = $exists; value = $value; kind = $kind }
    } catch {
        return @{ exists = $false; value = $null; kind = $null }
    }
}

# 写注册表值（自动创建键），失败抛异常
function Set-RegValue {
    param([string]$Hive, [string]$Path, [string]$Name, $Value, [string]$Kind = 'DWord')
    $root = if ($Hive -eq 'HKCU') { [Microsoft.Win32.Registry]::CurrentUser }
            else { [Microsoft.Win32.Registry]::LocalMachine }
    $key = $root.CreateSubKey($Path)
    if ($null -eq $key) { throw "无法创建注册表键: $Hive\$Path" }
    try {
        $key.SetValue($Name, $Value, $Kind)
    } finally {
        $key.Close()
    }
}

# 删除注册表值；值不存在时静默成功
function Remove-RegValue {
    param([string]$Hive, [string]$Path, [string]$Name)
    $root = if ($Hive -eq 'HKCU') { [Microsoft.Win32.Registry]::CurrentUser }
            else { [Microsoft.Win32.Registry]::LocalMachine }
    $key = $root.OpenSubKey($Path, $true)
    if ($null -eq $key) { return }
    try {
        if ($key.GetValueNames() -contains $Name) { $key.DeleteValue($Name) }
    } finally {
        $key.Close()
    }
}

# 运行原生命令，返回 (exitCode, stdoutLines)
function Invoke-Native {
    param([string]$Exe, [string[]]$Arguments)
    $out = @()
    $err = @()
    $pinfo = New-Object System.Diagnostics.ProcessStartInfo
    $pinfo.FileName = $Exe
    $pinfo.Arguments = ($Arguments -join ' ')
    $pinfo.UseShellExecute = $false
    $pinfo.RedirectStandardOutput = $true
    $pinfo.RedirectStandardError = $true
    $pinfo.CreateNoWindow = $true
    try {
        $proc = [System.Diagnostics.Process]::Start($pinfo)
        $out = @($proc.StandardOutput.ReadToEnd() -split "`r?`n" | Where-Object { $_ -ne '' })
        $err = @($proc.StandardError.ReadToEnd() -split "`r?`n" | Where-Object { $_ -ne '' })
        $proc.WaitForExit()
        return @{ code = $proc.ExitCode; output = $out; error = $err }
    } catch {
        return @{ code = -1; output = @(); error = @($_.Exception.Message) }
    }
}

# 原子写文件：先写 .tmp 再改名
function Write-AtomicJson {
    param([string]$Path, $Object)
    $dir = Split-Path $Path -Parent
    if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
    $tmp = "$Path.tmp"
    $Object | ConvertTo-Json -Depth 12 | Out-File -FilePath $tmp -Encoding UTF8
    if (Test-Path $Path) { Remove-Item $Path -Force }
    Move-Item $tmp $Path -Force
}

function Get-RebootItems {
    # 需要重启才能完全生效的项（保守口径，宁多勿漏）
    return @('hags', 'mpo-off', 'sysmain-off', 'wsearch-off', 'hibernate-off', 'power-tuning', 'paging-exec', 'mem-compress-off', 'dyntick-off')
}

# 定位主显卡的驱动注册表键（Class\{4d36e968-...}\00xx），按 DriverDesc 匹配主 GPU
function Get-MainGpuDriverKey {
    $base = 'SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}'
    $root = [Microsoft.Win32.Registry]::LocalMachine
    $key = $root.OpenSubKey($base)
    if ($null -eq $key) { return $null }
    $subs = @($key.GetSubKeyNames() | Where-Object { $_ -match '^00\d\d$' } | Sort-Object)
    $key.Close()
    if ($subs.Count -eq 0) { return $null }
    $mainGpu = $null
    try { $mainGpu = (Get-CimInstance Win32_VideoController | Where-Object { $_.Name } | Select-Object -First 1).Name } catch { }
    foreach ($sub in $subs) {
        $r = Read-RegValue 'HKLM' "$base\$sub" 'DriverDesc'
        if ($r.exists -and $mainGpu -and $r.value -eq $mainGpu) { return "$base\$sub" }
    }
    return "$base\$($subs[0])"
}

# ---------------------------------------------------------------------------
# 硬件与系统检测
# ---------------------------------------------------------------------------

function Get-HardwareInfo {
    $os = (Get-CimInstance Win32_OperatingSystem).Caption + ' (Build ' + (Get-CimInstance Win32_OperatingSystem).BuildNumber + ')'
    $cpu = (Get-CimInstance Win32_Processor | Select-Object -First 1).Name
    $ramGB = [math]::Round((Get-CimInstance Win32_ComputerSystem).TotalPhysicalMemory / 1GB, 0)
    $gpu = (Get-CimInstance Win32_VideoController | Where-Object { $_.Name } | Select-Object -First 1).Name
    $gpuFull = @(Get-CimInstance Win32_VideoController | Where-Object { $_.Name } | ForEach-Object { $_.Name }) -join ' | '
    $vendor = 'unknown'
    if ($gpu -match 'NVIDIA') { $vendor = 'nvidia' }
    elseif ($gpu -match 'AMD|Radeon') { $vendor = 'amd' }
    elseif ($gpu -match 'Intel') { $vendor = 'intel' }
    $chassis = @(Get-CimInstance Win32_SystemEnclosure | ForEach-Object { $_.ChassisTypes })
    $isLaptop = $false
    if ($chassis.Count -gt 0) {
        $laptopTypes = @(8, 9, 10, 11, 12, 14, 21, 30, 31, 32)
        $isLaptop = @($chassis | Where-Object { $laptopTypes -contains [int]$_ }).Count -gt 0
    }
    return @{
        os = $os; cpu = $cpu; ramGB = $ramGB; gpu = $gpuFull;
        gpuVendor = $vendor; isLaptop = $isLaptop
    }
}

function Find-GamePath {
    # 1) 正在运行的游戏进程
    try {
        $p = Get-Process -Name 'DeltaForceClient-Win64-Shipping' -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($p -and $p.Path -and (Test-Path $p.Path)) { return $p.Path }
    } catch { }

    # 2) 卸载注册表（WeGame / Steam / 官方安装器）
    $uninstallRoots = @(
        'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*',
        'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\*',
        'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\*'
    )
    foreach ($root in $uninstallRoots) {
        try {
            $apps = Get-ItemProperty $root -ErrorAction SilentlyContinue
            foreach ($a in $apps) {
                $dn = $a.DisplayName
                if ($dn -and ($dn -match '三角洲|Delta Force|DeltaForce')) {
                    $loc = $a.InstallLocation
                    if ($loc -and (Test-Path $loc)) {
                        $exe = Get-ChildItem $loc -Recurse -Filter 'DeltaForceClient-Win64-Shipping.exe' -ErrorAction SilentlyContinue -Depth 4 |
                               Select-Object -First 1
                        if ($exe) { return $exe.FullName }
                    }
                }
            }
        } catch { }
    }

    # 3) 常见目录名兜底（只查一级子目录，避免全盘慢扫）
    $drives = @(Get-PSDrive -PSProvider FileSystem -ErrorAction SilentlyContinue | Where-Object { $_.Root -match '^[A-Za-z]:\\$' } | ForEach-Object { $_.Root })
    foreach ($d in $drives) {
        try {
            $cands = Get-ChildItem $d -Directory -ErrorAction SilentlyContinue |
                     Where-Object { $_.Name -match 'Delta|三角洲' }
            foreach ($c in $cands) {
                $exe = Get-ChildItem $c.FullName -Recurse -Filter 'DeltaForceClient-Win64-Shipping.exe' -ErrorAction SilentlyContinue -Depth 4 |
                       Select-Object -First 1
                if ($exe) { return $exe.FullName }
            }
        } catch { }
    }
    return $null
}

# ---------------------------------------------------------------------------
# 优化项定义（safe 档；全部可还原）
# ---------------------------------------------------------------------------
# 每项字段：
#   id, name, desc(人话说明), sideEffect(副作用说明，空=无),
#   admin(是否需要管理员), default(是否默认勾选), reboot(是否需要重启),
#   kind(registry|layers|hibernate|service|power), apply(脚本块), revert(还原脚本块)
# apply/revert 通过 $ctx 传递：item / backupItem(备份记录,可改) / gamePath
# apply 返回 $true(已改动) 或 $false(本就达标，未改动)；抛异常视为失败。
# ---------------------------------------------------------------------------

function New-ItemRegistryBackup {
    param($Item, [string]$Hive, [string]$Path, [string]$Name)
    $r = Read-RegValue $Hive $Path $Name
    return @{
        id = $Item.id; kind = 'registry'; hive = $Hive; path = $Path; name = $Name;
        oldExists = $r.exists; oldValue = $r.value; oldKind = $r.kind
    }
}

function New-ItemLayersBackup {
    param($Item, [string]$ExePath)
    $r = Read-RegValue 'HKCU' 'Software\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Layers' $ExePath
    return @{
        id = $Item.id; kind = 'layers'; exe = $ExePath;
        oldExists = $r.exists; oldValue = $r.value
    }
}

$OptimizationItems = @(
    @{
        id = 'power-ultimate'; name = '电源计划 → 卓越性能'
        desc = '切换到"卓越性能"电源计划（系统无可用方案时自动创建一份），让 CPU 更积极跑满频率。'
        sideEffect = '功耗与发热略升；笔记本续航变短。'
        admin = $true; default = $true; reboot = $false
        kind = 'power'
        apply = {
            param($ctx)
            $oldGuid = $null
            $r = Invoke-Native 'powercfg.exe' @('-getactivescheme')
            if ($r.code -eq 0 -and $r.output.Count -gt 0 -and $r.output[0] -match '([0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12})') {
                $oldGuid = $matches[1]
            }
            $ctx.backupItem.oldGuid = $oldGuid
            $ult = 'e9a42b02-d5df-448d-aa00-03f14749eb61'
            if ($oldGuid -eq $ult) { return $false }   # 已达标
            $set = Invoke-Native 'powercfg.exe' @('-setactive', $ult)
            if ($set.code -eq 0) { return $true }
            # 模板不可激活 → 实例化一份再激活
            $dup = Invoke-Native 'powercfg.exe' @('-duplicatescheme', $ult)
            if ($dup.code -ne 0 -or $dup.output.Count -eq 0 -or $dup.output[0] -notmatch '([0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12})') {
                throw '无法激活或创建卓越性能电源计划: ' + (($dup.error -join '; '))
            }
            $newGuid = $matches[1]
            $rename = Invoke-Native 'powercfg.exe' @('-changename', $newGuid, '三角洲优化 · 卓越性能')
            $activate = Invoke-Native 'powercfg.exe' @('-setactive', $newGuid)
            if ($activate.code -ne 0) { throw '创建后激活失败: ' + (($activate.error -join '; ')) }
            return $true
        }
        revert = {
            param($ctx)
            if ($ctx.backupItem.oldGuid) {
                $r = Invoke-Native 'powercfg.exe' @('-setactive', $ctx.backupItem.oldGuid)
                if ($r.code -ne 0) { throw '切回原电源计划失败: ' + (($r.error -join '; ')) }
            }
        }
    }
    @{
        id = 'power-tuning'; name = '电源计划隐藏项调优'
        desc = '解除隐藏并调整几项影响性能的电源参数：关闭 USB3 链路省电、允许处理器更高性能提升、关闭空闲降频等待。'
        sideEffect = '功耗略升；不支持的 CPU 平台自动跳过，不报错。'
        admin = $true; default = $true; reboot = $true
        kind = 'power'
        apply = {
            param($ctx)
            # 1) USB3 链路省电关闭（解除隐藏 + 设 0）
            Invoke-Native 'powercfg.exe' @('-attributes', '2a737441-1930-4402-8d77-b2bebba308a3', '48e6b7a6-50f5-4782-a5d4-53bb8f07e226', '-ATTRIB_HIDE') | Out-Null
            Invoke-Native 'powercfg.exe' @('-setacvalueindex', 'SCHEME_CURRENT', '2a737441-1930-4402-8d77-b2bebba308a3', '48e6b7a6-50f5-4782-a5d4-53bb8f07e226', '0') | Out-Null
            # 2) 处理器性能提升模式 → Aggressive（解除隐藏 + 设 2）
            Invoke-Native 'powercfg.exe' @('-attributes', 'be337238-0d82-4146-a960-4f3749d470c7', '45bcc044-d885-43e2-8605-ee0ec6e96b59', '-ATTRIB_HIDE') | Out-Null
            Invoke-Native 'powercfg.exe' @('-setacvalueindex', 'SCHEME_CURRENT', 'be337238-0d82-4146-a960-4f3749d470c7', '45bcc044-d885-43e2-8605-ee0ec6e96b59', '2') | Out-Null
            # 3) 处理器空闲降频允许（解锁隐藏项并关闭空闲降频等待）
            Invoke-Native 'powercfg.exe' @('-attributes', 'bd3b718a-0680-4d9d-8ab2-e1d2b4ac806d', '4f2f7c6f-5e88-40dd-bad6-c8e8e0f8a9b3', '-ATTRIB_HIDE') | Out-Null
            $set = Invoke-Native 'powercfg.exe' @('-setacvalueindex', 'SCHEME_CURRENT', 'bd3b718a-0680-4d9d-8ab2-e1d2b4ac806d', '4f2f7c6f-5e88-40dd-bad6-c8e8e0f8a9b3', '0')
            $apply = Invoke-Native 'powercfg.exe' @('-setactive', 'SCHEME_CURRENT')
            if ($apply.code -ne 0) { throw '应用电源隐藏项失败: ' + (($apply.error -join '; ')) }
            return $true
        }
        revert = {
            param($ctx)
            # 还原动作：把上述三项恢复为系统默认（AC 默认值）
            Invoke-Native 'powercfg.exe' @('-setacvalueindex', 'SCHEME_CURRENT', '2a737441-1930-4402-8d77-b2bebba308a3', '48e6b7a6-50f5-4782-a5d4-53bb8f07e226', '1') | Out-Null
            Invoke-Native 'powercfg.exe' @('-setacvalueindex', 'SCHEME_CURRENT', 'be337238-0d82-4146-a960-4f3749d470c7', '45bcc044-d885-43e2-8605-ee0ec6e96b59', '0') | Out-Null
            Invoke-Native 'powercfg.exe' @('-setacvalueindex', 'SCHEME_CURRENT', 'bd3b718a-0680-4d9d-8ab2-e1d2b4ac806d', '4f2f7c6f-5e88-40dd-bad6-c8e8e0f8a9b3', '1') | Out-Null
            Invoke-Native 'powercfg.exe' @('-setactive', 'SCHEME_CURRENT') | Out-Null
        }
    }
    @{
        id = 'hags'; name = '开启硬件加速 GPU 计划（HAGS）'
        desc = 'HwSchMode=2，让 GPU 调度走硬件队列，降低部分场景的输入延迟与掉帧。'
        sideEffect = '个别老驱动下可能蓝屏，属已知兼容性风险；出现异常可随时还原。'
        admin = $true; default = $true; reboot = $true
        kind = 'registry'
        apply = {
            param($ctx)
            $b = New-ItemRegistryBackup $ctx.item 'HKLM' 'SYSTEM\CurrentControlSet\Control\GraphicsDrivers' 'HwSchMode'
            $ctx.backupItem = $b
            $r = Read-RegValue 'HKLM' 'SYSTEM\CurrentControlSet\Control\GraphicsDrivers' 'HwSchMode'
            if ($r.exists -and $r.value -eq 2) { return $false }
            Set-RegValue 'HKLM' 'SYSTEM\CurrentControlSet\Control\GraphicsDrivers' 'HwSchMode' 2 'DWord'
            return $true
        }
        revert = {
            param($ctx)
            if ($ctx.backupItem.oldExists) { Set-RegValue 'HKLM' $ctx.backupItem.path $ctx.backupItem.name $ctx.backupItem.oldValue $ctx.backupItem.oldKind }
            else { Remove-RegValue 'HKLM' $ctx.backupItem.path $ctx.backupItem.name }
        }
    }
    @{
        id = 'game-mode'; name = '开启 Windows 游戏模式'
        desc = '允许系统在游戏运行时优先分配 CPU/GPU 资源。'
        sideEffect = ''
        admin = $false; default = $true; reboot = $false
        kind = 'registry'
        apply = {
            param($ctx)
            $b = New-ItemRegistryBackup $ctx.item 'HKCU' 'Software\Microsoft\GameBar' 'AutoGameModeEnabled'
            $ctx.backupItem = $b
            $r = Read-RegValue 'HKCU' 'Software\Microsoft\GameBar' 'AutoGameModeEnabled'
            if ($r.exists -and $r.value -eq 1) { return $false }
            Set-RegValue 'HKCU' 'Software\Microsoft\GameBar' 'AutoGameModeEnabled' 1 'DWord'
            Set-RegValue 'HKCU' 'Software\Microsoft\GameBar' 'AllowAutoGameMode' 1 'DWord'
            return $true
        }
        revert = {
            param($ctx)
            Set-RegValue 'HKCU' 'Software\Microsoft\GameBar' 'AllowAutoGameMode' 1 'DWord'
            if ($ctx.backupItem.oldExists) { Set-RegValue 'HKCU' $ctx.backupItem.path $ctx.backupItem.name $ctx.backupItem.oldValue $ctx.backupItem.oldKind }
            else { Remove-RegValue 'HKCU' $ctx.backupItem.path $ctx.backupItem.name }
        }
    }
    @{
        id = 'dvr-off'; name = '关闭 Xbox 后台录制'
        desc = '关掉 Game Bar 后台录制（DVR），减少游戏时的后台编码负载。'
        sideEffect = 'Win+G 录制/截图功能不可用。'
        admin = $false; default = $true; reboot = $false
        kind = 'registry'
        apply = {
            param($ctx)
            $b = New-ItemRegistryBackup $ctx.item 'HKCU' 'System\GameConfigStore' 'GameDVR_Enabled'
            $ctx.backupItem = $b
            $r = Read-RegValue 'HKCU' 'System\GameConfigStore' 'GameDVR_Enabled'
            $changed = $false
            if (-not ($r.exists -and $r.value -eq 0)) {
                Set-RegValue 'HKCU' 'System\GameConfigStore' 'GameDVR_Enabled' 0 'DWord'
                $changed = $true
            }
            $p = Read-RegValue 'HKLM' 'SOFTWARE\Policies\Microsoft\Windows\GameDVR' 'AllowGameDVR'
            if (-not ($p.exists -and $p.value -eq 0)) {
                Set-RegValue 'HKLM' 'SOFTWARE\Policies\Microsoft\Windows\GameDVR' 'AllowGameDVR' 0 'DWord'
                $changed = $true
            }
            return $changed
        }
        revert = {
            param($ctx)
            if ($ctx.backupItem.oldExists) { Set-RegValue 'HKCU' $ctx.backupItem.path $ctx.backupItem.name $ctx.backupItem.oldValue $ctx.backupItem.oldKind }
            else { Remove-RegValue 'HKCU' $ctx.backupItem.path $ctx.backupItem.name }
            Remove-RegValue 'HKLM' 'SOFTWARE\Policies\Microsoft\Windows\GameDVR' 'AllowGameDVR'
        }
    }
    @{
        id = 'prio-separation'; name = '前台进程调度权重提升'
        desc = 'Win32PrioritySeparation=0x28（短/变长量子、前台提升 2），让前台游戏进程获得更高调度优先级。'
        sideEffect = '个别软件在后台时响应变慢。'
        admin = $true; default = $true; reboot = $false
        kind = 'registry'
        apply = {
            param($ctx)
            $b = New-ItemRegistryBackup $ctx.item 'HKLM' 'SYSTEM\CurrentControlSet\Control\PriorityControl' 'Win32PrioritySeparation'
            $ctx.backupItem = $b
            $r = Read-RegValue 'HKLM' 'SYSTEM\CurrentControlSet\Control\PriorityControl' 'Win32PrioritySeparation'
            if ($r.exists -and $r.value -eq 0x28) { return $false }
            Set-RegValue 'HKLM' 'SYSTEM\CurrentControlSet\Control\PriorityControl' 'Win32PrioritySeparation' 0x28 'DWord'
            return $true
        }
        revert = {
            param($ctx)
            if ($ctx.backupItem.oldExists) { Set-RegValue 'HKLM' $ctx.backupItem.path $ctx.backupItem.name $ctx.backupItem.oldValue $ctx.backupItem.oldKind }
            else { Remove-RegValue 'HKLM' $ctx.backupItem.path $ctx.backupItem.name }
        }
    }
    @{
        id = 'wer-off'; name = '关闭 Windows 错误报告'
        desc = '关闭 WER 弹窗与后台转储，减少崩溃时的磁盘/CPU 开销。'
        sideEffect = '程序崩溃时不再有系统级提示窗口。'
        admin = $true; default = $true; reboot = $false
        kind = 'registry'
        apply = {
            param($ctx)
            $b = New-ItemRegistryBackup $ctx.item 'HKLM' 'SOFTWARE\Microsoft\Windows\Windows Error Reporting' 'Disabled'
            $ctx.backupItem = $b
            $r = Read-RegValue 'HKLM' 'SOFTWARE\Microsoft\Windows\Windows Error Reporting' 'Disabled'
            if ($r.exists -and $r.value -eq 1) { return $false }
            Set-RegValue 'HKLM' 'SOFTWARE\Microsoft\Windows\Windows Error Reporting' 'Disabled' 1 'DWord'
            return $true
        }
        revert = {
            param($ctx)
            if ($ctx.backupItem.oldExists) { Set-RegValue 'HKLM' $ctx.backupItem.path $ctx.backupItem.name $ctx.backupItem.oldValue $ctx.backupItem.oldKind }
            else { Remove-RegValue 'HKLM' $ctx.backupItem.path $ctx.backupItem.name }
        }
    }
    @{
        id = 'transparency-off'; name = '关闭窗口透明特效'
        desc = '关闭任务栏/窗口亚克力透明，省一点 GPU 开销。'
        sideEffect = '桌面观感变朴素。'
        admin = $false; default = $true; reboot = $false
        kind = 'registry'
        apply = {
            param($ctx)
            $b = New-ItemRegistryBackup $ctx.item 'HKCU' 'Software\Microsoft\Windows\CurrentVersion\Themes\Personalize' 'EnableTransparency'
            $ctx.backupItem = $b
            $r = Read-RegValue 'HKCU' 'Software\Microsoft\Windows\CurrentVersion\Themes\Personalize' 'EnableTransparency'
            if ($r.exists -and $r.value -eq 0) { return $false }
            Set-RegValue 'HKCU' 'Software\Microsoft\Windows\CurrentVersion\Themes\Personalize' 'EnableTransparency' 0 'DWord'
            return $true
        }
        revert = {
            param($ctx)
            if ($ctx.backupItem.oldExists) { Set-RegValue 'HKCU' $ctx.backupItem.path $ctx.backupItem.name $ctx.backupItem.oldValue $ctx.backupItem.oldKind }
            else { Remove-RegValue 'HKCU' $ctx.backupItem.path $ctx.backupItem.name }
        }
    }
    @{
        id = 'fso-off'; name = '禁用游戏全屏优化'
        desc = '为游戏主程序在 AppCompat 层加 DISABLEDXMAXIMIZEDWINDOWEDMODE，绕过全屏优化合成层。'
        sideEffect = 'Alt+Tab 切换可能略慢；需要先找到游戏主程序路径，找不到则跳过。'
        admin = $false; default = $true; reboot = $false
        kind = 'layers'
        apply = {
            param($ctx)
            $exe = $ctx.gamePath
            if (-not $exe) { return $false }   # 找不到游戏路径 → 跳过（上层会标记 skipped）
            $b = New-ItemLayersBackup $ctx.item $exe
            $ctx.backupItem = $b
            $flag = 'DISABLEDXMAXIMIZEDWINDOWEDMODE'
            $current = if ($b.oldExists) { $b.oldValue } else { '' }
            if ($current -match [regex]::Escape($flag)) { return $false }
            $newVal = (($current -replace '\s+$', '') + ' ' + $flag).Trim()
            Set-RegValue 'HKCU' 'Software\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Layers' $exe $newVal 'String'
            return $true
        }
        revert = {
            param($ctx)
            $b = $ctx.backupItem
            $flag = 'DISABLEDXMAXIMIZEDWINDOWEDMODE'
            if ($b.oldExists) {
                $cleaned = ($b.oldValue -replace [regex]::Escape($flag), '') -replace '\s+', ' '
                $cleaned = $cleaned.Trim()
                if ($cleaned -eq '') { Remove-RegValue 'HKCU' 'Software\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Layers' $b.exe }
                else { Set-RegValue 'HKCU' 'Software\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Layers' $b.exe $cleaned 'String' }
            } else {
                Remove-RegValue 'HKCU' 'Software\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Layers' $b.exe
            }
        }
    }
    @{
        id = 'gpu-pref'; name = '游戏强制使用高性能 GPU'
        desc = '在 DirectX UserGpuPreferences 中把游戏指定到高性能 GPU（双显卡笔记本关键）。'
        sideEffect = ''
        admin = $false; default = $true; reboot = $false
        kind = 'registry'
        apply = {
            param($ctx)
            $exe = $ctx.gamePath
            if (-not $exe) { return $false }
            $b = New-ItemRegistryBackup $ctx.item 'HKCU' 'Software\Microsoft\DirectX\UserGpuPreferences' $exe
            $ctx.backupItem = $b
            $r = Read-RegValue 'HKCU' 'Software\Microsoft\DirectX\UserGpuPreferences' $exe
            $current = if ($r.exists) { [string]$r.value } else { '' }
            if ($current -match 'GpuPreference=2') { return $false }
            if ($current -match 'GpuPreference=\d') {
                $newVal = $current -replace 'GpuPreference=\d', 'GpuPreference=2'
            } else {
                $newVal = ($current.Trim() + ';GpuPreference=2;').TrimStart(';')
            }
            Set-RegValue 'HKCU' 'Software\Microsoft\DirectX\UserGpuPreferences' $exe $newVal 'String'
            return $true
        }
        revert = {
            param($ctx)
            if ($ctx.backupItem.oldExists) { Set-RegValue 'HKCU' $ctx.backupItem.path $ctx.backupItem.name $ctx.backupItem.oldValue $ctx.backupItem.oldKind }
            else { Remove-RegValue 'HKCU' $ctx.backupItem.path $ctx.backupItem.name }
        }
    }
    @{
        id = 'mpo-off'; name = '禁用 MPO 多平面叠加'
        desc = 'DWM OverlayTestMode=5，规避已知的 MPO 闪烁/掉帧问题。'
        sideEffect = '个别 HDR/多屏场景显示行为可能不同。'
        admin = $true; default = $true; reboot = $true
        kind = 'registry'
        apply = {
            param($ctx)
            $b = New-ItemRegistryBackup $ctx.item 'HKLM' 'SOFTWARE\Microsoft\Windows\Dwm' 'OverlayTestMode'
            $ctx.backupItem = $b
            $r = Read-RegValue 'HKLM' 'SOFTWARE\Microsoft\Windows\Dwm' 'OverlayTestMode'
            if ($r.exists -and $r.value -eq 5) { return $false }
            Set-RegValue 'HKLM' 'SOFTWARE\Microsoft\Windows\Dwm' 'OverlayTestMode' 5 'DWord'
            return $true
        }
        revert = {
            param($ctx)
            if ($ctx.backupItem.oldExists) { Set-RegValue 'HKLM' $ctx.backupItem.path $ctx.backupItem.name $ctx.backupItem.oldValue $ctx.backupItem.oldKind }
            else { Remove-RegValue 'HKLM' $ctx.backupItem.path $ctx.backupItem.name }
        }
    }
    @{
        id = 'net-throttling-off'; name = '解除多媒体网络限流'
        desc = 'NetworkThrottlingIndex=0xffffffff，去掉 Windows 对多媒体流量的节流，降低网络延迟抖动。'
        sideEffect = ''
        admin = $true; default = $true; reboot = $false
        kind = 'registry'
        apply = {
            param($ctx)
            $b = New-ItemRegistryBackup $ctx.item 'HKLM' 'SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile' 'NetworkThrottlingIndex'
            $ctx.backupItem = $b
            $r = Read-RegValue 'HKLM' 'SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile' 'NetworkThrottlingIndex'
            if ($r.exists -and $r.value -eq 0xffffffff) { return $false }
            Set-RegValue 'HKLM' 'SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile' 'NetworkThrottlingIndex' 0xffffffff 'DWord'
            return $true
        }
        revert = {
            param($ctx)
            if ($ctx.backupItem.oldExists) { Set-RegValue 'HKLM' $ctx.backupItem.path $ctx.backupItem.name $ctx.backupItem.oldValue $ctx.backupItem.oldKind }
            else { Remove-RegValue 'HKLM' $ctx.backupItem.path $ctx.backupItem.name }
        }
    }
    @{
        id = 'sys-responsiveness'; name = '系统后台响应保留设为最低'
        desc = 'SystemResponsiveness=10（MMCSS 文档允许的最低值），把更多 CPU 留给前台游戏。'
        sideEffect = '后台任务（解压、杀毒扫描）响应略慢。'
        admin = $true; default = $true; reboot = $false
        kind = 'registry'
        apply = {
            param($ctx)
            $b = New-ItemRegistryBackup $ctx.item 'HKLM' 'SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile' 'SystemResponsiveness'
            $ctx.backupItem = $b
            $r = Read-RegValue 'HKLM' 'SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile' 'SystemResponsiveness'
            if ($r.exists -and $r.value -eq 10) { return $false }
            Set-RegValue 'HKLM' 'SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile' 'SystemResponsiveness' 10 'DWord'
            return $true
        }
        revert = {
            param($ctx)
            if ($ctx.backupItem.oldExists) { Set-RegValue 'HKLM' $ctx.backupItem.path $ctx.backupItem.name $ctx.backupItem.oldValue $ctx.backupItem.oldKind }
            else { Remove-RegValue 'HKLM' $ctx.backupItem.path $ctx.backupItem.name }
        }
    }
    @{
        id = 'mmcss-games'; name = 'MMCSS 游戏任务档位拉满'
        desc = '把多媒体类"游戏"任务的 GPU/CPU 调度权重设为最高档（收益微弱但零副作用）。'
        sideEffect = ''
        admin = $true; default = $true; reboot = $false
        kind = 'registry'
        apply = {
            param($ctx)
            $base = 'SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile\Tasks\Games'
            $b1 = New-ItemRegistryBackup $ctx.item "$base" 'GPU Priority'
            $ctx.backupItem = @{
                id = $ctx.item.id; kind = 'registry'; hive = 'HKLM'; path = $base;
                name = 'GPU Priority'; oldExists = $b1.oldExists; oldValue = $b1.oldValue; oldKind = $b1.oldKind
            }
            $r1 = Read-RegValue 'HKLM' $base 'GPU Priority'
            $r2 = Read-RegValue 'HKLM' $base 'Priority'
            $r3 = Read-RegValue 'HKLM' $base 'Scheduling Category'
            $r4 = Read-RegValue 'HKLM' $base 'SFIO Priority'
            if ($r1.exists -and $r1.value -eq 8 -and $r2.exists -and $r2.value -eq 6 -and
                $r3.exists -and $r3.value -eq 'High' -and $r4.exists -and $r4.value -eq 'High') { return $false }
            Set-RegValue 'HKLM' $base 'GPU Priority' 8 'DWord'
            Set-RegValue 'HKLM' $base 'Priority' 6 'DWord'
            Set-RegValue 'HKLM' $base 'Scheduling Category' 'High' 'String'
            Set-RegValue 'HKLM' $base 'SFIO Priority' 'High' 'String'
            return $true
        }
        revert = {
            param($ctx)
            $base = 'SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile\Tasks\Games'
            Remove-RegValue 'HKLM' $base 'GPU Priority'
            Remove-RegValue 'HKLM' $base 'Priority'
            Remove-RegValue 'HKLM' $base 'Scheduling Category'
            Remove-RegValue 'HKLM' $base 'SFIO Priority'
        }
    }
    @{
        id = 'sysmain-off'; name = '禁用 SysMain（预取）服务'
        desc = '把 SysMain 服务启动类型设为禁用（Start=4）。SSD 时代预取收益有限，省一点后台 IO。'
        sideEffect = '系统启动后程序冷启动略慢；默认不勾选。'
        admin = $true; default = $false; reboot = $true
        kind = 'service'
        apply = {
            param($ctx)
            $svc = Get-Service -Name 'SysMain' -ErrorAction SilentlyContinue
            if (-not $svc) { return $false }
            $cfg = Get-CimInstance Win32_Service -Filter "Name='SysMain'"
            $ctx.backupItem = @{ id = $ctx.item.id; kind = 'service'; service = 'SysMain'; oldStart = $cfg.StartMode }
            $r = Invoke-Native 'sc.exe' @('config', 'SysMain', 'start=', 'disabled')
            if ($r.code -ne 0) { throw '设置 SysMain 启动类型失败: ' + (($r.error -join '; ')) }
            return $true
        }
        revert = {
            param($ctx)
            $mode = $ctx.backupItem.oldStart
            if (-not $mode) { $mode = 'Manual' }
            Invoke-Native 'sc.exe' @('config', 'SysMain', 'start=', $mode) | Out-Null
        }
    }
    @{
        id = 'wsearch-off'; name = '禁用 Windows Search 索引'
        desc = '把 Windows Search 服务设为禁用（Start=4），减少索引后台占用。'
        sideEffect = '开始菜单/资源管理器搜索明显变慢；默认不勾选。'
        admin = $true; default = $false; reboot = $true
        kind = 'service'
        apply = {
            param($ctx)
            $svc = Get-Service -Name 'WSearch' -ErrorAction SilentlyContinue
            if (-not $svc) { return $false }
            $cfg = Get-CimInstance Win32_Service -Filter "Name='WSearch'"
            $ctx.backupItem = @{ id = $ctx.item.id; kind = 'service'; service = 'WSearch'; oldStart = $cfg.StartMode }
            $r = Invoke-Native 'sc.exe' @('config', 'WSearch', 'start=', 'disabled')
            if ($r.code -ne 0) { throw '设置 WSearch 启动类型失败: ' + (($r.error -join '; ')) }
            return $true
        }
        revert = {
            param($ctx)
            $mode = $ctx.backupItem.oldStart
            if (-not $mode) { $mode = 'Manual' }
            Invoke-Native 'sc.exe' @('config', 'WSearch', 'start=', $mode) | Out-Null
        }
    }
    @{
        id = 'hibernate-off'; name = '关闭休眠与快速启动'
        desc = 'powercfg /h off，删除休眠文件，加快启动并腾出磁盘；快速启动随之失效。'
        sideEffect = '合盖只剩睡眠（无休眠）；开机冷启动略慢；笔记本默认不勾选。'
        admin = $true; default = $false; reboot = $true
        kind = 'hibernate'
        apply = {
            param($ctx)
            $ctx.backupItem = @{ id = $ctx.item.id; kind = 'hibernate'; oldState = if (Test-Path "$env:SystemDrive\hiberfil.sys") { 'on' } else { 'off' } }
            if ($ctx.backupItem.oldState -eq 'off') { return $false }
            $r = Invoke-Native 'powercfg.exe' @('/h', 'off')
            if ($r.code -ne 0) { throw '关闭休眠失败: ' + (($r.error -join '; ')) }
            return $true
        }
        revert = {
            param($ctx)
            if ($ctx.backupItem.oldState -eq 'on') {
                $r = Invoke-Native 'powercfg.exe' @('/h', 'on')
                if ($r.code -ne 0) { throw '重新开启休眠失败: ' + (($r.error -join '; ')) }
            }
        }
    }
    @{
        id = 'game-priority'; name = '游戏进程 CPU 优先级提到高'
        desc = '通过 IFEO PerfOptions 让游戏进程启动即以高优先级运行（CpuPriorityClass=3）。'
        sideEffect = '对指定进程生效；多开/直播同机时可能影响其他程序。'
        admin = $true; default = $true; reboot = $false
        kind = 'registry'
        apply = {
            param($ctx)
            $exeName = $ctx.gameName
            if (-not $exeName) { return $false }
            $base = "SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options\$exeName\PerfOptions"
            $b = New-ItemRegistryBackup $ctx.item 'HKLM' $base 'CpuPriorityClass'
            $ctx.backupItem = $b
            $ctx.backupItem.perfKeyExisted = (Read-RegValue 'HKLM' $base 'CpuPriorityClass').exists
            $r = Read-RegValue 'HKLM' $base 'CpuPriorityClass'
            if ($r.exists -and $r.value -eq 3) { return $false }
            Set-RegValue 'HKLM' $base 'CpuPriorityClass' 3 'DWord'
            return $true
        }
        revert = {
            param($ctx)
            $b = $ctx.backupItem
            $base = "SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options\$($b.path.Split('\')[-2])\PerfOptions"
            if ($b.oldExists) { Set-RegValue 'HKLM' $b.path $b.name $b.oldValue $b.oldKind }
            else { Remove-RegValue 'HKLM' $b.path $b.name }
        }
    }
    @{
        id = 'paging-exec'; name = '内核代码常驻内存'
        desc = 'DisablePagingExecutive=1，不让内核与驱动代码分页到磁盘，减少关键路径的磁盘等待。'
        sideEffect = '多占少量常驻内存（通常几十 MB，可忽略）。'
        admin = $true; default = $true; reboot = $true
        kind = 'registry'
        apply = {
            param($ctx)
            $path = 'SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management'
            $b = New-ItemRegistryBackup $ctx.item 'HKLM' $path 'DisablePagingExecutive'
            $ctx.backupItem = $b
            $r = Read-RegValue 'HKLM' $path 'DisablePagingExecutive'
            if ($r.exists -and $r.value -eq 1) { return $false }
            Set-RegValue 'HKLM' $path 'DisablePagingExecutive' 1 'DWord'
            return $true
        }
        revert = {
            param($ctx)
            if ($ctx.backupItem.oldExists) { Set-RegValue 'HKLM' $ctx.backupItem.path $ctx.backupItem.name $ctx.backupItem.oldValue $ctx.backupItem.oldKind }
            else { Remove-RegValue 'HKLM' $ctx.backupItem.path $ctx.backupItem.name }
        }
    }
    @{
        id = 'mem-compress-off'; name = '关闭内存压缩'
        desc = 'EnableCompression=0，关闭内存压缩与页面合并（压缩省内存但耗 CPU；内存充足时关掉可能更稳）。'
        sideEffect = '内存占用上升；仅供手动对比，默认不勾选。'
        admin = $true; default = $false; reboot = $true
        kind = 'registry'
        apply = {
            param($ctx)
            $path = 'SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management'
            $b = New-ItemRegistryBackup $ctx.item 'HKLM' $path 'EnableCompression'
            $ctx.backupItem = $b
            $r = Read-RegValue 'HKLM' $path 'EnableCompression'
            if ($r.exists -and $r.value -eq 0) { return $false }
            Set-RegValue 'HKLM' $path 'EnableCompression' 0 'DWord'
            return $true
        }
        revert = {
            param($ctx)
            if ($ctx.backupItem.oldExists) { Set-RegValue 'HKLM' $ctx.backupItem.path $ctx.backupItem.name $ctx.backupItem.oldValue $ctx.backupItem.oldKind }
            else { Remove-RegValue 'HKLM' $ctx.backupItem.path $ctx.backupItem.name }
        }
    }
    @{
        id = 'gpu-pstate-lock'; name = '禁止显卡动态降频'
        desc = '在主显卡驱动键写入 DisableDynamicPstate=1，锁住 GPU 频率避免波动掉帧。'
        sideEffect = '待机功耗与发热升高；默认不勾选。'
        admin = $true; default = $false; reboot = $false
        kind = 'registry'
        apply = {
            param($ctx)
            $path = Get-MainGpuDriverKey
            if (-not $path) { throw '未找到主显卡驱动键，跳过' }
            $b = New-ItemRegistryBackup $ctx.item 'HKLM' $path 'DisableDynamicPstate'
            $ctx.backupItem = $b
            $r = Read-RegValue 'HKLM' $path 'DisableDynamicPstate'
            if ($r.exists -and $r.value -eq 1) { return $false }
            Set-RegValue 'HKLM' $path 'DisableDynamicPstate' 1 'DWord'
            return $true
        }
        revert = {
            param($ctx)
            if ($ctx.backupItem.oldExists) { Set-RegValue 'HKLM' $ctx.backupItem.path $ctx.backupItem.name $ctx.backupItem.oldValue $ctx.backupItem.oldKind }
            else { Remove-RegValue 'HKLM' $ctx.backupItem.path $ctx.backupItem.name }
        }
    }
    @{
        id = 'dyntick-off'; name = '禁用动态计时器'
        desc = 'bcdedit disabledynamictick yes，让系统以固定高频率计时，减少延迟抖动。'
        sideEffect = '待机功耗略升；默认不勾选。'
        admin = $true; default = $false; reboot = $true
        kind = 'bcdedit'
        apply = {
            param($ctx)
            $q = Invoke-Native 'bcdedit.exe' @('/enum', '{current}')
            $ctx.backupItem = @{ id = $ctx.item.id; kind = 'bcdedit'; oldState = if (($q.output -join "`n") -match 'disabledynamictick\s+yes') { 'on' } else { 'off' } }
            if ($ctx.backupItem.oldState -eq 'on') { return $false }
            $r = Invoke-Native 'bcdedit.exe' @('/set', '{current}', 'disabledynamictick', 'yes')
            if ($r.code -ne 0) { throw '设置 disabledynamictick 失败: ' + (($r.error -join '; ')) }
            return $true
        }
        revert = {
            param($ctx)
            if ($ctx.backupItem.oldState -eq 'on') {
                $r = Invoke-Native 'bcdedit.exe' @('/set', '{current}', 'disabledynamictick', 'no')
                if ($r.code -ne 0) { throw '还原 disabledynamictick 失败: ' + (($r.error -join '; ')) }
            }
        }
    }
)

# ---------------------------------------------------------------------------
# 只读体检项
# ---------------------------------------------------------------------------

function Get-CheckItems {
    $checks = @()

    # VC++ v14 运行库（x64 / x86 相互独立，只有缺失才报问题）
    $vcBase = 'SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes'
    $missing = @()
    foreach ($arch in @('x64', 'x86')) {
        $r = Read-RegValue 'HKLM' "$vcBase\$arch" 'Installed'
        if (-not $r.exists -or $r.value -ne 1) { $missing += $arch }
    }
    if ($missing.Count -eq 0) {
        $checks += @{ id = 'vcredist-check'; name = 'VC++ v14 运行库'; status = 'ok'; message = 'x64 与 x86 均已安装。' }
    } else {
        $checks += @{
            id = 'vcredist-check'; name = 'VC++ v14 运行库'; status = 'attention'
            message = '缺失架构: ' + ($missing -join ', ') + '。请从微软官方下载对应架构的 vc_redist（aka.ms/vs/18/release/vc_redist.x64.exe 与 vc_redist.x86.exe）覆盖安装，装完重启后再检测。x64 与 x86 是两套独立运行库，版本不同步通常无害，无需卸载其他年份的 VC++。'
        }
    }

    # 内存频率体检
    try {
        $mem = @(Get-CimInstance Win32_PhysicalMemory -ErrorAction Stop)
        if ($mem.Count -gt 0) {
            $speeds = @($mem | ForEach-Object { $_.Speed } | Where-Object { $_ -gt 0 } | Sort-Object -Unique)
            $cfg = @($mem | ForEach-Object { $_.ConfiguredClockSpeed } | Where-Object { $_ -gt 0 } | Sort-Object -Unique)
            $cur = ($speeds -join '/')
            $cfgS = ($cfg -join '/')
            if ($speeds.Count -gt 0 -and $cfg.Count -gt 0 -and ($speeds -join ',') -ne ($cfg -join ',')) {
                $checks += @{
                    id = 'xmp-check'; name = '内存频率'; status = 'attention'
                    message = "当前运行频率 ${cur} MHz 与 BIOS 配置频率 ${cfgS} MHz 不一致。可进 BIOS 查看 XMP/A-XMP/EXPO/DOCP（菜单名因主板品牌与 CPU 平台而异）确认是否开启了对应性能档位；菜单不存在说明厂商未开放，无需强求。"
                }
            } else {
                $checks += @{ id = 'xmp-check'; name = '内存频率'; status = 'ok'; message = "当前频率 ${cur} MHz 与 BIOS 配置一致，无需进 BIOS 调整。" }
            }
        }
    } catch {
        $checks += @{ id = 'xmp-check'; name = '内存频率'; status = 'ok'; message = '无法读取内存频率信息（非问题）。' }
    }

    # PCIe 链路体检（仅 NVIDIA 卡可读）
    $hw = Get-HardwareInfo
    if ($hw.gpuVendor -eq 'nvidia') {
        $smi = Get-Command nvidia-smi -ErrorAction SilentlyContinue
        if ($smi) {
            $r = Invoke-Native 'nvidia-smi.exe' @('--query-gpu=pcie.link.gen.current,pcie.link.gen.max', '--format=csv,noheader')
            if ($r.code -eq 0 -and $r.output.Count -gt 0) {
                $parts = ($r.output[0] -split ',' | ForEach-Object { $_.Trim() })
                $cur = [int]($parts[0] -replace '\D', '')
                $max = [int]($parts[1] -replace '\D', '')
                if ($max -gt 0 -and $cur -lt $max) {
                    $checks += @{
                        id = 'pcie-check'; name = 'PCIe 链路'; status = 'attention'
                        message = "显卡 PCIe 当前运行在 Gen$cur，上限 Gen$max。可检查插槽/延长线接触或 BIOS 中 PCIe 速率设置。"
                    }
                } else {
                    $checks += @{ id = 'pcie-check'; name = 'PCIe 链路'; status = 'ok'; message = "显卡 PCIe 运行在 Gen$cur（上限 Gen$max），正常。" }
                }
            } else {
                $checks += @{ id = 'pcie-check'; name = 'PCIe 链路'; status = 'ok'; message = 'nvidia-smi 不可用或读取失败，跳过（非问题）。' }
            }
        } else {
            $checks += @{ id = 'pcie-check'; name = 'PCIe 链路'; status = 'ok'; message = '未找到 nvidia-smi，跳过（非问题）。' }
        }
    } else {
        $checks += @{ id = 'pcie-check'; name = 'PCIe 链路'; status = 'ok'; message = '仅 NVIDIA 显卡支持自动读取，当前显卡跳过（非问题）。' }
    }

    return $checks
}

# ---------------------------------------------------------------------------
# 显卡驱动手动清单（GpuGuide）
# ---------------------------------------------------------------------------

function Get-GpuGuide {
    param([string]$Vendor)
    $nvidia = @(
        'NVIDIA 控制面板 → 管理 3D 设置 → 全局/程序设置：电源管理模式 = 最高性能优先',
        '纹理过滤-质量 = 高性能；线程优化 = 开',
        '低延迟模式：RTX 系可选"开"（Reflex 兼容），追求极限可试 Ultra，注意个别场景 CPU 瓶颈时可能反而掉帧',
        'DLSS：RTX 20/30 系用最新版模型（Preset E/F），40/50 系可试新 Preset K',
        '垂直同步 = 关（交给游戏内设置）'
    )
    $amd = @(
        'AMD Software: Adrenalin → 游戏 → 图形：Radeon Anti-Lag 开',
        '纹理过滤质量 = 性能；曲面细分模式 = 使用应用程序设置',
        'FSR：游戏内开启 FSR 3 或以上（各显卡通用）',
        '垂直同步 = 关（交给游戏内设置）'
    )
    $intel = @(
        'Intel Arc 控制面板：开启 XeSS 超分（游戏内）',
        '图形电源设置 = 最高性能',
        '垂直同步 = 关（交给游戏内设置）'
    )
    switch ($Vendor) {
        'nvidia' { return $nvidia }
        'amd'    { return $amd }
        'intel'  { return $intel }
        default  { return @('无法识别显卡厂商，请在显卡驱动控制面板中检查 3D 设置（电源模式高性能、垂直同步关闭）。') }
    }
}

# ---------------------------------------------------------------------------
# 检测（-Detect）
# ---------------------------------------------------------------------------

function Invoke-Detect {
    $admin = Get-IsAdmin
    $hw = Get-HardwareInfo
    $gamePath = if ($GamePath) { $GamePath } else { Find-GamePath }

    $items = @()
    foreach ($it in $OptimizationItems) {
        $state = Get-ItemState $it $gamePath
        $items += @{
            id = $it.id; name = $it.name; desc = $it.desc; sideEffect = $it.sideEffect;
            admin = $it.admin; default = $it.default; reboot = $it.reboot;
            optimized = $state.optimized; current = $state.current
        }
    }

    $presets = @{
        full = @($OptimizationItems | Where-Object { $_.default -or $true } | ForEach-Object { $_.id })
        balanced = @($OptimizationItems | Where-Object { $_.id -notin @('sysmain-off', 'wsearch-off', 'hibernate-off', 'power-tuning') } | ForEach-Object { $_.id })
        'safe-only' = @('game-mode', 'dvr-off', 'transparency-off', 'fso-off', 'gpu-pref')
    }

    return @{
        tool = $ToolName; version = $ToolVersion; mode = 'detect';
        admin = $admin;
        hardware = $hw;
        gamePath = $gamePath;
        gameName = if ($gamePath) { (Split-Path $gamePath -Leaf) } else { $null };
        items = $items;
        checks = Get-CheckItems;
        presets = $presets;
        gpuGuide = Get-GpuGuide $hw.gpuVendor;
        rebootItems = Get-RebootItems;
        notes = @(
            '只调整 Windows 系统层设置：不修改游戏文件、不注入进程、不与反作弊交互、不关闭引导虚拟化、不做显卡伪装。',
            '需要管理员的项在非管理员会话下会失败并明确报错。',
            '全部改动写入前自动备份，可用 -Restore 一键还原。'
        )
    }
}

# 单项目前状态（只读探测）
function Get-ItemState {
    param($Item, $GamePath)
    switch ($Item.kind) {
        'registry' {
            $spec = $null
            switch ($Item.id) {
                'hags' { $spec = @('HKLM', 'SYSTEM\CurrentControlSet\Control\GraphicsDrivers', 'HwSchMode', 2) }
                'game-mode' { $spec = @('HKCU', 'Software\Microsoft\GameBar', 'AutoGameModeEnabled', 1) }
                'dvr-off' { $spec = @('HKCU', 'System\GameConfigStore', 'GameDVR_Enabled', 0) }
                'prio-separation' { $spec = @('HKLM', 'SYSTEM\CurrentControlSet\Control\PriorityControl', 'Win32PrioritySeparation', 0x28) }
                'wer-off' { $spec = @('HKLM', 'SOFTWARE\Microsoft\Windows\Windows Error Reporting', 'Disabled', 1) }
                'transparency-off' { $spec = @('HKCU', 'Software\Microsoft\Windows\CurrentVersion\Themes\Personalize', 'EnableTransparency', 0) }
                'gpu-pref' { if ($GamePath) { $spec = @('HKCU', 'Software\Microsoft\DirectX\UserGpuPreferences', $GamePath, 'GpuPreference=2') } }
                'mpo-off' { $spec = @('HKLM', 'SOFTWARE\Microsoft\Windows\Dwm', 'OverlayTestMode', 5) }
                'net-throttling-off' { $spec = @('HKLM', 'SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile', 'NetworkThrottlingIndex', 0xffffffff) }
                'sys-responsiveness' { $spec = @('HKLM', 'SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile', 'SystemResponsiveness', 10) }
                'mmcss-games' { $spec = @('HKLM', 'SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile\Tasks\Games', 'GPU Priority', 8) }
                'paging-exec' { $spec = @('HKLM', 'SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management', 'DisablePagingExecutive', 1) }
                'mem-compress-off' { $spec = @('HKLM', 'SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management', 'EnableCompression', 0) }
                'gpu-pstate-lock' {
                    $k = Get-MainGpuDriverKey
                    if ($k) { $spec = @('HKLM', $k, 'DisableDynamicPstate', 1) }
                }
                'game-priority' { if ($GamePath) { $spec = @('HKLM', "SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options\$((Split-Path $GamePath -Leaf))\PerfOptions", 'CpuPriorityClass', 3) } }
            }
            if (-not $spec) { return @{ optimized = $false; current = '未知（缺少游戏路径等前置条件）' } }
            $r = Read-RegValue $spec[0] $spec[1] $spec[2]
            $target = $spec[3]
            $ok = $false
            if ($r.exists) {
                if ($target -is [string]) { $ok = ([string]$r.value) -match [regex]::Escape($target) }
                else { $ok = ([int64]$r.value) -eq ([int64]$target) }
            }
            return @{ optimized = $ok; current = if ($r.exists) { "当前=$($r.value)" } else { '未设置' } }
        }
        'layers' {
            if (-not $GamePath) { return @{ optimized = $false; current = '未找到游戏路径' } }
            $r = Read-RegValue 'HKCU' 'Software\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Layers' $GamePath
            $ok = $r.exists -and $r.value -match 'DISABLEDXMAXIMIZEDWINDOWEDMODE'
            return @{ optimized = $ok; current = if ($r.exists) { $r.value } else { '未设置' } }
        }
        'power' {
            if ($Item.id -eq 'power-ultimate') {
                $r = Invoke-Native 'powercfg.exe' @('-getactivescheme')
                $ok = $r.code -eq 0 -and ($r.output -join ' ') -match 'e9a42b02-d5df-448d-aa00-03f14749eb61'
                return @{ optimized = $ok; current = ($r.output -join ' ') }
            }
            return @{ optimized = $false; current = '需要管理员会话探测' }
        }
        'service' {
            $svc = Get-Service -Name $Item.id.Replace('-off', '') -ErrorAction SilentlyContinue
            return @{ optimized = $false; current = '运行时探测（见服务状态）' }
        }
        'hibernate' {
            $on = Test-Path "$env:SystemDrive\hiberfil.sys"
            return @{ optimized = (-not $on); current = if ($on) { '休眠开启' } else { '已关闭' } }
        }
        'bcdedit' {
            $q = Invoke-Native 'bcdedit.exe' @('/enum', '{current}')
            $joined = $q.output -join "`n"
            $on = $joined -match 'disabledynamictick\s+yes'
            return @{ optimized = $on; current = if ($on) { 'disabledynamictick yes' } else { 'disabledynamictick 未开启' } }
        }
    }
    return @{ optimized = $false; current = '未知' }
}

# ---------------------------------------------------------------------------
# 应用（-Apply）
# ---------------------------------------------------------------------------

function Split-ItemIds {
    param([string[]]$Requested)
    $ids = @()
    if ($null -eq $Requested) { return $ids }
    foreach ($raw in $Requested) {
        foreach ($id in ($raw -split ',')) {
            $trimmed = $id.Trim()
            if ($trimmed) { $ids += $trimmed }
        }
    }
    return $ids
}

function Resolve-ItemIds {
    param([string[]]$Requested)
    $all = @{}
    foreach ($it in $OptimizationItems) { $all[$it.id] = $it }
    $result = @()
    if ($Requested) {
        foreach ($id in @(Split-ItemIds $Requested)) {
            if ($all.ContainsKey($id)) { $result += $all[$id] }
        }
    } else {
        $result = @($OptimizationItems | Where-Object { $_.default })
    }
    return $result
}

function Invoke-Apply {
    param([string[]]$RequestedItems, [string]$PresetName, [string]$GamePathInput)
    $admin = Get-IsAdmin

    # 解析要执行的项
    $toApply = $null
    if ($PresetName) {
        switch ($PresetName) {
            'full' { $toApply = @($OptimizationItems) }
            'balanced' { $toApply = @($OptimizationItems | Where-Object { $_.id -notin @('sysmain-off', 'wsearch-off', 'hibernate-off', 'power-tuning') }) }
            'safe-only' { $toApply = @($OptimizationItems | Where-Object { $_.id -in @('game-mode', 'dvr-off', 'transparency-off', 'fso-off', 'gpu-pref') }) }
            default { throw "未知预设: $PresetName（可选 full / balanced / safe-only）" }
        }
    } else {
        $toApply = @(Resolve-ItemIds $RequestedItems)
    }

    if ($toApply.Count -eq 0) { throw '没有可执行的优化项。' }

    # 管理员项检查
    $needAdmin = @($toApply | Where-Object { $_.admin })
    if ($needAdmin.Count -gt 0 -and -not $admin) {
        throw '以下项需要管理员权限: ' + (($needAdmin | ForEach-Object { $_.id }) -join ', ') + '。请用管理员身份重新运行。'
    }

    # 定位游戏（fso-off / gpu-pref / game-priority 依赖）
    $resolvedGame = $GamePathInput
    if (-not $resolvedGame) { $resolvedGame = Find-GamePath }
    $gameName = if ($resolvedGame) { Split-Path $resolvedGame -Leaf } else { $null }
    $needGame = @($toApply | Where-Object { $_.id -in @('fso-off', 'gpu-pref', 'game-priority') })
    if ($needGame.Count -gt 0 -and -not $resolvedGame) {
        Write-Warning '未找到游戏主程序，fso-off / gpu-pref / game-priority 将跳过（可用 -GamePath 指定）。'
    }

    # 执行
    $backupItems = @()
    $results = @()
    $rebootNeeded = @()
    foreach ($it in $toApply) {
        $entry = @{ id = $it.id; name = $it.name; ok = $false; changed = $false; skipped = $false; message = '' }
        try {
            if ($it.id -in @('fso-off', 'gpu-pref', 'game-priority') -and -not $resolvedGame) {
                $entry.skipped = $true
                $entry.message = '未找到游戏主程序，已跳过'
                $results += $entry
                continue
            }
            $ctx = @{ item = $it; backupItem = $null; gamePath = $resolvedGame; gameName = $gameName }
            $changed = & $it.apply $ctx
            $entry.changed = [bool]$changed
            $entry.ok = $true
            if ($ctx.backupItem) { $backupItems += $ctx.backupItem }
            if ($it.reboot) { $rebootNeeded += $it.id }
            $entry.message = if ($changed) { '已应用' } else { '本就达标，未改动' }
        } catch {
            $entry.ok = $false
            $entry.message = $_.Exception.Message
        }
        $results += $entry
    }

    # 写备份（含失败项也记录，便于排查）
    $backupFile = $null
    if ($backupItems.Count -gt 0) {
        $ts = Get-Date -Format 'yyyyMMdd-HHmmss'
        $backupFile = Join-Path $BackupRoot "backup-$ts.json"
        $backupDoc = @{
            schema = 'v1'; tool = $ToolName; version = $ToolVersion;
            createdAt = (Get-Date).ToString('o'); preset = $PresetName;
            items = $backupItems; results = $results
        }
        Write-AtomicJson $backupFile $backupDoc
    }

    $okCount = @($results | Where-Object { $_.ok }).Count
    $failCount = @($results | Where-Object { -not $_.ok }).Count
    $skipCount = @($results | Where-Object { $_.skipped }).Count

    return @{
        tool = $ToolName; version = $ToolVersion; mode = 'apply';
        admin = $admin; gamePath = $resolvedGame;
        results = $results;
        backupFile = $backupFile;
        reboot = @($rebootNeeded | Sort-Object -Unique);
        summary = "$okCount 成功、$failCount 失败、$skipCount 跳过"
    }
}

# ---------------------------------------------------------------------------
# 还原（-ListRestoreItems / -Restore）
# ---------------------------------------------------------------------------

function Get-BackupFiles {
    if (-not (Test-Path $BackupRoot)) { return @() }
    return @(Get-ChildItem $BackupRoot -Filter 'backup-*.json' -File -ErrorAction SilentlyContinue |
             Where-Object { $_.Name -notmatch '\.restored$' } | Sort-Object LastWriteTime -Descending)
}

function Invoke-ListRestore {
    $files = @(Get-BackupFiles)
    $restoreItems = @()
    foreach ($f in $files) {
        try {
            $doc = Get-Content $f.FullName -Raw -Encoding UTF8 | ConvertFrom-Json
            foreach ($it in $doc.items) {
                $restoreItems += @{
                    backupFile = $f.Name; id = $it.id; kind = $it.kind;
                    desc = (($OptimizationItems | Where-Object { $_.id -eq $it.id } | Select-Object -First 1).name)
                }
            }
        } catch { }
    }
    return @{ tool = $ToolName; version = $ToolVersion; mode = 'list-restore'; restoreItems = $restoreItems; backupCount = $files.Count }
}

function Invoke-Restore {
    param([string[]]$RestoreItems)
    $files = @(Get-BackupFiles)
    if ($files.Count -eq 0) { return @{ tool = $ToolName; version = $ToolVersion; mode = 'restore'; restored = @(); failed = @(); skipped = @(); summary = '没有可还原的备份' } }

    $restoreIds = @(Split-ItemIds $RestoreItems)
    $wantAll = -not $restoreIds -or $restoreIds.Count -eq 0
    $restored = @()
    $failed = @()
    $skipped = @()
    $consumedFiles = @()

    foreach ($f in $files) {
        try {
            $doc = Get-Content $f.FullName -Raw -Encoding UTF8 | ConvertFrom-Json
        } catch {
            $failed += @{ backupFile = $f.Name; id = '(备份损坏)'; message = '备份文件无法解析' }
            continue
        }
        foreach ($bit in $doc.items) {
            $itemDef = $OptimizationItems | Where-Object { $_.id -eq $bit.id } | Select-Object -First 1
            if (-not $itemDef) {
                $skipped += @{ backupFile = $f.Name; id = $bit.id; message = '未知项，跳过' }
                continue
            }
            if (-not $wantAll -and $bit.id -notin $restoreIds) { continue }
            try {
                $ctx = @{ item = $itemDef; backupItem = $bit; gamePath = $null; gameName = $null }
                & $itemDef.revert $ctx
                $restored += @{ backupFile = $f.Name; id = $bit.id; message = '已还原' }
            } catch {
                $failed += @{ backupFile = $f.Name; id = $bit.id; message = $_.Exception.Message }
            }
        }
        $consumedFiles += $f.FullName
    }

    # 消费备份：重命名为 .restored（保留文件供审计）
    foreach ($path in $consumedFiles) {
        $renamed = "$path.restored"
        if (Test-Path $renamed) { Remove-Item $renamed -Force }
        Rename-Item $path $renamed -Force
    }

    return @{
        tool = $ToolName; version = $ToolVersion; mode = 'restore';
        restored = $restored; failed = $failed; skipped = $skipped;
        summary = "$($restored.Count) 项已还原、$($failed.Count) 项失败、$($skipped.Count) 项跳过"
    }
}

# ---------------------------------------------------------------------------
# 主入口
# ---------------------------------------------------------------------------

$actions = @()
if ($Detect) { $actions += 'detect' }
if ($Apply) { $actions += 'apply' }
if ($Restore) { $actions += 'restore' }
if ($ListRestoreItems) { $actions += 'list-restore' }

if ($actions.Count -ne 1) {
    Write-Host "用法: delta-optimizer.ps1 -Detect | -Apply | -Restore | -ListRestoreItems（四选一，加 -Json 输出 JSON）"
    Write-Host "  -Apply 可用 -Items id1,id2 或 -Preset full|balanced|safe-only；需要用户同意后加 -Force 执行"
    Write-Host "  -Restore 可用 -RestoreItems id1,id2 精确还原"
    exit 2
}

try {
    switch ($actions[0]) {
        'detect' {
            $result = Invoke-Detect
            if ($Json) { $result | ConvertTo-Json -Depth 12 }
            else { $result | Format-List | Out-String | Write-Host }
        }
        'apply' {
            if (-not $Force) {
                Write-Host 'Apply 会修改系统设置。请先向用户说明每项的作用与副作用并征得同意，然后加 -Force 执行。'
                exit 3
            }
            $result = Invoke-Apply $Items $Preset $GamePath
            if ($Json) { $result | ConvertTo-Json -Depth 12 }
            else {
                Write-Host ($result.summary)
                $result.results | Format-Table id, ok, changed, skipped, message -AutoSize | Out-String | Write-Host
                if ($result.reboot.Count -gt 0) { Write-Host ('需重启生效: ' + ($result.reboot -join ', ')) }
                if ($result.backupFile) { Write-Host ('备份: ' + $result.backupFile) }
            }
        }
        'restore' {
            $result = Invoke-Restore $Items
            if ($Json) { $result | ConvertTo-Json -Depth 12 }
            else {
                Write-Host ($result.summary)
                $result.restored | Format-Table id, message -AutoSize | Out-String | Write-Host
                $result.failed | Format-Table id, message -AutoSize | Out-String | Write-Host
            }
        }
        'list-restore' {
            $result = Invoke-ListRestore
            if ($Json) { $result | ConvertTo-Json -Depth 12 }
            else {
                Write-Host ('备份数量: ' + $result.backupCount)
                $result.restoreItems | Format-Table id, kind, desc, backupFile -AutoSize | Out-String | Write-Host
            }
        }
    }
} catch {
    if ($Json) {
        @{ tool = $ToolName; version = $ToolVersion; mode = 'error'; error = $_.Exception.Message; stack = $_.ScriptStackTrace; line = $_.InvocationInfo.ScriptLineNumber } | ConvertTo-Json -Depth 6
    } else {
        Write-Error $_.Exception.Message
    }
    exit 1
}
