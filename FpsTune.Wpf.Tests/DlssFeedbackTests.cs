using System.Diagnostics;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Xml.Linq;
using FpsTune.Wpf.Services;
using FpsTune.Wpf.Views;
using Xunit;

namespace FpsTune.Wpf.Tests;

[CollectionDefinition("DLSS feedback UI", DisableParallelization = true)]
public sealed class DlssFeedbackCollection;

[Collection("DLSS feedback UI")]
public sealed class DlssFeedbackTests
{
    [Fact]
    public void Refresh_keeps_failed_choice_and_error_then_syncs_verified_apply_restore_and_game_change()
    {
        // WPF permits one Application per process; keep global resources out of other service tests.
        if (Environment.GetEnvironmentVariable("FPSTUNE_DLSS_UI_TEST_CHILD") != "1")
        {
            RunIsolated();
            return;
        }
        Exception? error = null;
        var thread = new Thread(() =>
        {
            Application? app = null;
            try
            {
                // Load the real UI resources without starting App services or touching a driver.
                app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                app.Resources = LoadResources();
                var page = new DisplayQualityView();
                var k = (RadioButton)page.FindName("PresetK");
                var follow = (RadioButton)page.FindName("PresetFollowGame");
                var text = (TextBlock)page.FindName("StateText");
                var badge = (TextBlock)page.FindName("TabStatusDlss");

                Snapshot(page, "first.exe", false);
                Assert.True(follow.IsChecked);
                k.IsChecked = true;
                const string failure = "Save readback failed: 10E41DF3; original backup retained.";
                Field(page, "_dlssStatus", failure);
                Snapshot(page, "first.exe", false);
                Assert.True(k.IsChecked);
                Assert.False(follow.IsChecked);
                Assert.Equal(failure, text.Text);
                Assert.Equal(Str.T("Str.FollowGame"), badge.Text);
                Assert.True(((Button)page.FindName("ApplyButton")).IsEnabled);

                // Programmatic Checked must not erase a failure or the verified operation result.
                Field(page, "_presetSelectionDirty", false);
                Snapshot(page, "first.exe", false);
                Assert.True(follow.IsChecked);
                Assert.Equal(failure, text.Text);
                const string verified = "K was saved and independently verified.";
                Field(page, "_dlssStatus", verified);
                Snapshot(page, "first.exe", true);
                Assert.True(k.IsChecked);
                Assert.Equal(verified, text.Text);
                Assert.Equal("K", badge.Text);

                const string restored = "Original override restored.";
                Field(page, "_dlssStatus", restored);
                Snapshot(page, "first.exe", false);
                Assert.True(follow.IsChecked);
                Assert.Equal(restored, text.Text);

                k.IsChecked = true;
                Field(page, "_dlssStatus", failure);
                Snapshot(page, "second.exe", false);
                Assert.True(follow.IsChecked);
                Assert.Equal(Str.T("Str.NotOverriddenInGame"), text.Text);
            }
            catch (Exception ex) { error = ex; }
            finally { app?.Shutdown(); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(15)), "DLSS UI regression did not finish.");
        if (error is not null) ExceptionDispatchInfo.Capture(error).Throw();
    }

    private static void RunIsolated()
    {
        var results = Path.Combine(RepoRoot(), "work", "test-results", "dlss-ui-child-" + Guid.NewGuid().ToString("N"));
        var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        start.Environment["FPSTUNE_DLSS_UI_TEST_CHILD"] = "1";
        start.ArgumentList.Add("vstest");
        start.ArgumentList.Add(typeof(DlssFeedbackTests).Assembly.Location);
        start.ArgumentList.Add("--TestCaseFilter:FullyQualifiedName~DlssFeedbackTests");
        start.ArgumentList.Add("--logger:trx;LogFileName=ui-child.trx");
        start.ArgumentList.Add("--ResultsDirectory:" + results);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var errors = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(45000))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException("Isolated WPF regression did not finish.");
        }
        Assert.True(process.ExitCode == 0, output.GetAwaiter().GetResult() + errors.GetAwaiter().GetResult());
        var trx = XDocument.Load(Path.Combine(results, "ui-child.trx"));
        var counters = trx.Descendants().Single(e => e.Name.LocalName == "Counters");
        Assert.Equal("1", (string?)counters.Attribute("executed"));
        Assert.Equal("1", (string?)counters.Attribute("passed"));
        Assert.Equal("0", (string?)counters.Attribute("failed"));
    }

    private static void Field(DisplayQualityView page, string name, object value)
        => typeof(DisplayQualityView).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(page, value);

    private static void Snapshot(DisplayQualityView page, string exe, bool covered)
    {
        var type = typeof(DisplayQualityView).GetNestedType("StateSnapshot", BindingFlags.NonPublic)!;
        var state = new DisplayQualityService.DlssState(exe, covered, covered ? 11u : null, true);
        var snapshot = Activator.CreateInstance(type, true, true, true, exe, exe,
            state, null, null, null, null, null, null, null);
        typeof(DisplayQualityView).GetMethod("ApplyDlssState", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(page, [snapshot]);
    }

    private static ResourceDictionary LoadResources()
    {
        var document = XDocument.Load(Path.Combine(RepoRoot(), "FpsTune.Wpf", "App.xaml"));
        var dictionary = document.Root!.Elements().Single().Elements().Single();
        foreach (var attribute in document.Root.Attributes().Where(a => a.IsNamespaceDeclaration))
            dictionary.SetAttributeValue(attribute.Name, attribute.Value.StartsWith("clr-namespace:", StringComparison.Ordinal)
                ? attribute.Value + ";assembly=FpsTune" : attribute.Value);
        foreach (var source in dictionary.Descendants().Attributes("Source"))
            source.Value = "pack://application:,,,/FpsTune;component/" + source.Value;
        return (ResourceDictionary)XamlReader.Parse(dictionary.ToString());
    }

    private static string RepoRoot()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "FpsTune.Wpf", "App.xaml")))
            root = root.Parent;
        Assert.NotNull(root);
        return root.FullName;
    }
}
