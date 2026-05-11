using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.Versioning;

namespace Deluno.Tray;

// All geometry mirrors favicon.svg (100×100 viewBox) scaled to 16×16 (factor 0.16).
// Screen : x=2.5 y=4 w=11 h=8  (70%×50% of icon, centred)
// Tabs   : w=2 h=1.5, straddling left (x=2.5) and right (x=13.5) screen edges
// Play ▶ : vertices (6.5,6.5) (6.5,9.5) (9.5,8)  — ~40% of screen height
[SupportedOSPlatform("windows")]
public static class TrayIconRenderer
{
    public static Icon Render(TrayState state)
    {
        const int size = 16;
        using var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using var g   = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Color.Transparent);

        using var bgBrush = new SolidBrush(StateColor(state));
        g.FillEllipse(bgBrush, 0, 0, size - 1, size - 1);

        using var pen   = new Pen(Color.White, 1.1f);
        using var brush = new SolidBrush(Color.White);

        // Screen outline
        DrawRoundedRect(g, pen, 2.5f, 4f, 11f, 8f, 1.1f);

        // Film strip tabs — left edge (straddle x=2.5)
        g.FillRectangle(brush, 1.5f, 5.8f, 2f, 1.4f);
        g.FillRectangle(brush, 1.5f, 8.8f, 2f, 1.4f);

        // Film strip tabs — right edge (straddle x=13.5)
        g.FillRectangle(brush, 12.5f, 5.8f, 2f, 1.4f);
        g.FillRectangle(brush, 12.5f, 8.8f, 2f, 1.4f);

        // Play triangle
        g.FillPolygon(brush, new PointF[]
        {
            new(6.5f, 6.5f),
            new(6.5f, 9.5f),
            new(9.5f, 8f),
        });

        return Icon.FromHandle(bmp.GetHicon());
    }

    private static void DrawRoundedRect(Graphics g, Pen pen, float x, float y, float w, float h, float r)
    {
        using var path = new GraphicsPath();
        path.AddArc(x, y, r * 2, r * 2, 180, 90);
        path.AddArc(x + w - r * 2, y, r * 2, r * 2, 270, 90);
        path.AddArc(x + w - r * 2, y + h - r * 2, r * 2, r * 2, 0, 90);
        path.AddArc(x, y + h - r * 2, r * 2, r * 2, 90, 90);
        path.CloseFigure();
        g.DrawPath(pen, path);
    }

    private static Color StateColor(TrayState state) => state switch
    {
        TrayState.Running  => Color.FromArgb(0xF5, 0xA6, 0x23),  // brand orange
        TrayState.Degraded => Color.FromArgb(0xF5, 0x9E, 0x0B),  // amber
        TrayState.Error    => Color.FromArgb(0xEF, 0x44, 0x44),  // red
        TrayState.Starting => Color.FromArgb(0xF5, 0xA6, 0x23),  // brand orange
        TrayState.Updating => Color.FromArgb(0x63, 0x66, 0xF1),  // indigo
        _                  => Color.FromArgb(0x6B, 0x72, 0x80),  // gray
    };
}
