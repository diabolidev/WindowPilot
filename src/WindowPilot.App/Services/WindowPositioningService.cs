
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace WindowPilot.App.Services;

public sealed class WindowPositioningService
{
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_NOOWNERZORDER = 0x0200;

    private const int SW_MAXIMIZE = 3;
    private const int SW_RESTORE = 9;

    private const uint MONITOR_DEFAULTTONEAREST = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsZoomed(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(
        IntPtr hWnd,
        out NativeRect rect
    );

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(
        IntPtr hWnd,
        IntPtr hWndInsertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags
    );

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(
        IntPtr hWnd,
        int command
    );

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(
        IntPtr hWnd,
        uint flags
    );

    // ---------------------------------------------
    // Validierung
    // ---------------------------------------------

    private static void ValidateWindow(IntPtr handle)
    {
        if (handle == IntPtr.Zero || !IsWindow(handle))
        {
            throw new InvalidOperationException(
                "The selected window no longer exists."
            );
        }
    }

    private static void ValidateMonitor(DisplayMonitor monitor)
    {
        ArgumentNullException.ThrowIfNull(monitor);

        if (monitor.Handle == IntPtr.Zero ||
            monitor.WorkWidth <= 0 ||
            monitor.WorkHeight <= 0)
        {
            throw new InvalidOperationException(
                "The target monitor has no valid work area."
            );
        }
    }

    // ---------------------------------------------
    // Minimierte oder maximierte Fenster wiederherstellen
    // ---------------------------------------------

    private static void RestoreWindow(IntPtr handle)
    {
        ValidateWindow(handle);

        if (!IsIconic(handle) && !IsZoomed(handle))
            return;

        // Echte Wiederherstellung über die Windows-API.
        // Der Rückgabewert von ShowWindow beschreibt
        // den vorherigen Sichtbarkeitszustand.
        ShowWindow(handle, SW_RESTORE);

        if (IsIconic(handle))
        {
            throw new InvalidOperationException(
                "Windows could not restore the minimized window."
            );
        }

        if (IsZoomed(handle))
        {
            throw new InvalidOperationException(
                "Windows could not restore the maximized window."
            );
        }
    }

    // ---------------------------------------------
    // Normales Fenster verschieben
    // ---------------------------------------------

    public void MoveToMonitor(
        IntPtr windowHandle,
        DisplayMonitor targetMonitor)
    {
        ValidateWindow(windowHandle);
        ValidateMonitor(targetMonitor);

        if (IsIconic(windowHandle) || IsZoomed(windowHandle))
        {
            throw new InvalidOperationException(
                "Before moving the window normally, it " +
                "must first be restored."
            );
        }

        MoveNormalWindow(windowHandle, targetMonitor);
    }

    // ---------------------------------------------
    // Interne Fensterpositionierung
    // ---------------------------------------------

    private static void MoveNormalWindow(
        IntPtr windowHandle,
        DisplayMonitor targetMonitor)
    {
        if (!GetWindowRect(windowHandle, out NativeRect rect))
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                "The window geometry could not be read."
            );
        }

        int currentWidth = rect.Right - rect.Left;
        int currentHeight = rect.Bottom - rect.Top;

        if (currentWidth <= 0 || currentHeight <= 0)
        {
            throw new InvalidOperationException(
                "The window has an invalid size."
            );
        }

        // Sicherheitsabstand nur für den manuellen Test.
        // Bei späteren Snap-Funktionen entfällt dieser.
        const int margin = 40;

        int marginX = Math.Min(
            margin,
            targetMonitor.WorkWidth / 4
        );

        int marginY = Math.Min(
            margin,
            targetMonitor.WorkHeight / 4
        );

        int availableWidth =
            targetMonitor.WorkWidth - marginX * 2;

        int availableHeight =
            targetMonitor.WorkHeight - marginY * 2;

        int targetWidth = Math.Min(
            currentWidth,
            availableWidth
        );

        int targetHeight = Math.Min(
            currentHeight,
            availableHeight
        );

        int targetX = targetMonitor.WorkX + marginX;
        int targetY = targetMonitor.WorkY + marginY;

        uint flags =
            SWP_NOZORDER |
            SWP_NOACTIVATE |
            SWP_NOOWNERZORDER;

        if (!SetWindowPos(
            windowHandle,
            IntPtr.Zero,
            targetX,
            targetY,
            targetWidth,
            targetHeight,
            flags))
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                "The window could not be moved."
            );
        }
    }

    // ---------------------------------------------
    // Echte Windows-Maximierung auf einem Zielmonitor
    // ---------------------------------------------

    public void MaximizeOnMonitor(
        IntPtr windowHandle,
        DisplayMonitor targetMonitor)
    {
        ValidateWindow(windowHandle);
        ValidateMonitor(targetMonitor);

        // Tatsächlichen aktuellen Zustand abfragen.
        // Keine Abhängigkeit von der Fensterübersicht.
        bool isMinimized = IsIconic(windowHandle);
        bool isMaximized = IsZoomed(windowHandle);

        IntPtr currentMonitor = MonitorFromWindow(
            windowHandle,
            MONITOR_DEFAULTTONEAREST
        );

        // Bereits richtig maximiert:
        // Fenster unverändert lassen.
        if (!isMinimized &&
            isMaximized &&
            currentMonitor == targetMonitor.Handle)
        {
            return;
        }

        // Auch minimierte Fenster werden verarbeitet!
        //
        // Normal     -> Positionieren -> Maximieren
        // Minimiert  -> Wiederherstellen -> Positionieren -> Maximieren
        // Maximiert  -> Wiederherstellen -> Positionieren -> Maximieren
        RestoreWindow(windowHandle);

        // Fenster zuerst normal auf den Zielmonitor setzen.
        MoveNormalWindow(
            windowHandle,
            targetMonitor
        );

        // Zielmonitor prüfen.
        IntPtr positionedMonitor = MonitorFromWindow(
            windowHandle,
            MONITOR_DEFAULTTONEAREST
        );

        if (positionedMonitor != targetMonitor.Handle)
        {
            throw new InvalidOperationException(
                "The window could not be moved to the " +
                "requested monitor."
            );
        }

        // Tatsächlichen Maximierungszustand auslösen.
        // Keine Tastatur- oder Maussimulation.
        ShowWindow(
            windowHandle,
            SW_MAXIMIZE
        );

        if (!IsZoomed(windowHandle))
        {
            throw new InvalidOperationException(
                "Windows did not maximize the window."
            );
        }

        // Abschließende Überprüfung.
        IntPtr maximizedMonitor = MonitorFromWindow(
            windowHandle,
            MONITOR_DEFAULTTONEAREST
        );

        if (maximizedMonitor != targetMonitor.Handle)
        {
            throw new InvalidOperationException(
                "The window was maximized but is " +
                "on the wrong monitor."
            );
        }
    }
}
