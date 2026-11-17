using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace PlugSafe;

/// <summary>The PlugSafe window: pick backup drives and, for each drive, the files and folders it backs up.</summary>
sealed class MainForm : Form
{
    const int FormW = 480, Pad = 16, HeaderH = 48, ChipsH = 32, CardH = 108, SectionH = 40, FooterH = 44, MaxRows = 6;

    readonly Config config;
    readonly ContextMenuStrip menu;
    readonly Func<string, bool> isRunning;
    readonly IconButton menuButton = new(Glyphs.Settings), closeButton = new(Glyphs.Close), moreButton = new(Glyphs.More);
    readonly DriveChips chips = new();
    readonly FlatButton backupButton = new() { Text = "Back up now" };
    readonly FlatButton addFiles = new() { Text = "Files", Glyph = Glyphs.Add, Secondary = true, Font = Theme.UiSmallBold };
    readonly FlatButton addFolder = new() { Text = "Folder", Glyph = Glyphs.Add, Secondary = true, Font = Theme.UiSmallBold };
    readonly FlatButton addDriveButton = new() { Text = "Add drive", Glyph = Glyphs.Usb };
    readonly ItemList list = new();
    readonly ToolTip tips = new();
    readonly System.Windows.Forms.Timer flashTimer = new() { Interval = 4000 };
    readonly Bitmap headerIcon;
    readonly Dictionary<string, long> sizes = new(StringComparer.OrdinalIgnoreCase);
    readonly HashSet<string> sizing = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, DriveCandidate> connected = new();
    readonly Dictionary<string, LiveBackup> live = new(); // backups running right now, by drive id
    long lastLivePaint;
    string? selectedId, flash;
    Color flashColor;
    Rectangle cardRect, sectionRect, footerRect, emptyRect;

    internal Func<BackupDrive, DriveCandidate?>? DemoConnection; // preview renders only
    internal Func<string, string>? DemoDisplayPath;               // preview renders only: show made-up paths
    internal void RefreshForPreview() => RefreshView();

    public event Action<BackupDrive>? BackupRequested;
    public event Action<BackupDrive>? StopRequested;
    public event Action<List<char>>? DrivesArrived;
    public event Action? DrivesRemoved;
    public event Action? Resumed;

    public MainForm(Config config, ContextMenuStrip menu, Func<string, bool> isRunning)
    {
        this.config = config;
        this.menu = menu;
        this.isRunning = isRunning;
        Text = "PlugSafe";
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.None;
        BackColor = Theme.Bg;
        ForeColor = Theme.Text;
        Font = Theme.Ui;
        KeyPreview = true;
        DoubleBuffered = true;
        Icon = AppIcon.Get(32);
        using (var icon = AppIcon.Get(S(16))) headerIcon = icon.ToBitmap();

        menuButton.Click += (_, _) => menu.Show(menuButton, new Point(0, menuButton.Height + S(4)));
        closeButton.Click += (_, _) => Hide();
        chips.SelectedChanged += id => { selectedId = id; RefreshView(); };
        chips.AddClicked += at => ShowAddDriveMenu(chips, at);
        addDriveButton.Click += (_, _) => ShowAddDriveMenu(addDriveButton, new Point(0, addDriveButton.Height + S(4)));
        backupButton.Click += (_, _) =>
        {
            if (Selected is not { } drive) return;
            if (isRunning(drive.Id)) StopRequested?.Invoke(drive);
            else BackupRequested?.Invoke(drive);
        };
        moreButton.Click += (_, _) => ShowDriveMenu();
        addFiles.Click += (_, _) => AddFiles();
        addFolder.Click += (_, _) => AddFolders();
        list.RemoveClicked += RemoveItem;
        list.OpenClicked += OpenItem;
        list.MenuRequested += ShowItemMenu;
        flashTimer.Tick += (_, _) => { flashTimer.Stop(); flash = null; Invalidate(footerRect); };
        tips.SetToolTip(menuButton, "Settings");
        tips.SetToolTip(closeButton, "Hide (PlugSafe keeps watching for your drive)");
        tips.SetToolTip(moreButton, "More");
        tips.SetToolTip(addFiles, "Add files to back up");
        tips.SetToolTip(addFolder, "Add folders to back up");
        moreButton.BackColor = backupButton.BackColor = Theme.Surface; // they sit on the drive card

        Controls.AddRange(new Control[] { menuButton, closeButton, chips, backupButton, moreButton, addFiles, addFolder, addDriveButton, list });
        selectedId = config.Drives.FirstOrDefault()?.Id;
        RefreshView();
    }

    int S(int value) => LogicalToDeviceUnits(value);

    BackupDrive? Selected => config.Drives.FirstOrDefault(d => d.Id == selectedId) ?? config.Drives.FirstOrDefault();

    bool Busy(BackupDrive drive) => isRunning(drive.Id) || live.ContainsKey(drive.Id);

    DriveCandidate? Connection(BackupDrive drive) =>
        DemoConnection != null ? DemoConnection(drive) : connected.GetValueOrDefault(drive.Id);

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ClassStyle |= Native.CS_DROPSHADOW;
            return cp;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Native.StyleWindow(Handle);
    }

    /// <summary>Windows tells every top-level window when a drive appears or disappears: no polling needed.</summary>
    protected override void WndProc(ref Message m)
    {
        if (m.Msg == Native.WM_DEVICECHANGE && m.LParam != IntPtr.Zero &&
            (int)m.WParam is Native.DBT_DEVICEARRIVAL or Native.DBT_DEVICEREMOVECOMPLETE &&
            Marshal.ReadInt32(m.LParam, 4) == Native.DBT_DEVTYP_VOLUME)
        {
            var letters = Drives.LettersFromMask((uint)Marshal.ReadInt32(m.LParam, 12)).ToList();
            if ((int)m.WParam == Native.DBT_DEVICEARRIVAL) DrivesArrived?.Invoke(letters);
            else DrivesRemoved?.Invoke();
        }
        else if (m.Msg == Native.WM_POWERBROADCAST && (int)m.WParam is Native.PBT_APMRESUMEAUTOMATIC or Native.PBT_APMRESUMESUSPEND)
        {
            Resumed?.Invoke(); // woke from sleep: a good moment to check backup reminders
        }
        base.WndProc(ref m);
    }

    // ---------------------------------------------------------------- show / refresh

    public void ShowMain(string? selectDrive = null)
    {
        if (selectDrive != null) selectedId = selectDrive;
        if (!Visible)
        {
            sizes.Clear();
            RefreshDrives();
            var area = Screen.FromPoint(Cursor.Position).WorkingArea;
            Location = new Point(area.Left + (area.Width - Width) / 2, area.Top + Math.Max(0, (area.Height - Height) / 3));
            Show();
        }
        else RefreshDrives();
        Activate();
        Native.SetForegroundWindow(Handle);
    }

    /// <summary>Re-reads which backup drives are plugged in (only when something happened or the window is opened).</summary>
    public void RefreshDrives()
    {
        connected.Clear();
        var plugged = Drives.UsbDrives();
        foreach (var drive in config.Drives)
            if (Drives.FindConnected(drive, plugged) is { } found) connected[drive.Id] = found;
        RefreshView();
    }

    void RefreshView()
    {
        chips.Set(config.Drives.Select(d => new DriveChips.Chip(d.Id, d.Name, Connection(d) != null)).ToList(), Selected?.Id);
        var drive = Selected;
        if (drive != null)
        {
            if (Visible) ComputeSizes(drive.Items);
            RefreshRows();
            var found = Connection(drive);
            bool running = Busy(drive);
            // While a backup runs, the same button stops it.
            backupButton.Enabled = running || (found != null && drive.Items.Count > 0);
            backupButton.Text = running ? "Stop" : "Back up now";
            backupButton.Glyph = running ? Glyphs.Stop : null;
            backupButton.Secondary = running;
            backupButton.BackColor = Theme.Surface;
            tips.SetToolTip(backupButton, running ? "Stop the backup (safe: files already copied stay on the drive)"
                : found == null ? "Plug the drive in first" : drive.Items.Count == 0 ? "Add files first" : "Back up the changed files now");
        }
        DoLayout();
        Invalidate();
    }

    void RefreshRows()
    {
        var drive = Selected;
        if (drive == null) return;
        var backup = live.GetValueOrDefault(drive.Id);
        var found = Connection(drive);
        list.SetRows(drive.Items.Select(item =>
        {
            bool exists = item.IsFolder ? Directory.Exists(item.Path) : File.Exists(item.Path);
            string size = !exists ? "" : sizes.TryGetValue(item.Path, out var bytes) && bytes >= 0 ? Fmt.Size(bytes) : "…";
            string folder = Path.GetDirectoryName(item.Path) ?? item.Path;
            string detail = exists ? DemoDisplayPath?.Invoke(folder) ?? folder : "Not found. Moved or deleted?";
            // During a backup: this item's own progress (or "up to date" if nothing in it changed).
            double? progress = null;
            string? progressText = null;
            if (backup != null && exists) // a missing item keeps saying "Not found" instead of "up to date"
            {
                if (backup.Items.TryGetValue(item.Path, out var p) && p.Total > 0)
                {
                    progress = Math.Min(1, p.Done / (double)p.Total);
                    progressText = progress >= 1 ? "done" : $"{(int)(progress.Value * 100)}%";
                }
                else (progress, progressText) = (1, "up to date");
            }
            string? hover = BackupCopy(drive, item, found) != null ? "Open backup" : exists ? "Open" : null;
            return new ItemList.Row(item.IsFolder ? Glyphs.Folder : Glyphs.File, Path.GetFileName(item.Path), detail, size, !exists,
                item.IsFolder, progress, progressText, hover);
        }).ToList());
    }

    // ---------------------------------------------------------------- live backup progress

    /// <summary>Per-drive progress of a running backup, overall and per chosen file/folder.</summary>
    sealed class LiveBackup
    {
        public readonly Dictionary<string, (long Done, long Total)> Items = new(StringComparer.OrdinalIgnoreCase);
        readonly List<WorkItem> work;
        readonly long[] fileDone;
        public int Done, Total;
        public long Bytes, TotalBytes;
        public string File = "";

        public LiveBackup(ScanResult scan)
        {
            work = scan.Changed;
            Total = work.Count;
            TotalBytes = scan.ChangedBytes;
            fileDone = new long[work.Count];
            foreach (var w in work)
            {
                var t = Items.GetValueOrDefault(w.ItemPath);
                Items[w.ItemPath] = (0, t.Total + w.Size);
            }
        }

        public void Apply(BackupProgress p)
        {
            Done = p.Done;
            Bytes = p.Bytes;
            File = p.File;
            if (p.Index < 0 || p.Index >= work.Count) return;
            var w = work[p.Index];
            long delta = p.FileBytes - fileDone[p.Index];
            fileDone[p.Index] = p.FileBytes;
            var t = Items[w.ItemPath];
            Items[w.ItemPath] = (t.Done + delta, t.Total);
        }
    }

    public void BackupStarted(BackupDrive drive, ScanResult scan)
    {
        live[drive.Id] = new LiveBackup(scan);
        if (Visible) RefreshView();
    }

    public void ReportProgress(BackupDrive drive, BackupProgress p)
    {
        if (!live.TryGetValue(drive.Id, out var backup)) return;
        backup.Apply(p);
        long now = Environment.TickCount64;
        if (!Visible || Selected?.Id != drive.Id || now - lastLivePaint < 66) return; // ~15 updates a second is plenty
        lastLivePaint = now;
        RefreshRows();
        Invalidate(cardRect);
    }

    public void BackupFinished(BackupDrive drive)
    {
        live.Remove(drive.Id);
        if (Visible) RefreshDrives();
    }

    // ---------------------------------------------------------------- opening items

    /// <summary>Where this item's backup copy is on the drive, if the drive is plugged in and it's been backed up.</summary>
    static string? BackupCopy(BackupDrive drive, BackupItem item, DriveCandidate? found)
    {
        if (found == null) return null;
        string copy = Path.Combine(Drives.FolderOn(found), Drives.ComputerName, "Latest", item.Name);
        return (item.IsFolder ? Directory.Exists(copy) : File.Exists(copy)) ? copy : null;
    }

    static void OpenPath(string path, bool folder)
    {
        if (folder) Shell.OpenFolder(path);
        else Shell.OpenFile(path);
    }

    /// <summary>Click: open the backed-up copy if the drive is here and has it, otherwise the original.</summary>
    void OpenItem(int index)
    {
        if (Selected is not { } drive || index < 0 || index >= drive.Items.Count) return;
        var item = drive.Items[index];
        if (BackupCopy(drive, item, Connection(drive)) is { } copy) OpenPath(copy, item.IsFolder);
        else if (item.IsFolder ? Directory.Exists(item.Path) : File.Exists(item.Path)) OpenPath(item.Path, item.IsFolder);
        else Flash($"\"{Path.GetFileName(item.Path)}\" isn't on your PC any more.", Theme.Warning);
    }

    void ShowItemMenu(int index, Point at)
    {
        if (Selected is not { } drive || index < 0 || index >= drive.Items.Count) return;
        var item = drive.Items[index];
        var found = Connection(drive);
        string? copy = BackupCopy(drive, item, found);
        bool original = item.IsFolder ? Directory.Exists(item.Path) : File.Exists(item.Path);
        var menu = new ContextMenuStrip { Renderer = new DarkRenderer(), Font = Theme.Ui, ShowImageMargin = false };
        menu.Items.Add(new ToolStripMenuItem(found != null ? $"Open backup copy ({found.Letter}:)" : "Open backup copy (plug the drive in)",
            null, (_, _) => OpenPath(copy!, item.IsFolder)) { Enabled = copy != null });
        menu.Items.Add(new ToolStripMenuItem("Open original", null, (_, _) => OpenPath(item.Path, item.IsFolder)) { Enabled = original });
        menu.Items.Add(new ToolStripMenuItem("Show original in its folder", null, (_, _) => Shell.ShowInFolder(item.Path)) { Enabled = original });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Remove from list", null, (_, _) => BeginInvoke(new Action(() => RemoveItem(index))))
            { Enabled = !Busy(drive) });
        menu.Closed += (_, _) => BeginInvoke(new Action(menu.Dispose));
        menu.Show(list, at);
    }

    /// <summary>The skipped-folders list changed: sizes need recounting.</summary>
    public void SkipListChanged()
    {
        sizes.Clear();
        RefreshView();
    }

    /// <summary>Folder sizes are counted in the background, only while the window is open.</summary>
    void ComputeSizes(IEnumerable<BackupItem> items)
    {
        var skip = config.SkipSet();
        foreach (var item in items)
        {
            if (sizes.ContainsKey(item.Path) || !sizing.Add(item.Path)) continue;
            string path = item.Path;
            bool folder = item.IsFolder;
            Task.Run(() => SizeOf(path, folder, skip)).ContinueWith(t =>
            {
                sizing.Remove(path);
                sizes[path] = t.Result;
                if (!IsDisposed && Visible) { RefreshRows(); Invalidate(sectionRect); }
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }
    }

    static long SizeOf(string path, bool folder, HashSet<string> skip)
    {
        try
        {
            if (!folder) return File.Exists(path) ? new FileInfo(path).Length : -1;
            if (!Directory.Exists(path)) return -1;
            return FileWalker.Files(new DirectoryInfo(path), skip).Sum(f => f.Length);
        }
        catch { return -1; }
    }

    public void Flash(string text, Color color)
    {
        flash = text;
        flashColor = color;
        flashTimer.Stop();
        flashTimer.Start();
        Invalidate(footerRect);
    }

    // ---------------------------------------------------------------- drives

    void ShowAddDriveMenu(Control anchor, Point at)
    {
        var picker = new ContextMenuStrip { Renderer = new DarkRenderer(), Font = Theme.Ui, ShowImageMargin = false };
        var drives = Drives.UsbDrives();
        if (drives.Count == 0)
            picker.Items.Add(new ToolStripMenuItem("No USB drive found. Plug one in first.") { Enabled = false });
        foreach (var d in drives)
        {
            bool added = config.Drives.Any(k => Drives.FindConnected(k, new[] { d }) != null);
            string label = $"{d.DisplayName} ({d.Letter}:)   {Fmt.Size(d.Free)} free of {Fmt.Size(d.Total)}" + (added ? "   already added" : "");
            var candidate = d;
            picker.Items.Add(new ToolStripMenuItem(label, null, (_, _) => BeginInvoke(new Action(() => AddDrive(candidate)))) { Enabled = !added });
        }
        picker.Closed += (_, _) => BeginInvoke(new Action(picker.Dispose));
        picker.Show(anchor, at);
    }

    void AddDrive(DriveCandidate d)
    {
        if (!ConfirmForm.Ask(this, $"Back up to {d.DisplayName} ({d.Letter}:)?",
                "PlugSafe will create a \"PlugSafe\" folder on this drive and only ever write inside it. " +
                "Nothing else on the drive is touched or deleted.\n\n" +
                "After this, plugging this drive in starts a backup of the files you choose.", "Use this drive"))
            return;
        try
        {
            string id = Drives.Register(d);
            var drive = config.Drives.FirstOrDefault(x => x.Id == id && x.Serial == d.Serial);
            if (drive == null)
            {
                drive = new BackupDrive { Id = id, Serial = d.Serial };
                config.Drives.Add(drive);
            }
            drive.Name = d.DisplayName;
            drive.TotalBytes = d.Total;
            drive.Added ??= DateTime.Now;
            config.Save();
            selectedId = id;
            Log.Write($"Added backup drive {d.DisplayName} ({d.Letter}:)");
            RefreshDrives();
            Flash("Drive added. Now choose the files and folders to back up.", Theme.Success);
        }
        catch (Exception ex) { Flash("Couldn't prepare the drive: " + ex.Message, Theme.Error); }
    }

    void ShowDriveMenu()
    {
        var drive = Selected;
        if (drive == null) return;
        var found = Connection(drive);
        var more = new ContextMenuStrip { Renderer = new DarkRenderer(), Font = Theme.Ui, ShowImageMargin = false };
        more.Items.Add(new ToolStripMenuItem("Open backup folder", null, (_, _) =>
        {
            string folder = Drives.FolderOn(found!), mine = Path.Combine(folder, Drives.ComputerName);
            Shell.OpenFolder(Directory.Exists(mine) ? mine : folder);
        }) { Enabled = found != null });
        more.Items.Add(new ToolStripMenuItem("Clean up old versions…", null, (_, _) => BeginInvoke(new Action(() => OpenCleanup(drive))))
            { Enabled = found != null && !isRunning(drive.Id) });
        more.Items.Add(new ToolStripMenuItem("Problems from the last backup", null, (_, _) => BeginInvoke(new Action(() => ShowProblems(drive))))
            { Enabled = drive.LastProblems.Count > 0 });
        more.Items.Add(new ToolStripSeparator());
        more.Items.Add("Stop using this drive…", null, (_, _) => BeginInvoke(new Action(() => RemoveDrive(drive))));
        more.Closed += (_, _) => BeginInvoke(new Action(more.Dispose));
        more.Show(moreButton, new Point(0, moreButton.Height + S(4)));
    }

    void RemoveDrive(BackupDrive drive)
    {
        if (!ConfirmForm.Ask(this, $"Stop backing up to {drive.Name}?",
                "PlugSafe will stop backing up to this drive. The backups already on it stay there, and nothing is deleted. " +
                "You can add it again later and it will carry on where it left off.", "Stop using it", danger: true))
            return;
        config.Drives.Remove(drive);
        config.Save();
        selectedId = config.Drives.FirstOrDefault()?.Id;
        RefreshView();
        Flash($"{drive.Name} removed from PlugSafe. Its backups are still on the drive.", Theme.Muted);
    }

    void OpenCleanup(BackupDrive drive)
    {
        // Same identity check as a backup: only the registered drive, recognised by ID file + serial number.
        var found = Drives.FindConnected(drive);
        if (found == null) { Flash("Plug the drive in first.", Theme.Warning); return; }
        string driveFolder = Drives.FolderOn(found);
        string versions = Path.Combine(driveFolder, Drives.ComputerName, "Versions");
        TargetGuard guard;
        try { guard = new TargetGuard(driveFolder, found.Serial, drive.Items.Select(i => i.Path)); }
        catch (SafetyException ex) { Flash(ex.Message, Theme.Warning); return; }
        using var form = new CleanupForm($"{drive.Name} ({found.Letter}:)", versions, guard);
        if (form.ShowDialog(this) == DialogResult.OK)
        {
            RefreshDrives();
            Flash($"Freed {Fmt.Size(form.FreedBytes)} on {drive.Name}.", Theme.Success);
        }
    }

    public void ShowProblems(BackupDrive drive)
    {
        ShowMain(drive.Id);
        using var form = new ProblemsForm(drive.Name, drive.LastProblems);
        form.ShowDialog(this);
    }

    // ---------------------------------------------------------------- items

    void AddFiles()
    {
        using var dialog = new OpenFileDialog { Multiselect = true, Title = "Choose files to back up", CheckFileExists = true };
        if (dialog.ShowDialog(this) == DialogResult.OK) AddItems(dialog.FileNames.Select(p => (p, false)));
    }

    void AddFolders()
    {
        using var dialog = new FolderBrowserDialog { Multiselect = true, Description = "Choose folders to back up", UseDescriptionForTitle = true };
        if (dialog.ShowDialog(this) == DialogResult.OK) AddItems(dialog.SelectedPaths.Select(p => (p, true)));
    }

    void AddItems(IEnumerable<(string Path, bool Folder)> picked)
    {
        var drive = Selected;
        if (drive == null) return;
        int added = 0;
        var notes = new List<string>();
        foreach (var (raw, folder) in picked)
        {
            string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(raw));
            string name = Path.GetFileName(full);
            if (name.Length == 0) { notes.Add("Pick folders inside a drive, not a whole drive."); continue; }
            // Safety: a backup drive must never be backed up onto itself (or onto another backup drive).
            uint? serial = Native.VolumeSerial(Path.GetPathRoot(full)!);
            if (config.Drives.Any(d => d.Serial == serial)) { notes.Add($"\"{name}\" is on a backup drive, so it can't be backed up to one."); continue; }
            if (drive.Items.Any(i => i.Path.Equals(full, StringComparison.OrdinalIgnoreCase))) { notes.Add($"\"{name}\" is already in the list."); continue; }
            var parent = drive.Items.FirstOrDefault(i => i.IsFolder && TargetGuard.IsInside(full, i.Path));
            if (parent != null) { notes.Add($"\"{name}\" is already included in \"{Path.GetFileName(parent.Path)}\"."); continue; }
            drive.Items.Add(new BackupItem { Path = full, IsFolder = folder, Name = UniqueName(drive, name) });
            added++;
        }
        config.Save();
        RefreshView();
        if (notes.Count > 0) Flash(notes[0] + (added > 0 ? $" Added {Fmt.Count(added, "other item")}." : ""), Theme.Warning);
        else if (added > 0) Flash($"Added {Fmt.Count(added, "item")}. They'll be backed up next time the drive is plugged in.", Theme.Success);
    }

    static string UniqueName(BackupDrive drive, string name)
    {
        string candidate = name;
        for (int i = 2; drive.Items.Any(x => x.Name.Equals(candidate, StringComparison.OrdinalIgnoreCase)); i++)
            candidate = Path.HasExtension(name) ? $"{Path.GetFileNameWithoutExtension(name)} ({i}){Path.GetExtension(name)}" : $"{name} ({i})";
        return candidate;
    }

    void RemoveItem(int index)
    {
        var drive = Selected;
        if (drive == null || index < 0 || index >= drive.Items.Count) return;
        string name = Path.GetFileName(drive.Items[index].Path);
        drive.Items.RemoveAt(index);
        config.Save();
        RefreshView();
        Flash($"Removed \"{name}\" from the list. Nothing was deleted, on your PC or the drive.", Theme.Muted);
    }

    // ---------------------------------------------------------------- window behaviour

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == Keys.Escape) { Hide(); return true; }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left) return;
        Native.ReleaseCapture();
        Native.SendMessage(Handle, Native.WM_NCLBUTTONDOWN, Native.HTCAPTION, IntPtr.Zero);
    }

    // ---------------------------------------------------------------- layout + painting

    void DoLayout()
    {
        int w = S(FormW), pad = S(Pad), b = S(30);
        closeButton.Bounds = new Rectangle(w - S(10) - b, (S(HeaderH) - b) / 2, b, b);
        menuButton.Bounds = new Rectangle(closeButton.Left - S(2) - b, closeButton.Top, b, b);
        int y = S(HeaderH);
        bool empty = config.Drives.Count == 0;
        foreach (var c in new Control[] { chips, backupButton, moreButton, addFiles, addFolder, list }) c.Visible = !empty;
        addDriveButton.Visible = empty;

        if (empty)
        {
            emptyRect = new Rectangle(pad, y, w - 2 * pad, S(200));
            addDriveButton.Bounds = new Rectangle(emptyRect.X + (emptyRect.Width - S(150)) / 2, emptyRect.Bottom - S(54), S(150), S(36));
            y = emptyRect.Bottom + S(10);
        }
        else
        {
            chips.Bounds = new Rectangle(pad, y, w - 2 * pad, S(ChipsH));
            y = chips.Bottom + S(10);
            cardRect = new Rectangle(pad, y, w - 2 * pad, S(CardH));
            moreButton.Bounds = new Rectangle(cardRect.Right - S(8) - S(28), cardRect.Y + S(8), S(28), S(28));
            backupButton.Bounds = new Rectangle(cardRect.Right - S(12) - S(132), cardRect.Y + S(40), S(132), S(32));
            y = cardRect.Bottom + S(12);
            sectionRect = new Rectangle(pad, y, w - 2 * pad, S(SectionH));
            addFolder.Bounds = new Rectangle(sectionRect.Right - S(84), y + S(5), S(84), S(30));
            addFiles.Bounds = new Rectangle(addFolder.Left - S(6) - S(74), y + S(5), S(74), S(30));
            y = sectionRect.Bottom + S(2);
            int rows = list.Rows.Count;
            list.Bounds = new Rectangle(pad, y, w - 2 * pad, rows == 0 ? S(64) : Math.Min(rows, MaxRows) * S(ItemList.RowH) - S(4));
            y = list.Bottom + S(8);
        }
        footerRect = new Rectangle(pad, y, w - 2 * pad, S(FooterH));
        ClientSize = new Size(w, footerRect.Bottom + S(8));
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        int pad = S(Pad), headerH = S(HeaderH), iconSize = S(16);
        g.DrawImage(headerIcon, new Rectangle(pad, (headerH - iconSize) / 2, iconSize, iconSize));
        int x = pad + iconSize + S(8);
        int titleW = Theme.TextWidth(g, "PlugSafe", Theme.UiBold);
        Theme.DrawText(g, "PlugSafe", Theme.UiBold, new Rectangle(x, 0, titleW + S(2), headerH), Theme.Text);
        Theme.DrawText(g, "Automatic USB backups", Theme.UiSmall, new Rectangle(x + titleW + S(8), 0, menuButton.Left - x - titleW - S(14), headerH), Theme.Muted);

        if (config.Drives.Count == 0) PaintEmpty(g);
        else
        {
            PaintCard(g);
            PaintSection(g);
        }
        PaintFooter(g);
    }

    void PaintEmpty(Graphics g)
    {
        Theme.FillRound(g, Theme.Surface, emptyRect, S(12));
        var icon = new Rectangle(emptyRect.X + (emptyRect.Width - S(52)) / 2, emptyRect.Y + S(18), S(52), S(52));
        Theme.FillRound(g, Theme.Thumb, icon, S(14));
        Theme.DrawGlyph(g, Glyphs.Usb, icon, Theme.Accent, Theme.IconsLarge);
        Theme.DrawText(g, "Add your backup drive", Theme.UiLarge, new Rectangle(emptyRect.X, icon.Bottom + S(10), emptyRect.Width, S(24)), Theme.Text, TextFormatFlags.HorizontalCenter);
        TextRenderer.DrawText(g,
            "Plug in the USB drive you want to back up to, then add it here. After that, your chosen files are backed up every time you plug it in.",
            Theme.UiSmall, new Rectangle(emptyRect.X + S(24), icon.Bottom + S(38), emptyRect.Width - S(48), S(40)), Theme.Muted,
            TextFormatFlags.WordBreak | TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPrefix);
    }

    void PaintCard(Graphics g)
    {
        var drive = Selected!;
        var found = Connection(drive);
        bool running = Busy(drive);
        Theme.FillRound(g, Theme.Surface, cardRect, S(12));

        var icon = new Rectangle(cardRect.X + S(14), cardRect.Y + S(14), S(44), S(44));
        Theme.FillRound(g, Theme.Thumb, icon, S(10));
        Theme.DrawGlyph(g, Glyphs.Usb, icon, found != null ? Theme.Accent : Theme.Muted, Theme.IconsLarge);

        int x = icon.Right + S(12), right = moreButton.Left - S(6);
        string status = running ? "Backing up" : found != null ? $"Plugged in as {found.Letter}:" : "Not plugged in";
        int statusW = Theme.TextWidth(g, status, Theme.UiSmall) + S(26);
        int nameW = Math.Min(Theme.TextWidth(g, drive.Name, Theme.UiLarge), right - x - statusW - S(10));
        Theme.DrawText(g, drive.Name, Theme.UiLarge, new Rectangle(x, cardRect.Y + S(12), nameW + S(2), S(24)), Theme.Text);
        var pill = new Rectangle(x + nameW + S(10), cardRect.Y + S(14), statusW, S(20));
        Theme.FillRound(g, Theme.SurfaceHover, pill, pill.Height / 2f);
        var dotColor = running ? Theme.Accent : found != null ? Theme.Success : Theme.Muted;
        using (var dot = new SolidBrush(dotColor))
            g.FillEllipse(dot, pill.X + S(9), pill.Y + (pill.Height - S(7)) / 2f, S(7), S(7));
        Theme.DrawText(g, status, Theme.UiSmall, new Rectangle(pill.X + S(20), pill.Y, pill.Width - S(22), pill.Height), Theme.Text);

        int textRight = backupButton.Left - S(10);
        if (running)
        {
            PaintRunning(g, drive, x, textRight);
            return;
        }
        bool low = found != null && Drives.LowOnSpace(found.Free, found.Total);
        string line2 = found == null ? "Backs up automatically when you plug it in"
            : low ? $"Almost full: {Fmt.Size(found.Free)} free  ·  clean up in …"
            : $"{Fmt.Size(found.Free)} free of {Fmt.Size(found.Total)}  ·  {found.Format}";
        Theme.DrawText(g, line2, Theme.UiSmall, new Rectangle(x, cardRect.Y + S(40), textRight - x, S(18)), low ? Theme.Warning : Theme.Muted);

        // Full width under the icon, so the summary never gets cut off by the button.
        var line3 = new Rectangle(cardRect.X + S(16), cardRect.Y + S(80), cardRect.Width - S(32), S(18));
        if (drive.LastProblems.Count > 0)
        {
            Theme.DrawGlyph(g, Glyphs.Warning, new Rectangle(line3.X, line3.Y, S(14), line3.Height), Theme.Warning, Theme.IconsSmall);
            line3 = new Rectangle(line3.X + S(18), line3.Y, line3.Width - S(18), line3.Height);
        }
        string last = drive.LastBackup is DateTime when ? $"Last backup {Fmt.When(when)}  ·  {drive.LastSummary}" : "No backup yet";
        Theme.DrawText(g, last, Theme.UiSmall, line3, drive.LastProblems.Count > 0 ? Theme.Warning : Theme.Muted);
    }

    /// <summary>Drive card while a backup runs: counts, the file being copied, and a progress bar across the card.</summary>
    void PaintRunning(Graphics g, BackupDrive drive, int x, int textRight)
    {
        if (!live.TryGetValue(drive.Id, out var backup))
        {
            // Still checking what changed (or counting down in the popup).
            Theme.DrawText(g, "Checking what changed…", Theme.UiSmall, new Rectangle(x, cardRect.Y + S(40), textRight - x, S(18)), Theme.Text);
            return;
        }
        Theme.DrawText(g, $"{backup.Done:N0} of {Fmt.Count(backup.Total, "file")}  ·  {Fmt.Size(backup.Bytes)} of {Fmt.Size(backup.TotalBytes)}",
            Theme.UiSmall, new Rectangle(x, cardRect.Y + S(40), textRight - x, S(18)), Theme.Text);
        Theme.DrawText(g, backup.File, Theme.UiSmall, new Rectangle(x, cardRect.Y + S(60), textRight - x, S(18)), Theme.Muted);
        var track = new Rectangle(cardRect.X + S(16), cardRect.Y + S(88), cardRect.Width - S(32), S(5));
        Theme.FillRound(g, Theme.Border, track, track.Height / 2f);
        double done = backup.TotalBytes > 0 ? Math.Min(1, backup.Bytes / (double)backup.TotalBytes)
            : backup.Total > 0 ? backup.Done / (double)backup.Total : 0;
        Theme.FillRound(g, Theme.Accent, new RectangleF(track.X, track.Y, Math.Max(track.Height, (float)(track.Width * done)), track.Height), track.Height / 2f);
    }

    void PaintSection(Graphics g)
    {
        var drive = Selected!;
        int titleW = Theme.TextWidth(g, "Back up these", Theme.UiBold);
        Theme.DrawText(g, "Back up these", Theme.UiBold, new Rectangle(sectionRect.X + S(2), sectionRect.Y, titleW + S(2), sectionRect.Height), Theme.Text);
        if (drive.Items.Count == 0) return;
        bool allKnown = drive.Items.All(i => sizes.ContainsKey(i.Path));
        long total = drive.Items.Sum(i => Math.Max(0, sizes.GetValueOrDefault(i.Path)));
        string summary = $"  ·  {Fmt.Count(drive.Items.Count, "item")}" + (allKnown ? $"  ·  {Fmt.Size(total)}" : "  ·  counting…");
        int x = sectionRect.X + S(2) + titleW;
        Theme.DrawText(g, summary, Theme.UiSmall, new Rectangle(x, sectionRect.Y, addFiles.Left - x - S(6), sectionRect.Height), Theme.Muted);
    }

    void PaintFooter(Graphics g)
    {
        string text;
        Color color = Theme.Muted;
        string glyph = Glyphs.Shield;
        if (flash != null)
        {
            text = flash;
            color = flashColor;
            glyph = flashColor == Theme.Success ? Glyphs.Check : flashColor == Theme.Muted ? Glyphs.Shield : Glyphs.Warning;
        }
        else if (Selected is { } drive && Connection(drive) is { } found)
            text = $"Copies go to {found.Letter}:\\{Drives.FolderName}\\{Drives.ComputerName}. Your originals are only read, never changed.";
        else if (config.Drives.Count > 0)
            text = "Backs up every time the drive is plugged in. Your originals are only read, never changed.";
        else
            text = "PlugSafe waits in the tray and uses no CPU until a backup drive is plugged in.";
        Theme.DrawGlyph(g, glyph, new Rectangle(footerRect.X, footerRect.Y, S(16), S(20)), color == Theme.Muted ? Theme.Success : color, Theme.IconsSmall);
        TextRenderer.DrawText(g, text, Theme.UiSmall, new Rectangle(footerRect.X + S(22), footerRect.Y + S(2), footerRect.Width - S(22), footerRect.Height),
            color, TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            headerIcon.Dispose();
            tips.Dispose();
            flashTimer.Dispose();
        }
        base.Dispose(disposing);
    }
}

/// <summary>Pill buttons for the backup drives, plus "Add drive".</summary>
sealed class DriveChips : PaintedControl
{
    public sealed record Chip(string Id, string Name, bool Connected);

    List<Chip> chips = new();
    string? selected;
    int hover = -1;
    readonly List<Rectangle> rects = new();

    public event Action<string>? SelectedChanged;
    public event Action<Point>? AddClicked;

    public DriveChips() { Cursor = Cursors.Hand; }

    public void Set(List<Chip> chips, string? selected)
    {
        this.chips = chips;
        this.selected = selected;
        Measure();
        Invalidate();
    }

    void Measure()
    {
        rects.Clear();
        int x = 0;
        foreach (var chip in chips)
        {
            int w = S(26) + TextRenderer.MeasureText(chip.Name, Theme.UiSmallBold, Size.Empty, TextFormatFlags.NoPadding).Width + S(14);
            rects.Add(new Rectangle(x, 0, w, Height));
            x += w + S(6);
        }
        rects.Add(new Rectangle(x, 0, S(30) + TextRenderer.MeasureText("Add drive", Theme.UiSmall, Size.Empty, TextFormatFlags.NoPadding).Width + S(12), Height));
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        Measure();
    }

    int IndexAt(Point p) => rects.FindIndex(r => r.Contains(p));

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        int i = IndexAt(e.Location);
        if (i != hover) { hover = i; Invalidate(); }
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        hover = -1;
        base.OnMouseLeave(e);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        int i = IndexAt(e.Location);
        if (e.Button != MouseButtons.Left || i < 0) return;
        if (i < chips.Count) SelectedChanged?.Invoke(chips[i].Id);
        else AddClicked?.Invoke(new Point(rects[i].X, rects[i].Bottom + S(4)));
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        if (rects.Count != chips.Count + 1) Measure();
        for (int i = 0; i < chips.Count; i++)
        {
            var r = rects[i];
            bool isSelected = chips[i].Id == selected;
            Theme.FillRound(g, isSelected ? Theme.Pressed : hover == i ? Theme.SurfaceHover : Theme.Surface, r, r.Height / 2f);
            using (var dot = new SolidBrush(chips[i].Connected ? Theme.Success : Theme.Border))
                g.FillEllipse(dot, r.X + S(12), r.Y + (r.Height - S(8)) / 2f, S(8), S(8));
            Theme.DrawText(g, chips[i].Name, Theme.UiSmallBold, new Rectangle(r.X + S(26), r.Y, r.Width - S(30), r.Height),
                isSelected ? Theme.Text : Theme.Muted);
        }
        var add = rects[^1];
        bool hot = hover == chips.Count;
        using (var pen = new Pen(hot ? Theme.Muted : Theme.Border))
        using (var path = Theme.RoundRect(new RectangleF(add.X + 0.5f, add.Y + 0.5f, add.Width - 1, add.Height - 1), add.Height / 2f))
            g.DrawPath(pen, path);
        Theme.DrawGlyph(g, Glyphs.Add, new Rectangle(add.X + S(10), add.Y, S(14), add.Height), hot ? Theme.Text : Theme.Muted, Theme.IconsSmall);
        Theme.DrawText(g, "Add drive", Theme.UiSmall, new Rectangle(add.X + S(28), add.Y, add.Width - S(30), add.Height), hot ? Theme.Text : Theme.Muted);
    }
}

/// <summary>
/// The chosen files and folders. Click a row to open it; during a backup each row shows its own progress.
/// The remove (×) button only takes an item off the list.
/// </summary>
sealed class ItemList : PaintedControl
{
    public const int RowH = 50;

    /// <param name="Progress">0..1 while a backup runs (null otherwise).</param>
    /// <param name="HoverText">What a click does, shown on hover ("Open backup" / "Open").</param>
    public sealed record Row(string Glyph, string Name, string Detail, string Size, bool Missing, bool Folder,
        double? Progress = null, string? ProgressText = null, string? HoverText = null);

    List<Row> rows = new();
    int scroll, hover = -1;
    bool hoverRemove;

    public IReadOnlyList<Row> Rows => rows;
    public event Action<int>? RemoveClicked;
    public event Action<int>? OpenClicked;
    public event Action<int, Point>? MenuRequested;

    public void SetRows(List<Row> rows)
    {
        this.rows = rows;
        ClampScroll();
        Invalidate();
    }

    int Content => rows.Count * S(RowH);
    bool Scrolls => Content - S(4) > Height;
    void ClampScroll() => scroll = Math.Clamp(scroll, 0, Math.Max(0, Content - S(4) - Height));
    Rectangle RowRect(int i) => new(0, i * S(RowH) - scroll, Width - (Scrolls ? S(10) : 0), S(RowH) - S(4));
    Rectangle RemoveRect(Rectangle row) => new(row.Right - S(38), row.Y + (row.Height - S(28)) / 2, S(28), S(28));

    void UpdateHover(Point p)
    {
        int i = rows.Count == 0 ? -1 : (p.Y + scroll) / S(RowH);
        if (i >= rows.Count || !ClientRectangle.Contains(p)) i = -1;
        bool onRemove = i >= 0 && rows[i].Progress == null && RemoveRect(RowRect(i)).Contains(p); // no removing mid-backup
        if (i != hover || onRemove != hoverRemove)
        {
            hover = i;
            hoverRemove = onRemove;
            Cursor = onRemove || (i >= 0 && rows[i].HoverText != null) ? Cursors.Hand : Cursors.Default;
            Invalidate();
        }
    }

    protected override void OnMouseMove(MouseEventArgs e) { base.OnMouseMove(e); UpdateHover(e.Location); }
    protected override void OnMouseLeave(EventArgs e) { hover = -1; hoverRemove = false; base.OnMouseLeave(e); }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        scroll -= e.Delta / 120 * S(RowH);
        ClampScroll();
        UpdateHover(e.Location);
        Invalidate();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (hover < 0) return;
        if (e.Button == MouseButtons.Right) MenuRequested?.Invoke(hover, e.Location);
        else if (e.Button == MouseButtons.Left && hoverRemove) RemoveClicked?.Invoke(hover);
        else if (e.Button == MouseButtons.Left && rows[hover].HoverText != null) OpenClicked?.Invoke(hover);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        if (rows.Count == 0)
        {
            Theme.FillRound(g, Theme.Surface, new RectangleF(0, 0, Width, Height), S(10));
            TextRenderer.DrawText(g, "Nothing chosen yet. Add the files and folders you want copied to this drive.", Theme.UiSmall,
                new Rectangle(S(20), 0, Width - S(40), Height), Theme.Muted,
                TextFormatFlags.WordBreak | TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
            return;
        }
        for (int i = 0; i < rows.Count; i++)
        {
            var r = RowRect(i);
            if (r.Bottom < 0 || r.Top > Height) continue;
            var row = rows[i];
            Theme.FillRound(g, hover == i ? Theme.SurfaceHover : Theme.Surface, r, S(8));
            Theme.DrawGlyph(g, row.Glyph, new Rectangle(r.X + S(12), r.Y, S(22), r.Height),
                row.Missing ? Theme.Error : row.Folder ? Theme.Warning : Theme.Muted);
            var sizeRect = new Rectangle(r.Right - S(44) - S(90), r.Y, S(90), r.Height);
            int textX = r.X + S(44), textW = sizeRect.Left - textX - S(8);
            Theme.DrawText(g, row.Name, Theme.UiBold, new Rectangle(textX, r.Y + S(6), textW, S(20)), Theme.Text);
            if (row.Progress is double progress)
            {
                // Backing up: this item's own progress bar in place of its path.
                var track = new Rectangle(textX, r.Y + S(31), textW, S(4));
                Theme.FillRound(g, Theme.Border, track, S(2));
                Theme.FillRound(g, progress >= 1 ? Theme.Success : Theme.Accent,
                    new RectangleF(track.X, track.Y, Math.Max(track.Height, (float)(track.Width * progress)), track.Height), S(2));
                Theme.DrawText(g, row.ProgressText ?? "", Theme.UiSmall, sizeRect, progress >= 1 ? Theme.Success : Theme.Text, TextFormatFlags.Right);
            }
            else
            {
                Theme.DrawText(g, row.Detail, Theme.UiSmall, new Rectangle(textX, r.Y + S(25), textW, S(17)), row.Missing ? Theme.Error : Theme.Muted,
                    TextFormatFlags.PathEllipsis);
                if (hover == i && !hoverRemove && row.HoverText != null)
                {
                    // Hover: say what a click does, e.g. "Open backup".
                    int w = Theme.TextWidth(g, row.HoverText, Theme.UiSmallBold);
                    Theme.DrawGlyph(g, Glyphs.OpenFile, new Rectangle(sizeRect.Right - w - S(20), r.Y, S(14), r.Height), Theme.Accent, Theme.IconsSmall);
                    Theme.DrawText(g, row.HoverText, Theme.UiSmallBold, sizeRect, Theme.Accent, TextFormatFlags.Right);
                }
                else Theme.DrawText(g, row.Size, Theme.UiSmall, sizeRect, Theme.Muted, TextFormatFlags.Right);
            }
            if (hover == i && row.Progress == null)
            {
                var x = RemoveRect(r);
                if (hoverRemove) Theme.FillRound(g, Theme.Pressed, x, S(6));
                Theme.DrawGlyph(g, Glyphs.Close, x, hoverRemove ? Theme.Text : Theme.Muted, Theme.IconsSmall);
            }
        }
        if (Scrolls)
        {
            int track = Height, thumb = Math.Max(S(24), track * track / Content);
            int y = (int)((track - thumb) * (scroll / (double)Math.Max(1, Content - S(4) - Height)));
            Theme.FillRound(g, Theme.Border, new Rectangle(Width - S(5), y, S(4), thumb), S(2));
        }
    }
}

/// <summary>What went wrong in the last backup, as plain text you can copy.</summary>
sealed class ProblemsForm : Form
{
    readonly string driveName;
    readonly int count;

    public ProblemsForm(string driveName, IReadOnlyList<string> problems)
    {
        this.driveName = driveName;
        count = problems.Count;
        Text = "Backup problems";
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false;
        AutoScaleMode = AutoScaleMode.None;
        BackColor = Theme.Bg;
        ForeColor = Theme.Text;
        KeyPreview = true;
        DoubleBuffered = true;
        ClientSize = new Size(S(540), S(380));

        var box = new TextBox
        {
            Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, BorderStyle = BorderStyle.None,
            BackColor = Theme.Surface, ForeColor = Theme.Text, Font = Theme.UiSmall,
            Text = string.Join("\r\n\r\n", problems),
            Bounds = new Rectangle(S(18), S(60), ClientSize.Width - S(36), ClientSize.Height - S(60) - S(66)),
        };
        box.HandleCreated += (_, _) => Native.SetWindowTheme(box.Handle, "DarkMode_Explorer", null);
        var close = new FlatButton { Text = "Close", Bounds = new Rectangle(ClientSize.Width - S(18) - S(100), ClientSize.Height - S(50), S(100), S(34)) };
        var copy = new FlatButton { Text = "Copy", Secondary = true, Bounds = new Rectangle(close.Left - S(8) - S(90), close.Top, S(90), S(34)) };
        close.Click += (_, _) => Close();
        copy.Click += (_, _) => { if (box.Text.Length > 0) Clipboard.SetText(box.Text); };
        Controls.AddRange(new Control[] { box, copy, close });
    }

    int S(int value) => LogicalToDeviceUnits(value);

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ClassStyle |= Native.CS_DROPSHADOW;
            return cp;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Native.StyleWindow(Handle);
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == Keys.Escape) { Close(); return true; }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        Theme.DrawGlyph(g, Glyphs.Warning, new Rectangle(S(18), S(14), S(20), S(26)), Theme.Warning);
        Theme.DrawText(g, $"Last backup: {Fmt.Count(count, "problem")}", Theme.UiLarge, new Rectangle(S(44), S(12), Width - S(60), S(28)), Theme.Text);
        Theme.DrawText(g, $"{driveName}. Skipped files are tried again next time.", Theme.UiSmall, new Rectangle(S(44), S(36), Width - S(60), S(18)), Theme.Muted);
    }
}
