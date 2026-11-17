using System.Drawing.Drawing2D;

namespace PlugSafe;

/// <summary>Base for the custom-painted, non-focusable controls.</summary>
abstract class PaintedControl : Control
{
    protected bool Hover, Down;

    protected PaintedControl()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        SetStyle(ControlStyles.Selectable, false);
        BackColor = Theme.Bg;
        ForeColor = Theme.Text;
    }

    protected int S(int value) => LogicalToDeviceUnits(value);

    protected override void OnMouseEnter(EventArgs e) { Hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { Hover = Down = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { if (e.Button == MouseButtons.Left) { Down = true; Invalidate(); } base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e) { Down = false; Invalidate(); base.OnMouseUp(e); }
    protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }
}

sealed class FlatButton : PaintedControl
{
    public string? Glyph;
    public bool Secondary;

    public FlatButton()
    {
        Font = Theme.UiBold;
        Cursor = Cursors.Hand;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        Color bg, fg = Theme.Text;
        if (!Enabled) { bg = Theme.Surface; fg = Theme.Muted; }
        else if (Secondary) bg = Down ? Theme.Pressed : Hover ? Theme.SurfaceHover : Theme.Surface;
        else bg = Down ? Theme.AccentDown : Hover ? Theme.AccentHover : Theme.Accent;
        Theme.FillRound(g, bg, new RectangleF(0, 0, Width, Height), S(8));

        const TextFormatFlags flags = TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine |
                                      TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding;
        var textSize = TextRenderer.MeasureText(g, Text, Font, Size.Empty, flags);
        int glyphW = Glyph == null ? 0 : S(16) + S(6);
        int x = (Width - textSize.Width - glyphW) / 2;
        if (Glyph != null) Theme.DrawGlyph(g, Glyph, new Rectangle(x, 0, S(16), Height), fg, Font.Size < 9.5f ? Theme.IconsSmall : null);
        TextRenderer.DrawText(g, Text, Font, new Rectangle(x + glyphW, 0, textSize.Width + S(2), Height), fg, flags);
    }
}

sealed class IconButton : PaintedControl
{
    readonly string glyph;

    public IconButton(string glyph)
    {
        this.glyph = glyph;
        Cursor = Cursors.Hand;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        if (Hover) Theme.FillRound(g, Down ? Theme.Pressed : Theme.SurfaceHover, new RectangleF(0, 0, Width, Height), S(6));
        Theme.DrawGlyph(g, glyph, ClientRectangle, Hover ? Theme.Text : Theme.Muted);
    }
}

/// <summary>Dark context menus to match the windows.</summary>
sealed class DarkRenderer : ToolStripProfessionalRenderer
{
    public DarkRenderer() { RoundedEdges = false; }

    protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
    {
        using var b = new SolidBrush(Theme.Surface);
        e.Graphics.FillRectangle(b, e.AffectedBounds);
    }

    protected override void OnRenderImageMargin(ToolStripRenderEventArgs e)
    {
        using var b = new SolidBrush(Theme.Surface);
        e.Graphics.FillRectangle(b, e.AffectedBounds);
    }

    protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
    {
        using var p = new Pen(Theme.Border);
        e.Graphics.DrawRectangle(p, 0, 0, e.AffectedBounds.Width - 1, e.AffectedBounds.Height - 1);
    }

    protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
    {
        if (!e.Item.Selected || !e.Item.Enabled) return;
        using var b = new SolidBrush(Theme.Pressed);
        e.Graphics.FillRectangle(b, new Rectangle(3, 1, e.Item.Width - 6, e.Item.Height - 2));
    }

    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        bool isShortcut = e.Item is ToolStripMenuItem mi && e.Text == mi.ShortcutKeyDisplayString;
        e.TextColor = !e.Item.Enabled || isShortcut ? Theme.Muted : Theme.Text;
        base.OnRenderItemText(e);
    }

    protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e) =>
        Theme.DrawGlyph(e.Graphics, Glyphs.Check, e.ImageRectangle, Theme.Text, Theme.IconsSmall);

    protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
    {
        int y = e.Item.Height / 2;
        using var p = new Pen(Theme.Border);
        e.Graphics.DrawLine(p, 8, y, e.Item.Width - 8, y);
    }

    protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
    {
        e.ArrowColor = Theme.Text;
        base.OnRenderArrow(e);
    }
}

/// <summary>Small dark yes/no dialog in the same style as everything else.</summary>
sealed class ConfirmForm : Form
{
    readonly string title, message;

    public ConfirmForm(string title, string message, string okText, string cancelText = "Cancel", bool danger = false)
    {
        this.title = title;
        this.message = message;
        Text = title;
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false;
        AutoScaleMode = AutoScaleMode.None;
        BackColor = Theme.Bg;
        ForeColor = Theme.Text;
        KeyPreview = true;
        DoubleBuffered = true;
        TopMost = true;

        int w = S(400), pad = S(18), bh = S(34);
        using (var g = CreateGraphics())
        {
            var textSize = TextRenderer.MeasureText(g, message, Theme.Ui, new Size(w - 2 * pad, 0),
                TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
            messageRect = new Rectangle(pad, S(48), w - 2 * pad, textSize.Height + S(4));
        }
        int y = messageRect.Bottom + S(18);
        var ok = new FlatButton { Text = okText, Bounds = new Rectangle(w - pad - S(130), y, S(130), bh) };
        var cancel = new FlatButton { Text = cancelText, Secondary = true, Bounds = new Rectangle(ok.Left - S(8) - S(96), y, S(96), bh) };
        if (danger) ok.Secondary = true;
        ok.Click += (_, _) => DialogResult = DialogResult.OK;
        cancel.Click += (_, _) => DialogResult = DialogResult.Cancel;
        Controls.Add(ok);
        Controls.Add(cancel);
        ClientSize = new Size(w, y + bh + pad);
    }

    readonly Rectangle messageRect;

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
        if (keyData == Keys.Escape) { DialogResult = DialogResult.Cancel; return true; }
        if (keyData == Keys.Enter) { DialogResult = DialogResult.OK; return true; }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        int pad = S(18);
        Theme.DrawText(g, title, Theme.UiLarge, new Rectangle(pad, S(12), Width - 2 * pad, S(30)), Theme.Text);
        TextRenderer.DrawText(g, message, Theme.Ui, messageRect, Theme.Muted, TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
    }

    public static bool Ask(IWin32Window? owner, string title, string message, string okText, bool danger = false)
    {
        using var form = new ConfirmForm(title, message, okText, danger: danger);
        if (owner == null) form.StartPosition = FormStartPosition.CenterScreen;
        return form.ShowDialog(owner) == DialogResult.OK;
    }
}
