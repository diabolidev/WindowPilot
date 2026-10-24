using System;
using System.IO;
using Microsoft.Win32;

namespace WindowPilot.App.Services;

public static class StartupRegistrationService
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ApprovedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    private const string EntryName = "WindowPilot";

    public static bool IsEnabled()
    {
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKey);
        if (key?.GetValue(EntryName) is not string command || string.IsNullOrWhiteSpace(command))
            return false;
        using RegistryKey? approved = Registry.CurrentUser.OpenSubKey(ApprovedKey);
        // Windows' Task Manager writes 03 or 07 to disable a Run entry.
        if (approved?.GetValue(EntryName) is byte[] state && state.Length > 0 &&
            (state[0] == 3 || state[0] == 7))
            return false;
        string exe = ReadExecutableFromCommand(command);
        return !string.IsNullOrWhiteSpace(exe) && File.Exists(exe);
    }

    private static string ReadExecutableFromCommand(string command)
    {
        command = command.Trim();
        if (command.StartsWith('"'))
        {
            int end = command.IndexOf('"', 1);
            return end > 1 ? command[1..end] : "";
        }
        int index = command.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        return index >= 0 ? command[..(index + 4)] : "";
    }

    public static void SetEnabled(bool enabled)
    {
        if (enabled)
        {
            string exe = Environment.ProcessPath ??
                throw new InvalidOperationException("Could not determine the executable path.");
            if (!File.Exists(exe) || !exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Auto Start requires a Windows executable.");
            if (IsVisualStudioBinary(exe))
                throw new InvalidOperationException(
                    "Enable Auto Start in an installed WindowPilot release, not a Visual Studio development build.");

            using RegistryKey run = Registry.CurrentUser.CreateSubKey(RunKey, true)
                ?? throw new InvalidOperationException("Failed to register Auto Start.");
            run.SetValue(EntryName, $"\"{exe}\" --background", RegistryValueKind.String);

            // Explicit switch opt-in also re-enables an entry disabled in Task Manager.
            using RegistryKey approved = Registry.CurrentUser.CreateSubKey(ApprovedKey, true)
                ?? throw new InvalidOperationException("Failed to enable Auto Start in Windows.");
            approved.SetValue(EntryName, new byte[12] { 2, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 },
                RegistryValueKind.Binary);
        }
        else
        {
            using RegistryKey? run = Registry.CurrentUser.OpenSubKey(RunKey, true);
            run?.DeleteValue(EntryName, false);
            using RegistryKey? approved = Registry.CurrentUser.OpenSubKey(ApprovedKey, true);
            approved?.DeleteValue(EntryName, false);
        }
    }

    // The previous app could register the Visual Studio bin\Debug path.
    // When the installed binary starts, replace that stale entry without
    // changing the enabled/disabled state selected by the user.
    public static void RepairInstalledRegistration()
    {
        string exe = Environment.ProcessPath ?? "";
        string expectedDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Programs", "WindowPilot");
        if (!Path.GetFullPath(exe).Equals(
                Path.Combine(expectedDirectory, "WindowPilot.App.exe"),
                StringComparison.OrdinalIgnoreCase))
            return;
        using RegistryKey? approved = Registry.CurrentUser.OpenSubKey(ApprovedKey);
        if (approved?.GetValue(EntryName) is byte[] state && state.Length > 0 &&
            (state[0] == 3 || state[0] == 7)) return;
        using RegistryKey? run = Registry.CurrentUser.OpenSubKey(RunKey, true);
        if (run?.GetValue(EntryName) is not string) return;
        run.SetValue(EntryName, $"\"{exe}\" --background", RegistryValueKind.String);
    }

    private static bool IsVisualStudioBinary(string executable) =>
        executable.Contains(@"\bin\Debug\", StringComparison.OrdinalIgnoreCase) ||
        executable.Contains(@"\bin\Release\", StringComparison.OrdinalIgnoreCase);
}
