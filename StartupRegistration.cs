using System.IO;
using Microsoft.Win32;

namespace DisplayBrightness;

internal static class StartupRegistration
{
    internal const string TrayArgument = "--tray";
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "DisplayBrightness";

    private static string Command => $"\"{Environment.ProcessPath}\" {TrayArgument}";

    internal static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return string.Equals(key?.GetValue(ValueName) as string, Command, StringComparison.OrdinalIgnoreCase);
    }

    internal static void SetEnabled(bool enabled)
    {
        if (!enabled)
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            key?.DeleteValue(ValueName, throwOnMissingValue: false);
            return;
        }

        string command = Command;
        if (command.Length > 260)
            throw new PathTooLongException();

        using var run = Registry.CurrentUser.CreateSubKey(RunKey);
        run.SetValue(ValueName, command, RegistryValueKind.String);
    }
}
