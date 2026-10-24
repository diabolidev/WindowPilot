using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Drawing;

namespace WindowPilot.App.Services;

// Allgemeine Windows-Symbolerkennung, unabhängig vom Programm.
// Fenster-Icon -> Dateisymbol der EXE -> generisches Windows-Icon.
public static class WindowIconService
{
    private const uint WmGetIcon = 0x007F;
    private const uint IconSmall2 = 2;
    private const uint IconSmall = 0;
    private const uint IconBig = 1;
    private const uint SmtoAbortIfHung = 0x0002;
    private const int GclpHicon = -14;
    private const int GclpHiconSm = -34;
    private const uint ShgfiIcon = 0x000000100;
    private const uint ShgfiSmallIcon = 0x000000001;

    private static readonly Dictionary<uint, ImageSource> ProcessIcons = new();
    private static readonly ImageSource DefaultIcon = ConvertHandle(SystemIcons.Application.Handle);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShellFileInfo
    {
        public IntPtr IconHandle;
        public int IconIndex;
        public uint Attributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string DisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
        public string TypeName;
    }

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr SendMessageTimeout(
        IntPtr hwnd, uint message, UIntPtr wParam, IntPtr lParam,
        uint flags, uint timeoutMs, out IntPtr result);

    [DllImport("user32.dll", EntryPoint = "GetClassLongPtrW", SetLastError = true)]
    private static extern IntPtr GetClassLongPtr64(IntPtr hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "GetClassLongW", SetLastError = true)]
    private static extern uint GetClassLong32(IntPtr hwnd, int index);

    [DllImport("shell32.dll", EntryPoint = "SHGetFileInfoW", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(
        string path, uint fileAttributes, ref ShellFileInfo info,
        uint infoSize, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr icon);

    public static ImageSource GetIcon(DiscoveredWindow window)
    {
        try
        {
            // Kleine Fenstersymbole haben Vorrang; so wird bei
            // Anwendungen mit mehreren Fensterarten nicht blind
            // das Icon des Hostprozesses verwendet.
            foreach (uint size in new[] { IconSmall2, IconSmall, IconBig })
            {
                if (SendMessageTimeout(window.Handle, WmGetIcon, new UIntPtr(size),
                        IntPtr.Zero, SmtoAbortIfHung, 80, out IntPtr handle) != IntPtr.Zero &&
                    handle != IntPtr.Zero)
                {
                    return ConvertHandle(handle);
                }
            }

            foreach (int index in new[] { GclpHiconSm, GclpHicon })
            {
                IntPtr classIcon = IntPtr.Size == 8
                    ? GetClassLongPtr64(window.Handle, index)
                    : new IntPtr(unchecked((int)GetClassLong32(window.Handle, index)));

                if (classIcon != IntPtr.Zero)
                    return ConvertHandle(classIcon);
            }

            if (ProcessIcons.TryGetValue(window.ProcessId, out ImageSource? cached))
                return cached;

            if (window.ProcessId <= int.MaxValue)
            {
                using Process process = Process.GetProcessById((int)window.ProcessId);
                string? path = process.MainModule?.FileName;
                if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
                {
                    var info = new ShellFileInfo();
                    IntPtr ok = SHGetFileInfo(path, 0, ref info,
                        (uint)Marshal.SizeOf<ShellFileInfo>(), ShgfiIcon | ShgfiSmallIcon);

                    if (ok != IntPtr.Zero && info.IconHandle != IntPtr.Zero)
                    {
                        try
                        {
                            ImageSource icon = ConvertHandle(info.IconHandle);
                            if (ProcessIcons.Count > 256) ProcessIcons.Clear();
                            ProcessIcons[window.ProcessId] = icon;
                            return icon;
                        }
                        finally
                        {
                            DestroyIcon(info.IconHandle);
                        }
                    }
                }
            }
        }
        catch (Exception ex) when (
            ex is System.ComponentModel.Win32Exception or
            UnauthorizedAccessException or InvalidOperationException or
            ArgumentException or NotSupportedException or IOException or
            ExternalException)
        {
            // Geschützte Prozesse oder Fenster ohne eigene Icons
            // erhalten das neutrale Windows-Symbol.
        }

        return DefaultIcon;
    }

    private static ImageSource ConvertHandle(IntPtr hIcon)
    {
        BitmapSource bitmap = Imaging.CreateBitmapSourceFromHIcon(
            hIcon, System.Windows.Int32Rect.Empty, BitmapSizeOptions.FromWidthAndHeight(32, 32));
        bitmap.Freeze();
        return bitmap;
    }
}
