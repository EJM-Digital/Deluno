using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Deluno.Tray;

/// <summary>
/// The tray icon: the Deluno brand icon, with one small mark in the bottom-right corner only when something differs
/// (docs/tray-standard.md). It is drawn at the size the shell asks for a notification-area icon, from the matching
/// frame of the brand icon, with a thin outline round the mark so it reads on a light and a dark taskbar.
/// </summary>
[SupportedOSPlatform("windows")]
public static class TrayIconRenderer
{
    private static readonly Lazy<byte[]?> BrandIconBytes = new(LoadBrandIconBytes);

    private static readonly Color Red = Color.FromArgb(0xE5, 0x39, 0x35);
    private static readonly Color Blue = Color.FromArgb(0x1E, 0x88, 0xE5);
    private static readonly Color Grey = Color.FromArgb(0x9E, 0x9E, 0x9E);
    private static readonly Color PausedFill = Color.FromArgb(0x42, 0x42, 0x42);

    /// <summary>The size of a notification-area icon on this display.</summary>
    public static Size ShellSmallIconSize => SystemInformation.SmallIconSize;

    /// <summary>An icon the caller owns and must dispose: disposing also frees the Windows icon handle.</summary>
    public static OwnedIcon Render(TrayBadge badge) => Render(badge, ShellSmallIconSize.Width);

    public static OwnedIcon Render(TrayBadge badge, int size)
    {
        using var bitmap = RenderBitmap(badge, size);
        var handle = bitmap.GetHicon();
        return new OwnedIcon(handle);
    }

    /// <summary>The icon as a bitmap: the brand frame for this size, and the mark when there is one.</summary>
    public static Bitmap RenderBitmap(TrayBadge badge, int size)
    {
        var bitmap = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bitmap);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.Clear(Color.Transparent);

        using (var brand = LoadBrandFrame(size))
        {
            if (brand is not null)
            {
                g.DrawImage(brand, new Rectangle(0, 0, size, size));
            }
            else
            {
                // The brand icon is embedded in every build; this only keeps a missing resource from drawing nothing.
                using var tile = new SolidBrush(Color.FromArgb(0x1F, 0x1F, 0x23));
                g.FillEllipse(tile, 0, 0, size - 1, size - 1);
            }
        }

        if (badge != TrayBadge.None)
        {
            DrawBadge(g, badge, size);
        }

        return bitmap;
    }

    /// <summary>The mark's box: bottom-right, about 60 percent of the icon.</summary>
    public static Rectangle BadgeBounds(int size)
    {
        var diameter = Math.Max(8, (int)Math.Round(size * 0.6));
        return new Rectangle(size - diameter, size - diameter, diameter, diameter);
    }

    private static void DrawBadge(Graphics g, TrayBadge badge, int size)
    {
        var bounds = BadgeBounds(size);

        // Outline: a thin dark ring outside a thin white ring, so the mark reads on light and dark backgrounds.
        using (var dark = new SolidBrush(Color.FromArgb(200, 0x10, 0x10, 0x10)))
        {
            g.FillEllipse(dark, bounds);
        }

        var inner = Rectangle.Inflate(bounds, -1, -1);
        using (var white = new SolidBrush(Color.White))
        {
            g.FillEllipse(white, inner);
        }

        var fillBounds = Rectangle.Inflate(inner, -1, -1);
        switch (badge)
        {
            case TrayBadge.NeedsYou:
                using (var fill = new SolidBrush(Red)) { g.FillEllipse(fill, fillBounds); }
                break;
            case TrayBadge.UpdateReady:
                using (var fill = new SolidBrush(Blue)) { g.FillEllipse(fill, fillBounds); }
                break;
            case TrayBadge.Starting:
                // A grey ring: grey disc with the middle left light.
                using (var fill = new SolidBrush(Grey)) { g.FillEllipse(fill, fillBounds); }
                var hole = Rectangle.Inflate(fillBounds, -Math.Max(1, fillBounds.Width / 4), -Math.Max(1, fillBounds.Height / 4));
                using (var middle = new SolidBrush(Color.White)) { g.FillEllipse(middle, hole); }
                break;
            case TrayBadge.Paused:
                // Two bars on a dark disc.
                using (var fill = new SolidBrush(PausedFill)) { g.FillEllipse(fill, fillBounds); }
                var barWidth = Math.Max(1, fillBounds.Width / 5);
                var barHeight = Math.Max(2, fillBounds.Height / 2);
                var top = fillBounds.Y + (fillBounds.Height - barHeight) / 2;
                var gap = Math.Max(1, barWidth);
                var left = fillBounds.X + (fillBounds.Width - (barWidth * 2 + gap)) / 2;
                using (var bars = new SolidBrush(Color.White))
                {
                    g.FillRectangle(bars, left, top, barWidth, barHeight);
                    g.FillRectangle(bars, left + barWidth + gap, top, barWidth, barHeight);
                }

                break;
        }
    }

    /// <summary>The brand icon's own frame for this size (16, 20, 24, 32, 40, 48, 64, 128 or 256), or the nearest.</summary>
    private static Bitmap? LoadBrandFrame(int size)
    {
        var bytes = BrandIconBytes.Value;
        if (bytes is null)
        {
            return null;
        }

        try
        {
            using var stream = new MemoryStream(bytes);
            using var icon = new Icon(stream, new Size(size, size));
            return icon.ToBitmap();
        }
        catch
        {
            return null;
        }
    }

    private static byte[]? LoadBrandIconBytes()
    {
        const string resourceSuffix = "Assets.deluno.ico";
        var assembly = Assembly.GetExecutingAssembly();
        var name = assembly.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith(resourceSuffix, StringComparison.OrdinalIgnoreCase));
        if (name is null)
        {
            return null;
        }

        using var stream = assembly.GetManifestResourceStream(name);
        if (stream is null)
        {
            return null;
        }

        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return copy.ToArray();
    }
}

/// <summary>An icon built from a Windows icon handle, which it frees when disposed (Icon.FromHandle does not).</summary>
[SupportedOSPlatform("windows")]
public sealed class OwnedIcon : IDisposable
{
    private IntPtr _handle;

    internal OwnedIcon(IntPtr handle)
    {
        _handle = handle;
        Icon = Icon.FromHandle(handle);
    }

    public Icon Icon { get; }

    public void Dispose()
    {
        if (_handle == IntPtr.Zero)
        {
            return;
        }

        Icon.Dispose();
        DestroyIcon(_handle);
        _handle = IntPtr.Zero;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr handle);
}
