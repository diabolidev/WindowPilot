
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Windows.Automation;

namespace WindowPilot.App.Services;

public sealed class WindowStructureSignatureService
{
    private const int MaximumDepth = 10;
    private const int MaximumElements = 250;
    private const int MinimumMarkers = 3;

    // Mindestens 75 % der gespeicherten
    // charakteristischen Merkmale müssen
    // im aktuellen Fenster vorhanden sein.
    //
    // Zusätzliche UI-Elemente sind erlaubt.
    private const double MinimumSavedCoverage = 0.75;

    private static readonly HashSet<string>
        IgnoredSystemMarkers = new(
            StringComparer.Ordinal
        )
        {
            "50000|Close",
            "50000|Maximize",
            "50000|Minimize",
            "50010|SystemMenuBar",
            "50011|Item 1",
            "50037|TitleBar"
        };

    // --------------------------------------------------
    // Technische Fenstermerkmale auslesen
    // --------------------------------------------------

    public string[]? ReadMarkers(IntPtr windowHandle)
    {
        if (windowHandle == IntPtr.Zero)
        {
            Debug.WriteLine(
                "[WindowPilot/UIA] Invalid window handle."
            );

            return null;
        }

        var stopwatch = Stopwatch.StartNew();

        var markers = new HashSet<string>(
            StringComparer.Ordinal
        );

        int visited = 0;
        int errors = 0;

        string apartment =
            Thread.CurrentThread
                .GetApartmentState()
                .ToString();

        try
        {
            AutomationElement root =
                AutomationElement.FromHandle(windowHandle);

            if (root is null)
            {
                Debug.WriteLine(
                    $"[WindowPilot/UIA] Handle={windowHandle} | " +
                    $"Thread={apartment} | Root=null"
                );

                return null;
            }

            VisitElement(
                root,
                TreeWalker.ControlViewWalker,
                markers,
                0,
                ref visited,
                ref errors
            );

            HashSet<string> normalized =
                NormalizeMarkers(markers);

            string[] result = normalized
                .OrderBy(
                    marker => marker,
                    StringComparer.Ordinal
                )
                .ToArray();

            stopwatch.Stop();

            Debug.WriteLine(
                $"[WindowPilot/UIA] Handle={windowHandle} | " +
                $"Thread={apartment} | " +
                $"Elements={visited} | " +
                $"ReadErrors={errors} | " +
                $"Markers={result.Length} | " +
                $"Duration={stopwatch.ElapsedMilliseconds}ms"
            );

            Debug.WriteLine(
                "[WindowPilot/UIA] Detected: " +
                (result.Length == 0
                    ? "(none)"
                    : string.Join(", ", result))
            );

            if (result.Length < MinimumMarkers)
            {
                Debug.WriteLine(
                    "[WindowPilot/UIA] REJECTED: " +
                    "Insufficient structural markers."
                );

                return null;
            }

            return result;
        }
        catch (Exception ex)
        {
            stopwatch.Stop();

            Debug.WriteLine(
                $"[WindowPilot/UIA] ERROR | " +
                $"Handle={windowHandle} | " +
                $"Thread={apartment} | " +
                $"Dauer={stopwatch.ElapsedMilliseconds}ms | " +
                $"{ex.GetType().Name}: {ex.Message}"
            );

            return null;
        }
    }

    // --------------------------------------------------
    // UI-Automation-Baum durchlaufen
    // --------------------------------------------------

    private static void VisitElement(
        AutomationElement element,
        TreeWalker walker,
        HashSet<string> markers,
        int depth,
        ref int visited,
        ref int errors)
    {
        if (depth > MaximumDepth ||
            visited >= MaximumElements)
        {
            return;
        }

        visited++;

        try
        {
            var current = element.Current;

            string automationId =
                current.AutomationId;

            if (!string.IsNullOrWhiteSpace(automationId) &&
                automationId.Length <= 128)
            {
                int controlTypeId =
                    current.ControlType.Id;

                string marker =
                    controlTypeId.ToString(
                        CultureInfo.InvariantCulture
                    ) +
                    "|" +
                    automationId;

                if (!IgnoredSystemMarkers.Contains(marker))
                {
                    markers.Add(marker);
                }
            }
        }
        catch (Exception)
        {
            errors++;
        }

        AutomationElement? child;

        try
        {
            child = walker.GetFirstChild(element);
        }
        catch (Exception)
        {
            errors++;
            return;
        }

        while (child is not null &&
               visited < MaximumElements)
        {
            VisitElement(
                child,
                walker,
                markers,
                depth + 1,
                ref visited,
                ref errors
            );

            try
            {
                child = walker.GetNextSibling(child);
            }
            catch (Exception)
            {
                errors++;
                break;
            }
        }
    }

    // --------------------------------------------------
    // Merkmale normalisieren
    // --------------------------------------------------

    private static HashSet<string> NormalizeMarkers(
        IEnumerable<string>? markers)
    {
        var normalized = new HashSet<string>(
            StringComparer.Ordinal
        );

        if (markers is null)
            return normalized;

        foreach (string marker in markers)
        {
            if (string.IsNullOrWhiteSpace(marker))
                continue;

            if (IgnoredSystemMarkers.Contains(marker))
                continue;

            normalized.Add(marker);
        }

        return normalized;
    }

    // --------------------------------------------------
    // Fensterstrukturen vergleichen
    // --------------------------------------------------

    public static bool AreCompatible(
        IReadOnlyCollection<string>? savedMarkers,
        IReadOnlyCollection<string>? currentMarkers)
    {
        // Auch ältere gespeicherte Fingerabdrücke
        // werden automatisch normalisiert.
        HashSet<string> saved =
            NormalizeMarkers(savedMarkers);

        HashSet<string> current =
            NormalizeMarkers(currentMarkers);

        int sharedCount = saved.Count(
            marker => current.Contains(marker)
        );

        double savedCoverage =
            saved.Count > 0
                ? (double)sharedCount / saved.Count
                : 0;

        double currentCoverage =
            current.Count > 0
                ? (double)sharedCount / current.Count
                : 0;

        // Entscheidend ist der Anteil der
        // gespeicherten Merkmale, die wir
        // im aktuellen Fenster wiederfinden.
        //
        // Zusätzliche Elemente können beispielsweise
        // durch Menüs, Toolbars, Dokumentinhalte
        // oder dynamisch aufgebaute UI entstehen.
        //
        // Diese dürfen einen ansonsten passenden
        // Fenstertyp nicht automatisch ausschließen.
        bool compatible =
            saved.Count >= MinimumMarkers &&
            sharedCount >= MinimumMarkers &&
            savedCoverage >= MinimumSavedCoverage;

        Debug.WriteLine(
            "[WindowPilot/UIA] MATCH | " +
            $"Saved={saved.Count} | " +
            $"Current={current.Count} | " +
            $"Shared={sharedCount} | " +
            $"Coverage={savedCoverage:P0}/{currentCoverage:P0} | " +
            $"Compatible={compatible}"
        );

        if (!compatible)
        {
            string[] missing = saved
                .Except(current)
                .OrderBy(
                    marker => marker,
                    StringComparer.Ordinal
                )
                .ToArray();

            Debug.WriteLine(
                "[WindowPilot/UIA] Missing: " +
                (missing.Length == 0
                    ? "(none)"
                    : string.Join(", ", missing))
            );
        }

        return compatible;
    }
}
