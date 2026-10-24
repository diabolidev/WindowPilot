
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace WindowPilot.App.Services;

public sealed record DiscoveredWindow(
    IntPtr Handle,
    string Title,
    string ClassName,
    uint ProcessId,
    string ProcessName,
    string WindowState,
    string CloakStatus,
    string OwnerStatus,
    string BoundsDisplay
)
{
    public string HandleDisplay => $"0x{Handle.ToInt64():X}";
}

public sealed class WindowDiscoveryService
{
    private const int DWMWA_CLOAKED = 14;
    private const uint GW_OWNER = 4;

    private const uint SW_SHOWMINIMIZED = 2;
    private const uint SW_SHOWMAXIMIZED = 3;
    private const uint SW_MINIMIZE = 6;
    private const uint SW_SHOWMINNOACTIVE = 7;
    private const uint SW_FORCEMINIMIZE = 11;

    private delegate bool EnumWindowsProc(
        IntPtr hWnd,
        IntPtr lParam
    );

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

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(
        EnumWindowsProc callback,
        IntPtr lParam
    );

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll", EntryPoint = "GetWindowTextLengthW")]
    private static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport(
        "user32.dll",
        EntryPoint = "GetWindowTextW",
        CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(
        IntPtr hWnd,
        StringBuilder text,
        int maxCount
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

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(
        IntPtr hWnd,
        out uint processId
    );

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(
        IntPtr hWnd,
        uint command
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

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(
        IntPtr hWnd,
        int attribute,
        out int value,
        int valueSize
    );

    public IReadOnlyList<DiscoveredWindow> GetOpenWindows()
    {
        var windows = new List<DiscoveredWindow>();

        EnumWindowsProc callback = (handle, _) =>
        {
            if (!IsWindowVisible(handle))
                return true;

            int titleLength = GetWindowTextLength(handle);

            if (titleLength <= 0)
                return true;

            var titleBuffer = new StringBuilder(
                Math.Min(titleLength + 1, 4096)
            );

            if (GetWindowText(
                handle,
                titleBuffer,
                titleBuffer.Capacity) == 0)
            {
                return true;
            }

            var classBuffer = new StringBuilder(256);

            GetClassName(
                handle,
                classBuffer,
                classBuffer.Capacity
            );

            string className = classBuffer.ToString();

            // Internes Desktopfenster ignorieren
            if (string.Equals(
                className,
                "Progman",
                StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            // DWM-verhüllte Fenster ignorieren
            bool cloakReadSuccessful = TryGetCloakState(
                handle,
                out int cloakValue
            );

            if (cloakReadSuccessful && cloakValue != 0)
                return true;

            string cloakStatus = cloakReadSuccessful
                ? "No"
                : "Unknown";

            GetWindowThreadProcessId(
                handle,
                out uint processId
            );

            windows.Add(new DiscoveredWindow(
                handle,
                titleBuffer.ToString(),
                className,
                processId,
                GetProcessName(processId),
                GetWindowState(handle),
                cloakStatus,
                GetOwnerStatus(handle),
                GetBoundsDisplay(handle)
            ));

            return true;
        };

        if (!EnumWindows(callback, IntPtr.Zero))
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                "The windows could not be enumerated."
            );
        }

        // Nur eindeutig zugeordnete WebView2-Hilfsfenster
        // aus der Benutzerübersicht entfernen.
        return windows
            .Where(window => !IsEmbeddedWebView(window, windows))
            .OrderBy(window => window.ProcessName)
            .ThenBy(window => window.Title)
            .ToArray();
    }

    private static bool IsEmbeddedWebView(
        DiscoveredWindow candidate,
        IReadOnlyList<DiscoveredWindow> windows)
    {
        // Nur bekannte WebView2-Fenster berücksichtigen.
        if (!string.Equals(
            candidate.ProcessName,
            "msedgewebview2",
            StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!string.Equals(
            candidate.ClassName,
            "Chrome_WidgetWin_1",
            StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (candidate.WindowState != "Normal")
            return false;

        if (!GetWindowRect(
            candidate.Handle,
            out NativeRect childRect))
        {
            return false;
        }

        foreach (var host in windows)
        {
            if (host.Handle == candidate.Handle)
                continue;

            // Ein bekanntes WinUI-Hostfenster suchen.
            if (!string.Equals(
                host.ClassName,
                "WinUIDesktopWin32WindowClass",
                StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (host.WindowState != "Normal")
                continue;

            // Fenstertitel müssen identisch sein.
            if (!string.Equals(
                candidate.Title,
                host.Title,
                StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!GetWindowRect(
                host.Handle,
                out NativeRect hostRect))
            {
                continue;
            }

            if (IsContainedWithin(
                childRect,
                hostRect))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsContainedWithin(
        NativeRect child,
        NativeRect host)
    {
        const int maxInset = 32;

        int childWidth = child.Right - child.Left;
        int childHeight = child.Bottom - child.Top;
        int hostWidth = host.Right - host.Left;
        int hostHeight = host.Bottom - host.Top;

        if (childWidth <= 0 || childHeight <= 0)
            return false;

        if (hostWidth <= childWidth ||
            hostHeight <= childHeight)
        {
            return false;
        }

        int leftInset = child.Left - host.Left;
        int topInset = child.Top - host.Top;
        int rightInset = host.Right - child.Right;
        int bottomInset = host.Bottom - child.Bottom;

        // Das WebView-Fenster muss vollständig innerhalb
        // des Hosts liegen, mit kleinen Rahmenabständen.
        return leftInset >= 0 &&
               topInset >= 0 &&
               rightInset >= 0 &&
               bottomInset >= 0 &&
               leftInset <= maxInset &&
               topInset <= maxInset &&
               rightInset <= maxInset &&
               bottomInset <= maxInset;
    }

    private static bool TryGetCloakState(
        IntPtr handle,
        out int cloakValue)
    {
        return DwmGetWindowAttribute(
            handle,
            DWMWA_CLOAKED,
            out cloakValue,
            sizeof(int)
        ) == 0;
    }

    private static string GetWindowState(IntPtr handle)
    {
        var placement = new NativeWindowPlacement
        {
            Length = (uint)Marshal.SizeOf<NativeWindowPlacement>()
        };

        if (!GetWindowPlacement(handle, ref placement))
            return "Unknown";

        return placement.ShowCmd switch
        {
            SW_SHOWMINIMIZED => "Minimiert",
            SW_MINIMIZE => "Minimiert",
            SW_SHOWMINNOACTIVE => "Minimiert",
            SW_FORCEMINIMIZE => "Minimiert",
            SW_SHOWMAXIMIZED => "Maximiert",
            _ => "Normal"
        };
    }

    private static string GetOwnerStatus(IntPtr handle)
    {
        IntPtr owner = GetWindow(handle, GW_OWNER);

        return owner == IntPtr.Zero ? "No" : "Yes";
    }

    private static string GetBoundsDisplay(IntPtr handle)
    {
        if (!GetWindowRect(handle, out NativeRect rect))
            return "Unknown";

        int width = Math.Max(0, rect.Right - rect.Left);
        int height = Math.Max(0, rect.Bottom - rect.Top);

        return $"{rect.Left}, {rect.Top} | {width} x {height}";
    }

    private static string GetProcessName(uint processId)
    {
        if (processId == 0 || processId > int.MaxValue)
            return "Unknown";

        try
        {
            using var process =
                Process.GetProcessById((int)processId);

            return process.ProcessName;
        }
        catch (ArgumentException)
        {
            return "Unknown";
        }
        catch (InvalidOperationException)
        {
            return "Unknown";
        }
        catch (Win32Exception)
        {
            return "Access restricted";
        }
    }
}
