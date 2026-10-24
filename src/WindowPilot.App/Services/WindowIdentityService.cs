
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace WindowPilot.App.Services;

public enum WindowIdentityAssessment
{
    SingleCandidate,
    DistinctCurrentTitles,
    Indistinguishable
}

public sealed record WindowIdentitySnapshot(
    IntPtr Handle,
    uint ProcessId,
    string ProcessName,
    string WindowClass,
    string WindowTitle,
    string? ExecutablePath
);

public sealed record WindowIdentityGroup(
    string ProcessName,
    string WindowClass,
    IReadOnlyList<WindowIdentitySnapshot> Windows,
    WindowIdentityAssessment Assessment
);

public sealed class WindowIdentityService
{
    private const uint ProcessQueryLimitedInformation = 0x1000;

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(
        IntPtr hWnd
    );

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(
        IntPtr hWnd,
        out uint processId
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

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(
        uint desiredAccess,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandle,
        uint processId
    );

    [DllImport(
        "kernel32.dll",
        EntryPoint = "QueryFullProcessImageNameW",
        CharSet = CharSet.Unicode,
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(
        IntPtr process,
        uint flags,
        StringBuilder filename,
        ref uint size
    );

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(
        IntPtr handle
    );

    // --------------------------------------------------
    // Universelle Fenstermerkmale ermitteln
    // --------------------------------------------------

    public IReadOnlyList<WindowIdentitySnapshot> InspectWindows(
        IReadOnlyList<DiscoveredWindow> windows)
    {
        ArgumentNullException.ThrowIfNull(windows);

        var results = new List<WindowIdentitySnapshot>();

        foreach (DiscoveredWindow window in windows)
        {
            WindowIdentitySnapshot? snapshot =
                InspectWindow(window);

            if (snapshot is not null)
            {
                results.Add(snapshot);
            }
        }

        return results;
    }

    public WindowIdentitySnapshot? InspectWindow(
        DiscoveredWindow window)
    {
        ArgumentNullException.ThrowIfNull(window);

        if (window.Handle == IntPtr.Zero ||
            !IsWindow(window.Handle))
        {
            return null;
        }

        GetWindowThreadProcessId(
            window.Handle,
            out uint currentProcessId
        );

        // Verhindert, dass ein inzwischen anderweitig
        // verwendetes Fenster-Handle übernommen wird.
        if (currentProcessId == 0 ||
            currentProcessId != window.ProcessId)
        {
            return null;
        }

        var classBuffer = new StringBuilder(256);

        if (GetClassName(
            window.Handle,
            classBuffer,
            classBuffer.Capacity) == 0)
        {
            return null;
        }

        string currentClass = classBuffer.ToString();

        if (!string.Equals(
            currentClass,
            window.ClassName,
            StringComparison.Ordinal))
        {
            return null;
        }

        // Der Pfad ist ein zusätzliches Merkmal.
        // Er muss nicht für jeden Prozess lesbar sein.
        string? executablePath =
            TryGetExecutablePath(currentProcessId);

        return new WindowIdentitySnapshot(
            Handle: window.Handle,
            ProcessId: currentProcessId,
            ProcessName: window.ProcessName,
            WindowClass: currentClass,
            WindowTitle: window.Title,
            ExecutablePath: executablePath
        );
    }

    // --------------------------------------------------
    // Gleichartige Fenster gruppieren
    // --------------------------------------------------

    public IReadOnlyList<WindowIdentityGroup> AnalyzeWindows(
        IReadOnlyList<DiscoveredWindow> windows)
    {
        ArgumentNullException.ThrowIfNull(windows);

        var snapshots = InspectWindows(windows);

        // Prozessname und Fensterklasse bilden
        // zunächst eine allgemeine Fenstergruppe.
        //
        // Beispiel:
        // ApplicationFrameHost + ApplicationFrameWindow
        //
        // Der Gruppenvergleich ist ausdrücklich noch
        // keine endgültige Identifikation der Anwendung.
        var groups = snapshots
            .GroupBy(
                item => (
                    item.ProcessName.ToUpperInvariant() +
                    "\u001F" +
                    item.WindowClass.ToUpperInvariant()
                )
            );

        var results = new List<WindowIdentityGroup>();

        foreach (var group in groups)
        {
            WindowIdentitySnapshot[] members =
                group.ToArray();

            WindowIdentityAssessment assessment =
                AssessGroup(members);

            results.Add(
                new WindowIdentityGroup(
                    ProcessName: members[0].ProcessName,
                    WindowClass: members[0].WindowClass,
                    Windows: members,
                    Assessment: assessment
                )
            );
        }

        return results;
    }

    // --------------------------------------------------
    // Eindeutigkeit innerhalb einer Gruppe prüfen
    // --------------------------------------------------

    private static WindowIdentityAssessment AssessGroup(
        IReadOnlyList<WindowIdentitySnapshot> windows)
    {
        if (windows.Count == 1)
        {
            return WindowIdentityAssessment.SingleCandidate;
        }

        // Leere Titel können nicht zur eindeutigen
        // Identifikation verwendet werden.
        bool allHaveTitles = windows.All(
            window => !string.IsNullOrWhiteSpace(
                window.WindowTitle
            )
        );

        if (!allHaveTitles)
        {
            return WindowIdentityAssessment.Indistinguishable;
        }

        int uniqueTitleCount = windows
            .Select(window => window.WindowTitle.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();

        if (uniqueTitleCount == windows.Count)
        {
            return WindowIdentityAssessment.DistinctCurrentTitles;
        }

        return WindowIdentityAssessment.Indistinguishable;
    }

    // --------------------------------------------------
    // Ausführbare Datei über Windows ermitteln
    // --------------------------------------------------

    private static string? TryGetExecutablePath(
        uint processId)
    {
        IntPtr process = OpenProcess(
            ProcessQueryLimitedInformation,
            false,
            processId
        );

        if (process == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            var buffer = new StringBuilder(32768);

            uint length = (uint)buffer.Capacity;

            bool success = QueryFullProcessImageName(
                process,
                0,
                buffer,
                ref length
            );

            if (!success || length == 0)
            {
                return null;
            }

            return buffer.ToString();
        }
        finally
        {
            CloseHandle(process);
        }
    }
}
