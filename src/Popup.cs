using System.Drawing.Drawing2D;

namespace PlugSafe;

/// <summary>
/// Toast in the bottom-right corner: drive connected → what changed → countdown → progress → done.
/// It never takes keyboard focus away from what you're doing.
/// </summary>
sealed class BackupPopup : Form
{
    static readonly List<BackupPopup> Open = new();
    public static int OpenCount => Open.Count;

    readonly FlatButton primary = new();
    readonly FlatButton secondary = new() { Secondary = true };
    readonly IconButton close = new(Glyphs.Close);
    readonly System.Windows.Forms.Timer anim = new() { Interval = 30 };
    readonly System.Windows.Forms.Timer tick = new() { Interval = 1000 };
    readonly string driveName;

    string title = "", body = "", detail = "", glyph = Glyphs.Usb;
    Color titleColor = Theme.Text, glyphColor = Theme.Accent, detailColor = Theme.Muted;
    double bar = -2; // -2 hidden, -1 indeterminate, 0..1 value
    float animPhase;
    long lastPaint;
    int countdown, countdownTotal, autoClose;
    Action? primaryAction, secondaryAction;
    TaskCompletionSource<bool>? decision;

    public event Action? StopRequested;

    public BackupPopup(string driveName)
    {
        this.driveName = driveName;
        Text = "PlugSafe";
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        TopMost = true;
        AutoScaleMode = AutoScaleMode.None;
        BackColor = Theme.Bg;
        ForeColor = Theme.Text;
        DoubleBuffered = true;
        ClientSize = new Size(S(380), S(162));

        close.Bounds = new Rectangle(ClientSize.Width - S(36), S(8), S(28), S(28));
        primary.Bounds = new Rectangle(ClientSize.Width - S(14) - S(150), S(118), S(150), S(32));
        secondary.Bounds = new Rectangle(primary.Left - S(8) - S(96), S(118), S(96), S(32));
        primary.Click += (_, _) => primaryAction?.Invoke();
        secondary.Click += (_, _) => secondaryAction?.Invoke();
        close.Click += (_, _) => { decision?.TrySetResult(false); Close(); };
        Controls.AddRange(new Control[] { primary, secondary, close });

        anim.Tick += (_, _) => { animPhase = (animPhase + 0.012f) % 1f; Invalidate(BarRect); };
        tick.Tick += (_, _) => OnTick();
    }

    int S(int value) => LogicalToDeviceUnits(value);
    Rectangle BarRect => new(S(14), S(104), ClientSize.Width - S(28), S(4));

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ClassStyle |= Native.CS_DROPSHADOW;
            cp.ExStyle |= Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE | Native.WS_EX_TOPMOST;
            return cp;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Native.StyleWindow(Handle);
    }

    void Present()
    {
        if (IsDisposed) return;
        if (!Visible)
        {
            Open.Add(this);
            var area = Screen.PrimaryScreen!.WorkingArea;
            int stack = (Open.Count - 1) * (Height + S(10));
            Location = new Point(area.Right - Width - S(16), area.Bottom - Height - S(16) - stack);
            Show();
        }
        Invalidate();
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        Open.Remove(this);
        anim.Stop();
        tick.Stop();
        decision?.TrySetResult(false);
        base.OnFormClosed(e);
    }

    void Set(string title, string body, string detail, Color titleColor, string glyph, Color glyphColor, double bar)
    {
        this.title = title;
        this.body = body;
        this.detail = detail;
        this.titleColor = titleColor;
        this.glyph = glyph;
        this.glyphColor = glyphColor;
        this.bar = bar;
        detailColor = Theme.Muted;
        anim.Enabled = bar == -1;
    }

    /// <summary>Replaces the small line under the summary with an orange warning (e.g. drive almost full).</summary>
    void Warn(string? warning)
    {
        if (warning == null) return;
        detail = warning;
        detailColor = Theme.Warning;
    }

    void Buttons(string? primaryText, Action? onPrimary, string? secondaryText, Action? onSecondary)
    {
        primary.Visible = primaryText != null;
        primary.Text = primaryText ?? "";
        primaryAction = onPrimary;
        secondary.Visible = secondaryText != null;
        secondary.Text = secondaryText ?? "";
        secondaryAction = onSecondary;
        if (primaryText == null) secondary.Left = ClientSize.Width - S(14) - secondary.Width;
        else secondary.Left = primary.Left - S(8) - secondary.Width;
    }

    // ---------------------------------------------------------------- states

    public void ShowChecking()
    {
        if (IsDisposed) return;
        Set("Backup drive connected", "Checking your files…", "", Theme.Text, Glyphs.Usb, Theme.Accent, -1);
        Buttons(null, null, "Cancel", () => StopRequested?.Invoke());
        Present();
    }

    /// <summary>"N files changed": waits for the countdown (or the user) and returns whether to go ahead.</summary>
    public Task<bool> AskToStart(ScanResult scan, StartMode mode, int seconds)
    {
        if (IsDisposed) return Task.FromResult(false);
        decision = new TaskCompletionSource<bool>();
        string what = scan.New == scan.Total
            ? $"First backup: {Fmt.Count(scan.Total, "file")}  ·  {Fmt.Size(scan.ChangedBytes)}"
            : $"{scan.Changed.Count:N0} of {Fmt.Count(scan.Total, "file")} changed  ·  {Fmt.Size(scan.ChangedBytes)}";
        if (mode == StartMode.Ask)
        {
            Set("Back up now?", what, "Your originals are only read, never changed.", Theme.Text, Glyphs.Usb, Theme.Accent, -2);
            Buttons("Back up", () => decision.TrySetResult(true), "Not now", () => { decision.TrySetResult(false); Close(); });
        }
        else
        {
            if (seconds <= 0) return Task.FromResult(true);
            countdown = countdownTotal = seconds;
            Set("Backup drive connected", what, "Starting automatically…", Theme.Text, Glyphs.Usb, Theme.Accent, 1);
            Buttons($"Back up now ({countdown})", () => decision.TrySetResult(true), "Skip", () => { decision.TrySetResult(false); Close(); });
            tick.Start();
        }
        Present();
        return decision.Task;
    }

    public void ShowRunning(ScanResult scan)
    {
        if (IsDisposed) return;
        tick.Stop();
        Set("Backing up…", $"0 of {Fmt.Count(scan.Changed.Count, "file")}  ·  0 B of {Fmt.Size(scan.ChangedBytes)}", "",
            Theme.Text, Glyphs.Usb, Theme.Accent, 0);
        Buttons(null, null, "Stop", () => StopRequested?.Invoke());
        Present();
    }

    public void Report(BackupProgress p)
    {
        if (IsDisposed) return;
        body = $"{p.Done:N0} of {Fmt.Count(p.Total, "file")}  ·  {Fmt.Size(p.Bytes)} of {Fmt.Size(p.TotalBytes)}";
        detail = p.File;
        bar = p.TotalBytes > 0 ? Math.Min(1, p.Bytes / (double)p.TotalBytes) : p.Total > 0 ? p.Done / (double)p.Total : 0;
        long now = Environment.TickCount64;
        if (now - lastPaint > 60) { lastPaint = now; Invalidate(); }
    }

    public void ShowDone(BackupResult result, Action openBackup, Action showDetails, string? warning = null)
    {
        if (IsDisposed) return;
        if (result.StoppedBecause != null)
        {
            Set("Backup stopped", result.StoppedBecause, result.Summary, Theme.Warning, Glyphs.Warning, Theme.Warning, -2);
            Buttons("Open backup", openBackup, "Close", Close);
            AutoClose(20);
        }
        else if (result.Problems.Count > 0)
        {
            Set("Backed up, with a few problems", result.Summary, "Safe to unplug.", Theme.Warning, Glyphs.Warning, Theme.Warning, -2);
            Buttons("Open backup", openBackup, "Details", () => { showDetails(); Close(); });
            AutoClose(20);
        }
        else
        {
            Set("Backup complete", result.Summary, "Safe to unplug.", Theme.Success, Glyphs.Check, Theme.Success, -2);
            Buttons("Open backup", openBackup, "Close", Close);
            AutoClose(warning == null ? 10 : 20);
        }
        Warn(warning);
        Present();
    }

    public void ShowUpToDate(ScanResult scan, Action openBackup, string? warning = null)
    {
        if (IsDisposed) return;
        string note = scan.Problems.Count > 0 ? scan.Problems[0] : "Safe to unplug.";
        Set("Already backed up", $"All {Fmt.Count(scan.Total, "file")} are up to date", note,
            Theme.Success, Glyphs.Check, Theme.Success, -2);
        Buttons("Open backup", openBackup, "Close", Close);
        AutoClose(warning == null ? 6 : 20);
        Warn(warning);
        Present();
    }

    public void ShowMessage(string title, string message, Color color, string icon, string? actionText = null, Action? action = null,
        string detail = "")
    {
        if (IsDisposed) return;
        tick.Stop();
        Set(title, message, detail, color, icon, color, -2);
        Buttons(actionText, action == null ? null : () => { action(); Close(); }, "Close", Close);
        AutoClose(20);
        Present();
    }

    void AutoClose(int seconds)
    {
        autoClose = seconds;
        countdown = 0;
        tick.Start();
    }

    void OnTick()
    {
        if (countdown > 0)
        {
            countdown--;
            bar = countdown / (double)Math.Max(1, countdownTotal);
            primary.Text = $"Back up now ({countdown})";
            primary.Invalidate();
            Invalidate(BarRect);
            if (countdown == 0) { tick.Stop(); decision?.TrySetResult(true); }
            return;
        }
        if (autoClose <= 0) return;
        // Don't vanish while the mouse is on it.
        if (ClientRectangle.Contains(PointToClient(Cursor.Position))) return;
        if (--autoClose == 0) Close();
    }

    // ---------------------------------------------------------------- painting

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left) return;
        Native.ReleaseCapture();
        Native.SendMessage(Handle, Native.WM_NCLBUTTONDOWN, Native.HTCAPTION, IntPtr.Zero);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        int pad = S(14), w = ClientSize.Width;

        var icon = new Rectangle(pad, pad, S(40), S(40));
        Theme.FillRound(g, Theme.Surface, icon, S(10));
        Theme.DrawGlyph(g, glyph, icon, glyphColor, Theme.IconsLarge);
        int x = icon.Right + S(12);
        Theme.DrawText(g, title, Theme.UiBold, new Rectangle(x, S(13), close.Left - x - S(4), S(22)), titleColor);
        Theme.DrawText(g, driveName, Theme.UiSmall, new Rectangle(x, S(34), close.Left - x - S(4), S(18)), Theme.Muted);

        Theme.DrawText(g, body, Theme.Ui, new Rectangle(pad, S(62), w - 2 * pad, S(20)), Theme.Text);
        Theme.DrawText(g, detail, Theme.UiSmall, new Rectangle(pad, S(81), w - 2 * pad, S(18)), detailColor);

        if (bar > -2)
        {
            var r = BarRect;
            float radius = r.Height / 2f;
            Theme.FillRound(g, Theme.Border, r, radius);
            if (bar >= 0)
            {
                Theme.FillRound(g, Theme.Accent, new RectangleF(r.X, r.Y, Math.Max(r.Height, (float)(r.Width * bar)), r.Height), radius);
            }
            else
            {
                float seg = r.Width * 0.3f;
                using var clip = Theme.RoundRect(r, radius);
                var saved = g.Save();
                g.SetClip(clip);
                Theme.FillRound(g, Theme.Accent, new RectangleF(r.X - seg + animPhase * (r.Width + seg), r.Y, seg, r.Height), radius);
                g.Restore(saved);
            }
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            anim.Dispose();
            tick.Dispose();
        }
        base.Dispose(disposing);
    }
}
