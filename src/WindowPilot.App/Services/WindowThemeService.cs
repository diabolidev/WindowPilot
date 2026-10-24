using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace WindowPilot.App.Services;

public static class WindowThemeService
{
    private const int DwmUseImmersiveDarkMode = 20;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(
        IntPtr hwnd, int attribute, ref int value, int size);

    public static void ApplyDarkTitleBar(Window window)
    {
        try
        {
            IntPtr hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero)
                return;

            int enabled = 1;
            _ = DwmSetWindowAttribute(hwnd, DwmUseImmersiveDarkMode,
                ref enabled, sizeof(int));
        }
        catch (DllNotFoundException) { }
        catch (EntryPointNotFoundException) { }
    }
}
