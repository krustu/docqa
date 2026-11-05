namespace DocQa.Services;

/// <summary>One searchable piece of a document.</summary>
public sealed record Chunk(string DocName, int Page, string Text);

public static class Chunker
{
    /// <summary>
    /// Splits text into pieces of roughly <paramref name="maxChars"/> characters,
    /// preferring to break on blank lines, then on line ends.
    /// </summary>
    public static IEnumerable<string> Split(string text, int maxChars = 900)
    {
        text = text.Replace("\r\n", "\n").Trim();
        if (text.Length == 0) yield break;

        var current = new System.Text.StringBuilder();
        foreach (var paragraph in text.Split("\n\n", StringSplitOptions.RemoveEmptyEntries))
        {
            var p = paragraph.Trim();
            if (p.Length == 0) continue;

            // A single huge paragraph: cut it by lines/words.
            if (p.Length > maxChars)
            {
                if (current.Length > 0) { yield return current.ToString().Trim(); current.Clear(); }
                foreach (var piece in CutLong(p, maxChars)) yield return piece;
                continue;
            }

            if (current.Length + p.Length + 2 > maxChars && current.Length > 0)
            {
                yield return current.ToString().Trim();
                current.Clear();
            }
            current.Append(p).Append("\n\n");
        }
        if (current.Length > 0) yield return current.ToString().Trim();
    }

    private static IEnumerable<string> CutLong(string text, int maxChars)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var word in text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            if (sb.Length + word.Length + 1 > maxChars && sb.Length > 0)
            {
                yield return sb.ToString();
                sb.Clear();
            }
            sb.Append(word).Append(' ');
        }
        if (sb.Length > 0) yield return sb.ToString().Trim();
    }
}
