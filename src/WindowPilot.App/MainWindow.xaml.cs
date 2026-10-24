using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using WindowPilot.App.Services;

namespace WindowPilot.App;

public sealed class WindowListItem : INotifyPropertyChanged
{
    private DiscoveredWindow _window;
    private bool _isMarked;
    private bool _isSaved;
    private ImageSource _appIcon;

    public event PropertyChangedEventHandler? PropertyChanged;

    public WindowListItem(DiscoveredWindow window)
    {
        _window = window;
        _appIcon = WindowIconService.GetIcon(window);
    }

    public DiscoveredWindow Window
    {
        get => _window;
        private set
        {
            if (EqualityComparer<DiscoveredWindow>.Default.Equals(_window, value)) return;
            _window = value;
            PropertyChanged?.Invoke(this, new(nameof(Window)));
        }
    }

    public ImageSource AppIcon
    {
        get => _appIcon;
        private set
        {
            _appIcon = value;
            PropertyChanged?.Invoke(this, new(nameof(AppIcon)));
        }
    }

    // Entwurfsauswahl, die mit "Save Layout" übernommen wird.
    public bool IsMarked
    {
        get => _isMarked;
        set
        {
            if (_isMarked == value) return;
            _isMarked = value;
            PropertyChanged?.Invoke(this, new(nameof(IsMarked)));
        }
    }

    // Tatsächliche Mitgliedschaft im zuletzt gespeicherten Layout.
    public bool IsSaved
    {
        get => _isSaved;
        set
        {
            if (_isSaved == value) return;
            _isSaved = value;
            PropertyChanged?.Invoke(this, new(nameof(IsSaved)));
        }
    }

    public void UpdateWindow(DiscoveredWindow window)
    {
        if (_window.Handle == window.Handle && _window.ProcessId == window.ProcessId)
        {
            Window = window;
            return;
        }
        Window = window;
        AppIcon = WindowIconService.GetIcon(window);
    }
}

public partial class MainWindow : Window
{
    private readonly WindowDiscoveryService _discoveryService = new();
    private readonly MonitorService _monitorService = new();
    private readonly LayoutCaptureService _layoutCaptureService = new();
    private readonly LayoutRestoreService _layoutRestoreService = new();
    private readonly WindowAssignmentSettingsService _settingsService = new();
    private readonly SavedLayoutSelectionService _savedSelectionService = new();
    private readonly ObservableCollection<WindowListItem> _windows = new();

    // Unbestätigte Änderungen werden über Aktualisierungen der
    // Fensterliste hinweg beibehalten, ohne das gespeicherte Layout
    // vorzeitig zu verändern.
    private readonly Dictionary<WindowInstanceKey, bool> _selectionOverrides = new();
    private SavedLayout? _savedLayout;
    private bool _refreshInProgress;
    private bool _saveInProgress;
    private bool _deleteInProgress;
    private bool _restoreInProgress;
    private bool _updateInProgress;
    private bool _updatingMode;
    private bool _updatingAutostart = true;
    private bool _isClosed;

    public MainWindow()
    {
        InitializeComponent();
        WindowList.ItemsSource = _windows;
        VersionText.Text = $"v{GitHubUpdateService.CurrentVersion.ToString(3)}";

        Loaded += MainWindow_Loaded;
        SourceInitialized += (_, _) => WindowThemeService.ApplyDarkTitleBar(this);
        Closing += MainWindow_Closing;
        StateChanged += MainWindow_StateChanged;
        Closed += MainWindow_Closed;

        UpdateSelectionCounter();
        ShowTab(settings: false);
        UpdateButtons();
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        LoadModeFromSettings();
        LoadAutostartFromWindows();
        await RefreshDataAsync();
    }

    public void RefreshAfterOpen()
    {
        if (IsLoaded && !_isClosed) _ = RefreshDataAsync();
    }

    private void LoadModeFromSettings()
    {
        _updatingMode = true;
        try
        {
            WindowAssignmentMode mode = _settingsService.GetGlobalMode();
            AssignmentModeSwitch.IsChecked = mode == WindowAssignmentMode.FreeSlots;
            ShowAssignmentMode(mode);
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(this, ex.Message,
                "WindowPilot · Assignment", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            _updatingMode = false;
        }
    }

    private void GeneralTabButton_Click(object sender, RoutedEventArgs e)
        => ShowTab(settings: false);

    private void SettingsTabButton_Click(object sender, RoutedEventArgs e)
        => ShowTab(settings: true);

    private void ShowTab(bool settings)
    {
        GeneralPanel.Visibility = settings ? Visibility.Collapsed : Visibility.Visible;
        SettingsPanel.Visibility = settings ? Visibility.Visible : Visibility.Collapsed;
        GeneralTabButton.Tag = settings ? "Inactive" : "Active";
        SettingsTabButton.Tag = settings ? "Active" : "Inactive";
    }

    private void OpenInstallFolderButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            // The currently running executable is authoritative for installed
            // and portable builds. During development, fall back to the
            // application's own EXE rather than selecting dotnet.exe.
            string? executable = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(executable) ||
                !string.Equals(Path.GetFileName(executable), "WindowPilot.App.exe",
                    StringComparison.OrdinalIgnoreCase))
            {
                executable = Path.Combine(AppContext.BaseDirectory, "WindowPilot.App.exe");
            }

            executable = Path.GetFullPath(executable);
            if (!File.Exists(executable))
                throw new FileNotFoundException("WindowPilot.App.exe could not be found.", executable);

            // /select opens Explorer and highlights the EXE without launching it.
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"/select,\"{executable}\"",
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(this,
                "Could not select the WindowPilot executable.\n\n" + ex.Message,
                "WindowPilot", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void MainWindow_StateChanged(object? sender, EventArgs e)
    {
        if (_isClosed || ((App)System.Windows.Application.Current).IsExiting ||
            WindowState != WindowState.Minimized) return;

        ShowInTaskbar = false;
        Hide();
        WindowState = WindowState.Normal;
    }

    private void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        var app = (App)System.Windows.Application.Current;
        if (app.IsExiting) return;
        e.Cancel = true;
        Dispatcher.BeginInvoke(new Action(app.ExitApplication));
    }

    private void WindowRow_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_isClosed || _refreshInProgress || _saveInProgress || _deleteInProgress) return;
        if (sender is not FrameworkElement { DataContext: WindowListItem item }) return;

        item.IsMarked = !item.IsMarked;
        var key = new WindowInstanceKey(item.Window.Handle, item.Window.ProcessId);
        if (item.IsMarked == item.IsSaved)
            _selectionOverrides.Remove(key);
        else
            _selectionOverrides[key] = item.IsMarked;
        UpdateSelectionCounter();
        UpdateButtons();
        e.Handled = true;
    }

    private void UpdateSelectionCounter()
    {
        int count = _windows.Count(item => item.IsMarked);
        MarkedCountText.Text = count == 1 ? "1 selected" : $"{count} selected";
    }

    private void UpdateButtons()
    {
        if (_isClosed) return;
        bool busy = _refreshInProgress || _saveInProgress || _deleteInProgress || _restoreInProgress || _updateInProgress;
        RefreshButton.IsEnabled = !busy;
        SaveLayoutButton.IsEnabled = !busy && _windows.Any(item => item.IsMarked);
        DeleteLayoutButton.IsEnabled = !busy && WindowPilotDataService.HasSavedData();
        RestoreLayoutButton.IsEnabled = !busy && _savedLayout is not null;
        CheckUpdatesButton.IsEnabled = !busy;
        AssignmentModeSwitch.IsEnabled = !busy;
        AutostartSwitch.IsEnabled = !busy;
        SaveLayoutButtonText.Text = _saveInProgress ? "Saving…" : "Save Layout";
    }

    private void ShowAssignmentMode(WindowAssignmentMode mode)
    {
        ModeStateText.Text = mode == WindowAssignmentMode.FreeSlots
            ? "Free Slots" : "Individual";
    }

    private void AssignmentModeSwitch_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || _isClosed || _updatingMode) return;
        WindowAssignmentMode mode = AssignmentModeSwitch.IsChecked == true
            ? WindowAssignmentMode.FreeSlots : WindowAssignmentMode.Individual;
        try
        {
            _settingsService.SaveGlobalMode(mode);
            ShowAssignmentMode(mode);
            UpdateButtons();
        }
        catch (Exception ex)
        {
            _updatingMode = true;
            try
            {
                AssignmentModeSwitch.IsChecked = mode != WindowAssignmentMode.FreeSlots;
                ShowAssignmentMode(mode == WindowAssignmentMode.FreeSlots
                    ? WindowAssignmentMode.Individual : WindowAssignmentMode.FreeSlots);
            }
            finally { _updatingMode = false; }
            System.Windows.MessageBox.Show(this, ex.Message, "WindowPilot",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e)
        => await RefreshDataAsync();

    // Autostart ist eine Windows-Einstellung, nicht Teil der
    // gespeicherten WindowPilot-Layouts. Das Löschen der App-Daten
    // verändert diesen Schalter deshalb nicht.
    private void LoadAutostartFromWindows()
    {
        _updatingAutostart = true;
        try
        {
            bool enabled = StartupRegistrationService.IsEnabled();
            AutostartSwitch.IsChecked = enabled;
            ShowAutostartState(enabled);
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(this,
                "Could not read the Auto Start status.\n\n" + ex.Message,
                "WindowPilot", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            _updatingAutostart = false;
        }
    }

    private void ShowAutostartState(bool enabled)
    {
        StartupStateText.Text = enabled ? "On" : "Off";
    }

    private void AutostartSwitch_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || _isClosed || _updatingAutostart) return;

        bool enabled = AutostartSwitch.IsChecked == true;
        try
        {
            StartupRegistrationService.SetEnabled(enabled);
            ShowAutostartState(enabled);
        }
        catch (Exception ex)
        {
            _updatingAutostart = true;
            try
            {
                AutostartSwitch.IsChecked = !enabled;
                ShowAutostartState(!enabled);
            }
            finally
            {
                _updatingAutostart = false;
            }
            System.Windows.MessageBox.Show(this,
                "Could not change Auto Start.\n\n" + ex.Message,
                "WindowPilot", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async Task RefreshDataAsync()
    {
        if (_isClosed || _refreshInProgress || _saveInProgress || _deleteInProgress) return;
        _refreshInProgress = true;
        UpdateButtons();
        try
        {
            // UI Automation und Fenstererkennung laufen nicht auf dem
            // WPF-Thread. Die gespeicherte Auswahl ist handle-unabhängig.
            var snapshot = await Task.Run(() =>
            {
                var current = _discoveryService.GetOpenWindows().ToArray();
                SavedLayout? layout = null;
                try
                {
                    layout = _layoutRestoreService.LoadStandardLayout();
                }
                catch (FileNotFoundException) { }

                HashSet<WindowInstanceKey> saved = layout is null
                    ? new HashSet<WindowInstanceKey>()
                    : _savedSelectionService.FindSavedWindows(layout, current);
                return (Windows: current, Layout: layout, Saved: saved);
            });

            if (_isClosed) return;
            _savedLayout = snapshot.Layout;
            SynchronizeWindows(snapshot.Windows, snapshot.Saved);
            UpdateSelectionCounter();
        }
        catch (Exception ex)
        {
            if (!_isClosed)
                System.Windows.MessageBox.Show(this, ex.Message, "Refresh Windows",
                    MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _refreshInProgress = false;
            UpdateButtons();
        }
    }

    private void SynchronizeWindows(
        IReadOnlyList<DiscoveredWindow> latestWindows,
        HashSet<WindowInstanceKey> savedWindows)
    {
        var previous = _windows.ToDictionary(
            item => new WindowInstanceKey(item.Window.Handle, item.Window.ProcessId));

        foreach (WindowListItem item in _windows)
            item.PropertyChanged -= WindowListItem_PropertyChanged;
        _windows.Clear();

        var currentKeys = new HashSet<WindowInstanceKey>();
        foreach (DiscoveredWindow window in latestWindows)
        {
            var key = new WindowInstanceKey(window.Handle, window.ProcessId);
            currentKeys.Add(key);
            WindowListItem item;
            if (previous.TryGetValue(key, out WindowListItem? existing))
            {
                existing.UpdateWindow(window);
                item = existing;
            }
            else
            {
                item = new WindowListItem(window);
            }

            item.IsSaved = savedWindows.Contains(key);
            item.IsMarked = _selectionOverrides.TryGetValue(key, out bool chosen)
                ? chosen : item.IsSaved;
            item.PropertyChanged += WindowListItem_PropertyChanged;
            _windows.Add(item);
        }

        foreach (WindowInstanceKey key in _selectionOverrides.Keys.ToArray())
        {
            if (!currentKeys.Contains(key)) _selectionOverrides.Remove(key);
        }
    }

    private void WindowListItem_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(WindowListItem.IsMarked) && !_isClosed)
        {
            UpdateSelectionCounter();
            UpdateButtons();
        }
    }

    private async void SaveLayoutButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isClosed || _saveInProgress || _refreshInProgress || _deleteInProgress) return;
        DiscoveredWindow[] selected = _windows
            .Where(item => item.IsMarked).Select(item => item.Window).ToArray();
        if (selected.Length == 0) return;

        _saveInProgress = true;
        UpdateButtons();
        try
        {
            await Task.Run(() => _layoutCaptureService.CaptureAndSave(
                selected, _monitorService.GetMonitors()));

            if (_isClosed) return;
            _savedLayout = _layoutRestoreService.LoadStandardLayout();
            _selectionOverrides.Clear();
            var selectedKeys = selected.Select(window =>
                new WindowInstanceKey(window.Handle, window.ProcessId)).ToHashSet();
            foreach (WindowListItem item in _windows)
            {
                var key = new WindowInstanceKey(item.Window.Handle, item.Window.ProcessId);
                item.IsSaved = selectedKeys.Contains(key);
                item.IsMarked = item.IsSaved;
            }
            UpdateSelectionCounter();
        }
        catch (Exception ex)
        {
            if (!_isClosed)
                System.Windows.MessageBox.Show(this, ex.Message, "Save Layout",
                    MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _saveInProgress = false;
            UpdateButtons();
        }
    }

    private async void RestoreLayoutButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isClosed || _restoreInProgress || _saveInProgress || _deleteInProgress) return;
        _restoreInProgress = true;
        UpdateButtons();
        try
        {
            var report = await ((App)System.Windows.Application.Current).RestoreLayoutNowAsync();
            if (_isClosed) return;
            if (report.Positioned == 0)
                System.Windows.MessageBox.Show(this,
                    "No open window could be matched unambiguously to the saved layout. Make sure the relevant applications are open.",
                    "WindowPilot", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            if (!_isClosed)
                System.Windows.MessageBox.Show(this, ex.Message, "Apply Layout",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            _restoreInProgress = false;
            UpdateButtons();
        }
    }

    private async void CheckUpdatesButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isClosed || _updateInProgress) return;
        _updateInProgress = true;
        CheckUpdatesButtonLabel.Text = "Checking…";
        UpdateButtons();
        try
        {
            var service = new GitHubUpdateService();
            GitHubUpdateInfo? update = await service.CheckForUpdatesAsync();
            if (_isClosed) return;
            if (update is null)
            {
                System.Windows.MessageBox.Show(this,
                    $"WindowPilot is up to date (v{GitHubUpdateService.CurrentVersion.ToString(3)}).",
                    "Updates", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var answer = System.Windows.MessageBox.Show(this,
                $"Version {update.Version} is available.\n\n" +
                "Download the installer from GitHub, verify its SHA-256 checksum, and install the update?\n\n" +
                "WindowPilot will close during the update.",
                "Update WindowPilot", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (answer != MessageBoxResult.Yes) return;

            CheckUpdatesButtonLabel.Text = "Downloading…";
            string installer = await service.DownloadVerifiedInstallerAsync(update);
            if (_isClosed) return;
            GitHubUpdateService.LaunchInstaller(installer);
            ((App)System.Windows.Application.Current).ExitApplication();
        }
        catch (Exception ex)
        {
            if (!_isClosed)
                System.Windows.MessageBox.Show(this, ex.Message, "Update Failed",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            _updateInProgress = false;
            if (!_isClosed)
            {
                CheckUpdatesButtonLabel.Text = "Check for Updates";
                UpdateButtons();
            }
        }
    }

    private void DeleteLayoutButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isClosed || _saveInProgress || _refreshInProgress || _deleteInProgress) return;
        DeleteConfirmation.Visibility = Visibility.Visible;
        SaveActions.Visibility = Visibility.Collapsed;
    }

    private void CancelDeleteButton_Click(object sender, RoutedEventArgs e)
    {
        DeleteConfirmation.Visibility = Visibility.Collapsed;
        SaveActions.Visibility = Visibility.Visible;
    }

    private void ConfirmDeleteButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isClosed || _saveInProgress || _refreshInProgress || _deleteInProgress) return;
        _deleteInProgress = true;
        UpdateButtons();
        try
        {
            // Löscht ausschließlich lokale WindowPilot-Daten.
            // Windows-Autostart (HKCU Run) bleibt ausdrücklich bestehen.
            WindowPilotDataService.DeleteAllLocalData();
            _savedLayout = null;
            _selectionOverrides.Clear();
            foreach (WindowListItem item in _windows)
            {
                item.IsSaved = false;
                item.IsMarked = false;
            }
            _updatingMode = true;
            try
            {
                AssignmentModeSwitch.IsChecked = true;
                ShowAssignmentMode(WindowAssignmentMode.FreeSlots);
            }
            finally { _updatingMode = false; }
            UpdateSelectionCounter();
            DeleteConfirmation.Visibility = Visibility.Collapsed;
            SaveActions.Visibility = Visibility.Visible;
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(this, ex.Message, "Delete Data",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _deleteInProgress = false;
            UpdateButtons();
        }
    }

    private void MainWindow_Closed(object? sender, EventArgs e)
    {
        _isClosed = true;
        foreach (WindowListItem item in _windows)
            item.PropertyChanged -= WindowListItem_PropertyChanged;
        _windows.Clear();
    }
}
