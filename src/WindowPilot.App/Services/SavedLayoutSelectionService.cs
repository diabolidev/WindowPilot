using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;

namespace WindowPilot.App.Services;

// Ordnet die heute geöffneten Fenster den Regeln des zuletzt
// gespeicherten Layouts zu. HWND/PID werden nicht dauerhaft gespeichert.
// Die Zuordnung verändert keine Fensterpositionen.
public sealed class SavedLayoutSelectionService
{
    private readonly WindowStructureSignatureService _structures = new();

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left, Top, Right, Bottom;
        public int Width => Right - Left;
        public int Height => Bottom - Top;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hWnd, out NativeRect rect);

    public HashSet<WindowInstanceKey> FindSavedWindows(
        SavedLayout layout,
        IReadOnlyList<DiscoveredWindow> windows)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(windows);

        var results = new HashSet<WindowInstanceKey>();
        var signatures = new Dictionary<WindowInstanceKey, string[]?>();
        var available = new List<DiscoveredWindow>(windows);

        // Erst eindeutige Fenstergruppen, dann mehrdeutige Gruppen.
        // Jede geöffnete Instanz darf nur eine gespeicherte Regel belegen.
        var rules = layout.Windows
            .OrderBy(rule => layout.Windows.Count(other =>
                Same(other.ProcessName, rule.ProcessName) &&
                Same(other.WindowClass, rule.WindowClass)))
            .ToArray();

        foreach (SavedWindowRule rule in rules)
        {
            var ranked = new List<(DiscoveredWindow Window, int Score)>();

            foreach (DiscoveredWindow window in available)
            {
                if (!Same(rule.ProcessName, window.ProcessName) ||
                    !Same(rule.WindowClass, window.ClassName))
                    continue;

                bool titleMatches = Same(rule.WindowTitle, window.Title);
                bool genericHost = IsGenericHost(rule.ProcessName);

                // Ein universeller Windows-Host kann verschiedene Apps
                // enthalten. Hier reicht Prozess + Fensterklasse nicht.
                if (genericHost && !titleMatches)
                    continue;

                // Ohne eindeutigen Titel keine unsichere Verwechslung
                // zwischen mehreren Dokumenten derselben Anwendung.
                if (!titleMatches &&
                    layout.Windows.Count(other =>
                        Same(other.ProcessName, rule.ProcessName) &&
                        Same(other.WindowClass, rule.WindowClass)) > 1)
                    continue;

                if (rule.WindowStructureMarkers is { Length: >= 3 })
                {
                    WindowInstanceKey key = new(window.Handle, window.ProcessId);
                    if (!signatures.TryGetValue(key, out string[]? markers))
                    {
                        markers = _structures.ReadMarkers(window.Handle);
                        signatures[key] = markers;
                    }

                    if (!WindowStructureSignatureService.AreCompatible(
                        rule.WindowStructureMarkers, markers))
                        continue;
                }

                int score = titleMatches ? 1000 : 100;
                score += ScoreGeometry(rule, window);
                ranked.Add((window, score));
            }

            if (ranked.Count == 0)
                continue;

            DiscoveredWindow winner = ranked
                .OrderByDescending(x => x.Score)
                .ThenBy(x => x.Window.Handle.ToInt64())
                .First().Window;

            results.Add(new WindowInstanceKey(winner.Handle, winner.ProcessId));
            available.Remove(winner);
        }

        return results;
    }

    private static int ScoreGeometry(SavedWindowRule rule, DiscoveredWindow window)
    {
        if (!GetWindowRect(window.Handle, out NativeRect current))
            return 0;

        int targetX;
        int targetY;
        if (rule.OuterBoundsRelativeToWorkArea is { } relative)
        {
            targetX = rule.MonitorWorkArea.X + relative.X;
            targetY = rule.MonitorWorkArea.Y + relative.Y;
        }
        else
        {
            targetX = rule.VisibleFrameBoundsPhysical?.X ?? rule.MonitorWorkArea.X;
            targetY = rule.VisibleFrameBoundsPhysical?.Y ?? rule.MonitorWorkArea.Y;
        }

        long distance = Math.Abs((long)current.Left - targetX) +
                        Math.Abs((long)current.Top - targetY);
        // Geometrie hilft nur bei der Zuordnung ansonsten gleicher
        // Fenster; sie darf einen falschen Fenstertyp nicht legitimieren.
        return Math.Max(0, 200 - (int)Math.Min(200, distance / 10));
    }

    private static bool Same(string a, string b) =>
        string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static bool IsGenericHost(string processName) =>
        Same(processName, "ApplicationFrameHost") ||
        Same(processName, "explorer") ||
        Same(processName, "msedgewebview2");
}
