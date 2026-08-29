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

# JSON 输出走 UTF8，避免中文经管道被转码（CI 可稳定解析）
try { [Console]::OutputEncoding = [System.Text.Encoding]::UTF8 } catch { }
$ErrorActionPreference = 'Stop'

$ToolName    = 'delta-optimizer'
$BackupRoot  = Join-Path $env:LOCALAPPDATA 'FpsTune\backup'

# ---------------------------------------------------------------------------
# 单一数据源
#   优化项声明与预设 -> catalog/catalog.json（CLI / WPF GUI / 文档三方共用）。
#   本文件的条目表只保留 id + apply/revert 实现块，运行时从 catalog 注入元信息；
#   任一侧多出或缺少某个 id 都会直接报错，防止 GUI 与 CLI 静默漂移。
#   版本号唯一来源为仓库根 Directory.Build.props 的 <Version>。
# ---------------------------------------------------------------------------
$script:CatalogCache = $null
$script:ToolVersionResolved = $null

function Get-RepoFile {
    param([string]$Relative)
    $dir = $PSScriptRoot
    for ($i = 0; $i -lt 6 -and $dir; $i++) {
        $candidate = Join-Path $dir $Relative
        if (Test-Path $candidate) { return $candidate }
        $parent = Split-Path $dir -Parent
        if ($parent -eq $dir) { break }
        $dir = $parent
    }
    return $null
}

function Get-Catalog {
    if ($script:CatalogCache) { return $script:CatalogCache }
    $path = Get-RepoFile 'catalog/catalog.json'
    if (-not $path) { throw '未找到 catalog/catalog.json（须与脚本同仓库分发）' }
    try {
        $json = Get-Content $path -Raw -Encoding UTF8 | ConvertFrom-Json
    } catch { throw "catalog.json 解析失败: $($_.Exception.Message)" }
    $script:CatalogCache = $json
    return $json
}

function Get-ToolVersion {
    if ($script:ToolVersionResolved) { return $script:ToolVersionResolved }
    $v = 'dev'
    $propsPath = Get-RepoFile 'Directory.Build.props'
    if (-not $propsPath) {
        $fallback = Join-Path $PSScriptRoot 'Directory.Build.props'
        if (Test-Path $fallback) { $propsPath = $fallback }
    }
    if ($propsPath) {
        try {
            [xml]$xml = Get-Content $propsPath -Raw -Encoding UTF8
            foreach ($pg in @($xml.Project.PropertyGroup)) {
                if ($pg.Version) { $v = [string]$pg.Version; break }
            }
        } catch { }
    }
    $script:ToolVersionResolved = $v
    return $v
}

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

# 查询某电源设置的当前 AC 值（失败返回 $null），用于 power-tuning 无损备份。
function Get-PowerAcIndex {
    param([string]$Subgroup, [string]$Setting)
    $r = Invoke-Native 'powercfg.exe' @('/query', 'SCHEME_CURRENT', $Subgroup, $Setting)
    if ($r.code -ne 0) { return $null }
    foreach ($line in $r.output) {
        if ($line -notmatch 'AC' -and $line -notmatch '交流') { continue }
        if ($line -match '0x([0-9a-fA-F]+)') {
            try { return [int][Convert]::ToInt64($matches[1], 16) } catch { return $null }
        }
    }
    return $null
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
    # 由 catalog 的 reboot 标志驱动（原手写列表已收敛进单一数据源）
    return @($OptimizationItems | Where-Object { $_.reboot } | ForEach-Object { $_.id })
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
    $gpus = @(Get-CimInstance Win32_VideoController | Where-Object { $_.Name -and $_.Name -notmatch 'Basic Render|Hyper-V|Virtual|RemoteFX' })
    if ($gpus.Count -eq 0) { $gpus = @(Get-CimInstance Win32_VideoController | Where-Object { $_.Name }) }

    # WMI 的 Name 可能是陈旧的友好名（新显卡 + 旧驱动时常见），注册表 Class 键的
    # DriverDesc 才是当前驱动写入的准确名称；用 MatchingDeviceId <-> PNPDeviceID 对齐。
    $classBase = 'SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}'
    $regMap = @{}
    try {
        $root = [Microsoft.Win32.Registry]::LocalMachine
        $ck = $root.OpenSubKey($classBase)
        if ($ck) {
            foreach ($sub in @($ck.GetSubKeyNames() | Where-Object { $_ -match '^00\d\d$' })) {
                $sk = $ck.OpenSubKey($sub)
                if (-not $sk) { continue }
                $desc = [string]$sk.GetValue('DriverDesc')
                $matchId = ([string]$sk.GetValue('MatchingDeviceId')).ToLowerInvariant()
                $mem = $sk.GetValue('HardwareInformation.qwMemorySize')
                if ($desc -and $matchId) { $regMap[$matchId] = @{ desc = $desc; mem = $mem } }
                $sk.Close()
            }
            $ck.Close()
        }
    } catch { }

    $names = @()
    foreach ($g in $gpus) {
        $name = [string]$g.Name
        # MatchingDeviceId 不含 REV 与实例号，先把 WMI 的 PNP ID 截齐再比对
        $pnp = ([string]$g.PNPDeviceID).ToLowerInvariant()
        $revIdx = $pnp.IndexOf('&rev_')
        if ($revIdx -gt 0) { $pnp = $pnp.Substring(0, $revIdx) }
        if ($regMap.ContainsKey($pnp)) {
            $name = [string]$regMap[$pnp].desc
            $mem = $regMap[$pnp].mem
            if (($mem -is [long] -or $mem -is [int]) -and $mem -gt 0) {
                $name = "$name · $([math]::Round($mem / 1GB)) GB"
            }
        }
        $names += $name
    }
    $gpu = $names[0]
    $gpuFull = $names -join ' | '
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
    # 1) 正在运行的游戏进程（覆盖主流 FPS）
    $procNames = @('cs2', 'VALORANT-Win64-Shipping', 'r5apex_dx12', 'TslGame',
                   'Overwatch', 'cod', 'TheFinals',
                   'DeltaForceClient-Win64-Shipping', 'DeltaForceClient', 'DeltaForce')
    try {
        foreach ($name in $procNames) {
            $p = Get-Process -Name $name -ErrorAction SilentlyContinue | Select-Object -First 1
            if ($p -and $p.Path -and (Test-Path $p.Path)) { return $p.Path }
        }
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
                if ($dn -and ($dn -match '三角洲|Delta Force|DeltaForce|Counter-Strike|CS2|CS 2|VALORANT|Apex Legends|PUBG|绝地求生|Call of Duty|使命召唤|Overwatch|守望先锋|THE FINALS')) {
                    $loc = $a.InstallLocation
                    if ($loc -and (Test-Path $loc)) {
                        $exeNames = @('DeltaForceClient-Win64-Shipping.exe','cs2.exe','VALORANT-Win64-Shipping.exe','r5apex_dx12.exe','TslGame.exe','Overwatch.exe','cod.exe','TheFinals.exe')
                        $exe = Get-ChildItem $loc -Recurse -Include $exeNames -File -ErrorAction SilentlyContinue -Depth 4 |
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
        id = 'power-ultimate'
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
        id = 'power-tuning'
        apply = {
            param($ctx)
            $specs = @(
                @{ key = 'usb';   sub = '2a737441-1930-4402-8d77-b2bebba308a3'; setting = '48e6b7a6-50f5-4782-a5d4-53bb8f07e226' },
                @{ key = 'boost'; sub = 'be337238-0d82-4146-a960-4f3749d470c7'; setting = '45bcc044-d885-43e2-8605-ee0ec6e96b59' },
                @{ key = 'idle';  sub = 'bd3b718a-0680-4d9d-8ab2-e1d2b4ac806d'; setting = '4f2f7c6f-5e88-40dd-bad6-c8e8e0f8a9b3' }
            )
            # 先备份三项的当前 AC 值（无损还原）；读取失败的项记为 null，还原时回退到常见默认值。
            $oldValues = @{}
            foreach ($s in $specs) { $oldValues[$s.key] = Get-PowerAcIndex $s.sub $s.setting }
            $ctx.backupItem = @{ id = $ctx.item.id; kind = 'power-tuning'; oldValues = $oldValues }

            foreach ($s in $specs) {
                Invoke-Native 'powercfg.exe' @('-attributes', $s.sub, $s.setting, '-ATTRIB_HIDE') | Out-Null
            }
            Invoke-Native 'powercfg.exe' @('-setacvalueindex', 'SCHEME_CURRENT', $specs[0].sub, $specs[0].setting, '0') | Out-Null
            Invoke-Native 'powercfg.exe' @('-setacvalueindex', 'SCHEME_CURRENT', $specs[1].sub, $specs[1].setting, '2') | Out-Null
            Invoke-Native 'powercfg.exe' @('-setacvalueindex', 'SCHEME_CURRENT', $specs[2].sub, $specs[2].setting, '0')
            $apply = Invoke-Native 'powercfg.exe' @('-setactive', 'SCHEME_CURRENT')
            if ($apply.code -ne 0) { throw '应用电源隐藏项失败: ' + (($apply.error -join '; ')) }
            return $true
        }
        revert = {
            param($ctx)
            $b = $ctx.backupItem
            $defaults = @{
                usb   = @{ sub = '2a737441-1930-4402-8d77-b2bebba308a3'; setting = '48e6b7a6-50f5-4782-a5d4-53bb8f07e226'; value = 1 }
                boost = @{ sub = 'be337238-0d82-4146-a960-4f3749d470c7'; setting = '45bcc044-d885-43e2-8605-ee0ec6e96b59'; value = 0 }
                idle  = @{ sub = 'bd3b718a-0680-4d9d-8ab2-e1d2b4ac806d'; setting = '4f2f7c6f-5e88-40dd-bad6-c8e8e0f8a9b3'; value = 1 }
            }
            # 优先还原备份的原值；旧版本备份没有该数据时回退到系统常见默认值。
            $old = $null
            if ($b -is [hashtable]) { $old = $b['oldValues'] }
            else {
                $prop = $b.PSObject.Properties['oldValues']
                if ($prop) { $old = $prop.Value }
            }
            foreach ($key in @('usb', 'boost', 'idle')) {
                $d = $defaults[$key]
                $value = $d.value
                $recorded = $null
                if ($null -ne $old) {
                    if ($old -is [hashtable]) { $recorded = $old[$key] }
                    else {
                        $p = $old.PSObject.Properties[$key]
                        if ($p) { $recorded = $p.Value }
                    }
                }
                if ($null -ne $recorded) { $value = [int]$recorded }
                Invoke-Native 'powercfg.exe' @('-setacvalueindex', 'SCHEME_CURRENT', $d.sub, $d.setting, [string]$value) | Out-Null
            }
            Invoke-Native 'powercfg.exe' @('-setactive', 'SCHEME_CURRENT') | Out-Null
        }
    }
    @{
        id = 'hags'
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
        id = 'game-mode'
        apply = {
            param($ctx)
            $base = 'Software\Microsoft\GameBar'
            $b = New-ItemRegistryBackup $ctx.item 'HKCU' $base 'AutoGameModeEnabled'
            $allow = Read-RegValue 'HKCU' $base 'AllowAutoGameMode'
            # 同时备份 AllowAutoGameMode，还原时按原值恢复（无损还原）。
            $b.allowExists = $allow.exists
            $b.allowValue = $allow.value
            $ctx.backupItem = $b
            $changed = $false
            if (-not ($b.oldExists -and $b.oldValue -eq 1)) {
                Set-RegValue 'HKCU' $base 'AutoGameModeEnabled' 1 'DWord'
                $changed = $true
            }
            if (-not ($allow.exists -and $allow.value -eq 1)) {
                Set-RegValue 'HKCU' $base 'AllowAutoGameMode' 1 'DWord'
                $changed = $true
            }
            return $changed
        }
        revert = {
            param($ctx)
            $b = $ctx.backupItem
            $base = 'Software\Microsoft\GameBar'
            if ($b.oldExists) { Set-RegValue 'HKCU' $b.path $b.name $b.oldValue $b.oldKind }
            else { Remove-RegValue 'HKCU' $b.path $b.name }
            # AllowAutoGameMode 按原值恢复；旧版本备份没有该字段时保持不动，避免覆盖用户原有设置。
            $hasAllow = $b.PSObject.Properties['allowExists']
            if ($hasAllow -and $b.allowExists) {
                Set-RegValue 'HKCU' $base 'AllowAutoGameMode' $b.allowValue 'DWord'
            } elseif ($hasAllow) {
                Remove-RegValue 'HKCU' $base 'AllowAutoGameMode'
            }
        }
    }
    @{
        id = 'dvr-off'
        apply = {
            param($ctx)
            $b = New-ItemRegistryBackup $ctx.item 'HKCU' 'System\GameConfigStore' 'GameDVR_Enabled'
            $p = Read-RegValue 'HKLM' 'SOFTWARE\Policies\Microsoft\Windows\GameDVR' 'AllowGameDVR'
            # 同时备份 AllowGameDVR 策略原值，还原时按原值恢复（无损还原）。
            $b.policyExists = $p.exists
            $b.policyValue = $p.value
            $ctx.backupItem = $b
            $changed = $false
            if (-not ($b.oldExists -and $b.oldValue -eq 0)) {
                Set-RegValue 'HKCU' 'System\GameConfigStore' 'GameDVR_Enabled' 0 'DWord'
                $changed = $true
            }
            if (-not ($p.exists -and $p.value -eq 0)) {
                Set-RegValue 'HKLM' 'SOFTWARE\Policies\Microsoft\Windows\GameDVR' 'AllowGameDVR' 0 'DWord'
                $changed = $true
            }
            return $changed
        }
        revert = {
            param($ctx)
            $b = $ctx.backupItem
            if ($b.oldExists) { Set-RegValue 'HKCU' $b.path $b.name $b.oldValue $b.oldKind }
            else { Remove-RegValue 'HKCU' $b.path $b.name }
            # AllowGameDVR 策略按原值恢复；旧版本备份没有该字段时保持不动，避免误删用户/企业原有策略。
            $hasPolicy = $b.PSObject.Properties['policyExists']
            if ($hasPolicy -and $b.policyExists) {
                Set-RegValue 'HKLM' 'SOFTWARE\Policies\Microsoft\Windows\GameDVR' 'AllowGameDVR' $b.policyValue 'DWord'
            } elseif ($hasPolicy) {
                Remove-RegValue 'HKLM' 'SOFTWARE\Policies\Microsoft\Windows\GameDVR' 'AllowGameDVR'
            }
        }
    }
    @{
        id = 'prio-separation'
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
        id = 'wer-off'
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
        id = 'transparency-off'
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
        id = 'fso-off'
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
        id = 'gpu-pref'
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
        id = 'mpo-off'
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
        id = 'net-throttling-off'
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
        id = 'sys-responsiveness'
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
        id = 'mmcss-games'
        apply = {
            param($ctx)
            $base = 'SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile\Tasks\Games'
            $targets = @(
                @('GPU Priority', 8, 'DWord'),
                @('Priority', 6, 'DWord'),
                @('Scheduling Category', 'High', 'String'),
                @('SFIO Priority', 'High', 'String')
            )
            # 四个值的原值全部入备份，逐个按原值恢复（无损还原）。
            $backs = @()
            foreach ($t in $targets) {
                $r = Read-RegValue 'HKLM' $base $t[0]
                $backs += @{ name = $t[0]; exists = $r.exists; value = $r.value; kind = $t[2] }
            }
            $ctx.backupItem = @{ id = $ctx.item.id; kind = 'mmcss'; base = $base; values = $backs }
            $changed = $false
            foreach ($t in $targets) {
                $r = Read-RegValue 'HKLM' $base $t[0]
                if (-not ($r.exists -and $r.value -eq $t[1])) {
                    Set-RegValue 'HKLM' $base $t[0] $t[1] $t[2]
                    $changed = $true
                }
            }
            return $changed
        }
        revert = {
            param($ctx)
            $b = $ctx.backupItem
            $base = 'SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile\Tasks\Games'
            $vals = $null
            if ($b -is [hashtable]) { $vals = $b['values'] }
            else {
                $prop = $b.PSObject.Properties['values']
                if ($prop) { $vals = $prop.Value }
            }
            if (-not $vals) {
                # 旧版本备份只记录了 GPU Priority 且会删除全部四个值（有损）；
                # 这里保守跳过，不覆盖用户可能存在的自定义值。重新 Apply 后再 Restore 即可走新逻辑。
                return
            }
            foreach ($v in $vals) {
                if ($v.exists) { Set-RegValue 'HKLM' $base $v.name $v.value $v.kind }
                else { Remove-RegValue 'HKLM' $base $v.name }
            }
        }
    }
    @{
        id = 'sysmain-off'
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
        id = 'wsearch-off'
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
        id = 'hibernate-off'
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
        id = 'game-priority'
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
        id = 'paging-exec'
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
        id = 'mem-compress-off'
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
        id = 'gpu-pstate-lock'
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
        id = 'dyntick-off'
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
    @{
        id = 'mouse-accel-off'
        apply = {
            param($ctx)
            $path = 'HKCU:\Control Panel\Mouse'
            $names = @('MouseSpeed', 'MouseThreshold1', 'MouseThreshold2')
            $ip = Get-ItemProperty -Path $path -ErrorAction SilentlyContinue
            $old = @{}
            $changed = $false
            foreach ($n in $names) {
                $prop = $null
                if ($ip) {
                    $pp = $ip.PSObject.Properties[$n]
                    if ($pp) { $prop = [string]$pp.Value }
                }
                $old[$n] = $prop
                if ($prop -eq '0') { continue }
                Set-ItemProperty -Path $path -Name $n -Value '0' -Type String
                $changed = $true
            }
            $ctx.backupItem = @{ id = $ctx.item.id; kind = 'mouse-accel'; oldValues = $old }
            return $changed
        }
        revert = {
            param($ctx)
            $path = 'HKCU:\Control Panel\Mouse'
            $defaults = @{ MouseSpeed = '1'; MouseThreshold1 = '6'; MouseThreshold2 = '10' }
            $old = $null
            if ($ctx.backupItem -is [hashtable]) { $old = $ctx.backupItem['oldValues'] }
            else {
                $pp = $ctx.backupItem.PSObject.Properties['oldValues']
                if ($pp) { $old = $pp.Value }
            }
            foreach ($n in @('MouseSpeed', 'MouseThreshold1', 'MouseThreshold2')) {
                $value = $defaults[$n]
                if ($null -ne $old) {
                    $recorded = $null
                    if ($old -is [hashtable]) { $recorded = $old[$n] }
                    else {
                        $p2 = $old.PSObject.Properties[$n]
                        if ($p2) { $recorded = $p2.Value }
                    }
                    if ($null -ne $recorded -and "$recorded" -ne '') { $value = [string]$recorded }
                }
                Set-ItemProperty -Path $path -Name $n -Value $value -Type String
            }
        }
    }
    @{
        id = 'keyboard-latency'
        kind = 'registry'
        apply = {
            param($ctx)
            $spec = @('HKLM', 'SYSTEM\CurrentControlSet\Services\kbdclass\Parameters', 'KeyboardDataQueueSize', 50)
            $b = New-ItemRegistryBackup $ctx.item $spec[0] $spec[1] $spec[2]
            if ($b.oldExists -and "$($b.oldValue)" -eq '50') { $ctx.backupItem = $b; return $false }
            Set-RegValue $spec[0] $spec[1] $spec[2] $spec[3] 'DWord'
            $ctx.backupItem = $b
            return $true
        }
        revert = {
            param($ctx)
            $b = $ctx.backupItem
            if ($b.oldExists -and $null -ne $b.oldValue) {
                Set-RegValue $b.hive $b.path $b.name ([int]$b.oldValue) 'DWord'
            } else {
                Remove-RegValue $b.hive $b.path $b.name
            }
        }
    }
    @{
        id = 'keyboard-repeat'
        kind = 'registry'
        apply = {
            param($ctx)
            $path = 'HKCU:\Control Panel\Keyboard'
            $targets = @{ KeyboardDelay = '0'; KeyboardSpeed = '31' }
            $ip = Get-ItemProperty -Path $path -ErrorAction SilentlyContinue
            $old = @{}; $changed = $false
            foreach ($n in $targets.Keys) {
                $prop = $null
                if ($ip) { $pp = $ip.PSObject.Properties[$n]; if ($pp) { $prop = [string]$pp.Value } }
                $old[$n] = $prop
                if ($prop -eq $targets[$n]) { continue }
                Set-ItemProperty -Path $path -Name $n -Value $targets[$n] -Type String
                $changed = $true
            }
            $ctx.backupItem = @{ id = $ctx.item.id; kind = 'multi-sz'; oldValues = $old }
            return $changed
        }
        revert = {
            param($ctx)
            Restore-MultiSzValues 'HKCU:\Control Panel\Keyboard' @{ KeyboardDelay = '1'; KeyboardSpeed = '31' } $ctx.backupItem
        }
    }
    @{
        id = 'sticky-keys-off'
        kind = 'registry'
        apply = {
            param($ctx)
            $specs = @(
                @{ path = 'HKCU:\Control Panel\Accessibility\StickyKeys'; name = 'Flags'; target = '510' },
                @{ path = 'HKCU:\Control Panel\Accessibility\ToggleKeys'; name = 'Flags'; target = '58' }
            )
            $old = @{}; $changed = $false
            foreach ($sp in $specs) {
                $ip = Get-ItemProperty -Path $sp.path -ErrorAction SilentlyContinue
                $prop = $null
                if ($ip) { $pp = $ip.PSObject.Properties[$sp.name]; if ($pp) { $prop = [string]$pp.Value } }
                $old[$sp.path + '\' + $sp.name] = $prop
                if ($prop -eq $sp.target) { continue }
                Set-ItemProperty -Path $sp.path -Name $sp.name -Value $sp.target -Type String
                $changed = $true
            }
            $ctx.backupItem = @{ id = $ctx.item.id; kind = 'multi-sz'; oldValues = $old }
            return $changed
        }
        revert = {
            param($ctx)
            Restore-MultiSzValuesMultiPath $ctx.backupItem
        }
    }
    @{
        id = 'menu-delay-off'
        kind = 'registry'
        apply = {
            param($ctx)
            $path = 'HKCU:\Control Panel\Desktop'
            $b = New-ItemRegistryBackup $ctx.item 'HKCU' 'Control Panel\Desktop' 'MenuShowDelay'
            if ($b.oldExists -and "$($b.oldValue)" -eq '0') { $ctx.backupItem = $b; return $false }
            Set-RegValue 'HKCU' 'Control Panel\Desktop' 'MenuShowDelay' '0' 'String'
            $ctx.backupItem = $b
            return $true
        }
        revert = {
            param($ctx)
            $b = $ctx.backupItem
            if ($b.oldExists -and $null -ne $b.oldValue) {
                Set-RegValue 'HKCU' 'Control Panel\Desktop' 'MenuShowDelay' ([string]$b.oldValue) 'String'
            } else {
                Set-RegValue 'HKCU' 'Control Panel\Desktop' 'MenuShowDelay' '400' 'String'
            }
        }
    }
    @{
        id = 'usb-power-save-off'
        kind = 'registry'
        apply = {
            param($ctx)
            $b = New-ItemRegistryBackup $ctx.item 'HKLM' 'SYSTEM\CurrentControlSet\Services\USB' 'DisableSelectiveSuspend'
            if ($b.oldExists -and "$($b.oldValue)" -eq '1') { $ctx.backupItem = $b; return $false }
            Set-RegValue 'HKLM' 'SYSTEM\CurrentControlSet\Services\USB' 'DisableSelectiveSuspend' 1 'DWord'
            $ctx.backupItem = $b
            return $true
        }
        revert = {
            param($ctx)
            $b = $ctx.backupItem
            if ($b.oldExists -and $null -ne $b.oldValue) {
                Set-RegValue $b.hive $b.path $b.name ([int]$b.oldValue) 'DWord'
            } else {
                Remove-RegValue $b.hive $b.path $b.name
            }
        }
    }
    @{
        id = 'net-nagle-off'
        kind = 'registry'
        apply = {
            param($ctx)
            $base = 'HKLM:\SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces'
            $keys = @(Get-ChildItem -Path $base -ErrorAction SilentlyContinue)
            $old = @{}; $changed = $false
            foreach ($k in $keys) {
                $ip = Get-ItemProperty -Path $k.PSPath -ErrorAction SilentlyContinue
                foreach ($n in @('TcpAckFrequency', 'TCPNoDelay')) {
                    $prop = $null
                    if ($ip) { $pp = $ip.PSObject.Properties[$n]; if ($pp) { $prop = [string][int]$pp.Value } }
                    $old[$k.PSPath + '|' + $n] = $prop
                    if ($prop -eq '1') { continue }
                    Set-ItemProperty -Path $k.PSPath -Name $n -Value 1 -Type DWord
                    $changed = $true
                }
            }
            $ctx.backupItem = @{ id = $ctx.item.id; kind = 'nagle'; oldValues = $old }
            return $changed
        }
        revert = {
            param($ctx)
            $b = $ctx.backupItem
            $old = $null
            if ($b -is [hashtable]) { $old = $b['oldValues'] }
            else { $pp = $b.PSObject.Properties['oldValues']; if ($pp) { $old = $pp.Value } }
            if (-not $old) { return }
            foreach ($key in @($old.Keys)) {
                $pspath, $name = $key -split '\|', 2
                $recorded = $old[$key]
                if ($null -ne $recorded) {
                    Set-ItemProperty -Path $pspath -Name $name -Value ([int]$recorded) -Type DWord
                } else {
                    Remove-ItemProperty -Path $pspath -Name $name -ErrorAction SilentlyContinue
                }
            }
        }
    }
)

# 以 catalog 补齐元信息；实现表必须与 catalog 一一对应，否则直接报错。
foreach ($item in $OptimizationItems) {
    $meta = (Get-Catalog).items | Where-Object { $_.id -eq $item.id }
    if (-not $meta) { throw "脚本中的优化项在 catalog 中不存在: $($item.id)" }
    $item.name       = $meta.name
    $item.description = $meta.description
    $item.sideEffect = $meta.sideEffect
    $item.admin      = [bool]$meta.admin
    $item.default    = [bool]$meta.default
    $item.reboot     = [bool]$meta.reboot
    $item.kind       = $meta.kind
}
foreach ($cid in ((Get-Catalog).items | ForEach-Object { $_.id })) {
    if (-not ($OptimizationItems | Where-Object { $_.id -eq $cid })) {
        throw "catalog 中存在但脚本未实现的优化项: $cid"
    }
}
# 兼容旧字段名
foreach ($item in $OptimizationItems) { $item.desc = $item.description }


function Restore-MultiSzValues {
    param([string]$Path, [hashtable]$Defaults, $BackupItem)
    $old = $null
    if ($BackupItem -is [hashtable]) { $old = $BackupItem['oldValues'] }
    else { $pp = $BackupItem.PSObject.Properties['oldValues']; if ($pp) { $old = $pp.Value } }
    foreach ($n in $Defaults.Keys) {
        $value = $Defaults[$n]
        $recorded = $null
        if ($null -ne $old) {
            if ($old -is [hashtable]) { $recorded = $old[$n] }
            else { $pp = $old.PSObject.Properties[$n]; if ($pp) { $recorded = $pp.Value } }
        }
        if ($null -ne $recorded -and "$recorded" -ne '') { $value = [string]$recorded }
        Set-ItemProperty -Path $Path -Name $n -Value $value -Type String
    }
}

function Restore-MultiSzValuesMultiPath {
    param($BackupItem)
    $old = $null
    if ($BackupItem -is [hashtable]) { $old = $BackupItem['oldValues'] }
    else { $pp = $BackupItem.PSObject.Properties['oldValues']; if ($pp) { $old = $pp.Value } }
    if (-not $old) { return }
    foreach ($key in @($old.Keys)) {
        $idx = $key.LastIndexOf('\')
        $path = $key.Substring(0, $idx)
        $name = $key.Substring($idx + 1)
        $recorded = $old[$key]
        if ($null -ne $recorded -and "$recorded" -ne '') {
            Set-ItemProperty -Path $path -Name $name -Value ([string]$recorded) -Type String
        }
    }
}

function Resolve-PresetIds {
    # full = 全部；其余按 catalog.presets 的 include/exclude。未知预设直接抛错。
    param([string]$Name)
    if (-not $Name -or $Name -eq 'full') {
        return @((Get-Catalog).items | ForEach-Object { $_.id })
    }
    $prop = (Get-Catalog).presets.PSObject.Properties[$Name]
    if (-not $prop) { throw "未知预设: $Name（可选 full / balanced / safe-only）" }
    $def = $prop.Value
    if ($def.PSObject.Properties['include']) { return @($def.include) }
    $exclude = @()
    if ($def.PSObject.Properties['exclude']) { $exclude = @($def.exclude) }
    return @(((Get-Catalog).items | ForEach-Object { $_.id }) | Where-Object { $_ -notin $exclude })
}

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
        full = Resolve-PresetIds 'full'
        balanced = Resolve-PresetIds 'balanced'
        'safe-only' = Resolve-PresetIds 'safe-only'
    }

    return @{
        tool = $ToolName; version = (Get-ToolVersion); mode = 'detect';
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
        $presetIds = Resolve-PresetIds $PresetName
        $toApply = @($OptimizationItems | Where-Object { $presetIds -contains $_.id })
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
            schema = 'v1'; tool = $ToolName; version = (Get-ToolVersion);
            createdAt = (Get-Date).ToString('o'); preset = $PresetName;
            items = $backupItems; results = $results
        }
        Write-AtomicJson $backupFile $backupDoc
    }

    $okCount = @($results | Where-Object { $_.ok }).Count
    $failCount = @($results | Where-Object { -not $_.ok }).Count
    $skipCount = @($results | Where-Object { $_.skipped }).Count

    return @{
        tool = $ToolName; version = (Get-ToolVersion); mode = 'apply';
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
    return @{ tool = $ToolName; version = (Get-ToolVersion); mode = 'list-restore'; restoreItems = $restoreItems; backupCount = $files.Count }
}

function Invoke-Restore {
    param([string[]]$RestoreItems)
    $files = @(Get-BackupFiles)
    if ($files.Count -eq 0) { return @{ tool = $ToolName; version = (Get-ToolVersion); mode = 'restore'; restored = @(); failed = @(); skipped = @(); summary = '没有可还原的备份' } }

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
        tool = $ToolName; version = (Get-ToolVersion); mode = 'restore';
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
        @{ tool = $ToolName; version = (Get-ToolVersion); mode = 'error'; error = $_.Exception.Message; stack = $_.ScriptStackTrace; line = $_.InvocationInfo.ScriptLineNumber } | ConvertTo-Json -Depth 6
    } else {
        Write-Error $_.Exception.Message
    }
    exit 1
}
