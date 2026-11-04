// StateStore — holder den aktuelle sessionstilstand pr. topic.
// Samme merge-regler som webklienten (se F1LIVE_FRAMEWORK.md):
// objekter flettes rekursivt pr. nøgle (inkl. F1's numeriske nøgler),
// skalarer/arrays overskriver. F1 sender lister som ARRAY i snapshottet
// men som OBJEKT med index-nøgler i patches ("Stints": {"2": {...}}) —
// en sådan patch opdaterer arrayet pr. index i stedet for at erstatte det.
// "_deleted": [nøgler] i en patch fjerner nøglerne/indexene.
// ".z"-topics er base64 + raw-deflate JSON.

using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SlqF1Live.Core;

public sealed class StateStore
{
    private readonly object _lock = new();
    private JsonObject _state = new();
    public string? UpdatedAt { get; private set; }

    public static JsonNode? DeepMerge(JsonNode? target, JsonNode? patch)
    {
        if (patch is not JsonObject patchObj)
            return patch?.DeepClone();

        if (target is JsonArray arr && IsIndexPatch(patchObj))
        {
            MergeIntoArray(arr, patchObj);
            return arr;
        }

        var result = target as JsonObject ?? new JsonObject();
        foreach (var kv in patchObj)
        {
            if (kv.Key == "_deleted")
            {
                if (kv.Value is JsonArray del)
                    foreach (var d in del)
                        if (d is not null) result.Remove(KeyOf(d));
                continue;
            }
            var existing = result[kv.Key];
            var merged = DeepMerge(existing, kv.Value);
            if (!ReferenceEquals(existing, merged) || !result.ContainsKey(kv.Key))
                result[kv.Key] = merged;
        }
        return result;
    }

    /// Patch hvor alle nøgler (bortset fra "_deleted") er heltal ≥ 0.
    public static bool IsIndexPatch(JsonObject patch)
    {
        var any = false;
        foreach (var kv in patch)
        {
            if (kv.Key == "_deleted") continue;
            if (!int.TryParse(kv.Key, out var i) || i < 0) return false;
            any = true;
        }
        return any || patch.ContainsKey("_deleted");
    }

    private static void MergeIntoArray(JsonArray arr, JsonObject patch)
    {
        foreach (var kv in patch.OrderBy(k => k.Key == "_deleted" ? int.MaxValue : int.Parse(k.Key)))
        {
            if (kv.Key == "_deleted") continue;
            var i = int.Parse(kv.Key);
            while (arr.Count < i) arr.Add(null);
            if (i == arr.Count)
            {
                arr.Add(DeepMerge(null, kv.Value));
                continue;
            }
            var existing = arr[i];
            var merged = DeepMerge(existing, kv.Value);
            if (!ReferenceEquals(existing, merged)) arr[i] = merged;
        }
        if (patch["_deleted"] is JsonArray del)
        {
            var idx = del.Select(d => int.TryParse(d is null ? "" : KeyOf(d), out var n) ? n : -1)
                .Where(n => n >= 0 && n < arr.Count).Distinct().OrderByDescending(n => n);
            foreach (var n in idx) arr.RemoveAt(n);
        }
    }

    private static string KeyOf(JsonNode n)
        => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : n.ToJsonString();

    /// Dekod et topic: ".z"-suffix = base64(raw-deflate(JSON)). Suffix fjernes.
    /// Kaster ved ugyldige data — brug TryDecodeTopic i feed-stien.
    public static (string Topic, JsonNode? Data) DecodeTopic(string topic, JsonNode? data)
    {
        if (topic.EndsWith(".z", StringComparison.Ordinal) && data is JsonValue v &&
            v.TryGetValue<string>(out var b64))
        {
            return (topic[..^2], JsonNode.Parse(InflateBase64(b64)));
        }
        return (topic, data);
    }

    public static bool TryDecodeTopic(string topic, JsonNode? data, out string decodedTopic, out JsonNode? decoded)
    {
        try
        {
            (decodedTopic, decoded) = DecodeTopic(topic, data);
            return true;
        }
        catch (Exception ex) when (ex is FormatException or InvalidDataException or JsonException)
        {
            decodedTopic = topic.EndsWith(".z", StringComparison.Ordinal) ? topic[..^2] : topic;
            decoded = null;
            return false;
        }
    }

    public static string InflateBase64(string b64)
    {
        var raw = Convert.FromBase64String(b64);
        using var ds = new DeflateStream(new MemoryStream(raw), CompressionMode.Decompress);
        using var reader = new StreamReader(ds, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    /// Anvend en feed-patch. Returnerer dekodet (topic, data) til broadcast/UI.
    /// Ugyldige .z-data springes over (Data = null, tilstand uændret).
    public (string Topic, JsonNode? Data) Apply(string rawTopic, JsonNode? rawData, string? timestamp)
    {
        if (!TryDecodeTopic(rawTopic, rawData, out var topic, out var data))
            return (topic, null);
        lock (_lock)
        {
            var existing = _state[topic];
            var merged = DeepMerge(existing, data);
            if (!ReferenceEquals(existing, merged) || !_state.ContainsKey(topic))
                _state[topic] = merged;
            UpdatedAt = timestamp ?? DateTime.UtcNow.ToString("o");
        }
        return (topic, data);
    }

    /// Anvend et fuldt snapshot (Subscribe-svaret).
    public void ApplySnapshot(JsonObject snapshot)
    {
        lock (_lock)
        {
            foreach (var kv in snapshot)
            {
                if (!TryDecodeTopic(kv.Key, kv.Value, out var topic, out var data)) continue;
                _state[topic] = data?.DeepClone();
            }
            UpdatedAt = DateTime.UtcNow.ToString("o");
        }
    }

    /// Trådsikker JSON af {updatedAt, state} — til /state og SSE-snapshot.
    /// exclude: topics der udelades (fx "CarData" til lette klienter).
    public string SnapshotJson(ISet<string>? exclude = null, ISet<string>? only = null)
    {
        lock (_lock)
        {
            var state = new JsonObject();
            foreach (var kv in _state)
            {
                if (exclude is not null && exclude.Contains(kv.Key)) continue;
                if (only is not null && !only.Contains(kv.Key)) continue;
                state[kv.Key] = kv.Value?.DeepClone();
            }
            var wrapper = new JsonObject
            {
                ["updatedAt"] = UpdatedAt,
                ["state"] = state,
            };
            return wrapper.ToJsonString();
        }
    }

    /// Trådsikker klon af ét topic (til UI-rendering).
    public JsonNode? GetTopic(string topic)
    {
        lock (_lock) return _state[topic]?.DeepClone();
    }

    public void Reset()
    {
        lock (_lock) { _state = new JsonObject(); UpdatedAt = null; }
    }
}
