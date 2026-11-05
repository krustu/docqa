namespace DocQa.Services;

public sealed record DocInfo(string Name, int Pages, int Chunks);
public sealed record Hit(Chunk Chunk, double Score);

/// <summary>
/// In-memory store with BM25 ranking. Good enough for a handful of documents;
/// swap for a database / vector index when the project outgrows it.
/// </summary>
public sealed class DocumentStore
{
    private const double K1 = 1.5, B = 0.75;

    private readonly List<Chunk> _chunks = new();
    private readonly object _lock = new();

    public void Add(string docName, IEnumerable<(int Page, string Text)> pages)
    {
        lock (_lock)
        {
            _chunks.RemoveAll(c => c.DocName == docName); // re-upload replaces
            foreach (var (page, text) in pages)
                foreach (var piece in Chunker.Split(text))
                    _chunks.Add(new Chunk(docName, page, piece));
        }
    }

    public IReadOnlyList<DocInfo> List()
    {
        lock (_lock)
        {
            return _chunks
                .GroupBy(c => c.DocName)
                .Select(g => new DocInfo(g.Key, g.Select(c => c.Page).Distinct().Count(), g.Count()))
                .OrderBy(d => d.Name)
                .ToList();
        }
    }

    public bool Remove(string docName)
    {
        lock (_lock) return _chunks.RemoveAll(c => c.DocName == docName) > 0;
    }

    public IReadOnlyList<Hit> Search(string query, int top = 4)
    {
        var q = Tokenize(query).Distinct().ToList();
        if (q.Count == 0) return Array.Empty<Hit>();

        lock (_lock)
        {
            if (_chunks.Count == 0) return Array.Empty<Hit>();

            var tokenized = _chunks.Select(c => Tokenize(c.Text).ToList()).ToList();
            var avgLen = tokenized.Average(t => t.Count);
            var n = _chunks.Count;

            var df = new Dictionary<string, int>();
            foreach (var term in q)
                df[term] = tokenized.Count(t => t.Contains(term));

            var hits = new List<Hit>();
            for (var i = 0; i < n; i++)
            {
                double score = 0;
                var len = tokenized[i].Count;
                foreach (var term in q)
                {
                    var tf = tokenized[i].Count(t => t == term);
                    if (tf == 0) continue;
                    var idf = Math.Log(1 + (n - df[term] + 0.5) / (df[term] + 0.5));
                    score += idf * (tf * (K1 + 1)) / (tf + K1 * (1 - B + B * len / avgLen));
                }
                if (score > 0) hits.Add(new Hit(_chunks[i], score));
            }
            return hits.OrderByDescending(h => h.Score).Take(top).ToList();
        }
    }

    /// <summary>
    /// Lowercase word tokens. Long words are cut to a 6-letter prefix, a cheap
    /// "stemmer" that helps with Polish endings (szkolenie / szkolenia / szkoleniu).
    /// </summary>
    private static IEnumerable<string> Tokenize(string text)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var ch in text.ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(ch)) { sb.Append(ch); continue; }
            if (sb.Length > 1) yield return Stem(sb.ToString());
            sb.Clear();
        }
        if (sb.Length > 1) yield return Stem(sb.ToString());
    }

    private static string Stem(string w) => w.Length > 6 ? w[..6] : w;
}
