using System.Diagnostics;
using System.IO;
using System.Text;

namespace FpsTune.Wpf.Services;

internal static class UpdateWorker
{
    internal static void Start(UpdateAttempt attempt, CancellationToken token)
    {
        var script = UpdateRecovery.AttemptPath(attempt.Id, "worker.ps1");
        using (var resource = typeof(UpdateWorker).Assembly.GetManifestResourceStream("FpsTune.UpdateWorker.ps1")
            ?? throw new InvalidOperationException("Missing update worker."))
        using (var reader = new StreamReader(resource))
            AtomicFile.WriteAllTextDurable(script, reader.ReadToEnd(), new UTF8Encoding(true));
        using var scriptLease = new FileStream(script, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var receiptLease = new FileStream(UpdateRecovery.AttemptPath(attempt.Id, "launch.json"), FileMode.Open, FileAccess.Read, FileShare.Read);
        var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
            "WindowsPowerShell", "v1.0", "powershell.exe"))
        { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetDirectoryName(script)! };
        foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", script,
            "-Receipt", UpdateRecovery.AttemptPath(attempt.Id, "launch.json") }) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException(Str.T("Str.CannotStartInstaller"));
        var deadline = DateTime.UtcNow.AddSeconds(15);
        try
        {
            while (true)
            {
                token.ThrowIfCancellationRequested();
                if (File.Exists(UpdateRecovery.AttemptPath(attempt.Id, "ready"))) return;
                if (process.HasExited) throw new InvalidOperationException(Str.T("Str.UpdateWorkerFailed"));
                if (DateTime.UtcNow > deadline) throw new TimeoutException(Str.T("Str.UpdateWorkerFailed"));
                Thread.Sleep(50);
            }
        }
        catch
        {
            File.WriteAllText(UpdateRecovery.AttemptPath(attempt.Id, "abort"), "abort");
            throw;
        }
    }
}
