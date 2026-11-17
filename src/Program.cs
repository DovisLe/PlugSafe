using System.Drawing.Imaging;

namespace PlugSafe;

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        Application.SetHighDpiMode(HighDpiMode.SystemAware);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        ToolStripManager.Renderer = new DarkRenderer(); // dark submenus too

        int preview = Array.IndexOf(args, "--preview");
        if (preview >= 0 && preview + 1 < args.Length) { Preview.Render(args[preview + 1]); return; }
        int selfTest = Array.IndexOf(args, "--selftest");
        if (selfTest >= 0 && selfTest + 1 < args.Length) { SelfTest.Run(args[selfTest + 1]); return; }
        int register = Array.IndexOf(args, "--dev-register");
        if (register >= 0 && register + 2 < args.Length) { SelfTest.Register(args[register + 1], args[register + 2]); return; }
        // Used by the installer: PlugSafe.exe --startup on|off
        int startup = Array.IndexOf(args, "--startup");
        if (startup >= 0 && startup + 1 < args.Length)
        {
            try { Startup.Set(args[startup + 1].Equals("on", StringComparison.OrdinalIgnoreCase)); }
            catch (Exception ex) { Log.Write("--startup failed: " + ex.Message); Environment.ExitCode = 1; }
            return;
        }

        // Used by the installer: PlugSafe.exe --updates on|off (the weekly update check is off unless the user says yes).
        int updates = Array.IndexOf(args, "--updates");
        if (updates >= 0 && updates + 1 < args.Length)
        {
            var saved = Config.Load();
            saved.UpdateCheck = args[updates + 1].Equals("on", StringComparison.OrdinalIgnoreCase);
            saved.Save();
            return;
        }
        int updateTest = Array.IndexOf(args, "--selftest-update");
        if (updateTest >= 0 && updateTest + 3 < args.Length)
        {
            SelfTest.Update(args[updateTest + 1], args[updateTest + 2], args[updateTest + 3]);
            return;
        }

        // Started by the updater: let the old copy exit first, so this one can take over.
        int after = Array.IndexOf(args, "--after-update");
        bool updated = after >= 0 && after + 1 < args.Length && int.TryParse(args[after + 1], out int oldPid) && Updater.WaitForExit(oldPid);

        using var mutex = new Mutex(true, @"Local\PlugSafe.Instance", out bool first);
        using var showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\PlugSafe.Show");
        if (!first)
        {
            Native.AllowSetForegroundWindow(-1); // let the running instance take focus
            showSignal.Set();
            return;
        }

        Log.Write($"Started (pid {Environment.ProcessId}, args: {string.Join(' ', args)})");
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => Log.Write("ERROR " + e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Write("CRASH " + e.ExceptionObject);

        Updater.CleanUp(); // the previous exe an update left behind
        using var app = new TrayApp(args.Contains("--background") || updated, showSignal, updated);
        Application.Run(app);
        Log.Write("Stopped");
    }
}

/// <summary>Dev aid: renders the windows to PNGs so they can be checked without clicking through them.</summary>
static class Preview
{
    public static void Render(string dir)
    {
        Directory.CreateDirectory(dir);
        // Made-up files and names only, so screenshots never show the real user, PC or folders.
        Drives.ComputerNameOverride = "MY-PC";
        string demoRoot = Path.Combine(dir, "demo-c");
        string docs = Path.Combine(demoRoot, "Users", "Alex", "Documents"), desk = Path.Combine(demoRoot, "Users", "Alex", "Desktop");
        foreach (var (folder, name, kb) in new[]
                 {
                     (docs, "Lab report.docx", 840), (docs, "Notes.txt", 12), (Path.Combine(docs, "Photos"), "Trip.jpg", 4200),
                     (Path.Combine(docs, "Photos"), "Team.jpg", 3100), (desk, "Project plan.xlsx", 260),
                 })
        {
            Directory.CreateDirectory(folder);
            File.WriteAllBytes(Path.Combine(folder, name), new byte[kb * 1024]);
        }
        string ShowAsC(string path) => path.StartsWith(demoRoot, StringComparison.OrdinalIgnoreCase) ? "C:" + path[demoRoot.Length..] : path;
        var config = new Config
        {
            Drives =
            {
                new BackupDrive
                {
                    Id = "demo1", Name = "Backup stick", TotalBytes = 128_000_000_000, LastBackup = DateTime.Now.AddHours(-3),
                    LastSummary = "3 new  ·  9 updated  ·  1 older version kept",
                    Items =
                    {
                        new BackupItem { Path = docs, IsFolder = true, Name = "Documents" },
                        new BackupItem { Path = Path.Combine(desk, "Project plan.xlsx"), Name = "Project plan.xlsx" },
                        new BackupItem { Path = @"C:\Old stuff\thesis-final-v3.docx", Name = "thesis-final-v3.docx" },
                    },
                },
                new BackupDrive { Id = "demo2", Name = "Spare USB 64 GB", TotalBytes = 64_000_000_000 },
            },
        };
        using var menu = new ContextMenuStrip();
        using (var main = new MainForm(config, menu, _ => false))
        {
            main.DemoConnection = d => d.Id == "demo1" ? new DriveCandidate('E', d.Name, "", 128_000_000_000, 101_700_000_000, "NTFS", 1) : null;
            main.DemoDisplayPath = ShowAsC;
            main.Location = new Point(-30000, -30000);
            main.Show();
            main.RefreshForPreview(); // now visible: starts the background size counting
            Snapshot(main, Path.Combine(dir, "main.png"), wait: 3);
        }
        using (var empty = new MainForm(new Config(), menu, _ => false))
            Snapshot(empty, Path.Combine(dir, "main-empty.png"));

        // The window in the middle of a backup: overall progress on the card, per-item progress in the list.
        using (var busy = new MainForm(config, menu, _ => false))
        {
            busy.DemoConnection = d => d.Id == "demo1" ? new DriveCandidate('E', d.Name, "", 128_000_000_000, 101_700_000_000, "NTFS", 1) : null;
            busy.DemoDisplayPath = ShowAsC;
            busy.Location = new Point(-30000, -30000);
            busy.Show();
            var drive = config.Drives[0];
            var demoScan = new ScanResult { Total = 40, ChangedBytes = 190_600_000 };
            demoScan.Changed.Add(new WorkItem("Trip video.mp4", "a", 100_000_000, docs));
            demoScan.Changed.Add(new WorkItem("Lab report.docx", "b", 50_000_000, docs));
            demoScan.Changed.Add(new WorkItem("c.pdf", "c", 40_000_000, docs));
            demoScan.Changed.Add(new WorkItem("Project plan.xlsx", "d", 600_000, drive.Items[1].Path));
            busy.BackupStarted(drive, demoScan);
            busy.ReportProgress(drive, new BackupProgress(1, 4, 100_000_000, 190_600_000, "Trip video.mp4", 0, 100_000_000, FileStatus.Added));
            Thread.Sleep(100);
            busy.ReportProgress(drive, new BackupProgress(1, 4, 120_000_000, 190_600_000, "Lab report.docx", 1, 20_000_000));
            Snapshot(busy, Path.Combine(dir, "main-backing-up.png"), wait: 2);
        }

        var scan = new ScanResult { Total = 37, Unchanged = 25, ChangedBytes = 356_000_000 };
        for (int i = 0; i < 12; i++) scan.Changed.Add(new WorkItem("x", "y", 1));
        Popup("popup-checking.png", p => p.ShowChecking());
        Popup("popup-countdown.png", p => _ = p.AskToStart(scan, StartMode.Countdown, 5));
        Popup("popup-running.png", p =>
        {
            p.ShowRunning(scan);
            p.Report(new BackupProgress(4, 12, 140_000_000, 356_000_000, "Squad roster 2026.xlsx"));
        });
        var done = new BackupResult { Added = 3, Updated = 9, VersionsKept = 2, Unchanged = 25 };
        Popup("popup-done.png", p => p.ShowDone(done, () => { }, () => { }));
        var problems = new BackupResult { Added = 3, Updated = 7, Skipped = 2, Unchanged = 25 };
        problems.Problems.Add("x");
        Popup("popup-problems.png", p => p.ShowDone(problems, () => { }, () => { }));
        Popup("popup-lowspace.png", p => p.ShowDone(done, () => { }, () => { }, "Safe to unplug. Drive almost full: 2.1 GB left."));
        Popup("popup-reminder.png", p => p.ShowMessage("Time for a backup", "Your last backup was 9 days ago.",
            Theme.Warning, Glyphs.Usb, detail: "Plug the drive in and PlugSafe does the rest."));

        // Clean-up window, pointed at a small fake drive folder with a few version days.
        string fake = Path.Combine(dir, "fake-usb", "PlugSafe"), versions = Path.Combine(fake, Drives.ComputerName, "Versions");
        foreach (var (day, kb) in new[] { ("2025-09-02", 9000), ("2026-03-14", 24000), ("2026-10-01", 3000), ("2026-11-10 14-20", 1200) })
        {
            Directory.CreateDirectory(Path.Combine(versions, day));
            File.WriteAllBytes(Path.Combine(versions, day, "file.bin"), new byte[kb * 1024]);
        }
        uint serial = Native.VolumeSerial(Path.GetPathRoot(Path.GetFullPath(dir))!)!.Value;
        using (var cleanup = new CleanupForm("Backup stick (E:)", versions, new TargetGuard(fake, serial, Array.Empty<string>())))
            Snapshot(cleanup, Path.Combine(dir, "cleanup.png"), wait: 1);
        using (var skipForm = new SkipForm(Config.DefaultSkipped))
            Snapshot(skipForm, Path.Combine(dir, "skipped.png"));

        void Popup(string name, Action<BackupPopup> setup)
        {
            using var popup = new BackupPopup("Backup stick (E:)");
            setup(popup);
            Snapshot(popup, Path.Combine(dir, name));
            popup.Close();
        }
    }

    static void Snapshot(Form form, string path, int wait = 0)
    {
        form.Location = new Point(-30000, -30000);
        if (!form.Visible) form.Show();
        form.Location = new Point(-30000, -30000);
        var until = DateTime.Now.AddSeconds(wait);
        do { Application.DoEvents(); Thread.Sleep(50); } while (DateTime.Now < until);
        using var bmp = new Bitmap(form.Width, form.Height);
        form.DrawToBitmap(bmp, new Rectangle(Point.Empty, form.Size));
        bmp.Save(path, ImageFormat.Png);
    }
}
