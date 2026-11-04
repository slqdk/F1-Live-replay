// MainForm — UI i kode. Faner: Timing / Omgangstider / Intervaller.
// Live-feedet optages automatisk til recordings/*.f1rec (rå beskeder).
// Afspilningsbjælke: Åbn optagelse, Play/Pause, slider, ±10/±60 s,
// hastighed — søgning genopbygger tilstanden deterministisk.
// Live og afspilning er gensidigt udelukkende; SSE-serveren broadcaster
// i begge tilstande (fundament for replay på f1.slq.dk senere).

using System.Text.Json;
using System.Text.Json.Nodes;
using SlqF1Live.Core;

namespace SlqF1Live;

public sealed class MainForm : Form
{
    private enum Mode { Live, Replay }

    private readonly StateStore _store = new();
    private readonly F1FeedClient _feed = new();
    private readonly LapHistory _lapHistory = new();
    private readonly GapHistory _gapHistory = new();
    private readonly SessionPlayer _player = new();
    private SessionRecorder? _recorder;
    private SseServer? _server;
    private Mode _mode = Mode.Live;

    private int _port = 8137;
    private string _allowOrigin = "https://f1.slq.dk";
    private bool _autoStartFeed = true;
    private string? _authToken;

    private string _feedStatus = "stoppet";
    private DateTime _feedStart = DateTime.UtcNow;
    private int _tickCount;

    private readonly Label _lblSession = new();
    private readonly Button _btnToggle = new();
    private readonly TabControl _tabs = new();
    private readonly DataGridView _grid = new();
    private readonly ChartPanel _lapChart = new();
    private readonly ChartPanel _gapChart = new();
    private readonly CheckedListBox _lapDrivers = new();
    private readonly CheckedListBox _gapDrivers = new();
    private readonly CheckBox _chkZoom = new();
    private readonly StatusStrip _statusStrip = new();
    private readonly ToolStripStatusLabel _stFeed = new();
    private readonly ToolStripStatusLabel _stServer = new();
    private readonly ToolStripStatusLabel _stClients = new();
    private readonly ToolStripStatusLabel _stRec = new();
    private readonly ToolStripStatusLabel _stUpdated = new();
    private readonly System.Windows.Forms.Timer _uiTimer = new();
    private readonly TextBox _log = new();
    private readonly object _logLock = new();
    private string _logPath = "";

    // Afspilningsbjælke
    private readonly Button _btnOpenRec = new();
    private readonly Button _btnPlayPause = new();
    private readonly Button _btnBack60 = new();
    private readonly Button _btnBack10 = new();
    private readonly Button _btnFwd10 = new();
    private readonly Button _btnFwd60 = new();
    private readonly TrackBar _slider = new();
    private readonly Label _lblTime = new();
    private readonly ComboBox _cmbSpeed = new();
    private readonly CheckBox _chkRecord = new();
    private bool _updatingReplayUi;

    private readonly Dictionary<string, bool> _visible = new();
    private bool _updatingChecklist;

    public MainForm()
    {
        LoadSettings();
        _feed.AuthToken = F1Auth.ExtractToken(_authToken);

        Text = "SLQ F1 Live";
        MinimumSize = new Size(1280, 800);
        WindowState = FormWindowState.Maximized;
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Segoe UI", 9f);
        AutoScaleMode = AutoScaleMode.Dpi;

        // Top: session + feed-knap
        var top = new Panel { Dock = DockStyle.Top, Height = 40, Padding = new Padding(8, 8, 8, 4) };
        _lblSession.Dock = DockStyle.Fill;
        _lblSession.TextAlign = ContentAlignment.MiddleLeft;
        _lblSession.Font = new Font("Segoe UI", 10.5f, FontStyle.Bold);
        _lblSession.Text = "Ingen sessionsinfo endnu.";
        _btnToggle.Dock = DockStyle.Right;
        _btnToggle.Width = 130;
        _btnToggle.Click += (_, _) => ToggleFeed();
        top.Controls.Add(_lblSession);
        top.Controls.Add(_btnToggle);

        // Afspilningsbjælke
        var replayBar = BuildReplayBar();

        // Grid
        _grid.Dock = DockStyle.Fill;
        _grid.ReadOnly = true;
        _grid.AllowUserToAddRows = false;
        _grid.AllowUserToDeleteRows = false;
        _grid.AllowUserToResizeRows = false;
        _grid.RowHeadersVisible = false;
        _grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        _grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        _grid.BackgroundColor = SystemColors.Window;
        _grid.BorderStyle = BorderStyle.None;
        typeof(DataGridView).GetProperty("DoubleBuffered",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            ?.SetValue(_grid, true);
        AddCol("Pos", 55, weight: 0);
        AddCol("Kører", 80, weight: 0);
        AddCol("Hold", 160);
        AddCol("Seneste omgang", 120);
        AddCol("Bedste omgang", 120);
        AddCol("Interval", 100);
        AddCol("Gab til P1", 100);
        AddCol("Omg.", 60, weight: 0);
        AddCol("Status", 90, weight: 0);

        // Faner
        _tabs.Dock = DockStyle.Fill;
        var tabTiming = new TabPage("Timing");
        tabTiming.Controls.Add(_grid);
        var tabLaps = new TabPage("Omgangstider");
        tabLaps.Controls.Add(BuildChartTab(_lapChart, _lapDrivers, withZoom: true));
        var tabGaps = new TabPage("Intervaller");
        tabGaps.Controls.Add(BuildChartTab(_gapChart, _gapDrivers, withZoom: false));
        _tabs.TabPages.Add(tabTiming);
        _tabs.TabPages.Add(tabLaps);
        _tabs.TabPages.Add(tabGaps);
        var tabHist = new TabPage("Historik");
        tabHist.Controls.Add(new HistoryTab());
        _tabs.TabPages.Add(tabHist);
        var tabDev = new TabPage("Development");
        tabDev.Controls.Add(new DevelopmentTab());
        _tabs.TabPages.Add(tabDev);

        _lapChart.YFormat = FormatLapTime;
        _lapChart.XFormat = v => "Omg. " + v.ToString("0");
        _lapChart.EmptyText = "Ingen fuldførte omgange endnu.";
        _gapChart.InvertY = true;
        _gapChart.YFormat = v => "+" + v.ToString("0.0") + " s";
        _gapChart.XFormat = FormatClock;
        _gapChart.EmptyText = "Ingen gab-data endnu (samles mens feedet/afspilningen kører).";

        // Logrude
        _log.Multiline = true;
        _log.ReadOnly = true;
        _log.ScrollBars = ScrollBars.Vertical;
        _log.WordWrap = false;
        _log.Font = new Font("Consolas", 8.5f);
        _log.Dock = DockStyle.Bottom;
        _log.Height = 110;
        var split = new Splitter { Dock = DockStyle.Bottom, Height = 4 };

        // Statusbar
        _stFeed.Text = "Feed: stoppet";
        _stServer.Text = "Server: —";
        _stClients.Text = "Klienter: 0";
        _stRec.Text = "";
        _stUpdated.Spring = true;
        _stUpdated.TextAlign = ContentAlignment.MiddleRight;
        _statusStrip.Items.AddRange(new ToolStripItem[] { _stFeed, _stServer, _stClients, _stRec, _stUpdated });

        Controls.Add(_tabs);
        Controls.Add(split);
        Controls.Add(_log);
        Controls.Add(replayBar);
        Controls.Add(top);
        Controls.Add(_statusStrip);

        _logPath = Path.Combine(AppContext.BaseDirectory, "slqf1live.log");

        // Live-feed-events (baggrundstråde)
        _feed.Status += s => { _feedStatus = s; LogLine("feed: " + s); };
        _feed.Snapshot += snap =>
        {
            EnsureRecorder();
            _recorder?.WriteSnapshot(snap);
            _store.ApplySnapshot(snap);
            _server?.Broadcast("snapshot", _store.SnapshotJson());
            LogLine("snapshot modtaget");
        };
        _feed.Feed += item =>
        {
            _recorder?.WriteFeed(item.Topic, item.Data, item.Timestamp);
            ApplyAndBroadcast(item);
        };

        // Afspilningsevents (kaldes fra UI-timerens Tick — UI-tråd)
        _player.Reset += () => { _store.Reset(); _lapHistory.Reset(); _gapHistory.Reset(); };
        _player.Snapshot += snap =>
        {
            _store.ApplySnapshot(snap);
            _server?.Broadcast("snapshot", _store.SnapshotJson());
        };
        _player.Feed += item =>
        {
            ApplyAndBroadcast(item);
            // Billig patch-baseret omgangsregistrering (vigtigt for seek-ydelse)
            if (item.Topic == "TimingData") _lapHistory.ApplyPatch(item.Data);
        };

        _uiTimer.Interval = 500;
        _uiTimer.Tick += (_, _) => RefreshUi();

        Load += (_, _) => OnLoaded();
        FormClosing += (_, _) => { _feed.Dispose(); _recorder?.Dispose(); _server?.Dispose(); };
    }

    private void ApplyAndBroadcast(FeedItem item)
    {
        var (topic, data) = _store.Apply(item.Topic, item.Data, item.Timestamp);
        _server?.BroadcastFeed(topic, data, item.Timestamp);
    }

    // ---------- Afspilningsbjælke ----------

    private Control BuildReplayBar()
    {
        var flow = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            WrapContents = false,
            FlowDirection = FlowDirection.LeftToRight,
            Padding = new Padding(8, 4, 8, 4),
        };

        static void SizeBtn(Button b)
        {
            b.AutoSize = true;
            b.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            b.Padding = new Padding(8, 2, 8, 2);
            b.Margin = new Padding(0, 8, 6, 0);
        }

        _btnOpenRec.Text = "Åbn optagelse…";
        SizeBtn(_btnOpenRec);
        _btnOpenRec.Click += (_, _) => OpenRecording();

        _btnPlayPause.Text = "▶";
        SizeBtn(_btnPlayPause);
        _btnPlayPause.Padding = new Padding(12, 2, 12, 2);
        _btnPlayPause.Enabled = false;
        _btnPlayPause.Click += (_, _) => TogglePlay();

        void Nudge(Button b, string text, int seconds)
        {
            b.Text = text;
            SizeBtn(b);
            b.Enabled = false;
            b.Click += (_, _) => { if (_mode == Mode.Replay) _player.Seek(_player.PositionMs + seconds * 1000L); };
        }
        Nudge(_btnBack60, "−60 s", -60);
        Nudge(_btnBack10, "−10 s", -10);
        Nudge(_btnFwd10, "+10 s", 10);
        Nudge(_btnFwd60, "+60 s", 60);

        _slider.Minimum = 0;
        _slider.Maximum = 100;
        _slider.TickStyle = TickStyle.None;
        _slider.Enabled = false;
        _slider.Width = 380;
        _slider.Margin = new Padding(0, 6, 6, 0);
        _slider.Scroll += (_, _) =>
        {
            if (_updatingReplayUi || _mode != Mode.Replay) return;
            _player.Seek((long)_slider.Value * 1000L);
        };

        _lblTime.Text = "0:00 / 0:00";
        _lblTime.AutoSize = true;
        _lblTime.Margin = new Padding(0, 13, 8, 0);

        _cmbSpeed.DropDownStyle = ComboBoxStyle.DropDownList;
        _cmbSpeed.Width = 70;
        _cmbSpeed.Margin = new Padding(0, 9, 8, 0);
        _cmbSpeed.Items.AddRange(new object[] { "0.5×", "1×", "2×", "5×", "10×" });
        _cmbSpeed.SelectedIndex = 1;
        _cmbSpeed.SelectedIndexChanged += (_, _) =>
            _player.Speed = _cmbSpeed.SelectedIndex switch
            {
                0 => 0.5, 1 => 1.0, 2 => 2.0, 3 => 5.0, _ => 10.0
            };

        _chkRecord.Text = "Optag live";
        _chkRecord.Checked = true;
        _chkRecord.AutoSize = true;
        _chkRecord.Margin = new Padding(0, 12, 0, 0);

        flow.Controls.AddRange(new Control[]
        {
            _btnOpenRec, _btnPlayPause, _btnBack60, _btnBack10, _slider, _btnFwd10, _btnFwd60,
            _lblTime, _cmbSpeed, _chkRecord,
        });
        return flow;
    }

    private void OpenRecording()
    {
        using var dlg = new OpenFileDialog
        {
            Filter = "F1-optagelser (*.f1rec)|*.f1rec|Alle filer (*.*)|*.*",
            InitialDirectory = RecordingsDir(),
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;

        if (_feed.Wanted) StopFeed();
        try
        {
            _player.Load(dlg.FileName);
        }
        catch (Exception ex)
        {
            MessageBox.Show("Kunne ikke læse optagelsen: " + ex.Message, "SLQ F1 Live",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }
        _mode = Mode.Replay;
        _player.Seek(0);
        SetReplayControlsEnabled(true);
        _btnToggle.Text = "Til live";
        LogLine($"afspilning: {Path.GetFileName(dlg.FileName)} ({_player.SessionName}, {FormatClock(_player.DurationMs / 1000.0)})");
    }

    private void TogglePlay()
    {
        if (_mode != Mode.Replay) return;
        if (_player.IsPlaying) _player.Pause(); else _player.Play();
    }

    private void SetReplayControlsEnabled(bool on)
    {
        _btnPlayPause.Enabled = on;
        _btnBack60.Enabled = on;
        _btnBack10.Enabled = on;
        _btnFwd10.Enabled = on;
        _btnFwd60.Enabled = on;
        _slider.Enabled = on;
    }

    // ---------- Optagelse ----------

    private string RecordingsDir()
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "recordings");
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// Opret optageren når vi kender sessionen (første snapshot).
    private void EnsureRecorder()
    {
        if (_recorder is not null || !_chkRecord.Checked || _mode != Mode.Live) return;
        var name = TimingView.SessionHeadline(_store.GetTopic("SessionInfo"), null, null);
        // SessionInfo er måske ikke i store endnu (snapshot anvendes efter) —
        // brug dato som basis; sessionsnavnet står i headeren når det kendes.
        var stamp = DateTime.Now.ToString("yyyy-MM-dd_HH.mm");
        var file = Path.Combine(RecordingsDir(),
            SessionRecorder.SanitizeFileName(stamp + "_" + (name.Length > 0 ? name : "session")) + ".f1rec");
        try
        {
            _recorder = new SessionRecorder(file, name);
            LogLine("optager til " + Path.GetFileName(file));
        }
        catch (IOException ex)
        {
            LogLine("optagelse fejlede: " + ex.Message);
        }
    }

    // ---------- Chart-faner ----------

    private Control BuildChartTab(ChartPanel chart, CheckedListBox drivers, bool withZoom)
    {
        var host = new Panel { Dock = DockStyle.Fill };
        chart.Dock = DockStyle.Fill;

        var right = new Panel { Dock = DockStyle.Right, Width = 130, Padding = new Padding(6) };
        drivers.Dock = DockStyle.Fill;
        drivers.CheckOnClick = true;
        drivers.BorderStyle = BorderStyle.FixedSingle;
        drivers.ItemCheck += (s, e) =>
        {
            if (_updatingChecklist) return;
            var clb = (CheckedListBox)s!;
            if (clb.Items[e.Index] is DriverItem item)
                _visible[item.Dn] = e.NewValue == CheckState.Checked;
        };
        right.Controls.Add(drivers);

        if (withZoom)
        {
            _chkZoom.Text = "Zoom (skjul outliers)";
            _chkZoom.Checked = true;
            _chkZoom.Dock = DockStyle.Bottom;
            _chkZoom.Height = 26;
            right.Controls.Add(_chkZoom);
        }

        host.Controls.Add(chart);
        host.Controls.Add(right);
        return host;
    }

    private sealed record DriverItem(string Dn, string Tla)
    {
        public override string ToString() => Tla + "  #" + Dn;
    }

    private void AddCol(string name, int width, float weight = 1f)
    {
        var col = new DataGridViewTextBoxColumn
        {
            HeaderText = name,
            MinimumWidth = width,
            SortMode = DataGridViewColumnSortMode.NotSortable,
        };
        if (weight <= 0)
        {
            col.AutoSizeMode = DataGridViewAutoSizeColumnMode.None;
            col.Width = width;
        }
        else
        {
            col.FillWeight = weight * 100;
        }
        _grid.Columns.Add(col);
    }

    // ---------- Livscyklus ----------

    private void OnLoaded()
    {
        _server = new SseServer(_port, _allowOrigin, _store,
            feedStatus: () => _mode == Mode.Replay ? "afspilning" : _feedStatus,
            feedConnected: () => _feed.Connected,
            recordingsDir: RecordingsDir());
        _server.Log += msg => LogLine(msg);
        _server.Start();
        _stServer.Text = $"Server: :{_port}" + (_server.LocalhostOnly ? " (kun localhost — se README)" : "");

        _feed.Status += s => _server?.Broadcast("status",
            new JsonObject { ["status"] = s }.ToJsonString());

        LogLine(F1Auth.Describe(_authToken));
        if (_autoStartFeed) StartFeed(); else SetToggleText(false);
        _uiTimer.Start();
    }

    private void ToggleFeed()
    {
        if (_mode == Mode.Replay)
        {
            // Tilbage til live
            _player.Pause();
            _mode = Mode.Live;
            SetReplayControlsEnabled(false);
            _store.Reset();
            _lapHistory.Reset();
            _gapHistory.Reset();
            StartFeed();
            return;
        }
        if (_feed.Wanted) StopFeed(); else StartFeed();
    }

    private void StartFeed()
    {
        _mode = Mode.Live;
        _feedStart = DateTime.UtcNow;
        _lapHistory.Reset();
        _gapHistory.Reset();
        _feed.Start();
        SetToggleText(true);
    }

    private void StopFeed()
    {
        _feed.Stop();
        _recorder?.Dispose();
        _recorder = null;
        _store.Reset();
        SetToggleText(false);
    }

    private void SetToggleText(bool running)
        => _btnToggle.Text = running ? "Stop feed" : "Start feed";

    // ---------- UI-opdatering ----------

    private void RefreshUi()
    {
        _tickCount++;

        if (_mode == Mode.Replay)
        {
            _player.Tick(Environment.TickCount64);
            UpdateReplayUi();
            _stFeed.Text = "Afspilning: " + (_player.IsPlaying ? "kører" : "pause");
        }
        else
        {
            _stFeed.Text = "Feed: " + _feedStatus;
        }
        _stClients.Text = "Klienter: " + (_server?.ClientCount ?? 0);
        _stRec.Text = _recorder is not null ? "● Optager" : "";
        _stUpdated.Text = _store.UpdatedAt is null ? "" : "Opdateret: " + _store.UpdatedAt;

        _lblSession.Text = TimingView.SessionHeadline(
            _store.GetTopic("SessionInfo"), _store.GetTopic("TrackStatus"), _store.GetTopic("LapCount"));

        var timingData = _store.GetTopic("TimingData");
        var driverList = _store.GetTopic("DriverList");

        if (_mode == Mode.Live)
        {
            _lapHistory.UpdateFrom(timingData);
            if (_tickCount % 2 == 0)
                _gapHistory.SampleFrom((DateTime.UtcNow - _feedStart).TotalSeconds, timingData);
        }
        else if (_player.IsPlaying && _tickCount % 2 == 0)
        {
            _gapHistory.SampleFrom(_player.PositionMs / 1000.0, timingData);
        }

        var rows = TimingView.BuildRows(timingData, driverList);
        RenderRows(rows);
        SyncDriverLists(rows);

        if (_tabs.SelectedIndex == 1) RenderLapChart(rows);
        else if (_tabs.SelectedIndex == 2) RenderGapChart(rows);
    }

    private void UpdateReplayUi()
    {
        _updatingReplayUi = true;
        var durS = (int)Math.Max(1, _player.DurationMs / 1000);
        if (_slider.Maximum != durS) _slider.Maximum = durS;
        var posS = (int)Math.Clamp(_player.PositionMs / 1000, 0, durS);
        if (_slider.Value != posS) _slider.Value = posS;
        _lblTime.Text = FormatClock(_player.PositionMs / 1000.0) + " / " + FormatClock(_player.DurationMs / 1000.0);
        _btnPlayPause.Text = _player.IsPlaying ? "⏸" : "▶";
        _updatingReplayUi = false;
    }

    // ---------- Grid ----------

    private void RenderRows(List<TimingRow> rows)
    {
        while (_grid.Rows.Count < rows.Count) _grid.Rows.Add();
        while (_grid.Rows.Count > rows.Count) _grid.Rows.RemoveAt(_grid.Rows.Count - 1);

        for (var i = 0; i < rows.Count; i++)
        {
            var r = rows[i];
            var g = _grid.Rows[i];
            SetCell(g, 0, r.Position.ToString());
            SetCell(g, 1, r.Name);
            SetCell(g, 2, r.Team);
            SetCell(g, 3, r.LastLap);
            SetCell(g, 4, r.BestLap);
            SetCell(g, 5, r.Interval);
            SetCell(g, 6, r.GapToLeader);
            SetCell(g, 7, r.Laps);
            SetCell(g, 8, r.Retired ? "Ude" : r.InPit ? "Pit" : "");

            var colour = ParseTeamColour(r.TeamColourHex);
            if (g.Cells[1].Style.ForeColor != colour) g.Cells[1].Style.ForeColor = colour;
            var dim = r.Retired ? SystemColors.GrayText : SystemColors.ControlText;
            if (g.DefaultCellStyle.ForeColor != dim) g.DefaultCellStyle.ForeColor = dim;
        }
    }

    private static void SetCell(DataGridViewRow row, int index, string value)
    {
        if ((row.Cells[index].Value as string) != value) row.Cells[index].Value = value;
    }

    // ---------- Grafer ----------

    private void SyncDriverLists(List<TimingRow> rows)
    {
        if (_lapDrivers.Items.Count == rows.Count) return;
        _updatingChecklist = true;
        foreach (var clb in new[] { _lapDrivers, _gapDrivers })
        {
            clb.Items.Clear();
            foreach (var r in rows)
            {
                if (!_visible.ContainsKey(r.RacingNumber)) _visible[r.RacingNumber] = true;
                clb.Items.Add(new DriverItem(r.RacingNumber, r.Name), _visible[r.RacingNumber]);
            }
        }
        _updatingChecklist = false;
    }

    private (Color Color, bool Dashed) StyleFor(TimingRow r, Dictionary<string, int> teamSeen)
    {
        var color = ParseTeamColour(r.TeamColourHex);
        var key = r.Team.Length > 0 ? r.Team : r.TeamColourHex;
        teamSeen.TryGetValue(key, out var seen);
        teamSeen[key] = seen + 1;
        return (color, seen % 2 == 1);
    }

    private void RenderLapChart(List<TimingRow> rows)
    {
        _lapChart.Series.Clear();
        var teamSeen = new Dictionary<string, int>();
        double best = double.MaxValue;
        foreach (var r in rows)
        {
            var (color, dashed) = StyleFor(r, teamSeen);
            if (!_visible.TryGetValue(r.RacingNumber, out var vis) || !vis) continue;
            var laps = _lapHistory.Series(r.RacingNumber);
            if (laps.Count == 0) continue;
            foreach (var (_, s) in laps) if (s < best) best = s;
            _lapChart.Series.Add(new ChartSeries
            {
                Label = r.Name,
                Color = color,
                Dashed = dashed,
                Points = laps.Select(l => new PointF(l.Lap, (float)l.Seconds)).ToList(),
            });
        }
        _lapChart.YMaxClamp = _chkZoom.Checked && best < double.MaxValue ? best * 1.10 : null;
        _lapChart.Invalidate();
    }

    private void RenderGapChart(List<TimingRow> rows)
    {
        _gapChart.Series.Clear();
        var teamSeen = new Dictionary<string, int>();
        foreach (var r in rows)
        {
            var (color, dashed) = StyleFor(r, teamSeen);
            if (!_visible.TryGetValue(r.RacingNumber, out var vis) || !vis) continue;
            var samples = _gapHistory.Series(r.RacingNumber);
            var pts = new List<PointF>();
            foreach (var s in samples)
            {
                if (s.Gap is { } gap) pts.Add(new PointF((float)s.TimeS, (float)gap));
            }
            if (pts.Count == 0) continue;
            _gapChart.Series.Add(new ChartSeries
            {
                Label = r.Name,
                Color = color,
                Dashed = dashed,
                Points = pts,
            });
        }
        _gapChart.Invalidate();
    }

    private static string FormatLapTime(double sec)
    {
        var m = (int)(sec / 60);
        var s = sec - m * 60;
        return m > 0 ? $"{m}:{s:00.0}" : s.ToString("0.0");
    }

    private static string FormatClock(double sec)
    {
        var t = TimeSpan.FromSeconds(Math.Max(0, sec));
        return t.TotalHours >= 1
            ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}"
            : $"{(int)t.TotalMinutes}:{t.Seconds:00}";
    }

    private static Color ParseTeamColour(string hex)
    {
        if (hex.Length == 6 &&
            int.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out var rgb))
        {
            return Color.FromArgb((rgb >> 16) & 0xFF, (rgb >> 8) & 0xFF, rgb & 0xFF);
        }
        return SystemColors.ControlText;
    }

    // ---------- Log + settings ----------

    private void LogLine(string msg)
    {
        var line = DateTime.Now.ToString("HH:mm:ss.fff") + "  " + msg;
        lock (_logLock)
        {
            try { File.AppendAllText(_logPath, line + Environment.NewLine); }
            catch (IOException) { /* ignorér */ }
        }
        if (IsHandleCreated)
        {
            try
            {
                BeginInvoke(() =>
                {
                    if (_log.TextLength > 200_000) _log.Clear();
                    _log.AppendText(line + Environment.NewLine);
                });
            }
            catch (InvalidOperationException) { /* lukker ned */ }
        }
    }

    private void LoadSettings()
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "settings.json");
            if (!File.Exists(path)) return;
            var doc = JsonNode.Parse(File.ReadAllText(path));
            _port = doc?["port"]?.GetValue<int>() ?? _port;
            _allowOrigin = doc?["allowOrigin"]?.GetValue<string>() ?? _allowOrigin;
            _autoStartFeed = doc?["autoStartFeed"]?.GetValue<bool>() ?? _autoStartFeed;
            _authToken = doc?["authToken"]?.GetValue<string>();
        }
        catch (JsonException)
        {
            MessageBox.Show("settings.json kunne ikke læses — bruger standardværdier.",
                "SLQ F1 Live", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }
}
