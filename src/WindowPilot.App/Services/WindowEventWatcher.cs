
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace WindowPilot.App.Services;

public sealed class WindowEventWatcher : IDisposable
{
    private const uint EVENT_OBJECT_CREATE = 0x8000;
    private const uint EVENT_OBJECT_DESTROY = 0x8001;
    private const uint EVENT_OBJECT_SHOW = 0x8002;
    private const uint EVENT_OBJECT_HIDE = 0x8003;
    private const uint EVENT_OBJECT_NAMECHANGE = 0x800C;

    private const int OBJID_WINDOW = 0;
    private const int CHILDID_SELF = 0;
    private const uint GA_ROOT = 2;

    private const uint WINEVENT_OUTOFCONTEXT = 0;

    private readonly WinEventProc _callback;
    private readonly List<IntPtr> _hooks = new();
    private readonly HashSet<IntPtr> _knownWindows = new();

    private GCHandle _callbackHandle;
    private bool _disposed;

    public event EventHandler? WindowsChanged;

    private delegate void WinEventProc(
        IntPtr hook,
        uint eventType,
        IntPtr windowHandle,
        int objectId,
        int childId,
        uint eventThread,
        uint eventTime
    );

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWinEventHook(
        uint eventMin,
        uint eventMax,
        IntPtr module,
        WinEventProc callback,
        uint processId,
        uint threadId,
        uint flags
    );

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWinEvent(
        IntPtr hook
    );

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(
        IntPtr windowHandle,
        uint flags
    );

    public WindowEventWatcher()
    {
        _callback = HandleWindowEvent;
        _callbackHandle = GCHandle.Alloc(_callback);

        try
        {
            RegisterHook(EVENT_OBJECT_CREATE);
            RegisterHook(EVENT_OBJECT_DESTROY);
            RegisterHook(EVENT_OBJECT_SHOW);
            RegisterHook(EVENT_OBJECT_HIDE);
            RegisterHook(EVENT_OBJECT_NAMECHANGE);
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    private void RegisterHook(uint eventType)
    {
        IntPtr hook = SetWinEventHook(
            eventType,
            eventType,
            IntPtr.Zero,
            _callback,
            0,
            0,
            WINEVENT_OUTOFCONTEXT
        );

        if (hook == IntPtr.Zero)
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                $"Windows event {eventType:X} could " +
                "not be monitored."
            );
        }

        _hooks.Add(hook);
    }

    public void UpdateKnownWindows(
        IEnumerable<IntPtr> handles)
    {
        if (_disposed)
            return;

        _knownWindows.Clear();

        foreach (IntPtr handle in handles)
        {
            if (handle != IntPtr.Zero)
                _knownWindows.Add(handle);
        }
    }

    private void HandleWindowEvent(
        IntPtr hook,
        uint eventType,
        IntPtr windowHandle,
        int objectId,
        int childId,
        uint eventThread,
        uint eventTime)
    {
        if (_disposed)
            return;

        if (windowHandle == IntPtr.Zero)
            return;

        if (objectId != OBJID_WINDOW ||
            childId != CHILDID_SELF)
        {
            return;
        }

        // Bei bereits zerstörten Fenstern können wir
        // die Fensterhierarchie nicht mehr abfragen.
        // Deshalb prüfen wir deren zuletzt bekannte Handles.
        if (eventType == EVENT_OBJECT_DESTROY)
        {
            if (_knownWindows.Contains(windowHandle))
                WindowsChanged?.Invoke(this, EventArgs.Empty);

            return;
        }

        // Nur Top-Level-Fenster berücksichtigen.
        // Untergeordnete Steuerelemente ignorieren.
        IntPtr root = GetAncestor(
            windowHandle,
            GA_ROOT
        );

        if (root == IntPtr.Zero || root != windowHandle)
            return;

        WindowsChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

        foreach (IntPtr hook in _hooks)
        {
            if (hook != IntPtr.Zero)
                UnhookWinEvent(hook);
        }

        _hooks.Clear();
        _knownWindows.Clear();

        if (_callbackHandle.IsAllocated)
            _callbackHandle.Free();

        WindowsChanged = null;
    }
}
