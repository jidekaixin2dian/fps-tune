using System.IO.Compression;
using System.Text;
using System.Text.Json;
using FpsTune.Wpf.Services;
using Xunit;

namespace FpsTune.Wpf.Tests;

/// <summary>诊断包的隐私最小化、状态记录与原子覆盖语义。</summary>
public sealed class DiagnosticReportTests : IDisposable
{
    private readonly string _baseDir = Path.Combine(
        Path.GetTempPath(), "fpstune-diagnostic-tests-" + Guid.NewGuid().ToString("N"));

    public DiagnosticReportTests()
    {
        Directory.CreateDirectory(_baseDir);
        DiagnosticReportExporter.BaseDirOverride = _baseDir;
    }

    public void Dispose()
    {
        DiagnosticReportExporter.BaseDirOverride = null;
        DiagnosticReportExporter.ExperimentWizardLoaderOverride = null;
        DiagnosticReportExporter.FileMetadataProbeOverride = null;
        if (Directory.Exists(_baseDir))
            Directory.Delete(_baseDir, recursive: true);
    }

    [Fact]
    public void Export_omits_raw_sensitive_files_and_records_machine_statuses()
    {
        var experimentDir = Path.Combine(_baseDir, "experiment");
        Directory.CreateDirectory(experimentDir);

        // 这些文件只用于证明导出不会搬运原始内容；实现只检查存在性。
        File.WriteAllText(
            Path.Combine(_baseDir, "last-detect.json"),
            "{\"gamePath\":\"C:\\\\Users\\\\Aether\\\\Documents\\\\FPS Tune\\\\game.exe\",\"unc\":\"\\\\\\\\server\\\\share\\\\game.exe\"}",
            Encoding.UTF8);
        File.WriteAllText(
            Path.Combine(experimentDir, "state.json"),
            "{\"log\":\"C:\\\\Program Files\\\\FPS Tune\\\\logs\\\\error.log\"}",
            Encoding.UTF8);

        var target = Path.Combine(_baseDir, "out", "diagnostic.zip");
        var result = DiagnosticReportExporter.ExportTo(target);

        Assert.Equal(Path.GetFullPath(target), result);
        using var archive = ZipFile.OpenRead(result);
        var rawEntryNames = new[]
        {
            "detect.json",
            "experiment/state.json",
            "experiment/history.jsonl",
            "experiment/wizard.json"
        };
        Assert.DoesNotContain(archive.Entries, e => rawEntryNames.Contains(e.FullName, StringComparer.OrdinalIgnoreCase));

        var statusEntry = Assert.Single(archive.Entries, e => e.FullName == "diagnostic-input-status.json");
        using var statusReader = new StreamReader(statusEntry.Open(), Encoding.UTF8);
        using var status = JsonDocument.Parse(statusReader.ReadToEnd());
        var files = status.RootElement.GetProperty("files").EnumerateArray().ToList();
        Assert.Equal(4, files.Count);
        Assert.Equal(
            new[] { "last-detect.json", "experiment/state.json", "experiment/history.jsonl", "experiment/wizard.json" },
            files.Select(x => x.GetProperty("source").GetString()).ToArray());
        Assert.Equal(
            new[] { "source-present-but-omitted-by-privacy", "source-present-but-omitted-by-privacy", "source-missing", "source-missing" },
            files.Select(x => x.GetProperty("status").GetString()).ToArray());

        foreach (var entry in archive.Entries)
        {
            using var reader = new StreamReader(entry.Open(), Encoding.UTF8);
            var content = reader.ReadToEnd();
            Assert.DoesNotContain("C:\\Users\\Aether", content, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("C:\\Program Files\\FPS Tune", content, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("\\\\server\\share", content, StringComparison.OrdinalIgnoreCase);
        }

        var reportEntry = Assert.Single(archive.Entries, e => e.FullName == "report.json");
        using var reportReader = new StreamReader(reportEntry.Open(), Encoding.UTF8);
        var report = reportReader.ReadToEnd();
        Assert.DoesNotContain("Aether", report, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("C:\\Users", report, StringComparison.OrdinalIgnoreCase);

        var markdownEntry = Assert.Single(archive.Entries, e => e.FullName == "概览.md");
        using var markdownReader = new StreamReader(markdownEntry.Open(), Encoding.UTF8);
        var markdown = markdownReader.ReadToEnd();
        Assert.DoesNotContain("Aether", markdown, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("C:\\Users", markdown, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Report_projects_wizard_without_raw_output_or_sensitive_script_text()
    {
        const string token = "fpstune-test-token-7f6b";
        const string password = "password=hunter2";
        DiagnosticReportExporter.ExperimentWizardLoaderOverride = () => new ExperimentWizardState
        {
            BaselineDone = true,
            BaselineStable = true,
            BaselineAvgFps = 120,
            BaselineP1Low = 80,
            BaselineCv = 0.02,
            BaselineSessionId = "baseline-session",
            BaselineAt = DateTime.UtcNow,
            Groups =
            [
                new WizardGroupResult(
                    "group-1", true, false, "结果摘要", 121, 81, "group-session", false, DateTime.UtcNow)
            ],
            LastError = "最近错误摘要",
            LastErrorStep = "group-2",
            LastRawOutput = $"Bearer {token}\n{password}"
        };

        var target = Path.Combine(_baseDir, "out", "raw-output.zip");
        try
        {
            DiagnosticReportExporter.ExportTo(target);
        }
        finally
        {
            DiagnosticReportExporter.ExperimentWizardLoaderOverride = null;
        }

        using var archive = ZipFile.OpenRead(target);
        var allText = string.Join(
            "\n",
            archive.Entries.Select(entry =>
            {
                using var reader = new StreamReader(entry.Open(), Encoding.UTF8);
                return reader.ReadToEnd();
            }));
        Assert.DoesNotContain(token, allText, StringComparison.Ordinal);
        Assert.DoesNotContain(password, allText, StringComparison.Ordinal);
        Assert.DoesNotContain("LastRawOutput", allText, StringComparison.Ordinal);

        var reportEntry = Assert.Single(archive.Entries, e => e.FullName == "report.json");
        using var reportReader = new StreamReader(reportEntry.Open(), Encoding.UTF8);
        using var report = JsonDocument.Parse(reportReader.ReadToEnd());
        var wizard = report.RootElement
            .GetProperty("experiment")
            .GetProperty("wizard");
        Assert.True(wizard.GetProperty("rawOutputOmitted").GetBoolean());
        Assert.True(wizard.GetProperty("BaselineDone").GetBoolean());
        Assert.Equal("baseline-session", wizard.GetProperty("BaselineSessionId").GetString());
        Assert.Equal("group-session", wizard.GetProperty("Groups")[0].GetProperty("SessionId").GetString());
        Assert.False(wizard.TryGetProperty("LastRawOutput", out _));
    }

    [Fact]
    public void Privacy_metadata_failures_are_not_reported_as_missing()
    {
        Assert.Equal("source-missing", DiagnosticReportExporter.ClassifyPrivacyMetadataException(new FileNotFoundException()));
        Assert.Equal("source-missing", DiagnosticReportExporter.ClassifyPrivacyMetadataException(new DirectoryNotFoundException()));
        Assert.Equal("metadata-check-failed", DiagnosticReportExporter.ClassifyPrivacyMetadataException(new UnauthorizedAccessException()));
        Assert.Equal("metadata-check-failed", DiagnosticReportExporter.ClassifyPrivacyMetadataException(new IOException()));

        var experimentDir = Path.Combine(_baseDir, "experiment");
        Directory.CreateDirectory(experimentDir);
        File.WriteAllText(Path.Combine(_baseDir, "last-detect.json"), "{}", Encoding.UTF8);
        DiagnosticReportExporter.FileMetadataProbeOverride = path =>
        {
            if (path.EndsWith("history.jsonl", StringComparison.OrdinalIgnoreCase))
                throw new UnauthorizedAccessException("metadata denied");
            return File.GetAttributes(path);
        };

        var target = Path.Combine(_baseDir, "out", "metadata-status.zip");
        try
        {
            DiagnosticReportExporter.ExportTo(target);
        }
        finally
        {
            DiagnosticReportExporter.FileMetadataProbeOverride = null;
        }

        using var archive = ZipFile.OpenRead(target);
        var statusEntry = Assert.Single(archive.Entries, e => e.FullName == "diagnostic-input-status.json");
        using var statusReader = new StreamReader(statusEntry.Open(), Encoding.UTF8);
        using var status = JsonDocument.Parse(statusReader.ReadToEnd());
        var history = status.RootElement.GetProperty("files").EnumerateArray()
            .Single(file => file.GetProperty("source").GetString() == "experiment/history.jsonl");
        Assert.Equal("metadata-check-failed", history.GetProperty("status").GetString());
        Assert.Equal("metadata denied", history.GetProperty("message").GetString());
    }

    [Fact]
    public void Existing_target_survives_failed_atomic_replacement()
    {
        var targetDir = Path.Combine(_baseDir, "overwrite");
        Directory.CreateDirectory(targetDir);
        var target = Path.Combine(targetDir, "diagnostic.zip");
        File.WriteAllText(target, "old archive", Encoding.UTF8);
        var missingStaging = Path.Combine(targetDir, "missing-staging.zip");

        Assert.ThrowsAny<IOException>(() =>
            DiagnosticReportExporter.MoveArchiveAtomically(missingStaging, target));
        Assert.Equal("old archive", File.ReadAllText(target, Encoding.UTF8));
    }
}
