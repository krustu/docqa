using DocQa.Services;
using Microsoft.AspNetCore.Http.Features;

var builder = WebApplication.CreateBuilder(args);

// Hosting platforms (Railway, Render, ...) tell us the port via PORT.
var port = Environment.GetEnvironmentVariable("PORT");
if (!string.IsNullOrEmpty(port))
    builder.WebHost.UseUrls($"http://0.0.0.0:{port}");

builder.Services.Configure<FormOptions>(o => o.MultipartBodyLengthLimit = 20 * 1024 * 1024);
builder.Services.AddSingleton<DocumentStore>();
builder.Services.AddSingleton<ITextExtractor, TextExtractor>();
builder.Services.AddHttpClient<AnswerService>(c => c.Timeout = TimeSpan.FromSeconds(60));

var app = builder.Build();

// Optional protection: if ACCESS_CODE is set, every /api call must send it.
// Keeps strangers from spending the Claude API budget on a public URL.
var accessCode = app.Configuration["ACCESS_CODE"];
app.Use(async (ctx, next) =>
{
    if (!string.IsNullOrEmpty(accessCode)
        && ctx.Request.Path.StartsWithSegments("/api")
        && ctx.Request.Path != "/api/status"
        && ctx.Request.Headers["X-Access-Code"] != accessCode)
    {
        ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
        await ctx.Response.WriteAsync("Access code required.");
        return;
    }
    await next();
});

app.UseDefaultFiles();
app.UseStaticFiles();

app.MapGet("/api/status", (AnswerService answers) => new
{
    provider = answers.Provider,
    accessCodeRequired = !string.IsNullOrEmpty(accessCode)
});

app.MapGet("/api/documents", (DocumentStore store) => store.List());

app.MapDelete("/api/documents/{name}", (string name, DocumentStore store) =>
    store.Remove(name) ? Results.NoContent() : Results.NotFound());

app.MapPost("/api/documents", async (HttpRequest req, DocumentStore store, ITextExtractor extractor, CancellationToken ct) =>
{
    if (!req.HasFormContentType)
        return Results.BadRequest("Send the file as multipart/form-data.");

    var form = await req.ReadFormAsync(ct);
    var file = form.Files.FirstOrDefault();
    if (file is null || file.Length == 0)
        return Results.BadRequest("No file uploaded.");

    var name = Path.GetFileName(file.FileName);
    try
    {
        await using var stream = file.OpenReadStream();
        var pages = await extractor.ExtractAsync(name, stream, ct);
        if (pages.Count == 0)
            return Results.BadRequest("No text found (scanned PDF? OCR is not supported yet).");

        store.Add(name, pages);
        return Results.Ok(store.List().First(d => d.Name == name));
    }
    catch (NotSupportedException ex)
    {
        return Results.BadRequest(ex.Message);
    }
});

app.MapPost("/api/ask", async (AskRequest body, AnswerService answers, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(body.Question))
        return Results.BadRequest("Question is empty.");
    try
    {
        return Results.Ok(await answers.AskAsync(body.Question.Trim(), ct));
    }
    catch (InvalidOperationException ex)
    {
        return Results.Problem(ex.Message, statusCode: 502);
    }
});

// Load the files from ./samples on startup so the demo works right away.
if (app.Configuration["LOAD_SAMPLES"] != "0")
{
    var samples = Path.Combine(AppContext.BaseDirectory, "samples");
    if (Directory.Exists(samples))
    {
        var store = app.Services.GetRequiredService<DocumentStore>();
        var extractor = app.Services.GetRequiredService<ITextExtractor>();
        foreach (var path in Directory.GetFiles(samples))
        {
            try
            {
                await using var fs = File.OpenRead(path);
                var pages = await extractor.ExtractAsync(Path.GetFileName(path), fs, CancellationToken.None);
                store.Add(Path.GetFileName(path), pages);
            }
            catch (Exception ex)
            {
                app.Logger.LogWarning(ex, "Could not load sample {Path}", path);
            }
        }
    }
}

app.Run();

public sealed record AskRequest(string Question);
