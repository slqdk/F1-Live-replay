// DevelopmentTab — sæsonudvikling som på f1.slq.dk: linjegraf pr. kører
// (samlet pr. hold) over alle afholdte GP'er. Y = sekunder efter pole/
// vinderen (0 øverst), tooltip med sekunder + meter, hold-legend med
// klik-skjul (standard: kun bedste og dårligste hold), zoom, Vis/Skjul
// alle. Resultater diskcaches (cache/) så kun nye løb hentes.

using System.Text.Json.Nodes;
using SlqF1Live.Core;

namespace SlqF1Live;

public sealed class DevelopmentTab : UserControl
{
    private readonly OpenF1Client _api = new();

    private readonly ComboBox _cmbYear = new();
    private readonly Button _btnQuali = new();
    private readonly Button _btnRace = new();
    private readonly CheckBox _chkZoom = new();
    private readonly Button _btnAll = new();
    private readonly Button _btnNone = new();
    private readonly Label _lblStatus = new();
    private readonly Label _lblTitle = new();
    private readonly ChartPanel _chart = new();
    private readonly CheckedListBox _teams = new();
    private readonly ToolTip _tip = new();

    private bool _isQuali = true;

    private readonly Dictionary<string, DevSeason> _cache = new(); // "year:mode"
    private readonly Dictionary<string, HashSet<string>> _hidden = new();
    private readonly HashSet<string> _initialized = new();
    private bool _updatingTeams;
    private CancellationTokenSource? _loadCts;

    public DevelopmentTab()
    {
        Dock = DockStyle.Fill;

        var bar = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            WrapContents = false,
            Padding = new Padding(8, 6, 8, 4),
        };
        _cmbYear.DropDownStyle = ComboBoxStyle.DropDownList;
        _cmbYear.Width = 90;
        _cmbYear.Margin = new Padding(0, 3, 12, 0);
        for (var y = DateTime.Now.Year; y >= 2023; y--) _cmbYear.Items.Add(y);
        _cmbYear.SelectedIndexChanged += (_, _) => { _initialized.Clear(); _hidden.Clear(); _cache.Clear(); _ = RenderAsync(); };

        void ModeBtn(Button b, string text, bool quali)
        {
            b.Text = text;
            b.AutoSize = true;
            b.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            b.Padding = new Padding(12, 3, 12, 3);
            b.Margin = new Padding(0, 0, 6, 0);
            b.Click += (_, _) => { _isQuali = quali; UpdateModeButtons(); _ = RenderAsync(); };
            bar.Controls.Add(b);
        }
        bar.Controls.Add(_cmbYear);
        ModeBtn(_btnQuali, "Qualifying", true);
        ModeBtn(_btnRace, "Race", false);

        _chkZoom.Text = "Zoom (skjul outliers)";
        _chkZoom.Checked = true;
        _chkZoom.AutoSize = true;
        _chkZoom.Margin = new Padding(12, 6, 12, 0);
        _chkZoom.CheckedChanged += (_, _) => RenderChart();
        bar.Controls.Add(_chkZoom);

        void SmallBtn(Button b, string text, Action onClick)
        {
            b.Text = text;
            b.AutoSize = true;
            b.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            b.Padding = new Padding(8, 2, 8, 2);
            b.Margin = new Padding(0, 2, 6, 0);
            b.Click += (_, _) => onClick();
            bar.Controls.Add(b);
        }
        SmallBtn(_btnAll, "Vis alle", () => { CurrentHidden().Clear(); RenderChart(); });
        SmallBtn(_btnNone, "Skjul alle", () =>
        {
            if (_cache.TryGetValue(CacheKey(), out var data))
            {
                var hidden = CurrentHidden();
                foreach (var (dn, _) in data.Series) hidden.Add(TeamOf(data, dn));
                RenderChart();
            }
        });

        _lblStatus.AutoSize = true;
        _lblStatus.ForeColor = SystemColors.GrayText;
        _lblStatus.Margin = new Padding(8, 8, 0, 0);
        bar.Controls.Add(_lblStatus);

        _lblTitle.Font = new Font("Segoe UI", 9f, FontStyle.Bold);
        _lblTitle.AutoSize = true;
        _lblTitle.Padding = new Padding(8, 4, 0, 2);
        _lblTitle.Dock = DockStyle.Top;

        _teams.Dock = DockStyle.Right;
        _teams.Width = 170;
        _teams.CheckOnClick = true;
        _teams.BorderStyle = BorderStyle.FixedSingle;
        _teams.ItemCheck += (_, e) =>
        {
            if (_updatingTeams) return;
            if (_teams.Items[e.Index] is string team)
            {
                var hidden = CurrentHidden();
                if (e.NewValue == CheckState.Checked) hidden.Remove(team);
                else hidden.Add(team);
                BeginInvoke(RenderChart);
            }
        };

        _chart.Dock = DockStyle.Fill;
        _chart.InvertY = true;
        _chart.YFormat = v => "+" + v.ToString("0.0") + " s";
        _chart.EmptyText = "Ingen data — vælg år, eller vent på at sæsonen hentes.";
        _chart.HoverIndex += OnHover;

        Controls.Add(_chart);
        Controls.Add(_teams);
        Controls.Add(_lblTitle);
        Controls.Add(bar);

        UpdateModeButtons();
        if (_cmbYear.Items.Count > 0) _cmbYear.SelectedIndex = 0;
    }

    private string CacheKey() => (_cmbYear.SelectedItem ?? "?") + ":" + (_isQuali ? "quali" : "race");

    private HashSet<string> CurrentHidden()
    {
        if (!_hidden.TryGetValue(CacheKey(), out var set)) _hidden[CacheKey()] = set = new HashSet<string>();
        return set;
    }

    private void UpdateModeButtons()
    {
        void Style(Button b, bool on)
        {
            b.BackColor = on ? Color.FromArgb(0, 120, 212) : SystemColors.Control;
            b.ForeColor = on ? Color.White : SystemColors.ControlText;
        }
        Style(_btnQuali, _isQuali);
        Style(_btnRace, !_isQuali);
        _lblTitle.Text = _isQuali
            ? "Udvikling over sæsonen — tid efter pole pr. Grand Prix"
            : "Udvikling over sæsonen — tid efter vinderen pr. Grand Prix";
    }

    private static string TeamOf(DevSeason data, string dn) => data.TeamOf(dn);
    private static string NameOf(DevSeason data, string dn) => data.NameOf(dn);
    private static string ColourOf(DevSeason data, string dn) => data.ColourOf(dn);

    // ---------- Dataload (fælles med serveren: Core/DevSeasonLoader) ----------

    private Task<DevSeason> LoadSeasonAsync(int year, bool isQuali, CancellationToken ct) =>
        DevSeasonLoader.LoadAsync(_api, year, isQuali, Path.Combine(AppContext.BaseDirectory, "cache"),
            text => _lblStatus.Text = text, ct);

    // ---------- Rendering ----------

    private async Task RenderAsync()
    {
        UpdateModeButtons();
        if (_cmbYear.SelectedItem is not int year) return;
        var key = CacheKey();
        if (!_cache.ContainsKey(key))
        {
            _loadCts?.Cancel();
            _loadCts = new CancellationTokenSource();
            try
            {
                _cache[key] = await LoadSeasonAsync(year, _isQuali, _loadCts.Token);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex) { _lblStatus.Text = "Fejl: " + ex.Message; return; }
        }
        var data = _cache[key];

        if (!_initialized.Contains(key))
        {
            _initialized.Add(key);
            var stats = DevStats.TeamStats(data.Series, dn => TeamOf(data, dn));
            _hidden[key] = DevStats.DefaultHiddenTeams(stats);
        }
        RenderChart();
    }

    private void RenderChart()
    {
        if (!_cache.TryGetValue(CacheKey(), out var data)) { _chart.Series.Clear(); _chart.Invalidate(); return; }
        var hidden = CurrentHidden();

        // Hold-legend sorteret bedste først
        var stats = DevStats.TeamStats(data.Series, dn => TeamOf(data, dn));
        var teams = stats.OrderBy(kv => kv.Value.Avg).Select(kv => kv.Key).ToList();
        _updatingTeams = true;
        var existing = _teams.Items.Cast<string>().ToList();
        if (!teams.SequenceEqual(existing))
        {
            _teams.Items.Clear();
            foreach (var team in teams) _teams.Items.Add(team, !hidden.Contains(team));
        }
        else
        {
            for (var i = 0; i < teams.Count; i++)
                _teams.SetItemChecked(i, !hidden.Contains(teams[i]));
        }
        _updatingTeams = false;

        _chart.Series.Clear();
        _chart.XTickLabels = data.GpLabels;
        var teamSeen = new Dictionary<string, int>();
        var vals = new List<double>();
        foreach (var (dn, points) in data.Series.OrderBy(s => AvgGap(s.Points)))
        {
            var team = TeamOf(data, dn);
            teamSeen.TryGetValue(team, out var seen);
            teamSeen[team] = seen + 1;
            if (hidden.Contains(team)) continue;
            var pts = new List<PointF>();
            for (var i = 0; i < points.Count; i++)
            {
                if (points[i]?.GapS is { } g) { pts.Add(new PointF(i, (float)g)); vals.Add(g); }
            }
            if (pts.Count == 0) continue;
            _chart.Series.Add(new ChartSeries
            {
                Label = NameOf(data, dn),
                Color = ParseHex(ColourOf(data, dn)),
                Dashed = seen % 2 == 1,
                Points = pts,
            });
        }
        if (_chkZoom.Checked && vals.Count > 4)
        {
            vals.Sort();
            var p90 = vals[(int)(vals.Count * 0.9)];
            _chart.YMaxClamp = Math.Max(p90 * 1.25, vals[0] + 0.5);
        }
        else
        {
            _chart.YMaxClamp = null;
        }
        _chart.Invalidate();
    }

    private static double AvgGap(List<DevPoint?> points)
    {
        var v = points.Where(p => p?.GapS is not null).Select(p => p!.GapS!.Value).ToList();
        return v.Count > 0 ? v.Average() : 1e9;
    }

    private void OnHover(int? idx)
    {
        if (idx is null || !_cache.TryGetValue(CacheKey(), out var data) || idx >= data.GpNames.Count)
        {
            _tip.Hide(_chart);
            return;
        }
        var hidden = CurrentHidden();
        var rows = new List<(double Sort, string Line)>();
        foreach (var (dn, points) in data.Series)
        {
            if (hidden.Contains(TeamOf(data, dn))) continue;
            var p = points[idx.Value];
            if (p is null || (p.GapS is null && p.Note is null)) continue;
            rows.Add((p.GapS ?? 1e9, NameOf(data, dn) + ": " + DevStats.TooltipValue(p, _isQuali)));
        }
        if (rows.Count == 0) { _tip.Hide(_chart); return; }
        var text = data.GpNames[idx.Value] + Environment.NewLine +
            string.Join(Environment.NewLine, rows.OrderBy(r => r.Sort).Select(r => r.Line));
        var pos = _chart.PointToClient(Cursor.Position);
        _tip.Show(text, _chart, pos.X + 16, pos.Y + 12, 8000);
    }

    private static Color ParseHex(string hex)
    {
        var h = hex.TrimStart('#');
        if (h.Length == 6 && int.TryParse(h, System.Globalization.NumberStyles.HexNumber, null, out var rgb))
            return Color.FromArgb((rgb >> 16) & 0xFF, (rgb >> 8) & 0xFF, rgb & 0xFF);
        return Color.Gray;
    }
}
