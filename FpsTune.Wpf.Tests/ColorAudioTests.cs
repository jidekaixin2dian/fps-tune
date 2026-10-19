using System.Text;
using FpsTune.Wpf.Services;
using Xunit;

namespace FpsTune.Wpf.Tests;

[Collection("BackupService serial")]
public sealed class ColorAudioTests
{
    [Fact]
    public void Screen_presets_have_distinct_curves_and_restore_the_selected_screen_original()
    {
        using var screens = new MultiDisplayColorTests();
        var original = MultiDisplayColorTests.LinearRamp();
        var curves = Enum.GetValues<IccFilterPreset>().Select(p => ScreenColorService.BuildRamp(ScreenColorSettings.ForPreset(p), original)).ToArray();
        Assert.Equal(curves.Length, curves.Select(c => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Runtime.InteropServices.MemoryMarshal.AsBytes(c.AsSpan())))).Distinct().Count());
        screens.Displays[1] = screens.Displays[1] with { FriendlyName = "AMD Radeon display" };
        IccFilterService.Apply(IccFilterPreset.Neutral, "B"); // Migrate an old managed ICC receipt, not the vendor's profile.
        ScreenColorService.Apply(new(Brightness: 5, Contrast: 110, Gamma: 1.1, Blue: 90), "B");
        Assert.Equal(new[] { "b.icm" }, screens.Profiles["B"]);
        Assert.False(ScreenColorService.Matches(original, screens.Ramps["B"]));
        Assert.Equal(original, screens.Ramps["A"]);
        ScreenColorService.Apply(new(Shadows: 12), "B");
        Assert.True(ScreenColorService.Restore("B"));
        Assert.Equal(original, screens.Ramps["B"]);
        Assert.Empty(ScreenColorService.BackupTargets());
    }
    [Fact]
    public void Gamma_silent_refusal_keeps_original_and_HDR_is_rejected_without_writes()
    {
        using var screens = new MultiDisplayColorTests { IgnoreGammaWrite = true };
        Assert.Throws<InvalidOperationException>(() => ScreenColorService.Apply(new(Brightness: 10), "B"));
        Assert.Single(ScreenColorService.BackupTargets());
        Assert.True(ScreenColorService.Restore("B"));
        screens.Displays[1] = screens.Displays[1] with { AdvancedColorEnabled = true, ColorMode = DisplayColorMode.Hdr };
        var count = screens.Writes.Count;
        Assert.False(ScreenColorService.GetState("B").Supported);
        Assert.Throws<InvalidOperationException>(() => ScreenColorService.Apply(new(Brightness: 10), "B"));
        Assert.Equal(count, screens.Writes.Count);
    }
    [Fact]
    public void SDR_auto_color_management_is_not_HDR_and_unknown_mode_does_not_guess()
    {
        using var screens = new MultiDisplayColorTests();
        screens.Displays[1] = screens.Displays[1] with { AdvancedColorEnabled = true, ColorMode = DisplayTopology.DecodeColorMode(1) };
        Assert.True(ScreenColorService.GetState("B").Supported);
        ScreenColorService.Apply(new(Brightness: 5), "B");
        Assert.True(ScreenColorService.Restore("B"));
        screens.Displays[1] = screens.Displays[1] with { ColorMode = DisplayTopology.DecodeColorMode(2) };
        Assert.False(ScreenColorService.GetState("B").Supported);
        screens.Displays[1] = screens.Displays[1] with { ColorMode = DisplayTopology.DecodeColorMode(99) };
        Assert.False(ScreenColorService.GetState("B").Supported);
        Assert.Empty(ScreenColorService.BackupTargets());
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void Equalizer_preserves_original_config_encoding_and_later_unrelated_edits(bool unicode)
    {
        var dir = Path.Combine(Path.GetTempPath(), "fpstune-eq-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var encoding = unicode ? Encoding.Unicode : new UTF8Encoding(true);
        var main = Path.Combine(dir, "config.txt");
        File.WriteAllText(main, "# 用户原有配置\r\nPreamp: -1 dB\r\n", encoding);
        var original = File.ReadAllBytes(main);
        AudioEqualizerService.EngineOverride = new(dir, dir, "unused");
        AudioEqualizerService.BackupDirOverride = Path.Combine(dir, "receipts");
        try
        {
            var gains = new double[10]; gains[6] = 5;
            var settings = new EqualizerSettings(Guid.NewGuid().ToString("B"), gains);
            AudioEqualizerService.Apply(settings);
            var own = File.ReadAllText(Path.Combine(dir, "FpsTune-Equalizer.txt"));
            Assert.Contains("Preamp: -5 dB", own);
            Assert.Contains("2000 5", own);
            File.AppendAllText(Path.Combine(dir, "FpsTune-Equalizer.txt"), "# external modification");
            Assert.Throws<InvalidOperationException>(() => AudioEqualizerService.Restore());
            Assert.True(AudioEqualizerService.HasBackup);
            File.WriteAllText(Path.Combine(dir, "FpsTune-Equalizer.txt"), own, new UTF8Encoding(false));
            AudioEqualizerService.Apply(settings with { Enabled = false });
            Assert.Contains("Bypass", File.ReadAllText(Path.Combine(dir, "FpsTune-Equalizer.txt")));
            File.AppendAllText(main, "# Other tool\r\n", encoding);
            Assert.True(AudioEqualizerService.Restore());
            Assert.Equal(original.Concat(encoding.GetBytes("# Other tool\r\n")).ToArray(), File.ReadAllBytes(main));
            Assert.False(File.Exists(Path.Combine(dir, "FpsTune-Equalizer.txt")));
        }
        finally { AudioEqualizerService.EngineOverride = null; AudioEqualizerService.BackupDirOverride = null; Directory.Delete(dir, true); }
    }
    [Fact]
    public void ICC_single_gamma_uses_two_byte_u8fixed8_value()
    {
        var bytes = IccProfileGenerator.Build(IccFilterPreset.Neutral);
        static int Be(byte[] b, int offset) => System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(b.AsSpan(offset, 4));
        var count = Be(bytes, 128);
        var tag = Enumerable.Range(0, count).Select(i => 132 + i * 12).Single(o => Encoding.ASCII.GetString(bytes, o, 4) == "rTRC");
        Assert.Equal(14, Be(bytes, tag + 8));
        var data = Be(bytes, tag + 4);
        Assert.Equal(1, Be(bytes, data + 8));
        Assert.Equal((ushort)Math.Round(2.2 * 256), System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(data + 12, 2)));
    }
}
