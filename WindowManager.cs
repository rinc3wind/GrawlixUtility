using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace PersistentWindows;

internal static class WindowManager
{
    private const uint SwpNoZOrder = 0x0004, SwpNoActivate = 0x0010, SwpShowWindow = 0x0040;
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint WmClose = 0x0010;
    private const int SwRestore = 9, SwMaximize = 3;
    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool IsZoomed(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out Rect rect);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int maxCount);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hwnd, StringBuilder className, int maxCount);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hwnd, int command);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenProcess(uint access, bool inheritHandle, uint processId);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool QueryFullProcessImageName(IntPtr process, uint flags, StringBuilder imageName, ref uint size);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);

    [StructLayout(LayoutKind.Sequential)] private struct WindowPlacement
    {
        public int Length, Flags, ShowCommand;
        public System.Drawing.Point MinPosition, MaxPosition;
        public Rect NormalPosition;
    }
    [DllImport("user32.dll")] private static extern bool GetWindowPlacement(IntPtr hwnd, ref WindowPlacement placement);
    [DllImport("user32.dll")] private static extern bool SetWindowPlacement(IntPtr hwnd, ref WindowPlacement placement);

    private sealed record FoundWindow(IntPtr Handle, string Path, string Title, WindowRect Rect);

    public static async Task CloseAllOtherWindowsAsync(uint currentProcessId, Action<string> status)
    {
        var windowsToClose = new List<IntPtr>();
        EnumWindows((hwnd, _) =>
        {
            if (!IsWindowVisible(hwnd)) return true;
            GetWindowThreadProcessId(hwnd, out var pid);
            if (pid == currentProcessId) return true;
            var className = new StringBuilder(256);
            GetClassName(hwnd, className, className.Capacity);
            if (className.ToString() is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd") return true;
            windowsToClose.Add(hwnd);
            return true;
        }, IntPtr.Zero);

        status($"Closing {windowsToClose.Count} other windows…");
        foreach (var hwnd in windowsToClose) PostMessage(hwnd, WmClose, IntPtr.Zero, IntPtr.Zero);
        for (var attempt = 0; attempt < 40; attempt++)
        {
            if (windowsToClose.All(hwnd => !IsWindow(hwnd) || !IsWindowVisible(hwnd))) break;
            await Task.Delay(125);
        }

        var remaining = windowsToClose.Count(hwnd => IsWindow(hwnd) && IsWindowVisible(hwnd));
        if (remaining > 0) status($"{remaining} window(s) stayed open; restoring layout…");
    }

    private static List<FoundWindow> Enumerate()
    {
        var found = new List<FoundWindow>();
        EnumWindows((hwnd, _) =>
        {
            if (!IsWindowVisible(hwnd)) return true;
            GetWindowThreadProcessId(hwnd, out var pid);
            try
            {
                var path = GetProcessPath(pid);
                if (string.IsNullOrWhiteSpace(path) || !GetWindowRect(hwnd, out var r)) return true;
                var title = new StringBuilder(512);
                GetWindowText(hwnd, title, title.Capacity);
                var maximized = IsZoomed(hwnd);
                var placement = new WindowPlacement { Length = Marshal.SizeOf<WindowPlacement>() };
                if (maximized && GetWindowPlacement(hwnd, ref placement)) r = placement.NormalPosition;
                found.Add(new FoundWindow(hwnd, System.IO.Path.IsPathRooted(path) ? Normalize(path) : path, title.ToString(), new WindowRect { X = r.Left, Y = r.Top, Width = Math.Max(100, r.Right - r.Left), Height = Math.Max(80, r.Bottom - r.Top), Maximized = maximized }));
            }
            catch { }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    private static string? GetProcessPath(uint processId)
    {
        var process = OpenProcess(ProcessQueryLimitedInformation, false, processId);
        if (process != IntPtr.Zero)
        {
            try
            {
                var buffer = new StringBuilder(32768);
                uint size = (uint)buffer.Capacity;
                if (QueryFullProcessImageName(process, 0, buffer, ref size)) return buffer.ToString();
            }
            finally { CloseHandle(process); }
        }

        try
        {
            using var fallback = Process.GetProcessById((int)processId);
            return fallback.ProcessName + ".exe";
        }
        catch { return null; }
    }

    public static Dictionary<string, List<WindowRect>> Capture(IEnumerable<string> paths)
    {
        var entries = paths.Select(p =>
        {
            var isScript = System.IO.Path.GetExtension(p).Equals(".bat", StringComparison.OrdinalIgnoreCase) || System.IO.Path.GetExtension(p).Equals(".cmd", StringComparison.OrdinalIgnoreCase);
            return (Key: Normalize(p), ExpectedPaths: GetExpectedWindowPaths(p), IsScript: isScript);
        }).ToList();
        var result = entries.ToDictionary(e => e.Key, _ => new List<WindowRect>(), StringComparer.OrdinalIgnoreCase);
        var windows = Enumerate();
        foreach (var entry in entries)
        {
            var matches = PreferNamedWindow(windows.Where(w => MatchesExpectedPath(entry.ExpectedPaths, w.Path)).ToList(), System.IO.Path.GetFileNameWithoutExtension(entry.Key));
            if (entry.IsScript) matches = matches.Take(entry.ExpectedPaths.Count).ToList();
            result[entry.Key].AddRange(matches.Select(w => w.Rect));
        }
        return result;
    }

    public static string DescribeExpectedWindowPaths(string path) => string.Join("; ", GetExpectedWindowPaths(path));

    public static async Task RestoreAsync(Profile profile, Action<string> status)
    {
        if (profile.Applications.Count == 0)
        {
            status("This profile has no executables. Add an app first.");
            return;
        }

        var launchedWithoutLayout = new List<string>();
        var windowsNotFound = new List<string>();
        var positioningFailed = new List<string>();
        var restoredWindows = new List<string>();
        foreach (var app in profile.Applications)
        {
            var hasSavedLayout = app.Windows.Count > 0;
            var isScript = IsBatchScript(app.Path);
            status(hasSavedLayout ? $"Restoring {app.Name}…" : $"Opening {app.Name}…");
            var expectedPaths = GetExpectedWindowPaths(app.Path);
            var beforeLaunch = FindProcessWindows(expectedPaths);
            var beforeHandles = beforeLaunch.Select(w => w.Handle).ToHashSet();
            var existing = FindMatchingWindows(app.Path, expectedPaths, requireNamedScriptWindow: isScript);

            if (!hasSavedLayout && existing.Count > 0) continue;

            var targetCount = hasSavedLayout ? app.Windows.Count : 1;
            for (var i = existing.Count; i < targetCount; i++)
            {
                try { Process.Start(new ProcessStartInfo(app.Path) { UseShellExecute = true }); }
                catch (Exception ex) { status($"Could not launch {app.Name}: {ex.Message}"); break; }
                await Task.Delay(900);
            }
            for (var attempt = 0; attempt < 48; attempt++)
            {
                if (isScript)
                {
                    var newlyOpened = FindMatchingWindows(app.Path, expectedPaths, beforeHandles);
                    if (existing.Count + newlyOpened.Count >= targetCount) break;
                }
                else
                {
                    existing = FindMatchingWindows(app.Path, expectedPaths);
                    if (existing.Count >= targetCount) break;
                }
                await Task.Delay(250);
            }
            if (isScript)
                existing = existing.Concat(FindMatchingWindows(app.Path, expectedPaths, beforeHandles)).Take(targetCount).ToList();

            if (!hasSavedLayout)
            {
                if (existing.Count == 0) windowsNotFound.Add(app.Name);
                else launchedWithoutLayout.Add(app.Name);
                continue;
            }

            if (existing.Count < targetCount) windowsNotFound.Add(app.Name);
            for (var i = 0; i < Math.Min(existing.Count, app.Windows.Count); i++)
            {
                var rect = app.Windows[i];
                if (rect.Maximized)
                {
                    var placement = new WindowPlacement
                    {
                        Length = Marshal.SizeOf<WindowPlacement>(),
                        ShowCommand = SwMaximize,
                        NormalPosition = new Rect { Left = rect.X, Top = rect.Y, Right = rect.X + Math.Max(100, rect.Width), Bottom = rect.Y + Math.Max(80, rect.Height) }
                    };
                    if (!SetWindowPlacement(existing[i].Handle, ref placement)) positioningFailed.Add(app.Name);
                }
                else
                {
                    ShowWindow(existing[i].Handle, SwRestore);
                    if (!SetWindowPos(existing[i].Handle, IntPtr.Zero, rect.X, rect.Y, Math.Max(100, rect.Width), Math.Max(80, rect.Height), SwpNoZOrder | SwpNoActivate | SwpShowWindow)) positioningFailed.Add(app.Name);
                }
                await Task.Delay(100);
                if (GetWindowRect(existing[i].Handle, out var actual))
                    restoredWindows.Add($"{app.Name}: {actual.Left},{actual.Top} {actual.Right - actual.Left}x{actual.Bottom - actual.Top}");
            }
        }

        if (windowsNotFound.Count > 0)
        {
            var unresolved = profile.Applications.Where(a => windowsNotFound.Contains(a.Name));
            var targets = unresolved.SelectMany(a => GetExpectedWindowPaths(a.Path)).Distinct(StringComparer.OrdinalIgnoreCase);
            status($"No matching window for {string.Join(", ", windowsNotFound)}. Expected: {string.Join("; ", targets)}");
        }
        else if (positioningFailed.Count > 0)
            status($"Windows were found, but Windows rejected positioning: {string.Join(", ", positioningFailed.Distinct())}");
        else if (launchedWithoutLayout.Count > 0)
            status($"Opened {string.Join(", ", launchedWithoutLayout)}. Save current layout to remember their positions.");
        else if (profile.Applications.All(a => a.Windows.Count == 0))
            status("Apps are already open. Arrange them, then save the layout.");
        else if (restoredWindows.Count > 0)
            status($"Layout restored — {string.Join("; ", restoredWindows)}");
        else
            status("Layout restored");
    }

    private static string Normalize(string path)
    {
        try { return System.IO.Path.GetFullPath(path); } catch { return path; }
    }

    private static List<FoundWindow> FindProcessWindows(List<string> expectedPaths) =>
        Enumerate().Where(w => MatchesExpectedPath(expectedPaths, w.Path)).ToList();

    private static List<FoundWindow> FindMatchingWindows(string applicationPath, List<string> expectedPaths, HashSet<IntPtr>? excludeHandles = null, bool requireNamedScriptWindow = false)
    {
        var isScript = IsBatchScript(applicationPath);
        var matches = FindProcessWindows(expectedPaths);
        if (excludeHandles is not null) matches = matches.Where(w => !excludeHandles.Contains(w.Handle)).ToList();
        var named = matches.Where(w => w.Title.Contains(System.IO.Path.GetFileNameWithoutExtension(applicationPath), StringComparison.OrdinalIgnoreCase)).ToList();
        if (named.Count > 0) matches = named;
        else if (isScript && requireNamedScriptWindow) return [];
        if (isScript)
            return matches.Take(expectedPaths.Count).ToList();
        return matches;
    }

    private static bool IsBatchScript(string path) =>
        System.IO.Path.GetExtension(path).Equals(".bat", StringComparison.OrdinalIgnoreCase) ||
        System.IO.Path.GetExtension(path).Equals(".cmd", StringComparison.OrdinalIgnoreCase);

    private static List<FoundWindow> PreferNamedWindow(List<FoundWindow> windows, string applicationName)
    {
        var named = windows.Where(w => w.Title.Contains(applicationName, StringComparison.OrdinalIgnoreCase)).ToList();
        return named.Count > 0 ? named : windows;
    }

    private static bool MatchesExpectedPath(IEnumerable<string> expectedPaths, string actualPath)
    {
        if (!System.IO.Path.IsPathRooted(actualPath))
            return expectedPaths.Any(p => System.IO.Path.GetFileName(p).Equals(actualPath, StringComparison.OrdinalIgnoreCase));
        var normalizedActual = Normalize(actualPath);
        return expectedPaths.Any(p => Normalize(p).Equals(normalizedActual, StringComparison.OrdinalIgnoreCase));
    }

    private static List<string> GetExpectedWindowPaths(string path)
    {
        var extension = System.IO.Path.GetExtension(path);
        if (extension.Equals(".bat", StringComparison.OrdinalIgnoreCase) || extension.Equals(".cmd", StringComparison.OrdinalIgnoreCase))
            return FindStartedExecutables(path);
        return [GetExpectedWindowPath(path)];
    }

    private static List<string> FindStartedExecutables(string scriptPath)
    {
        var matches = new List<string>();
        var currentDirectory = System.IO.Path.GetDirectoryName(Normalize(scriptPath)) ?? Environment.CurrentDirectory;
        try
        {
            foreach (var rawLine in File.ReadLines(scriptPath))
            {
                var line = rawLine.Trim();
                if (line.StartsWith('@')) line = line[1..].TrimStart();

                var cdMatch = Regex.Match(line, "^(?:cd|chdir)\\s+(?:/d\\s+)?(?:\"(?<quoted>[^\"]+)\"|(?<plain>.+))$", RegexOptions.IgnoreCase);
                if (cdMatch.Success)
                {
                    var directory = Environment.ExpandEnvironmentVariables(cdMatch.Groups["quoted"].Success ? cdMatch.Groups["quoted"].Value : cdMatch.Groups["plain"].Value.Trim());
                    if (!System.IO.Path.IsPathRooted(directory)) directory = System.IO.Path.Combine(currentDirectory, directory);
                    try { directory = Normalize(directory); } catch { }
                    if (Directory.Exists(directory)) currentDirectory = directory;
                    continue;
                }

                var startMatch = Regex.Match(line, "^start\\b(?<tail>.*)$", RegexOptions.IgnoreCase);
                if (!startMatch.Success) continue;
                var command = GetStartCommand(startMatch.Groups["tail"].Value);
                if (string.IsNullOrWhiteSpace(command) || !System.IO.Path.GetExtension(command).Equals(".exe", StringComparison.OrdinalIgnoreCase)) continue;
                command = Environment.ExpandEnvironmentVariables(command);
                var executable = System.IO.Path.IsPathRooted(command) ? command : System.IO.Path.Combine(currentDirectory, command);
                if (!File.Exists(executable)) executable = FindOnPath(command) ?? executable;
                if (File.Exists(executable)) matches.Add(Normalize(executable));
            }
        }
        catch { }
        return matches;
    }

    private static string GetStartCommand(string tail)
    {
        tail = tail.TrimStart();
        while (tail.StartsWith('/'))
        {
            var space = tail.IndexOfAny([' ', '\t']);
            if (space < 0) return "";
            var option = tail[..space];
            tail = tail[(space + 1)..].TrimStart();
            if (option.Equals("/d", StringComparison.OrdinalIgnoreCase) && tail.Length > 0)
            {
                if (tail[0] == '"')
                {
                    var end = tail.IndexOf('"', 1);
                    if (end >= 0) tail = tail[(end + 1)..].TrimStart();
                }
                else
                {
                    var end = tail.IndexOfAny([' ', '\t']);
                    tail = end < 0 ? "" : tail[(end + 1)..].TrimStart();
                }
            }
        }

        if (tail.Length == 0) return "";
        if (tail[0] == '"')
        {
            var end = tail.IndexOf('"', 1);
            if (end < 0) return "";
            var first = tail[1..end];
            if (first.Length > 0 && System.IO.Path.GetExtension(first).Equals(".exe", StringComparison.OrdinalIgnoreCase)) return first;
            tail = tail[(end + 1)..].TrimStart(); // start treats a first quoted argument as the window title
            if (tail.Length == 0) return "";
            if (tail[0] == '"')
            {
                end = tail.IndexOf('"', 1);
                return end > 0 ? tail[1..end] : "";
            }
        }
        var separator = tail.IndexOfAny([' ', '\t']);
        return separator < 0 ? tail : tail[..separator];
    }

    private static string? FindOnPath(string fileName)
    {
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(System.IO.Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = System.IO.Path.Combine(directory.Trim('"'), fileName);
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    private static string GetExpectedWindowPath(string path)
    {
        if (!System.IO.Path.GetExtension(path).Equals(".lnk", StringComparison.OrdinalIgnoreCase)) return Normalize(path);
        object? shell = null;
        object? shortcut = null;
        try
        {
            var shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType is null) return Normalize(path);
            shell = Activator.CreateInstance(shellType);
            if (shell is null) return Normalize(path);
            dynamic automation = shell;
            shortcut = automation.CreateShortcut(path);
            dynamic link = shortcut;
            string target = link.TargetPath;
            return string.IsNullOrWhiteSpace(target) ? Normalize(path) : Normalize(target);
        }
        catch { return Normalize(path); }
        finally
        {
            if (shortcut is not null && Marshal.IsComObject(shortcut)) Marshal.FinalReleaseComObject(shortcut);
            if (shell is not null && Marshal.IsComObject(shell)) Marshal.FinalReleaseComObject(shell);
        }
    }
}
