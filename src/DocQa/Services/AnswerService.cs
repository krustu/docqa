using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace DocQa.Services;

public sealed record Source(string Doc, int Page, string Excerpt);
public sealed record Answer(string Text, List<Source> Sources, bool Mock);

/// <summary>
/// Retrieval-augmented answering: find the best chunks (BM25), then ask an LLM
/// to answer using only those chunks. Works with Gemini (free tier) or Claude,
/// whichever key is set. Without any key it returns the best excerpts.
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
    private readonly string? _geminiKey;
    private readonly string _geminiModel;
    private readonly string? _claudeKey;
    private readonly string _claudeModel;

    public AnswerService(DocumentStore store, HttpClient http, IConfiguration config)
    {
        _store = store;
        _http = http;
        _geminiKey = config["GEMINI_API_KEY"];
        _geminiModel = config["GEMINI_MODEL"] ?? "gemini-flash-latest";
        _claudeKey = config["ANTHROPIC_API_KEY"];
        _claudeModel = config["ANTHROPIC_MODEL"] ?? "claude-sonnet-5-5";
    }

    /// <summary>"gemini", "claude" or null (demo mode).</summary>
    public string? Provider =>
        !string.IsNullOrWhiteSpace(_geminiKey) ? "gemini" :
        !string.IsNullOrWhiteSpace(_claudeKey) ? "claude" : null;

    public bool HasApiKey => Provider is not null;

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
            var sb = new StringBuilder("(Demo mode: no API key set, showing the best matching excerpts.)\n\n");
            foreach (var h in hits)
                sb.AppendLine($"[{h.Chunk.DocName} p.{h.Chunk.Page}] {Shorten(h.Chunk.Text, 500)}\n");
            return new Answer(sb.ToString().Trim(), sources, true);
        }

        var context = new StringBuilder();
        for (var i = 0; i < hits.Count; i++)
            context.AppendLine($"Excerpt {i + 1} [{hits[i].Chunk.DocName} p.{hits[i].Chunk.Page}]:\n{hits[i].Chunk.Text}\n");
        var prompt = $"{context}\nQuestion: {question}";

        var text = Provider == "gemini"
            ? await AskGeminiAsync(prompt, ct)
            : await AskClaudeAsync(prompt, ct);
        return new Answer(text, sources, false);
    }

    private async Task<string> AskGeminiAsync(string prompt, CancellationToken ct)
    {
        var body = new
        {
            system_instruction = new { parts = new[] { new { text = SystemPrompt } } },
            contents = new[] { new { role = "user", parts = new[] { new { text = prompt } } } },
            generationConfig = new { maxOutputTokens = 4096 }
        };

        var url = $"https://generativelanguage.googleapis.com/v1beta/models/{_geminiModel}:generateContent";
        using var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(body) };
        req.Headers.Add("x-goog-api-key", _geminiKey);

        using var doc = await SendAsync(req, "Gemini", ct);

        // Thinking models can return a candidate without text (e.g. token limit hit
        // while "thinking", or blocked by safety), so read the response defensively.
        if (!doc.RootElement.TryGetProperty("candidates", out var candidates) || candidates.GetArrayLength() == 0)
            throw new InvalidOperationException("Gemini returned no answer: " + Shorten(doc.RootElement.ToString(), 300));

        var candidate = candidates[0];
        var text = "";
        if (candidate.TryGetProperty("content", out var content) && content.TryGetProperty("parts", out var parts))
            text = string.Concat(parts.EnumerateArray()
                .Where(p => !(p.TryGetProperty("thought", out var th) && th.ValueKind == JsonValueKind.True))
                .Select(p => p.TryGetProperty("text", out var t) ? t.GetString() : ""));

        if (string.IsNullOrWhiteSpace(text))
        {
            var reason = candidate.TryGetProperty("finishReason", out var r) ? r.GetString() : "unknown";
            throw new InvalidOperationException($"Gemini returned an empty answer (finishReason: {reason}).");
        }
        return text;
    }

    private async Task<string> AskClaudeAsync(string prompt, CancellationToken ct)
    {
        var body = new
        {
            model = _claudeModel,
            max_tokens = 800,
            system = SystemPrompt,
            messages = new[] { new { role = "user", content = prompt } }
        };

        using var req = new HttpRequestMessage(HttpMethod.Post, "https://api.anthropic.com/v1/messages")
        {
            Content = JsonContent.Create(body)
        };
        req.Headers.Add("x-api-key", _claudeKey);
        req.Headers.Add("anthropic-version", "2023-06-01");

        using var doc = await SendAsync(req, "Claude", ct);
        return doc.RootElement.GetProperty("content")[0].GetProperty("text").GetString() ?? "";
    }

    private async Task<JsonDocument> SendAsync(HttpRequestMessage req, string name, CancellationToken ct)
    {
        using var resp = await _http.SendAsync(req, ct);
        var json = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"{name} API error {(int)resp.StatusCode}: {Shorten(json, 300)}");
        return JsonDocument.Parse(json);
    }

    private static string Shorten(string s, int max) =>
        s.Length <= max ? s : s[..max].TrimEnd() + "…";
}
