using System;
using System.IO;

namespace WindowPilot.App.Services;

public static class WindowPilotDataService
{
    public static string DataDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "WindowPilot");

    public static bool HasSavedData() =>
        Directory.Exists(DataDirectory) &&
        (File.Exists(LayoutCaptureService.GetDefaultLayoutPath()) ||
         File.Exists(WindowAssignmentSettingsService.GetSettingsPath()) ||
         Directory.GetFileSystemEntries(DataDirectory, "*", SearchOption.AllDirectories).Length > 0);

    public static void DeleteAllLocalData()
    {
        // Ausschließlich das eigene LocalAppData-Verzeichnis entfernen.
        // Der Autostart liegt getrennt davon in HKCU\...\Run und
        // wird durch diesen Vorgang ausdrücklich NICHT verändert.
        if (Directory.Exists(DataDirectory))
            Directory.Delete(DataDirectory, recursive: true);
    }
}
