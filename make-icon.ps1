# Generates src\icon.ico (blue rounded square with a white USB stick and a check mark) at all common sizes.
param([string]$Out = (Join-Path $PSScriptRoot 'src\icon.ico'))

Add-Type -ReferencedAssemblies System.Drawing -TypeDefinition @'
using System;
using System.IO;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

public static class PlugSafeIcon
{
    static float Snap(float v, int s) { return s <= 32 ? (float)Math.Round(v) : v; }

    static GraphicsPath Round(RectangleF r, float radius)
    {
        var path = new GraphicsPath();
        float d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    static Bitmap Draw(int s)
    {
        var bmp = new Bitmap(s, s, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.Clear(Color.Transparent);

            float m = s <= 24 ? 0f : s * 0.04f;
            var r = new RectangleF(m, m, s - 2 * m, s - 2 * m);
            using (var path = Round(r, r.Width * 0.22f))
            using (var b = new LinearGradientBrush(r, Color.FromArgb(255, 64, 156, 255), Color.FromArgb(255, 0, 96, 210), 90f))
                g.FillPath(b, path);

            float u = s / 16f, cx = s / 2f;
            var blue = Color.FromArgb(255, 0, 100, 215);
            using (var white = new SolidBrush(Color.White))
            {
                // connector
                var plug = new RectangleF(Snap(cx - 2.5f * u, s), Snap(2f * u, s), Snap(5f * u, s), Snap(3.6f * u, s));
                g.FillRectangle(white, plug);
                if (s >= 24)
                    using (var slot = new SolidBrush(blue))
                    {
                        g.FillRectangle(slot, cx - 1.6f * u, 3f * u, 1f * u, 1f * u);
                        g.FillRectangle(slot, cx + 0.6f * u, 3f * u, 1f * u, 1f * u);
                    }
                // body
                var body = new RectangleF(Snap(cx - 3.8f * u, s), Snap(5.4f * u, s), Snap(7.6f * u, s), Snap(8.8f * u, s));
                using (var path = Round(body, 1.6f * u)) g.FillPath(white, path);
            }
            // check mark on the body
            using (var pen = new Pen(blue, Math.Max(1.4f, 1.5f * u)) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
                g.DrawLines(pen, new[] { new PointF(cx - 2.0f * u, 9.9f * u), new PointF(cx - 0.5f * u, 11.4f * u), new PointF(cx + 2.1f * u, 8.2f * u) });
        }
        return bmp;
    }

    static byte[] Png(Bitmap b)
    {
        using (var ms = new MemoryStream()) { b.Save(ms, ImageFormat.Png); return ms.ToArray(); }
    }

    static byte[] Dib(Bitmap b)
    {
        int s = b.Width, maskStride = ((s + 31) / 32) * 4;
        using (var ms = new MemoryStream())
        using (var w = new BinaryWriter(ms))
        {
            w.Write(40); w.Write(s); w.Write(s * 2); w.Write((short)1); w.Write((short)32);
            w.Write(0); w.Write(s * s * 4 + maskStride * s); w.Write(0); w.Write(0); w.Write(0); w.Write(0);
            var data = b.LockBits(new Rectangle(0, 0, s, s), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            var row = new byte[s * 4];
            var clear = new bool[s, s];
            for (int y = s - 1; y >= 0; y--)
            {
                Marshal.Copy(new IntPtr(data.Scan0.ToInt64() + (long)y * data.Stride), row, 0, row.Length);
                for (int x = 0; x < s; x++) clear[x, y] = row[x * 4 + 3] == 0;
                w.Write(row);
            }
            b.UnlockBits(data);
            for (int y = s - 1; y >= 0; y--)
            {
                var mask = new byte[maskStride];
                for (int x = 0; x < s; x++) if (clear[x, y]) mask[x / 8] |= (byte)(0x80 >> (x % 8));
                w.Write(mask);
            }
            w.Flush();
            return ms.ToArray();
        }
    }

    public static void Save(string path)
    {
        int[] sizes = { 16, 20, 24, 32, 40, 48, 64, 128, 256 };
        var images = new byte[sizes.Length][];
        for (int i = 0; i < sizes.Length; i++)
            using (var b = Draw(sizes[i])) images[i] = sizes[i] >= 256 ? Png(b) : Dib(b);

        using (var fs = File.Create(path))
        using (var w = new BinaryWriter(fs))
        {
            w.Write((short)0); w.Write((short)1); w.Write((short)sizes.Length);
            int offset = 6 + 16 * sizes.Length;
            for (int i = 0; i < sizes.Length; i++)
            {
                byte dim = (byte)(sizes[i] >= 256 ? 0 : sizes[i]);
                w.Write(dim); w.Write(dim); w.Write((byte)0); w.Write((byte)0);
                w.Write((short)1); w.Write((short)32); w.Write(images[i].Length); w.Write(offset);
                offset += images[i].Length;
            }
            foreach (var img in images) w.Write(img);
        }
    }

    public static void Preview(string path)
    {
        using (var b = Draw(256)) b.Save(path, ImageFormat.Png);
    }
}
'@

[PlugSafeIcon]::Save($Out)
Write-Host "Wrote $Out"
