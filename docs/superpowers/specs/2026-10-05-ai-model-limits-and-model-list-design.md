# AI: the model's own limits, and a model list in Settings

The owner asked on 2026-10-05: "please make ai api call suport auto dectect max context window of selected model and make it has higher context size both input/output (should dynamic adjust based on max context window) and modify ai setting to more user friendly ,should auto load model name for user to select".

Lines marked **(R)** are rulings made without asking, under the owner's standing goal ("autonomous continue implement until finish"). Each can be changed.

## Today

Every limit is a fixed number, the same for a model with an 8,000-token window and one with a million:

| | Ask MicaStats | MicaPad AI |
|---|---|---|
| Output | 2,000 tokens | 4,096 tokens |
| Input | no limit on a question | 8,000 characters for a rewrite, 24,000 otherwise |
| Reply | no limit | 64,000 characters |
| Conversation | sent again whole with every question, never trimmed | none |
| A reply cut at the limit | not noticed | "Cut short at the length limit" |

Also fixed: `get_note` 16,000 characters and 400 lines a call, a kept tool result 20,000 characters, 8 passages for Ask your notes. The model is a name typed into a box. Nothing asks the provider what models it has or how large they are.

## 1. What a model can take

### 1.1 Asking the provider

A model's limits are read from the provider's model list.

- **Claude.** The list (`GET /v1/models`, through the SDK) gives each model its name, its input window (`max_input_tokens`) and its largest output (`max_tokens`). It needs the saved key.
- **OpenAI-compatible.** `GET {base URL}/models`, with the saved key when there is one. Every server gives the ids. The window is taken from the first of these fields a model has, in this order: `max_model_len` (vLLM, SGLang), `context_length`, `context_window`, `max_context_length`, `max_input_tokens`, `meta.n_ctx_train` (llama.cpp). The largest output from the first of: `max_output_tokens`, `max_completion_tokens`, `top_provider.max_completion_tokens`.
- **(R)** A server that reports no window (OpenAI itself, Ollama) leaves it unknown. No table of model names is kept in the app: it would go stale, and a wrong number is worse than none. The user can state the window (2.3).
- **(R)** Ollama's `/api/show` is not asked. It reports what the model could take, not the window the server runs it with (`num_ctx`), and trusting the larger number would cut prompts silently.

### 1.2 What comes back is not trusted

- A model id is shown as plain text: at most 200 characters, control and format characters removed; an empty one is dropped. At most 500 models are kept.
- A window under 1,024 tokens or not a whole number is "unknown". A window over 2,000,000 counts as 2,000,000. An output limit over the window is the window.
- The answer is read up to 4 MB; a longer one is a failure.
- A failure (no network, a refusal, an answer that is not the expected shape) is said in a sentence in Settings and logged by its exception type. It never stops a request: the limits are then unknown.

### 1.3 When the provider is asked

- In **Settings → AI**: when the section is shown, when the provider is changed, when the base URL was changed and the box loses focus, when a key is saved or removed, and on **Refresh**. Never on a keystroke.
- **(R)** Outside Settings: once per run, in the background, the first time an AI request is made with a model whose limits are not known for the current provider, address and model. That request uses what is known; the answer serves the next.
- **(R)** Never while every AI feature is off, and never by itself at startup. Listing models sends the key to the provider, as a question does; it happens only when the user is setting AI up or using it.
- What was learned for the chosen model is kept in the settings with what it belongs to (provider, address, model), so the next start has it at once. It is ignored when any of the three changed.

## 2. Limits that follow the window

### 2.1 Counting

- **(R)** Limits are counted in estimated tokens, not characters. The estimate: a quarter of a token for each ASCII character, one token for each other character (Thai, Chinese, emoji), rounded up. It is the safe side for Thai, which a fixed characters-per-token ratio is not, and close for English.
- It is an estimate. The window is never filled: see the shares below.

### 2.2 The budget

`W` is the window in use (2.3). With `W` unknown, every number is today's, so nothing changes for a provider that reports nothing.

With `W` known:

| What | Rule | At 8,192 | At 262,144 | At 1,048,576 |
|---|---|---|---|---|
| Ask MicaStats output | `W / 8`, at least 2,000, at most 16,000, and never over `W / 4` | 2,000 | 16,000 | 16,000 |
| MicaPad output | `W / 4`, at least 2,048, at most 32,000, and never over `W / 2` | 2,048 | 32,000 | 32,000 |
| MicaPad rewrite input | 80% of MicaPad's output (a rewrite must come back whole) | 1,638 | 25,600 | 25,600 |
| MicaPad read input (Summarize, Explain, Ask AI, Draw as diagram) | `W / 2` less MicaPad's output, at least 1,000 or `W / 4` if that is less | 2,048 | 99,072 | 492,288 |
| Ask conversation sent again | `W / 2` | 4,096 | 131,072 | 524,288 |
| `get_note` in Ask, per call | `W / 16` tokens of text, between 4,000 and 64,000, and never over `W / 4`; lines: 400, 1,000 from a window of 32,000, 2,000 from 128,000, 4,000 from 1,000,000 | 2,048 | 16,384 | 64,000 |
| A tool result kept in the conversation | the `get_note` share and a quarter more | 2,560 | 20,480 | 80,000 |
| Passages for Ask your notes | 8; 12 from 32,000; 20 from 128,000 | 8 | 20 | 20 |
| MicaPad reply, in characters | 4 for each output token, at least 64,000 | 64,000 | 128,000 | 128,000 |

- Where the provider gave the model's largest output, the two output numbers are never above it.
- *(Added after Task 1's review.)* **The pieces fit the window.** The "never over" parts of the rules are for small windows, where the floors alone would ask for more than the model has: for every window, MicaPad's input and output together are at most the window, and Ask's output and one note read are at most half of it. A very small model (under about 12,000 tokens) is still tight for Ask MicaStats, whose tools and instructions take a few thousand tokens by themselves; the provider's refusal is shown as it is today.
- **A small model is protected too.** Today 24,000 characters go to an 8,192-token model and the provider refuses them. With the window known, the pane says the text is too long before anything is sent.
- **(R)** MCP clients keep today's fixed limits for the note tools. They have a window of their own that MicaStats does not know.
- **(R)** The nine PC tools keep their limits (history points, process counts, report sizes). They are sized for what is useful, not for the window.
- The daily question limit, the tool rounds (8) and the timeouts do not change.

### 2.3 The window in use

- **Context window** in Settings → AI: **Auto**, or a number of tokens.
- Auto: the window the provider reported for the model; unknown when it reported none.
- A number: that many tokens, and never more than what the provider reported. With a provider that reports nothing, the number is the window.
- **(R)** Auto is the default. A larger window means more text can go in one request and a longer conversation is sent again with each question, which costs more on a paid provider; the number is how a user holds that down.

### 2.4 What a user sees when text is too long

- MicaPad, before anything is sent: "This text is too long for a rewrite with this model: about 31,000 tokens, and it can take about 25,600. Select less text." For the other actions: "This text is too long for this model: about …, and it can take about …".
- With the window unknown, the sentences are today's ("Select less text: at most 8,000 characters for a rewrite").

## 3. Ask MicaStats: two things it lacks today

- **A reply cut at the length limit is said.** Under the answer: "The answer was cut short at the length limit." Today such an answer ends like a whole one.
- **A long conversation is trimmed to fit.** Before each request, the oldest exchanges (a question with its tool calls, their results and its answer, always whole) are left out until what is sent fits the share of 2.2. The system prompt and the question being asked always go. With the window unknown the share is 48,000 tokens.
  - The turns stay on screen. The status line says once: "Earlier turns are no longer sent to the model: the conversation is longer than it can take."
  - Trimming removes nothing from the conversation itself: what was read from notes is still taken back when access changes, and a credential store still ends it.

## 4. Settings → AI

- **The model is picked from a list.** For each provider, the model box becomes a list that is filled from the provider. Each line shows the model's name and, when known, its window: "glm-5.2 · 1,048,576 tokens". A name can still be typed, for a model the list does not have or when the list cannot be loaded.
- **Refresh** loads the list again. Under the box, one line says what happened: "9 models from llm.example.com", "Loading…", or the failure in words with "You can still type a model name."
- **The limits in use**, one line under that: "Context window 262,144 tokens (from the server) · answers up to 16,000 tokens · MicaPad reads up to about 99,000 tokens of text". With the window unknown: "Context window not known for this model: the standard limits are used. Set it below if you know it."
- **Context window**: the box of 2.3, with Auto and common sizes (8,000, 16,000, 32,000, 64,000, 128,000, 200,000, 256,000, 1,000,000), and any number typed.
- Choosing a model from the list saves it at once and takes its limits with it.
- **Test connection** is unchanged, and its result line also names the window when it is known.
- The Ask window's model line and MicaPad's source line are unchanged.

## 5. Failures

- No list, or a model that is not in it: the limits are unknown, the standard ones are used, and everything works as today.
- A provider that refuses a request because it is too long anyway (the estimate was low, or the server runs the model with a smaller window than it reports): the existing error is shown. **(R)** The window is not lowered by itself; the user can set it.
- A settings value that cannot be read is Auto.

## 6. Privacy and safety

- **The model list goes only to the provider the user chose,** with the same key and through the same HTTP path as a question, and only when the user is in Settings → AI or is using AI (1.3).
- **Nothing new is sent in a question.** What changes is how much may be sent, and that is still what the pane names: MicaPad's source line already says how many characters and where they go.
- **Consent is unchanged.** No check in MicaPad's window files is moved, removed or reordered. The setting is still read at every entry point and right before each request.
- **Text from the provider is data.** A model id is shown as text only; numbers are clamped (1.2). A server cannot make MicaStats send more than the ceilings of 2.2.
- **Credentials.** A larger read still passes through the same masking; no part of a `{{secret:ID}}` marker leaves the PC.
- **Notes taken back.** A larger kept tool result is taken back by the same rule when notes access changes.
- Logs: a failure by its exception type and the host only; never a key, a full address with a path or query, or text.
- No real service address appears in any file of the repository; an example host is `llm.example.com`.

## 7. Tests

- Pure: the token estimate; the budget at each window size of the table, with and without a reported output limit and a user's number; the parsing of each provider's list (each field name, bad values, the ceilings, 500 models, a 4 MB answer); the trimming of a conversation (whole exchanges, tool pairs kept together, the system prompt and the current question always sent).
- With a scripted HTTP handler, never the network: the Claude list through the SDK, the compatible list, a failure of each kind, that the key goes only to the configured host.
- UI, on the shared UI thread: the model list fills, a typed name is kept, a pick saves the model and its limits, Refresh, the status and limits lines, the triggers of 1.3 and that a keystroke is not one.
- That nothing is asked while every AI feature is off.
- Every existing test that pins one of today's numbers still passes with the window unknown.

## 8. Docs

`GUIDE.md` and `README.md` (English and Thai): the model list, the context window setting and what Auto means, that limits follow the model, the table of 2.2 in short, the two new Ask lines, and that the list is asked for only in Settings or when AI is used.
