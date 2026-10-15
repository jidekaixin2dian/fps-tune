using System.IO;
using Microsoft.Win32;

namespace FpsTune.Wpf.Services;

// Account preference deliberately lives outside installation and app-data directories.
// The installer neither owns nor removes this key; restoring app backups does not reset it.
internal sealed class CommunityPreferences(string keyPath = @"Software\FpsTune\Preferences")
{
    private const string ValueName = "CommunityReminderDismissed";

    internal bool IsSuppressed
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(keyPath);
                return key?.GetValue(ValueName) is int value && value == 1;
            }
            catch { return false; }
        }
    }

    internal void Suppress()
    {
        using var key = Registry.CurrentUser.CreateSubKey(keyPath, writable: true);
        key.SetValue(ValueName, 1, RegistryValueKind.DWord);
        key.Flush();
        if (key.GetValue(ValueName) is not int value || value != 1)
            throw new IOException("Community preference could not be verified.");
    }
}
