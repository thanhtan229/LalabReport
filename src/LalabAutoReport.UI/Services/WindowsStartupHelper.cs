using System;
using System.IO;
using Microsoft.Win32;

namespace LalabAutoReport.UI.Services;

/// <summary>
/// Helper for registering and unregistering Windows startup entry in CurrentUser Registry Run key
/// </summary>
public static class WindowsStartupHelper
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string AppName = "LalabAutoReport";

    public static bool IsAutoStartConfigured()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, false);
            return key?.GetValue(AppName) != null;
        }
        catch
        {
            return false;
        }
    }

    public static bool IsAutoStartHostProcess(string? processName)
    {
        if (string.IsNullOrWhiteSpace(processName)) return false;
        return processName.Equals("LalabAutoReport.UI", StringComparison.OrdinalIgnoreCase)
            || processName.Equals("LalabAutoReport", StringComparison.OrdinalIgnoreCase);
    }

    public static void SetAutoStart(bool enable)
    {
        try
        {
            // Never write the Run key from tests, harnesses or dotnet hosts.
            // Otherwise testhost.exe (or a harness) would become the Windows startup entry.
            string? processName = Path.GetFileNameWithoutExtension(Environment.ProcessPath);
            if (!IsAutoStartHostProcess(processName)) return;

            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, true);
            if (key == null) return;

            if (enable)
            {
                string? exePath = Environment.ProcessPath;
                if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath))
                {
                    exePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "LalabAutoReport.UI.exe");
                }

                // Append --minimized argument to launch directly to system tray
                string command = $"\"{exePath}\" --minimized";
                key.SetValue(AppName, command);
            }
            else
            {
                key.DeleteValue(AppName, false);
            }
        }
        catch
        {
            // Ignore registry permission errors if any
        }
    }
}
