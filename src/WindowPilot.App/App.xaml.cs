using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using WindowPilot.App.Services;
using Forms = System.Windows.Forms;

namespace WindowPilot.App;

public partial class App : System.Windows.Application
{
    private const string MutexName = @"Local\WindowPilot_SingleInstance_2026";
    private const string ActivationEventName = @"Local\WindowPilot_OpenInterface_2026";

    private const int SwShow = 5;
    private const int SwRestore = 9;

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    private WindowAutoRestoreService? _autoRestoreService;
    private Forms.NotifyIcon? _trayIcon;
    private MainWindow? _mainWindow;
    private Mutex? _singleInstanceMutex;
    private EventWaitHandle? _activationEvent;
    private Task? _activationTask;
    private volatile bool _isExiting;
    private System.Threading.Timer? _updateTimer;
    private int _updateCheckRunning;
    private Version? _lastAnnouncedUpdate;

    public bool IsExiting => _isExiting;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        bool backgroundStart = e.Args.Any(a =>
            a.Equals("--background", StringComparison.OrdinalIgnoreCase));

        // Eine zweite Instanz darf nicht gleichzeitig Fenster verschieben.
        _singleInstanceMutex = new Mutex(true, MutexName, out bool isFirstInstance);
        if (!isFirstInstance)
        {
            _singleInstanceMutex.Dispose();
            _singleInstanceMutex = null;
            // Ein später Autostart darf eine schon laufende Instanz
            // nicht ungefragt aus dem Tray holen.
            if (!backgroundStart)
            {
                try
                {
                    using EventWaitHandle existing = EventWaitHandle.OpenExisting(ActivationEventName);
                    existing.Set();
                }
                catch (WaitHandleCannotBeOpenedException) { }
                catch (UnauthorizedAccessException) { }
            }
            Shutdown();
            return;
        }

        _activationEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ActivationEventName);
        _activationTask = Task.Run(() =>
        {
            while (!_isExiting)
            {
                _activationEvent.WaitOne();
                if (_isExiting)
                    break;

                try
                {
                    Dispatcher.BeginInvoke(new Action(ShowMainWindow));
                }
                catch (InvalidOperationException) { break; }
            }
        });

        // Repair old Run paths after installation (not while debugging in Visual Studio).
        try { StartupRegistrationService.RepairInstalledRegistration(); }
        catch (Exception ex) { Debug.WriteLine("[WindowPilot] Auto Start repair: " + ex.Message); }

        // Den WinEvent-Hook so früh wie möglich installieren.
        // Erst danach die Benutzeroberfläche und das Tray vorbereiten.
        // Der erste Abgleich umfasst immer auch vorhandene Fenster.
        // Windows garantiert keine Autostart-Reihenfolge.
        try
        {
            _autoRestoreService = new WindowAutoRestoreService();
            // Existing windows are restored once on EVERY process launch.
            _autoRestoreService.Start(restoreExistingAtLogon: true);
        }
        catch (Exception ex)
        {
            _autoRestoreService?.Dispose();
            _autoRestoreService = null;
            Debug.WriteLine("[WindowPilot] Auto Restore could not start: " + ex);
            if (!backgroundStart)
            {
                System.Windows.MessageBox.Show("Automatic window placement could not start.\n\n" + ex.Message,
                    "WindowPilot", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        SetupTrayIcon();
        // One delayed background check per process launch, with no recurring
        // polling. Errors are logged only and never block Windows sign-in.
        // The Settings tab also provides an independent manual check.
        _updateTimer = new System.Threading.Timer(
            state => { _ = CheckUpdatesQuietlyAsync(); },
            null,
            TimeSpan.FromSeconds(8),
            Timeout.InfiniteTimeSpan);

        // Hauptfenster nur bei manuellem Programmstart anzeigen.
        _mainWindow = new MainWindow();
        MainWindow = _mainWindow;

        if (!backgroundStart)
            ShowMainWindow();
    }

    public Task<WindowAutoRestoreService.LayoutRestoreReport> RestoreLayoutNowAsync()
    {
        if (_autoRestoreService is null)
            return Task.FromException<WindowAutoRestoreService.LayoutRestoreReport>(
                new InvalidOperationException("Window monitoring is not available."));
        return _autoRestoreService.RestoreLayoutNowAsync();
    }

    private void SetupTrayIcon()
    {
        var open = new Forms.ToolStripMenuItem("Open WindowPilot");
        open.Click += (_, _) => RequestShowMainWindow();

        var quit = new Forms.ToolStripMenuItem("Exit WindowPilot");
        quit.Click += (_, _) => Dispatcher.BeginInvoke(new Action(ExitApplication));

        var menu = new Forms.ContextMenuStrip
        {
            BackColor = Color.FromArgb(23, 32, 49),
            ForeColor = Color.FromArgb(235, 241, 253),
            ShowImageMargin = false,
            ShowCheckMargin = false
        };
        menu.Items.Add(open);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(quit);

        Icon icon;
        try
        {
            var uri = new Uri("pack://application:,,,/WindowPilot.App;component/Assets/windowpilot.ico");
            using Stream stream = System.Windows.Application.GetResourceStream(uri)!.Stream;
            using var resourceIcon = new Icon(stream);
            icon = (Icon)resourceIcon.Clone();
        }
        catch
        {
            icon = (Icon)SystemIcons.Application.Clone();
        }

        _trayIcon = new Forms.NotifyIcon
        {
            Text = "WindowPilot – Window Layouts",
            Icon = icon,
            ContextMenuStrip = menu,
            Visible = true
        };

        _trayIcon.BalloonTipClicked += (_, _) => RequestShowMainWindow();
        _trayIcon.MouseClick += (_, args) =>
        {
            if (args.Button == Forms.MouseButtons.Left)
                RequestShowMainWindow();
        };
    }

    // Den Klick auf das Tray-Symbol möglichst sofort bearbeiten.
    // Eine verzögerte Dispatcher-Aktivierung kann Windows den Fokus
    // wieder an die zuvor aktive Anwendung überlassen.
    private void RequestShowMainWindow()
    {
        if (_isExiting || _mainWindow is null)
            return;

        if (Dispatcher.CheckAccess())
        {
            ShowMainWindow();
        }
        else if (!Dispatcher.HasShutdownStarted && !Dispatcher.HasShutdownFinished)
        {
            Dispatcher.BeginInvoke(
                new Action(ShowMainWindow),
                DispatcherPriority.Input);
        }
    }

    private void ShowMainWindow()
    {
        if (_isExiting || _mainWindow is null)
            return;

        // Erst den minimierten Zustand verlassen, dann die Taskleiste
        // und das Fenster wieder anzeigen. Ein Tray-Start darf nicht
        // als minimiertes Taskleistenfenster enden.
        if (_mainWindow.WindowState == WindowState.Minimized)
            _mainWindow.WindowState = WindowState.Normal;

        _mainWindow.ShowInTaskbar = true;

        if (!_mainWindow.IsVisible)
            _mainWindow.Show();

        IntPtr handle = new WindowInteropHelper(_mainWindow).Handle;
        if (handle != IntPtr.Zero)
        {
            // Auch einen eventuell noch von Windows minimierten
            // nativen Zustand explizit wiederherstellen.
            ShowWindow(handle, IsIconic(handle) ? SwRestore : SwShow);
        }

        bool activated = _mainWindow.Activate();
        bool foreground = handle != IntPtr.Zero && SetForegroundWindow(handle);

        if (!activated && !foreground)
        {
            // Fallback, falls Windows die Vordergrundaktivierung
            // einschränkt. Den bisherigen Topmost-Wert erhalten.
            bool wasTopmost = _mainWindow.Topmost;
            if (!wasTopmost)
            {
                _mainWindow.Topmost = true;
                _mainWindow.Topmost = false;
            }
            _mainWindow.Activate();
        }

        _mainWindow.Focus();
        _mainWindow.RefreshAfterOpen();
    }

    private async Task CheckUpdatesQuietlyAsync()
    {
        if (_isExiting || Interlocked.Exchange(ref _updateCheckRunning, 1) != 0) return;
        try
        {
            var update = await new GitHubUpdateService().CheckForUpdatesAsync();
            if (update is null || _isExiting) return;
            _ = Dispatcher.BeginInvoke(new Action(() =>
            {
                if (_isExiting || _trayIcon is null || _lastAnnouncedUpdate == update.Version) return;
                _lastAnnouncedUpdate = update.Version;
                _trayIcon.BalloonTipTitle = $"WindowPilot {update.Version} available";
                _trayIcon.BalloonTipText = "Open WindowPilot and select Check for Updates.";
                _trayIcon.BalloonTipIcon = Forms.ToolTipIcon.Info;
                _trayIcon.ShowBalloonTip(6000);
            }));
        }
        catch (Exception ex)
        {
            Debug.WriteLine("[WindowPilot] GitHub Update-Check: " + ex.Message);
        }
        finally
        {
            Interlocked.Exchange(ref _updateCheckRunning, 0);
        }
    }

    public void ExitApplication()
    {
        if (_isExiting)
            return;

        _isExiting = true;
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _isExiting = true;
        _updateTimer?.Dispose();
        _updateTimer = null;
        _activationEvent?.Set();

        _autoRestoreService?.Dispose();
        _autoRestoreService = null;

        if (_trayIcon is not null)
        {
            _trayIcon.Visible = false;
            Icon? icon = _trayIcon.Icon;
            Forms.ContextMenuStrip? menu = _trayIcon.ContextMenuStrip;
            _trayIcon.Dispose();
            menu?.Dispose();
            icon?.Dispose();
            _trayIcon = null;
        }

        // Die Aktivierungsaufgabe beendet sich durch das Signal oben.
        // Das Handle bleibt bis zu ihrem Ende gültig.
        if (_activationTask?.Wait(TimeSpan.FromMilliseconds(500)) == true)
        {
            _activationEvent?.Dispose();
            _activationEvent = null;
        }

        if (_singleInstanceMutex is not null)
        {
            _singleInstanceMutex.ReleaseMutex();
            _singleInstanceMutex.Dispose();
            _singleInstanceMutex = null;
        }

        base.OnExit(e);
    }
}
