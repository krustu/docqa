using System.Diagnostics;

namespace DocQa.Services;

public interface ITextExtractor
{
    /// <summary>Returns the text of each page (page numbers start at 1).</summary>
    Task<List<(int Page, string Text)>> ExtractAsync(string fileName, Stream content, CancellationToken ct);
}

/// <summary>
/// PDF via the poppler `pdftotext` tool (installed in the Dockerfile),
/// plain text and markdown read directly.
/// </summary>
public sealed class TextExtractor : ITextExtractor
{
    public async Task<List<(int Page, string Text)>> ExtractAsync(string fileName, Stream content, CancellationToken ct)
    {
        var ext = Path.GetExtension(fileName).ToLowerInvariant();
        return ext switch
        {
            ".pdf" => await FromPdfAsync(content, ct),
            ".txt" or ".md" => await FromTextAsync(content, ct),
            _ => throw new NotSupportedException("Only .pdf, .txt and .md files are supported.")
        };
    }

    private static async Task<List<(int, string)>> FromTextAsync(Stream content, CancellationToken ct)
    {
        using var reader = new StreamReader(content);
        var text = await reader.ReadToEndAsync(ct);
        return new() { (1, text) };
    }

    private static async Task<List<(int, string)>> FromPdfAsync(Stream content, CancellationToken ct)
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.pdf");
        try
        {
            await using (var fs = File.Create(tmp))
                await content.CopyToAsync(fs, ct);

            var psi = new ProcessStartInfo("pdftotext")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = System.Text.Encoding.UTF8,
            };
            psi.ArgumentList.Add("-layout");
            psi.ArgumentList.Add(tmp);
            psi.ArgumentList.Add("-"); // write to stdout

            using var proc = Process.Start(psi) ?? throw new InvalidOperationException("Cannot start pdftotext.");
            var stdout = proc.StandardOutput.ReadToEndAsync(ct);
            var stderr = proc.StandardError.ReadToEndAsync(ct);
            await proc.WaitForExitAsync(ct);
            if (proc.ExitCode != 0)
                throw new InvalidOperationException($"pdftotext failed: {await stderr}");

            // pdftotext separates pages with a form feed character.
            var pages = (await stdout).Split('\f');
            var result = new List<(int, string)>();
            for (var i = 0; i < pages.Length; i++)
                if (!string.IsNullOrWhiteSpace(pages[i]))
                    result.Add((i + 1, pages[i]));
            return result;
        }
        finally
        {
            if (File.Exists(tmp)) File.Delete(tmp);
        }
    }
}
