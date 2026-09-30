# MicaStats — User Guide

A complete guide to using, customizing, and mastering your hardware telemetry overlay.

---

## 🛠️ Getting Started

### 1. Installation
The app is provided as a unified installer:
- **`MicaStats-vX.Y.Z-Setup.exe`**: A high-performance setup that handles your Start Menu, Desktop shortcuts, and ensures the app is registered correctly for startup.

Download the latest version from [GitHub Releases](https://github.com/manoi-bms/MicaStats/releases) and launch.

---

## 🖥️ The Overlay

The overlay is a slim, elegant pill that sits directly on your **Windows 11 taskbar**. It displays real-time telemetry from your hardware:

### 📈 Included Metrics
- **CPU**: Total processor load percentage.
- **RAM**: Real-time memory pressure.
- **NET**: Combined Upload and Download speeds.
- **GPU**: Raw load and temperatures from your graphics processor.
- **DISK**: Real-time activity and storage usage for multiple drives simultaneously.

### 🍏 iStat-Style Taskbar (default)
The overlay ships in a stacked layout modelled on iStat Menus for macOS: every metric is its own
module with a small dim label above a bold value, and network shows paired **↑ upload / ↓ download**
lines (red up, cyan down). With **Live Graphs** on, each module gains a full-height mini bar chart,
and network gets a mirrored up/down graph around a dashed axis — the iStat signature.

Prefer the classic single-row layout? Turn off **iStat Taskbar (stacked)** in **Appearance**; the
classic mode keeps all per-section colour customisation. The stacked mode uses a fixed palette
(grey labels, white values, cyan graphs) so it always looks cohesive.

### 🚫 Start Menu Avoidance
A centred Windows 11 taskbar moves its Start button **left** as apps open, so a fixed overlay
would eventually sit underneath it. The stacked overlay watches the taskbar's own buttons
(widgets, Start, tray) and stays inside the free corridor between them — **sliding left into
unused space first**, so it keeps every module whenever the corridor has room, and drifting
back to your chosen spot as apps close. When the corridor tightens, the sparklines squeeze
down first (to 40% of their width) so the layout always fills the available space exactly;
below that they disappear, then the chrome itself tightens — slimmer pod padding and column
gaps, whitespace rather than content — and only when even the compact layout cannot fit do
trailing modules hide one by one — temperature yields first (its reading also lives in the
CPU dropdown header), storage stays visible longer, and network and CPU survive longest —
with a small **⋯** marker showing that modules are elided rather than missing. Everything hidden stays one hover or click away in the panels. Turn
this off with **Avoid Start Menu** in **Appearance**.

### 📉 Live Graphs
Turn on **Live Graphs** in **Appearance** to draw a small bar chart of recent history beside each
reading, so you can see a trend rather than just an instant value. Graphs work in stacked, Standard
and Compact label modes, and a sensor that cannot be read shows a flat baseline rather than a
misleading zero.

---

## 📊 The Stats Panel

**Click the overlay** to open the stats panel — an iStat Menus-style dropdown of dark rounded
cards with the detail the taskbar has no room for:

- **CPU card**: stacked User/System history bars, a ● User / ● System legend, one ring gauge per
  logical processor, and uptime.
- **Memory card**: MEMORY and COMMIT ring gauges, a memory-pressure history graph, and a
  Used / Free / Committed / Cached breakdown with top memory consumers.
- **GPU card**: usage and temperature rings above a history graph.
- **Network card**: big upload/download readings and a mirrored ↑/↓ graph around a dashed axis,
  with window peaks.
- **Disks card**: activity history for the busiest drive plus a row per selected drive.
- **Processes card**: top CPU consumers with their memory use.

Everything follows the iStat two-hue rule — cyan for the primary series, red for its counterpart
(System time, Upload) — on near-black cards.

Each card is badged with its own icon — a processor die for CPU, a memory module for Memory, a
display for GPU, paired ↑/↓ arrows for Network, a drive for Disks — drawn from **Segoe Fluent
Icons**, the Windows 11 system icon font (Segoe MDL2 Assets on Windows 10). The same glyph
marks that section everywhere it appears, including the hardware inspector's tabs, so the eye
can find a section without reading the label.

**Or just hover**: pause over any taskbar module and its own compact dropdown opens — CPU (with
top processes), Memory, GPU, Network or Disks — then retargets as you slide along the taskbar,
exactly like iStat Menus. It never steals focus; clicking inside pins it as the full panel. Turn
this off with **Hover Panels** in **Appearance**.

Every card ends with **quick-action buttons** that open the matching Windows tool — Task Manager
and Resource Monitor from CPU and Memory, Display Settings from GPU, Network Settings and
Connections from Network, Disk Management and Storage Settings from Disks.

Panels open with a quick rise-and-fade, and every ring gauge sweeps from zero to its reading
— then eases between values while the panel stays open. All motion runs on WPF's
GPU-composited animation clock and exists only while a panel is on screen, so the idle app
animates nothing; the taskbar overlay keeps its efficient once-per-second GDI+ pipeline.

Press **Esc** or click anywhere else to dismiss it. Process sampling only runs while the panel is
open, so a closed panel costs nothing.

If you would rather clicking never opened the panel, turn off **Panel on Click** in **Appearance**;
the panel stays available from the right-click menu as **Show Stats Panel**.

---

## 📸 Screen Capture

Right-click the overlay (or open **Settings → Capture**) for **Capture Region**, **Capture
Window**, **Capture Screen** and **Capture All Screens**. The shortcuts work anywhere in
Windows:

| Shortcut | Captures |
| --- | --- |
| **Ctrl+Shift+1** | Region — pick a rectangle, window or screen |
| **Ctrl+Shift+2** | The window currently in front |
| **Ctrl+Shift+3** | The screen the pointer is on |

### The region picker

Choosing a region **freezes the screen first** and lets you select on that still image, so
menus and tooltips stay open instead of vanishing when the picker takes focus, and the
selection is exact even across monitors running different scaling.

- **Drag** for a rectangle, or **click** a window or screen the picker highlights for you
- A **magnifier** follows the pointer with a pixel grid, crosshair and the **hex colour**
  under the cursor — it doubles as an eyedropper
- Edges **snap** to window and monitor borders; hold nothing and it just works
- **Arrow keys** nudge (Shift for 10px, Ctrl to resize), **M** magnifier, **S** snapping,
  **A** everything, **Enter** accept, **Esc** or right-click cancel

### The editor

Unless you turn it off, each capture opens in an annotation editor:

- **Arrow, rectangle, ellipse, line, pen, highlighter, text** and **numbered steps** for
  walkthroughs
- **Redact** to hide sensitive content — **pixelate**, **blur** or a **solid block**.
  Redactions are baked into real pixels, so what is hidden on screen is hidden in the file
- **Select (V)** — the tool the editor starts in. Click a mark to select it, **drag to move**
  it, drag its **handles to resize** (an arrow or line gets a handle at each end), **arrow
  keys** to nudge (Shift for 10px), **Delete** to remove it. Each drag is a single undo step,
  and **Esc** clears the selection before it closes the window
- **Crop**, full **undo/redo** (Ctrl+Z / Ctrl+Y), colour swatches and stroke size
- **Copy** (Ctrl+C), **Save** (Ctrl+S), **Save as…**, or **Pin** the capture on top of every
  window — drag it, scale it with the wheel, Esc to dismiss

Captures are copied to the clipboard as both PNG and DIB so they paste into anything, and are
saved to **Pictures\MicaStats** with a timestamped name. Format, folder, naming, cursor
inclusion, redaction style, a capture **delay** (3/5/10s, for catching menus) and the shortcuts
are all in **Settings → Capture**.

---

## 📝 MicaPad

A notepad built into MicaStats that never loses text and never asks to save. Open it from the
overlay's right-click menu, with **Ctrl+Alt+N** from anywhere, from the Start menu, or with
**Open with → MicaPad** on a text file in Explorer.

### How saving works

* Every note is saved one second after you stop typing, and at least every five seconds while
  you keep typing. The status bar says **Saving…** or **Saved 3s ago**.
* When Windows shuts down or restarts, MicaPad writes everything and lets Windows continue —
  no questions. If MicaPad was open, it reopens with the same tabs after you sign in, with
  **Launch on Startup** on.
* Closing a tab (**Ctrl+W**, middle-click or **×**) never asks either. **Ctrl+Shift+T** reopens
  the last one; the **▾** button lists every closed note, with search, **Reopen** and **Delete**
  (which uses the Recycle Bin). A note that never had any text is simply discarded.

### History

A version is kept each time you pause after at least a minute of changes, and before anything
that replaces the whole text (reload, restore, Replace All, a line-ending change). **Ctrl+Shift+H**
or **History** in the status bar lists them by day. Pick one to preview it, then **Restore**,
**Copy all** or **Back**. Restoring is one step: **Ctrl+Z** undoes it.

Every version from the last day is kept; then one per hour for a week; then one per day up to the
limit in **Settings → MicaPad** (90 days by default). The newest version is always kept.

### Real files

Opening a file (Ctrl+O, or Open with) edits a *copy*: your changes are saved continuously, but the
file on disk changes only when you press **Ctrl+S**. A dot on the tab means "edited since the
file was last saved". The file keeps its encoding and line endings; both are shown, and can be
changed, in the status bar. If a character you typed cannot be stored in the file's encoding,
MicaPad offers **Save as UTF-8** instead of saving question marks.

If another program changes the file, MicaPad reloads it — or, when you have edits of your own,
asks **Reload from disk** or **Keep mine** (a reload first keeps your version in history). If the
file is deleted, **Keep as note** turns the tab into an ordinary note.

### Light or dark

The sun button in MicaPad's tab strip switches MicaPad to a light theme; the moon button switches
it back. Only MicaPad changes — Settings and the rest of MicaStats keep their look. The choice is
remembered, and is also in **Settings → MicaPad → Theme**.

### Right-click menus

Right-click in the text for **Undo**, **Redo**, **Cut**, **Copy**, **Paste**, **Delete**,
**Select all**, **Find**, **Replace** and **Go to line**. The caret moves to where you clicked,
unless you click inside the selection — then the selection stays, so Cut and Copy act on it.
Right-click a history version for **Copy** and **Select all**.

Right-click a tab for **Rename**, **Close** and **Close other tabs** (closed tabs go to Closed notes
as usual — nothing is deleted). A tab that is a real file also has **Copy file path** and
**Show in folder**.

### Markdown

Notes and `.md`/`.txt` files are formatted as you type: `#` headings grow, `**bold**`, `*italic*`,
`~~strike~~` and `` `code` `` look the part, `- ` items get bullets, `- [x]` tasks are crossed off,
`>` quotes get a bar and fenced ```` ``` ```` blocks a shaded background. The markers stay visible
(dimmed) — the file is plain text and never changes. Turn it off with **☰ → Markdown formatting**
or in **Settings → MicaPad**.

Right-click → **Format** wraps the selection in bold, italic, strikethrough, code or a link, or
turns the selected lines into headings, lists, tasks, quotes or a code block — choose bold, italic,
strikethrough, code, a list, a task or a quote again to take it off; a heading item switches the level,
and choosing the level a line already has removes the heading. Each is a single **Ctrl+Z**.

### Colors for code, logs and settings files

JSON, XML, HTML, C#, JavaScript, CSS, PowerShell, Python, SQL, C/C++, Java, PHP, VB, diff, INI,
YAML, batch and log files open colored, in both themes. The language shows in the status bar;
click it to pick another for that tab (or **Auto** to go back to the file type). Notes and
`.md`/`.txt` files are Markdown. Text over 2 MB is shown plain, so huge files stay fast.

JSON, C#, JavaScript, CSS, C/C++, Java, PHP and PowerShell fold at braces, XML and HTML at tags,
and Markdown at headings and code blocks: click the ⊟ box in the margin to fold, ⊞ to open again.
Moving to text inside a fold (Find, Go to line) opens it.

### Editing helpers

**Auto-close**: typing `(`, `[`, `{` or a quote adds the closing one after the caret; typing it
again steps over it, and Backspace right after the opening one removes both. With text selected,
an opening bracket or quote wraps the selection. It stays out of the way in prose — `don't` and
`คำว่า"ใช่"` type normally, and nothing is added in front of a word. Turn it off in **☰** or **Settings → MicaPad**.

**Lines**: **Ctrl+D** duplicates the line (or the selection; with a rectangular **Alt**+drag
selection, every line it touches), **Ctrl+Shift+↑/↓** moves the selected lines, **Ctrl+J** joins
them with one space. Right-click → **Lines** also sorts
(ascending or descending, ignoring case), removes duplicate lines (keeping the first; blank lines
stay) and trims trailing spaces — on the selected lines, or the whole note when nothing is
selected. Line endings are kept, and each is a single **Ctrl+Z**.

**Bookmarks**: **Ctrl+F2** marks the caret line with a dot in the margin (again to remove it);
**F2** / **Shift+F2** jump to the next / previous one, wrapping around. They move with the text as
you edit — moved lines take theirs along, and trimming or **Replace all** leaves them on their
lines — and each tab keeps its own across restarts. **☰ → Clear bookmarks** removes them.

**Occurrences**: select a whole word and every other place it appears (same case, whole words
only) gets a soft box; the status bar counts them (`5 matches`, up to `10,000+`). Words are letters, digits and
underscores, so a word inside unspaced Thai text is only marked where it stands alone (between
spaces or punctuation).

### Links, tabs and view

**Links**: `http://`, `https://` and `mailto:` addresses are underlined; **Ctrl+Click** opens them
in your browser or mail program. Nothing else in a note is ever opened — not files, paths or other
protocols.

**Tabs**: drag a tab to move it; the order is kept after a restart.

**Full screen**: **F11** hides the title bar and fills the screen; **F11** again brings the window back. MicaPad always reopens windowed.

### Where notes live

`%APPDATA%\MicaStats\MicaPad\notes\` — one folder per note, with `current.txt` and a `history`
folder of plain text files. **Settings → MicaPad → Open notes folder** takes you there.

---

## 🤖 AI assistant and MCP

MicaStats measures a lot and used to leave the reading to you. **Ask MicaStats** answers
questions about this PC from MicaStats' own data, and **MCP** lets Claude Desktop or Claude Code
read the same data. Everything is **off until you turn it on** in **Settings → AI**, and nothing
is sent anywhere until you press **Send**, **Explain** or **Test connection**.

### Setting it up

1. **Settings → AI → Ask MicaStats**: turn it on.
2. **Provider**:
   - **Claude** — paste an API key from [console.anthropic.com](https://console.anthropic.com/)
     and press **Save**. The default model, `claude-haiku-4-5`, is quick and cheap;
     `claude-sonnet-5-5` and `claude-opus-5-5` think harder, and any model name works.
     MicaStats talks to Claude only at `api.anthropic.com`, with the key saved here. It ignores
     `ANTHROPIC_BASE_URL`, `ANTHROPIC_AUTH_TOKEN` and `ANTHROPIC_CUSTOM_HEADERS`, which belong to
     Claude Code gateways.
   - **OpenAI-compatible** — a base URL, a model and, if the server needs one, a key. For a model
     on this PC with [Ollama](https://ollama.com/): run `ollama pull llama3.2`, then use base URL
     `http://localhost:11434/v1` and model `llama3.2`, with no key. LM Studio's server is
     `http://localhost:1234/v1`. OpenAI, Azure and OpenRouter work the same way with their URL
     and key.
3. **Test connection** sends one tiny request and shows the reply. It does not count toward the
   daily limit.

A key is stored encrypted for your Windows account (DPAPI) in `%APPDATA%\MicaStats\secrets.bin`,
never in `config.json`, and is never shown again: the box turns into **Saved** and **Remove**.
Settings checks that a save worked: if the key cannot be stored, it says so and keeps the key in
the box so you can try again.
The line under the provider says where questions go — *Everything stays on this PC* for a local
server, otherwise the host name.

### Asking

Open it from **Ask MicaStats…** in the overlay's right-click menu or with **Ctrl+Alt+A** (change
the shortcut in **Settings → AI**). Type a question and press **Enter** (**Shift+Enter** adds a
line):

- "Why was the PC slow ten minutes ago?"
- "What is using my memory right now?"
- "Did the CPU run hotter today than yesterday?" (needs the 7-day history, below)
- "ทำไมเครื่องช้าเมื่อเช้านี้?" — a question in Thai is answered in Thai

The answer streams in. Under it, **Details** lists what MicaStats looked up, with the arguments
(for example `get_top_processes {"by":"cpu","count":5}`), so you can see what the answer rests
on. **Stop** cancels; **New conversation** starts over. If something fails, the question stays in
the box and **Retry** sends it again; a missing key or model offers **Open Settings > AI**.

**Explain buttons** ask for you, in your Windows display language:

- **Diagnostics → Slowdowns**: **Explain** beside each saved slowdown report
- **Alert cards**: **Explain** beside **Show me**
- **Processes**: select one process and press **Explain**

They appear only while the assistant is on.

### Suggestions, never actions

An answer can end with buttons such as **End chrome.exe (PID 1234)**, **Record a slowdown now**,
**Open Diagnostics** or **Open the process list**. An end-process button always names the process
and its PID, whatever the AI suggested calling it. Nothing happens until you click. Ending a
process goes through the same checks as the process window: the PID must still belong to the same
program with the same start time, core Windows processes are refused, and MicaStats never ends
itself. A process that needs administrator rights is left to the process window's **Retry as
administrator**. Buttons from a cleared conversation do nothing.

### Limits and cost

- One **Send** or **Explain** counts as one question, however many lookups it takes. The default
  limit is **100 a day**, reset at local midnight; **Settings → AI** shows today's count
- At most eight rounds of lookups per question and 2,000 output tokens per answer
- A request that receives no data for 60 seconds ends with a timeout message; the provider is
  retried twice before that
- A local model that cannot use tools still answers, in **limited mode**, from a short summary of
  the PC's current state

### Privacy

Sent: readings, hardware model names, process names and their paths. Removed first: your profile
folder (shown as `%USERPROFILE%`), other users' folder names, the computer name and your user name
(when 3 or more characters long), IP and MAC addresses. Never collected by any tool: window titles, command lines, environment
variables. The diagnostics log records failures only — never questions, answers or data.

### 7-day history

**Keep 7 days of history** records one row a minute — CPU, temperatures, memory, GPU, network,
disk, battery, and the busiest process by CPU and by memory — to `%APPDATA%\MicaStats\history\`,
one CSV file per day. After a day the rows are thinned to one every five minutes; after seven
days the file is deleted, about 10 MB at most. **Delete history** removes it all. The assistant
and MCP use it to answer questions about the past.

### MCP for Claude Desktop and Claude Code

MCP lets an AI app you already use read MicaStats' data directly — no key or cost inside
MicaStats, and **read-only**: the same lookups as the assistant, without suggestions. Choose the
connection in **Settings → AI → MCP**.

**Stdio bridge (recommended).** The AI app starts `MicaStats.exe --mcp`, which reads from the
running MicaStats over a private pipe open to your Windows account only. Nothing listens on the
network.

- **Claude Desktop**: press **Copy Claude Desktop config**, then in Claude Desktop open
  **Settings → Developer → Edit Config** and merge it into `claude_desktop_config.json`:

  ```json
  {
    "mcpServers": {
      "micastats": {
        "command": "C:\\Program Files\\MicaStats\\MicaStats.exe",
        "args": ["--mcp"]
      }
    }
  }
  ```

  Restart Claude Desktop and ask, for example, "Use MicaStats: what slowed my PC down today?"
- **Claude Code**: press **Copy Claude Code command** and run it in a terminal:

  ```powershell
  claude mcp add --scope user micastats -- "C:\Program Files\MicaStats\MicaStats.exe" --mcp
  ```

When MicaStats is not running, the bridge still answers from the files on disk (history and
slowdown reports) and says "MicaStats is not running" for live readings.

> **Pick the mode that matches your client's config.** The private pipe runs only while MCP is
> set to **Stdio bridge**. A Claude Desktop or Claude Code config that starts `MicaStats.exe --mcp`
> while MCP is set to **Local HTTP** gets the files on disk only, and every live reading says
> "MicaStats is not running".

**Local HTTP.** While MicaStats runs it serves MCP at `http://127.0.0.1:47831/mcp` (the port can
be changed) — this PC only, and every request needs the token. **Copy Claude Code command** gives
the complete command with your token:

```powershell
claude mcp add --transport http --scope user micastats http://127.0.0.1:47831/mcp --header "Authorization: Bearer <token>"
```

Copying the token or this command keeps it out of Windows clipboard history. **Regenerate**
makes a new token; a client still using the old one is refused until you copy the command again.
If the port is taken, **Settings → AI** says so and the diagnostics log records it.

> The snippets above use the default install folder and port. The **Copy** buttons always produce
> the exact text for your installation — prefer them.

Setting MCP to **Off** stops the pipe and the HTTP server; an AI app that still calls MicaStats is
told "MCP is turned off in MicaStats Settings".

---

## 🔩 Hardware Inspector

The **Hardware** button at the top of the stats panel opens a CPU-Z-style inspector with six tabs:

- **CPU** — name, vendor, socket, family/model/stepping, core topology (with the P/E split on
  hybrid processors), every cache level, base/boost/bus clocks, and the supported instruction
  sets (SSE…AVX-512, AES-NI, VT-x / AMD-V).
- **MAINBOARD** — system, board and BIOS identity, including the SMBIOS version.
- **MEMORY** — total, slots used and configured speed, then one card per module: size, type
  (DDR4/DDR5/LPDDR5…), manufacturer, part number, rated vs configured MT/s, and voltage.
- **GRAPHICS** — every adapter with driver version/date and full video memory (read from the
  driver registry, which is immune to the well-known 4 GB WMI truncation), plus the primary
  display mode.
- **STORAGE** — each physical disk with capacity, bus (NVMe/SATA/USB), kind (SSD/HDD),
  firmware and health.
- **SYSTEM** — Windows edition/version/build, architecture, hypervisor presence, uptime and
  the MicaStats data folder.

The strip on top shows the **live effective core clock** (base clock scaled by the processor
performance counter, so turbo reads above base) and memory load, once per second while the
window is open.

The data comes from the same sources CPU-Z reads where user mode allows: the **CPUID
instruction** executed directly, the **raw SMBIOS/DMI firmware tables**, and the kernel's
processor-topology API. What genuinely requires CPU-Z's kernel driver (MSR core voltage, SPD
timing tables over SMBus) is omitted rather than guessed.

**Save Report** writes the whole inspection to a timestamped text file under
`%APPDATA%\MicaStats\reports\` and reveals it in Explorer — handy for support threads or
comparing machines. **Data Folder** opens `%APPDATA%\MicaStats`, which also holds the
diagnostics log below.

---

## 🎨 Taskbar Colours

The overlay paints directly onto the taskbar rather than onto a plate of its own, so its
colours only work against whatever the taskbar actually is. On a light taskbar the original
white readings measured **1.10:1** contrast — a ratio of 1.0 means the text and the background
are the same colour — so they were effectively invisible.

**Appearance → Taskbar colours** controls this:

- **Match Windows** (default) follows the system light/dark setting and repaints the moment you
  switch it
- **Always light** / **Always dark** pin it regardless

On a light taskbar the readings become dark ink (measured **16:1**) and the cyan graph hue
deepens to a teal that carries on white. A dark taskbar is unchanged, pixel for pixel.

> Any colour you have customised yourself is never overridden — only values still at their
> shipped default follow the theme. MicaStats reads `SystemUsesLightTheme`, which is the
> taskbar's setting; `AppsUseLightTheme` is a separate one and the two frequently disagree.

---

## 🩺 Diagnostics

Windows measures how long your boot took, which app delayed it, and how worn your battery is —
and shows you almost none of it. **Diagnostics** turns those measurements into numbers.

Open it from the **Diagnostics** button on the stats panel, from **Diagnostics…** in the
overlay's right-click menu, or from **Settings → Diagnostics**.

### ⏱️ Slowdowns

Task Manager only ever shows the present instant. By the time a four-second freeze is over, the
process responsible has finished and left nothing behind, which is why these are so rarely
diagnosed. MicaStats keeps a rolling window — five minutes by default — of per-process **CPU,
memory and disk activity**, so the question can still be answered afterwards.

- **Record what just happened** saves the retained window as a timeline report. The same command
  sits in the overlay's right-click menu as **Record Slowdown Now**, which is where your hand
  already is the moment after a stall
- With **Save a report automatically** on, a report is written by itself when the CPU, disk or
  memory stays past its threshold for long enough. A ten-minute cooldown stops one bad afternoon
  producing fifty files, and the thirty newest reports are kept
- Reports land in `%APPDATA%\MicaStats\reports\` as plain text and are listed in the tab

Each report carries a second-by-second timeline and a **worst offenders** summary, so the
culprit is named rather than merely present.

> **Note on cost.** A sample is one kernel snapshot that already carries CPU time, working set
> and disk bytes for every process, so recording adds a single system call every two seconds
> rather than a per-process performance counter read.

> **Note on network.** Per-process network traffic is deliberately missing. Windows exposes
> per-process byte counts only to an administrator, and MicaStats runs unelevated — which is
> exactly why tools that do show it install a service or a driver.

### 🔌 Boot

- **Time to desktop** for the last start, split into core startup and the part after sign-in
- **Trend** across recent boots, so you can tell whether last week's change actually helped
- **What held the last boot up** — every application, driver and service Windows measured as
  delaying startup, with its real duration in seconds
- **Starts with Windows** lists every registered program *and whether it is already switched
  off*, which `Win32_StartupCommand` alone cannot tell you

Clearing a box stops that program launching at sign-in, using the same switch Task Manager
operates. Entries registered for **all users** need administrator rights, so they are shown but
left read-only rather than failing silently — the status line says so if you try.

All of this is read **without administrator rights**.

### 🔋 Battery

Only appears on a portable; on a desktop the tab and the taskbar module hide themselves rather
than showing a row of dashes.

- **Health** against design capacity, with a plain verdict and the cycle count. Windows has no
  battery health readout at all and never warns that a pack is wearing out
- **Right now**: charge, power source, and the actual charge or discharge in **watts**
- **Time remaining** computed from the power being drawn. Windows' own estimate is shown beside
  it for comparison, and frequently reads *Not available* — it returns a placeholder of roughly
  136 years when it does not know, which is why MicaStats does not forward it

Turn on the taskbar module in **Settings → Diagnostics**; its label reads `CHG` while charging.

### 🔔 Alerts

MicaStats has always watched temperature, disk space, memory and GPU load — and never said
anything about them. A drive fills overnight, a cooler clogs and the processor throttles for
weeks, and all of it is visible only in a panel nobody had open at the time.

Alerts appear as a quiet amber card in the corner. They never take focus, up to three stack at
once, and every firing is written to the diagnostics log.

| Rule | Default |
| :--- | :--- |
| CPU temperature | Above 95 °C for 30 s — **on** |
| Free space on a drive | Below 10 GB for 60 s — **on** |
| Memory in use | Above 92 % for 120 s — off |
| Battery health | Below 80 % — **on** |

Two rules keep them trustworthy: a reading must **hold** for the whole sustain window before
anything fires, and a fired rule only re-arms once the reading has recovered past a margin — so
a value hovering on the threshold cannot flicker on and off. A sensor that cannot be read never
fires at all, because a missing temperature probe must not look like a cold processor.

---

## ⬆️ Updates

MicaStats checks GitHub for a newer release **once a day**, a short while after startup, and
tells you with a small card in the corner of the screen. It never opens a modal dialog and never
takes focus — you can ignore it and carry on.

- **Install** from the notification, from **Settings → Updates**, or from **Update to vX.Y.Z…**
  in the overlay's right-click menu
- **Skip this version** stops that particular release being announced again
- Turn the whole thing off with **Check automatically** in **Settings → Updates**; the
  **Check now** button still works whenever you want it

**Every download is verified before it runs.** MicaStats fetches the SHA-256 checksum published
alongside the installer and compares it against the file it downloaded. If the checksum is
missing, unreadable, or does not match, the download is deleted and the update refused — an
updater that runs whatever arrives would be a way into your machine. Downloads are only accepted
from `github.com`.

Installing needs administrator rights, so **Windows will ask for permission**. Declining simply
leaves your current version in place. Setup closes MicaStats and starts it again as part of the
upgrade.

### 🧾 Diagnostics Log

MicaStats appends informative events — startup identity, a one-line hardware summary, sensor
sources that failed, report saves, unexpected errors — to
`%APPDATA%\MicaStats\logs\micastats.log` (plain text, rotated at 512 KB). If something looks
wrong, this file is the first place to look. The MCP stdio bridge (`MicaStats.exe --mcp`) runs as
a separate process and keeps its own `mcp-bridge.log` in the same folder.

---

### 🖱️ Overlay Controls
- **Click**: Opens the stats panel.
- **Drag & Move**: Press and drag the overlay to reposition it. A click that does not move opens
  the panel instead, so both gestures share the same button. *(Dragging requires **Lock Position**
  OFF.)*
- **Snap to Taskbar**: When enabled, the overlay snaps to the taskbar area. Disable this to **free-float** the overlay anywhere on your screen.
- **Toggle Lock**: Right-click the overlay and select **Lock Position** to prevent any accidental movement.
- **Settings**: Right-click to quickly jump into the dashboard.
- **Show Desktop**: Right-click and choose **Show Desktop** to minimise every window; choose it
  again to bring them all back — the same toggle as the corner of the Windows taskbar.
- **Capture**: The right-click menu also carries Capture Region / Window / Screen / All Screens.
  Choosing one waits for the menu to leave the screen before any pixels are taken — a menu is
  logically closed the moment you click it, but the area underneath takes about a quarter of a
  second to be redrawn, and capturing sooner puts the menu in your screenshot.
- **Diagnostics…**: Opens the Diagnostics window — slowdowns, boot, battery and alerts.
- **Record Slowdown Now**: Saves the last few minutes of per-process activity to a report. Use
  it immediately after the machine stutters, while the rolling window still holds what happened.
- **Ask MicaStats…**: Opens the AI assistant. Shown only while it is on in **Settings → AI**.

---

## 🏠 Home Dashboard

The Home dashboard is your high-level control center. It features four primary quick-links:
1. **General**: Configure startup behavior and app lifecycle.
2. **Monitoring**: Select which hardware sensors to track.
3. **Appearance**: Customize font, colors, and styling.
4. **About**: View version history and developer links.

---

## ⚙️ Core Configuration

Open the **Settings Window** to customize your experience:

### 🚀 General Settings
- **Hardware Overlay**: Toggle the entire overlay on or off.
- **Snap to Taskbar**: Enable to snap to the taskbar; disable to **unlock** it so you can position the overlay anywhere on your desktop.
- **Launch on Startup**: Enable this to start monitoring automatically when you log in to Windows.
- **Lock Position**: Lock the overlay in its current location.
- **Hide in Full Screen**: Automatically hides the overlay when a full-screen application or game is active to prevent distractions.
- **Keep on Top**: Forces the overlay to stay above all other windows.
- **Refresh Rate**: Customize how often the sensors update (from 500ms for high precision to 5s for ultra-low overhead).

### 📊 Monitoring & Sensors
- **Sensor Selection**: Choose which metrics you want to see (CPU, RAM, NET, GPU, DISK).
- **Network Adapter**: If you have multiple network cards (Wi-Fi, Ethernet, VPN), pick the one you want to track.
- **Multi-Disk Selection**: In v3.0, you can select multiple drives simultaneously. The overlay will dynamically adjust to show activity (C:DK, D:DK, etc.) for each drive you select. Pick up to 9 drives for a balanced 3x3 layout.

### 🎨 Appearance & Design
- **Accent Color**: Pick a color that matches your Windows theme.
- **Font Selection**: Choose from high-legibility fonts (Segoe UI, Outfit, Inter). On
  Windows 11 the default renders with **Segoe UI Variable** — Text for values, Small for the
  tiny labels — the face designed for exactly these sizes.
- **Design Mode**: Toggle between **Standard** and **Compact** modes for different levels of detail.

---

## ❓ Troubleshooting

**Q: The overlay is missing!**  
A: Go to **Monitoring** settings and ensure at least one sensor is toggled **ON**. On a
multi-monitor setup the overlay can also end up off-screen if its saved position falls in the
gap between mismatched displays — MicaStats now detects this and snaps the overlay back onto
the taskbar automatically at startup (and whenever your displays change); the recovery is
recorded in the diagnostics log.

**Q: Why doesn't the app start with Windows?**  
A: Ensure "Launch on Windows Startup" is enabled in **General** settings. This registers the app in your user registry for a seamless boot experience.

**Q: Windows says "Unknown Publisher" or "SmartScreen" prevents it from running.**  
A: This happens because the app is a local, independent release. Click **"More Info"** and then **"Run Anyway"**. v3.0 is a lightweight, zero-bloat build with no telemetry or external tracking.

**Q: My network speed shows 0 KB/s.**  
A: In **Monitoring** settings, select the correct active Network Adapter from the dropdown menu.

**Q: The CPU temperature shows a dash.**  
A: That means no source can supply it, and it is the expected state on a clean machine. The
CPU die sensors (AMD Tctl, Intel DTS) are reachable only from kernel mode, so every tool that
shows them installs a kernel driver. MicaStats does not — it runs unelevated and installs
nothing — so it reads what another tool has already published.

Run any one of **Core Temp**, **HWiNFO**, **MSI Afterburner**, **AIDA64**,
**LibreHardwareMonitor** or **OpenHardwareMonitor** and the reading fills in on its own within
a few seconds; there is nothing to configure in MicaStats. Two of them need a setting of their
own: HWiNFO requires *Shared Memory Support* to be enabled, and on current free builds that is
time-limited per session, so the reading can stop after a while and return when HWiNFO is
restarted. AIDA64 requires shared memory to be switched on in its preferences.

A **SENSORS** block inside the CPU card shows everything that *is* readable without any of
that — the ACPI thermal zone and whether the firmware is limiting the processor. The GPU card
carries its own, listing every adapter's temperature and power draw. Each reading sits beside
the load that produced it; hover any row for its source.

**Q: End task says access denied.**  
A: MicaStats runs without administrator rights, so it cannot end a process that has more of
them. When that happens a **Retry as administrator** button appears: it asks for consent once,
ends that single process, and exits. MicaStats itself never keeps those rights — the consent
covers one termination, not the session.

If it still fails when elevated, Windows is protecting the process. Anti-malware services and
some system components are marked protected and cannot be ended by anything at all.

**Q: Why will it not end csrss.exe?**  
A: Because that stops Windows. `csrss.exe`, `wininit.exe`, `services.exe`, `smss.exe`,
`lsass.exe` and `winlogon.exe` are load-bearing — terminating any of them produces an immediate
stop error, not a recoverable failure. Windows Task Manager asks you to confirm; MicaStats
refuses outright, because a confirmation dialog is one mis-click away from a blue screen and
this list is re-sorting itself while you read it.

**Q: The CPU column shows dashes when I first open the process list.**  
A: CPU share is the difference between two samples, so it does not exist until the second one
arrives — about two seconds. A dash is shown rather than 0.0%, because a screen full of zeroes
would be indistinguishable from the frozen list this window exists to replace. If the stats
panel is already open the list is fully populated the moment it appears.

**Q: Why is the "System" temperature different from my CPU temperature?**  
A: Because it is not the CPU. It is the ACPI thermal zone, which sits downstream of the fan
control loop and reports how the cooling system is responding. Under a sustained load it can
even fall while the processor heats up, as the fans ramp. It is shown because it is real and
it is what the cooling system reacts to — not as a stand-in for the die.

---

## 🌐 Community & Support

Built with ❤️ by **Chaiyaporn Suratemeekul (manoi-bms)**, with Claude Code. MicaStats is a
fork of [kil0bit System Monitor](https://github.com/kil0bit-kb/kil0bit-system-monitor) (MIT)
by KB - kil0bit; the UX/UI is modeled on iStat Menus for macOS.
For feedback, bug reports, or feature requests, visit the [GitHub Repository](https://github.com/manoi-bms/MicaStats).
