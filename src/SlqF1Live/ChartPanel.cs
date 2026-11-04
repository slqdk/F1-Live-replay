// ChartPanel — håndrullet linjegraf (ingen chart-biblioteker), samme
// designsprog som graferne på f1.slq.dk: gitter, teamfarver, stiplet
// linje til kører nr. 2 pr. hold, TLA-etiket for enden af hver linje.
// Ejerens ansvar: sæt Series (datakoordinater) og kald Invalidate().

using System.Drawing.Drawing2D;

namespace SlqF1Live;

public sealed class ChartSeries
{
    public string Label = "";
    public Color Color = Color.Gray;
    public bool Dashed;
    public List<PointF> Points = new(); // (x, y) i datakoordinater
}

public sealed class ChartPanel : Panel
{
    public List<ChartSeries> Series { get; } = new();

    /// 0 øverst (bruges til gab-grafen: tættest på P1 er øverst).
    public bool InvertY { get; set; }

    /// Y-loft (zoom): værdier over klippes af plottet.
    public double? YMaxClamp { get; set; }

    public Func<double, string> YFormat { get; set; } = v => v.ToString("0.0") + " s";
    public Func<double, string> XFormat { get; set; } = v => v.ToString("0");
    public string EmptyText { get; set; } = "Ingen data endnu.";

    /// Kategoriske x-etiketter (fx GP-navne). Når sat: gitter + roteret
    /// etiket pr. heltalsindeks 0..n-1, og HoverIndex-eventet aktiveres.
    public List<string>? XTickLabels { get; set; }
    public event Action<int?>? HoverIndex;
    private int _lastHoverIndex = int.MinValue;
    private double _paintXMin, _paintXMax;
    private int _paintPlotW;

    private const int MarginL = 64, MarginR = 52, MarginT = 10, MarginB = 26;

    public ChartPanel()
    {
        DoubleBuffered = true;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint
            | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        BackColor = SystemColors.Window;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        using var gridPen = new Pen(Color.FromArgb(228, 228, 228));
        using var dimBrush = new SolidBrush(SystemColors.GrayText);
        using var font = new Font("Segoe UI", 8f);

        // Datavindue
        double xMin = double.MaxValue, xMax = double.MinValue;
        double yMin = double.MaxValue, yMax = double.MinValue;
        var any = false;
        foreach (var s in Series)
        {
            foreach (var p in s.Points)
            {
                any = true;
                if (p.X < xMin) xMin = p.X;
                if (p.X > xMax) xMax = p.X;
                if (p.Y < yMin) yMin = p.Y;
                if (p.Y > yMax) yMax = p.Y;
            }
        }
        if (!any)
        {
            g.DrawString(EmptyText, font, dimBrush, MarginL, MarginT + 8);
            return;
        }
        if (YMaxClamp is { } clamp && clamp > yMin) yMax = Math.Min(yMax, clamp);
        if (xMax <= xMin) xMax = xMin + 1;
        if (yMax <= yMin) yMax = yMin + 1;
        var yPad = (yMax - yMin) * 0.05;
        yMin -= yPad; yMax += yPad;

        var marginB = XTickLabels is not null ? 64 : MarginB;
        var plotW = Width - MarginL - MarginR;
        var plotH = Height - MarginT - marginB;
        if (plotW < 20 || plotH < 20) return;
        _paintXMin = xMin; _paintXMax = xMax; _paintPlotW = plotW;

        float X(double v) => (float)(MarginL + (v - xMin) / (xMax - xMin) * plotW);
        float Y(double v)
        {
            var f = (v - yMin) / (yMax - yMin);
            if (!InvertY) f = 1 - f;
            return (float)(MarginT + f * plotH);
        }
        // (plotH er allerede beregnet med marginB)

        // Y-gitter
        var yStep = NiceStep(yMax - yMin, 6);
        var sf = new StringFormat { Alignment = StringAlignment.Far, LineAlignment = StringAlignment.Center };
        for (var v = Math.Ceiling(yMin / yStep) * yStep; v <= yMax + 1e-9; v += yStep)
        {
            var yy = Y(v);
            g.DrawLine(gridPen, MarginL, yy, Width - MarginR, yy);
            g.DrawString(YFormat(v), font, dimBrush, MarginL - 6, yy, sf);
        }

        // X-gitter
        if (XTickLabels is { Count: > 0 } labels)
        {
            for (var i = 0; i < labels.Count; i++)
            {
                var xx = X(i);
                g.DrawLine(gridPen, xx, MarginT, xx, Height - marginB);
                g.TranslateTransform(xx, Height - marginB + 6);
                g.RotateTransform(-35f);
                var sfr2 = new StringFormat { Alignment = StringAlignment.Far, LineAlignment = StringAlignment.Center };
                g.DrawString(labels[i], font, dimBrush, 0, 0, sfr2);
                g.ResetTransform();
            }
        }
        else
        {
            var xStep = NiceStep(xMax - xMin, 8);
            var sfc = new StringFormat { Alignment = StringAlignment.Center };
            for (var v = Math.Ceiling(xMin / xStep) * xStep; v <= xMax + 1e-9; v += xStep)
            {
                var xx = X(v);
                g.DrawLine(gridPen, xx, MarginT, xx, Height - marginB);
                g.DrawString(XFormat(v), font, dimBrush, xx, Height - marginB + 4, sfc);
            }
        }

        // Serier (klippet til plottet)
        var clip = new RectangleF(MarginL, MarginT, plotW, plotH);
        g.SetClip(clip);
        foreach (var s in Series)
        {
            if (s.Points.Count == 0) continue;
            using var pen = new Pen(s.Color, 1.8f);
            if (s.Dashed) pen.DashPattern = new float[] { 5f, 3f };
            var pts = s.Points.Select(p => new PointF(X(p.X), Y(p.Y))).ToArray();
            if (pts.Length > 1) g.DrawLines(pen, pts);
            using var brush = new SolidBrush(s.Color);
            foreach (var p in pts) g.FillEllipse(brush, p.X - 2f, p.Y - 2f, 4f, 4f);
        }
        g.ResetClip();

        // TLA-etiket for enden af hver linje (i højre margen)
        foreach (var s in Series)
        {
            if (s.Points.Count == 0) continue;
            var last = s.Points[^1];
            if (last.Y < yMin || last.Y > yMax) continue;
            using var brush = new SolidBrush(s.Color);
            g.DrawString(s.Label, font, brush, Width - MarginR + 4,
                Y(last.Y) - 6);
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (XTickLabels is not { Count: > 0 } labels || _paintPlotW <= 0) return;
        int? idx = null;
        if (e.X >= MarginL - 12 && e.X <= MarginL + _paintPlotW + 12)
        {
            var span = Math.Max(1e-9, _paintXMax - _paintXMin);
            var v = _paintXMin + (e.X - MarginL) / (double)_paintPlotW * span;
            var i = (int)Math.Round(v);
            if (i >= 0 && i < labels.Count) idx = i;
        }
        if (idx != _lastHoverIndex)
        {
            _lastHoverIndex = idx ?? int.MinValue;
            HoverIndex?.Invoke(idx);
        }
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (_lastHoverIndex != int.MinValue)
        {
            _lastHoverIndex = int.MinValue;
            HoverIndex?.Invoke(null);
        }
    }

    public static double NiceStep(double range, int targetTicks)
    {
        var rough = range / Math.Max(1, targetTicks);
        double[] steps = { 0.05, 0.1, 0.2, 0.25, 0.5, 1, 2, 5, 10, 15, 20, 30, 60, 120, 300, 600 };
        foreach (var s in steps) if (s >= rough) return s;
        return 1200;
    }
}
