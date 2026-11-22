using Microsoft.Win32;
using System.Diagnostics;
using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace PersistentWindows;

public partial class MainWindow : Window
{
    private readonly AppSettings _settings;
    private Profile? _activeProfile;
    private bool _loadingProfiles;

    public MainWindow()
    {
        InitializeComponent();
        _settings = SettingsStore.Load();
        RestoreMainWindowBounds();
        Closing += MainWindow_Closing;
        ProfileCombo.ItemsSource = _settings.Profiles;
        ProfileCombo.SelectedItem = _settings.Profiles.FirstOrDefault(p => p.Name == _settings.SelectedProfile) ?? _settings.Profiles[0];
        RefreshList();
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
}
