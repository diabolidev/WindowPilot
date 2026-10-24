
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace WindowPilot.App.Services;

public sealed class WindowAutoRestoreService : IDisposable
{
    private const int ScanDelayMilliseconds = 200;
    private const int MaximumRestoreAttempts = 5;
    private const int MaximumStructureAttempts = 5;
    private const uint MonitorDefaultToNearest = 2;

    private readonly WindowDiscoveryService
        _discoveryService = new();

    private readonly WindowRuleMatchingService
        _matchingService = new();

    private readonly LayoutRestoreService
        _restoreService = new();

    private readonly WindowAssignmentSettingsService
        _settingsService = new();

    private readonly WindowStructureSignatureService
        _structureService = new();

    private readonly MonitorService
        _monitorService = new();

    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _scanTimer;

    private readonly HashSet<WindowInstanceKey>
        _startupWindows = new();

    private readonly HashSet<WindowInstanceKey>
        _restoredWindows = new();

    private readonly HashSet<WindowInstanceKey>
        _ignoredWindows = new();

    private readonly HashSet<WindowInstanceKey>
        _verifiedWindowTypes = new();

    private readonly HashSet<WindowInstanceKey>
        _excludedWindowTypes = new();

    private readonly Dictionary<WindowInstanceKey, int>
        _structureAttempts = new();

    private readonly Dictionary<WindowInstanceKey, int>
        _restoreAttempts = new();

    // Nur zur Laufzeit gültige Zuordnungen:
    // Gespeicherte Regel -> sichtbare Fensterinstanz.
    private readonly Dictionary<Guid, WindowInstanceKey>
        _activeAssignments = new();

    private WindowEventWatcher? _eventWatcher;

    private volatile bool _started;
    private volatile bool _disposed;

    // Beim Start dürfen schon sichtbare Fenster ebenfalls einmalig
    // sortiert werden. Windows garantiert keine Autostart-Reihenfolge.
    private bool _restoreExistingAtLogon;
    private DateTime _startupTopologyRetryUntilUtc;

    // Ausschließlich auf dem WPF-Dispatcher verändert.
    private bool _scanRunning;
    private bool _rescanRequested;
    private TaskCompletionSource<LayoutRestoreReport>? _pendingManualRestore;

    public sealed record LayoutRestoreReport(int Positioned, int Matched, int Available);

    // A manual command operates through the same serialized background scanner
    // as automatic placement. This avoids racing with WinEvent processing.
    public Task<LayoutRestoreReport> RestoreLayoutNowAsync()
    {
        _dispatcher.VerifyAccess();
        if (_disposed || !_started)
            return Task.FromException<LayoutRestoreReport>(
                new InvalidOperationException("Window monitoring is not running."));

        if (_pendingManualRestore is not null)
            return _pendingManualRestore.Task;

        _pendingManualRestore = new TaskCompletionSource<LayoutRestoreReport>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        ScheduleScan();
        return _pendingManualRestore.Task;
    }

    private sealed record ScanResult(bool NeedsRetry, LayoutRestoreReport Report);

    private sealed record FilterResult(
        DiscoveredWindow[] EligibleWindows,
        bool NeedsRetry
    );

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;

        public readonly int Width => Right - Left;
        public readonly int Height => Bottom - Top;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(
        IntPtr handle
    );

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(
        IntPtr handle
    );

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(
        IntPtr handle,
        out uint processId
    );

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(
        IntPtr handle,
        out NativeRect rect
    );

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(
        IntPtr handle
    );

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsZoomed(
        IntPtr handle
    );

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(
        IntPtr handle,
        uint flags
    );

    public WindowAutoRestoreService()
    {
        _dispatcher = System.Windows.Application.Current.Dispatcher;

        _scanTimer = new DispatcherTimer(
            DispatcherPriority.Background,
            _dispatcher
        )
        {
            Interval = TimeSpan.FromMilliseconds(
                ScanDelayMilliseconds
            )
        };

        _scanTimer.Tick += ScanTimer_Tick;
    }

    // --------------------------------------------------
    // Start
    // --------------------------------------------------

    public void Start(bool restoreExistingAtLogon = false)
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(
                nameof(WindowAutoRestoreService)
            );
        }

        if (_started)
            return;

        _restoreExistingAtLogon = restoreExistingAtLogon;
        _startupTopologyRetryUntilUtc = DateTime.UtcNow.AddSeconds(25);

        var initialWindows =
            _discoveryService.GetOpenWindows();

        foreach (DiscoveredWindow window in initialWindows)
        {
            _startupWindows.Add(GetKey(window));
        }

        _eventWatcher = new WindowEventWatcher();

        _eventWatcher.WindowsChanged +=
            EventWatcher_WindowsChanged;

        _eventWatcher.UpdateKnownWindows(
            initialWindows.Select(window => window.Handle)
        );

        _started = true;

        Debug.WriteLine(
            "[WindowPilot] Auto Restore started."
        );

        ScheduleScan();
    }

    // --------------------------------------------------
    // Windows-Ereignisse
    // --------------------------------------------------

    private void EventWatcher_WindowsChanged(
        object? sender,
        EventArgs e)
    {
        if (_disposed || !_started)
            return;

        if (_dispatcher.CheckAccess())
        {
            ScheduleScan();
            return;
        }

        if (_dispatcher.HasShutdownStarted ||
            _dispatcher.HasShutdownFinished)
        {
            return;
        }

        try
        {
            _dispatcher.BeginInvoke(
                new Action(ScheduleScan),
                DispatcherPriority.Background
            );
        }
        catch (InvalidOperationException)
        {
            // Anwendung wird beendet.
        }
    }

    private void ScheduleScan()
    {
        if (_disposed || !_started)
            return;

        if (_scanRunning)
        {
            _rescanRequested = true;
            return;
        }

        _scanTimer.Stop();
        _scanTimer.Start();
    }

    // --------------------------------------------------
    // Hintergrundprüfung
    // --------------------------------------------------

    private async void ScanTimer_Tick(
        object? sender,
        EventArgs e)
    {
        _scanTimer.Stop();

        if (_disposed || !_started)
            return;

        if (_scanRunning)
        {
            _rescanRequested = true;
            return;
        }

        _scanRunning = true;
        _rescanRequested = false;

        bool needsRetry = false;
        // This is an explicit command, never an automatic continuous force.
        TaskCompletionSource<LayoutRestoreReport>? manualRequest = _pendingManualRestore;
        _pendingManualRestore = null;
        try
        {
            ScanResult result = await Task.Run(() => ProcessScan(manualRequest is not null));
            needsRetry = result.NeedsRetry;
            manualRequest?.TrySetResult(result.Report);
        }
        catch (Exception ex)
        {
            Debug.WriteLine("[WindowPilot] Background scan failed: " + ex);
            manualRequest?.TrySetException(ex);
        }
        finally
        {
            _scanRunning = false;

            if (!_disposed &&
                _started &&
                (_rescanRequested || needsRetry))
            {
                _rescanRequested = false;
                ScheduleScan();
            }
        }
    }

    private ScanResult ProcessScan(bool forceRestore)
    {
        if (_disposed || !_started)
            return new ScanResult(false, new LayoutRestoreReport(0, 0, 0));

        var stopwatch = Stopwatch.StartNew();

        try
        {
            var windows =
                _discoveryService.GetOpenWindows()
                    .ToArray();

            if (_disposed)
                return new ScanResult(false, new LayoutRestoreReport(0, 0, 0));

            // Zuerst geschlossene oder ausgeblendete
            // Fenster aus den Platzreservierungen
            // und den Instanz-Caches entfernen.
            CleanupInactiveWindows();

            SavedLayout layout;

            try
            {
                layout =
                    _restoreService.LoadStandardLayout();
            }
            catch (FileNotFoundException)
            {
                if (forceRestore)
                    throw new InvalidOperationException("Save a layout first.");
                return new ScanResult(false, new LayoutRestoreReport(0, 0, 0));
            }

            if (forceRestore)
            {
                // A previously excluded or restored instance may now be eligible.
                _ignoredWindows.Clear();
                _excludedWindowTypes.Clear();
                _structureAttempts.Clear();
                _restoreAttempts.Clear();
            }

            var preferences =
                _settingsService.LoadPreferencesForLayout(layout);

            CleanupInvalidAssignments(
                layout,
                preferences
            );

            var monitors =
                _monitorService.GetMonitors();

            // Beim Anmelden können Displaygeräte noch fehlen oder in
            // temporärer Anordnung erscheinen. Keinesfalls Fenster auf
            // einen nur ähnlich benannten Monitor verschieben.
            if (layout.Windows.Any(rule =>
                MonitorIdentityService.ResolveTarget(rule, monitors) is null))
            {
                if (DateTime.UtcNow < _startupTopologyRetryUntilUtc)
                    return new ScanResult(true, new LayoutRestoreReport(0, 0, 0));

                if (forceRestore)
                    throw new InvalidOperationException(
                        "Not all saved monitors are available unambiguously. " +
                        "Check your display settings and save the layout again.");

                return new ScanResult(false, new LayoutRestoreReport(0, 0, 0));
            }

            FilterResult filter = FilterEligibleWindows(
                layout,
                windows,
                preferences,
                monitors
            );

            if (_disposed)
                return new ScanResult(false, new LayoutRestoreReport(0, 0, 0));

            ReserveStartupWindowsByPosition(
                layout,
                filter.EligibleWindows,
                preferences,
                monitors
            );

            var candidateWindows =
                filter.EligibleWindows
                    .Where(window =>
                    {
                        WindowInstanceKey key =
                            GetKey(window);

                        return
                            forceRestore || _restoreExistingAtLogon ||
                            !_startupWindows.Contains(key) ||
                            _activeAssignments.Values.Contains(key);
                    })
                    .ToArray();

            var options = new WindowRuleMatchingOptions(
                GroupPreferences: preferences,
                ActiveAssignments: _activeAssignments
            );

            var matches = _matchingService.MatchRules(
                layout,
                candidateWindows,
                options
            );

            if (_disposed)
                return new ScanResult(false, new LayoutRestoreReport(0, 0, 0));

            (bool restoreRetry, int positioned) = RestoreMatchedWindows(
                layout,
                candidateWindows,
                matches,
                preferences,
                forceRestore
            );

            int matched = matches.Count(item =>
                item.Status == WindowRuleMatchStatus.Matched && item.Window is not null);
            return new ScanResult(
                filter.NeedsRetry || restoreRetry,
                new LayoutRestoreReport(positioned, matched, candidateWindows.Length));
        }
        finally
        {
            stopwatch.Stop();

            if (stopwatch.ElapsedMilliseconds >= 500)
            {
                Debug.WriteLine(
                    "[WindowPilot] Background scan: " +
                    $"{stopwatch.ElapsedMilliseconds} ms"
                );
            }
        }
    }

    // --------------------------------------------------
    // Fenstertypen erkennen
    // --------------------------------------------------

    private FilterResult FilterEligibleWindows(
        SavedLayout layout,
        IReadOnlyList<DiscoveredWindow> windows,
        IReadOnlyList<WindowGroupAssignmentPreference>
            preferences,
        IReadOnlyList<DisplayMonitor> monitors)
    {
        var eligible =
            new List<DiscoveredWindow>();

        bool needsRetry = false;

        var freeSlotRules = layout.Windows
            .Where(rule =>
                IsFreeSlotsMode(rule, preferences))
            .ToArray();

        foreach (DiscoveredWindow window in windows)
        {
            if (_disposed)
                break;

            WindowInstanceKey key = GetKey(window);

            SavedWindowRule[] groupRules =
                freeSlotRules
                    .Where(rule => SameGroup(
                        rule.ProcessName,
                        rule.WindowClass,
                        window.ProcessName,
                        window.ClassName
                    ))
                    .ToArray();

            if (groupRules.Length == 0)
            {
                eligible.Add(window);
                continue;
            }

            if (_excludedWindowTypes.Contains(key))
                continue;

            if (_verifiedWindowTypes.Contains(key))
            {
                eligible.Add(window);
                continue;
            }

            // Vorhandene Fenster nicht aufwendig
            // untersuchen, solange sie sich auf
            // keinem gespeicherten Platz befinden.
            if (!_restoreExistingAtLogon &&
                _startupWindows.Contains(key) &&
                !_activeAssignments.Values.Contains(key) &&
                !IsAtAnySavedPosition(
                    window,
                    groupRules,
                    monitors))
            {
                continue;
            }

            bool compatible = MatchesSavedWindowType(
                window,
                groupRules
            );

            if (compatible)
            {
                _verifiedWindowTypes.Add(key);
                _structureAttempts.Remove(key);

                eligible.Add(window);
                continue;
            }

            int attempt =
                _structureAttempts.TryGetValue(
                    key,
                    out int previous)
                    ? previous + 1
                    : 1;

            if (attempt >= MaximumStructureAttempts)
            {
                _structureAttempts.Remove(key);
                _excludedWindowTypes.Add(key);

                Debug.WriteLine(
                    "[WindowPilot] Window type excluded: " +
                    $"{window.ProcessName} | {window.Title} | " +
                    $"Handle={window.Handle}"
                );
            }
            else
            {
                _structureAttempts[key] = attempt;
                needsRetry = true;
            }
        }

        return new FilterResult(
            eligible.ToArray(),
            needsRetry
        );
    }

    private bool MatchesSavedWindowType(
        DiscoveredWindow window,
        IReadOnlyList<SavedWindowRule> rules)
    {
        if (rules.Count == 0 ||
            IsIconic(window.Handle))
        {
            return false;
        }

        if (IsGenericHostProcess(window.ProcessName))
        {
            bool matchingTitle = rules.Any(rule =>
                string.Equals(
                    rule.WindowTitle,
                    window.Title,
                    StringComparison.OrdinalIgnoreCase
                )
            );

            if (!matchingTitle)
                return false;
        }

        SavedWindowRule[] structuredRules =
            rules
                .Where(rule =>
                    rule.WindowStructureMarkers is
                    { Length: >= 3 })
                .ToArray();

        if (structuredRules.Length > 0)
        {
            if (structuredRules.Length != rules.Count)
                return false;

            string[] reference =
                structuredRules[0].WindowStructureMarkers!;

            bool uniformGroup = structuredRules.All(
                rule =>
                    WindowStructureSignatureService
                        .AreCompatible(
                            reference,
                            rule.WindowStructureMarkers
                        )
            );

            if (!uniformGroup)
                return false;

            string[]? currentMarkers =
                _structureService.ReadMarkers(
                    window.Handle
                );

            return WindowStructureSignatureService
                .AreCompatible(
                    reference,
                    currentMarkers
                );
        }

        // Eingeschränkter Rückfall für Anwendungen,
        // die keine geeignete UI-Struktur bereitstellen.
        if (!GetWindowRect(
            window.Handle,
            out NativeRect rect))
        {
            return false;
        }

        foreach (SavedWindowRule rule in rules)
        {
            if (rule.WindowState == "Maximized")
                return true;

            SavedRectangle? saved =
                rule.OuterBoundsRelativeToWorkArea;

            if (rule.WindowState != "Normal" ||
                saved is null)
            {
                continue;
            }

            double widthTolerance =
                Math.Max(48, saved.Width * 0.15);

            double heightTolerance =
                Math.Max(48, saved.Height * 0.15);

            if (Math.Abs(
                    (double)rect.Width - saved.Width)
                    <= widthTolerance &&
                Math.Abs(
                    (double)rect.Height - saved.Height)
                    <= heightTolerance)
            {
                return true;
            }
        }

        return false;
    }

    // --------------------------------------------------
    // Gespeicherte Plätze erkennen
    // --------------------------------------------------

    private static bool IsAtAnySavedPosition(
        DiscoveredWindow window,
        IReadOnlyList<SavedWindowRule> rules,
        IReadOnlyList<DisplayMonitor> monitors)
    {
        foreach (SavedWindowRule rule in rules)
        {
            DisplayMonitor? monitor =
                    MonitorIdentityService.ResolveTarget(rule, monitors);

            if (monitor is null)
                continue;

            if (GetCurrentPlacementScore(
                window,
                rule,
                monitor).HasValue)
            {
                return true;
            }
        }

        return false;
    }

    private void ReserveStartupWindowsByPosition(
        SavedLayout layout,
        IReadOnlyList<DiscoveredWindow> windows,
        IReadOnlyList<WindowGroupAssignmentPreference>
            preferences,
        IReadOnlyList<DisplayMonitor> monitors)
    {
        SavedWindowRule[] rules = layout.Windows
            .Where(rule =>
                IsFreeSlotsMode(rule, preferences))
            .ToArray();

        foreach (DiscoveredWindow window in windows)
        {
            WindowInstanceKey key = GetKey(window);

            if (!_startupWindows.Contains(key) ||
                _activeAssignments.Values.Contains(key))
            {
                continue;
            }

            var possibilities =
                new List<(SavedWindowRule Rule, long Score)>();

            foreach (SavedWindowRule rule in rules)
            {
                if (_activeAssignments.ContainsKey(rule.RuleId))
                    continue;

                if (!SameGroup(
                    rule.ProcessName,
                    rule.WindowClass,
                    window.ProcessName,
                    window.ClassName))
                {
                    continue;
                }

                if (IsGenericHostProcess(rule.ProcessName) &&
                    !string.Equals(
                        rule.WindowTitle,
                        window.Title,
                        StringComparison.OrdinalIgnoreCase
                    ))
                {
                    continue;
                }

                DisplayMonitor? monitor =
                    MonitorIdentityService.ResolveTarget(rule, monitors);

                if (monitor is null)
                    continue;

                long? score =
                    GetCurrentPlacementScore(
                        window,
                        rule,
                        monitor
                    );

                if (score.HasValue)
                {
                    possibilities.Add(
                        (rule, score.Value)
                    );
                }
            }

            var ordered = possibilities
                .OrderBy(item => item.Score)
                .ToArray();

            if (ordered.Length == 0)
                continue;

            if (ordered.Length > 1 &&
                ordered[0].Score == ordered[1].Score)
            {
                continue;
            }

            SavedWindowRule selected =
                ordered[0].Rule;

            _activeAssignments[selected.RuleId] = key;

            Debug.WriteLine(
                "[WindowPilot] Existing slot reserved: " +
                selected.RuleId
            );
        }
    }

    private static long? GetCurrentPlacementScore(
        DiscoveredWindow window,
        SavedWindowRule rule,
        DisplayMonitor monitor)
    {
        IntPtr currentMonitor = MonitorFromWindow(
            window.Handle,
            MonitorDefaultToNearest
        );

        if (currentMonitor != monitor.Handle)
            return null;

        if (rule.WindowState == "Maximized")
        {
            return IsZoomed(window.Handle)
                ? 0L
                : null;
        }

        if (rule.WindowState != "Normal" ||
            IsIconic(window.Handle) ||
            IsZoomed(window.Handle))
        {
            return null;
        }

        SavedRectangle? bounds =
            rule.OuterBoundsRelativeToWorkArea;

        if (bounds is null)
            return null;

        if (!GetWindowRect(
            window.Handle,
            out NativeRect rect))
        {
            return null;
        }

        long targetX = (long)monitor.WorkX + bounds.X;
        long targetY = (long)monitor.WorkY + bounds.Y;

        long deltaX = Math.Abs(
            (long)rect.Left - targetX
        );

        long deltaY = Math.Abs(
            (long)rect.Top - targetY
        );

        long deltaWidth = Math.Abs(
            (long)rect.Width - bounds.Width
        );

        long deltaHeight = Math.Abs(
            (long)rect.Height - bounds.Height
        );

        if (deltaX > 40 ||
            deltaY > 40 ||
            deltaWidth > 48 ||
            deltaHeight > 48)
        {
            return null;
        }

        return
            deltaX +
            deltaY +
            deltaWidth +
            deltaHeight;
    }

    // --------------------------------------------------
    // Fenster einmalig wiederherstellen
    // --------------------------------------------------

    private (bool NeedsRetry, int Positioned) RestoreMatchedWindows(
        SavedLayout layout,
        IReadOnlyList<DiscoveredWindow> windows,
        IReadOnlyList<WindowRuleMatchResult> matches,
        IReadOnlyList<WindowGroupAssignmentPreference>
            preferences,
        bool forceRestore)
    {
        var matchedByWindow = matches
            .Where(match =>
                match.Status == WindowRuleMatchStatus.Matched &&
                match.Window is not null
            )
            .GroupBy(match => GetKey(match.Window!))
            .ToDictionary(
                group => group.Key,
                group => group.ToArray()
            );

        bool needsRetry = false;
        int positioned = 0;

        foreach (DiscoveredWindow window in windows)
        {
            if (_disposed)
                break;

            WindowInstanceKey key = GetKey(window);

            // Beim Prozessstart werden bereits geöffnete Fenster einmalig
            // einsortiert; spätere manuelle Bewegungen bleiben unberührt.
            // Ein ausdrücklich erzwungener Restore überschreibt die
            // Positionen dagegen bewusst.
            if ((!forceRestore && _startupWindows.Contains(key) && !_restoreExistingAtLogon) ||
                (!forceRestore && _restoredWindows.Contains(key)) ||
                (!forceRestore && _ignoredWindows.Contains(key)) ||
                (!forceRestore && _startupWindows.Contains(key) &&
                 _activeAssignments.Values.Contains(key)))
            {
                continue;
            }

            bool relevant = layout.Windows.Any(rule =>
                SameGroup(
                    rule.ProcessName,
                    rule.WindowClass,
                    window.ProcessName,
                    window.ClassName
                )
            );

            if (!relevant)
                continue;

            if (!matchedByWindow.TryGetValue(
                key,
                out WindowRuleMatchResult[]? assigned))
            {
                needsRetry |= RegisterRestoreFailure(key);
                continue;
            }

            if (assigned.Length != 1)
            {
                _ignoredWindows.Add(key);
                continue;
            }

            SavedWindowRule rule =
                assigned[0].Rule;

            bool freeSlots =
                IsFreeSlotsMode(rule, preferences);

            if (rule.RequiresManualAssignment &&
                !freeSlots)
            {
                _ignoredWindows.Add(key);
                continue;
            }

            if (freeSlots &&
                _activeAssignments.TryGetValue(
                    rule.RuleId,
                    out WindowInstanceKey occupiedBy) &&
                occupiedBy != key)
            {
                Debug.WriteLine(
                    "[WindowPilot] Slot still occupied: " +
                    rule.RuleId
                );

                needsRetry |= RegisterRestoreFailure(key);
                continue;
            }

            try
            {
                _restoreService.RestoreSelectedWindow(
                    window.Handle,
                    rule.RuleId
                );

                _restoredWindows.Add(key);
                positioned++;
                _restoreAttempts.Remove(key);

                if (freeSlots)
                {
                    _activeAssignments[rule.RuleId] = key;
                }

                Debug.WriteLine(
                    "[WindowPilot] Automatically positioned: " +
                    $"{window.ProcessName} | {window.Title} | " +
                    $"Handle={window.Handle} | " +
                    $"Rule={rule.RuleId}"
                );
            }
            catch (Exception ex)
            {
                Debug.WriteLine(
                    "[WindowPilot] Placement failed: " +
                    ex.Message
                );

                needsRetry |= RegisterRestoreFailure(key);
            }
        }

        return (needsRetry, positioned);
    }

    private bool RegisterRestoreFailure(
        WindowInstanceKey key)
    {
        int next =
            _restoreAttempts.TryGetValue(
                key,
                out int previous)
                ? previous + 1
                : 1;

        if (next >= MaximumRestoreAttempts)
        {
            _restoreAttempts.Remove(key);
            _ignoredWindows.Add(key);

            Debug.WriteLine(
                "[WindowPilot] Restore attempts " +
                "exhausted for window: " + key.Handle
            );

            return false;
        }

        _restoreAttempts[key] = next;
        return true;
    }

    // --------------------------------------------------
    // Geschlossene und ausgeblendete Fenster bereinigen
    // --------------------------------------------------

    private void CleanupInactiveWindows()
    {
        // Entscheidend:
        //
        // IsWindow() allein reicht hier nicht.
        //
        // Viele Anwendungen behalten geschlossene
        // Fenster intern bei und blenden sie lediglich
        // aus. Solche Fenster dürfen keinen
        // gespeicherten Platz dauerhaft blockieren.
        //
        // Minimierte Fenster besitzen normalerweise
        // weiterhin den WS_VISIBLE-Stil. Sie bleiben
        // deshalb als vorhandene Instanz erhalten.
        bool IsActivePresentation(WindowInstanceKey key)
        {
            if (!IsWindow(key.Handle))
                return false;

            GetWindowThreadProcessId(
                key.Handle,
                out uint processId
            );

            if (processId == 0 ||
                processId != key.ProcessId)
            {
                return false;
            }

            return IsWindowVisible(key.Handle);
        }

        var trackedKeys = _startupWindows
            .Concat(_restoredWindows)
            .Concat(_ignoredWindows)
            .Concat(_verifiedWindowTypes)
            .Concat(_excludedWindowTypes)
            .Concat(_structureAttempts.Keys)
            .Concat(_restoreAttempts.Keys)
            .Concat(_activeAssignments.Values)
            .Distinct()
            .ToArray();

        var inactiveKeys = trackedKeys
            .Where(key => !IsActivePresentation(key))
            .ToHashSet();

        if (inactiveKeys.Count == 0)
            return;

        _startupWindows.RemoveWhere(
            key => inactiveKeys.Contains(key)
        );

        _restoredWindows.RemoveWhere(
            key => inactiveKeys.Contains(key)
        );

        _ignoredWindows.RemoveWhere(
            key => inactiveKeys.Contains(key)
        );

        _verifiedWindowTypes.RemoveWhere(
            key => inactiveKeys.Contains(key)
        );

        _excludedWindowTypes.RemoveWhere(
            key => inactiveKeys.Contains(key)
        );

        foreach (WindowInstanceKey key in
            _structureAttempts.Keys.ToArray())
        {
            if (inactiveKeys.Contains(key))
                _structureAttempts.Remove(key);
        }

        foreach (WindowInstanceKey key in
            _restoreAttempts.Keys.ToArray())
        {
            if (inactiveKeys.Contains(key))
                _restoreAttempts.Remove(key);
        }

        foreach (Guid ruleId in
            _activeAssignments.Keys.ToArray())
        {
            WindowInstanceKey occupant =
                _activeAssignments[ruleId];

            if (!inactiveKeys.Contains(occupant))
                continue;

            _activeAssignments.Remove(ruleId);

            Debug.WriteLine(
                "[WindowPilot] Window slot released: " +
                $"Rule={ruleId} | Handle={occupant.Handle}"
            );
        }
    }

    private void CleanupInvalidAssignments(
        SavedLayout layout,
        IReadOnlyList<WindowGroupAssignmentPreference>
            preferences)
    {
        var validRules = layout.Windows
            .Where(rule =>
                IsFreeSlotsMode(rule, preferences))
            .Select(rule => rule.RuleId)
            .ToHashSet();

        foreach (Guid ruleId in
            _activeAssignments.Keys.ToArray())
        {
            if (!validRules.Contains(ruleId))
            {
                _activeAssignments.Remove(ruleId);
            }
        }
    }

    // --------------------------------------------------
    // Allgemeine Hilfsfunktionen
    // --------------------------------------------------

    private static bool IsFreeSlotsMode(
        SavedWindowRule rule,
        IReadOnlyList<WindowGroupAssignmentPreference>
            preferences)
    {
        return preferences.Any(preference =>
            preference.Mode == WindowAssignmentMode.FreeSlots &&
            SameGroup(
                rule.ProcessName,
                rule.WindowClass,
                preference.ProcessName,
                preference.WindowClass
            )
        );
    }

    private static bool SameGroup(
        string processA,
        string classA,
        string processB,
        string classB)
    {
        return
            string.Equals(
                processA,
                processB,
                StringComparison.OrdinalIgnoreCase
            ) &&
            string.Equals(
                classA,
                classB,
                StringComparison.OrdinalIgnoreCase
            );
    }

    private static bool IsGenericHostProcess(
        string processName)
    {
        return
            processName.Equals(
                "ApplicationFrameHost",
                StringComparison.OrdinalIgnoreCase
            ) ||
            processName.Equals(
                "explorer",
                StringComparison.OrdinalIgnoreCase
            ) ||
            processName.Equals(
                "msedgewebview2",
                StringComparison.OrdinalIgnoreCase
            );
    }

    private static WindowInstanceKey GetKey(
        DiscoveredWindow window)
    {
        return new WindowInstanceKey(
            window.Handle,
            window.ProcessId
        );
    }

    // --------------------------------------------------
    // Beenden
    // --------------------------------------------------

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _started = false;
        _pendingManualRestore?.TrySetException(
            new ObjectDisposedException(nameof(WindowAutoRestoreService)));
        _pendingManualRestore = null;

        _scanTimer.Stop();
        _scanTimer.Tick -= ScanTimer_Tick;

        if (_eventWatcher is not null)
        {
            _eventWatcher.WindowsChanged -=
                EventWatcher_WindowsChanged;

            _eventWatcher.Dispose();
            _eventWatcher = null;
        }

        // Hintergrundprüfungen dürfen auslaufen.
        // Die verwendeten Collections werden hier
        // deshalb nicht währenddessen geleert.
    }
}
