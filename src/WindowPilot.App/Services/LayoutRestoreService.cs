
using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace WindowPilot.App.Services;

public sealed class LayoutRestoreService
{
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_NOOWNERZORDER = 0x0200;

    private const int SW_RESTORE = 9;

    private const uint PositionFlags =
        SWP_NOZORDER |
        SWP_NOACTIVATE |
        SWP_NOOWNERZORDER;

    private readonly MonitorService _monitorService = new();
    private readonly WindowPositioningService
        _positioningService = new();

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
    private static extern bool IsWindow(IntPtr handle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr handle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsZoomed(IntPtr handle);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(
        IntPtr handle,
        out uint processId
    );

    [DllImport(
        "user32.dll",
        EntryPoint = "GetClassNameW",
        CharSet = CharSet.Unicode)]
    private static extern int GetClassName(
        IntPtr handle,
        StringBuilder buffer,
        int maxCount
    );

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(
        IntPtr handle,
        out NativeRect rect
    );

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(
        IntPtr handle,
        IntPtr insertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags
    );

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(
        IntPtr handle,
        int command
    );

    // ---------------------------------------------
    // Gespeichertes Layout laden
    // ---------------------------------------------

    public SavedLayout LoadStandardLayout()
    {
        string path =
            LayoutCaptureService.GetDefaultLayoutPath();

        if (!File.Exists(path))
        {
            throw new FileNotFoundException(
                "No default layout has been saved yet.",
                path
            );
        }

        string json = File.ReadAllText(path);

        SavedLayout? layout;

        try
        {
            layout = JsonSerializer.Deserialize<SavedLayout>(
                json,
                new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                }
            );
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException(
                "The saved layout file " +
                "contains invalid JSON data.",
                ex
            );
        }

        if (layout is null)
        {
            throw new InvalidDataException(
                "The saved layout file is empty."
            );
        }

        if (layout.SchemaVersion != 1)
        {
            throw new InvalidDataException(
                "This layout version is " +
                "not supported."
            );
        }

        if (layout.Windows is null ||
            layout.Windows.Length == 0)
        {
            throw new InvalidDataException(
                "The layout contains no window rules."
            );
        }

        if (layout.Windows.Any(rule =>
            rule is null ||
            rule.RuleId == Guid.Empty ||
            string.IsNullOrWhiteSpace(rule.ProcessName) ||
            string.IsNullOrWhiteSpace(rule.WindowClass) ||
            string.IsNullOrWhiteSpace(rule.MonitorDeviceName)))
        {
            throw new InvalidDataException(
                "The layout contains incomplete " +
                "or invalid window rules."
            );
        }

        if (layout.Windows
            .Select(rule => rule.RuleId)
            .Distinct()
            .Count() != layout.Windows.Length)
        {
            throw new InvalidDataException(
                "The layout contains duplicate rule IDs."
            );
        }

        return layout;
    }

    // ---------------------------------------------
    // Einzelnes Fenster ausdrücklich wiederherstellen
    // ---------------------------------------------

    public void RestoreSelectedWindow(
        IntPtr windowHandle,
        Guid ruleId)
    {
        SavedLayout layout = LoadStandardLayout();

        SavedWindowRule? rule = layout.Windows
            .FirstOrDefault(item => item.RuleId == ruleId);

        if (rule is null)
        {
            throw new InvalidOperationException(
                "The selected window rule " +
                "no longer exists."
            );
        }

        ValidateWindow(windowHandle, rule);

        // Monitore immer aktuell aus Windows auslesen.
        // Keine veralteten Monitorhandles verwenden.
        var monitors = _monitorService.GetMonitors();

        DisplayMonitor? targetMonitor =
            MonitorIdentityService.ResolveTarget(rule, monitors);

        if (targetMonitor is null)
        {
            throw new InvalidOperationException(
                "The saved target monitor could not be " +
                "identified unambiguously. Check your monitor arrangement " +
                "and save the layout again if needed."
            );
        }

        // In diesem ersten Wiederherstellungstest
        // muss der Arbeitsbereich noch dieselbe
        // Größe besitzen wie beim Speichern.
        //
        // Anpassungen an veränderte Monitorlayouts
        // ergänzen wir später.
        if (rule.MonitorWorkArea is null ||
            targetMonitor.WorkWidth !=
                rule.MonitorWorkArea.Width ||
            targetMonitor.WorkHeight !=
                rule.MonitorWorkArea.Height)
        {
            throw new InvalidOperationException(
                "The monitor work area size " +
                "has changed since the layout was saved. " +
                "Automatic adjustment is " +
                "not implemented yet."
            );
        }

        switch (rule.WindowState)
        {
            case "Normal":
                RestoreNormalWindow(
                    windowHandle,
                    rule,
                    targetMonitor
                );
                break;

            case "Maximized":
                _positioningService.MaximizeOnMonitor(
                    windowHandle,
                    targetMonitor
                );
                break;

            case "Minimized":
                throw new NotSupportedException(
                    "Restoring a window saved as " +
                    "minimized " +
                    "is not implemented yet."
                );

            default:
                throw new InvalidDataException(
                    $"Unknown window state: " +
                    rule.WindowState
                );
        }
    }

    // ---------------------------------------------
    // Fensteridentität überprüfen
    // ---------------------------------------------

    private static void ValidateWindow(
        IntPtr handle,
        SavedWindowRule rule)
    {
        if (handle == IntPtr.Zero || !IsWindow(handle))
        {
            throw new InvalidOperationException(
                "The selected window no longer exists."
            );
        }

        GetWindowThreadProcessId(
            handle,
            out uint processId
        );

        if (processId == 0 || processId > int.MaxValue)
        {
            throw new InvalidOperationException(
                "The window process could not be " +
                "determined."
            );
        }

        string processName;

        try
        {
            using var process = Process.GetProcessById(
                (int)processId
            );

            processName = process.ProcessName;
        }
        catch (Exception ex) when (
            ex is ArgumentException ||
            ex is InvalidOperationException ||
            ex is Win32Exception)
        {
            throw new InvalidOperationException(
                "The window process is no " +
                "longer available.",
                ex
            );
        }

        if (!string.Equals(
            processName,
            rule.ProcessName,
            StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "The selected window does " +
                "not belong to the saved application."
            );
        }

        var classBuffer = new StringBuilder(256);

        if (GetClassName(
            handle,
            classBuffer,
            classBuffer.Capacity) == 0)
        {
            throw new InvalidOperationException(
                "The window class could " +
                "not be determined."
            );
        }

        if (!string.Equals(
            classBuffer.ToString(),
            rule.WindowClass,
            StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The window class does not " +
                "match the saved rule."
            );
        }

        // Fenstertitel werden bei der manuellen
        // Wiederherstellung nicht strikt verglichen.
        //
        // Beispielsweise ändert Discord seinen
        // Fenstertitel während der Benutzung.
        //
        // Bei identischen Kurznotizen muss der
        // Benutzer ausdrücklich auswählen,
        // welches Fenster welcher Regel entspricht.
    }

    // ---------------------------------------------
    // Normales Fenster wiederherstellen
    // ---------------------------------------------

    private static void RestoreNormalWindow(
        IntPtr handle,
        SavedWindowRule rule,
        DisplayMonitor monitor)
    {
        SavedRectangle? bounds =
            rule.OuterBoundsRelativeToWorkArea;

        if (bounds is null)
        {
            throw new InvalidDataException(
                "The saved window rule " +
                "does not contain normal window geometry."
            );
        }

        if (bounds.Width <= 0 || bounds.Height <= 0)
        {
            throw new InvalidDataException(
                "The saved window size is invalid."
            );
        }

        // Position relativ zum aktuellen
        // Arbeitsbereich des Zielmonitors berechnen.
        //
        // Negative relative Koordinaten sind erlaubt,
        // da Windows unsichtbare Fensterrahmen besitzt.
        long targetX =
            (long)monitor.WorkX + bounds.X;

        long targetY =
            (long)monitor.WorkY + bounds.Y;

        if (targetX < int.MinValue ||
            targetX > int.MaxValue ||
            targetY < int.MinValue ||
            targetY > int.MaxValue)
        {
            throw new InvalidDataException(
                "The saved window coordinates " +
                "are outside the valid range."
            );
        }

        // Ein minimiertes oder maximiertes Fenster
        // vor der normalen Positionierung
        // wiederherstellen.
        if (IsIconic(handle) || IsZoomed(handle))
        {
            ShowWindow(handle, SW_RESTORE);

            if (IsIconic(handle) || IsZoomed(handle))
            {
                throw new InvalidOperationException(
                    "The window could not be restored " +
                    "to the normal state."
                );
            }
        }

        if (!SetWindowPos(
            handle,
            IntPtr.Zero,
            (int)targetX,
            (int)targetY,
            bounds.Width,
            bounds.Height,
            PositionFlags))
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                "The saved window position " +
                "could not be applied."
            );
        }

        // Ergebnis einmalig überprüfen.
        // Keine permanente Korrekturschleife.
        if (!GetWindowRect(handle, out NativeRect actual))
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                "The restored window position " +
                "could not be verified."
            );
        }

        const int tolerance = 2;

        bool matches =
            Math.Abs((long)actual.Left - targetX) <= tolerance &&
            Math.Abs((long)actual.Top - targetY) <= tolerance &&
            Math.Abs((long)actual.Width - bounds.Width) <= tolerance &&
            Math.Abs((long)actual.Height - bounds.Height) <= tolerance;

        if (!matches)
        {
            throw new InvalidOperationException(
                "Windows did not fully apply " +
                "the saved window geometry " +
                "as requested.\n\n" +
                $"Expected: {targetX}, {targetY} | " +
                $"{bounds.Width} x {bounds.Height}\n" +
                $"Actual: {actual.Left}, " +
                $"{actual.Top} | " +
                $"{actual.Width} x {actual.Height}"
            );
        }
    }
}
