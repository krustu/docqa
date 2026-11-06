using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace DocQa.Services;

public sealed record Source(string Doc, int Page, string Excerpt);
public sealed record Answer(string Text, List<Source> Sources, bool Mock, string? Provider);
public sealed record ProviderInfo(string Id, string Label);

/// <summary>
/// Retrieval-augmented answering: find the best chunks (BM25), then ask an LLM
/// to answer using only those chunks.
///
/// Every provider whose API key is set is available at the same time, and the
/// user picks one in the UI. Without any key the app returns the best excerpts.
/// </summary>
public sealed class AnswerService
{
    private const string SystemPrompt =
        "You answer questions about the user's documents. Use ONLY the numbered excerpts provided. " +
        "Cite the source after each claim like [file.pdf p.3]. " +
        "If the excerpts do not contain the answer, say you could not find it in the documents. " +
        "Reply in the same language as the question. Be concise. " +
        "Write plain text with simple markdown (bold, bullet lists); never use LaTeX.";

    private enum Kind { OpenAiCompatible, Gemini, Claude }

    private sealed record Provider(string Id, string Label, Kind Kind, string Key, string Model, string? BaseUrl = null);

    private readonly DocumentStore _store;
    private readonly HttpClient _http;
    private readonly List<Provider> _providers = new();
    private readonly string? _defaultId;

    public AnswerService(DocumentStore store, HttpClient http, IConfiguration config)
    {
        _store = store;
        _http = http;

        void Add(string id, string label, Kind kind, string? key, string model, string? baseUrl = null)
        {
            if (!string.IsNullOrWhiteSpace(key))
                _providers.Add(new Provider(id, $"{label} ({model})", kind, key, model, baseUrl));
        }

        // Each provider is switched on just by setting its key (model is optional).
        Add("gemini", "Gemini", Kind.Gemini,
            config["GEMINI_API_KEY"], config["GEMINI_MODEL"] ?? "gemini-flash-latest");
        Add("groq", "Groq", Kind.OpenAiCompatible,
            config["GROQ_API_KEY"], config["GROQ_MODEL"] ?? "openai/gpt-oss-120b", "https://api.groq.com/openai/v1");
        Add("deepseek", "DeepSeek", Kind.OpenAiCompatible,
            config["DEEPSEEK_API_KEY"], config["DEEPSEEK_MODEL"] ?? "deepseek-chat", "https://api.deepseek.com/v1");
        Add("claude", "Claude", Kind.Claude,
            config["ANTHROPIC_API_KEY"], config["ANTHROPIC_MODEL"] ?? "claude-sonnet-5-5");
        // Any other OpenAI-compatible API (OpenRouter, OpenAI, Mistral...).
        Add("custom", config["LLM_NAME"] ?? "Custom", Kind.OpenAiCompatible,
            config["LLM_API_KEY"], config["LLM_MODEL"] ?? "deepseek-chat",
            (config["LLM_BASE_URL"] ?? "https://api.deepseek.com/v1").TrimEnd('/'));

        var wanted = config["DEFAULT_PROVIDER"];
        _defaultId = _providers.FirstOrDefault(p => p.Id == wanted)?.Id ?? _providers.FirstOrDefault()?.Id;
    }

    public IReadOnlyList<ProviderInfo> Providers => _providers.Select(p => new ProviderInfo(p.Id, p.Label)).ToList();
    public string? DefaultProvider => _defaultId;
    public bool HasApiKey => _providers.Count > 0;

    public async Task<Answer> AskAsync(string question, string? providerId, CancellationToken ct)
    {
        var hits = _store.Search(question, top: 4);
        var sources = hits
            .Select(h => new Source(h.Chunk.DocName, h.Chunk.Page, Shorten(h.Chunk.Text, 300)))
            .ToList();

        var provider = _providers.FirstOrDefault(p => p.Id == (providerId ?? _defaultId))
                       ?? _providers.FirstOrDefault();

        if (hits.Count == 0)
            return new Answer("No relevant text found in the uploaded documents.", sources, provider is null, provider?.Id);

        if (provider is null)
        {
            var sb = new StringBuilder("(Demo mode: no API key set, showing the best matching excerpts.)\n\n");
            foreach (var h in hits)
                sb.AppendLine($"[{h.Chunk.DocName} p.{h.Chunk.Page}] {Shorten(h.Chunk.Text, 500)}\n");
            return new Answer(sb.ToString().Trim(), sources, true, null);
        }

        var context = new StringBuilder();
        for (var i = 0; i < hits.Count; i++)
            context.AppendLine($"Excerpt {i + 1} [{hits[i].Chunk.DocName} p.{hits[i].Chunk.Page}]:\n{hits[i].Chunk.Text}\n");
        var prompt = $"{context}\nQuestion: {question}";

        var text = provider.Kind switch
        {
            Kind.Gemini => await AskGeminiAsync(provider, prompt, ct),
            Kind.Claude => await AskClaudeAsync(provider, prompt, ct),
            _ => await AskOpenAiCompatibleAsync(provider, prompt, ct)
        };
        return new Answer(text, sources, false, provider.Id);
    }

    private async Task<string> AskOpenAiCompatibleAsync(Provider p, string prompt, CancellationToken ct)
    {
        var body = new
        {
            model = p.Model,
            max_tokens = 1500,
            messages = new[]
            {
                new { role = "system", content = SystemPrompt },
                new { role = "user", content = prompt }
            }
        };

        using var doc = await SendAsync(() =>
        {
            var req = new HttpRequestMessage(HttpMethod.Post, $"{p.BaseUrl}/chat/completions") { Content = JsonContent.Create(body) };
            req.Headers.Add("Authorization", $"Bearer {p.Key}");
            return req;
        }, p.Label, ct);

        var text = doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString();
        if (string.IsNullOrWhiteSpace(text))
            throw new InvalidOperationException($"{p.Label} returned an empty answer.");
        return text;
    }

    private async Task<string> AskGeminiAsync(Provider p, string prompt, CancellationToken ct)
    {
        var body = new
        {
            system_instruction = new { parts = new[] { new { text = SystemPrompt } } },
            contents = new[] { new { role = "user", parts = new[] { new { text = prompt } } } },
            generationConfig = new { maxOutputTokens = 4096 }
        };

        var url = $"https://generativelanguage.googleapis.com/v1beta/models/{p.Model}:generateContent";
        using var doc = await SendAsync(() =>
        {
            var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(body) };
            req.Headers.Add("x-goog-api-key", p.Key);
            return req;
        }, p.Label, ct);

        // Thinking models can return a candidate without text (e.g. token limit hit
        // while "thinking", or blocked by safety), so read the response defensively.
        if (!doc.RootElement.TryGetProperty("candidates", out var candidates) || candidates.GetArrayLength() == 0)
            throw new InvalidOperationException("Gemini returned no answer: " + Shorten(doc.RootElement.ToString(), 300));

        var candidate = candidates[0];
        var text = "";
        if (candidate.TryGetProperty("content", out var content) && content.TryGetProperty("parts", out var parts))
            text = string.Concat(parts.EnumerateArray()
                .Where(x => !(x.TryGetProperty("thought", out var th) && th.ValueKind == JsonValueKind.True))
                .Select(x => x.TryGetProperty("text", out var t) ? t.GetString() : ""));

        if (string.IsNullOrWhiteSpace(text))
        {
            var reason = candidate.TryGetProperty("finishReason", out var r) ? r.GetString() : "unknown";
            throw new InvalidOperationException($"Gemini returned an empty answer (finishReason: {reason}).");
        }
        return text;
    }

    private async Task<string> AskClaudeAsync(Provider p, string prompt, CancellationToken ct)
    {
        var body = new
        {
            model = p.Model,
            max_tokens = 1500,
            system = SystemPrompt,
            messages = new[] { new { role = "user", content = prompt } }
        };

        using var doc = await SendAsync(() =>
        {
            var req = new HttpRequestMessage(HttpMethod.Post, "https://api.anthropic.com/v1/messages")
            {
                Content = JsonContent.Create(body)
            };
            req.Headers.Add("x-api-key", p.Key);
            req.Headers.Add("anthropic-version", "2023-06-01");
            return req;
        }, p.Label, ct);

        return doc.RootElement.GetProperty("content")[0].GetProperty("text").GetString() ?? "";
    }

    /// <summary>
    /// Sends the request, retrying up to 3 times when the provider is overloaded
    /// (429 / 503 / 529). Free tiers hit this often.
    /// </summary>
    private async Task<JsonDocument> SendAsync(Func<HttpRequestMessage> makeRequest, string name, CancellationToken ct)
    {
        const int attempts = 3;
        for (var attempt = 1; ; attempt++)
        {
            using var req = makeRequest();
            using var resp = await _http.SendAsync(req, ct);
            var json = await resp.Content.ReadAsStringAsync(ct);
            if (resp.IsSuccessStatusCode)
                return JsonDocument.Parse(json);

            var code = (int)resp.StatusCode;
            var overloaded = code is 429 or 503 or 529;
            if (overloaded && attempt < attempts)
            {
                await Task.Delay(TimeSpan.FromSeconds(2 * attempt), ct);
                continue;
            }

            var hint = overloaded ? " The model is overloaded right now, try again in a minute or pick another model." : "";
            throw new InvalidOperationException($"{name} API error {code}.{hint} {Shorten(json, 250)}");
        }
    }

    private static string Shorten(string s, int max) =>
        s.Length <= max ? s : s[..max].TrimEnd() + "…";
}
