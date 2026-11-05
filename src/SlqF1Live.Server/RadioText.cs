// RadioText — rene hjælpere til teamradio-tekst (ingen I/O): prompt med
// kørernavne, parsing af VAD-output, lydkontekst til whisper og oprydning
// af whisper-output. Testet i tests/ServerTests.

using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace SlqF1Live.Server;

public static partial class RadioText
{
    public const string BasePrompt = "F1 team radio. Box, box. Copy that.";
    public const int MaxPromptChars = 600;
    public const int MaxTextChars = 600;

    /// Prompt med sessionens kørernavne — så staver whisper "Leclerc",
    /// "Antonelli" og "Bortoleto" rigtigt. Uden navne: kun grundprompten.
    public static string Prompt(IEnumerable<string> names)
    {
        var sb = new StringBuilder(BasePrompt);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var any = false;
        foreach (var raw in names)
        {
            var n = Spaces().Replace(raw ?? "", " ").Trim();
            if (n.Length is 0 or > 40 || !seen.Add(n)) continue;
            if (sb.Length + n.Length + 3 > MaxPromptChars) break;
            sb.Append(any ? ", " : " ").Append(n);
            any = true;
        }
        if (any) sb.Append('.');
        return sb.ToString();
    }

    /// Taleafsnit fra whisper-vad-speech-segments i sekunder. Værktøjet skriver
    /// "Detected N speech segments:" og "Speech segment i: start = 64.00, end = 550.00"
    /// (centisekunder). null = uforståeligt output (fx fejl i værktøjet).
    public static List<(double Start, double End)>? ParseVad(string stdout)
    {
        var head = VadHead().Match(stdout);
        if (!head.Success) return null;
        var list = new List<(double, double)>();
        foreach (Match m in VadSegment().Matches(stdout))
            list.Add((double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) / 100.0,
                      double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture) / 100.0));
        return list;
    }

    /// Lydkontekst til whisper (-ac): kun den del af 30 s-vinduet som talen
    /// fylder (+1 s). Halverer tiden for typiske klip på 3–10 s uden at koste
    /// præcision. 0 = hele vinduet (ukendt eller lang tale).
    public static int AudioContext(double speechEndS) =>
        speechEndS <= 0 || speechEndS > 25 ? 0 : Math.Min(1500, (int)Math.Ceiling((speechEndS + 1.0) * 50) + 64);

    // Ting whisper "hører" i støj — fjernes, hvis de er det eneste i klippet
    private static readonly HashSet<string> Hallucinations = new(StringComparer.Ordinal)
    {
        "you", "thank you", "thank you very much", "thanks for watching", "thank you for watching",
        "thank you so much for watching", "please subscribe", "bye", "bye bye", "so",
        "subtitles by the amaraorg community", "transcription by castingwords",
    };

    /// Ryd op i whisper-output: lydbeskrivelser ([MUSIC], (engine revving),
    /// *beep*), kendte støj-hallucinationer og løkker hvor samme sætning
    /// gentages mange gange.
    public static string Clean(string raw)
    {
        var s = (raw ?? "").Replace('\r', ' ').Replace('\n', ' ');
        s = Annotations().Replace(s, " ");
        s = s.Replace("♪", " ");
        s = Spaces().Replace(s, " ").Trim().TrimStart('-', '>', ' ').Trim();
        if (s.Length == 0 || Hallucinations.Contains(Normal(s))) return "";

        // Samme sætning mere end 3 gange i træk → 3 (whisper-løkke)
        var sentences = SentenceSplit().Split(s);
        var kept = new List<string>();
        var run = 0;
        foreach (var t in sentences)
        {
            if (kept.Count > 0 && Normal(t) == Normal(kept[^1])) run++;
            else run = 0;
            if (run < 3) kept.Add(t);
        }
        s = string.Join(" ", kept);
        if (s.Length > MaxTextChars) s = s[..MaxTextChars].TrimEnd() + "…";
        return s;
    }

    private static string Normal(string s) => Spaces().Replace(NonLetters().Replace(s.ToLowerInvariant(), ""), " ").Trim();

    [GeneratedRegex(@"\s+")] private static partial Regex Spaces();
    [GeneratedRegex(@"\[[^\]]*\]|\([^)]*\)|\*[^*]*\*")] private static partial Regex Annotations();
    [GeneratedRegex(@"[^\p{L}\p{N}\s]")] private static partial Regex NonLetters();
    [GeneratedRegex(@"(?<=[.!?])\s+")] private static partial Regex SentenceSplit();
    [GeneratedRegex(@"Detected (\d+) speech segments")] private static partial Regex VadHead();
    [GeneratedRegex(@"Speech segment \d+: start = ([0-9.]+), end = ([0-9.]+)")] private static partial Regex VadSegment();
}
