# 生成 src/QuotaChecker/app.ico —— 极简 Fluent 风格：蓝色圆角方块 + 白色 ¥
# 用法：powershell -ExecutionPolicy Bypass -File tools\make-icon.ps1
param(
    [string]$OutIco = (Join-Path $PSScriptRoot '..\src\QuotaChecker\app.ico'),
    [string]$PreviewPng = '',
    [string]$StripPng = ''
)

Add-Type -AssemblyName System.Drawing
Add-Type -ReferencedAssemblies System.Drawing @"
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Drawing.Imaging;
using System.IO;

public static class IconForge
{
    static GraphicsPath RoundedRect(RectangleF r, float radius)
    {
        float d = radius * 2f;
        GraphicsPath p = new GraphicsPath();
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    public static Bitmap Render(int size)
    {
        Bitmap bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using (Graphics g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.Clear(Color.Transparent);

            // 背景：Fluent 蓝渐变圆角方块
            float inset = size * 0.02f;
            RectangleF body = new RectangleF(inset, inset, size - inset * 2f, size - inset * 2f);
            float radius = size * 0.22f;
            using (GraphicsPath path = RoundedRect(body, radius))
            using (LinearGradientBrush bg = new LinearGradientBrush(body,
                       Color.FromArgb(255, 0x35, 0x8F, 0xE4),
                       Color.FromArgb(255, 0x0A, 0x54, 0xA0), 90f))
            {
                g.FillPath(bg, path);
            }

            // 前景：白色 ¥
            string glyph = "\u00A5";
            float fontSize = size * 0.66f;
            FontStyle style = size <= 20 ? FontStyle.Bold : FontStyle.Regular;
            using (FontFamily family = new FontFamily("Segoe UI"))
            using (Font font = new Font(family, fontSize, style, GraphicsUnit.Pixel))
            using (StringFormat sf = new StringFormat(StringFormat.GenericTypographic))
            {
                sf.Alignment = StringAlignment.Center;
                sf.LineAlignment = StringAlignment.Center;

                SizeF measured = g.MeasureString(glyph, font, int.MaxValue, sf);
                float cx = size / 2f;
                float cy = size / 2f + measured.Height * 0.02f;

                using (SolidBrush fg = new SolidBrush(Color.White))
                {
                    g.DrawString(glyph, font, fg, new PointF(cx, cy), sf);
                }
            }
        }
        return bmp;
    }

    public static void Build(string icoPath, int[] sizes)
    {
        MemoryStream[] pngs = new MemoryStream[sizes.Length];
        for (int i = 0; i < sizes.Length; i++)
        {
            using (Bitmap bmp = Render(sizes[i]))
            {
                MemoryStream ms = new MemoryStream();
                bmp.Save(ms, ImageFormat.Png);
                pngs[i] = ms;
            }
        }

        string dir = Path.GetDirectoryName(Path.GetFullPath(icoPath));
        Directory.CreateDirectory(dir);
        using (FileStream fs = new FileStream(icoPath, FileMode.Create, FileAccess.Write))
        using (BinaryWriter w = new BinaryWriter(fs))
        {
            w.Write((ushort)0);                 // reserved
            w.Write((ushort)1);                 // type = icon
            w.Write((ushort)sizes.Length);      // image count

            int offset = 6 + 16 * sizes.Length;
            for (int i = 0; i < sizes.Length; i++)
            {
                int s = sizes[i];
                w.Write((byte)(s >= 256 ? 0 : s));   // width
                w.Write((byte)(s >= 256 ? 0 : s));   // height
                w.Write((byte)0);                    // palette
                w.Write((byte)0);                    // reserved
                w.Write((ushort)1);                  // planes
                w.Write((ushort)32);                 // bpp
                w.Write((int)pngs[i].Length);
                w.Write(offset);
                offset += (int)pngs[i].Length;
            }

            for (int i = 0; i < sizes.Length; i++)
            {
                pngs[i].Position = 0;
                pngs[i].CopyTo(fs);
                pngs[i].Dispose();
            }
        }
    }
}
"@

$sizes = @(16, 20, 24, 32, 40, 48, 64, 128, 256)
[IconForge]::Build($OutIco, $sizes)
Write-Output "icon   : $([math]::Round((Get-Item $OutIco).Length / 1KB, 1)) KB, $($sizes.Count) sizes"

if ($PreviewPng -ne '') {
    $b = [IconForge]::Render(256)
    $b.Save($PreviewPng, [System.Drawing.Imaging.ImageFormat]::Png)
    $b.Dispose()
    Write-Output "preview: $PreviewPng"
}

if ($StripPng -ne '') {
    # 各尺寸 1:1 并排，便于检查小尺寸可读性
    $show = @(16, 24, 32, 48, 64)
    $pad = 12
    $h = 64 + $pad * 2
    $w = $pad
    foreach ($s in $show) { $w += $s + $pad }
    $canvas = New-Object System.Drawing.Bitmap($w, $h)
    $g2 = [System.Drawing.Graphics]::FromImage($canvas)
    $g2.Clear([System.Drawing.Color]::FromArgb(255, 245, 245, 245))
    $x = $pad
    foreach ($s in $show) {
        $ib = [IconForge]::Render($s)
        $g2.DrawImage($ib, $x, [int](($h - $s) / 2))
        $ib.Dispose()
        $x += $s + $pad
    }
    $g2.Dispose()
    $canvas.Save($StripPng, [System.Drawing.Imaging.ImageFormat]::Png)
    $canvas.Dispose()
    Write-Output "strip  : $StripPng"
}
