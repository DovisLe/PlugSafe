using System.Drawing.Drawing2D;
using System.Globalization;

namespace PlugSafe;

/// <summary>One day-folder under Versions (made by PlugSafe, named "yyyy-MM-dd" or "yyyy-MM-dd HH-mm").</summary>
sealed record VersionDay(DateTime Day, string Folder, long Bytes, int Files);

/// <summary>
/// Frees space by deleting old version folders, and nothing else: never Latest, never anything outside
/// PlugSafe\&lt;this PC&gt;\Versions, never a folder PlugSafe didn't name, and every file goes through the guard.
/// </summary>
static class VersionCleaner
{
    public static List<VersionDay> Scan(string versionsFolder, CancellationToken ct = default)
    {
        var days = new List<VersionDay>();
        if (!Directory.Exists(versionsFolder)) return days;
        foreach (var dir in new DirectoryInfo(versionsFolder).EnumerateDirectories())
        {
            ct.ThrowIfCancellationRequested();
            if (dir.Attributes.HasFlag(FileAttributes.ReparsePoint)) continue;
            if (!TryParseDay(dir.Name, out var day)) continue; // not ours: leave it alone
            long bytes = 0;
            int files = 0;
            foreach (var file in FileWalker.Files(dir, FileWalker.NoSkips, ct)) { bytes += file.Length; files++; }
            days.Add(new VersionDay(day, dir.FullName, bytes, files));
        }
        return days.OrderBy(d => d.Day).ToList();
    }

    static bool TryParseDay(string name, out DateTime day) =>
        DateTime.TryParseExact(name.Length >= 10 ? name[..10] : name, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out day) &&
        (name.Length == 10 || (name.Length == 16 && name[10] == ' '));

    public static (int Files, long Bytes) Delete(TargetGuard guard, string versionsFolder, IEnumerable<VersionDay> days, CancellationToken ct = default)
    {
        string versions = Path.TrimEndingDirectorySeparator(Path.GetFullPath(versionsFolder));
        if (!Path.GetFileName(versions).Equals("Versions", StringComparison.Ordinal))
            throw new SafetyException("Clean-up only works inside a Versions folder.");
        int files = 0;
        long bytes = 0;
        foreach (var day in days)
        {
            string folder = guard.Check(day.Folder);
            if (!Path.GetDirectoryName(folder)!.Equals(versions, StringComparison.OrdinalIgnoreCase) || !TryParseDay(Path.GetFileName(folder), out _))
                throw new SafetyException($"Refused to clean up \"{folder}\": it isn't one of PlugSafe's version folders.");
            foreach (var file in FileWalker.Files(new DirectoryInfo(folder), FileWalker.NoSkips, ct).ToList())
            {
                ct.ThrowIfCancellationRequested();
                string path = guard.Check(file.FullName);
                if (file.IsReadOnly) file.IsReadOnly = false;
                long length = file.Length;
                File.Delete(path);
                files++;
                bytes += length;
            }
            // Remove the now-empty folders, deepest first, without stepping into links. Anything unexpected left
            // inside (such as a link) keeps its folder.
            var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
            foreach (var dir in Directory.EnumerateDirectories(folder, "*", options).ToList().OrderByDescending(d => d.Length).Append(folder))
            {
                try { Directory.Delete(guard.Check(dir), recursive: false); }
                catch (IOException) { }
            }
        }
        Log.Write($"Cleaned up {files} old version files ({Fmt.Size(bytes)}) in {versions}");
        return (files, bytes);
    }
}

/// <summary>Should we nudge the user to plug the drive in? Checked only at sign-in and after sleep.</summary>
static class Reminders
{
    public static bool Due(BackupDrive drive, int reminderDays, DateTime now, bool pluggedIn)
    {
        if (reminderDays <= 0 || drive.Items.Count == 0 || pluggedIn) return false;
        if (drive.LastReminder?.Date == now.Date) return false; // at most once a day
        var since = drive.LastBackup ?? drive.Added ?? now;
        return (now - since).TotalDays >= reminderDays;
    }

    public static int DaysSince(BackupDrive drive, DateTime now) => (int)(now - (drive.LastBackup ?? drive.Added ?? now)).TotalDays;
}

/// <summary>Pick how old versions must be to go, see how much space that frees, confirm.</summary>
sealed class CleanupForm : Form
{
    static readonly (string Label, int Months)[] Choices = { ("1 month", 1), ("3 months", 3), ("6 months", 6), ("1 year", 12) };

    readonly string driveName, versionsFolder;
    readonly TargetGuard guard;
    readonly FlatButton delete = new() { Text = "Delete" };
    readonly FlatButton cancel = new() { Text = "Cancel", Secondary = true };
    List<VersionDay>? days;
    int selected = 2, hover = -1;
    bool busy;
    string status = "Counting old versions…";

    public long FreedBytes { get; private set; }

    public CleanupForm(string driveName, string versionsFolder, TargetGuard guard)
    {
        this.driveName = driveName;
        this.versionsFolder = versionsFolder;
        this.guard = guard;
        Text = "Clean up old versions";
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false;
        AutoScaleMode = AutoScaleMode.None;
        BackColor = Theme.Bg;
        ForeColor = Theme.Text;
        KeyPreview = true;
        DoubleBuffered = true;
        ClientSize = new Size(S(440), S(372));
        delete.Bounds = new Rectangle(ClientSize.Width - S(18) - S(170), ClientSize.Height - S(52), S(170), S(34));
        cancel.Bounds = new Rectangle(delete.Left - S(8) - S(92), delete.Top, S(92), S(34));
        delete.Enabled = false;
        delete.Click += (_, _) => DeleteSelected();
        cancel.Click += (_, _) => Close();
        Controls.Add(delete);
        Controls.Add(cancel);
    }

    int S(int value) => LogicalToDeviceUnits(value);
    Rectangle Row(int i) => new(S(18), S(96) + i * S(46), ClientSize.Width - S(36), S(40));

    protected override async void OnShown(EventArgs e)
    {
        base.OnShown(e);
        days = await Task.Run(() => VersionCleaner.Scan(versionsFolder));
        status = days.Count == 0
            ? "There are no old versions on this drive yet."
            : $"Old versions: {Fmt.Size(days.Sum(d => d.Bytes))} from {Fmt.Count(days.Count, "day")}. Your latest copies are never touched.";
        UpdateButton();
        Invalidate();
    }

    IEnumerable<VersionDay> OlderThan(int months) =>
        days?.Where(d => d.Day < DateTime.Today.AddMonths(-months)) ?? Enumerable.Empty<VersionDay>();

    void UpdateButton()
    {
        long bytes = OlderThan(Choices[selected].Months).Sum(d => d.Bytes);
        delete.Text = bytes > 0 ? $"Delete {Fmt.Size(bytes)}" : "Nothing to delete";
        delete.Enabled = bytes > 0 && !busy;
        delete.Invalidate();
    }

    async void DeleteSelected()
    {
        var picked = OlderThan(Choices[selected].Months).ToList();
        long bytes = picked.Sum(d => d.Bytes);
        if (picked.Count == 0) return;
        DateTime before = DateTime.Today.AddMonths(-Choices[selected].Months);
        if (!ConfirmForm.Ask(this, $"Delete {Fmt.Size(bytes)} of old versions?",
                $"Versions saved before {before:d MMMM yyyy} ({Fmt.Count(picked.Sum(d => d.Files), "file")}) will be deleted from {driveName}. " +
                "Your latest copies and the files on your PC are not touched.\n\nUSB drives have no Recycle Bin, so this can't be undone.",
                "Delete", danger: true))
            return;
        busy = true;
        delete.Enabled = cancel.Enabled = false;
        status = "Deleting old versions…";
        Invalidate();
        try
        {
            var (files, freed) = await Task.Run(() => VersionCleaner.Delete(guard, versionsFolder, picked));
            FreedBytes = freed;
            DialogResult = DialogResult.OK;
        }
        catch (Exception ex)
        {
            Log.Write("Clean-up failed: " + ex.Message);
            busy = false;
            status = "Clean-up stopped: " + ex.Message;
            cancel.Enabled = true;
            UpdateButton();
            Invalidate();
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        int i = Enumerable.Range(0, Choices.Length).FirstOrDefault(i => Row(i).Contains(e.Location), -1);
        if (i != hover) { hover = i; Cursor = i >= 0 ? Cursors.Hand : Cursors.Default; Invalidate(); }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (busy || hover < 0 || e.Button != MouseButtons.Left) return;
        selected = hover;
        UpdateButton();
        Invalidate();
    }

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
        if (keyData == Keys.Escape && !busy) { Close(); return true; }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        int pad = S(18), w = ClientSize.Width;
        Theme.DrawText(g, "Clean up old versions", Theme.UiLarge, new Rectangle(pad, S(12), w - 2 * pad, S(28)), Theme.Text);
        Theme.DrawText(g, driveName, Theme.UiSmall, new Rectangle(pad, S(38), w - 2 * pad, S(18)), Theme.Muted);
        TextRenderer.DrawText(g, status, Theme.UiSmall, new Rectangle(pad, S(58), w - 2 * pad, S(34)), Theme.Muted,
            TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);

        for (int i = 0; i < Choices.Length; i++)
        {
            var r = Row(i);
            bool isSelected = i == selected;
            Theme.FillRound(g, isSelected ? Theme.Pressed : hover == i ? Theme.SurfaceHover : Theme.Surface, r, S(8));
            // radio dot
            var dot = new Rectangle(r.X + S(14), r.Y + (r.Height - S(14)) / 2, S(14), S(14));
            using (var pen = new Pen(isSelected ? Theme.Accent : Theme.Muted, S(2) * 0.75f)) g.DrawEllipse(pen, dot);
            if (isSelected) using (var fill = new SolidBrush(Theme.Accent)) g.FillEllipse(fill, Rectangle.Inflate(dot, -S(4), -S(4)));
            Theme.DrawText(g, $"Older than {Choices[i].Label}", Theme.Ui, new Rectangle(r.X + S(38), r.Y, r.Width - S(160), r.Height), Theme.Text);
            var older = OlderThan(Choices[i].Months).ToList();
            string frees = days == null ? "…" : older.Count == 0 ? "nothing" : $"frees {Fmt.Size(older.Sum(d => d.Bytes))}";
            Theme.DrawText(g, frees, Theme.UiSmall, new Rectangle(r.Right - S(150), r.Y, S(136), r.Height),
                older.Count == 0 ? Theme.Muted : Theme.Text, TextFormatFlags.Right);
        }
    }
}

/// <summary>Edit the list of folder names that are never backed up.</summary>
sealed class SkipForm : Form
{
    readonly TextBox box;
    readonly Rectangle listArea;

    public List<string> Result { get; private set; } = new();

    public SkipForm(IEnumerable<string> current)
    {
        Text = "Skipped folders";
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false;
        AutoScaleMode = AutoScaleMode.None;
        BackColor = Theme.Bg;
        ForeColor = Theme.Text;
        KeyPreview = true;
        DoubleBuffered = true;
        ClientSize = new Size(S(400), S(380));

        listArea = new Rectangle(S(18), S(96), ClientSize.Width - S(36), ClientSize.Height - S(96) - S(68));
        box = new TextBox
        {
            Multiline = true, ScrollBars = ScrollBars.Vertical, AcceptsReturn = true, BorderStyle = BorderStyle.None,
            BackColor = Theme.Surface, ForeColor = Theme.Text, Font = Theme.Ui,
            Text = string.Join("\r\n", current),
            Bounds = Rectangle.Inflate(listArea, -S(10), -S(8)),
        };
        box.HandleCreated += (_, _) => Native.SetWindowTheme(box.Handle, "DarkMode_Explorer", null);
        Shown += (_, _) => box.Select(box.TextLength, 0); // cursor at the end, nothing pre-selected
        var save = new FlatButton { Text = "Save", Bounds = new Rectangle(ClientSize.Width - S(18) - S(92), ClientSize.Height - S(52), S(92), S(34)) };
        var cancel = new FlatButton { Text = "Cancel", Secondary = true, Bounds = new Rectangle(save.Left - S(8) - S(88), save.Top, S(88), S(34)) };
        var reset = new FlatButton { Text = "Defaults", Secondary = true, Bounds = new Rectangle(S(18), save.Top, S(96), S(34)) };
        save.Click += (_, _) =>
        {
            Result = box.Lines.Select(l => l.Trim().Trim('\\', '/')).Where(l => l.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            DialogResult = DialogResult.OK;
        };
        cancel.Click += (_, _) => Close();
        reset.Click += (_, _) => box.Text = string.Join("\r\n", Config.DefaultSkipped);
        Controls.AddRange(new Control[] { box, reset, cancel, save });
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
        g.SmoothingMode = SmoothingMode.AntiAlias;
        int pad = S(18);
        Theme.DrawText(g, "Skipped folders", Theme.UiLarge, new Rectangle(pad, S(12), Width - 2 * pad, S(28)), Theme.Text);
        TextRenderer.DrawText(g, "Folders with these names are never backed up, wherever they are inside your chosen folders. One name per line.",
            Theme.UiSmall, new Rectangle(pad, S(44), Width - 2 * pad, S(44)), Theme.Muted,
            TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
        Theme.FillRound(g, Theme.Surface, listArea, S(8));
    }
}
