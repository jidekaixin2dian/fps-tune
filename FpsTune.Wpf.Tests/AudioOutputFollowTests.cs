using System.Text;
using FpsTune.Wpf.Services;
using Xunit;

namespace FpsTune.Wpf.Tests;

/// <summary>
/// 声音页「输出到」的服务层语义：默认端点判定、设备变化后跟随或不跟随、未安装引擎时如实说明、
/// 以及写入只碰自己的 include。端点与引擎全部走注入，配置文件放在临时目录，不接触真实音频配置。
/// </summary>
[Collection("BackupService serial")]
public sealed class AudioOutputFollowTests : IDisposable
{
    private static readonly AudioOutput Headset = new(Guid.NewGuid().ToString("D"), "耳机（测试）");
    private static readonly AudioOutput Speakers = new(Guid.NewGuid().ToString("D"), "音箱（测试）");

    private readonly string _root = Path.Combine(Path.GetTempPath(), "fpstune-eq-output-" + Guid.NewGuid().ToString("N"));
    private readonly string _main;
    private readonly string _own;
    private DateTimeOffset _now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public AudioOutputFollowTests()
    {
        Directory.CreateDirectory(_root);
        _main = Path.Combine(_root, "config.txt");
        _own = Path.Combine(_root, "FpsTune-Equalizer.txt");
        File.WriteAllText(_main, "# 用户原有配置\n", new UTF8Encoding(false));
        AudioEqualizerService.EngineOverride = new(_root, _root, "unused");
        AudioEqualizerService.BackupDirOverride = Path.Combine(_root, "receipts");
        AudioEqualizerService.OutputsOverride = () => [Headset, Speakers];
        AudioEqualizerService.ClockOverride = () => _now;
        AudioEqualizerService.ResetDefaultOutputTracking();
    }

    public void Dispose()
    {
        AudioEqualizerService.EngineOverride = null;
        AudioEqualizerService.EngineProbeOverride = null;
        AudioEqualizerService.OutputsOverride = null;
        AudioEqualizerService.DefaultOutputOverride = null;
        AudioEqualizerService.ClockOverride = null;
        AudioEqualizerService.BackupDirOverride = null;
        AudioEqualizerService.ResetDefaultOutputTracking();
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    [Fact]
    public void Default_output_resolves_the_system_endpoint_onto_the_known_device()
    {
        // 系统返回的是 {0.0.0.00000000}.{GUID}，注册表端点键是裸 GUID；名字要取注册表里的友好名。
        AudioEqualizerService.DefaultOutputOverride = () => new AudioOutput("{0.0.0.00000000}." + Speakers.Id, "raw id");
        var device = AudioEqualizerService.DefaultOutput();
        Assert.NotNull(device);
        Assert.Equal(Speakers.Name, device!.Name);
        Assert.True(AudioEqualizerService.SameDevice(Speakers.Id, device.Id));

        AudioEqualizerService.DefaultOutputOverride = () => null;
        Assert.Null(AudioEqualizerService.DefaultOutput());
    }

    [Fact]
    public void Monitoring_follows_the_new_default_only_when_following_is_on()
    {
        AudioEqualizerService.Apply(new EqualizerSettings(Headset.Id, new double[10]));

        AudioEqualizerService.DefaultOutputOverride = () => Headset;
        Assert.Null(AudioEqualizerService.MonitorDefaultOutput(follow: true)); // 第一次读到只作初始化

        AudioEqualizerService.DefaultOutputOverride = () => Speakers;
        Assert.Null(AudioEqualizerService.MonitorDefaultOutput(follow: true)); // 2 秒节流窗口内不重复读

        _now = _now.AddSeconds(3);
        var result = AudioEqualizerService.MonitorDefaultOutput(follow: true);
        Assert.NotNull(result);
        Assert.True(result!.Written);
        Assert.Equal("Str.EqFollowed", result.Key);
        Assert.Contains(Guid.Parse(Speakers.Id).ToString("B"), File.ReadAllText(_own));
        Assert.True(AudioEqualizerService.SameDevice(Speakers.Id, AudioEqualizerService.AppliedTarget()!.Id));

        var again = AudioEqualizerService.FollowDefaultOutput();
        Assert.False(again.Written);
        Assert.Equal("Str.EqFollowAlready", again.Key);
    }

    [Fact]
    public void Monitoring_without_following_reports_the_switch_and_writes_nothing()
    {
        AudioEqualizerService.Apply(new EqualizerSettings(Headset.Id, new double[10]));
        var written = File.ReadAllBytes(_own);

        AudioEqualizerService.DefaultOutputOverride = () => Headset;
        Assert.Null(AudioEqualizerService.MonitorDefaultOutput(follow: false));
        AudioEqualizerService.DefaultOutputOverride = () => Speakers;
        _now = _now.AddSeconds(3);
        var result = AudioEqualizerService.MonitorDefaultOutput(follow: false);

        Assert.NotNull(result);
        Assert.False(result!.Written);
        Assert.Equal("Str.EqOutputSwitched", result.Key);
        Assert.Equal(written, File.ReadAllBytes(_own));
        Assert.True(AudioEqualizerService.SameDevice(Headset.Id, AudioEqualizerService.AppliedTarget()!.Id));
    }

    [Fact]
    public void Reopening_the_panel_catches_up_and_ignores_a_receipt_without_a_live_include()
    {
        AudioEqualizerService.Apply(new EqualizerSettings(Headset.Id, new double[10]));
        AudioEqualizerService.DefaultOutputOverride = () => Speakers;

        Assert.True(AudioEqualizerService.MonitorDefaultOutput(follow: true, reconcile: true)!.Written);
        Assert.True(AudioEqualizerService.SameDevice(Speakers.Id, AudioEqualizerService.AppliedTarget()!.Id));

        File.WriteAllText(_main, "# user removed the include\n");
        AudioEqualizerService.DefaultOutputOverride = () => Headset;
        Assert.Null(AudioEqualizerService.AppliedTarget());
        Assert.False(AudioEqualizerService.FollowDefaultOutput().Written);
        Assert.Equal("# user removed the include\n", File.ReadAllText(_main));
    }

    [Fact]
    public void Polling_reads_the_default_endpoint_at_most_once_per_throttle_window()
    {
        AudioEqualizerService.DefaultOutputOverride = () => Headset;
        Assert.Null(AudioEqualizerService.PollDefaultOutput());
        AudioEqualizerService.DefaultOutputOverride = () => Speakers;
        Assert.Null(AudioEqualizerService.PollDefaultOutput());
        _now = _now.AddMilliseconds(AudioEqualizerService.PollIntervalMs + 1);
        var change = AudioEqualizerService.PollDefaultOutput();
        Assert.NotNull(change);
        Assert.True(AudioEqualizerService.SameDevice(Headset.Id, change!.Previous!.Id));
        Assert.True(AudioEqualizerService.SameDevice(Speakers.Id, change.Current!.Id));
    }

    [Fact]
    public void Missing_engine_reports_plainly_and_writes_nothing()
    {
        AudioEqualizerService.EngineOverride = null;
        AudioEqualizerService.EngineProbeOverride = () => null;
        AudioEqualizerService.DefaultOutputOverride = () => Speakers;

        var follow = AudioEqualizerService.FollowDefaultOutput();
        Assert.False(follow.Written);
        Assert.Equal("Str.EqFollowNoEngine", follow.Key);
        var apply = Assert.Throws<InvalidOperationException>(() => AudioEqualizerService.Apply(new(Headset.Id, new double[10])));
        Assert.Contains("Str.EqInstallFirst", apply.Message);
        Assert.False(File.Exists(_own));
        Assert.Equal("# 用户原有配置\n", File.ReadAllText(_main));
    }

    [Fact]
    public void Pausing_takeover_stops_writes_and_keeps_what_was_written()
    {
        var settings = new EqualizerSettings(Headset.Id, new double[10]);
        AudioEqualizerService.Apply(settings);
        var written = File.ReadAllBytes(_own);

        AudioEqualizerService.TakeoverPaused = true;
        Assert.True(AudioEqualizerService.TakeoverPaused);
        var apply = Assert.Throws<InvalidOperationException>(() => AudioEqualizerService.Apply(settings with { DeviceId = Speakers.Id }));
        Assert.Contains("Str.EqTakeoverPaused", apply.Message);
        var follow = AudioEqualizerService.FollowDefaultOutput();
        Assert.False(follow.Written);
        Assert.Equal("Str.EqTakeoverPaused", follow.Key);
        Assert.Equal(written, File.ReadAllBytes(_own)); // 暂停不删已有内容，也不改用户 config.txt

        AudioEqualizerService.TakeoverPaused = false;
        AudioEqualizerService.Apply(settings with { DeviceId = Speakers.Id });
        Assert.Contains(Guid.Parse(Speakers.Id).ToString("B"), File.ReadAllText(_own));
    }

    [Fact]
    public void Applying_writes_only_its_own_include_and_keeps_the_user_config_body()
    {
        AudioEqualizerService.Apply(new EqualizerSettings(Headset.Id, new double[10]));

        var main = File.ReadAllText(_main);
        Assert.StartsWith("# BEGIN FpsTune Equalizer", main);
        Assert.EndsWith("# 用户原有配置\n", main);
        Assert.Equal(new[] { "FpsTune-Equalizer.txt", "config.txt" },
            Directory.GetFiles(_root).Select(Path.GetFileName).OrderBy(n => n, StringComparer.Ordinal));
        var own = File.ReadAllText(_own);
        Assert.StartsWith("# Managed by FPS Tune.", own);
        Assert.Contains("Device: " + Guid.Parse(Headset.Id).ToString("B"), own);
        Assert.Single(Directory.GetFiles(Path.Combine(_root, "receipts")));
    }
}
