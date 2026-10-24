using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WindowPilot.App.Services;

// Abwärtskompatibel zu vorhandenen assignment-settings.json-Dateien.
// Früher wurde pro Anwendungsgruppe konfiguriert; die minimalistische
// Oberfläche verwendet nun einen einzigen globalen Zuordnungsmodus.
public sealed record WindowAssignmentSettings(
    int SchemaVersion,
    WindowGroupAssignmentPreference[] GroupPreferences,
    WindowAssignmentMode? GlobalMode = null
);

public sealed class WindowAssignmentSettingsService
{
    private const int CurrentSchemaVersion = 1;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public static string GetSettingsPath()
    {
        string localAppData = Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData);

        return Path.Combine(localAppData, "WindowPilot", "assignment-settings.json");
    }

    // Vorhandene FreeSlots-Einstellungen werden bei der Umstellung
    // auf den globalen Schalter übernommen, damit funktionierende
    // Anordnungen nicht unerwartet auf "Individuell" zurückfallen.
    public WindowAssignmentMode GetGlobalMode()
    {
        WindowAssignmentSettings settings = LoadSettings();

        if (settings.GlobalMode.HasValue)
            return settings.GlobalMode.Value;

        // Ohne explizite Auswahl standardmäßig freie Fensterplätze nutzen.
        return WindowAssignmentMode.FreeSlots;
    }

    public void SaveGlobalMode(WindowAssignmentMode mode)
    {
        if (!Enum.IsDefined(typeof(WindowAssignmentMode), mode))
            throw new ArgumentOutOfRangeException(nameof(mode));

        // Die globale Einstellung ersetzt vorherige gruppenbezogene
        // Ausnahmen bewusst: Es gibt nur noch einen Schalter.
        SaveSettings(new WindowAssignmentSettings(
            CurrentSchemaVersion,
            Array.Empty<WindowGroupAssignmentPreference>(),
            mode));
    }

    // Die Erkennungsdienste arbeiten weiterhin mit Gruppenpräferenzen.
    // Hier werden die Einstellungen für alle im Layout vorkommenden
    // Anwendungsgruppen aus dem globalen Schalter abgeleitet.
    public IReadOnlyList<WindowGroupAssignmentPreference> LoadPreferencesForLayout(
        SavedLayout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);

        if (GetGlobalMode() != WindowAssignmentMode.FreeSlots)
            return Array.Empty<WindowGroupAssignmentPreference>();

        return layout.Windows
            .GroupBy(
                rule => rule.ProcessName + "\u001F" + rule.WindowClass,
                StringComparer.OrdinalIgnoreCase)
            // Einzelne Fenster benötigen keine austauschbaren Plätze.
            // Sie bleiben auch bei global aktivierten FreeSlots individuell,
            // damit ihre verlässliche bisherige Erkennung erhalten bleibt.
            .Where(group => group.Count() > 1)
            .Select(group => group.First())
            .Select(rule => new WindowGroupAssignmentPreference(
                rule.ProcessName,
                rule.WindowClass,
                WindowAssignmentMode.FreeSlots))
            .ToArray();
    }

    // Legacy-Methoden bleiben für bestehende Aufrufer erhalten.
    public IReadOnlyList<WindowGroupAssignmentPreference> LoadPreferences()
        => LoadSettings().GroupPreferences;

    public WindowAssignmentMode GetMode(string processName, string windowClass)
        => GetGlobalMode();

    public void SavePreference(
        string processName,
        string windowClass,
        WindowAssignmentMode mode)
    {
        if (string.IsNullOrWhiteSpace(processName) ||
            string.IsNullOrWhiteSpace(windowClass))
        {
            throw new ArgumentException(
                "Application name and window class are required.");
        }

        // Historische API: In der vereinfachten Anwendung werden
        // Einstellungen für alle Fenstergruppen gemeinsam festgelegt.
        SaveGlobalMode(mode);
    }

    private static WindowAssignmentSettings LoadSettings()
    {
        string path = GetSettingsPath();

        if (!File.Exists(path))
        {
            return new WindowAssignmentSettings(
                CurrentSchemaVersion,
                Array.Empty<WindowGroupAssignmentPreference>());
        }

        WindowAssignmentSettings? settings;

        try
        {
            string json = File.ReadAllText(path, Encoding.UTF8);
            settings = JsonSerializer.Deserialize<WindowAssignmentSettings>(
                json,
                JsonOptions);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException(
                "The window assignment settings file contains invalid data.", ex);
        }

        if (settings is null ||
            settings.SchemaVersion != CurrentSchemaVersion ||
            settings.GroupPreferences is null ||
            (settings.GlobalMode.HasValue &&
             !Enum.IsDefined(typeof(WindowAssignmentMode), settings.GlobalMode.Value)))
        {
            throw new InvalidDataException(
                "The window assignment settings have an invalid format.");
        }

        foreach (WindowGroupAssignmentPreference preference in settings.GroupPreferences)
        {
            if (preference is null ||
                string.IsNullOrWhiteSpace(preference.ProcessName) ||
                string.IsNullOrWhiteSpace(preference.WindowClass) ||
                !Enum.IsDefined(typeof(WindowAssignmentMode), preference.Mode))
            {
                throw new InvalidDataException(
                    "The window assignment settings contain an invalid entry.");
            }
        }

        return settings;
    }

    private static void SaveSettings(WindowAssignmentSettings settings)
    {
        string path = GetSettingsPath();
        string? directory = Path.GetDirectoryName(path);

        if (string.IsNullOrWhiteSpace(directory))
            throw new InvalidOperationException("The storage path is invalid.");

        Directory.CreateDirectory(directory);

        string json = JsonSerializer.Serialize(settings, JsonOptions);
        string temporaryPath = Path.Combine(
            directory,
            $"assignment-{Guid.NewGuid():N}.tmp");

        try
        {
            File.WriteAllText(temporaryPath, json, new UTF8Encoding(false));
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }
}
