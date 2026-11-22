using System.Collections.ObjectModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

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

    private ImageSource? _icon;
    public string Path { get; set; } = "";
    public List<WindowRect> Windows { get; set; } = [];
    public string Name => System.IO.Path.GetFileNameWithoutExtension(Path);

    [JsonIgnore]
    public ImageSource? Icon => _icon ??= LoadIcon();

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
}

public static class SettingsStore
{
    private static readonly string FilePath = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PersistentWindows", "profiles.json");
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var data = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath));
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
