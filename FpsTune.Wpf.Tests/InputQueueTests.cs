using FpsTune.Wpf.Core;
using FpsTune.Wpf.Services;
using Microsoft.Win32;
using Xunit;

namespace FpsTune.Wpf.Tests;

/// <summary>
/// 键鼠缓冲深度策略、三态判定、引擎写入值与键鼠异常反馈字段。
/// 覆盖时长公式锁死为「深度 / 报告率 × 1000」，推荐值由 96 毫秒目标反推。
/// </summary>
[Collection("BackupService serial")]
public sealed class InputQueueTests : IDisposable
{
    private readonly Dictionary<string, object> _store = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<(string Name, object Value)> _writes = [];
    private readonly string _root = Path.Combine(Path.GetTempPath(), "fpstune-input-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        RegistryHelper.SnapshotOverride = null;
        RegistryHelper.SetOverride = null;
        DiagnosticReportExporter.DeviceProbeOverride = null;
        DiagnosticReportExporter.BaseDirOverride = null;
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    /// <summary>假注册表：写入立刻可见，读回复核因此能走真实判定分支。</summary>
    private void UseFakeRegistry()
    {
        Directory.CreateDirectory(_root);
        UserDataPaths.RootOverride = _root;
        RegistryHelper.SnapshotOverride = (_, _, name) => _store.TryGetValue(name, out var value)
            ? new RegistryHelper.Snapshot(true, value, RegistryValueKind.DWord)
            : new RegistryHelper.Snapshot(false, null, RegistryValueKind.None);
        RegistryHelper.SetOverride = (_, _, name, value, _) =>
        {
            _store[name] = value;
            _writes.Add((name, value));
        };
    }

    [Theory]
    [InlineData(50, 1000, 50)]
    [InlineData(100, 1000, 100)]
    [InlineData(200, 2000, 100)]
    [InlineData(100, 8000, 13)]
    [InlineData(16, 125, 128)]
    [InlineData(1024, 8000, 128)]
    [InlineData(0, 1000, 0)]
    [InlineData(100, 0, 0)]
    public void Coverage_is_depth_over_polling_rate(int depth, int pollingRate, int expected)
        => Assert.Equal(expected, InputQueuePolicy.CoverageMs(depth, pollingRate));

    [Theory]
    [InlineData(125, 16)]
    [InlineData(250, 24)]
    [InlineData(500, 48)]
    [InlineData(1000, 96)]
    [InlineData(2000, 192)]
    [InlineData(4000, 384)]
    [InlineData(8000, 768)]
    public void Recommended_depth_covers_the_target_window(int pollingRate, int expected)
    {
        Assert.Equal(expected, InputQueuePolicy.RecommendedDepth(pollingRate));
        Assert.True(InputQueuePolicy.CoverageMs(expected, pollingRate) >= InputQueuePolicy.TargetCoverageMs);
        Assert.True(InputQueuePolicy.IsValidDepth(expected));
    }

    [Theory]
    [InlineData(50, 1000, true)]
    [InlineData(96, 1000, false)]
    [InlineData(104, 1000, false)]
    [InlineData(100, 8000, true)]
    [InlineData(768, 8000, false)]
    public void Below_recommended_tracks_the_polling_rate(int depth, int pollingRate, bool below)
        => Assert.Equal(below, InputQueuePolicy.IsBelowRecommended(depth, pollingRate));

    [Theory]
    [InlineData(50, true)]
    [InlineData(88, true)]
    [InlineData(96, false)]
    public void Keyboard_uses_a_fixed_recommended_depth(int depth, bool below)
        => Assert.Equal(below, InputQueuePolicy.IsKeyboardBelowRecommended(depth));

    [Theory]
    [InlineData(100, 104)]
    [InlineData(50, 56)]
    [InlineData(16, 16)]
    [InlineData(7, 16)]
    [InlineData(1024, 1024)]
    [InlineData(2000, 1024)]
    public void Normalize_clamps_then_rounds_up(int depth, int expected)
        => Assert.Equal(expected, InputQueuePolicy.Normalize(depth));

    [Theory]
    [InlineData(16, true)]
    [InlineData(104, true)]
    [InlineData(1024, true)]
    [InlineData(15, false)]
    [InlineData(100, false)]
    [InlineData(1032, false)]
    public void Valid_depth_is_in_range_and_on_the_step_grid(int depth, bool valid)
        => Assert.Equal(valid, InputQueuePolicy.IsValidDepth(depth));

    [Theory]
    [InlineData(null, 1000, false, InputQueueState.NotSet)]
    [InlineData(null, 1000, true, InputQueueState.NotSet)]
    [InlineData(50, 1000, false, InputQueueState.BelowRecommended)]
    [InlineData(50, 1000, true, InputQueueState.BelowRecommended)]
    [InlineData(96, 1000, false, InputQueueState.Ok)]
    [InlineData(96, 1000, true, InputQueueState.Ok)]
    [InlineData(100, 8000, false, InputQueueState.BelowRecommended)]
    public void Three_states_keep_unset_apart_from_below_recommended(
        int? stored, int pollingRate, bool keyboard, InputQueueState expected)
        => Assert.Equal(expected, InputQueuePolicy.Evaluate(stored, pollingRate, keyboard));

    [Fact]
    public void Engine_writes_the_depths_that_come_from_settings()
    {
        UseFakeRegistry();
        var settings = SettingsService.Current;
        var keyboard = settings.KeyboardQueueDepth;
        var mouse = settings.MouseQueueDepth;
        try
        {
            settings.KeyboardQueueDepth = 200;
            settings.MouseQueueDepth = 152;

            var results = NativeOptimizationEngine.ApplyAll(["keyboard-latency", "mouse-latency"], null);

            Assert.All(results, r => Assert.True(r.Ok, r.Message));
            Assert.Contains(("KeyboardDataQueueSize", (object)200), _writes);
            Assert.Contains(("MouseDataQueueSize", (object)152), _writes);
            Assert.All(results, r => Assert.True(r.Changed));

            // 值已经一致时不再写，也不再把「本就达标」报成改动。
            var again = NativeOptimizationEngine.ApplyAll(["keyboard-latency", "mouse-latency"], null);
            Assert.All(again, r => Assert.False(r.Changed));
            Assert.Equal(2, _writes.Count);
        }
        finally
        {
            settings.KeyboardQueueDepth = keyboard;
            settings.MouseQueueDepth = mouse;
        }
    }

    [Fact]
    public void Engine_clamps_out_of_range_depths_before_writing()
    {
        UseFakeRegistry();
        var settings = SettingsService.Current;
        var keyboard = settings.KeyboardQueueDepth;
        try
        {
            settings.KeyboardQueueDepth = 5000;
            NativeOptimizationEngine.ApplyAll(["keyboard-latency"], null);
            Assert.Contains(("KeyboardDataQueueSize", (object)InputQueuePolicy.MaxDepth), _writes);
        }
        finally
        {
            settings.KeyboardQueueDepth = keyboard;
        }
    }

    [Fact]
    public void Service_start_type_maps_the_documented_values()
    {
        // dwStartType：0 引导 / 1 系统 / 2 自动 / 3 手动 / 4 已禁用，其余按未知。
        (uint Raw, ServiceStartType Expected)[] table =
        [
            (0u, ServiceStartType.Boot),
            (1u, ServiceStartType.System),
            (2u, ServiceStartType.Auto),
            (3u, ServiceStartType.Demand),
            (4u, ServiceStartType.Disabled),
            (9u, ServiceStartType.Unknown)
        ];

        foreach (var (raw, expected) in table)
            Assert.Equal(expected, NativeServiceStatus.MapStartType(raw));
    }

    [Fact]
    public void Feedback_fields_carry_settings_registry_and_devices()
    {
        UseFakeRegistry();
        DiagnosticReportExporter.BaseDirOverride = _root;
        DiagnosticReportExporter.DeviceProbeOverride = () => ("Fixture Keyboard", "Fixture Mouse");
        var settings = SettingsService.Current;
        var (keyboard, mouse, rate) = (settings.KeyboardQueueDepth, settings.MouseQueueDepth, settings.MousePollingRate);
        try
        {
            settings.KeyboardQueueDepth = 200;
            settings.MouseQueueDepth = 152;
            settings.MousePollingRate = 2000;
            _store["KeyboardDataQueueSize"] = 104;

            var fields = DiagnosticReportExporter.CollectInputFeedback();

            Assert.Equal(200, fields.KeyboardDepth);
            Assert.Equal(152, fields.MouseDepth);
            Assert.Equal(2000, fields.PollingRate);
            Assert.Equal(InputQueuePolicy.KeyboardRecommendedDepth, fields.KeyboardRecommended);
            Assert.Equal(InputQueuePolicy.RecommendedDepth(2000), fields.MouseRecommended);
            Assert.Equal(InputQueuePolicy.CoverageMs(152, 2000), fields.MouseCoverageMs);
            Assert.Equal(104, fields.KeyboardRegistryDepth);
            Assert.Null(fields.MouseRegistryDepth);
            Assert.False(fields.MouseAccelerationOff);
            Assert.Equal("Fixture Keyboard", fields.KeyboardDevices);
            Assert.Equal("Fixture Mouse", fields.MouseDevices);
        }
        finally
        {
            settings.KeyboardQueueDepth = keyboard;
            settings.MouseQueueDepth = mouse;
            settings.MousePollingRate = rate;
        }
    }

    [Fact]
    public void Export_writes_the_feedback_note_and_keeps_paths_out()
    {
        UseFakeRegistry();
        DiagnosticReportExporter.BaseDirOverride = _root;
        DiagnosticReportExporter.DeviceProbeOverride = () => ("Fixture Keyboard", "Fixture Mouse");

        var path = DiagnosticReportExporter.ExportInputFeedback();

        Assert.StartsWith(Path.Combine(_root, "feedback"), path);
        Assert.True(File.Exists(path));
        var text = File.ReadAllText(path);
        Assert.False(string.IsNullOrWhiteSpace(text));
        Assert.DoesNotContain(Environment.UserName, text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(":\\", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Writer_saves_the_chosen_depths_before_asking_the_engine()
    {
        UseFakeRegistry();
        var settings = SettingsService.Current;
        var (keyboard, mouse, rate) = (settings.KeyboardQueueDepth, settings.MouseQueueDepth, settings.MousePollingRate);
        var asked = new List<(int Keyboard, int Mouse)>();
        InputQueueWriter.WriteOverride = (k, m) =>
        {
            asked.Add((k, m));
            return Task.FromResult<IReadOnlyList<OptimizationApplyResult>>([]);
        };
        try
        {
            settings.MousePollingRate = 500;
            await InputQueueWriter.WriteAsync(200, 120, 500);

            Assert.Equal((200, 120), Assert.Single(asked));
            Assert.Equal(200, settings.KeyboardQueueDepth);
            Assert.Equal(120, settings.MouseQueueDepth);
            Assert.Equal(500, settings.MousePollingRate);
        }
        finally
        {
            InputQueueWriter.WriteOverride = null;
            settings.KeyboardQueueDepth = keyboard;
            settings.MouseQueueDepth = mouse;
            settings.MousePollingRate = rate;
        }
    }
}
