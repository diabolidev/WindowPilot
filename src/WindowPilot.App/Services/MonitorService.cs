
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace WindowPilot.App.Services;

public sealed record DisplayMonitor(
    IntPtr Handle,
    string DeviceName,
    bool IsPrimary,
    int X,
    int Y,
    int Width,
    int Height,
    int WorkX,
    int WorkY,
    int WorkWidth,
    int WorkHeight,
    string? StableDeviceId = null
)
{
    public string Resolution => $"{Width} x {Height}";

    public string Position => $"{X}, {Y}";

    public string WorkArea =>
        $"{WorkX}, {WorkY} | {WorkWidth} x {WorkHeight}";

    public string Type => IsPrimary
        ? "Primary"
        : "Secondary";
}

public sealed class MonitorService
{
    private const uint MONITORINFOF_PRIMARY = 0x00000001;

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(
        LayoutKind.Sequential,
        CharSet = CharSet.Unicode)]
    private struct NativeMonitorInfoEx
    {
        public uint Size;
        public NativeRect Monitor;
        public NativeRect WorkArea;
        public uint Flags;

        [MarshalAs(
            UnmanagedType.ByValTStr,
            SizeConst = 32)]
        public string DeviceName;
    }

    // Windows bezeichnet Ausgänge je nach Start ggf. neu als DISPLAY1,
    // DISPLAY2 usw. Die Monitor-Geräteschnittstelle identifiziert
    // nach Möglichkeit stattdessen den angeschlossenen Bildschirm.
    private const uint EddGetDeviceInterfaceName = 0x00000001;
    private const uint DisplayDeviceActive = 0x00000001;
    private const uint DisplayDeviceMirroringDriver = 0x00000008;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NativeDisplayDevice
    {
        public int Size;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DeviceString;
        public uint StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DeviceID;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DeviceKey;
    }

    [DllImport("user32.dll", EntryPoint = "EnumDisplayDevicesW",
        CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplayDevices(
        string displayDeviceName, uint deviceNumber,
        ref NativeDisplayDevice displayDevice, uint flags);

    private static string? FindStableMonitorDeviceId(string displayDeviceName)
    {
        try
        {
            for (uint i = 0; i < 16; i++)
            {
                var device = new NativeDisplayDevice
                {
                    Size = Marshal.SizeOf<NativeDisplayDevice>(),
                    DeviceName = string.Empty,
                    DeviceString = string.Empty,
                    DeviceID = string.Empty,
                    DeviceKey = string.Empty
                };
                if (!EnumDisplayDevices(displayDeviceName, i,
                    ref device, EddGetDeviceInterfaceName))
                    break;

                if ((device.StateFlags & DisplayDeviceActive) != 0 &&
                    (device.StateFlags & DisplayDeviceMirroringDriver) == 0 &&
                    !string.IsNullOrWhiteSpace(device.DeviceID))
                    return device.DeviceID.Trim();
            }
        }
        catch (Exception)
        {
            // Nicht jede virtuelle/RDP-Konfiguration liefert eine ID.
        }

        return null;
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate bool MonitorEnumProc(
        IntPtr monitor,
        IntPtr deviceContext,
        IntPtr rectangle,
        IntPtr data
    );

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplayMonitors(
        IntPtr deviceContext,
        IntPtr clippingRectangle,
        MonitorEnumProc callback,
        IntPtr data
    );

    [DllImport(
        "user32.dll",
        EntryPoint = "GetMonitorInfoW",
        SetLastError = true,
        CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(
        IntPtr monitor,
        ref NativeMonitorInfoEx info
    );

    public IReadOnlyList<DisplayMonitor> GetMonitors()
    {
        var monitors = new List<DisplayMonitor>();

        Exception? callbackError = null;

        MonitorEnumProc callback = (
            monitorHandle,
            deviceContext,
            rectangle,
            data) =>
        {
            var info = new NativeMonitorInfoEx
            {
                Size = (uint)Marshal.SizeOf<NativeMonitorInfoEx>(),
                DeviceName = string.Empty
            };

            if (!GetMonitorInfo(monitorHandle, ref info))
            {
                callbackError = new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    "Monitorinformationen konnten " +
                    "could not be read."
                );

                return false;
            }

            int width =
                info.Monitor.Right - info.Monitor.Left;

            int height =
                info.Monitor.Bottom - info.Monitor.Top;

            int workWidth =
                info.WorkArea.Right - info.WorkArea.Left;

            int workHeight =
                info.WorkArea.Bottom - info.WorkArea.Top;

            monitors.Add(new DisplayMonitor(
                monitorHandle,
                info.DeviceName ?? string.Empty,
                (info.Flags & MONITORINFOF_PRIMARY) != 0,
                info.Monitor.Left,
                info.Monitor.Top,
                width,
                height,
                info.WorkArea.Left,
                info.WorkArea.Top,
                workWidth,
                workHeight,
                FindStableMonitorDeviceId(info.DeviceName ?? string.Empty)
            ));

            return true;
        };

        bool success = EnumDisplayMonitors(
            IntPtr.Zero,
            IntPtr.Zero,
            callback,
            IntPtr.Zero
        );

        int errorCode = success
            ? 0
            : Marshal.GetLastWin32Error();

        GC.KeepAlive(callback);

        if (callbackError is not null)
            throw callbackError;

        if (!success)
        {
            throw new Win32Exception(
                errorCode,
                "The monitors could not be " +
                "enumerated."
            );
        }

        // Gerätekennungen können bei geklonten/virtuellen Bildschirmen
        // mehrfach vorkommen. Solche IDs niemals als eindeutig speichern.
        for (int i = 0; i < monitors.Count; i++)
        {
            string? id = monitors[i].StableDeviceId;
            if (!string.IsNullOrWhiteSpace(id) &&
                monitors.Count(m => string.Equals(m.StableDeviceId, id,
                    StringComparison.OrdinalIgnoreCase)) != 1)
            {
                monitors[i] = monitors[i] with { StableDeviceId = null };
            }
        }

        return monitors.AsReadOnly();
    }
}
