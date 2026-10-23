using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FpsTune.Wpf.Core;
using FpsTune.Wpf.Services;
using Microsoft.Win32;
using Xunit;

namespace FpsTune.Wpf.Tests;

[Collection("BackupService serial")]
public sealed class BackupRecoveryBundleTests : IDisposable
{
    private readonly string _root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../work/backup-recovery-tests", Guid.NewGuid().ToString("N")));
    private int _value = 1;
    private int _restores;
    public BackupRecoveryBundleTests()
    {
        UserDataPaths.RootOverride = Path.Combine(_root, "source");
        BackupService.TransferMachineOverride = () => new string('A', 64);
        RegistryHelper.SnapshotOverride = (_, _, _) => new(true, _value, RegistryValueKind.DWord);
        RegistryHelper.SetOverride = (_, _, _, _, _) => throw new Exception("Real registry writes forbidden");
        RegistryHelper.DeleteOverride = (_, _, _) => throw new Exception("Real registry deletes forbidden");
        NativeSystem.RunOverride = (_, _) => throw new Exception("Real native commands forbidden");
        BackupService.RestoreRecordOverride = r => { _restores++; _value = Convert.ToInt32(((JsonElement)r.OldValue!).GetInt32()); };
    }
    public void Dispose()
    {
        UserDataPaths.RootOverride = null; BackupService.TransferMachineOverride = null;
        RegistryHelper.SnapshotOverride = null; RegistryHelper.SetOverride = null; RegistryHelper.DeleteOverride = null;
        NativeSystem.RunOverride = null; BackupService.RestoreRecordOverride = null; AudioEqualizerService.EngineOverride = null;
    }
    private string Pending()
    {
        var file = BackupService.Capture([], null);
        BackupService.AppendCapture(file, "transparency-off", null); _value = 0;
        return file;
    }
    [Fact]
    public void Interrupted_review_keeps_original_and_unlocks_restore_only_after_confirmation()
    {
        var file = Pending(); var original = File.ReadAllBytes(file);
        Assert.NotEmpty(BackupService.RestoreAll(null, file).Failures);
        var review = BackupService.ReviewInterrupted();
        Assert.Equal(1, review.Count); Assert.Equal(original, File.ReadAllBytes(file)); Assert.Equal(0, _restores);
        Assert.Equal(1, BackupService.ConfirmInterruptedReview(review));
        Assert.Equal(original, File.ReadAllBytes(Directory.GetFiles(Path.GetDirectoryName(file)!, Path.GetFileName(file), SearchOption.AllDirectories).Single(f => f != file)));
        BackupService.EnsureNoPendingWrites(["transparency-off"]);
        Assert.Empty(BackupService.RestoreAll(null, file).Failures);
        Assert.Equal(1, _value); Assert.Equal(1, _restores);
    }
    [Fact]
    public void Stale_review_is_rejected_and_matching_inflight_journal_is_archived_before_unlock()
    {
        var file = Pending(); var review = BackupService.ReviewInterrupted(); _value = 2;
        Assert.Throws<InvalidOperationException>(() => BackupService.ConfirmInterruptedReview(review));
        Assert.Equal("pending", BackupService.ReadRecords(file)[0].MutationState); Assert.Equal(0, _restores);
        _value = 0;
        typeof(BackupService).GetMethod("WriteRestoreJournal", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, [file, 0, BackupService.ReadRecords(file)[0]]);
        var journal = Path.Combine(Path.GetDirectoryName(file)!, ".restore-inflight.json"); var bytes = File.ReadAllBytes(journal);
        var fresh = BackupService.ReviewInterrupted();
        Assert.Equal(1, fresh.Count); BackupService.ConfirmInterruptedReview(fresh);
        Assert.False(File.Exists(journal));
        Assert.Equal(bytes, File.ReadAllBytes(Directory.GetFiles(Path.GetDirectoryName(file)!, ".restore-inflight.json", SearchOption.AllDirectories).Single()));
        Assert.Empty(BackupService.RestoreAll(null, file).Failures);
    }
    private void Put(string relative, object value)
    {
        var path = Path.Combine(UserDataPaths.Root, relative); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(value));
    }
    [Fact]
    public void Same_machine_bundle_recovers_all_modules_into_fresh_data_and_is_idempotent()
    {
        var file = Pending(); BackupService.CompleteCapture(file, new("transparency-off", "", true, true, false, ""));
        var identity = "screen-A"; var ramp = Enumerable.Range(0, 768).Select(i => (ushort)((i % 256) * 257)).ToArray();
        Put(Path.Combine("screen-color", Path.GetFileName(DisplayBackupFiles.PathFor("", "color", identity))),
            new { Identity = identity, DisplayName = "A", Original = ramp, Expected = ramp, Settings = new ScreenColorSettings() });
        Put(Path.Combine("display-quality", Path.GetFileName(DisplayBackupFiles.PathFor("", "vibrance", identity))),
            new { Level = 30, Display = identity, Expected = 40, Min = 0, Max = 100 });
        var before = new[] { new { SettingId = DisplayQualityService.PowerModeId, Existed = true, Value = 0u } };
        Put("display-quality/backup-test.exe.json", new { GameExe = "test.exe", OwnProfile = false, Settings = before, SchemaVersion = 2, PostSettings = before });
        Put(Path.Combine("display-icc", Path.GetFileName(DisplayBackupFiles.PathFor("", "backup", identity))),
            new { AssociationSubKey = "assoc-A", DefaultProfile = "a.icc", ListBefore = new[] { "a.icc" }, CreatedAt = "2026-10-23", Identity = identity });
        var profile = IccProfileGenerator.Build(IccFilterPreset.Vivid);
        var custom = "display-icc/custom/FpsTune-Custom-" + Convert.ToHexString(SHA256.HashData(profile))[..8] + ".icc";
        Directory.CreateDirectory(Path.Combine(UserDataPaths.Root, "display-icc", "custom")); File.WriteAllBytes(Path.Combine(UserDataPaths.Root, custom), profile);
        var engine = Path.Combine(_root, "engine"); Directory.CreateDirectory(engine); File.WriteAllText(Path.Combine(engine, "config.txt"), "# existing\n");
        AudioEqualizerService.EngineOverride = new(engine, engine, "unused");
        AudioEqualizerService.Apply(new(Guid.NewGuid().ToString("B"), new double[10]));
        var zip = Path.Combine(_root, "all.zip"); var count = BackupService.ExportBackups(zip); Assert.Equal(7, count);
        UserDataPaths.RootOverride = Path.Combine(_root, "fresh");
        Assert.Equal((count, 0, 0), BackupService.ImportBackups(zip));
        Assert.Equal((0, count, 0), BackupService.ImportBackups(zip));
        Assert.Equal(profile, File.ReadAllBytes(Path.Combine(UserDataPaths.Root, custom)));
        Assert.True(AudioEqualizerService.HasBackup); Assert.NotNull(AudioEqualizerService.SavedSettings());
        var imported = BackupService.ListBackups().Single();
        Assert.NotNull(JsonNode.Parse(File.ReadAllText(imported))!["ImportedOriginId"]);
        Assert.Empty(BackupService.RestoreAll(null, imported).Failures); Assert.Equal(1, _value);
        Assert.Single(Directory.GetFiles(Path.Combine(UserDataPaths.Root, "backup", "history")));
    }
    [Theory]
    [InlineData("machine")]
    [InlineData("tamper")]
    [InlineData("traversal")]
    [InlineData("conflict")]
    public void Invalid_bundle_or_local_conflict_does_not_publish_receipts(string failure)
    {
        var source = Pending(); var zip = Path.Combine(_root, "bundle.zip"); BackupService.ExportBackups(zip);
        UserDataPaths.RootOverride = Path.Combine(_root, "fresh");
        if (failure == "machine") BackupService.TransferMachineOverride = () => new string('B', 64);
        if (failure is "tamper" or "traversal")
        {
            using var archive = ZipFile.Open(zip, ZipArchiveMode.Update);
            var e = archive.GetEntry(Path.GetFileName(source))!; byte[] content;
            using (var input = e.Open()) { using var m = new MemoryStream(); input.CopyTo(m); content = m.ToArray(); }
            e.Delete();
            var name = Path.GetFileName(source);
            if (failure == "tamper") content[^1] = (byte)' ';
            else
            {
                name = "../" + name;
                var metadata = archive.GetEntry("fpstune-backup-manifest.json")!; JsonNode manifest;
                using (var input = metadata.Open()) manifest = JsonNode.Parse(input)!;
                manifest["Files"]![0]!["Name"] = name; metadata.Delete();
                using var output = archive.CreateEntry("fpstune-backup-manifest.json").Open(); JsonSerializer.Serialize(output, manifest);
            }
            using var data = archive.CreateEntry(name).Open(); data.Write(content);
        }
        if (failure == "conflict") Put("backup/" + Path.GetFileName(source), new { keep = "local" });
        var error = Record.Exception(() => BackupService.ImportBackups(zip));
        if (failure == "conflict") Assert.IsType<IOException>(error);
        else Assert.IsType<InvalidDataException>(error);
        if (failure == "tamper") Assert.Contains("BundleHashMismatch", error!.Message);
        Assert.Equal(0, _restores);
        var backups = Path.Combine(UserDataPaths.Root, "backup");
        if (failure == "conflict") Assert.Contains("local", File.ReadAllText(Path.Combine(backups, Path.GetFileName(source))));
        else Assert.Empty(Directory.Exists(backups) ? Directory.GetFiles(backups, "*.json") : []);
    }
    [Fact]
    public void Failed_bundle_export_preserves_previous_package()
    {
        Pending(); var zip = Path.Combine(_root, "bundle.zip"); BackupService.ExportBackups(zip); var bytes = File.ReadAllBytes(zip);
        using (var lease = new FileStream(zip, FileMode.Open, FileAccess.Read, FileShare.Read))
            Assert.ThrowsAny<IOException>(() => BackupService.ExportBackups(zip));
        Assert.Equal(bytes, File.ReadAllBytes(zip));
    }
}
