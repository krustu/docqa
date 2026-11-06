# DocQA — ask questions about your documents

A small RAG (retrieval-augmented generation) web app in C# / ASP.NET Core.
Built as a prototype for document-heavy companies (e.g. safety-training providers) where people
spend time searching through paperwork.

Upload PDF, TXT or Markdown files, ask a question, get an answer with the file name and page it came from.

## How it works

1. **Ingest** — `pdftotext` extracts text page by page; text is cut into ~900-character chunks.
2. **Retrieve** — the question is matched against all chunks with BM25 (keyword ranking, written from scratch in `DocumentStore.cs`).
3. **Generate** — the top 4 chunks go to the LLM the user picked (Gemini / Groq / DeepSeek / Claude) with the instruction "answer only from these excerpts and cite them".
4. **Show** — the answer and the source excerpts appear in the page.


## Run locally

Requirements: .NET 8 SDK and `pdftotext` (`sudo apt install poppler-utils`, or `brew install poppler`).

```bash
export GEMINI_API_KEY=...   # optional, any key from the table below
cd src/DocQa
dotnet run
```

Open the printed URL (usually http://localhost:5000).

## Configuration

Every model whose key is set shows up in the **Model** dropdown, so several can be active at once.

| Variable | Purpose |
|---|---|
| `GEMINI_API_KEY` / `GEMINI_MODEL` | Google Gemini (free tier, key from aistudio.google.com). Default model `gemini-flash-latest` |
| `GROQ_API_KEY` / `GROQ_MODEL` | Groq (free tier, console.groq.com). Default model `openai/gpt-oss-120b` |
| `DEEPSEEK_API_KEY` / `DEEPSEEK_MODEL` | DeepSeek. Default model `deepseek-chat` |
| `ANTHROPIC_API_KEY` / `ANTHROPIC_MODEL` | Claude. Default model `claude-sonnet-5-5` |
| `LLM_API_KEY` / `LLM_BASE_URL` / `LLM_MODEL` / `LLM_NAME` | Any other OpenAI-compatible API (OpenRouter, OpenAI, Mistral...) |
| `DEFAULT_PROVIDER` | Which one is selected first: `gemini`, `groq`, `deepseek`, `claude` or `custom` |
| `ACCESS_CODE` | If set, the API requires this code (protects your API budget on a public URL) |
| `LOAD_SAMPLES` | Set to `0` to skip loading the files from `samples/` on start |
| `PORT` | Port to listen on (set automatically by Railway) |

Without any key the app runs in demo mode and shows the best matching excerpts.
Overloaded providers (HTTP 429/503) are retried automatically up to 3 times.

## Deploy

**Railway:** New Project → Deploy from GitHub repo (it detects the `Dockerfile`) → add the variables above → Settings → Networking → Generate Domain.

**Own VPS (e.g. mikr.us):** `docker build -t docqa .` then `docker run -d -p 8080:8080 -e GEMINI_API_KEY=... docqa`.

## Limits and next steps

- Documents live in memory and disappear on restart → add a database (SQLite / PostgreSQL).
- Keyword search only → add embeddings (vector search) for questions worded differently from the text.
- Scanned PDFs have no text layer → add OCR.
- No tests yet → add unit tests for `Chunker` and `DocumentStore.Search`.
