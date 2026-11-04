// HistoryTab — slq.dk's weekend-grafer inde i app'en: År → Grand Prix →
// Practice/Qualifying/Race med hurtigste omgange / slutresultat og tid-
// til-næste (meter i parentes). Data fra OpenF1 (historik, gratis).
// Al beregning ligger i HistoryStats (Core, paritetstestet mod websiden).

using System.Text.Json.Nodes;
using SlqF1Live.Core;

namespace SlqF1Live;

public sealed class HistoryTab : UserControl
{
    private readonly OpenF1Client _api = new();

    private readonly ComboBox _cmbYear = new();
    private readonly ComboBox _cmbMeeting = new();
    private readonly NumericUpDown _numLength = new();
    private readonly Label _lblLenHint = new();
    private readonly Label _lblStatus = new();
    private readonly Button _btnPractice = new();
    private readonly Button _btnQuali = new();
    private readonly Button _btnRace = new();
    private readonly ComboBox _cmbSession = new();
    private readonly Label _lblTitleTop = new();
    private readonly Label _lblTitleBottom = new();
    private readonly BarChartPanel _chartTop = new();
    private readonly BarChartPanel _chartBottom = new();
    private readonly Panel _scroll = new() { AutoScroll = true };

    private string _mode = "practice"; // practice | quali | race
    private JsonArray _meetings = new();
    private JsonArray _sessions = new();
    private readonly Dictionary<string, JsonArray> _lapsCache = new();
    private readonly Dictionary<string, JsonArray> _resultCache = new();
    private readonly Dictionary<string, JsonObject> _drivers = new(); // dn -> driver
    private int? _trackLen;
    private bool _updatingLength;
    private CancellationTokenSource? _loadCts;

    public HistoryTab()
    {
        Dock = DockStyle.Fill;

        var topFlow = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            WrapContents = false,
            Padding = new Padding(8, 6, 8, 2),
        };
        static Control Labeled(string text, Control c)
        {
            var p = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.TopDown,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                WrapContents = false,
                Margin = new Padding(0, 0, 12, 0),
            };
            var l = new Label { Text = text, AutoSize = true, ForeColor = SystemColors.GrayText, Margin = new Padding(0) };
            c.Margin = new Padding(0, 2, 0, 0);
            p.Controls.Add(l);
            p.Controls.Add(c);
            return p;
        }
        _cmbYear.DropDownStyle = ComboBoxStyle.DropDownList;
        _cmbYear.Width = 90;
        _cmbMeeting.DropDownStyle = ComboBoxStyle.DropDownList;
        _cmbMeeting.Width = 260;
        _numLength.Minimum = 0;
        _numLength.Maximum = 10000;
        _numLength.Width = 100;
        _lblLenHint.AutoSize = true;
        _lblLenHint.ForeColor = SystemColors.GrayText;
        _lblLenHint.Margin = new Padding(0, 24, 0, 0);
        topFlow.Controls.Add(Labeled("År", _cmbYear));
        topFlow.Controls.Add(Labeled("Grand Prix", _cmbMeeting));
        topFlow.Controls.Add(Labeled("Banelængde [m]", _numLength));
        topFlow.Controls.Add(_lblLenHint);

        var modeFlow = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            WrapContents = false,
            Padding = new Padding(8, 4, 8, 4),
        };
        void ModeBtn(Button b, string text, string mode)
        {
            b.Text = text;
            b.AutoSize = true;
            b.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            b.Padding = new Padding(12, 3, 12, 3);
            b.Margin = new Padding(0, 0, 6, 0);
            b.Click += (_, _) => { _mode = mode; UpdateModeButtons(); _ = RenderAsync(); };
            modeFlow.Controls.Add(b);
        }
        ModeBtn(_btnPractice, "Practice", "practice");
        ModeBtn(_btnQuali, "Qualifying", "quali");
        ModeBtn(_btnRace, "Race", "race");
        _cmbSession.DropDownStyle = ComboBoxStyle.DropDownList;
        _cmbSession.Width = 180;
        _cmbSession.Visible = false;
        _cmbSession.Margin = new Padding(6, 3, 0, 0);
        _cmbSession.SelectedIndexChanged += (_, _) => _ = RenderAsync();
        modeFlow.Controls.Add(_cmbSession);
        _lblStatus.AutoSize = true;
        _lblStatus.ForeColor = SystemColors.GrayText;
        _lblStatus.Margin = new Padding(12, 8, 0, 0);
        modeFlow.Controls.Add(_lblStatus);

        _lblTitleTop.Font = new Font("Segoe UI", 9f, FontStyle.Bold);
        _lblTitleTop.AutoSize = true;
        _lblTitleTop.Padding = new Padding(8, 8, 0, 2);
        _lblTitleBottom.Font = new Font("Segoe UI", 9f, FontStyle.Bold);
        _lblTitleBottom.AutoSize = true;
        _lblTitleBottom.Padding = new Padding(8, 12, 0, 2);

        _scroll.Dock = DockStyle.Fill;
        var stack = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoSize = true,
        };
        stack.Controls.Add(_lblTitleTop);
        stack.Controls.Add(_chartTop);
        stack.Controls.Add(_lblTitleBottom);
        stack.Controls.Add(_chartBottom);
        _scroll.Controls.Add(stack);
        _scroll.Resize += (_, _) => SizeCharts();

        Controls.Add(_scroll);
        Controls.Add(modeFlow);
        Controls.Add(topFlow);

        _cmbYear.SelectedIndexChanged += (_, _) => _ = LoadMeetingsAsync();
        _cmbMeeting.SelectedIndexChanged += (_, _) => _ = OnMeetingChangedAsync();
        _numLength.ValueChanged += (_, _) =>
        {
            if (_updatingLength) return;
            _trackLen = _numLength.Value > 0 ? (int)_numLength.Value : null;
            _ = RenderAsync();
        };

        for (var y = DateTime.Now.Year; y >= 2023; y--) _cmbYear.Items.Add(y);
        UpdateModeButtons();
        if (_cmbYear.Items.Count > 0) _cmbYear.SelectedIndex = 0;
    }

    private void UpdateModeButtons()
    {
        void Style(Button b, bool on)
        {
            b.BackColor = on ? Color.FromArgb(0, 120, 212) : SystemColors.Control;
            b.ForeColor = on ? Color.White : SystemColors.ControlText;
        }
        Style(_btnPractice, _mode == "practice");
        Style(_btnQuali, _mode == "quali");
        Style(_btnRace, _mode == "race");
    }

    private void SizeCharts()
    {
        var w = Math.Max(400, _scroll.ClientSize.Width - 24);
        _chartTop.Width = w;
        _chartBottom.Width = w;
        _chartTop.Height = _chartTop.PreferredBarHeight();
        _chartBottom.Height = _chartBottom.PreferredBarHeight();
    }

    private void SetStatus(string s) => _lblStatus.Text = s;

    // ---------- Dataload ----------

    private async Task LoadMeetingsAsync()
    {
        var ct = NewLoad();
        try
        {
            SetStatus("Henter Grand Prix-liste…");
            var year = (int)_cmbYear.SelectedItem!;
            _meetings = await _api.GetArrayAsync($"/meetings?year={year}", ct);
            _cmbMeeting.Items.Clear();
            foreach (var m in _meetings.OrderBy(m => m?["date_start"]?.GetValue<string>() ?? ""))
                _cmbMeeting.Items.Add(m?["meeting_name"]?.GetValue<string>()
                    ?? m?["circuit_short_name"]?.GetValue<string>() ?? "?");
            SetStatus(_meetings.Count > 0 ? "" : "Ingen løb fundet for det år.");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { SetStatus("Fejl: " + ex.Message); }
    }

    private JsonNode? SelectedMeeting()
    {
        var name = _cmbMeeting.SelectedItem as string;
        return _meetings.FirstOrDefault(m =>
            (m?["meeting_name"]?.GetValue<string>() ?? m?["circuit_short_name"]?.GetValue<string>()) == name);
    }

    private async Task OnMeetingChangedAsync()
    {
        var meeting = SelectedMeeting();
        if (meeting is null) return;
        var ct = NewLoad();
        try
        {
            _lapsCache.Clear();
            _resultCache.Clear();
            _drivers.Clear();

            var circuit = meeting["circuit_short_name"]?.GetValue<string>() ?? "";
            _trackLen = HistoryStats.TrackLengthFor(circuit);
            _updatingLength = true;
            _numLength.Value = _trackLen ?? 0;
            _updatingLength = false;
            _lblLenHint.Text = _trackLen is not null ? $"(auto: {circuit})" : $"(ukendt bane \"{circuit}\" — indtast manuelt)";

            SetStatus("Henter sessioner…");
            var key = meeting["meeting_key"]?.ToString();
            _sessions = await _api.GetArrayAsync($"/sessions?meeting_key={key}", ct);
            SetStatus("");
            await RenderAsync();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { SetStatus("Fejl: " + ex.Message); }
    }

    private List<JsonNode?> SessionsOfType(string type) =>
        _sessions.Where(s => string.Equals(s?["session_type"]?.GetValue<string>(), type,
            StringComparison.OrdinalIgnoreCase))
        .OrderBy(s => s?["date_start"]?.GetValue<string>() ?? "").ToList();

    private async Task<JsonArray> LapsAsync(string sessionKey, CancellationToken ct)
    {
        if (!_lapsCache.TryGetValue(sessionKey, out var laps))
            _lapsCache[sessionKey] = laps = await _api.GetArrayAsync($"/laps?session_key={sessionKey}", ct);
        return laps;
    }

    private async Task LoadDriversAsync(string sessionKey, CancellationToken ct)
    {
        var drivers = await _api.GetArrayAsync($"/drivers?session_key={sessionKey}", ct);
        foreach (var d in drivers)
        {
            var dn = d?["driver_number"]?.ToString();
            if (dn is not null && d is JsonObject o && !_drivers.ContainsKey(dn)) _drivers[dn] = o;
        }
    }

    private string NameOf(string dn) =>
        _drivers.TryGetValue(dn, out var d)
            ? d["name_acronym"]?.GetValue<string>() ?? d["full_name"]?.GetValue<string>() ?? "#" + dn
            : "#" + dn;

    private string ColourOf(string dn) =>
        _drivers.TryGetValue(dn, out var d) ? d["team_colour"]?.GetValue<string>() ?? "888888" : "888888";

    private CancellationToken NewLoad()
    {
        _loadCts?.Cancel();
        _loadCts = new CancellationTokenSource();
        return _loadCts.Token;
    }

    // ---------- Rendering ----------

    private async Task RenderAsync()
    {
        if (SelectedMeeting() is null || _sessions.Count == 0) return;
        var ct = NewLoad();
        try
        {
            switch (_mode)
            {
                case "practice": await RenderPracticeAsync(ct); break;
                case "quali": await RenderSingleSessionAsync("qualifying", ct); break;
                default: await RenderRaceAsync(ct); break;
            }
            SizeCharts();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { SetStatus("Fejl: " + ex.Message); }
    }

    private void FillSessionCombo(List<JsonNode?> sessions)
    {
        _cmbSession.Visible = sessions.Count > 1;
        var wanted = sessions.Select(s => s?["session_name"]?.GetValue<string>() ?? "?").ToList();
        var existing = _cmbSession.Items.Cast<string>().ToList();
        if (!wanted.SequenceEqual(existing))
        {
            _cmbSession.Items.Clear();
            foreach (var w in wanted) _cmbSession.Items.Add(w);
            _cmbSession.SelectedIndex = _cmbSession.Items.Count - 1;
        }
        else if (_cmbSession.SelectedIndex < 0 && _cmbSession.Items.Count > 0)
        {
            _cmbSession.SelectedIndex = _cmbSession.Items.Count - 1;
        }
    }

    private JsonNode? PickedSession(List<JsonNode?> sessions)
    {
        if (sessions.Count == 0) return null;
        if (sessions.Count == 1 || _cmbSession.SelectedIndex < 0) return sessions[^1];
        var name = _cmbSession.SelectedItem as string;
        return sessions.FirstOrDefault(s => s?["session_name"]?.GetValue<string>() == name) ?? sessions[^1];
    }

    private async Task RenderPracticeAsync(CancellationToken ct)
    {
        _cmbSession.Visible = false;
        var fps = SessionsOfType("practice");
        if (fps.Count == 0) { SetStatus("Ingen practice-sessioner."); ClearCharts(); return; }
        SetStatus("Henter practice-data…");
        var perSession = new List<(string, Dictionary<string, double>)>();
        foreach (var s in fps)
        {
            var key = s!["session_key"]!.ToString();
            var laps = await LapsAsync(key, ct);
            perSession.Add((s["session_name"]?.GetValue<string>() ?? "?", HistoryStats.FastestPerDriver(laps)));
        }
        await LoadDriversAsync(fps[^1]!["session_key"]!.ToString(), ct);
        SetStatus("");

        var (grouped, gapNext) = HistoryStats.BuildPracticeRows(perSession, _trackLen, NameOf, ColourOf);
        _lblTitleTop.Text = "Hurtigste omgang pr. session (" +
            string.Join(" / ", perSession.Select(p => p.Item1)) + " — lys → mørk)";
        _lblTitleBottom.Text = "Tid til næste kører — bedste omgang på tværs af sessionerne";
        ShowRows(grouped, gapNext);
    }

    private async Task RenderSingleSessionAsync(string type, CancellationToken ct)
    {
        var sessions = SessionsOfType(type);
        FillSessionCombo(sessions);
        var session = PickedSession(sessions);
        if (session is null) { SetStatus("Ingen qualifying-session."); ClearCharts(); return; }
        SetStatus("Henter qualifying-data…");
        var key = session["session_key"]!.ToString();
        var laps = await LapsAsync(key, ct);
        await LoadDriversAsync(key, ct);
        SetStatus("");
        var best = HistoryStats.FastestPerDriver(laps);
        if (best.Count == 0) { SetStatus("Ingen omgangsdata endnu."); ClearCharts(); return; }
        var (times, gapNext) = HistoryStats.BuildQualiRows(best, _trackLen, NameOf, ColourOf);
        _lblTitleTop.Text = "Hurtigste omgang — tid relativt til den hurtigste";
        _lblTitleBottom.Text = "Tid til næste kører";
        ShowRows(times, gapNext);
    }

    private async Task RenderRaceAsync(CancellationToken ct)
    {
        var sessions = SessionsOfType("race");
        FillSessionCombo(sessions);
        var session = PickedSession(sessions);
        if (session is null) { SetStatus("Ingen race-session."); ClearCharts(); return; }
        SetStatus("Henter løbsresultat…");
        var key = session["session_key"]!.ToString();
        if (!_resultCache.TryGetValue(key, out var result))
            _resultCache[key] = result = await _api.GetArrayAsync($"/session_result?session_key={key}", ct);
        var laps = await LapsAsync(key, ct);
        await LoadDriversAsync(key, ct);
        SetStatus("");
        if (result.Count == 0) { SetStatus("Intet slutresultat tilgængeligt endnu."); ClearCharts(); return; }
        var fastest = HistoryStats.FastestPerDriver(laps);
        var (times, gapNext) = HistoryStats.BuildRaceRows(result, fastest, _trackLen, NameOf, ColourOf);
        _lblTitleTop.Text = "Slutresultat — tid efter vinderen";
        _lblTitleBottom.Text = "Tid til næste kører ved målstregen";
        ShowRows(times, gapNext);
    }

    private void ShowRows(List<BarRow> top, List<BarRow> bottom)
    {
        _chartTop.Rows.Clear();
        _chartTop.Rows.AddRange(top);
        _chartBottom.Rows.Clear();
        _chartBottom.Rows.AddRange(bottom);
        SizeCharts();
        _chartTop.Invalidate();
        _chartBottom.Invalidate();
    }

    private void ClearCharts() => ShowRows(new List<BarRow>(), new List<BarRow>());
}
