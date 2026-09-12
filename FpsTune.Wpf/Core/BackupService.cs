using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Text;
using FpsTune.Wpf.Services;
using Microsoft.Win32;

namespace FpsTune.Wpf.Core;

public sealed class BackupRecord
{
    public string Id { get; set; } = "";
    public string Kind { get; set; } = "registry";
    public string Hive { get; set; } = "";
    public string Path { get; set; } = "";
    public string Name { get; set; } = "";
    public object? OldValue { get; set; }
    public string ValueKind { get; set; } = "DWord";

    // 第二个关联注册表值（如 game-mode 的 AllowAutoGameMode、dvr-off 的 AllowGameDVR 策略），
    // 用于无损还原；旧版本备份没有该字段（null）时还原保持不动。
    // power-tuning 三项隐藏电源设置的原始 AC 值；null 表示当时读取失败，还原时回退常见默认值。
    public int? OldUsbValue { get; set; }
    public int? OldBoostValue { get; set; }
    public int? OldIdleValue { get; set; }

    public string? SecondaryHive { get; set; }
    public string? SecondaryPath { get; set; }
    public string? SecondaryName { get; set; }
    public bool? SecondaryExisted { get; set; }
    public object? SecondaryValue { get; set; }
    public bool Existed { get; set; }
    public string? ServiceName { get; set; }
    public int? OldStartValue { get; set; }
    public string? OldStartMode { get; set; }
    public string? OldActiveGuid { get; set; }
    public string? OldState { get; set; }

    // 定向还原后保留原始快照供审计，同时防止同一记录被后续 RestoreAll 重复覆盖。
    public bool Restored { get; set; }
}

public static partial class BackupService
{
    private const string CSharpBackupPrefix = "csharp-backup-";
    private const string LegacyBackupPrefix = "backup-";
    private const string AutostartBackupId = "autostart";
    private const string AutostartRunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string AutostartRunValueName = "FpsTune";
    private const string RestoreJournalFileName = ".restore-inflight.json";
    internal const string RestoreMutexName = @"Local\FpsTune.RestoreAll";
    private static readonly TimeSpan RestoreMutexWaitTimeout = TimeSpan.FromSeconds(30);

    // 测试可注入；生产代码保持默认目录
    internal static string? BackupDirOverride { get; set; }
    internal static Action<BackupRecord>? RestoreRecordOverride { get; set; }
    internal static Action<string, string>? RestoreProgressWriteOverride { get; set; }
    internal static TimeSpan? RestoreMutexWaitTimeoutOverride { get; set; }

    private sealed class RestoreInFlight
    {
        public string BackupFile { get; set; } = "";
        public int RecordIndex { get; set; }
        public string RecordId { get; set; } = "";
        public string RecordFingerprint { get; set; } = "";
    }

    private static string BackupDir =>
        BackupDirOverride ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "FpsTune", "backup");

}
