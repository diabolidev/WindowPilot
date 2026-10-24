
using System;
using System.Collections.Generic;
using System.Linq;

namespace WindowPilot.App.Services;

// Wie sollen mehrere gleichartige Fenster
// ihren gespeicherten Regeln zugeordnet werden?
public enum WindowAssignmentMode
{
    Individual,
    FreeSlots
}

public enum WindowRuleMatchStatus
{
    Matched,
    NotFound,
    Ambiguous,
    ManualAssignmentRequired
}

// Identifiziert eine konkrete Fensterinstanz
// während der laufenden Windows-Sitzung.
public readonly record struct WindowInstanceKey(
    IntPtr Handle,
    uint ProcessId
);

// Einstellungen für eine Gruppe gleichartiger Fenster.
// Die Zuordnung bezieht sich auf Prozess und Fensterklasse.
public sealed record WindowGroupAssignmentPreference(
    string ProcessName,
    string WindowClass,
    WindowAssignmentMode Mode
);

// Bereits vergebene Plätze können bei zukünftigen
// Abgleichen reserviert bleiben.
//
// Noch wird dies nicht vom AutoRestoreService verwendet.
// Die Anbindung folgt in einem späteren Schritt.
public sealed record WindowRuleMatchingOptions(
    IReadOnlyList<WindowGroupAssignmentPreference>
        GroupPreferences,
    IReadOnlyDictionary<Guid, WindowInstanceKey>?
        ActiveAssignments = null
);

public sealed record WindowRuleMatchResult(
    SavedWindowRule Rule,
    DiscoveredWindow? Window,
    WindowRuleMatchStatus Status,
    string Description
);

public sealed class WindowRuleMatchingService
{
    private readonly WindowIdentityService
        _identityService = new();

    private sealed record WindowCandidate(
        DiscoveredWindow Window,
        WindowIdentitySnapshot Identity
    )
    {
        public WindowInstanceKey Key => new(
            Window.Handle,
            Window.ProcessId
        );
    }

    // --------------------------------------------------
    // Alle gespeicherten Regeln zuordnen
    // --------------------------------------------------

    public IReadOnlyList<WindowRuleMatchResult> MatchRules(
        SavedLayout layout,
        IReadOnlyList<DiscoveredWindow> windows,
        WindowRuleMatchingOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(windows);

        if (layout.Windows is null)
        {
            throw new InvalidOperationException(
                "The layout does not contain a valid list of rules."
            );
        }

        // Vor dem Abgleich überprüfen wir über unseren
        // universellen IdentityService, welche Fenster
        // tatsächlich noch existieren.
        var snapshots = _identityService.InspectWindows(
            windows
        );

        var windowLookup = windows
            .GroupBy(window => new WindowInstanceKey(
                window.Handle,
                window.ProcessId
            ))
            .ToDictionary(
                group => group.Key,
                group => group.First()
            );

        var liveCandidates = new List<WindowCandidate>();

        foreach (WindowIdentitySnapshot snapshot in snapshots)
        {
            var key = new WindowInstanceKey(
                snapshot.Handle,
                snapshot.ProcessId
            );

            if (!windowLookup.TryGetValue(
                key,
                out DiscoveredWindow? window))
            {
                continue;
            }

            liveCandidates.Add(
                new WindowCandidate(window, snapshot)
            );
        }

        var results = new List<WindowRuleMatchResult>();

        // Gruppen bilden, damit bei mehreren Regeln
        // derselben Anwendung keine Fenster versehentlich
        // mehrfach zugeordnet werden.
        var groups = layout.Windows.GroupBy(
            rule =>
                rule.ProcessName + "\u001F" +
                rule.WindowClass,
            StringComparer.OrdinalIgnoreCase
        );

        foreach (var group in groups)
        {
            SavedWindowRule[] groupRules =
                group.ToArray();

            WindowCandidate[] groupCandidates =
                liveCandidates
                    .Where(candidate => SameApplication(
                        groupRules[0].ProcessName,
                        groupRules[0].WindowClass,
                        candidate.Identity.ProcessName,
                        candidate.Identity.WindowClass
                    ))
                    .ToArray();

            WindowAssignmentMode mode = GetMode(
                groupRules[0],
                options
            );

            if (mode == WindowAssignmentMode.FreeSlots)
            {
                results.AddRange(
                    MatchFreeSlots(
                        groupRules,
                        groupCandidates,
                        options
                    )
                );
            }
            else
            {
                foreach (SavedWindowRule rule in groupRules)
                {
                    results.Add(
                        MatchIndividualRule(
                            rule,
                            groupRules,
                            groupCandidates
                        )
                    );
                }
            }
        }

        // Ursprüngliche Reihenfolge der JSON-Regeln
        // beibehalten.
        var resultsById = results.ToDictionary(
            result => result.Rule.RuleId
        );

        return layout.Windows
            .Select(rule => resultsById[rule.RuleId])
            .ToArray();
    }

    // --------------------------------------------------
    // Zuordnungsmodus bestimmen
    // --------------------------------------------------

    private static WindowAssignmentMode GetMode(
        SavedWindowRule rule,
        WindowRuleMatchingOptions? options)
    {
        if (options?.GroupPreferences is null)
        {
            return WindowAssignmentMode.Individual;
        }

        WindowGroupAssignmentPreference? preference =
            options.GroupPreferences.FirstOrDefault(item =>
                SameApplication(
                    rule.ProcessName,
                    rule.WindowClass,
                    item.ProcessName,
                    item.WindowClass
                )
            );

        return preference?.Mode ??
            WindowAssignmentMode.Individual;
    }

    // --------------------------------------------------
    // Individuelle Fensterzuordnung
    // --------------------------------------------------

    private static WindowRuleMatchResult MatchIndividualRule(
        SavedWindowRule rule,
        IReadOnlyList<SavedWindowRule> groupRules,
        IReadOnlyList<WindowCandidate> groupCandidates)
    {
        // Explizit als mehrdeutig gespeicherte Fenster
        // werden nicht automatisch zugeordnet.
        if (rule.RequiresManualAssignment)
        {
            return Result(
                rule,
                null,
                WindowRuleMatchStatus.ManualAssignmentRequired,
                "This rule requires individual " +
                "window assignment."
            );
        }

        if (groupCandidates.Count == 0)
        {
            return Result(
                rule,
                null,
                WindowRuleMatchStatus.NotFound,
                "No matching window is open."
            );
        }

        // Mehrere gespeicherte Regeln mit demselben
        // Titel dürfen nicht dasselbe Fenster erhalten.
        int identicalRuleCount = groupRules.Count(other =>
            string.Equals(
                other.WindowTitle,
                rule.WindowTitle,
                StringComparison.OrdinalIgnoreCase
            )
        );

        if (identicalRuleCount > 1)
        {
            return Result(
                rule,
                null,
                WindowRuleMatchStatus.ManualAssignmentRequired,
                "Multiple rules share the same " +
                "window title and window class."
            );
        }

        WindowCandidate[] exactTitleMatches =
            groupCandidates
                .Where(candidate => string.Equals(
                    candidate.Identity.WindowTitle,
                    rule.WindowTitle,
                    StringComparison.OrdinalIgnoreCase
                ))
                .ToArray();

        if (exactTitleMatches.Length == 1)
        {
            return Result(
                rule,
                exactTitleMatches[0].Window,
                WindowRuleMatchStatus.Matched,
                "Unique match based on " +
                "application, window class, and title."
            );
        }

        if (exactTitleMatches.Length > 1)
        {
            // Wenn mehrere ansonsten identische
            // Fenster existieren, darf ein eindeutig
            // passender ausführbarer Dateipfad helfen.
            //
            // Der Pfad ist nicht zwingend stabil:
            // Anwendungen können ihn bei Updates ändern.
            // Deshalb ist er kein Pflichtmerkmal.
            if (!string.IsNullOrWhiteSpace(
                rule.ExecutablePath))
            {
                WindowCandidate[] pathMatches =
                    exactTitleMatches
                        .Where(candidate =>
                            SameExecutablePath(
                                rule.ExecutablePath,
                                candidate.Identity.ExecutablePath
                            )
                        )
                        .ToArray();

                if (pathMatches.Length == 1)
                {
                    return Result(
                        rule,
                        pathMatches[0].Window,
                        WindowRuleMatchStatus.Matched,
                        "Unique match including " +
                        "the executable path."
                    );
                }
            }

            return Result(
                rule,
                null,
                WindowRuleMatchStatus.Ambiguous,
                "Multiple open windows match " +
                "this rule."
            );
        }

        // Fenstertitel können sich ändern, beispielsweise
        // bei Discord oder Dokumentfenstern.
        //
        // Dieser Fallback ist ausschließlich zulässig,
        // wenn es genau eine gespeicherte Regel und
        // genau ein mögliches Fenster gibt.
        //
        // Allgemeine Windows-Hostprozesse werden
        // hiervon ausgeschlossen.
        if (groupRules.Count == 1 &&
            groupCandidates.Count == 1 &&
            !IsGenericHostProcess(rule.ProcessName))
        {
            return Result(
                rule,
                groupCandidates[0].Window,
                WindowRuleMatchStatus.Matched,
                "Unique process and class combination. " +
                "The window title has changed."
            );
        }

        if (groupCandidates.Count > 1 ||
            groupRules.Count > 1)
        {
            return Result(
                rule,
                null,
                WindowRuleMatchStatus.Ambiguous,
                "No unambiguous individual " +
                "assignment is possible."
            );
        }

        return Result(
            rule,
            null,
            WindowRuleMatchStatus.NotFound,
            "No window with sufficient " +
            "identification markers was found."
        );
    }

    // --------------------------------------------------
    // Austauschbare Fensterplätze
    // --------------------------------------------------

    private static IReadOnlyList<WindowRuleMatchResult>
        MatchFreeSlots(
            IReadOnlyList<SavedWindowRule> groupRules,
            IReadOnlyList<WindowCandidate> groupCandidates,
            WindowRuleMatchingOptions? options)
    {
        var results = new Dictionary<Guid, WindowRuleMatchResult>();

        var usedWindows = new HashSet<WindowInstanceKey>();

        bool genericHost = IsGenericHostProcess(
            groupRules[0].ProcessName
        );

        // Allgemeine Hostprozesse können verschiedene
        // Anwendungen beherbergen.
        //
        // Ohne eindeutige zusätzliche App-Identität
        // erlauben wir die Platzverteilung dort nur,
        // wenn alle gespeicherten Regeln denselben
        // nicht-leeren Fenstertitel besitzen.
        if (genericHost)
        {
            string expectedTitle =
                groupRules[0].WindowTitle;

            bool safeHostGroup =
                !string.IsNullOrWhiteSpace(expectedTitle) &&
                groupRules.All(rule =>
                    string.Equals(
                        rule.WindowTitle,
                        expectedTitle,
                        StringComparison.OrdinalIgnoreCase
                    )
                );

            if (!safeHostGroup)
            {
                return groupRules
                    .Select(rule => Result(
                        rule,
                        null,
                        WindowRuleMatchStatus.ManualAssignmentRequired,
                        "Windows of this generic " +
                        "host process cannot safely " +
                        "be grouped as interchangeable " +
                        "slots."
                    ))
                    .ToArray();
            }
        }

        // Gleiche Fenster in stabiler Reihenfolge
        // innerhalb dieses einzelnen Abgleichs.
        //
        // Handle-Werte sind ausdrücklich KEINE
        // dauerhaften Fensteridentitäten.
        WindowCandidate[] availableCandidates =
            groupCandidates
                .Where(candidate =>
                    !genericHost ||
                    string.Equals(
                        candidate.Identity.WindowTitle,
                        groupRules[0].WindowTitle,
                        StringComparison.OrdinalIgnoreCase
                    )
                )
                .OrderBy(candidate =>
                    candidate.Identity.ProcessId
                )
                .ThenBy(candidate =>
                    candidate.Identity.Handle.ToInt64()
                )
                .ToArray();

        // Zuerst bereits bestehende, aktive
        // Zuordnungen reservieren.
        //
        // Damit werden Fenster nicht neu auf andere
        // Plätze verteilt, nur weil ein weiteres
        // gleichartiges Fenster erscheint.
        var activeAssignments =
            options?.ActiveAssignments;

        if (activeAssignments is not null)
        {
            foreach (SavedWindowRule rule in groupRules)
            {
                if (!activeAssignments.TryGetValue(
                    rule.RuleId,
                    out WindowInstanceKey previousKey))
                {
                    continue;
                }

                WindowCandidate? previousCandidate =
                    availableCandidates.FirstOrDefault(
                        candidate =>
                            candidate.Key == previousKey &&
                            !usedWindows.Contains(candidate.Key) &&
                            IsSlotCompatible(rule, candidate)
                    );

                if (previousCandidate is null)
                    continue;

                usedWindows.Add(previousCandidate.Key);

                results[rule.RuleId] = Result(
                    rule,
                    previousCandidate.Window,
                    WindowRuleMatchStatus.Matched,
                    "Existing window slot assignment " +
                    "was retained."
                );
            }
        }

        // Noch nicht belegte Plätze vergeben.
        //
        // Die Reihenfolge entspricht den gespeicherten
        // Regeln. Jede Fensterinstanz darf höchstens
        // einen Platz erhalten.
        foreach (SavedWindowRule rule in groupRules)
        {
            if (results.ContainsKey(rule.RuleId))
                continue;

            WindowCandidate? candidate =
                availableCandidates.FirstOrDefault(item =>
                    !usedWindows.Contains(item.Key) &&
                    IsSlotCompatible(rule, item)
                );

            if (candidate is null)
            {
                results[rule.RuleId] = Result(
                    rule,
                    null,
                    WindowRuleMatchStatus.NotFound,
                    "For this saved slot, " +
                    "no matching free " +
                    "window is currently available."
                );

                continue;
            }

            usedWindows.Add(candidate.Key);

            results[rule.RuleId] = Result(
                rule,
                candidate.Window,
                WindowRuleMatchStatus.Matched,
                "Window assigned to an available slot. " +
                "Its individual " +
                "identity is not guaranteed."
            );
        }

        return groupRules
            .Select(rule => results[rule.RuleId])
            .ToArray();
    }

    // --------------------------------------------------
    // Sicherheitsprüfungen
    // --------------------------------------------------

    private static bool IsSlotCompatible(
        SavedWindowRule rule,
        WindowCandidate candidate)
    {
        if (!SameApplication(
            rule.ProcessName,
            rule.WindowClass,
            candidate.Identity.ProcessName,
            candidate.Identity.WindowClass))
        {
            return false;
        }

        // Bei allgemeinen Hostprozessen muss
        // zusätzlich der gespeicherte Titel passen.
        if (IsGenericHostProcess(rule.ProcessName))
        {
            return string.Equals(
                rule.WindowTitle,
                candidate.Identity.WindowTitle,
                StringComparison.OrdinalIgnoreCase
            );
        }

        return true;
    }

    private static bool SameApplication(
        string processA,
        string classA,
        string processB,
        string classB)
    {
        return string.Equals(
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

    private static bool SameExecutablePath(
        string? pathA,
        string? pathB)
    {
        return
            !string.IsNullOrWhiteSpace(pathA) &&
            !string.IsNullOrWhiteSpace(pathB) &&
            string.Equals(
                pathA,
                pathB,
                StringComparison.OrdinalIgnoreCase
            );
    }

    private static bool IsGenericHostProcess(
        string processName)
    {
        return processName.Equals(
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

    private static WindowRuleMatchResult Result(
        SavedWindowRule rule,
        DiscoveredWindow? window,
        WindowRuleMatchStatus status,
        string description)
    {
        return new WindowRuleMatchResult(
            rule,
            window,
            status,
            description
        );
    }
}
