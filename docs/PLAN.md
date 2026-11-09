# Silvia: plan for a personal desktop RAG

Silvia is a single-user desktop app for managing several knowledge bases side by side. Each
knowledge base is a **project**: its own folders, its own SQLite file, its own settings and its own
conversations, fully isolated from every other project. Within a project you can search and chat,
and every answer cites its source files. It is built on two .NET 10 libraries:

- **[NeoAstra](https://github.com/NeoAstra/NeoAstra)** (BSD-2). Native window over the platform
  WebView (WebView2 on Windows), typed .NET ↔ JS RPC with generated TypeScript bindings,
  streaming channels, events, desktop services (dialogs, menus, notifications), and React/Vue/vanilla
  templates. Assets are served without a localhost server.
- **[Rag.NET](https://github.com/MarcelRoozekrans/Rag.NET)** (MIT, v1.3.6). Modular RAG pipeline on
  `Microsoft.Extensions.AI`: parsers, chunking, ingestion, retrieval, reranking, `AskStreamingAsync`.
  It already ships SQLite stores for everything **except vectors**.

## 1. What already exists vs. what we build

| Need | Source | Notes |
|---|---|---|
| Window, WebView, RPC, streaming to UI | NeoAstra | `[NeoRpcService]` + `IAsyncEnumerable` channel methods |
| Parse PDF/Office/HTML/EPUB/email/Markdown | `Rag.NET` + `Rag.NET.Parsers.*` | Install only what we use |
| Chunking (recursive, token-aware, header breadcrumbs) | `Rag.NET` | |
| Walk folders, incremental re-ingest | `LocalFilesDataProvider` + `UseContentHashRecordManager(db)` | Skips unchanged files |
| BM25 keyword index (persistent) | `UseSqlitePersistence(db)` → `SqliteBm25Index` | Fused with dense results by `EnsembleBehavior` |
| Parent-chunk store, document sidecar | `Rag.NET.Storage.Sqlite` | Same file |
| Re-embed after a model switch | `UseEmbeddingVersioning()` | |
| Local embeddings | `Rag.NET.Embeddings.Onnx` | No API calls |
| Local reranking | `Rag.NET.Reranking.Onnx` | ms-marco MiniLM cross-encoder |
| Conversation memory | `Rag.NET.Memory` (SQLite) | |
| Prompt-injection hardening | `Rag.NET.Security` | Files may contain hostile text |
| **SQLite vector store** | **We build it**: `SqliteVectorStore : IVectorStore` | The only missing piece; it could go upstream later |
| Projects: isolated runtimes, catalog, lifecycle | **We build it**: `ProjectManager` / `ProjectRuntime` | Same idea as Rag.NET's named pipelines, but created at runtime |
| Folder watching, job queue, settings, UI | **We build it** | |

## 2. Projects

A project is the unit of isolation. It contains:

- source folders and their include/exclude globs;
- every index (vectors, BM25, parent chunks, content hashes, embedding versions);
- conversations and conversation memory;
- settings: embedder, chunking, retrieval, chat model, system prompt, cloud policy;
- its ingest queue and job history.

### Isolation guarantees

1. **One SQLite file per project.** `projects/<id>/project.db` holds every Rag.NET store and every
   Silvia table for that project. No table carries a `project_id` column that a query could forget
   to filter on: the file *is* the boundary.
2. **One DI container per project.** Each `ProjectRuntime` builds its own `ServiceCollection` with
   `AddRagNet(...)` pointed at its own `project.db`. Its `IRagPipeline`, vector store, BM25 index,
   caches and behaviours can't reach another project's. This mirrors Rag.NET's
   [named pipelines](https://github.com/MarcelRoozekrans/Rag.NET/blob/main/docs/guide/architecture.md#named-pipelines),
   which give each name a child container with its own stores. We don't use `AddRagNet(name, …)`
   directly because names are fixed when the root container is built, and projects are created
   and deleted while the app runs.
3. **Only stateless, expensive things are shared.** A `ModelPool` holds one ONNX embedder and one
   reranker per model id, with reference counts. It also holds one DeepSeek `IChatClient`. These are
   registered into each project container as singleton *instances*, the same rule Rag.NET's
   `AddRagNetShared` follows. Two projects on the same embedder load MiniLM/bge once (~90 MB), not twice.
4. **Every RPC call is scoped by `projectId`**, and resolves `ProjectManager.Get(projectId)`
   before touching data. Events and channel items carry `projectId`, so the UI can drop stale
   ones after a project switch.
5. **Each project has its own ingest queue and watchers.** A global `SemaphoreSlim` (default 1)
   limits how many projects embed at once, so CPU stays bounded. Within a project there is still
   only one writer.

The same folder can belong to two projects. It is indexed twice, independently, and that is the
intended trade for isolation.

Cross-project search is **not** part of the default. If it's wanted later, it would be an explicit,
read-only "search across selected projects" built on Rag.NET's `FederatedVectorStore`, opened
from the per-project stores. It would never be a shared index.

### Lifecycle

| Operation | What happens |
|---|---|
| Create | Insert a row in the catalog, create `projects/<id>/`, initialise `project.db` (schema + PRAGMAs), choose settings (defaults copied from global) |
| Open | `ProjectRuntime` is built lazily on first use: DI container, vector matrix loaded, watchers started, pending `ingest_jobs` resumed |
| Close | Idle runtimes are closed on an LRU basis (default: keep 3 open). Watchers stop, the matrix is freed, connections close. Closed projects still get a rescan on next open, which catches changes made while they were closed |
| Rename | Changes the catalog only; the `id` (ULID) and folder name never change |
| Delete | Close the runtime, `SqliteConnection.ClearAllPools()`, delete `projects/<id>/` (moved to the Recycle Bin), remove the catalog row |
| Export / back up | `VACUUM INTO 'export/<name>.silvia'`: a single self-contained file. Source paths are kept, so the documents are not included |
| Import | Copy the file into `projects/<new id>/project.db`, register it in the catalog, re-point or disable missing source folders |
| Duplicate | `VACUUM INTO` a new project id, then let the user change settings, e.g. try another embedder without touching the original |

Per-project settings mean one project can use `multilingual-e5-small` for Arabic and English
documents while another uses `bge-small-en-v1.5`. The vector dimension is stored per project, and
the matrix is sized from it.

## 3. Architecture

```
┌──────────────────────── Silvia.App (NeoAstra host, .NET 10) ────────────────────────┐
│  frontend/ (React + TS, NeoAstra template)                                          │
│    Project switcher · Library · Chat · Search · Settings · Activity                 │
│        ▲  generated #neoastra bindings (calls, events, channels)                    │
│        ▼                                                                             │
│  RPC: projects · library · ingest · chat · search · settings · shell                │
│       (every call except projects.* takes projectId)                                │
└───────────────┬──────────────────────────────────────────────────────────────────────┘
                │ root DI
┌───────────────▼──────────── Silvia.Core ─────────────────────────────────────────────┐
│  ProjectManager ── catalog.db, LRU of open runtimes, create/delete/export/import     │
│  ModelPool ─────── shared ONNX embedders/rerankers (ref-counted), DeepSeek client    │
│  IngestThrottle ── global cap on concurrent embedding                                │
│                                                                                      │
│  ProjectRuntime (one per open project, own ServiceCollection)                        │
│    IRagPipeline (AddRagNet → project.db)   IngestQueue   FolderWatchers   Settings   │
└───────────────┬──────────────────────────────────────────────────────────────────────┘
                │
┌───────────────▼──────────── Silvia.Storage.Sqlite ──────────────────────────────────┐
│  SqliteVectorStore (IVectorStore, IChunkLookup, ICollectionManageable)              │
│  CatalogDb (projects, global settings)   ProjectDb (schema, migrations, PRAGMAs)    │
└───────────────┬──────────────────────────────────────────────────────────────────────┘
                ▼
  %LOCALAPPDATA%\Silvia\
    catalog.db                      project list + global defaults (no document data)
    models\                         ONNX embedders, rerankers, vocab (shared, read-only)
    projects\<ulid>\project.db      everything for one project (WAL)
    projects\<ulid>\logs\           per-project ingest log
```

### Solution layout

```
Silvia.slnx
src/Silvia.App/              NeoAstra exe, RPC services, frontend/
src/Silvia.Core/             ProjectManager, ProjectRuntime, ModelPool, queue, watcher, settings
src/Silvia.Storage.Sqlite/   SqliteVectorStore + app schema/migrations
tests/Silvia.Storage.Sqlite.Tests/
tests/Silvia.Core.Tests/
eval/                        a small golden Q&A set for your own documents
```

## 4. SQLite backend

### `catalog.db`: the project list

```sql
CREATE TABLE schema_version (version INTEGER NOT NULL);
CREATE TABLE projects (
  id TEXT PRIMARY KEY,                 -- ULID; also the folder name
  name TEXT NOT NULL UNIQUE,
  description TEXT,
  created_utc TEXT NOT NULL,
  last_opened_utc TEXT,
  status TEXT NOT NULL DEFAULT 'ready'); -- ready | importing | deleting (crash recovery)
CREATE TABLE global_settings (key TEXT PRIMARY KEY, value TEXT NOT NULL);  -- defaults for new projects
```

The catalog never holds document data, chunks or conversations. When the catalog and the disk
disagree on startup (a folder without a row, or a row in `deleting`), the startup check finishes or
rolls back the operation.

### `project.db`: one file, many owners

Rag.NET's stores each open their own `Microsoft.Data.Sqlite` connection to the path they are
given. Within a project, we give all of them that project's `project.db`, and open it ourselves
when the runtime starts to set:

```sql
PRAGMA journal_mode = WAL;      -- readers don't block the ingest writer
PRAGMA synchronous = NORMAL;
PRAGMA busy_timeout = 5000;     -- every connection; set in the connection string factory
PRAGMA foreign_keys = ON;
```

Rag.NET owns and creates these tables: `bm25_docs`, `parent_chunks`, `content_hashes`,
`embedding_versions`, `rag_documents`, `rag_chunks`, plus the memory tables. **We do not write to
them.** Ingest is serialized through one worker per project, so each file only ever has one writer.

`Rag.NET.Memory` stores past exchanges through the pipeline's `IVectorStore`. That keeps memory
inside the project automatically. Check in M1 that memory entries can't come back as document
chunks during retrieval. If they can, give memory its own `SqliteVectorStore` table in the same file.

### Tables Silvia owns (in each `project.db`)

```sql
CREATE TABLE schema_version (version INTEGER NOT NULL);
CREATE TABLE project_info (            -- lets an exported file describe itself
  id TEXT NOT NULL, name TEXT NOT NULL, embed_model TEXT NOT NULL, embed_dims INTEGER NOT NULL);

CREATE TABLE sources (                -- a watched folder
  id INTEGER PRIMARY KEY, path TEXT NOT NULL UNIQUE,
  include_globs TEXT NOT NULL DEFAULT '*', exclude_globs TEXT NOT NULL DEFAULT '',
  enabled INTEGER NOT NULL DEFAULT 1, last_scan_utc TEXT);

CREATE TABLE vec_chunks (             -- SqliteVectorStore
  document_id TEXT NOT NULL,
  chunk_index INTEGER NOT NULL,
  text        TEXT NOT NULL,
  metadata    TEXT NOT NULL,          -- JSON (file path, page, heading_breadcrumb, …)
  embedding   BLOB NOT NULL,          -- float32[dims], L2-normalized
  model_id    TEXT NOT NULL,          -- matches embedding_versions
  PRIMARY KEY (document_id, chunk_index)) WITHOUT ROWID;
CREATE INDEX ix_vec_chunks_model ON vec_chunks(model_id);

CREATE TABLE ingest_jobs (            -- survives crash/restart
  id INTEGER PRIMARY KEY, path TEXT NOT NULL, kind TEXT NOT NULL,  -- upsert|delete
  status TEXT NOT NULL, error TEXT, enqueued_utc TEXT NOT NULL, finished_utc TEXT);

CREATE TABLE settings (key TEXT PRIMARY KEY, value TEXT NOT NULL);  -- per project; never API keys
```

Migrations run per file when a runtime opens it, so an old exported project upgrades on import.

### `SqliteVectorStore`: how search works

The recommended approach is **managed brute force over an in-memory, normalized float matrix**,
with SQLite as the source of truth.

- Each project has its own instance over its own `project.db`. When the runtime opens, load
  `(rowkey, embedding)` for the active `model_id` into one contiguous `float[]`.
  Then answer each query with `TensorPrimitives.Dot` (SIMD, `System.Numerics.Tensors`) and a
  bounded top-K heap.
- `StoreAsync` and `DeleteByDocumentIdAsync` write to SQLite in one transaction, then patch the
  in-memory matrix.
- `MetadataFilter`: pre-filter candidates in SQL with `json_extract(metadata, '$.key') = ?` when a
  filter is present, then score only that subset.
- `MinScore`: cosine over normalized vectors, so the scale matches what Rag.NET expects of the
  dense path.
- Also implement `IChunkLookup` (parent-document and neighbour expansion) and `ICollectionManageable`.
- Don't implement `IHybridSearchable` yet: `EnsembleBehavior` already fuses this store with
  `SqliteBm25Index`. Revisit only if the eval set shows FTS5 + RRF in-store does better.

**Why not sqlite-vec first?** It means shipping a native extension for each platform, loading it
through `SqliteConnection.LoadExtension`, and it gets in the way of NativeAOT. Its KNN is also
brute force, so at personal scale it buys little.

Rough budget at 384 dims:

| Chunks | Matrix RAM | Query (1 core, SIMD) |
|---:|---:|---:|
| 50k | ~75 MB | ~5 ms |
| 250k | ~375 MB | ~25 ms |

The RAM figures are **per open project**. The LRU cap on open runtimes (default 3) and a global
matrix-memory budget (default 1 GB, which closes the least recently used project first) keep the
total bounded.

Past roughly 250k chunks in one project there are two steps, both behind the same `IVectorStore`. First, int8
scalar quantization (4× smaller, then rescore the top 100 in float32). Second, swap in sqlite-vec.

Write the store against Rag.NET's own store conformance tests, if `tests/` exposes a reusable suite.
If it doesn't, port the InMemoryVectorStore tests. That keeps it upstreamable as `Rag.NET.VectorStores.Sqlite`.

## 5. RAG composition (per project)

`ProjectRuntime.Build(project, modelPool)` builds one container per project:

```csharp
var db = Path.Combine(dataDir, "projects", project.Id, "project.db");
var s  = project.Settings;                              // from project.db `settings`

var services = new ServiceCollection();

// Shared, stateless, expensive: same instance in every project that uses the same model id.
services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(modelPool.Embedder(s.EmbedModel));
services.AddSingleton<IReranker>(modelPool.Reranker(s.RerankModel));

// DeepSeek speaks the OpenAI wire protocol: Microsoft.Extensions.AI.OpenAI with a custom endpoint.
// ModelPool caches one client per (endpoint, model).
services.AddSingleton<IChatClient>(modelPool.ChatClient(
    endpoint: new Uri("https://api.deepseek.com/v1"),
    apiKey:   credentialStore.Get("deepseek"),
    model:    s.ChatModel));                            // "deepseek-chat" (default) or "deepseek-reasoner"

// Everything below is private to this project and lives in its own file.
services.AddRagNet(rag => rag
    .UseVectorStore(sp => new SqliteVectorStore(db, dims: s.EmbedDims))   // ours
    .UseSqlitePersistence(db, collectionName: project.Id)                 // BM25 + parent chunks
    .UseContentHashRecordManager(db)
    .UseEmbeddingVersioning()
    .UseTokenAwareChunking()
    .UseChunkingStrategy<RecursiveChunkingStrategy>(o => { o.MaxChunkSize = s.ChunkTokens; o.Overlap = s.ChunkOverlap; })
    .AddPdfParser().AddWordParser().AddHtmlParser() /* + Epub, Email, Office */
    .UseConversationMemory(configure: m => m.UsePersistentMemory()));

var provider = services.BuildServiceProvider();          // disposed when the runtime closes
```

Check these against the real builder API when implementing:

- `UseVectorStore` (or its equivalent registration).
- That registering the embedder and reranker as instances makes Rag.NET use them rather than load
  its own. `UseOnnxEmbeddings` inside each project would load the model once per project.
- That disposing a project container does not dispose the shared instances. Rag.NET's named-pipeline
  docs say instance registrations stay owned by the root, and we rely on the same behaviour.

Defaults for new projects (each one is a per-project setting):

| Setting | Default | Why |
|---|---|---|
| Embedder | `bge-small-en-v1.5` ONNX (384d) | Fast on CPU, strong for its size; `multilingual-e5-small` as the non-English option |
| Reranker | `ms-marco-MiniLM-L-6-v2` ONNX | Rerank top 30 → keep 6 |
| Chat | DeepSeek `deepseek-chat` via `https://api.deepseek.com` | Cheap, OpenAI-compatible; `deepseek-reasoner` selectable for hard questions. Ollama remains an offline fallback through the same OpenAI-compatible client |
| Retrieval | dense + BM25 ensemble, rerank, redundancy filter 0.95, lost-in-the-middle | All built into Rag.NET |
| Chunk | 400 tokens / 60 overlap, header breadcrumbs | |

DeepSeek notes:

- Embeddings, reranking and BM25 stay local; **only the question plus the retrieved chunks
  (~6 × 400 tokens) go to DeepSeek per turn**, never whole files or the index.
- `deepseek-reasoner` streams a separate `reasoning_content` field before the answer. Show it in a
  collapsed "Thinking" section or drop it. Never feed it back into conversation history, because
  the API rejects that on the next turn.
- DeepSeek's context-caching discount applies to repeated prompt prefixes. Keep the system prompt
  fixed and put the retrieved context after it to benefit.
- Confirm the current model names and pricing at api-docs.deepseek.com when implementing; the
  model id is a setting, not a constant.

Changing a project's embedder bumps its `model_id`. `UseEmbeddingVersioning` then marks the stale
chunks, and that project's queue re-embeds them in the background. Search keeps using the old
matrix until the swap. Other projects are unaffected.

## 6. Ingestion (per project)

1. **Add source** (folder picker via NeoAstra dialogs) → row in that project's `sources` → full scan job.
2. **Scan**: `LocalFilesDataProvider` with the source's globs, then
   `pipeline.IngestFromProviderAsync(…, progress)`. The content-hash store skips unchanged files.
3. **Watch**: one `FileSystemWatcher` per source, debounced 2 s, coalescing per path, writing
   `ingest_jobs`. Rename becomes delete + upsert. A periodic rescan (on start and hourly) catches
   any events the watcher missed.
4. **Worker**: one `Channel<IngestJob>` consumer per open project. It takes the global
   `IngestThrottle` before embedding, so by default only one project embeds at a time. It runs at
   low priority, can be paused on battery, and retries a job with backoff before marking it failed
   and surfacing it in Activity. A closed project has no watchers: changes made while it was
   closed are picked up by its rescan on open. A "keep indexing in the background" option per
   project keeps that project's runtime pinned open instead.
5. **Delete**: `DeleteByDocumentIdAsync` on the vector store, plus BM25 and the parent store
   (through Rag.NET's data manager).
6. **Document ID** = normalized absolute path. Metadata carries `path`, `mtime`, `page`, and
   `heading_breadcrumb` for citations.

Progress (`IProgress<IngestionProgress>`) is bridged to a NeoAstra **event**
(`ingest.progress`, `Coalesce` overflow, payload includes `projectId`), so a fast ingest can't
flood the UI.

## 7. RPC surface (NeoAstra)

| Service | Methods | Kind |
|---|---|---|
| `projects` | `list`, `create`, `rename`, `delete`, `duplicate`, `export`, `import`, `open`; `changed` | calls + event; `delete` has `Permission = "projects:delete"` |
| `library` | `listSources`, `addSource`, `removeSource`, `rescan`, `listDocuments`, `getStats` | calls |
| `ingest` | `pause`, `resume`, `retryFailed`; `progress`, `jobChanged` | calls + events |
| `chat` | `ask(conversationId, question, options)` → stream of `{textDelta?, reasoningDelta?, sources?}` | **channel** (wraps `AskStreamingAsync`) |
| `chat` | `listConversations`, `getConversation`, `deleteConversation` | calls |
| `search` | `retrieve(query, topK, filter)` | call |
| `settings` | `getProject`, `updateProject`, `getGlobal`, `updateGlobal`, `listModels`, `downloadModel` | calls; download has a progress event |
| `shell` | `openFile(documentId, page?)`, `revealInExplorer(documentId)` | calls, `Permission = "shell:open"`; resolved to a path only through that project's `rag_documents` |
| `appearance` | `get`, `update`, `listFonts`; `changed` | calls + event (all windows restyle together) |

**Every method outside `projects.*`, `appearance.*` and the global settings takes `projectId` as its first field.**
A shared `ProjectScope` helper resolves it to a `ProjectRuntime`, and returns a typed
`project_not_found` error otherwise. Services never hold a "current project": the frontend owns
the selection. That way two windows can show two projects at once without crossing over. A
conversation id is only looked up inside its project's file, so a guessed id from another project
finds nothing.

DTOs go in one `[JsonSerializable]` context (NeoAstra requires it at compile time: `NEORPC009`).
Raise `InvocationTimeout` for `chat.ask` and `settings.downloadModel`. Cancellation from the UI
"Stop" button flows through the channel's `CancellationToken`.

## 8. UI

- **Project switcher** (top-left, Ctrl+P) lists projects with document counts and indexing status.
  "New project" asks for a name, folders and an embedder. Each project gets its own colour or icon,
  so it's always obvious which knowledge base you are asking. Switching cancels in-flight channels
  for the previous project. "Open in new window" shows two projects side by side.
- **Projects page**: rename, duplicate, export/import, delete (type the name to confirm), disk usage.
- **Chat**: streamed markdown answer with numbered citations `[1]`. A side panel shows the cited
  chunks. Clicking one runs `shell.openFile` (jumping to the page for PDFs where the viewer
  supports it). Conversations are listed on the left.
- **Search**: ranked chunks with file, breadcrumb, and score, without calling the LLM.
- **Library**: sources, per-folder counts, failed files, rescan.
- **Settings**: split into *Project* (embedder with re-index warning, chunking, retrieval, chat
  model, system prompt, cloud policy), *Global* (DeepSeek key, data folder, defaults for new
  projects, open-runtime limits), and *Appearance* (below).
- **Activity**: the queue, current file, and errors.
- Render model output with a markdown renderer plus **DOMPurify**. Never use raw `innerHTML`.

### Appearance: theme, font size, fonts

Appearance is an **app-wide** preference stored in `catalog.db` `global_settings`
(`appearance.*`). It is not per project: projects keep their accent colour or icon, which works
in either theme.

**Theme**: `System` (default) · `Light` · `Dark`

- All colours are CSS custom properties (design tokens) on `:root`: `--bg`, `--surface`, `--text`,
  `--text-muted`, `--border`, `--accent`, `--citation`, `--code-bg`, and so on. Each is defined
  twice, under `[data-theme="light"]` and `[data-theme="dark"]`. **No component uses a literal colour.**
  A lint rule (stylelint `color-no-hex` outside `tokens.css`) enforces this.
- `System` follows the OS live through `matchMedia('(prefers-color-scheme: dark)')`, which
  WebView2 reflects from Windows. The resolved theme sets `data-theme` and the CSS `color-scheme`
  property, so native form controls, scrollbars and the date picker match.
- **No white flash on start.** At window creation the host sets `NeoWindowOptions.BackgroundColor`
  from the saved theme, and calls `NeoWindow.BackgroundColor` again on every change. NeoAstra uses
  that colour while the page loads, during resize, and behind the page. The frontend keeps a mirror
  of the appearance settings in `localStorage`. A tiny inline script in `index.html` applies
  `data-theme` and font variables from that mirror before React mounts, then reconciles with
  `appearance.get`.
- **Chromeless title bar**: leave `NeoWindowTitleBar.SymbolColor` transparent so the native
  caption buttons follow the theme, and set it explicitly only when the app theme differs from the
  OS theme.
- **Dark-aware content.** Syntax highlighting uses paired light and dark themes (e.g. Shiki
  `github-light` / `github-dark`). Citation highlights, score bars and the project accent colours
  each have a tuned variant per theme. Images in rendered answers are not inverted.
- Both themes meet WCAG AA contrast for text, checked by an automated contrast test over the
  token pairs. Windows High Contrast is respected through `@media (forced-colors: active)`, using
  system colours and dropping custom backgrounds.

**Font size**

- Two independent sizes:
  - **Interface** (default 14 px, range 11–20): menus, lists, settings.
  - **Reading** (default 16 px, range 12–28): chat answers, chunk previews, search results. Long
    answers are where size matters most.
- Everything is sized in `rem`/`em` relative to `--font-size-ui` and `--font-size-reading`. No
  hard-coded `px` sizes for text, so spacing and icons scale too.
- Shortcuts: **Ctrl + = / Ctrl + − / Ctrl + 0** change the reading size (zoom the answer, not the
  chrome). Ctrl + mouse wheel works over the chat pane. These replace WebView2's built-in page
  zoom, which is disabled so the two can't fight.
- Line height (`compact` / `comfortable` / `spacious`) and reading column width (`narrow` /
  `wide` / `full`) sit next to the size controls.
- The layout is tested at the extremes: 11/12 px and 20/28 px must not clip or overlap at
  1280×720.

**Font selection**

- Three slots, each a CSS variable used through the token sheet:
  - **Interface font** (`--font-ui`, default `system-ui`, which is Segoe UI Variable on Windows 11).
  - **Reading font** (`--font-reading`, default the same as interface; serif options suit long reading).
  - **Monospace font** (`--font-mono`, default `Cascadia Mono` → `Consolas` → `monospace`):
    code, file paths, scores.
- **Choices** = bundled fonts + installed system fonts + "System default".
  - **Bundled** (OFL, served as local assets with no network): Inter, Source Serif 4, JetBrains
    Mono, and **Noto Sans Arabic / Noto Naskh Arabic**, so right-to-left documents and answers
    render well even when the OS lacks a good Arabic font.
  - **System fonts** come from `appearance.listFonts`, which the .NET side enumerates: on Windows,
    the `Fonts` registry keys under HKLM and HKCU (per-user installs); on macOS, `NSFontManager`;
    on Linux, `fc-list`. The result is cached and refreshed when Settings opens.
    The browser's Local Font Access API is not used, because it needs a permission prompt and
    WebView2 support for it varies.
- The picker shows each family **rendered in itself**, with search and a live preview pane: a
  sample answer with a citation, a code block and a line of Arabic.
- The chosen family is always followed by a fallback stack, e.g.
  `"<chosen>", "Segoe UI", "Noto Sans Arabic", system-ui, sans-serif`. A missing font, such as an
  uninstalled one or an imported settings file from another machine, degrades gracefully, and the
  picker marks it "not installed".
- **Validation**: `appearance.update` accepts a font family only if it's in the bundled or
  enumerated list. The frontend applies it with `style.setProperty('--font-ui', cssQuote(name))`,
  never by string-building a stylesheet, so a font name can't inject CSS.

**Mixed-direction text**: answers and chunks use `dir="auto"` per paragraph and CSS logical
properties (`margin-inline-start`, etc.), so Arabic and English mixed in one answer lay out
correctly with any font choice.

## 9. Security and privacy

- Indexing and search are fully local. The only network traffic is chat calls to DeepSeek and
  model downloads.
- **Data leaves the machine on every chat turn**: retrieved excerpts are sent to DeepSeek, which
  stores API data on servers in the PRC under its own privacy policy. Say so in first-run setup.
  Each **project** has a cloud policy: `deepseek` (default), `local-only` (Ollama), or
  `search-only` (no LLM at all). A sensitive knowledge base can be kept off the network entirely
  without affecting the others. Inside a cloud project, a per-source "never send to cloud" flag
  excludes those chunks from chat context.
- Store the DeepSeek API key in **Windows Credential Manager / DPAPI** (Keychain/libsecret on
  other platforms), never in `catalog.db` or any `project.db`, so exporting a project never leaks it.
- Exported `.silvia` files contain chunk text from the indexed documents. Treat them as being as
  sensitive as the source folders.
- Use NeoAstra's `ProductionLocalApp` profile with no remote content. Gate destructive and
  shell methods with permissions.
- Indexed files are untrusted input to the LLM. Use `Rag.NET.Security` sanitization and prompt
  hardening, and have the system prompt tell the model to treat context as data.
  *As built:* `Silvia.Core/Runtime/PromptGuard.cs`, without a new package. Passages lose invisible characters
  (format and tag characters), imitations of the prompt's structure are defused, a passage addressed to an AI is
  labelled, and a fixed system message after the editable system prompt says the context is data. Citations keep
  the document's own text.
- `shell.openFile` takes a document id and resolves it through that project's `rag_documents`, so
  the UI can't pass arbitrary paths.

## 10. Milestones

| # | Deliverable | Exit criteria |
|---|---|---|
| **M0** Spike (2–3 d) | Console app: Rag.NET + `InMemoryVectorStore` + ONNX embedder + DeepSeek over a sample folder; NeoAstra React template "hello RPC" | `AskAsync` answers with sources; template builds and runs on Windows |
| **M1** SQLite vector store (1 wk) | `Silvia.Storage.Sqlite` with tests (upsert, delete, filter, MinScore, restart persistence, 100k-chunk perf) | p95 query < 30 ms at 100k×384; all tests green |
| **M2** Headless core with projects (1.5 wk) | `Silvia.Core`: `ProjectManager` (catalog, create/delete/duplicate/export/import, crash recovery), `ProjectRuntime` + LRU, `ModelPool`, sources, queue, watcher, job persistence, embedding-versioning re-index | Folder edits reflected in search within 5 s; kill-and-restart loses no jobs; **isolation test suite green** (below) |
| **M3** Desktop shell (2 wk) | NeoAstra app: design tokens + light/dark/system theme and font variables **from the first component**; project switcher + Projects page, Library, Chat (streaming + citations), Search, Activity; Appearance settings (theme, two font sizes, three font slots, bundled fonts, system font enumeration) | Daily-usable end to end on Windows with ≥ 2 projects; screenshot check of every screen in light and dark at min/default/max font size; no flash on start in dark mode; contrast test green |
| **M4** Quality pass (1 wk) | `eval/` golden set (~30 Q&A over your own docs), `Rag.NET.Evaluation`; tune chunk size, TopK, reranker | Recorded baseline; regressions visible |
| **M5** Polish and ship | Settings + model download, credential storage, tray icon, `dotnet neoastra` publish/installer | Installable build; fresh-machine smoke test (`scripts/smoke-test.ps1`, which runs `Silvia.exe --smoke-test` on an empty data folder) |
| Later | Opt-in read-only cross-project search (`FederatedVectorStore`), int8 quantization / sqlite-vec, GraphRAG/RAPTOR, OCR for scanned PDFs, MCP server (`Rag.NET.Mcp`, one tool per project), macOS/Linux | |

### Isolation test suite (M2 exit gate)

Each test uses two projects, A and B, with distinct fixture corpora and a unique marker phrase in each.

1. Ingest A only. `search`, `chat` and `listDocuments` on B return nothing from A, and the marker
   isn't found in B's `project.db` (checked by grepping the raw bytes of the file).
2. The same folder is added to both. Deleting a file removes it from both, and removing the source
   from A leaves B's copy intact.
3. Delete A while B is ingesting. B finishes, and no file handle on A's folder remains.
4. Different embedders (384d vs 768d) are loaded at once. Both search correctly, and the model pool
   holds two embedders, not four.
5. Two projects on the same embedder share one `InferenceSession`. Closing one keeps the session
   alive; closing both releases it.
6. Conversation memory: a fact stated in a chat in A is never recalled in B.
7. Export A, import it as A′. Results match A. Deleting A leaves A′ working.
8. RPC: calling any method with B's `projectId` and A's `conversationId` or `documentId` returns
   `not_found`.
9. Crash during create, delete or import (kill the process at each step). Restart recovers to a
   consistent catalog.

## 11. Risks and open questions

- **NeoAstra is young and mostly a single author's project** (its v2 spec is in flux). Keep RPC
  services thin and keep all logic in `Silvia.Core`, so the host could be swapped for Photino or
  Avalonia + WebView if needed.
- **NativeAOT**: NeoAstra is AOT-friendly, but Rag.NET parsers (PdfPig, OpenXml) and ONNX Runtime
  may not be. Plan for a normal self-contained publish and treat AOT as a stretch goal.
- **Many SQLite connections to one file**: WAL plus `busy_timeout` plus a single ingest writer
  per project should be enough. Verify under M2 load. If not, split each project into
  `index.db` (Rag.NET stores) and `project.db` (ours) inside the same project folder; the
  isolation boundary stays the folder.
- **`UseSqlitePersistence` `collectionName`** wipes persisted data when it changes. It is the
  immutable project id, so it never changes. It is deliberately *not* tied to the embedder:
  BM25 doesn't depend on embeddings, and wiping it would leave the content-hash store skipping
  "unchanged" files that are no longer indexed.
- **Per-project DI containers**: Rag.NET is designed around one container, or named child
  containers built by its own factory. Building many root containers ourselves is untested
  upstream. Check for static or global state (meters, `ActivitySource`, caches) in M0. If
  something leaks across containers, fall back to Rag.NET's named pipelines and rebuild the root
  container when projects are added or removed.
- **File locks on delete/import**: pooled `Microsoft.Data.Sqlite` connections keep the file open.
  Close the runtime, call `SqliteConnection.ClearPool` for that project's connections, then delete;
  retry on `IOException` (antivirus, indexers).
- **Many projects, little RAM**: the LRU cap and matrix budget bound memory, but reopening a large
  project reloads its matrix (~1 s per 100k chunks). If that's too slow, persist the matrix as a
  memory-mapped sidecar file.
- **Package versions**: pin `Microsoft.Data.Sqlite` and `SQLitePCLRaw` to what Rag.NET uses
  (10.0.14 / 3.0.5) to avoid duplicate native e_sqlite3 binaries.
- **DeepSeek availability and rate limits**: the API has had capacity incidents. Wrap the chat
  client with `Rag.NET.Resilience` retry, and optionally its fallback chain to local Ollama, so
  an outage degrades to a slower local answer instead of an error.
- **Rag.NET internals that call the chat model** (query rewriting, HyDE, GraphRAG/RAPTOR
  summarization) would also send text to DeepSeek. Keep them off by default and switch them on
  deliberately.
