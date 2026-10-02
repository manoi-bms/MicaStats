# MicaPad: search every note by words and by meaning

Asked by the owner on 2026-10-02: "please modify MicaPad to support robust search feature using
vector search with custom embedding model (external api) and custom reranker model (external api)".
Decisions taken with the owner:

- **API format:** embeddings in the OpenAI format (`POST {base}/embeddings`), rerank in the
  Cohere/Jina format (`POST {base}/rerank`). These are what TEI, vLLM, Infinity, LiteLLM, Jina and
  Cohere serve.
- **Hybrid retrieval:** a local keyword index and the vector index both find candidates; the merged
  list goes to the reranker. Keyword search works offline and with no server at all.
- **A side pane, Ctrl+Shift+F**, where History opens today.
- **The vector index is local and encrypted** with the notes key; no external vector database.
- **Scope: every note, open and closed**, as its current text. Not history versions, not folders.
- **Keyword engine: built in** (BM25 over words, Thai/CJK as two-character pieces), no new library.
- The owner then asked to continue autonomously; the remaining choices are rulings marked **(R)**,
  each with its reason.

## What does not change

- Find and Replace (Ctrl+F, Ctrl+H) still searches the shown note only.
- Notes, files, history and the vault are untouched; search only reads.
- With **Search by meaning** off (the default), nothing leaves the PC.

## 1. The Search notes pane

- **Ctrl+Shift+F** or a new toolbar button (magnifier, tooltip "Search notes (Ctrl+Shift+F)")
  toggles a pane in the editor's right column. History and Search share the column: opening one
  closes the other. **(R)** One column keeps the editor wide on a laptop screen.
- A query box at the top gets the focus when the pane opens; when the editor has a selection of one
  line, it is put in the box, as Ctrl+F does.
- Results are searched 300 ms after typing stops; Enter searches at once. An empty query shows
  nothing. A query keeps running when typed over: the older search is cancelled.
- Each result shows the note title, a "closed" tag for a closed note, the line number, and a snippet
  of the passage (up to 3 lines, about 200 characters around the best keyword hit, matched words in
  bold). **(R)** No score is shown: reranker and fusion scores are not comparable to each other and
  invite misreading.
- At most **3 results per note** and **20 results** in all. **(R)** A long note would otherwise fill
  the list with neighbours of one hit.
- Picking a result (click, or arrows and Enter in the list) opens the note and selects the passage:
  - a note open in this window is shown;
  - a note open in another MicaPad window is shown there and that window comes forward;
  - a closed note is reopened in this window.
  The passage is found again in the current text (its first line's text, searched near the recorded
  line), so an edit since indexing moves the selection with the text; failing that, the recorded line
  is selected. Esc in the pane returns focus to the editor; Esc again (or the pane's close button)
  closes the pane.
- A status line under the query box says how the last search ran:
  - "Words" — meaning search is off;
  - "Meaning + words" or "Meaning + words, reranked";
  - "Words only: the embedding server could not be reached" (or "refused the key", or "timed out");
  - "Not reranked: the reranker could not be reached" (same variants), the list in fused order;
  - "Indexing 3 of 9 notes…" while vectors are being made, with results from what is indexed.
- The pane follows MicaPad's light/dark theme like the History pane.

## 2. Settings → MicaPad → Search

A **Search** group in the MicaPad section, in a `SearchSettingsPanel` user control (as
`AiSettingsPanel` is for AI):

- **Search by meaning** toggle, off by default. Text: "Sends passages of your notes to the embedding
  server below to find notes by meaning. Keyword search works without it."
  - **Server**: an `http`/`https` base address such as `http://gpu:8000/v1`; MicaPad adds
    `/embeddings`. Normalized and refused like the Kroki server box (`KrokiClient.TryParseServer`):
    host required, no user name, query or fragment, trailing slash removed.
  - **Model**: free text, may be empty (then no `model` field is sent; TEI and Infinity serve one
    model). **(R)**
  - **API key**: a password box with Save; once saved it shows "Saved" and Remove, as the AI keys do.
    Stored in `secrets.bin` under `pad-embedding-key`. Sent as `Authorization: Bearer <key>`; no
    header when none is saved.
  - **Test**: embeds the text "MicaPad test" and shows "OK: 1024 dimensions" or the error.
- **Rerank results** toggle, off by default, enabled only while Search by meaning is on. **(R)** The
  reranker sees passages chosen with the vector index's help; on its own it would rerank only
  keyword hits, which is allowed but needs a second privacy decision, so it is tied to the first.
  - Server, Model, API key (`pad-rerank-key`) and Test, as above; MicaPad adds `/rerank`. Test sends
    the query "test" with the documents "test" and "other" and shows "OK".
- **Index**: a status line ("312 passages from 9 notes; 312 with meaning") and a **Rebuild index**
  button that drops every stored vector and indexes again.
- Turning **Search by meaning off deletes** `search\vectors.bin` and the vectors in memory, and says
  so in the toggle's text. **(R)** Vectors are derived from note text; keeping them after the owner
  said "stop" would be surprising.
- Changing the embedding server or model drops the stored vectors (they no longer match the model)
  and indexes again; the Server box hint says so.

Config (`AppConfig`): `PadSemanticSearch` (bool, false), `PadEmbeddingServer` (string, ""),
`PadEmbeddingModel` (string, ""), `PadRerank` (bool, false), `PadRerankServer` (string, ""),
`PadRerankModel` (string, ""). Server strings are stored normalized or empty.

## 3. How it works

Every unit below lives in `Services/Pad/Search/` and is free of WPF, except the pane and the
settings panel in `Pad/`.

### 3.1 Passages (`NotePassages`)

- A note's text is cut into passages at Markdown headings and blank lines, packed up to about
  **800 characters**, never over **1,500** unless a single line is longer (that line is cut at 1,500).
- A fenced code block is kept whole when it fits in 1,500 characters; a longer one is cut at line
  boundaries.
- Each passage records its first and last line (1-based) and its heading path.
- The text sent for embedding and reranking is `"{title} › {heading path}\n\n{body}"` (the heading
  part left out when there is none). **(R)** The title and headings carry meaning the body often
  lacks ("VPN" in a heading, "it does not connect" in the body).
- Credential references `{{secret:XXXXXXXX}}` are replaced by `[credential]` in everything indexed or
  sent. The vault is never read.
- A note over **2 MB** of text is indexed up to its first 2 MB. **(R)** Bounds memory and server cost
  for a pasted log.

### 3.2 Keyword index (`KeywordIndex`)

- In memory only, rebuilt from the notes when MicaPad starts and updated per note on change.
- Tokens, lower-cased with the invariant culture:
  - a run of letters/digits joined by `. - _ : /` is one token **and** its parts are tokens
    (`ERR-1042` → `err-1042`, `err`, `1042`; `10.0.0.1` → `10.0.0.1`, `10`, `0`, `1`), so codes,
    addresses and IDs match exactly and by part;
  - Thai (U+0E00–U+0E7F) and CJK runs become overlapping two-character pieces (a single character
    stays one piece). **(R)** No dictionary is needed and any Thai word in the query is found where
    its letters appear.
- BM25 (k1 = 1.2, b = 0.75) over passages. The query's last Latin token also matches as a prefix
  (at most 50 expansions) so results appear while a word is being typed.

### 3.3 Embedding and rerank clients (`EmbeddingClient`, `RerankClient`)

- Embedding request: `POST {server}/embeddings`, JSON `{"model": m, "input": [texts]}` (`model`
  omitted when empty). Answer: `data[].embedding` (floats), placed by `data[].index`. A count or
  dimension that does not match is an error.
- Rerank request: `POST {server}/rerank`, JSON `{"model": m, "query": q, "documents": [texts],
  "top_n": n}`. Answer: `results[]` with `index` and `relevance_score`.
- The system's proxy settings; redirects refused (`AllowAutoRedirect = false`); answers over 32 MB
  refused. Timeouts: 60 s for an indexing batch, 5 s for a query embedding, 8 s for a rerank. **(R)**
  A CPU-only server can take seconds per batch; a search must stay interactive.
- Failures are classified for the status line: unreachable (network error, 3xx, 5xx), key refused
  (401/403), rate limited (429), timed out, bad answer (unparseable or mismatched). Never throws for
  these.
- Indexing sends **16 passages per request**.

### 3.4 Vector store (`VectorStore`)

- In memory: passage hash (SHA-256 of the sent text) → unit-length float vector. Keyed by hash, not
  by note, so identical passages share one vector and an unchanged passage is never sent again.
- On disk: `%APPDATA%\MicaStats\MicaPad\search\vectors.bin`, encrypted with the notes key (the same
  `StoreCipher` as the notes, through a new `NoteStore.EncryptBytes` / `TryDecryptBytes`), written
  through `AtomicFile`, at most once every 10 s while indexing and once when indexing finishes.
- The file carries a fingerprint (server + model) and the dimension. A file whose fingerprint or
  dimension does not match the settings, or that cannot be decrypted or parsed, is discarded and
  everything is indexed again; this is logged as a warning without contents.
- Vectors whose hash no current passage uses are dropped on each save.
- Capacity: **20,000 vectors**. **(R)** 80 MB at 1,024 dimensions. Beyond it, passages of the
  least-recently modified notes go without vectors and the Settings status says how many.

### 3.5 Indexer (`SearchIndexer`) and search (`NoteSearch`)

- One app-wide `NoteSearchService`, created with the MicaPad workspace (`App.OpenPad`), owning the
  passages, the keyword index, the vector store and one background worker.
- **Feeding:** `PadWorkspace` raises a new `NoteTextChanged(OpenNote)` from `NotifyChanged` and
  `Rename`, and `NoteDeleted(string id)` from `DeleteClosed`. The service reads the open note's text
  on the UI thread 2 s after the last change (debounced per note) and hands it to the worker.
  Closed notes are read with `NoteStore.LoadText` on the worker; a closed note that shadows a file
  is indexed from MicaPad's stored copy, not the file. **(R)** Reading files on disk is the
  "folders" scope the owner left out, and the stored copy is what reopening shows first. At start, and each time the pane
  opens, the service reconciles with `LoadAllMetas`: notes that no longer exist are removed, new
  ones are added.
- **Worker:** re-cuts the note into passages, updates the keyword index, and (with meaning search
  on) queues the passages without vectors. Embedding runs in batches; a failed batch is retried
  after 1, 2, 5, 10 then every 30 minutes, and the status line names the failure.
- **Search** (cancellable; a newer query cancels an older one):
  1. Keyword top 50 passages.
  2. With meaning search on: the query is embedded (5 s; the last 20 query vectors are cached) and
     the top 50 passages by cosine are taken.
  3. The two lists are merged by Reciprocal Rank Fusion (k = 60).
  4. With rerank on: the top 40 go to the reranker (8 s) and are ordered by its score.
  5. At most 3 per note, 20 in all.
  A step that fails or times out is skipped and the status line says which.

## 4. Privacy and safety

- Off by default; only passage texts (never whole files, never credential secrets) are sent, and
  only to the two servers in Settings.
- Keys live only in `secrets.bin` (DPAPI, current user). They are never written to `config.json`,
  never logged, never shown after saving.
- Logs (area `search`) carry counts, HTTP status codes and exception type names only: never note
  text, queries, keys or server answers.
- The vector file is encrypted with the notes key and deleted when meaning search is turned off.
- Tests never touch the network or `%APPDATA%`: the clients take an `HttpMessageHandler`, the store
  takes a folder and a cipher.

## 5. Testing

- **Passages:** headings and blank lines, packing to 800, the 1,500 cap, long lines, fenced code kept
  whole and cut when long, line numbers, heading paths, `[credential]` replacement, the 2 MB cap,
  Thai text.
- **Keyword index:** Thai query finds Thai text; `ERR-1042`, an IP address and an HN number match
  exactly and by part; BM25 orders a denser passage first; prefix matching of the last token;
  removing and updating a note.
- **Clients** against a fake handler: request JSON shape, omitted model, Bearer header present and
  absent, `index` ordering, count/dimension mismatch, 401/429/5xx/redirect/timeout classification,
  oversized answer refused.
- **Vector store:** encrypted round trip; fingerprint and dimension mismatch discard; corrupt file
  discard; unused vectors dropped; capacity.
- **Indexer/search:** only changed passages are embedded again; a server failure leaves keyword
  results and the right status; rerank failure keeps fused order; RRF arithmetic; 3-per-note and
  20 caps; cancellation of an older query.
- **UI (shared UiThread):** Ctrl+Shift+F opens and closes the pane and closes History; a result for
  a closed note reopens it with the passage selected; Settings saves and normalizes the servers and
  keeps keys out of `config.json`.
- **End to end (owner):** with their embedding and reranker servers, in the deployed build.

## Not in this feature

History versions, files outside MicaPad, query or document instruction prefixes for particular
models, other API formats (TEI native, Ollama native), an external vector database, and Ask
MicaStats using note search.
