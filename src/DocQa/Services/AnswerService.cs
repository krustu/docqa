using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace DocQa.Services;

public sealed record Source(string Doc, int Page, string Excerpt);
public sealed record Answer(string Text, List<Source> Sources, bool Mock);

/// <summary>
/// Retrieval-augmented answering: find the best chunks (BM25), then ask Claude
/// to answer using only those chunks. Without an API key it falls back to
/// returning the best excerpts so the app is still demoable.
/// </summary>
public sealed class AnswerService
{
    private const string SystemPrompt =
        "You answer questions about the user's documents. Use ONLY the numbered excerpts provided. " +
        "Cite the source after each claim like [file.pdf p.3]. " +
        "If the excerpts do not contain the answer, say you could not find it in the documents. " +
        "Reply in the same language as the question. Be concise.";

    private readonly DocumentStore _store;
    private readonly HttpClient _http;
    private readonly string? _apiKey;
    private readonly string _model;

    public AnswerService(DocumentStore store, HttpClient http, IConfiguration config)
    {
        _store = store;
        _http = http;
        _apiKey = config["ANTHROPIC_API_KEY"];
        _model = config["ANTHROPIC_MODEL"] ?? "claude-sonnet-5-5";
    }

    public bool HasApiKey => !string.IsNullOrWhiteSpace(_apiKey);

    public async Task<Answer> AskAsync(string question, CancellationToken ct)
    {
        var hits = _store.Search(question, top: 4);
        var sources = hits
            .Select(h => new Source(h.Chunk.DocName, h.Chunk.Page, Shorten(h.Chunk.Text, 300)))
            .ToList();

        if (hits.Count == 0)
            return new Answer("No relevant text found in the uploaded documents.", sources, !HasApiKey);

        if (!HasApiKey)
        {
            var sb = new StringBuilder("(Demo mode: no ANTHROPIC_API_KEY set, showing the best matching excerpts.)\n\n");
            foreach (var h in hits)
                sb.AppendLine($"[{h.Chunk.DocName} p.{h.Chunk.Page}] {Shorten(h.Chunk.Text, 500)}\n");
            return new Answer(sb.ToString().Trim(), sources, true);
        }

        var context = new StringBuilder();
        for (var i = 0; i < hits.Count; i++)
            context.AppendLine($"Excerpt {i + 1} [{hits[i].Chunk.DocName} p.{hits[i].Chunk.Page}]:\n{hits[i].Chunk.Text}\n");

        var body = new
        {
            model = _model,
            max_tokens = 800,
            system = SystemPrompt,
            messages = new[]
            {
                new { role = "user", content = $"{context}\nQuestion: {question}" }
            }
        };

        using var req = new HttpRequestMessage(HttpMethod.Post, "https://api.anthropic.com/v1/messages")
        {
            Content = JsonContent.Create(body)
        };
        req.Headers.Add("x-api-key", _apiKey);
        req.Headers.Add("anthropic-version", "2023-06-01");

        using var resp = await _http.SendAsync(req, ct);
        var json = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"Claude API error {(int)resp.StatusCode}: {Shorten(json, 300)}");

        using var doc = JsonDocument.Parse(json);
        var text = doc.RootElement.GetProperty("content")[0].GetProperty("text").GetString() ?? "";
        return new Answer(text, sources, false);
    }

    private static string Shorten(string s, int max) =>
        s.Length <= max ? s : s[..max].TrimEnd() + "…";
}
