
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace WindowPilot.App.Services;

public sealed record SavedRectangle(
    int X,
    int Y,
    int Width,
    int Height
);

public sealed record SavedWindowRule(
    Guid RuleId,
    string ProcessName,
    string? ExecutablePath,
    string WindowTitle,
    string WindowClass,
    string WindowState,
    string MonitorDeviceName,
    SavedRectangle MonitorWorkArea,
    SavedRectangle? OuterBoundsRelativeToWorkArea,
    SavedRectangle RestoredBoundsWorkspace,
    SavedRectangle? VisibleFrameBoundsPhysical,
    uint? WindowDpi,
    bool RequiresManualAssignment,
    string[]? WindowStructureMarkers = null,
    string? MonitorStableId = null
);

public sealed record SavedLayout(
    int SchemaVersion,
    string ProfileName,
    DateTimeOffset SavedAtUtc,
    SavedWindowRule[] Windows
);

public sealed record LayoutCaptureResult(
    string FilePath,
    int WindowCount,
    int AmbiguousWindowCount
);

public sealed class LayoutCaptureService
{
    private const uint MonitorDefaultToNearest = 2;

    private const uint SwShowMinimized = 2;
    private const uint SwShowMaximized = 3;
    private const uint SwMinimize = 6;
    private const uint SwShowMinNoActive = 7;
    private const uint SwForceMinimize = 11;

    private const int DwmExtendedFrameBounds = 9;

    private readonly WindowStructureSignatureService
        _structureService = new();

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

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

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeWindowPlacement
    {
        public uint Length;
        public uint Flags;
        public uint ShowCmd;
        public NativePoint MinPosition;
        public NativePoint MaxPosition;
        public NativeRect NormalPosition;
        public NativeRect Device;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(
        IntPtr hWnd,
        out uint processId
    );

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(
        IntPtr hWnd,
        out NativeRect rect
    );

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowPlacement(
        IntPtr hWnd,
        ref NativeWindowPlacement placement
    );

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(
        IntPtr hWnd,
        uint flags
    );

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(
        IntPtr hWnd
    );

    [DllImport(
        "user32.dll",
        EntryPoint = "GetClassNameW",
        CharSet = CharSet.Unicode)]
    private static extern int GetClassName(
        IntPtr hWnd,
        StringBuilder className,
        int maxCount
    );

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(
        IntPtr hWnd,
        int attribute,
        out NativeRect value,
        int valueSize
    );

    public LayoutCaptureResult CaptureAndSave(
        IReadOnlyList<DiscoveredWindow> selectedWindows,
        IReadOnlyList<DisplayMonitor> monitors)
    {
        ArgumentNullException.ThrowIfNull(selectedWindows);
        ArgumentNullException.ThrowIfNull(monitors);

        if (selectedWindows.Count == 0)
        {
            throw new InvalidOperationException(
                "No windows were selected to save."
            );
        }

        if (monitors.Count == 0)
        {
            throw new InvalidOperationException(
                "No monitors were detected."
            );
        }

        var capturedRules = selectedWindows
            .Select(window =>
                CaptureWindow(window, monitors))
            .ToArray();

        // Gleiche Prozesse, Klassen und Titel
        // können mehrere unterschiedliche Fenster
        // oder denselben Fenstertyp betreffen.
        //
        // Ein Struktur-Fingerabdruck unterscheidet
        // Fenstertypen, aber niemals zuverlässig
        // einzelne inhaltlich identische Fenster.
        var ambiguousIds = capturedRules
            .GroupBy(
                rule =>
                    rule.ProcessName + "\u001F" +
                    rule.WindowClass + "\u001F" +
                    rule.WindowTitle,
                StringComparer.OrdinalIgnoreCase
            )
            .Where(group => group.Count() > 1)
            .SelectMany(group =>
                group.Select(rule => rule.RuleId))
            .ToHashSet();

        var finalRules = capturedRules
            .Select(rule => rule with
            {
                RequiresManualAssignment =
                    ambiguousIds.Contains(rule.RuleId)
            })
            .ToArray();

        var layout = new SavedLayout(
            SchemaVersion: 1,
            ProfileName: "Standard",
            SavedAtUtc: DateTimeOffset.UtcNow,
            Windows: finalRules
        );

        string filePath = GetDefaultLayoutPath();

        SaveLayoutToFile(layout, filePath);

        return new LayoutCaptureResult(
            FilePath: filePath,
            WindowCount: finalRules.Length,
            AmbiguousWindowCount: ambiguousIds.Count
        );
    }

    private SavedWindowRule CaptureWindow(
        DiscoveredWindow window,
        IReadOnlyList<DisplayMonitor> monitors)
    {
        IntPtr handle = window.Handle;

        if (handle == IntPtr.Zero || !IsWindow(handle))
        {
            throw new InvalidOperationException(
                $"The window \"{window.Title}\" " +
                "no longer exists. " +
                "Please refresh the window list."
            );
        }

        GetWindowThreadProcessId(
            handle,
            out uint processId
        );

        if (processId == 0 ||
            processId != window.ProcessId)
        {
            throw new InvalidOperationException(
                $"The window \"{window.Title}\" " +
                "could no longer be identified unambiguously. " +
                "Please refresh the window list."
            );
        }

        var classBuffer = new StringBuilder(256);

        if (GetClassName(
            handle,
            classBuffer,
            classBuffer.Capacity) == 0 ||
            !string.Equals(
                classBuffer.ToString(),
                window.ClassName,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"The window class of \"{window.Title}\" " +
                "has changed. " +
                "Please refresh the window list."
            );
        }

        IntPtr monitorHandle = MonitorFromWindow(
            handle,
            MonitorDefaultToNearest
        );

        DisplayMonitor? monitor = monitors.FirstOrDefault(
            item => item.Handle == monitorHandle
        );

        if (monitor is null)
        {
            throw new InvalidOperationException(
                $"The target monitor for \"{window.Title}\" " +
                "could not be determined."
            );
        }

        var placement = new NativeWindowPlacement
        {
            Length =
                (uint)Marshal.SizeOf<NativeWindowPlacement>()
        };

        if (!GetWindowPlacement(handle, ref placement))
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                $"The window state of \"{window.Title}\" " +
                "could not be read."
            );
        }

        string state = placement.ShowCmd switch
        {
            SwShowMaximized => "Maximized",

            SwShowMinimized or
            SwMinimize or
            SwShowMinNoActive or
            SwForceMinimize => "Minimized",

            _ => "Normal"
        };

        SavedRectangle? relativeBounds = null;

        if (state == "Normal")
        {
            if (!GetWindowRect(handle, out NativeRect rect))
            {
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    "The window position could not be read."
                );
            }

            if (rect.Width <= 0 || rect.Height <= 0)
            {
                throw new InvalidOperationException(
                    $"The window \"{window.Title}\" " +
                    "has an invalid size."
                );
            }

            relativeBounds = new SavedRectangle(
                rect.Left - monitor.WorkX,
                rect.Top - monitor.WorkY,
                rect.Width,
                rect.Height
            );
        }

        NativeRect restoredRect =
            placement.NormalPosition;

        var restoredBounds = new SavedRectangle(
            restoredRect.Left,
            restoredRect.Top,
            restoredRect.Width,
            restoredRect.Height
        );

        SavedRectangle? visibleBounds = null;

        if (state != "Minimized")
        {
            int dwmResult = DwmGetWindowAttribute(
                handle,
                DwmExtendedFrameBounds,
                out NativeRect visibleRect,
                Marshal.SizeOf<NativeRect>()
            );

            if (dwmResult == 0 &&
                visibleRect.Width > 0 &&
                visibleRect.Height > 0)
            {
                visibleBounds = new SavedRectangle(
                    visibleRect.Left,
                    visibleRect.Top,
                    visibleRect.Width,
                    visibleRect.Height
                );
            }
        }

        // Universelle strukturelle Identifikation.
        //
        // Kein Sonderfall für Kurznotizen, OBS,
        // Discord oder sonstige Anwendungen.
        //
        // Falls eine Anwendung keine ausreichend
        // stabile UI-Automation-Struktur bereitstellt,
        // wird null gespeichert.
        string[]? structureMarkers =
            _structureService.ReadMarkers(handle);

        uint dpi = GetDpiForWindow(handle);

        return new SavedWindowRule(
            RuleId: Guid.NewGuid(),
            ProcessName: window.ProcessName,
            ExecutablePath: GetExecutablePath(processId),
            WindowTitle: window.Title,
            WindowClass: window.ClassName,
            WindowState: state,
            MonitorDeviceName: monitor.DeviceName,

            MonitorWorkArea: new SavedRectangle(
                monitor.WorkX,
                monitor.WorkY,
                monitor.WorkWidth,
                monitor.WorkHeight
            ),

            OuterBoundsRelativeToWorkArea: relativeBounds,
            RestoredBoundsWorkspace: restoredBounds,
            VisibleFrameBoundsPhysical: visibleBounds,
            WindowDpi: dpi > 0 ? dpi : null,

            RequiresManualAssignment: false,
            WindowStructureMarkers: structureMarkers,
            MonitorStableId: monitor.StableDeviceId
        );
    }

    private static string? GetExecutablePath(
        uint processId)
    {
        if (processId == 0 || processId > int.MaxValue)
            return null;

        try
        {
            using var process =
                Process.GetProcessById((int)processId);

            return process.MainModule?.FileName;
        }
        catch (Win32Exception)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
        catch (ArgumentException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
        catch (NotSupportedException)
        {
            return null;
        }
    }

    public static string GetDefaultLayoutPath()
    {
        string appData = Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData
        );

        return Path.Combine(
            appData,
            "WindowPilot",
            "layouts",
            "standard.json"
        );
    }

    private static void SaveLayoutToFile(
        SavedLayout layout,
        string filePath)
    {
        string? directory =
            Path.GetDirectoryName(filePath);

        if (string.IsNullOrWhiteSpace(directory))
        {
            throw new InvalidOperationException(
                "The layout storage location is invalid."
            );
        }

        Directory.CreateDirectory(directory);

        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy =
                JsonNamingPolicy.CamelCase
        };

        string json = JsonSerializer.Serialize(
            layout,
            options
        );

        string temporaryFile = Path.Combine(
            directory,
            $"layout-{Guid.NewGuid():N}.tmp"
        );

        try
        {
            File.WriteAllText(
                temporaryFile,
                json,
                new UTF8Encoding(false)
            );

            File.Move(
                temporaryFile,
                filePath,
                overwrite: true
            );
        }
        finally
        {
            if (File.Exists(temporaryFile))
            {
                File.Delete(temporaryFile);
            }
        }
    }
}
