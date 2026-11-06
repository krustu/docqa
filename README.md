# DocQA — ask questions about your documents

A small RAG (retrieval-augmented generation) web app in C# / ASP.NET Core.
Built as a prototype for document-heavy companies (e.g. safety-training providers) where people
spend time searching through paperwork.

Upload PDF, TXT or Markdown files, ask a question, get an answer with the file name and page it came from.

## How it works

1. **Ingest** — `pdftotext` extracts text page by page; text is cut into ~900-character chunks.
2. **Retrieve** — the question is matched against all chunks with BM25 (keyword ranking, written from scratch in `DocumentStore.cs`).
3. **Generate** — the top 4 chunks go to the LLM (DeepSeek / Groq / Gemini / Claude) with the instruction "answer only from these excerpts and cite them".
4. **Show** — the answer and the source excerpts appear in the page.

Without an API key (`GEMINI_API_KEY` or `ANTHROPIC_API_KEY`) the app runs in demo mode and shows the best matching excerpts instead of a generated answer.

## Run locally

Requirements: .NET 8 SDK and `pdftotext` (`sudo apt install poppler-utils`, or `brew install poppler`).

```bash
export ANTHROPIC_API_KEY=sk-ant-...   # optional
cd src/DocQa
dotnet run
```

Open the printed URL (usually http://localhost:5000).

## Configuration

| Variable | Purpose |
|---|---|
| `LLM_API_KEY` | Key for any OpenAI-compatible API (DeepSeek by default, also Groq, OpenRouter, OpenAI). Used first if set |
| `LLM_BASE_URL` | API base URL, default `https://api.deepseek.com` (Groq: `https://api.groq.com/openai/v1`) |
| `LLM_MODEL` | Model, default `deepseek-chat` |
| `GEMINI_API_KEY` | Enables answers from Google Gemini (free tier, key from aistudio.google.com) |
| `GEMINI_MODEL` | Gemini model, default `gemini-flash-latest` |
| `ANTHROPIC_API_KEY` | Enables answers from Claude (used if no Gemini key is set) |
| `ANTHROPIC_MODEL` | Model name, default `claude-sonnet-5-5` |
| `ACCESS_CODE` | If set, the API requires this code (protects your API budget on a public URL) |
| `LOAD_SAMPLES` | Set to `0` to skip loading the files from `samples/` on start |
| `PORT` | Port to listen on (set automatically by Railway) |

## Deploy

**Railway:** New Project → Deploy from GitHub repo (it detects the `Dockerfile`) → add the variables above → Settings → Networking → Generate Domain.

**Own VPS (e.g. mikr.us):** `docker build -t docqa .` then `docker run -d -p 8080:8080 -e ANTHROPIC_API_KEY=... docqa`.

## Limits and next steps

- Documents live in memory and disappear on restart → add a database (SQLite / PostgreSQL).
- Keyword search only → add embeddings (vector search) for questions worded differently from the text.
- Scanned PDFs have no text layer → add OCR.
- No tests yet → add unit tests for `Chunker` and `DocumentStore.Search`.
