# iStat Menus-style Taskbar UI — Design

**Date:** 2026-08-17
**Status:** Approved design, staged implementation
**Scope:** Live sparklines in the taskbar overlay + a click-to-open detail panel

---

## 1. Goal

Bring the two halves of iStat Menus to `kil0bit-system-monitor`:

1. **Inline graphs** — the taskbar overlay renders a live sparkline per metric alongside the
   numeric value, instead of text only.
2. **A detail panel** — clicking the overlay opens an anchored dropdown containing 60s history
   graphs, top processes, per-logical-core CPU bars, and a system-info header.

Non-goals: per-section dropdowns (one unified panel), fan speeds, alerts/notifications,
historical persistence across restarts.

---

## 2. Architecture

`MetricsHistory` becomes the fan-out point. Both windows subscribe to it, not to
`TelemetryService` directly.

```
TelemetryService            (timer thread, self-rescheduling, 1 Hz default)
  CPU · RAM · GPU · temp · net · disks · per-core
        │ MetricsUpdated(SystemMetrics)          [timer thread]
        ▼
MetricsHistory
  · marshals ONCE to the UI dispatcher
  · appends each series to a fixed-capacity ring
  · raises Updated                                [UI thread]
        ├──────────────────────────┬
        ▼                          ▼
OverlayWindow              StatsPanelWindow
  GDI+ sparklines            WPF, live only while open
                                   │ Enabled = IsOpen && overlay visible
                                   ▼
                             ProcessSampler
```

### Why route through `MetricsHistory`

`OverlayWindow` currently owns the thread hop (`_dispatcher.BeginInvoke`,
`OverlayWindow.cs:159`). If the panel also subscribed to `TelemetryService`, the history buffer
would be written on the timer thread and read on the UI thread, requiring locks and inviting the
same class of cross-thread problem that already exists around `_diskCounters`/`_gpuCounters`.

Hopping once, inside `MetricsHistory`, makes every downstream consumer UI-thread-only.
**No locks exist anywhere in the new code.**

### Ring buffer

120 samples, fixed capacity, no allocation after warm-up. At the 1000 ms default that is two
minutes; at the 500 ms setting, one minute. The panel draws the most recent 60 samples, the
taskbar sparkline ~13–30 (see §6.4).

**Partial history is right-aligned, never zero-padded.** Zero-padding would render a fake cliff
climbing out of zero for the first minute after launch.

---

## 3. Components

### 3.1 `Services/MetricsHistory.cs`

```csharp
public sealed class MetricsHistory : IDisposable
{
    public MetricsHistory(TelemetryService telemetry, Dispatcher ui, int capacity = 120);
    public SystemMetrics Latest { get; }
    public event Action? Updated;                  // always on the UI thread
    public Series Cpu { get; }
    public Series Ram { get; }
    public Series Gpu { get; }
    public Series Temp { get; }
    public Series NetUp { get; }
    public Series NetDown { get; }
    public Series Disk(string instanceName);       // created on demand, evicted when unselected
    public IReadOnlyList<Series> Cores { get; }
}
```

`Series` exposes `Count`, `Availability`, an oldest→newest indexer, and a rolling `Peak` with
decay for autoscaled series. No Win32, no WPF — fully unit-testable.

**Disk series are keyed by PerformanceCounter instance name, which changes when drives are
added or removed.** The dictionary must evict keys absent from the current selection or it leaks.

### 3.2 Availability tri-state

Every series carries `Unavailable | NoDataYet | Value`.

A graph has no "N/A". Today a missing GPU temperature renders the string `"N/A"`
(`OverlayWindow.cs:578`); a flat line at zero would instead assert *"GPU is idle"* when the truth
is *"unreadable"*. `Unavailable` renders as a dashed baseline plus the `N/A` glyph, never as data.

### 3.3 `Helpers/SparklineGeometry.cs`

```csharp
public static class SparklineGeometry
{
    // Bucket-max downsample + project into a w×h box. max<=0 ⇒ autoscale to Peak.
    public static int Project(Series s, float w, float h, float max, Span<PointF> into);
    public static int Bars(Series s, float w, float h, float max, int barW, int gap,
                           Span<RectangleF> into);
}
```

The single geometry source shared by the GDI+ taskbar renderer and the WPF panel, so a CPU curve
is identical in both. Pure function, no state.

Scaling per series: fixed 0–100 for CPU/RAM/GPU/disk; a clamped 30–95 °C window for temperature;
for network a decaying peak with a floor, sqrt-compressed, **shared between UP and DN** so the two
are visually comparable.

### 3.4 `Services/ProcessSampler.cs`

```csharp
public sealed class ProcessSampler : IDisposable
{
    public bool Enabled { get; set; }              // panel open AND overlay visible
    public IReadOnlyList<ProcessUsage> TopByCpu { get; }
    public IReadOnlyList<ProcessUsage> TopByRam { get; }
    public event Action? Updated;
}
public sealed record ProcessUsage(string Name, int Pid, float CpuPercent, long WorkingSet);
```

Disabling stops the timer *and* drops the retained snapshot, so a closed panel costs nothing.

**Implementation is `NtQuerySystemInformation(SystemProcessInformation)`, not
`Process.GetProcesses()`.** See §5.4 — the `Process` route is not merely slower, it returns
*wrong data*.

Rules:
- Divide by `GetActiveProcessorCount(ALL_PROCESSOR_GROUPS)`, **never** `Environment.ProcessorCount`
  (which respects CPU affinity and inflates every value).
- Rank on **CycleTime** deltas; use Kernel+User only for the displayed percentage.
- Key the previous-sample dictionary on **PID + CreateTime** to survive PID reuse.
- Clamp negative deltas. Keep PID 4 (`System` measured as a top-3 consumer); exclude only PID 0.
- Grow the buffer on `STATUS_INFO_LENGTH_MISMATCH`; buffer size scales with *thread* count
  (~1.5 MB for ~840 processes / ~12k threads).
- The csproj has **no `AllowUnsafeBlocks`**, so walk the buffer with `Marshal.ReadInt64`/
  `ReadIntPtr` at fixed offsets — a `fixed`-byte struct will not compile.
- Per-process values sum to only 29–98 % of the system gauge depending on load. Label the column
  honestly; do not claim it accounts for all CPU.
- Group by immediate parent **only when the parent shares the image name** (Chrome/Edge
  renderers). Unbounded ancestry walks collapse everything under `explorer.exe`/`wininit.exe`.

### 3.5 `ViewModels/StatsPanelViewModel.cs`

Exposes `IsLive` (false ⇒ no recompute at all), `SystemInfo`, `Uptime`, `Sections`
(CPU/Memory/GPU/Network/Disk), `Cores`, `TopProcesses`. Rebuilt on `MetricsHistory.Updated`
only while live.

`Cores` as an `ObservableCollection` in a `UniformGrid` is why WPF was chosen — 6 cores or 64
needs no layout math.

### 3.6 `StatsPanelWindow.xaml` / `.cs`

- `AllowsTransparency=false`, `WindowStyle=None` + `WindowChrome`, `ShowInTaskbar=false`,
  `ResizeMode=NoResize`, `SizeToContent=Height`.
- Rounding + translucency + shadow from DWM: `DWMWA_WINDOW_CORNER_PREFERENCE` =
  `DWMWCP_ROUNDSMALL`, `DWMWA_SYSTEMBACKDROP_TYPE` = `DWMSBT_TRANSIENTWINDOW`. Both constants
  already exist unused at `Win32Helper.cs:203-217`.
- **Owner = the overlay's HWND. `Topmost` is never set.** See §5.2.
- Positioning: `WindowInteropHelper.EnsureHandle()` then `SetWindowPos` in **physical pixels**.
  Never `Left`/`Top`. See §5.3.
- Flip rule reused from the existing native menu (`OverlayWindow.cs:717-729`): bottom half of the
  work area opens upward, otherwise downward.
- Dismissal: `Deactivated` + `Esc`, with a ~250 ms reopen guard and an `_isModalOpen` flag so a
  `ColorDialog` (`SettingsWindow.xaml.cs:344`) cannot close the panel mid-pick.
- **Never a fullscreen click-away scrim.** See §5.5.

---

## 4. Changes to existing code

### 4.1 `OverlayWindow.cs` — three prerequisite fixes (own commit)

| Bug | Fix |
|---|---|
| Zero-width first paint | `GetCachedMeasure` gets its own 1×1 measuring `Graphics` built in the ctor instead of using `_offscreenGraphics` |
| Positional section colours | `PrepareMetricsData` returns a `SectionKind` per column; brush selection switches on it, not on `i` |
| Measure/draw mismatch | `DrawString` uses `StringFormat.GenericTypographic`, matching what `GetCachedMeasure` measures |

### 4.2 `OverlayWindow.cs` — feature work

- `MetricItem` gains `Series? History` and `float GraphSlot`; `GetItemWidth` becomes
  `label + gap + GraphSlot + gap + valW`.
- **`GraphSlot` derives from `font.Height`, not `scale`.** `scale` includes `_dpiScale` but
  `textScale` does not, and the bitmap `Graphics` is 96 dpi — a scale-derived slot desynchronises
  from the glyphs at 150 %.
- Draw **no-antialias bars**, not a polyline (§6.4).
- Gesture state machine (§4.5).
- `public IntPtr Handle => _hWnd;` — zeroed in `Dispose` before exposure.
- Own-process exemption in `ShouldShowOverlay` (for the fullscreen heuristic only, *not* z-order).
- `ShowOwnedPopups(_hWnd, false)` in the fade-to-zero path.
- History is appended **only** in the `MetricsUpdated` handler, never in `UpdateLayer` — which
  also fires on hover, mouse-leave, DPI change and every config `PropertyChanged`.

### 4.3 `TelemetryService.cs`

- Per-core: **one** `PerformanceCounterCategory("Processor Information").ReadCategory()` per tick
  plus `CounterSampleCalculator.ComputeCounterValue`. Filter `^\d+,\d+$`, sort numerically on both
  fields, label by sorted ordinal.
- Take `_Total` from that same snapshot and **delete `_cpuCounter`**, removing today's
  first-frame zero at line 430.
- Fix pre-existing re-entrancy: `_timer.Start()` (line 170) currently precedes the synchronous
  `UpdateMetrics()` (line 173), so a slow first pass lets a threadpool tick read the same counter
  concurrently. Swap them.
- `SystemInfo` from registry `ProcessorNameString` + the already-present `GlobalMemoryStatusEx` +
  `RtlGetVersion` + `GetTickCount64`. **No WMI** — it must not sit on the panel-open path.

### 4.4 `AppConfig` / `ConfigService.cs` — prerequisite

44 properties, zero equality guards, a full-file rewrite per notification, and `LoadConfig`'s
silent `catch{}` means one torn write **factory-resets every setting with no warning**.

- A `Set<T>` equality-guard helper across all setters.
- Debounced, atomic writes (temp file + `File.Replace`).
- A `ConfigVersion` field.
- Rename to `config.json.corrupt` on parse failure instead of silent reset.

New config surface is deliberately small: **`ShowGraphs` (bool)** and `GraphHistorySeconds`.
Graph mode reuses `ShowPods`, `PodColorHex` and `ColumnSpacing`.

`ShowGraphs` is a **separate bool, not a third `DisplayStyle` value.** `DisplayStyle` is really a
*label-width* axis — its only reader (`OverlayWindow.cs:562`) just picks `"CPU"` vs `"C"` — so a
`"Graph"` value would collide with it and make compact-labels-plus-graphs unexpressible.

### 4.5 Gesture: tap vs drag

`SendMessage(WM_NCLBUTTONDOWN, HTCAPTION)` enters `DefWindowProc`'s modal move loop, which runs
its own pump and **consumes the terminating `WM_LBUTTONUP`**. Tap detection therefore cannot hang
off mouse-up in the current design. Confirmed: nothing in the repo references `WM_LBUTTONUP`, yet
dragging works, and position persistence hangs off `WM_EXITSIZEMOVE` precisely because no
button-up is available.

State machine — enter the native drag only *after* movement:

```
WM_LBUTTONDOWN / WM_LBUTTONDBLCLK
    anchor = GetCursorPos(); SetCapture(hWnd); _pressPending = true; return 0

WM_MOUSEMOVE
    if (!_pressPending) break
    if ((wParam & MK_LBUTTON) == 0) break            // anti-phantom-drag
    if (|delta| > SM_CXDRAG/SM_CYDRAG && !LockPosition)
        _pressPending = false                        // order matters: before ReleaseCapture
        ReleaseCapture(); SendMessage(WM_NCLBUTTONDOWN, HTCAPTION, IntPtr.Zero)

WM_LBUTTONUP        → if pending: ReleaseCapture(); toggle panel
WM_CAPTURECHANGED   → clear flag; do NOT return 0
WM_CANCELMODE       → clear flag; fall through to DefWindowProc (it releases capture)
```

- Thresholds from `GetSystemMetricsForDpi(SM_CXDRAG/SM_CYDRAG, _currentDpi)` — plain
  `GetSystemMetrics` is documented as not DPI-aware and this process is PerMonitorV2. Clamp a 0
  return to 4.
- `LockPosition` still blocks dragging; a tap still opens the panel.
- `WM_LBUTTONDBLCLK` routes through the same press path (`CS_DBLCLKS` means the second press
  arrives as DBLCLK, not DOWN). The old double-click→Task Manager binding is removed; Task Manager
  remains in the right-click menu.
- Side benefit: a tap never enters the move loop, so `WM_EXITSIZEMOVE` never fires for it —
  **fixing the pre-existing bug where every click writes `config.json`.**

---

## 5. Verified constraints

Every item below was empirically established; several overturned the initial design.

### 5.1 DWM rounding is incompatible with per-pixel alpha

Microsoft: apps using per-pixel alpha layering "cannot ever be rounded, even if they call the
opt-in API." Hence `AllowsTransparency=false` + DWM backdrop, which also buys a free system
shadow. (`AllowsTransparency` does **not** disable hardware acceleration — that is an XP-era
artifact.)

ModernWpf 0.9.6 ships **no real acrylic**: every `Acrylic*` key is a plain `SolidColorBrush` and
no `AcrylicBrush` type exists in the assembly. Translucency must come from DWM.

### 5.2 Ownership, not `Topmost`, controls z-order

`Shell_TrayWnd` itself carries `WS_EX_TOPMOST`, so "topmost" is a shared band. Measured: after one
`SetWindowPos(HWND_TOPMOST)` from `EnforceZOrder`, the overlay moved from z=10 to z=7 while the
**focused** panel fell to z=17 — silently, one-way.

Worse, with `StickToTaskbar` on (the default), taskbar ownership alone re-raises the overlay above
an unowned panel within ~300 ms with **zero** re-asserts. Pausing the timer does not fix this.

`WindowInteropHelper.Owner = overlayHwnd` inherits `WS_EX_TOPMOST` automatically and held the
panel above through 5 consecutive re-asserts. `Topmost` must stay unset — WPF's own re-assertion
is what would create a visible fight.

Also noted: the `GW_HWNDPREV` "smart check" at line 244 is vacuous — it was non-zero immediately
after a topmost call, so the re-assert fires every tick.

Caveat: `DestroyWindow(owner)` destroys owned windows, and the overlay is owned by `Shell_TrayWnd`
cross-process, so an Explorer restart can take both. There is no `TaskbarCreated` handling today.

### 5.3 Positioning must be physical-pixel

`Window.Left`/`Top` are logical units; after `Show`, the setter multiplies by the **current**
monitor's scale. `TransformFromDevice` yields the *panel's* scale, not the *target's*, so it is the
wrong tool for cross-monitor anchoring. WPF's own DIP space is per-monitor-normalised, producing
unmappable gaps and ambiguous overlaps under mixed DPI (dotnet/wpf #3105, #4127 — both open).

Therefore: `EnsureHandle()` → compute everything physical from `GetWindowRect(overlay)` +
`GetDpiForWindow(overlay)` + `GetMonitorInfo` work area → `SetWindowPos` → `Show()`. Re-run on
`DpiChanged`, `SizeChanged`, and whenever the overlay moves; a cross-monitor `SetWindowPos`
triggers `WM_DPICHANGED` and WPF applies the OS suggested rect, changing physical size, so a
second pass realigns edges.

`ContextMenuWindow.xaml.cs:30-33` contains exactly this defect today (dead code — delete it).

### 5.4 `Process.TotalProcessorTime` returns wrong data unelevated

The app manifest is `asInvoker`. Two independent measurements on ~847 processes:

| | Result |
|---|---|
| Access-denied processes | 244–246 of ~847 |
| Share of CPU activity hidden | **58–64 %** |
| Denied top consumers | `vmmemWSL`, `svchost`, `dwm`, `System` — all of them |

A top-processes list built this way omits the actual answer. `NtQuerySystemInformation` has no
access check and already carries the timings .NET discards as `Reserved1`.

Secondary: `Process.GetProcesses()` costs ~13–15 ms and 1.3 MB per call; `Refresh()` on cached
objects costs ~14 ms *per object*. Per-process `PerformanceCounter.NextValue()` costs ~88 ms each
(20 counters ⇒ ~1.7 s/tick) and its instance names collide (`node#105` of 137).

### 5.5 A same-process fullscreen window trips our own appbar

A borderless fullscreen window from *our* process made the shell send `ABN_FULLSCREENAPP`
(wParam=2, lParam=1) to our own registered appbar — with our window not even foreground. That sets
`_shellFullscreen` and returns false at line 335, **before** the guard at line 344. So exempting
our process from the foreground check is insufficient; the fix is to never make the panel
fullscreen (no scrim) and to guard `_shellFullscreen` while the panel is open.

### 5.6 Per-core counter details

- The instance pair `"0,3"` is **(NUMA node, index-within-node)** per the live perflib help text —
  not (group, index). On multi-NUMA hardware the second field restarts per node, so it cannot be a
  core label. Label by sorted ordinal.
- Filter must be `^\d+,\d+$`. The category exposes **both** `_Total` and `0,_Total`; the
  `!= "_Total"` idiom already used at `TelemetryService.cs:52` would keep `0,_Total` and
  double-count.
- `GetInstanceNames()` order is scrambled (`0,19 | 0,5 | 0,7 …`) — sort numerically.
- Use `% Processor Time`. `% Processor Utility` measured **118–124 %** at ~50 % real load and
  would peg a 0–100 bar. (Windows 11 24H2+ Task Manager also switched to `% Processor Time`.)
- `InstanceDataCollection[name]` returns **null** for an unknown instance — null-check and
  re-enumerate on miss, or CPU hot-add NREs.
- One `ReadCategory()` costs ~1.0–1.4 ms for 24 cores vs ~6.5–6.8 ms for 24 individual counters.
- Perflib retries 17× with 10 ms exponential backoff; an 8,962 ms stall was measured. **This must
  never run on the UI thread.**
- A fresh counter's first read is always exactly 0.

### 5.7 Layered hit-testing is per-pixel

Zero-alpha regions pass mouse messages through regardless of the `HTCLIENT` return at line 675.
With `ShowBackground` defaulting to false, only pods (alpha `0x0F`) and glyphs capture clicks.

---

## 6. Cost and identity

The project markets itself as ultra-lightweight with near-zero CPU.

1. **Sparklines + `MetricsHistory` are nearly free** — 120 samples × ~8 series × 4 B ≈ 4 KB, and
   bars cost ~2.4 µs against a repaint that already happens every tick.
2. **Per-core adds ~1 ms/tick** via the single-`ReadCategory` path.
3. **`ProcessSampler` adds ~12 ms per sample, but only while the panel is open.**
4. **The real cost is footprint, not CPU.** With `--startup`, today's typical session constructs
   no real WPF window at all (only the 0×0 dummy). A click-to-open panel makes
   `PresentationFramework` the normal path: expect **+150–400 ms first-open latency and
   +25–60 MB resident that never returns.** This is the honest price of the feature.

Note the README's "~2.71 MB" is already not reproducible from this tree — `publish_output/` is
5.1 MB and `publish_self_contained/` is 76 MB.

### 6.4 Sparkline legibility

Measured in a 40×12 px slot: a 1 px antialiased polyline is only **17 % solid ink with 46 % of
pixels below alpha 90** — it reads as haze. A 2 px pen reaches 49 %. **No-antialias bars are
100 % solid and cheapest** (2.4 µs vs 12.0 µs). 30 points in 40 px is 1.33 px/sample, resolving
23 of 40 columns; ~13 bars of 2 px + 1 px gap is the legible configuration.

`Clear(Transparent)` tints antialiased edges toward black, so AA text/lines look muddy against
light backgrounds. `SmoothingMode` must be restored after drawing bars — the `Graphics` persists
across frames.

Premultiplication was verified: managed `ARGB(128,255,255,255)` becomes DIB `BGRA(128,128,128,128)`,
so `GetHbitmap(Color.FromArgb(0))` premultiplies and `AlphaFormat = AC_SRC_ALPHA` is correct.

---

## 7. Error handling

- Series that cannot be read render as `Unavailable` (dashed baseline + `N/A`), never as zero.
- Per-core reads stay on the timer thread because of the perflib backoff tail.
- `NtQuerySystemInformation` → grow and retry on `STATUS_INFO_LENGTH_MISMATCH`, capped.
- Corrupt config is preserved as `.corrupt`, not silently reset.
- An exception in the panel's recompute must not stop the telemetry timer.
- Panel visibility is a strict subset of overlay visibility: when the overlay fades to zero, the
  panel closes and the sampler stops.

## 8. Testing

No test project exists today (4,168 lines, 17 files, zero tests), which makes the project's central
performance claim unfalsifiable. Adding `tests/Kil0bitSystemMonitor.Tests` (xunit):

- `MetricsHistory` — ring wrap, capacity, oldest→newest order, right-aligned partial history,
  peak decay, disk-series eviction
- `SparklineGeometry` — projection, autoscale, bucket-max, empty / single-sample / all-zero / NaN
- `ProcessSampler` delta math — PID reuse caught by CreateTime, negative clamp, divisor
- Per-core instance filter — fixture including `_Total`, `0,_Total`, `1,0` (multi-NUMA)
- `AppConfig` — setting an identical value raises no `PropertyChanged`

Plus a recorded perf baseline (idle CPU %, private working set, publish size) before stage 1.

## 9. Build order

| Stage | Work |
|---|---|
| 0 | Perf baseline; `AppConfig` equality guards; atomic/debounced config writes |
| 1 | The three `OverlayWindow` prerequisite bug fixes |
| 2 | `MetricsHistory` + `SparklineGeometry`, headless, fully tested |
| 3 | `ShowGraphs` mode in the overlay |
| 4 | Panel shell + system-info header + percentage charts |
| 5 | Network/disk charts (autoscaling, dynamic series eviction) |
| 6 | Top processes |
| 7 | Per-core bars |
| 8 | Docs — README/GUIDE vocabulary is already inconsistent ("Detailed" vs "Standard" vs "Text") |

Risky-but-cheap prerequisites land first; the two most expensive and least certain pieces
(processes, cores) land last where they can be cut without stranding anything.

## 10. Open items

- Explorer-restart resilience (`TaskbarCreated`) is a pre-existing gap that ownership makes more
  visible. Out of scope here; worth a follow-up.
- `ContextMenuWindow` is dead code containing the DPI defect described in §5.3 — delete it.
