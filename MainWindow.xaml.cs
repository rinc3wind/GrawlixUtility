using Microsoft.Win32;
using System.Diagnostics;
using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using DragEventArgs = System.Windows.DragEventArgs;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using IDataObject = System.Windows.IDataObject;
using Brush = System.Windows.Media.Brush;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;
using Button = System.Windows.Controls.Button;
using DragDropEffects = System.Windows.DragDropEffects;
using DataFormats = System.Windows.DataFormats;

namespace GrawlixUtility;

public partial class MainWindow : Window
{
    private readonly AppSettings _settings;
    private Profile? _activeProfile;
    private bool _loadingProfiles;
    private bool _initializingStartupSettings = true;
    private bool _exitingFromTray;
    private readonly System.Windows.Forms.NotifyIcon _trayIcon;
    private readonly DispatcherTimer _micToggleStatusTimer;

    public MainWindow()
    {
        InitializeComponent();
        _trayIcon = new System.Windows.Forms.NotifyIcon
        {
            Text = "Grawlix Utility",
            Icon = System.Drawing.SystemIcons.Application,
            Visible = true,
            ContextMenuStrip = new System.Windows.Forms.ContextMenuStrip()
        };
        _trayIcon.DoubleClick += (_, _) => RestoreFromTray();
        _trayIcon.ContextMenuStrip.Items.Add("Open Grawlix Utility", null, (_, _) => RestoreFromTray());
        _trayIcon.ContextMenuStrip.Items.Add("Exit", null, (_, _) => { _exitingFromTray = true; Close(); });
        _settings = SettingsStore.Load();
        RestoreMainWindowBounds();
        Closing += MainWindow_Closing;
        ProfileCombo.ItemsSource = _settings.Profiles;
        ProfileCombo.SelectedItem = _settings.Profiles.FirstOrDefault(p => p.Name == _settings.SelectedProfile) ?? _settings.Profiles[0];
        RefreshList();
        RefreshWebApps();
        _micToggleStatusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _micToggleStatusTimer.Tick += (_, _) => RefreshMicToggleStatus();
        _micToggleStatusTimer.Start();
        StartWithWindowsCheckBox.IsChecked = _settings.StartAutomaticallyOnWindowsBoot;
        StartMicrophoneToggleCheckBox.IsChecked = _settings.StartMicrophoneToggleAutomatically;
        MinimizeToTrayCheckBox.IsChecked = _settings.MinimizeToTrayOnExit;
        _initializingStartupSettings = false;
        try { SyncWindowsStartupRegistration(_settings.StartAutomaticallyOnWindowsBoot); }
        catch (Exception ex) { StartupSettingsStatus.Text = $"Could not update startup setting: {ex.Message}"; }
        RefreshMicToggleStatus();
        if (_settings.StartMicrophoneToggleAutomatically) StartMicToggle();
    }

    private void RestoreMainWindowBounds()
    {
        var bounds = _settings.MainWindowBounds;
        if (bounds is null || bounds.Width < MinWidth || bounds.Height < MinHeight) return;

        Width = bounds.Width;
        Height = bounds.Height;
        var minLeft = SystemParameters.VirtualScreenLeft - Width + 120;
        var maxLeft = SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - 120;
        var minTop = SystemParameters.VirtualScreenTop;
        var maxTop = SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - 100;
        Left = Math.Clamp(bounds.X, Math.Min(minLeft, maxLeft), Math.Max(minLeft, maxLeft));
        Top = Math.Clamp(bounds.Y, Math.Min(minTop, maxTop), Math.Max(minTop, maxTop));
        WindowStartupLocation = WindowStartupLocation.Manual;
        if (bounds.Maximized) WindowState = WindowState.Maximized;
    }

    private void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_settings.MinimizeToTrayOnExit && !_exitingFromTray)
        {
            e.Cancel = true;
            Hide();
            try { SettingsStore.Save(_settings); } catch { }
            return;
        }

        _micToggleStatusTimer.Stop();
        _trayIcon.Visible = false;
        _trayIcon.Dispose();
        var normalBounds = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
        _settings.MainWindowBounds = new WindowRect
        {
            X = (int)Math.Round(normalBounds.Left),
            Y = (int)Math.Round(normalBounds.Top),
            Width = (int)Math.Round(normalBounds.Width),
            Height = (int)Math.Round(normalBounds.Height),
            Maximized = WindowState == WindowState.Maximized
        };
        try { SettingsStore.Save(_settings); }
        catch { }
    }

    private void RestoreFromTray()
    {
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }

    private void TabHeaderButton_Click(object sender, RoutedEventArgs e)
    {
        var element = sender as DependencyObject;
        while (element is not null && element is not TabItem)
            element = VisualTreeHelper.GetParent(element);
        if (element is TabItem tabItem) tabItem.IsSelected = true;
    }

    private static Process[] GetMicToggleProcesses() => Process.GetProcessesByName("MicToggle");

    private void RefreshMicToggleStatus()
    {
        var processes = GetMicToggleProcesses();
        var running = processes.Length > 0;
        foreach (var process in processes) process.Dispose();

        MicStatusText.Text = running ? "MicToggle is running" : "MicToggle is stopped";
        MicStatusDot.Fill = (Brush)FindResource(running ? "Accent" : "TextMuted");
        StartMicToggleButton.IsEnabled = !running;
        StopMicToggleButton.IsEnabled = running;
        MicStatusDetailText.Text = running
            ? "Press Scroll Lock to toggle the microphone, or double-click the MicToggle tray icon."
            : "Start MicToggle to enable the global Scroll Lock microphone toggle.";
    }

    private void StartMicToggle_Click(object sender, RoutedEventArgs e)
    {
        StartMicToggle();
    }

    private void StartMicToggle()
    {
        var runningProcesses = GetMicToggleProcesses();
        var alreadyRunning = runningProcesses.Length > 0;
        foreach (var process in runningProcesses) process.Dispose();
        if (alreadyRunning) return;
        var executablePath = Path.Combine(AppContext.BaseDirectory, "MicToggle.exe");
        if (!File.Exists(executablePath))
        {
            MicStatusDetailText.Text = $"MicToggle.exe was not found at {executablePath}";
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(executablePath) { UseShellExecute = true });
            MicStatusDetailText.Text = "Starting MicToggle…";
            _ = Dispatcher.InvokeAsync(async () =>
            {
                await Task.Delay(300);
                RefreshMicToggleStatus();
            });
        }
        catch (Exception ex)
        {
            MicStatusDetailText.Text = $"Could not start MicToggle: {ex.Message}";
        }
    }

    private void StartWithWindows_Changed(object sender, RoutedEventArgs e)
    {
        if (_initializingStartupSettings) return;
        var enabled = StartWithWindowsCheckBox.IsChecked == true;
        _settings.StartAutomaticallyOnWindowsBoot = enabled;
        try
        {
            SyncWindowsStartupRegistration(enabled);
            SettingsStore.Save(_settings);
            StartupSettingsStatus.Text = enabled ? "Startup registration enabled." : "Startup registration disabled.";
        }
        catch (Exception ex)
        {
            StartupSettingsStatus.Text = $"Could not update startup setting: {ex.Message}";
        }
    }

    private void StartMicrophoneToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (_initializingStartupSettings) return;
        _settings.StartMicrophoneToggleAutomatically = StartMicrophoneToggleCheckBox.IsChecked == true;
        try
        {
            SettingsStore.Save(_settings);
            StartupSettingsStatus.Text = _settings.StartMicrophoneToggleAutomatically
                ? "Microphone Toggle will start whenever Grawlix Utility opens."
                : "Microphone Toggle will only start when you choose Start MicToggle.";
        }
        catch (Exception ex)
        {
            StartupSettingsStatus.Text = $"Could not save setting: {ex.Message}";
        }
    }

    private void MinimizeToTray_Changed(object sender, RoutedEventArgs e)
    {
        if (_initializingStartupSettings) return;
        _settings.MinimizeToTrayOnExit = MinimizeToTrayCheckBox.IsChecked == true;
        try
        {
            SettingsStore.Save(_settings);
            StartupSettingsStatus.Text = _settings.MinimizeToTrayOnExit
                ? "Closing the window will keep Grawlix Utility running in the system tray."
                : "Closing the window will exit Grawlix Utility.";
        }
        catch (Exception ex) { StartupSettingsStatus.Text = $"Could not save setting: {ex.Message}"; }
    }

    private static void SyncWindowsStartupRegistration(bool enabled)
    {
        using var runKey = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", writable: true);
        if (runKey is null) throw new InvalidOperationException("Could not access the current user's startup settings.");

        if (enabled)
        {
            var executablePath = Environment.ProcessPath
                ?? throw new InvalidOperationException("Could not determine the application executable path.");
            runKey.SetValue("GrawlixUtility", $"\"{executablePath}\"");
        }
        else
        {
            runKey.DeleteValue("GrawlixUtility", throwOnMissingValue: false);
        }
        runKey.DeleteValue("PersistentWindows", throwOnMissingValue: false);
    }

    private void StopMicToggle_Click(object sender, RoutedEventArgs e)
    {
        foreach (var process in GetMicToggleProcesses())
        {
            try { process.Kill(); }
            catch (Exception ex) { MicStatusDetailText.Text = $"Could not stop MicToggle: {ex.Message}"; }
            finally { process.Dispose(); }
        }
        RefreshMicToggleStatus();
    }

    private void OpenSoundSettings_Click(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo("mmsys.cpl") { UseShellExecute = true }); }
        catch (Exception ex) { MicStatusDetailText.Text = $"Could not open Sound settings: {ex.Message}"; }
    }

    private void ProfileCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingProfiles || ProfileCombo.SelectedItem is not Profile profile) return;
        _activeProfile = profile;
        _settings.SelectedProfile = profile.Name;
        RefreshList();
        Persist();
    }

    private void RefreshList()
    {
        _activeProfile ??= ProfileCombo.SelectedItem as Profile ?? _settings.Profiles[0];
        ApplicationsList.ItemsSource = null;
        ApplicationsList.ItemsSource = _activeProfile.Applications;
        EmptyHint.Visibility = _activeProfile.Applications.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Persist()
    {
        try { SettingsStore.Save(_settings); SetStatus("Profile saved"); }
        catch (Exception ex) { SetStatus($"Could not save settings: {ex.Message}"); }
    }

    private void SetStatus(string message) => StatusText.Text = message;

    private void NewProfile_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new ProfileNameDialog { Owner = this, Title = "New profile" };
        if (dialog.ShowDialog() != true) return;
        var name = dialog.ProfileName.Trim();
        if (_settings.Profiles.Any(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase))) { SetStatus("That profile already exists"); return; }
        var profile = new Profile { Name = name };
        _settings.Profiles.Add(profile);
        _loadingProfiles = true;
        ProfileCombo.ItemsSource = null;
        ProfileCombo.ItemsSource = _settings.Profiles;
        ProfileCombo.SelectedItem = profile;
        _loadingProfiles = false;
        _activeProfile = profile;
        _settings.SelectedProfile = profile.Name;
        RefreshList();
        Persist();
    }

    private void RenameProfile_Click(object sender, RoutedEventArgs e)
    {
        if (_activeProfile is null) return;
        var dialog = new ProfileNameDialog { Owner = this, Title = "Rename profile", ProfileName = _activeProfile.Name };
        if (dialog.ShowDialog() != true) return;
        var name = dialog.ProfileName.Trim();
        if (_settings.Profiles.Any(p => !ReferenceEquals(p, _activeProfile) && p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
        {
            SetStatus("That profile name is already in use");
            return;
        }

        _activeProfile.Name = name;
        _settings.SelectedProfile = name;
        _loadingProfiles = true;
        ProfileCombo.ItemsSource = null;
        ProfileCombo.ItemsSource = _settings.Profiles;
        ProfileCombo.SelectedItem = _activeProfile;
        _loadingProfiles = false;
        Persist();
    }

    private void DeleteProfile_Click(object sender, RoutedEventArgs e)
    {
        if (_activeProfile is null) return;
        if (_settings.Profiles.Count <= 1)
        {
            SetStatus("Keep at least one profile");
            return;
        }

        var confirmation = new DeleteProfileDialog(_activeProfile.Name) { Owner = this };
        if (confirmation.ShowDialog() != true) return;

        var deletedName = _activeProfile.Name;
        var replacement = _settings.Profiles.First(p => !ReferenceEquals(p, _activeProfile));
        _settings.Profiles.Remove(_activeProfile);
        _activeProfile = replacement;
        _settings.SelectedProfile = replacement.Name;

        _loadingProfiles = true;
        ProfileCombo.ItemsSource = null;
        ProfileCombo.ItemsSource = _settings.Profiles;
        ProfileCombo.SelectedItem = replacement;
        _loadingProfiles = false;

        RefreshList();
        Persist();
        SetStatus($"Deleted profile “{deletedName}”");
    }

    private void AddExecutable_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "Select an application", Filter = "Applications (*.exe;*.bat;*.cmd;*.lnk)|*.exe;*.bat;*.cmd;*.lnk|All files (*.*)|*.*", Multiselect = true };
        if (dialog.ShowDialog(this) == true) AddPaths(dialog.FileNames);
    }

    private void AddPaths(IEnumerable<string> paths)
    {
        var profile = _activeProfile;
        if (profile is null) return;
        var added = 0;
        foreach (var path in paths)
        {
            if (!File.Exists(path) || !IsSupportedApplication(path)) continue;
            var full = Path.GetFullPath(path);
            if (profile.Applications.Any(a => a.Path.Equals(full, StringComparison.OrdinalIgnoreCase))) continue;
            profile.Applications.Add(new ExecutableEntry { Path = full });
            added++;
        }
        RefreshList();
        if (added > 0) Persist(); else SetStatus("No new executable files were added");
    }

    private void RemoveExecutable_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: ExecutableEntry entry } && _activeProfile is not null)
        {
            _activeProfile.Applications.Remove(entry);
            RefreshList();
            Persist();
        }
    }

    private void RunExecutable_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: ExecutableEntry entry }) return;
        try
        {
            Process.Start(new ProcessStartInfo(entry.Path) { UseShellExecute = true });
            SetStatus($"Started {entry.Name}");
        }
        catch (Exception ex)
        {
            SetStatus($"Could not start {entry.Name}: {ex.Message}");
        }
    }

    private void SaveLayout_Click(object sender, RoutedEventArgs e)
    {
        if (_activeProfile is null) return;
        var captured = WindowManager.Capture(_activeProfile.Applications.Select(a => a.Path));
        foreach (var app in _activeProfile.Applications) app.Windows = captured.GetValueOrDefault(Path.GetFullPath(app.Path), []);
        Persist();
        var total = _activeProfile.Applications.Sum(a => a.Windows.Count);
        var perApp = string.Join(", ", _activeProfile.Applications.Select(a => a.Windows.Count > 0
            ? $"{a.Name}: {a.Windows.Count} [{string.Join("; ", a.Windows.Select(w => $"{w.X},{w.Y} {w.Width}x{w.Height}"))}]"
            : $"{a.Name}: 0 (target: {WindowManager.DescribeExpectedWindowPaths(a.Path)})"));
        SetStatus($"Saved {total} window{(total == 1 ? "" : "s")} in “{_activeProfile.Name}” ({perApp})");
    }

    private async void RestoreLayout_Click(object sender, RoutedEventArgs e)
    {
        if (_activeProfile is null) return;
        SaveLayoutButtonEnabled(false);
        try
        {
            if (CloseAllCheckBox.IsChecked == true)
                await WindowManager.CloseAllOtherWindowsAsync((uint)Environment.ProcessId, SetStatus);
            await WindowManager.RestoreAsync(_activeProfile, SetStatus);
        }
        finally { SaveLayoutButtonEnabled(true); }
    }

    private void SaveLayoutButtonEnabled(bool enabled)
    {
        foreach (var button in FindVisualChildren<Button>(this))
            if (button.Content?.ToString() is "Save current layout" or "Restore layout") button.IsEnabled = enabled;
    }

    private static IEnumerable<T> FindVisualChildren<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T typed) yield return typed;
            foreach (var nested in FindVisualChildren<T>(child)) yield return nested;
        }
    }

    private void Window_DragOver(object sender, DragEventArgs e) => e.Effects = IsExeDrop(e) ? DragDropEffects.Copy : DragDropEffects.None;
    private void List_DragOver(object sender, DragEventArgs e) => e.Effects = IsExeDrop(e) ? DragDropEffects.Copy : DragDropEffects.None;
    private static bool IsSupportedApplication(string path) => Path.GetExtension(path).ToLowerInvariant() is ".exe" or ".bat" or ".cmd" or ".lnk";
    private static bool IsExeDrop(DragEventArgs e) => e.Data.GetDataPresent(DataFormats.FileDrop) && ((string[])e.Data.GetData(DataFormats.FileDrop)!).Any(IsSupportedApplication);
    private void Window_Drop(object sender, DragEventArgs e) => HandleDrop(e);
    private void List_Drop(object sender, DragEventArgs e) => HandleDrop(e);
    private void HandleDrop(DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop)) AddPaths((string[])e.Data.GetData(DataFormats.FileDrop)!);
        e.Handled = true;
    }

    private void RefreshWebApps()
    {
        var hasChrome = !string.IsNullOrWhiteSpace(_settings.ChromePath);
        ChromePathText.Text = hasChrome ? _settings.ChromePath : "No Chrome executable selected";
        ChromePathText.Foreground = (Brush)FindResource(hasChrome ? "TextMain" : "TextMuted");

        WebAppsList.ItemsSource = null;
        WebAppsList.ItemsSource = _settings.WebApps;
        WebEmptyHint.Visibility = _settings.WebApps.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SetWebStatus(string message) => WebStatusText.Text = message;

    private void PersistWeb(string statusMessage = "Saved")
    {
        try { SettingsStore.Save(_settings); SetWebStatus(statusMessage); }
        catch (Exception ex) { SetWebStatus($"Could not save settings: {ex.Message}"); }
    }

    private void SelectChrome_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Select Google Chrome executable",
            Filter = "Executables (*.exe)|*.exe|All files (*.*)|*.*"
        };

        var currentDirectory = !string.IsNullOrWhiteSpace(_settings.ChromePath) ? Path.GetDirectoryName(_settings.ChromePath) : null;
        if (!string.IsNullOrEmpty(currentDirectory) && Directory.Exists(currentDirectory))
        {
            dialog.InitialDirectory = currentDirectory;
        }
        else
        {
            var defaultChromeDirectory = new[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Google", "Chrome", "Application"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Google", "Chrome", "Application"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Google", "Chrome", "Application")
            }.FirstOrDefault(Directory.Exists);
            if (defaultChromeDirectory is not null) dialog.InitialDirectory = defaultChromeDirectory;
        }

        if (dialog.ShowDialog(this) != true) return;
        _settings.ChromePath = Path.GetFullPath(dialog.FileName);
        RefreshWebApps();
        PersistWeb($"Chrome set to {Path.GetFileName(_settings.ChromePath)}");
    }

    private void WebLinkInput_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        AddWebLinkFromInput();
    }

    private void AddWebLink_Click(object sender, RoutedEventArgs e) => AddWebLinkFromInput();

    private void AddWebLinkFromInput()
    {
        if (AddWebLinks([WebLinkInput.Text]) > 0) WebLinkInput.Clear();
    }

    private int AddWebLinks(IEnumerable<string> rawLinks)
    {
        var added = 0;
        var invalid = false;
        foreach (var raw in rawLinks)
        {
            if (!TryNormalizeWebUrl(raw, out var url))
            {
                if (!string.IsNullOrWhiteSpace(raw)) invalid = true;
                continue;
            }
            if (_settings.WebApps.Any(w => w.Url.Equals(url, StringComparison.OrdinalIgnoreCase))) continue;
            _settings.WebApps.Add(new WebAppEntry { Url = url });
            added++;
        }

        RefreshWebApps();
        if (added > 0) PersistWeb($"Added {added} webpage link{(added == 1 ? "" : "s")}");
        else if (invalid) SetWebStatus("Enter a valid http or https webpage link");
        else SetWebStatus("No new webpage links were added");
        return added;
    }

    private void RemoveWebLink_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: WebAppEntry entry }) return;
        _settings.WebApps.Remove(entry);
        RefreshWebApps();
        PersistWeb($"Removed {entry.DisplayTitle}");
    }

    private void RunWebLink_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: WebAppEntry entry }) return;
        if (string.IsNullOrWhiteSpace(_settings.ChromePath) || !File.Exists(_settings.ChromePath))
        {
            SetWebStatus("Select your Google Chrome executable first");
            return;
        }

        try
        {
            var startInfo = new ProcessStartInfo(_settings.ChromePath)
            {
                UseShellExecute = false,
                WorkingDirectory = Path.GetDirectoryName(_settings.ChromePath) ?? ""
            };
            startInfo.ArgumentList.Add($"--app={entry.Url}");
            Process.Start(startInfo)?.Dispose();
            SetWebStatus($"Opened {entry.DisplayTitle} in Chrome");
        }
        catch (Exception ex)
        {
            SetWebStatus($"Could not launch Chrome: {ex.Message}");
        }
    }

    private void WebAppsList_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = ExtractDroppedWebLinks(e.Data).Any() ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void WebAppsList_Drop(object sender, DragEventArgs e)
    {
        AddWebLinks(ExtractDroppedWebLinks(e.Data));
        e.Handled = true;
    }

    private static IEnumerable<string> ExtractDroppedWebLinks(IDataObject data)
    {
        if (data.GetDataPresent(DataFormats.FileDrop) && data.GetData(DataFormats.FileDrop) is string[] files)
        {
            foreach (var file in files)
            {
                if (!Path.GetExtension(file).Equals(".url", StringComparison.OrdinalIgnoreCase) || !File.Exists(file)) continue;
                string? url = null;
                try
                {
                    foreach (var line in File.ReadLines(file))
                    {
                        if (line.StartsWith("URL=", StringComparison.OrdinalIgnoreCase))
                        {
                            url = line[4..].Trim();
                            break;
                        }
                    }
                }
                catch { }
                if (!string.IsNullOrWhiteSpace(url)) yield return url;
            }
            yield break;
        }

        var text = data.GetDataPresent(DataFormats.UnicodeText)
            ? data.GetData(DataFormats.UnicodeText) as string
            : data.GetDataPresent(DataFormats.Text)
                ? data.GetData(DataFormats.Text) as string
                : null;
        if (string.IsNullOrWhiteSpace(text)) yield break;

        foreach (var line in text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            yield return line;
    }

    private static bool TryNormalizeWebUrl(string raw, out string normalized)
    {
        normalized = "";
        var trimmed = raw.Trim();
        if (string.IsNullOrWhiteSpace(trimmed)) return false;
        var candidate = trimmed.Contains("://", StringComparison.Ordinal) ? trimmed : $"https://{trimmed}";
        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri)) return false;
        if (uri.Scheme is not ("http" or "https") || string.IsNullOrWhiteSpace(uri.Host)) return false;
        normalized = uri.AbsoluteUri;
        return true;
    }
}
