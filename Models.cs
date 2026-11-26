using System.Collections.ObjectModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Net.Http;
using System.Windows.Threading;

namespace PersistentWindows;

public sealed class WindowRect
{
    public int X { get; set; }
    public int Y { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public bool Maximized { get; set; }
}

public sealed class ExecutableEntry
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShellFileInfo
    {
        public IntPtr IconHandle;
        public int IconIndex;
        public uint Attributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string DisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string TypeName;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(string path, uint fileAttributes, out ShellFileInfo info, uint infoSize, uint flags);
    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr handle);
    private const uint ShgfiIcon = 0x100;

    public string Path { get; set; } = "";
    public List<WindowRect> Windows { get; set; } = [];
    public string Name => System.IO.Path.GetFileNameWithoutExtension(Path);

    [JsonIgnore]
    public ImageSource? Icon => field ??= LoadIcon();

    private ImageSource? LoadIcon()
    {
        IntPtr iconHandle = IntPtr.Zero;
        try
        {
            var info = new ShellFileInfo();
            if (SHGetFileInfo(Path, 0, out info, (uint)Marshal.SizeOf<ShellFileInfo>(), ShgfiIcon) == IntPtr.Zero) return null;
            iconHandle = info.IconHandle;
            var source = Imaging.CreateBitmapSourceFromHIcon(iconHandle, Int32Rect.Empty, BitmapSizeOptions.FromWidthAndHeight(32, 32));
            source.Freeze();
            return source;
        }
        catch { return null; }
        finally { if (iconHandle != IntPtr.Zero) DestroyIcon(iconHandle); }
    }
}

public sealed class WebAppEntry : System.ComponentModel.INotifyPropertyChanged
{
    private static readonly HttpClient HttpClient = new() { Timeout = TimeSpan.FromSeconds(8) };
    private static readonly string IconCacheDirectory = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PersistentWindows", "favicon-cache");
    private static readonly string TitleCacheDirectory = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PersistentWindows", "title-cache");
    private ImageSource? _icon;
    private bool _iconLoaded;
    private string? _pageTitle;
    private bool _titleLoaded;

    public string Url { get; set; } = "";

    [JsonIgnore]
    public ImageSource? Icon
    {
        get
        {
            if (_iconLoaded) return _icon;
            _iconLoaded = true;
            _icon = LoadCachedIcon();
            if (_icon is null) _ = LoadIconAsync();
            return _icon;
        }
    }

    [JsonIgnore]
    public string DisplayTitle
    {
        get
        {
            if (!_titleLoaded)
            {
                _titleLoaded = true;
                _pageTitle = LoadCachedTitle();
                if (_pageTitle is null) _ = LoadPageTitleAsync();
            }
            return string.IsNullOrWhiteSpace(_pageTitle) ? GetFallbackTitle() : _pageTitle;
        }
    }

    private ImageSource? LoadCachedIcon()
    {
        try
        {
            var path = GetIconCachePath();
            if (!File.Exists(path)) return null;
            using var stream = File.OpenRead(path);
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = stream;
            image.DecodePixelWidth = 32;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch { return null; }
    }

    private async Task LoadIconAsync()
    {
        if (!Uri.TryCreate(Url, UriKind.Absolute, out var pageUri)) return;
        try
        {
            var candidates = new[]
            {
                new Uri(pageUri, "/favicon.ico"),
                new Uri(pageUri, "/favicon.png")
            };
            foreach (var iconUri in candidates)
            {
                try
                {
                    using var response = await HttpClient.GetAsync(iconUri, HttpCompletionOption.ResponseHeadersRead);
                    if (!response.IsSuccessStatusCode) continue;
                    var bytes = await response.Content.ReadAsByteArrayAsync();
                    using var stream = new MemoryStream(bytes);
                    var image = new BitmapImage();
                    image.BeginInit();
                    image.CacheOption = BitmapCacheOption.OnLoad;
                    image.StreamSource = stream;
                    image.DecodePixelWidth = 32;
                    image.EndInit();
                    image.Freeze();
                    Directory.CreateDirectory(IconCacheDirectory);
                    await File.WriteAllBytesAsync(GetIconCachePath(), bytes);
                    _icon = image;
                    var dispatcher = Application.Current?.Dispatcher;
                    if (dispatcher is not null && !dispatcher.CheckAccess())
                        await dispatcher.InvokeAsync(() => PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(Icon))));
                    else
                        PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(Icon)));
                    return;
                }
                catch { }
            }
        }
        catch { }
    }

    private string GetIconCachePath()
    {
        var hostKey = Uri.TryCreate(Url, UriKind.Absolute, out var uri) ? uri.Host : Url;
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(hostKey.ToLowerInvariant())));
        return System.IO.Path.Combine(IconCacheDirectory, hash + ".img");
    }

    private string GetFallbackTitle()
    {
        if (!Uri.TryCreate(Url, UriKind.Absolute, out var uri)) return Url;
        var path = uri.AbsolutePath.Trim('/');
        return string.IsNullOrEmpty(path) ? uri.Host : $"{uri.Host}/{path}";
    }

    private string GetTitleCachePath()
    {
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(Url)));
        return System.IO.Path.Combine(TitleCacheDirectory, hash + ".txt");
    }

    private string? LoadCachedTitle()
    {
        try
        {
            var path = GetTitleCachePath();
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
        catch { return null; }
    }

    private async Task LoadPageTitleAsync()
    {
        if (!Uri.TryCreate(Url, UriKind.Absolute, out var uri)) return;
        try
        {
            using var response = await HttpClient.GetAsync(uri);
            response.EnsureSuccessStatusCode();
            var html = await response.Content.ReadAsStringAsync();
            var match = System.Text.RegularExpressions.Regex.Match(html, "<title\\b[^>]*>(.*?)</title\\s*>", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Singleline);
            if (!match.Success) return;
            var title = System.Net.WebUtility.HtmlDecode(System.Text.RegularExpressions.Regex.Replace(match.Groups[1].Value, "<[^>]+>", " "));
            title = System.Text.RegularExpressions.Regex.Replace(title, "\\s+", " ").Trim();
            if (string.IsNullOrWhiteSpace(title)) return;
            _pageTitle = title;
            Directory.CreateDirectory(TitleCacheDirectory);
            await File.WriteAllTextAsync(GetTitleCachePath(), title);
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher is not null && !dispatcher.CheckAccess())
                await dispatcher.InvokeAsync(() => PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(DisplayTitle))));
            else
                PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(DisplayTitle)));
        }
        catch { }
    }

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
}

public sealed class Profile
{
    public string Name { get; set; } = "Default";
    public List<ExecutableEntry> Applications { get; set; } = [];
}

public sealed class AppSettings
{
    public string SelectedProfile { get; set; } = "Default";
    public WindowRect? MainWindowBounds { get; set; }
    public List<Profile> Profiles { get; set; } = [new Profile()];
    public string ChromePath { get; set; } = "";
    public List<WebAppEntry> WebApps { get; set; } = [];
}

public static class SettingsStore
{
    private static readonly string FilePath = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PersistentWindows", "profiles.json");
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        RespectNullableAnnotations = true
    };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var data = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), JsonOptions);
                if (data?.Profiles is { Count: > 0 }) return data;
            }
        }
        catch { }
        return new AppSettings();
    }

    public static void Save(AppSettings settings)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(settings, JsonOptions));
    }
}
