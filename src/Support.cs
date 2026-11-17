using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32.SafeHandles;

namespace PlugSafe;

static class Theme
{
    public static readonly Color Bg = Color.FromArgb(28, 28, 31);
    public static readonly Color Surface = Color.FromArgb(40, 40, 44);
    public static readonly Color SurfaceHover = Color.FromArgb(50, 50, 55);
    public static readonly Color Pressed = Color.FromArgb(68, 68, 74);
    public static readonly Color Thumb = Color.FromArgb(58, 58, 64);
    public static readonly Color Border = Color.FromArgb(64, 64, 70);
    public static readonly Color Text = Color.FromArgb(242, 242, 245);
    public static readonly Color Muted = Color.FromArgb(150, 150, 160);
    public static readonly Color Accent = Color.FromArgb(10, 132, 255);
    public static readonly Color AccentHover = Color.FromArgb(64, 156, 255);
    public static readonly Color AccentDown = Color.FromArgb(0, 100, 210);
    public static readonly Color Success = Color.FromArgb(72, 199, 116);
    public static readonly Color Warning = Color.FromArgb(255, 196, 92);
    public static readonly Color Error = Color.FromArgb(255, 112, 102);

    static readonly string IconFamily =
        FontFamily.Families.Any(f => f.Name == "Segoe Fluent Icons") ? "Segoe Fluent Icons" : "Segoe MDL2 Assets";

    public static readonly Font Ui = new("Segoe UI", 9.75f);
    public static readonly Font UiSmall = new("Segoe UI", 8.75f);
    public static readonly Font UiBold = new("Segoe UI Semibold", 9.75f);
    public static readonly Font UiSmallBold = new("Segoe UI Semibold", 8.75f);
    public static readonly Font UiLarge = new("Segoe UI Semibold", 11f);
    public static readonly Font Icons = new(IconFamily, 10f);
    public static readonly Font IconsSmall = new(IconFamily, 8.5f);
    public static readonly Font IconsLarge = new(IconFamily, 16f);

    const TextFormatFlags TextFlags = TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine |
        TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding;
    const TextFormatFlags GlyphFlags = TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine |
        TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding;

    public static GraphicsPath RoundRect(RectangleF r, float radius)
    {
        var path = new GraphicsPath();
        float d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
        if (d < 1)
        {
            path.AddRectangle(r);
            return path;
        }
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    public static void FillRound(Graphics g, Color color, RectangleF r, float radius)
    {
        using var brush = new SolidBrush(color);
        using var path = RoundRect(r, radius);
        g.FillPath(brush, path);
    }

    public static void DrawText(Graphics g, string text, Font font, Rectangle r, Color color,
        TextFormatFlags extra = TextFormatFlags.Default) =>
        TextRenderer.DrawText(g, text, font, r, color, TextFlags | extra);

    public static int TextWidth(Graphics g, string text, Font font) =>
        TextRenderer.MeasureText(g, text, font, Size.Empty, TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix).Width;

    public static void DrawGlyph(Graphics g, string glyph, Rectangle r, Color color, Font? font = null) =>
        TextRenderer.DrawText(g, glyph, font ?? Icons, r, color, GlyphFlags);
}

static class Glyphs
{
    public const string Folder = "\uE8B7", File = "\uE8A5", Settings = "\uE713", Close = "\uE8BB", Check = "\uE73E",
        Error = "\uE783", Warning = "\uE7BA", Add = "\uE710", More = "\uE712", Usb = "\uE88E", Shield = "\uEA18",
        OpenFile = "\uE8A7", Stop = "\uE71A";
}

static class Native
{
    public const int WM_NCLBUTTONDOWN = 0x00A1, HTCAPTION = 2;
    public const int WM_DEVICECHANGE = 0x0219, DBT_DEVICEARRIVAL = 0x8000, DBT_DEVICEREMOVECOMPLETE = 0x8004, DBT_DEVTYP_VOLUME = 2;
    public const int WM_POWERBROADCAST = 0x0218, PBT_APMRESUMESUSPEND = 0x7, PBT_APMRESUMEAUTOMATIC = 0x12;
    public const int CS_DROPSHADOW = 0x00020000, WS_EX_TOOLWINDOW = 0x00000080, WS_EX_TOPMOST = 0x00000008, WS_EX_NOACTIVATE = 0x08000000;

    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool AllowSetForegroundWindow(int dwProcessId);
    [DllImport("user32.dll")] public static extern bool ReleaseCapture();
    [DllImport("user32.dll")] public static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
    [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)] public static extern int SetWindowTheme(IntPtr hwnd, string appName, string? idList);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool GetVolumeInformationW(string root, StringBuilder? name, int nameSize, out uint serial,
        out uint maxComponent, out uint flags, StringBuilder? fsName, int fsNameSize);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool DeviceIoControl(SafeFileHandle device, uint code, byte[] input, int inputSize, byte[] output, int outputSize,
        out int returned, IntPtr overlapped);

    /// <summary>Windows 11 rounded corners + subtle border on a borderless window (no-op on Windows 10).</summary>
    public static void StyleWindow(IntPtr hwnd)
    {
        int round = 2; // DWMWCP_ROUND
        DwmSetWindowAttribute(hwnd, 33, ref round, sizeof(int));
        int border = ColorTranslator.ToWin32(Theme.Border);
        DwmSetWindowAttribute(hwnd, 34, ref border, sizeof(int));
    }

    /// <summary>The volume serial number: changes when a drive is formatted, so it pins down one specific drive.</summary>
    public static uint? VolumeSerial(string root) =>
        GetVolumeInformationW(root, null, 0, out var serial, out _, out _, null, 0) ? serial : null;

    public const int BusUsb = 7, BusSd = 12, BusMmc = 13;

    /// <summary>How a drive is attached (USB, SD, NVMe…) and its product name, straight from the storage driver.</summary>
    public static (int Bus, string Product)? StorageInfo(char letter)
    {
        using var device = CreateFileW($@"\\.\{letter}:", 0, 3 /* share read+write */, IntPtr.Zero, 3 /* OPEN_EXISTING */, 0, IntPtr.Zero);
        if (device.IsInvalid) return null;
        var query = new byte[12]; // StorageDeviceProperty, PropertyStandardQuery
        var output = new byte[1024];
        if (!DeviceIoControl(device, 0x002D1400 /* IOCTL_STORAGE_QUERY_PROPERTY */, query, query.Length, output, output.Length, out _, IntPtr.Zero))
            return null;
        int bus = BitConverter.ToInt32(output, 28);
        string vendor = Ascii(output, BitConverter.ToInt32(output, 12));
        string product = Ascii(output, BitConverter.ToInt32(output, 16));
        return (bus, $"{vendor} {product}".Trim());
    }

    static string Ascii(byte[] buffer, int offset)
    {
        if (offset <= 0 || offset >= buffer.Length) return "";
        int end = Array.IndexOf(buffer, (byte)0, offset);
        return Encoding.ASCII.GetString(buffer, offset, (end < 0 ? buffer.Length : end) - offset).Trim();
    }
}

enum StartMode { Countdown, Immediately, Ask }

sealed class BackupItem
{
    public string Path { get; set; } = "";
    public bool IsFolder { get; set; }
    /// <summary>Folder name inside the backup (fixed when added, so it never moves around).</summary>
    public string Name { get; set; } = "";
}

sealed class BackupDrive
{
    public string Id { get; set; } = "";     // also written to the drive's PlugSafe\drive-id.txt
    public uint Serial { get; set; }          // volume serial number: both must match before anything is written
    public string Name { get; set; } = "";
    public long TotalBytes { get; set; }
    public List<BackupItem> Items { get; set; } = new();
    public DateTime? Added { get; set; }
    public DateTime? LastReminder { get; set; }
    public DateTime? LastBackup { get; set; }
    public string? LastSummary { get; set; }
    public List<string> LastProblems { get; set; } = new();
}

sealed class Config
{
    public List<BackupDrive> Drives { get; set; } = new();
    public StartMode Mode { get; set; } = StartMode.Countdown;
    public int CountdownSeconds { get; set; } = 5;
    /// <summary>Remind me when a drive hasn't been backed up to for this many days (0 = never).</summary>
    public int ReminderDays { get; set; } = 7;
    /// <summary>Folders with these names are never backed up, wherever they are (build output, caches…).</summary>
    public List<string> SkippedFolders { get; set; } = DefaultSkipped.ToList();
    /// <summary>Weekly check of the GitHub releases; off unless the user turned it on.</summary>
    public bool UpdateCheck { get; set; }
    public DateTime? LastUpdateCheck { get; set; }

    public static readonly string[] DefaultSkipped =
        { "node_modules", ".git", ".svn", ".vs", "__pycache__", ".venv", ".gradle", ".next", ".cache" };

    public HashSet<string> SkipSet() => new(SkippedFolders, StringComparer.OrdinalIgnoreCase);

    static readonly JsonSerializerOptions Json = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };
    static string FilePath => System.IO.Path.Combine(AppContext.BaseDirectory, "config.json");

    public static Config Load()
    {
        foreach (var path in new[] { FilePath, FilePath + ".bak" })
        {
            try
            {
                if (File.Exists(path) && JsonSerializer.Deserialize<Config>(File.ReadAllText(path), Json) is { } config)
                    return config;
            }
            catch (Exception ex) { Log.Write($"Couldn't read {System.IO.Path.GetFileName(path)}: {ex.Message}"); }
        }
        return new Config();
    }

    /// <summary>Write-then-swap, keeping the previous file as .bak, so a crash can't leave a half-written config.</summary>
    public void Save()
    {
        try
        {
            string temp = FilePath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(this, Json));
            if (File.Exists(FilePath)) File.Replace(temp, FilePath, FilePath + ".bak");
            else File.Move(temp, FilePath);
        }
        catch (Exception ex) { Log.Write("Couldn't save config: " + ex.Message); }
    }
}

/// <summary>Starts the app at sign-in through a per-user scheduled task (more reliable than the Run key on this PC).</summary>
static class Startup
{
    const string TaskName = "PlugSafe";
    static bool? cached;

    static string ExePath => Environment.ProcessPath ?? Application.ExecutablePath;

    public static bool IsEnabled => cached ??= Schtasks("/Query", "/TN", TaskName) == 0;

    public static void Set(bool enabled)
    {
        cached = null;
        if (!enabled)
        {
            Schtasks("/Delete", "/TN", TaskName, "/F");
            Log.Write("Startup: task removed");
            return;
        }
        var xml = Path.Combine(Path.GetTempPath(), $"plugsafe-task-{Environment.ProcessId}.xml");
        File.WriteAllText(xml, TaskXml(), Encoding.Unicode);
        try
        {
            int code = Schtasks("/Create", "/TN", TaskName, "/XML", xml, "/F");
            Log.Write($"Startup: task created (schtasks exit {code})");
            if (code != 0) throw new InvalidOperationException($"Task Scheduler refused the task (exit code {code}).");
        }
        finally { File.Delete(xml); }
    }

    static int Schtasks(params string[] args)
    {
        var psi = new ProcessStartInfo("schtasks.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var arg in args) psi.ArgumentList.Add(arg);
        using var p = Process.Start(psi)!;
        var stderr = p.StandardError.ReadToEndAsync();
        p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0 && args[0] != "/Query") Log.Write($"schtasks {args[0]}: {stderr.Result.Trim()}");
        return p.ExitCode;
    }

    static string TaskXml()
    {
        string user = SecurityElement.Escape(WindowsIdentity.GetCurrent().Name)!;
        string exe = SecurityElement.Escape(ExePath)!;
        string dir = SecurityElement.Escape(Path.GetDirectoryName(ExePath)!)!;
        // No battery restrictions and no time limit: the defaults would skip it on battery or kill it after 3 days.
        return $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <RegistrationInfo>
                <Description>Starts PlugSafe in the tray when you sign in, so it can back up your USB drive when you plug it in.</Description>
              </RegistrationInfo>
              <Triggers>
                <LogonTrigger>
                  <Enabled>true</Enabled>
                  <UserId>{user}</UserId>
                  <Delay>PT5S</Delay>
                </LogonTrigger>
              </Triggers>
              <Principals>
                <Principal id="Author">
                  <UserId>{user}</UserId>
                  <LogonType>InteractiveToken</LogonType>
                  <RunLevel>LeastPrivilege</RunLevel>
                </Principal>
              </Principals>
              <Settings>
                <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
                <AllowHardTerminate>true</AllowHardTerminate>
                <StartWhenAvailable>true</StartWhenAvailable>
                <IdleSettings>
                  <StopOnIdleEnd>false</StopOnIdleEnd>
                  <RestartOnIdle>false</RestartOnIdle>
                </IdleSettings>
                <Priority>5</Priority>
              </Settings>
              <Actions Context="Author">
                <Exec>
                  <Command>"{exe}"</Command>
                  <Arguments>--background</Arguments>
                  <WorkingDirectory>{dir}</WorkingDirectory>
                </Exec>
              </Actions>
            </Task>
            """;
    }
}

/// <summary>Small rolling log next to the exe (plugsafe.log).</summary>
static class Log
{
    static readonly object Gate = new();

    static string FilePath => Path.Combine(AppContext.BaseDirectory, "plugsafe.log");

    public static void Write(string message)
    {
        try
        {
            lock (Gate)
            {
                var file = new FileInfo(FilePath);
                if (file.Exists && file.Length > 512 * 1024) File.Move(FilePath, FilePath + ".old", overwrite: true);
                File.AppendAllText(FilePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {message}{Environment.NewLine}");
            }
        }
        catch { /* logging must never break the app */ }
    }
}

static class Shell
{
    public static void OpenFolder(string folder)
    {
        try
        {
            if (Directory.Exists(folder)) Process.Start("explorer.exe", $"\"{folder.TrimEnd('\\')}\"");
        }
        catch { }
    }

    public static void ShowInFolder(string path)
    {
        try { Process.Start("explorer.exe", $"/select,\"{path}\""); } catch { }
    }

    /// <summary>Opens a file with its usual app (Word, Photos…). False if it's gone or nothing can open it.</summary>
    public static bool OpenFile(string file)
    {
        try
        {
            if (!File.Exists(file)) return false;
            Process.Start(new ProcessStartInfo(file) { UseShellExecute = true });
            return true;
        }
        catch { return false; }
    }
}

static class AppIcon
{
    public static Icon Get(int size)
    {
        using var stream = typeof(AppIcon).Assembly.GetManifestResourceStream("PlugSafe.icon.ico");
        return stream != null ? new Icon(stream, size, size) : (Icon)SystemIcons.Application.Clone();
    }
}

static class Fmt
{
    public static string Size(double bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        int i = 0;
        while (bytes >= 1024 && i < units.Length - 1) { bytes /= 1024; i++; }
        return i == 0 ? $"{bytes:0} B" : $"{bytes:0.0} {units[i]}";
    }

    public static string Count(int n, string word) => $"{n:N0} {word}{(n == 1 ? "" : "s")}";

    /// <summary>"today 14:02", "yesterday 09:10", "3 Nov 14:02".</summary>
    public static string When(DateTime time)
    {
        var day = time.Date;
        if (day == DateTime.Today) return $"today {time:HH:mm}";
        if (day == DateTime.Today.AddDays(-1)) return $"yesterday {time:HH:mm}";
        return time.ToString("d MMM HH:mm");
    }
}

/// <summary>Keeps the idle tray app as close to free as possible.</summary>
static class Efficiency
{
    [StructLayout(LayoutKind.Sequential)]
    struct PowerThrottlingState { public uint Version, ControlMask, StateMask; }

    [DllImport("kernel32.dll")]
    static extern bool SetProcessInformation(IntPtr process, int infoClass, ref PowerThrottlingState info, int size);
    [DllImport("psapi.dll")]
    static extern bool EmptyWorkingSet(IntPtr process);

    static readonly IntPtr CurrentProcess = new(-1);

    /// <summary>Windows "efficiency mode" (EcoQoS): when on, Windows runs the app on efficient cores at low clocks.</summary>
    public static void SetIdle(bool idle)
    {
        var state = new PowerThrottlingState { Version = 1, ControlMask = 1, StateMask = idle ? 1u : 0 };
        SetProcessInformation(CurrentProcess, 4 /* ProcessPowerThrottling */, ref state, Marshal.SizeOf<PowerThrottlingState>());
    }

    /// <summary>Releases memory the hidden app isn't using.</summary>
    public static void TrimMemory()
    {
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        EmptyWorkingSet(CurrentProcess);
    }
}
