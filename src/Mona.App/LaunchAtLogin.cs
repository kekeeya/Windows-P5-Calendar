using Microsoft.Win32;
using Mona.Core.Diagnostics;

namespace Mona.App;

/// <summary>
/// Start with Windows, through the per-user Run key.
///
/// Not a scheduled task and not the Startup folder: the Run key needs no elevation,
/// no shortcut file to go stale when the app moves, and it is the one place a user
/// can find and switch the entry off without going through this app — which
/// matters for something that otherwise only lives in the tray.
/// </summary>
internal static class LaunchAtLogin
{
    private const string KeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Mona";

    public static bool Enabled
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(KeyPath);
                return key?.GetValue(ValueName) is string;
            }
            catch (Exception)
            {
                return false;
            }
        }
    }

    public static void Set(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(KeyPath, writable: true);
            if (key is null) return;
            if (enabled)
            {
                string path = Environment.ProcessPath ?? "";
                if (path.Length == 0) return;
                // Quoted, because Program Files has a space in it and an unquoted
                // path there starts a program called "C:\Program".
                key.SetValue(ValueName, $"\"{path}\"");
            }
            else
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }
        }
        catch (Exception error)
        {
            Log.Write($"could not change the login item: {error.Message}");
        }
    }
}
