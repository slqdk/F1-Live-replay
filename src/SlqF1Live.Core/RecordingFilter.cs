// RecordingFilter — serverer .f1rec-optagelser i lettere udgaver til
// browseren, uden at røre den rå fil:
//   Full   → uændret
//   Timing → uden CarData/Position (tårn, gab, dæk, race control) — typisk
//            under 10 % af en race-fil med telemetri
//   Map    → + Position dekodet, reduceret til seneste sample, max 1/s
//   Tele   → + CarData dekodet, reduceret til seneste sample, max 1/s
// Linjerne finder topic i linjens start uden fuld JSON-parse — det kræver
// optagerens faste feltrækkefølge {"t":…,"type":…,"topic":…}. Linjer der
// ikke følger mønstret, sendes uændret videre.

using System.Buffers;
using System.Text;
using System.Text.Json.Nodes;

namespace SlqF1Live.Core;

public static class RecordingFilter
{
    public enum Profile { Full, Timing, Map, Tele }

    public const long DownsampleMs = 1000;

    public static Profile ParseProfile(string? s) => s?.Trim().ToLowerInvariant() switch
    {
        "timing" => Profile.Timing,
        "map" => Profile.Map,
        "tele" => Profile.Tele,
        _ => Profile.Full,
    };

    private static readonly byte[] TPrefix = "{\"t\":"u8.ToArray();
    private static readonly byte[] TypeKey = "\"type\":\""u8.ToArray();
    private static readonly byte[] TopicKey = "\"topic\":\""u8.ToArray();

    /// t fra linjens start: {"t":123,…} → 123. null hvis linjen ikke har t.
    public static long? PeekT(string line)
    {
        var bytes = Encoding.UTF8.GetBytes(line.Length > 40 ? line[..40] : line);
        var t = PeekT(bytes);
        if (t is not null) return t;
        try
        {
            if (JsonNode.Parse(line) is JsonObject o && o["t"] is JsonValue v && v.TryGetValue<long>(out var tl))
                return tl;
        }
        catch (System.Text.Json.JsonException) { /* halv linje */ }
        return null;
    }

    public static long? PeekT(ReadOnlySpan<byte> line)
    {
        if (!line.StartsWith(TPrefix)) return null;
        long v = 0;
        var any = false;
        for (var i = TPrefix.Length; i < line.Length; i++)
        {
            var c = line[i];
            if (c is >= (byte)'0' and <= (byte)'9') { v = v * 10 + (c - '0'); any = true; }
            else if (c is (byte)',' or (byte)'}' or (byte)'.') return any ? v : null;
            else return null;
        }
        return null;
    }

    private static string? PeekString(ReadOnlySpan<byte> line, byte[] key, int searchLimit)
    {
        var window = line[..Math.Min(line.Length, searchLimit)];
        var idx = window.IndexOf(key);
        if (idx < 0) return null;
        var start = idx + key.Length;
        var rest = line[start..];
        var end = rest.IndexOf((byte)'"');
        if (end < 0 || end > 64) return null;
        return Encoding.UTF8.GetString(rest[..end]);
    }

    /// "feed", "snapshot", "header" — eller null.
    public static string? PeekType(ReadOnlySpan<byte> line) => PeekString(line, TypeKey, 48);

    public static string? PeekTopic(ReadOnlySpan<byte> line)
        => PeekType(line) == "feed" ? PeekString(line, TopicKey, 80) : null;

    /// Offset lige efter sidste '\n' (dvs. slutningen af sidste komplette linje).
    public static long CompleteEnd(Stream fs)
    {
        var len = fs.Length;
        if (len == 0) return 0;
        var buf = new byte[65536];
        var pos = len;
        while (pos > 0)
        {
            var n = (int)Math.Min(buf.Length, pos);
            pos -= n;
            fs.Seek(pos, SeekOrigin.Begin);
            fs.ReadExactly(buf, 0, n);
            for (var i = n - 1; i >= 0; i--)
                if (buf[i] == (byte)'\n') return pos + i + 1;
        }
        return 0;
    }

    /// Skriv filtrerede linjer fra [from, end) til output.
    public static async Task WriteFilteredAsync(Stream input, long from, long end, Stream output, Profile p,
        CancellationToken ct = default)
    {
        input.Seek(from, SeekOrigin.Begin);
        var state = new DownsampleState();
        var outBuf = new MemoryStream(1 << 17);
        var lineBuf = new ArrayBufferWriter<byte>(1 << 16);
        var readBuf = ArrayPool<byte>.Shared.Rent(1 << 16);
        try
        {
            var remaining = end - from;
            while (remaining > 0)
            {
                var n = await input.ReadAsync(readBuf.AsMemory(0, (int)Math.Min(readBuf.Length, remaining)), ct);
                if (n <= 0) break;
                remaining -= n;
                Consume(readBuf, n, lineBuf, p, state, outBuf);
                if (outBuf.Length > (1 << 17))
                {
                    await output.WriteAsync(outBuf.GetBuffer().AsMemory(0, (int)outBuf.Length), ct);
                    outBuf.SetLength(0);
                }
            }
            FlushRemainder(lineBuf, p, state, outBuf);
            if (outBuf.Length > 0)
                await output.WriteAsync(outBuf.GetBuffer().AsMemory(0, (int)outBuf.Length), ct);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(readBuf);
        }
    }

    /// Synkron variant (tests, konvertering).
    public static byte[] FilterBytes(byte[] input, Profile p)
    {
        using var ms = new MemoryStream(input);
        using var outMs = new MemoryStream();
        WriteFilteredAsync(ms, 0, input.Length, outMs, p).GetAwaiter().GetResult();
        return outMs.ToArray();
    }

    // Synkrone span-hjælpere (spans må ikke leve i async-metoder i C# 12).
    private static void Consume(byte[] buf, int n, ArrayBufferWriter<byte> lineBuf, Profile p,
        DownsampleState state, MemoryStream outBuf)
    {
        var span = buf.AsSpan(0, n);
        while (span.Length > 0)
        {
            var nl = span.IndexOf((byte)'\n');
            if (nl < 0) { lineBuf.Write(span); break; }
            lineBuf.Write(span[..nl]);
            span = span[(nl + 1)..];
            ProcessLine(lineBuf.WrittenSpan, p, state, outBuf);
            lineBuf.Clear();
        }
    }

    private static void FlushRemainder(ArrayBufferWriter<byte> lineBuf, Profile p, DownsampleState state,
        MemoryStream outBuf)
    {
        if (lineBuf.WrittenCount > 0) ProcessLine(lineBuf.WrittenSpan, p, state, outBuf);
        lineBuf.Clear();
    }

    private sealed class DownsampleState
    {
        // Ikke long.MinValue: t − MinValue løber over og ville smide alt væk
        public long LastPos = -1_000_000_000L;
        public long LastCar = -1_000_000_000L;
    }

    private static void WriteLine(MemoryStream outBuf, ReadOnlySpan<byte> line)
    {
        outBuf.Write(line);
        outBuf.WriteByte((byte)'\n');
    }

    private static void WriteLine(MemoryStream outBuf, string line)
    {
        var bytes = Encoding.UTF8.GetBytes(line);
        outBuf.Write(bytes, 0, bytes.Length);
        outBuf.WriteByte((byte)'\n');
    }

    private static void ProcessLine(ReadOnlySpan<byte> line, Profile p, DownsampleState st, MemoryStream outBuf)
    {
        if (line.Length == 0) return;
        if (p == Profile.Full) { WriteLine(outBuf, line); return; }

        var type = PeekType(line);
        if (type == "snapshot")
        {
            var converted = TransformSnapshot(line, p);
            if (converted is not null) WriteLine(outBuf, converted); else WriteLine(outBuf, line);
            return;
        }
        if (type != "feed") { WriteLine(outBuf, line); return; }

        var topic = PeekString(line, TopicKey, 80);
        var isCar = topic is "CarData.z" or "CarData";
        var isPos = topic is "Position.z" or "Position";
        if (!isCar && !isPos) { WriteLine(outBuf, line); return; }

        if (isCar && p != Profile.Tele) return;
        if (isPos && p == Profile.Timing) return;

        var t = PeekT(line) ?? 0;
        if (isCar)
        {
            if (t - st.LastCar < DownsampleMs) return;
            st.LastCar = t;
        }
        else
        {
            if (t - st.LastPos < DownsampleMs) return;
            st.LastPos = t;
        }
        var conv = DecodeReduceFeedLine(line, isCar ? "CarData" : "Position", isCar ? "Entries" : "Position");
        if (conv is not null) WriteLine(outBuf, conv);
    }

    /// Feed-linje med .z-data → linje med dekodet topic og kun seneste sample.
    public static string? DecodeReduceFeedLine(ReadOnlySpan<byte> line, string newTopic, string arrayKey)
    {
        try
        {
            if (JsonNode.Parse(line) is not JsonObject o) return null;
            var raw = o["topic"]?.GetValue<string>() ?? "";
            if (!StateStore.TryDecodeTopic(raw, o["data"], out _, out var decoded) || decoded is null) return null;
            var reduced = ReduceToLast(decoded, arrayKey);
            var outObj = new JsonObject
            {
                ["t"] = o["t"]?.DeepClone(),
                ["type"] = "feed",
                ["topic"] = newTopic,
                ["data"] = reduced,
                ["ts"] = o["ts"]?.DeepClone(),
            };
            return outObj.ToJsonString();
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidOperationException or FormatException)
        {
            return null;
        }
    }

    /// {"Position":[a,b,c]} → {"Position":[c]} (samme for CarData "Entries").
    public static JsonNode ReduceToLast(JsonNode decoded, string arrayKey)
    {
        if (decoded is JsonObject obj && obj[arrayKey] is JsonArray arr && arr.Count > 1)
        {
            var last = arr[^1]?.DeepClone();
            var copy = new JsonObject();
            foreach (var kv in obj)
                copy[kv.Key] = kv.Key == arrayKey ? new JsonArray(last) : kv.Value?.DeepClone();
            return copy;
        }
        return decoded.DeepClone();
    }

    private static string? TransformSnapshot(ReadOnlySpan<byte> line, Profile p)
    {
        try
        {
            if (JsonNode.Parse(line) is not JsonObject o || o["data"] is not JsonObject data) return null;
            var outData = new JsonObject();
            foreach (var kv in data)
            {
                var key = kv.Key;
                var isCar = key is "CarData.z" or "CarData";
                var isPos = key is "Position.z" or "Position";
                if (!isCar && !isPos) { outData[key] = kv.Value?.DeepClone(); continue; }
                if (isCar && p != Profile.Tele) continue;
                if (isPos && p == Profile.Timing) continue;
                if (StateStore.TryDecodeTopic(key, kv.Value, out var dt, out var decoded) && decoded is not null)
                    outData[dt] = ReduceToLast(decoded, isCar ? "Entries" : "Position");
            }
            var outObj = new JsonObject
            {
                ["t"] = o["t"]?.DeepClone(),
                ["type"] = "snapshot",
                ["data"] = outData,
            };
            return outObj.ToJsonString();
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidOperationException)
        {
            return null;
        }
    }
}
