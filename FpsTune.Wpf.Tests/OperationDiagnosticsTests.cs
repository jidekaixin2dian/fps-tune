using System.IO.Compression;
using System.Text.Json;
using FpsTune.Wpf.Services;
using Xunit;

namespace FpsTune.Wpf.Tests;

[Collection("BackupService serial")]
public sealed class OperationDiagnosticsTests : IDisposable
{
    private const string Exe = "FpsTune-Diagnostic-test.exe";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "fpstune-operation-" + Guid.NewGuid().ToString("N"));
    private readonly DisplayQualityTests.FakeNvdrsApi _api = new();
    public OperationDiagnosticsTests()
    {
        Directory.CreateDirectory(_root);
        DisplayQualityService.ApiOverride = () => _api;
        DisplayQualityService.BackupDirOverride = _root;
        DiagnosticReportExporter.BaseDirOverride = _root;
    }
    public void Dispose()
    {
        DisplayQualityService.ApiOverride = null;
        DisplayQualityService.BackupDirOverride = null;
        DiagnosticReportExporter.BaseDirOverride = null;
        Directory.Delete(_root, true);
    }
    [Fact]
    public void Save_denied_records_exact_phase_status_original_and_requested_but_not_fake_readback()
    {
        _api.AddProfile("existing", Exe);
        _api.SimulateSaveDenied = true;
        Assert.Throws<NvdrsException>(() => DisplayQualityService.ApplyDlssPreset(Exe, DlssPreset.PresetK));
        var record = Assert.Single(OperationDiagnostics.Read(_root).Operations);
        Assert.Equal("failed", record.Outcome);
        Assert.Equal("save-driver", record.Stage);
        Assert.Equal(-175, record.NativeStatus);
        var setting = Assert.Single(record.Settings);
        Assert.Equal("10E41DF3", setting.SettingId);
        Assert.Equal(new DriverValue(false, null), setting.Before);
        Assert.Equal(new DriverValue(true, 11), setting.Requested);
        Assert.Null(setting.After);
        Assert.True(DisplayQualityService.HasRestorableBackup(Exe));
    }
    [Fact]
    public void Independent_failed_readback_records_actual_absence()
    {
        _api.AddProfile("existing", Exe);
        _api.SimulateSaveIgnored = true;
        Assert.Throws<InvalidOperationException>(() => DisplayQualityService.ApplyDlssPreset(Exe, DlssPreset.PresetK));
        var record = Assert.Single(OperationDiagnostics.Read(_root).Operations);
        Assert.Equal("readback-driver", record.Stage);
        Assert.Equal(new DriverValue(false, null), Assert.Single(record.Settings).After);
    }
    [Fact]
    public void Restore_conflict_reports_external_value_and_both_receipt_values_without_writing()
    {
        var profile = _api.AddProfile("existing", Exe);
        profile.Settings[DisplayQualityService.DlssSrPresetId] = 5;
        DisplayQualityService.ApplyDlssPreset(Exe, DlssPreset.PresetK);
        profile.Settings[DisplayQualityService.DlssSrPresetId] = 10;
        Assert.Throws<InvalidOperationException>(() => DisplayQualityService.RemoveDlssOverride(Exe));
        var record = OperationDiagnostics.Read(_root).Operations.Last();
        Assert.Equal("check-restore-conflicts", record.Stage);
        var setting = Assert.Single(record.Settings);
        Assert.Equal(new DriverValue(true, 10), setting.Before);
        Assert.Equal(new DriverValue(true, 5), setting.Original);
        Assert.Equal(new DriverValue(true, 11), setting.LastApplied);
        Assert.Equal(1, _api.SaveCount);
    }
    [Fact]
    public void Diagnostic_storage_failure_never_changes_success_or_native_failure()
    {
        File.WriteAllText(Path.Combine(_root, "logs"), "blocked directory");
        var profile = _api.AddProfile("existing", Exe);
        DisplayQualityService.ApplyDlssPreset(Exe, DlssPreset.PresetK);
        Assert.Equal(11u, profile.Settings[DisplayQualityService.DlssSrPresetId]);
        _api.SimulateSaveDenied = true;
        Assert.Equal(-175, Assert.Throws<NvdrsException>(() => DisplayQualityService.ApplyDlssPreset(Exe, DlssPreset.PresetJ)).Status);
    }
    [Fact]
    public void Receipts_are_bounded_and_corrupt_original_is_preserved()
    {
        Directory.CreateDirectory(Path.Combine(_root, "logs"));
        var path = Path.Combine(_root, "logs", "operations.json");
        File.WriteAllText(path, "corrupt-original");
        for (var i = 0; i < OperationDiagnostics.Retention + 2; i++)
        {
            using var trace = OperationDiagnostics.Begin("retry", i.ToString(), _root);
            trace.Complete();
        }
        var rows = OperationDiagnostics.Read(_root).Operations;
        Assert.Equal(OperationDiagnostics.Retention, rows.Count);
        Assert.Equal("2", rows[0].Target);
        Assert.Equal("corrupt-original", File.ReadAllText(Assert.Single(Directory.GetFiles(Path.Combine(_root, "logs"), "*.invalid-*"))));
    }
    [Fact]
    public void Export_contains_structured_receipts_and_scrubs_paths_and_credentials()
    {
        using (var trace = OperationDiagnostics.Begin("driver-apply", @"C:\Users\Aether\Private Folder\game.exe", _root))
        {
            trace.Stage("save-driver");
            trace.Fail(new NvdrsException(-175, @"C:\Users\Aether\Private\file; Bearer sensitive-token password=secret-value"));
        }
        var zipPath = DiagnosticReportExporter.ExportTo(Path.Combine(_root, "diagnostic.zip"));
        using var archive = ZipFile.OpenRead(zipPath);
        foreach (var name in new[] { "operation-diagnostics.json", "report.json" })
        {
            using var reader = new StreamReader(archive.GetEntry(name)!.Open());
            var content = reader.ReadToEnd();
            Assert.DoesNotContain("sensitive-token", content);
            Assert.DoesNotContain("secret-value", content);
            Assert.DoesNotContain("Aether", content, StringComparison.OrdinalIgnoreCase);
            using var json = JsonDocument.Parse(content);
            var report = name == "report.json" ? json.RootElement.GetProperty("operations") : json.RootElement;
            Assert.Equal("save-driver", Assert.Single(report.GetProperty("Operations").EnumerateArray()).GetProperty("Stage").GetString());
        }
    }
}
