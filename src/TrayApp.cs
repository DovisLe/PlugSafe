namespace PlugSafe;

/// <summary>Runs in the tray: hears drives being plugged in and runs the backups.</summary>
sealed class TrayApp : ApplicationContext
{
    readonly Config config = Config.Load();
    readonly ContextMenuStrip menu = new()
    {
        Renderer = new DarkRenderer(),
        Font = Theme.Ui,
        ShowCheckMargin = true,
        ShowImageMargin = false,
    };
    readonly NotifyIcon tray;
    readonly MainForm main;
    readonly Dictionary<string, CancellationTokenSource> running = new();
    readonly System.Windows.Forms.Timer idleTimer = new() { Interval = 4000 };
    Updater.Release? pendingUpdate;
    bool checkingUpdates;
    Action? balloonAction;

    public TrayApp(bool background, WaitHandle showSignal, bool updated = false)
    {
        main = new MainForm(config, menu, id => running.ContainsKey(id));
        _ = main.Handle; // this (hidden) window is what hears "a drive was plugged in"
        main.DrivesArrived += letters => _ = OnDrivesArrived(letters);
        main.DrivesRemoved += () => main.RefreshDrives();
        main.BackupRequested += drive => StartBackup(drive, manual: true);
        main.StopRequested += drive =>
        {
            if (running.TryGetValue(drive.Id, out var cts)) try { cts.Cancel(); } catch (ObjectDisposedException) { }
        };
        main.VisibleChanged += (_, _) =>
        {
            UpdateIdle();
            if (main.Visible) MaybeCheckForUpdates();
        };
        main.Resumed += () => _ = RemindAfter(20);

        // Drives added before reminders existed count from today.
        if (config.Drives.Any(d => d.Added == null))
        {
            foreach (var d in config.Drives) d.Added ??= DateTime.Now;
            config.Save();
        }

        tray = new NotifyIcon
        {
            Icon = AppIcon.Get(SystemInformation.SmallIconSize.Width),
            Text = "PlugSafe: waiting for your backup drive",
            ContextMenuStrip = menu,
            Visible = true,
        };
        tray.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) main.ShowMain(); };
        tray.BalloonTipClicked += (_, _) =>
        {
            var action = balloonAction ?? (() => main.ShowMain());
            balloonAction = null;
            action();
        };
        menu.Opening += (_, _) => BuildMenu();
        BuildMenu();
        idleTimer.Tick += (_, _) => GoIdle();

        // A second launch (double-clicking the exe again) just opens the window.
        ThreadPool.RegisterWaitForSingleObject(showSignal,
            (_, _) => main.BeginInvoke(new Action(() => main.ShowMain())), null, Timeout.Infinite, false);

        if (!background || config.Drives.Count == 0) main.ShowMain();
        // A backup drive that was already plugged in when PlugSafe started counts as "just plugged in".
        main.BeginInvoke(new Action(async () =>
        {
            await Task.Delay(2000);
            CheckDrives(null);
        }));
        _ = RemindAfter(15);
        UpdateIdle();

        if (updated) Notify($"Updated to {Updater.CurrentText}", "PlugSafe is up to date. Your drives and settings were kept.");
        else if (config.UpdateCheck) _ = CheckForUpdatesLater();
    }

    void Notify(string title, string text, Action? onClick = null)
    {
        balloonAction = onClick;
        tray.ShowBalloonTip(5000, title, text, ToolTipIcon.None);
    }

    // ---------------------------------------------------------------- updates (opt-in, at most weekly)

    async Task CheckForUpdatesLater()
    {
        await Task.Delay(TimeSpan.FromMinutes(2)); // one-shot, so sign-in isn't slowed down
        MaybeCheckForUpdates();
    }

    void MaybeCheckForUpdates()
    {
        if (!config.UpdateCheck || checkingUpdates || pendingUpdate != null) return;
        if (config.LastUpdateCheck is { } last && DateTime.Now - last < Updater.CheckEvery) return;
        _ = CheckForUpdates(manual: false);
    }

    async Task CheckForUpdates(bool manual)
    {
        if (checkingUpdates) return;
        checkingUpdates = true;
        try
        {
            var release = await Updater.CheckAsync();
            config.LastUpdateCheck = DateTime.Now;
            config.Save();
            if (release != null)
            {
                pendingUpdate = release;
                Log.Write($"Update available: {Updater.Short(release.Version)}");
                if (manual) OfferUpdate();
                else Notify("Update available", $"PlugSafe {Updater.Short(release.Version)} is out. Click here to install it.", OfferUpdate);
            }
            else if (manual)
            {
                main.ShowMain();
                main.Flash($"You have the newest version ({Updater.CurrentText}).", Theme.Success);
            }
        }
        catch (Exception ex)
        {
            Log.Write("Update check failed: " + ex.Message);
            if (manual)
            {
                main.ShowMain();
                main.Flash("Couldn't check for updates: " + ex.Message, Theme.Warning);
            }
        }
        finally
        {
            checkingUpdates = false;
            UpdateIdle(); // give back the memory the network code used
        }
    }

    void OfferUpdate()
    {
        if (pendingUpdate is not { } release) return;
        main.ShowMain();
        if (running.Count > 0)
        {
            main.Flash("A backup is running. Install the update from the tray menu when it's done.", Theme.Warning);
            return;
        }
        string notes = release.Notes.Length > 0 ? $"\n\nWhat's new:\n{release.Notes}" : "";
        if (!ConfirmForm.Ask(main, $"Install PlugSafe {Updater.Short(release.Version)}?",
                $"You have {Updater.CurrentText}. PlugSafe restarts for a moment; your drives, file lists and backups are kept.{notes}",
                "Install update"))
            return;
        _ = InstallUpdate(release);
    }

    async Task InstallUpdate(Updater.Release release)
    {
        if (running.Count > 0) return; // a backup started while the dialog was open
        try
        {
            main.Flash("Downloading the update…", Theme.Muted);
            await Updater.InstallAsync(release);
            Log.Write("Exit for update");
            tray.Visible = false;
            ExitThread();
        }
        catch (Exception ex)
        {
            Log.Write("Update failed: " + ex.Message);
            main.Flash("The update didn't install, nothing was changed: " + ex.Message, Theme.Error);
        }
    }

    // ---------------------------------------------------------------- reminders

    /// <summary>Checked only at sign-in and after the PC wakes up, never on a timer, and at most once a day per drive.</summary>
    async Task RemindAfter(int seconds)
    {
        await Task.Delay(seconds * 1000);
        if (config.ReminderDays <= 0 || config.Drives.Count == 0) return;
        var plugged = Drives.UsbDrives();
        foreach (var drive in config.Drives)
        {
            bool pluggedIn = Drives.FindConnected(drive, plugged) != null || running.ContainsKey(drive.Id);
            if (!Reminders.Due(drive, config.ReminderDays, DateTime.Now, pluggedIn)) continue;
            drive.LastReminder = DateTime.Now;
            config.Save();
            int days = Reminders.DaysSince(drive, DateTime.Now);
            var popup = new BackupPopup(drive.Name);
            popup.FormClosed += (_, _) => UpdateIdle();
            popup.ShowMessage("Time for a backup",
                drive.LastBackup == null ? $"No backup yet, {days} days since you added it." : $"Your last backup was {days} days ago.",
                Theme.Warning, Glyphs.Usb, detail: "Plug the drive in and PlugSafe does the rest.");
            UpdateIdle();
        }
    }

    static string? SpaceWarning(DriveCandidate found)
    {
        try
        {
            var info = new DriveInfo(found.Root);
            return Drives.LowOnSpace(info.AvailableFreeSpace, info.TotalSize)
                ? $"Safe to unplug. Drive almost full: {Fmt.Size(info.AvailableFreeSpace)} left."
                : null;
        }
        catch { return null; }
    }

    // ---------------------------------------------------------------- drive detection

    async Task OnDrivesArrived(List<char> letters)
    {
        Log.Write($"Drive plugged in: {string.Join(", ", letters.Select(l => l + ":"))}");
        // Windows announces the drive slightly before it can be read; give it a moment.
        for (int attempt = 1; attempt <= 4; attempt++)
        {
            await Task.Delay(1500);
            var ready = Drives.UsbDrives().Where(d => letters.Contains(d.Letter)).ToList();
            if (ready.Count == letters.Count || attempt == 4)
            {
                CheckDrives(ready);
                return;
            }
        }
    }

    void CheckDrives(List<DriveCandidate>? candidates)
    {
        candidates ??= Drives.UsbDrives();
        foreach (var drive in config.Drives.ToList())
            if (Drives.FindConnected(drive, candidates) is { } found) StartBackup(drive, manual: false, found);
        main.RefreshDrives();
    }

    // ---------------------------------------------------------------- backups

    async void StartBackup(BackupDrive drive, bool manual, DriveCandidate? found = null)
    {
        if (running.ContainsKey(drive.Id)) return;
        found ??= Drives.FindConnected(drive);
        if (found == null) { main.Flash("Plug the drive in first.", Theme.Warning); return; }

        var cts = new CancellationTokenSource();
        running[drive.Id] = cts;
        UpdateIdle();
        main.RefreshDrives();
        tray.Text = $"PlugSafe: backing up to {drive.Name}";

        var popup = new BackupPopup($"{drive.Name} ({found.Letter}:)");
        popup.StopRequested += () => { try { cts.Cancel(); } catch (ObjectDisposedException) { } };
        popup.FormClosed += (_, _) => UpdateIdle();
        string driveFolder = Drives.FolderOn(found);
        string mine = Path.Combine(driveFolder, Drives.ComputerName);
        void OpenBackup() => Shell.OpenFolder(Directory.Exists(mine) ? mine : driveFolder);

        try
        {
            // Never back up anything that lives on the backup drive itself.
            var items = drive.Items.Where(i => Native.VolumeSerial(Path.GetPathRoot(i.Path)!) != found.Serial).ToList();
            if (items.Count == 0)
            {
                popup.ShowMessage("Nothing chosen to back up", "Pick the files and folders for this drive in PlugSafe.",
                    Theme.Warning, Glyphs.Warning, "Choose files", () => main.ShowMain(drive.Id));
                return;
            }

            popup.ShowChecking();
            var engine = new BackupEngine(driveFolder, Drives.ComputerName, found.Serial, items, found.IsFat32, config.SkipSet());
            var scan = await Task.Run(() => engine.Scan(cts.Token));
            if (scan.Changed.Count == 0)
            {
                Record(drive, "everything up to date", scan.Problems, null);
                popup.ShowUpToDate(scan, OpenBackup, SpaceWarning(found));
                return;
            }

            if (!manual && config.Mode != StartMode.Immediately &&
                !await popup.AskToStart(scan, config.Mode, config.CountdownSeconds))
                return;

            popup.ShowRunning(scan);
            main.BackupStarted(drive, scan);
            var progress = new Progress<BackupProgress>(p =>
            {
                popup.Report(p);
                main.ReportProgress(drive, p); // the window shows it too, per file and folder
            });
            var result = await Task.Run(() => engine.Run(scan, progress, cts.Token));
            Record(drive, result.Summary, result.Problems, result.StoppedBecause);
            if (popup.IsDisposed)
                Notify(result.StoppedBecause == null ? "Backup complete" : "Backup stopped", $"{drive.Name}: {result.Summary}");
            popup.ShowDone(result, OpenBackup, () => main.ShowProblems(drive), SpaceWarning(found));
        }
        catch (OperationCanceledException)
        {
            if (!popup.IsDisposed) popup.Close();
        }
        catch (SafetyException ex)
        {
            Log.Write("SAFETY STOP: " + ex.Message);
            popup.ShowMessage("Backup stopped for safety", ex.Message, Theme.Warning, Glyphs.Shield);
        }
        catch (Exception ex)
        {
            Log.Write("Backup failed: " + ex);
            popup.ShowMessage("Backup failed", ex.Message, Theme.Error, Glyphs.Error);
        }
        finally
        {
            running.Remove(drive.Id);
            cts.Dispose();
            main.BackupFinished(drive);
            tray.Text = "PlugSafe: waiting for your backup drive";
            main.RefreshDrives();
            UpdateIdle();
        }
    }

    void Record(BackupDrive drive, string summary, List<string> problems, string? stoppedBecause)
    {
        drive.LastBackup = DateTime.Now;
        drive.LastSummary = stoppedBecause != null ? "stopped early, " + summary : summary;
        drive.LastProblems = (stoppedBecause != null ? new[] { stoppedBecause } : Array.Empty<string>()).Concat(problems).Take(200).ToList();
        config.Save();
    }

    // ---------------------------------------------------------------- menu

    void BuildMenu()
    {
        menu.Items.Clear();
        if (pendingUpdate is { } update)
            menu.Items.Add(new ToolStripMenuItem($"Install update {Updater.Short(update.Version)}…", null, (_, _) => Later(OfferUpdate))
            {
                ForeColor = Theme.Success,
            });
        menu.Items.Add(new ToolStripMenuItem("Open PlugSafe", null, (_, _) => main.ShowMain()) { Font = Theme.UiBold });
        var plugged = Drives.UsbDrives();
        foreach (var drive in config.Drives)
        {
            if (Drives.FindConnected(drive, plugged) is not { } found) continue;
            var d = drive;
            menu.Items.Add(new ToolStripMenuItem($"Back up {drive.Name} ({found.Letter}:) now", null, (_, _) => StartBackup(d, manual: true, found))
                { Enabled = !running.ContainsKey(drive.Id) });
        }
        menu.Items.Add(new ToolStripSeparator());
        var when = new ToolStripMenuItem("When the drive is plugged in");
        foreach (var (mode, label) in new[]
                 {
                     (StartMode.Countdown, $"Back up automatically after {config.CountdownSeconds} s"),
                     (StartMode.Immediately, "Back up straight away"),
                     (StartMode.Ask, "Ask me first"),
                 })
        {
            when.DropDownItems.Add(new ToolStripMenuItem(label, null, (_, _) => { config.Mode = mode; config.Save(); })
                { Checked = config.Mode == mode });
        }
        menu.Items.Add(when);
        var remind = new ToolStripMenuItem("Remind me to back up");
        foreach (var (days, label) in new[] { (3, "After 3 days"), (7, "After 7 days"), (14, "After 14 days"), (0, "Never") })
        {
            remind.DropDownItems.Add(new ToolStripMenuItem(label, null, (_, _) => { config.ReminderDays = days; config.Save(); })
                { Checked = config.ReminderDays == days });
        }
        menu.Items.Add(remind);
        menu.Items.Add("Skipped folders…", null, (_, _) => Later(EditSkipped));
        menu.Items.Add(new ToolStripMenuItem("Start with Windows", null, (_, _) => Later(ToggleStartup)) { Checked = Startup.IsEnabled });
        var updates = new ToolStripMenuItem("PlugSafe updates");
        updates.DropDownItems.Add("Check now", null, (_, _) => Later(() => _ = CheckForUpdates(manual: true)));
        updates.DropDownItems.Add(new ToolStripMenuItem("Check once a week", null, (_, _) =>
        {
            config.UpdateCheck = !config.UpdateCheck;
            config.Save();
        }) { Checked = config.UpdateCheck });
        updates.DropDownItems.Add("Open releases page", null, (_, _) => Updater.OpenReleasesPage());
        menu.Items.Add(updates);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => Later(Exit));
    }

    void Later(Action action) => main.BeginInvoke(action);

    void EditSkipped()
    {
        main.ShowMain();
        using var form = new SkipForm(config.SkippedFolders);
        if (form.ShowDialog(main) != DialogResult.OK) return;
        config.SkippedFolders = form.Result;
        config.Save();
        main.SkipListChanged();
        main.Flash($"Skipping {Fmt.Count(form.Result.Count, "folder name")} from now on.", Theme.Success);
    }

    void ToggleStartup()
    {
        try { Startup.Set(!Startup.IsEnabled); }
        catch (Exception ex) { main.Flash("Couldn't change the startup setting: " + ex.Message, Theme.Error); }
    }

    void Exit()
    {
        if (running.Count > 0 && !ConfirmForm.Ask(main.Visible ? main : null, "Stop the backup and exit?",
                "A backup is running. Stopping is safe: files already copied stay on the drive, and nothing on your PC is touched.",
                "Stop and exit", danger: true))
            return;
        foreach (var cts in running.Values) cts.Cancel();
        Log.Write("Exit from tray menu");
        tray.Visible = false;
        ExitThread();
    }

    // ---------------------------------------------------------------- idle

    /// <summary>Nothing on screen and nothing running: Windows efficiency mode + give memory back. No timers keep running.</summary>
    void UpdateIdle()
    {
        idleTimer.Stop();
        if (IsIdle) idleTimer.Start();
        else Efficiency.SetIdle(false);
    }

    bool IsIdle => !main.Visible && running.Count == 0 && BackupPopup.OpenCount == 0;

    void GoIdle()
    {
        idleTimer.Stop();
        if (!IsIdle) return;
        Efficiency.SetIdle(true);
        Efficiency.TrimMemory();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            tray.Dispose();
            main.Dispose();
            menu.Dispose();
            idleTimer.Dispose();
        }
        base.Dispose(disposing);
    }
}
