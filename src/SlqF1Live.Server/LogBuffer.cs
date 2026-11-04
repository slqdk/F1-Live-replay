// LogBuffer — log til konsol (docker logs), daglig fil i /data/logs og en
// ringbuffer som adminsiden læser. Tider vises i containerens TZ.

using System.Globalization;

namespace SlqF1Live.Server;

public sealed class LogBuffer
{
    public sealed record Line(long Seq, DateTime Utc, string Text);

    private readonly object _lock = new();
    private readonly Line[] _ring = new Line[3000];
    private long _seq;
    private readonly string? _dir;
    private readonly int _retentionDays;
    private string? _currentDay;

    public LogBuffer(string? dir, int retentionDays)
    {
        _dir = dir;
        _retentionDays = retentionDays;
        if (dir is not null)
        {
            try { Directory.CreateDirectory(dir); } catch (IOException) { _dir = null; }
            catch (UnauthorizedAccessException) { _dir = null; }
        }
    }

    public void Info(string text)
    {
        var now = DateTime.UtcNow;
        var local = now.ToLocalTime();
        var stamp = local.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        lock (_lock)
        {
            _seq++;
            _ring[_seq % _ring.Length] = new Line(_seq, now, text);
            Console.WriteLine($"{stamp}  {text}");
            if (_dir is null) return;
            try
            {
                var day = local.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                if (day != _currentDay)
                {
                    _currentDay = day;
                    Cleanup();
                }
                File.AppendAllText(Path.Combine(_dir, $"server-{day}.log"), $"{stamp}  {text}{Environment.NewLine}");
            }
            catch (IOException) { /* disk fuld e.l. — konsollen har det stadig */ }
        }
    }

    private void Cleanup()
    {
        if (_dir is null) return;
        foreach (var f in Directory.GetFiles(_dir, "server-*.log"))
            if (File.GetLastWriteTimeUtc(f) < DateTime.UtcNow.AddDays(-_retentionDays))
                try { File.Delete(f); } catch (IOException) { /* ignorér */ }
    }

    public List<Line> Since(long seq, int max = 500)
    {
        lock (_lock)
        {
            var list = new List<Line>();
            var from = Math.Max(seq + 1, _seq - _ring.Length + 1);
            from = Math.Max(from, _seq - max + 1);
            for (var s = Math.Max(1, from); s <= _seq; s++)
                if (_ring[s % _ring.Length] is { } l && l.Seq == s) list.Add(l);
            return list;
        }
    }
}
