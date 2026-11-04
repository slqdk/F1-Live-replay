// BarChartPanel — vandret søjlegraf som websidens drawHBar: navn til
// venstre, søjle ∝ værdi, tekst efter søjlen, lodret gitter med enhed.
// Grupperede rækker (practice) tegnes som undersøjler med alpha-nuancer.
// Højden beregnes af kontrol­len selv (PreferredBarHeight) så ejeren kan
// lægge den i et AutoScroll-panel.

using System.Drawing.Drawing2D;
using SlqF1Live.Core;

namespace SlqF1Live;

public sealed class BarChartPanel : Panel
{
    public List<BarRow> Rows { get; } = new();
    public string Unit { get; set; } = "s";
    public string EmptyText { get; set; } = "Ingen data.";

    private const int MarginL = 130, MarginR = 40, MarginT = 8, MarginB = 26;
    private const int RowHSingle = 24, RowHSub = 15;

    public BarChartPanel()
    {
        DoubleBuffered = true;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint
            | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        BackColor = SystemColors.Window;
    }

    public int PreferredBarHeight()
    {
        if (Rows.Count == 0) return 60;
        var nSub = Rows.Max(r => r.Groups.Count);
        var rowH = nSub > 1 ? nSub * RowHSub + 8 : RowHSingle;
        return MarginT + Rows.Count * rowH + MarginB;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var font = new Font("Segoe UI", 8f);
        using var dimBrush = new SolidBrush(SystemColors.GrayText);
        using var textBrush = new SolidBrush(SystemColors.ControlText);
        using var gridPen = new Pen(Color.FromArgb(228, 228, 228));

        if (Rows.Count == 0)
        {
            g.DrawString(EmptyText, font, dimBrush, MarginL, MarginT + 6);
            return;
        }

        var nSub = Rows.Max(r => r.Groups.Count);
        var rowH = nSub > 1 ? nSub * RowHSub + 8 : RowHSingle;
        var plotW = Width - MarginL - MarginR;
        if (plotW < 40) return;

        double vMax = 0;
        foreach (var r in Rows)
            foreach (var grp in r.Groups)
                if (grp.Value is { } v && v > vMax) vMax = v;
        if (vMax <= 0) vMax = 1;

        float X(double v) => (float)(MarginL + v / vMax * plotW);

        // Lodret gitter
        var step = ChartPanel.NiceStep(vMax, 6);
        var totalH = Rows.Count * rowH;
        var sfc = new StringFormat { Alignment = StringAlignment.Center };
        for (var v = 0d; v <= vMax + 1e-9; v += step)
        {
            var xx = X(v);
            g.DrawLine(gridPen, xx, MarginT, xx, MarginT + totalH);
            g.DrawString(v.ToString(step < 1 ? "0.0" : "0") + " " + Unit,
                font, dimBrush, xx, MarginT + totalH + 6, sfc);
        }

        var sfr = new StringFormat { Alignment = StringAlignment.Far, LineAlignment = StringAlignment.Center };
        var sfl = new StringFormat { LineAlignment = StringAlignment.Center };
        for (var i = 0; i < Rows.Count; i++)
        {
            var row = Rows[i];
            var yTop = MarginT + i * rowH;
            g.DrawString(row.Name, font, textBrush, MarginL - 8, yTop + rowH / 2f, sfr);

            var subH = nSub > 1 ? RowHSub - 3 : RowHSingle - 8;
            for (var j = 0; j < row.Groups.Count; j++)
            {
                var grp = row.Groups[j];
                var yy = nSub > 1 ? yTop + 4 + j * RowHSub : yTop + 4;
                float barEnd = MarginL;
                if (grp.Value is { } v)
                {
                    var bw = Math.Max(v > 0 ? 2f : 0f, (float)(v / vMax * plotW));
                    using var brush = new SolidBrush(WithAlpha(ParseHex(row.ColourHex), grp.Alpha));
                    g.FillRectangle(brush, MarginL, yy, bw, subH);
                    barEnd = MarginL + bw;
                }
                g.DrawString(grp.Text, font, dimBrush, barEnd + 6, yy + subH / 2f, sfl);
            }
        }
    }

    private static Color ParseHex(string hex)
    {
        var h = hex.TrimStart('#');
        if (h.Length == 6 && int.TryParse(h, System.Globalization.NumberStyles.HexNumber, null, out var rgb))
            return Color.FromArgb((rgb >> 16) & 0xFF, (rgb >> 8) & 0xFF, rgb & 0xFF);
        return Color.Gray;
    }

    private static Color WithAlpha(Color c, float a)
        => Color.FromArgb((int)(Math.Clamp(a, 0f, 1f) * 255), c);
}
