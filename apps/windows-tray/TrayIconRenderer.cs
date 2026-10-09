using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Deluno.Tray;

/// <summary>
/// The tray icon: the Deluno brand icon with a coloured dot in the bottom-right corner that is the health of the
/// platform (green, amber, red; grey and blinking while it starts), and in the opposite corner the pause bars or the
/// update arrow (docs/tray-standard.md). It is drawn at the size the shell asks for a notification-area icon, from the
/// matching frame of the brand icon, with a thin outline round each mark so it reads on a light and a dark taskbar.
/// </summary>
[SupportedOSPlatform("windows")]
public static class TrayIconRenderer
{
    private static readonly Lazy<byte[]?> BrandIconBytes = new(LoadBrandIconBytes);

    private static readonly Color Green = Color.FromArgb(0x2E, 0xB8, 0x5C);
    private static readonly Color Amber = Color.FromArgb(0xFF, 0xB3, 0x00);
    private static readonly Color Red = Color.FromArgb(0xE5, 0x39, 0x35);
    private static readonly Color Blue = Color.FromArgb(0x1E, 0x88, 0xE5);
    private static readonly Color Grey = Color.FromArgb(0x9E, 0x9E, 0x9E);
    private static readonly Color PausedFill = Color.FromArgb(0x42, 0x42, 0x42);

    /// <summary>The size of a notification-area icon on this display.</summary>
    public static Size ShellSmallIconSize => SystemInformation.SmallIconSize;

    /// <summary>The dot's colour; grey while starting.</summary>
    public static Color DotColor(TrayDot dot) => dot switch
    {
        TrayDot.Green => Green,
        TrayDot.Amber => Amber,
        TrayDot.Red => Red,
        _ => Grey
    };

    /// <summary>An icon the caller owns and must dispose: disposing also frees the Windows icon handle.</summary>
    public static OwnedIcon Render(TrayIconSpec spec, bool dotShown = true) => Render(spec, ShellSmallIconSize.Width, dotShown);

    public static OwnedIcon Render(TrayIconSpec spec, int size, bool dotShown = true)
    {
        using var bitmap = RenderBitmap(spec, size, dotShown);
        var handle = bitmap.GetHicon();
        return new OwnedIcon(handle);
    }

    /// <summary>
    /// The icon as a bitmap: the brand frame for this size, the dot (left out on the off frame of a blink) and the
    /// extra mark when there is one.
    /// </summary>
    public static Bitmap RenderBitmap(TrayIconSpec spec, int size, bool dotShown = true)
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

        if (dotShown)
        {
            var dot = DotBounds(size);
            var fill = DrawDisc(g, dot, DotColor(spec.Dot));
            if (spec.Dot == TrayDot.Starting)
            {
                // A ring while starting: the middle left light.
                var hole = Rectangle.Inflate(fill, -Math.Max(1, fill.Width / 4), -Math.Max(1, fill.Height / 4));
                using var middle = new SolidBrush(Color.White);
                g.FillEllipse(middle, hole);
            }
        }

        if (spec.Mark != TrayMark.None)
        {
            DrawMark(g, spec.Mark, MarkBounds(size));
        }

        return bitmap;
    }

    /// <summary>The dot's box: bottom-right, about half the icon.</summary>
    public static Rectangle DotBounds(int size)
    {
        var diameter = Math.Max(8, (int)Math.Round(size * 0.55));
        return new Rectangle(size - diameter, size - diameter, diameter, diameter);
    }

    /// <summary>The extra mark's box: the opposite corner, top-left.</summary>
    public static Rectangle MarkBounds(int size)
    {
        var diameter = Math.Max(8, (int)Math.Round(size * 0.5));
        return new Rectangle(0, 0, diameter, diameter);
    }

    /// <summary>A disc with a dark outside ring and a white ring inside it; returns the box left for the fill.</summary>
    private static Rectangle DrawDisc(Graphics g, Rectangle bounds, Color color)
    {
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
        using (var fill = new SolidBrush(color))
        {
            g.FillEllipse(fill, fillBounds);
        }

        return fillBounds;
    }

    private static void DrawMark(Graphics g, TrayMark mark, Rectangle bounds)
    {
        var fillBounds = DrawDisc(g, bounds, mark == TrayMark.Paused ? PausedFill : Blue);
        using var ink = new SolidBrush(Color.White);
        if (mark == TrayMark.Paused)
        {
            // Two bars.
            var barWidth = Math.Max(1, fillBounds.Width / 5);
            var barHeight = Math.Max(2, fillBounds.Height / 2);
            var top = fillBounds.Y + (fillBounds.Height - barHeight) / 2;
            var gap = Math.Max(1, barWidth);
            var left = fillBounds.X + (fillBounds.Width - (barWidth * 2 + gap)) / 2;
            g.FillRectangle(ink, left, top, barWidth, barHeight);
            g.FillRectangle(ink, left + barWidth + gap, top, barWidth, barHeight);
            return;
        }

        // An arrow pointing down: an update is ready.
        var cx = fillBounds.X + fillBounds.Width / 2f;
        var w = fillBounds.Width;
        var h = fillBounds.Height;
        var shaftHalf = Math.Max(0.6f, w / 10f);
        var headHalf = Math.Max(1.5f, w / 4f);
        var topY = fillBounds.Y + h * 0.2f;
        var neckY = fillBounds.Y + h * 0.5f;
        var tipY = fillBounds.Y + h * 0.8f;
        g.FillPolygon(ink,
        [
            new PointF(cx - shaftHalf, topY), new PointF(cx + shaftHalf, topY), new PointF(cx + shaftHalf, neckY),
            new PointF(cx + headHalf, neckY), new PointF(cx, tipY), new PointF(cx - headHalf, neckY),
            new PointF(cx - shaftHalf, neckY)
        ]);
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
