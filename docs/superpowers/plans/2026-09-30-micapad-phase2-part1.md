# MicaPad phase 2, Part 1: theme switch and right-click menus — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** MicaPad gets its own light/dark theme (a sun/moon button and a Settings choice, remembered in `AppConfig.PadTheme`) and right-click menus on the editor, the history preview and the tabs.

**Architecture:** Every MicaPad color moves into `PadPalette` (pure, in `Services/Pad`, two instances: Dark and Light). `Pad/PadThemeApplier` turns a palette into frozen WPF brushes stored under `Pad.*` keys in the MicaPad window's resources; all MicaPad XAML reads those keys with `DynamicResource`, so a switch repaints live. Menus are built in code by `MicaPadWindow`, like its existing `☰` menu.

**Tech Stack:** .NET 8 WPF (`net8.0-windows`), ModernWpfUI, AvalonEdit 6.3.1.120, xUnit.

**Spec:** `docs/superpowers/specs/2026-09-30-micapad-phase2-design.md` — Part 1 (sections 1.1–1.3), plus the `PadTheme` row of *Settings and storage* and the Part 1 testing and manual items.

## Global Constraints

- **Only MicaPad changes.** Settings, Ask MicaStats, the overlay, the panels and toasts keep their current look.
- `AppConfig.PadTheme`: `"Dark"` (default, today's look) or `"Light"`. Any other value reads as `"Dark"`.
- Nothing in MicaPad keeps a literal color: `Pad/MicaPadWindow.xaml`, `Pad/FindReplaceBar.xaml` and `Pad/HistoryPane.xaml` contain no `"#RRGGBB"` / `"#AARRGGBB"` attribute values after this plan.
- Contrast rules, for both palettes (WCAG relative luminance): body text on background ≥ 7:1; muted text, link text and accent text ≥ 4.5:1; dimmed markers and line numbers ≥ 3:1.
- New code under `Services/Pad/` holds no WPF types.
- Tests never touch the real `%APPDATA%`, never launch MicaStats, never start Explorer, and never use the network. Temp folders only (`PadTestEnv`).
- Build and test only with the user-local SDK: `DOTNET_CLI_TELEMETRY_OPTOUT=1 "$LOCALAPPDATA/Microsoft/dotnet/dotnet.exe"`. Never build or publish into `bin\Release` (the controller deploys after the plan).
- Number and date text uses `CultureInfo.InvariantCulture` (the machine runs the Thai culture).
- Commits: `git add` the exact paths (never `-A`), message with `-m` (no heredoc), never amend, and end the message with a `Co-Authored-By:` trailer naming the model that wrote the commit, e.g. `-m "Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>"`.

## Review Focus

1. **Switching the theme while the find bar, a history preview or a popup is showing** — every one of them repaints at once, not at the next open (Task 2: `A_switch_repaints_the_find_bar_and_the_preview_too`).
2. **A hand-edited or older `config.json`** with `"PadTheme": "light"`, `"LIGHT"`, `"blue"` or no `PadTheme` at all — lower/upper case Light reads as Light, anything else as Dark, and nothing throws (Task 1: `PadConfigTests` additions).
3. **Right-click inside an existing selection** — the selection must stay so Cut/Copy act on it; right-click elsewhere moves the caret and drops the selection (Task 3: `A_right_click_inside_the_selection_keeps_it`, `A_right_click_outside_the_selection_moves_the_caret`).
4. **Show in folder on a tab whose file was deleted or moved** — MicaPad says the file is gone instead of opening some unrelated Explorer folder (Task 4: `Show_in_folder_on_a_missing_file_says_so`).
5. **Close other tabs when some of them hold unsaved edits of real files** — nothing is lost: each goes to Closed notes with its edits and reopens with them (Task 4: `Close_other_tabs_keeps_every_note_recoverable`).

## File structure

| File | Task | Responsibility |
|---|---|---|
| `Services/Pad/PadColor.cs` (new) | 1 | One ARGB color: parse, print, composite, WCAG luminance and contrast |
| `Services/Pad/PadPalette.cs` (new) | 1 | `PadThemes` (names, normalizing) and `PadPalette` (the two color sets, their resource keys) |
| `Models/SystemMetrics.cs` | 1 | `AppConfig.PadTheme` |
| `Pad/PadThemeApplier.cs` (new) | 2 | Palette → frozen brushes in a `ResourceDictionary`; title bar dark/light |
| `Pad/MicaPadWindow.xaml` | 2, 4 | Colors become `DynamicResource Pad.*`; theme button; tab right-click hook |
| `Pad/MicaPadWindow.xaml.cs` | 2, 3, 4 | `ApplyTheme`, theme button, menus |
| `Pad/FindReplaceBar.xaml`, `.xaml.cs` | 2 | Colors from `Pad.*`; `ApplyPalette` for the match fill |
| `Pad/HistoryPane.xaml` | 2 | Colors from `Pad.*` |
| `Pad/MatchHighlighter.cs` | 2 | `Fill` becomes a settable property |
| `SettingsWindow.xaml`, `.xaml.cs` | 2 | Theme choice in Settings → MicaPad |
| `GUIDE.md`, `README.md` | 2, 4 | User docs |
| `tests/Kil0bitSystemMonitor.Tests/PadPaletteTests.cs` (new) | 1 | Colors, contrast, completeness, names |
| `tests/Kil0bitSystemMonitor.Tests/PadConfigTests.cs` | 1 | `PadTheme` default, round trip, normalizing |
| `tests/Kil0bitSystemMonitor.Tests/PadThemeTests.cs` (new) | 2 | No literal colors; keys exist; live switching |
| `tests/Kil0bitSystemMonitor.Tests/PadMenuTests.cs` (new) | 3, 4 | Editor, preview and tab menus |

Run a single test class with:

```bash
DOTNET_CLI_TELEMETRY_OPTOUT=1 "$LOCALAPPDATA/Microsoft/dotnet/dotnet.exe" test tests/Kil0bitSystemMonitor.Tests/Kil0bitSystemMonitor.Tests.csproj --filter "FullyQualifiedName~Kil0bitSystemMonitor.Tests.PadPaletteTests" -nologo
```

and the whole suite with the same command without `--filter`.

---

### Task 1: PadColor, PadPalette and AppConfig.PadTheme

**Files:**
- Create: `Services/Pad/PadColor.cs`
- Create: `Services/Pad/PadPalette.cs`
- Modify: `Models/SystemMetrics.cs` (the `// ----- MicaPad ---` block, around lines 533–571)
- Create: `tests/Kil0bitSystemMonitor.Tests/PadPaletteTests.cs`
- Modify: `tests/Kil0bitSystemMonitor.Tests/PadConfigTests.cs`

**Interfaces:**
- Consumes: nothing new.
- Produces:
  - `public readonly record struct PadColor(byte A, byte R, byte G, byte B)` with `static PadColor Parse(string hex)`, `PadColor Over(PadColor background)`, `double Luminance`, `static double Contrast(PadColor foreground, PadColor background)`, `ToString()` → `#AARRGGBB`.
  - `public static class PadThemes { const string Dark = "Dark"; const string Light = "Light"; static string Normalize(string? value); }`
  - `public sealed class PadPalette` with `bool IsDark`, `string Name`, 25 `PadColor` properties (`Background, Chrome, Popup, PopupBorder, Border, Hover, Text, WindowText, TextSoft, Muted, Accent, AlertRed, TabHover, TabActive, TabTitle, TabActiveTitle, InfoBar, InfoBarBorder, Banner, BannerBorder, LineNumbers, Selection, Caret, CurrentLine, FindMatch`), `static PadPalette Dark`, `static PadPalette Light`, `static PadPalette For(string? theme)`, `IReadOnlyList<KeyValuePair<string, PadColor>> Resources()` whose keys are `"Pad." + property name`.
  - `AppConfig.PadTheme` (`string`, default `"Dark"`, setter normalizes through `PadThemes.Normalize`).

- [ ] **Step 1: Write the failing tests**

Create `tests/Kil0bitSystemMonitor.Tests/PadPaletteTests.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Kil0bitSystemMonitor.Services.Pad;
using Xunit;

namespace Kil0bitSystemMonitor.Tests
{
    /// <summary>MicaPad's two palettes: colors parse and print, both themes are complete, and every pairing meets its contrast rule.</summary>
    public class PadPaletteTests
    {
        [Theory]
        [InlineData("#3FD2E4", 0xFF, 0x3F, 0xD2, 0xE4)]
        [InlineData("#553FD2E4", 0x55, 0x3F, 0xD2, 0xE4)]
        [InlineData("3fd2e4", 0xFF, 0x3F, 0xD2, 0xE4)]
        public void Parse_reads_rgb_and_argb(string hex, int a, int r, int g, int b)
        {
            Assert.Equal(new PadColor((byte)a, (byte)r, (byte)g, (byte)b), PadColor.Parse(hex));
        }

        [Theory]
        [InlineData("#12345")]
        [InlineData("#GG0000")]
        [InlineData("")]
        public void Parse_rejects_what_is_not_a_color(string hex)
        {
            Assert.Throws<FormatException>(() => PadColor.Parse(hex));
        }

        [Fact]
        public void ToString_prints_argb_in_upper_case()
        {
            Assert.Equal("#553FD2E4", PadColor.Parse("#553fd2e4").ToString());
            Assert.Equal("#FF0E0E13", PadColor.Parse("#0E0E13").ToString());
        }

        [Fact]
        public void Black_on_white_is_21_to_1()
        {
            Assert.Equal(21.0, PadColor.Contrast(PadColor.Parse("#000000"), PadColor.Parse("#FFFFFF")), 2);
        }

        [Fact]
        public void A_translucent_color_is_judged_as_it_is_painted()
        {
            // Half-white over black is #808080: luminance 0.2159, so (0.2159 + 0.05) / 0.05 = 5.32.
            var halfWhite = PadColor.Parse("#80FFFFFF");
            Assert.Equal(PadColor.Parse("#808080"), halfWhite.Over(PadColor.Parse("#000000")));
            Assert.Equal(5.32, PadColor.Contrast(halfWhite, PadColor.Parse("#000000")), 2);
        }

        public static IEnumerable<object[]> ContrastRules()
        {
            var rules = new (string Fg, string Bg, double Min)[]
            {
                ("Text", "Background", 7), ("WindowText", "Background", 7), ("WindowText", "Chrome", 7),
                ("WindowText", "Popup", 7), ("WindowText", "InfoBar", 7), ("WindowText", "Banner", 7),
                ("TextSoft", "Background", 7), ("TabActiveTitle", "TabActive", 7),
                ("Muted", "Background", 4.5), ("Muted", "Chrome", 4.5), ("Muted", "Popup", 4.5),
                ("TabTitle", "Chrome", 4.5),
                ("Accent", "Background", 4.5), ("Accent", "Chrome", 4.5), ("Accent", "Popup", 4.5),
                ("Accent", "InfoBar", 4.5), ("Accent", "Banner", 4.5), ("Accent", "TabActive", 4.5),
                ("AlertRed", "Background", 4.5), ("AlertRed", "Chrome", 4.5),
                ("LineNumbers", "Background", 3),
            };
            foreach (string theme in new[] { PadThemes.Dark, PadThemes.Light })
                foreach (var rule in rules)
                    yield return new object[] { theme, rule.Fg, rule.Bg, rule.Min };
        }

        [Theory]
        [MemberData(nameof(ContrastRules))]
        public void Every_palette_meets_its_contrast_rules(string theme, string foreground, string background, double minimum)
        {
            var palette = PadPalette.For(theme);
            double ratio = PadColor.Contrast(ColorOf(palette, foreground), ColorOf(palette, background));
            Assert.True(ratio >= minimum, $"{theme}: {foreground} on {background} is {ratio:0.00}:1, needs {minimum}:1");
        }

        [Theory]
        [InlineData("Dark")]
        [InlineData("Light")]
        public void Every_color_is_listed_once_under_its_resource_key(string theme)
        {
            var palette = PadPalette.For(theme);
            var properties = typeof(PadPalette).GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.PropertyType == typeof(PadColor)).ToList();
            var resources = palette.Resources();

            Assert.Equal(25, properties.Count);
            Assert.Equal(properties.Count, resources.Count);
            Assert.Equal(resources.Count, resources.Select(r => r.Key).Distinct().Count());
            foreach (var property in properties)
            {
                var entry = Assert.Single(resources, r => r.Key == "Pad." + property.Name);
                Assert.Equal((PadColor)property.GetValue(palette)!, entry.Value);
                Assert.NotEqual(default(PadColor), entry.Value);
            }
        }

        [Fact]
        public void The_dark_palette_keeps_todays_look()
        {
            var dark = PadPalette.Dark;
            Assert.True(dark.IsDark);
            Assert.Equal("#FF0E0E13", dark.Background.ToString());
            Assert.Equal("#FF141419", dark.Chrome.ToString());
            Assert.Equal("#FF3FD2E4", dark.Accent.ToString());
            Assert.Equal("#FFEDEDF2", dark.Text.ToString());
            Assert.Equal("#553FD2E4", dark.Selection.ToString());
        }

        [Fact]
        public void The_light_palette_is_light_with_a_darker_accent()
        {
            var light = PadPalette.Light;
            Assert.False(light.IsDark);
            Assert.Equal("#FFFBFBFD", light.Background.ToString());
            Assert.Equal("#FF1B1B1F", light.Text.ToString());
            Assert.Equal("#FF06707C", light.Accent.ToString());
        }

        [Theory]
        [InlineData("Light", "Light")]
        [InlineData("light", "Light")]
        [InlineData(" LIGHT ", "Light")]
        [InlineData("Dark", "Dark")]
        [InlineData("dark", "Dark")]
        [InlineData("blue", "Dark")]
        [InlineData("", "Dark")]
        [InlineData(null, "Dark")]
        public void Theme_names_normalize_to_dark_or_light(string? value, string expected)
        {
            Assert.Equal(expected, PadThemes.Normalize(value));
        }

        [Fact]
        public void For_picks_the_palette_by_normalized_name()
        {
            Assert.Same(PadPalette.Light, PadPalette.For("light"));
            Assert.Same(PadPalette.Dark, PadPalette.For("anything"));
            Assert.Same(PadPalette.Dark, PadPalette.For(null));
        }

        private static PadColor ColorOf(PadPalette palette, string name) =>
            (PadColor)typeof(PadPalette).GetProperty(name)!.GetValue(palette)!;
    }
}
```

Add to `tests/Kil0bitSystemMonitor.Tests/PadConfigTests.cs`, inside the class (after `The_default_hotkey_parses`):

```csharp
        [Fact]
        public void The_theme_defaults_to_dark()
        {
            Assert.Equal("Dark", new AppConfig().PadTheme);
            Assert.Equal("Dark", JsonSerializer.Deserialize<AppConfig>("{\"ShowCpu\": false}")!.PadTheme);
        }

        [Fact]
        public void The_theme_survives_a_round_trip()
        {
            var back = JsonSerializer.Deserialize<AppConfig>(JsonSerializer.Serialize(new AppConfig { PadTheme = "Light" }))!;
            Assert.Equal("Light", back.PadTheme);
        }

        [Theory]
        [InlineData("\"light\"", "Light")]
        [InlineData("\"LIGHT\"", "Light")]
        [InlineData("\"blue\"", "Dark")]
        [InlineData("\"\"", "Dark")]
        [InlineData("null", "Dark")]
        public void A_hand_edited_theme_reads_as_dark_or_light(string json, string expected)
        {
            var config = JsonSerializer.Deserialize<AppConfig>("{\"PadTheme\": " + json + "}")!;
            Assert.Equal(expected, config.PadTheme);
        }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `DOTNET_CLI_TELEMETRY_OPTOUT=1 "$LOCALAPPDATA/Microsoft/dotnet/dotnet.exe" test tests/Kil0bitSystemMonitor.Tests/Kil0bitSystemMonitor.Tests.csproj --filter "FullyQualifiedName~PadPaletteTests|FullyQualifiedName~PadConfigTests" -nologo`
Expected: build FAILS with `CS0246: The type or namespace name 'PadColor' could not be found` (and `PadPalette`, `PadThemes`, `'AppConfig' does not contain a definition for 'PadTheme'`).

- [ ] **Step 3: Write `Services/Pad/PadColor.cs`**

```csharp
using System;
using System.Globalization;

namespace Kil0bitSystemMonitor.Services.Pad
{
    /// <summary>
    /// One MicaPad color, alpha first. Pure (no WPF types), so the palette and its contrast rules
    /// are tested without a UI thread; <c>Pad/PadThemeApplier</c> turns it into a WPF brush.
    /// </summary>
    public readonly record struct PadColor(byte A, byte R, byte G, byte B)
    {
        /// <summary>Parses <c>#RRGGBB</c> (opaque) or <c>#AARRGGBB</c>; the <c>#</c> is optional.</summary>
        /// <exception cref="FormatException">The text is not one of those forms.</exception>
        public static PadColor Parse(string hex)
        {
            string s = hex ?? "";
            if (s.StartsWith("#", StringComparison.Ordinal)) s = s.Substring(1);
            if (s.Length == 6) s = "FF" + s;
            if (s.Length != 8 || !uint.TryParse(s, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out uint v))
                throw new FormatException("Not a #RRGGBB or #AARRGGBB color: " + hex);
            return new PadColor((byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v);
        }

        /// <summary><c>#AARRGGBB</c>, upper case.</summary>
        public override string ToString() =>
            string.Format(CultureInfo.InvariantCulture, "#{0:X2}{1:X2}{2:X2}{3:X2}", A, R, G, B);

        /// <summary>This color painted over an opaque <paramref name="background"/>: what the eye sees.</summary>
        public PadColor Over(PadColor background)
        {
            double alpha = A / 255.0;
            return new PadColor(255, Mix(R, background.R, alpha), Mix(G, background.G, alpha), Mix(B, background.B, alpha));
        }

        /// <summary>WCAG 2 relative luminance of the RGB channels, ignoring alpha.</summary>
        public double Luminance => 0.2126 * Channel(R) + 0.7152 * Channel(G) + 0.0722 * Channel(B);

        /// <summary>
        /// WCAG 2 contrast ratio, 1 to 21, of <paramref name="foreground"/> painted over
        /// <paramref name="background"/>. The background is treated as opaque.
        /// </summary>
        public static double Contrast(PadColor foreground, PadColor background)
        {
            var solid = background with { A = 255 };
            double a = foreground.Over(solid).Luminance;
            double b = solid.Luminance;
            return (Math.Max(a, b) + 0.05) / (Math.Min(a, b) + 0.05);
        }

        private static byte Mix(byte front, byte back, double alpha) =>
            (byte)Math.Round(alpha * front + (1 - alpha) * back, MidpointRounding.AwayFromZero);

        private static double Channel(byte value)
        {
            double s = value / 255.0;
            return s <= 0.04045 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }
    }
}
```

- [ ] **Step 4: Write `Services/Pad/PadPalette.cs`**

```csharp
using System;
using System.Collections.Generic;

namespace Kil0bitSystemMonitor.Services.Pad
{
    /// <summary>The names MicaPad's theme is stored under in <c>AppConfig.PadTheme</c>.</summary>
    public static class PadThemes
    {
        public const string Dark = "Dark";
        public const string Light = "Light";

        /// <summary>"Light" in any letter case, with or without spaces, is Light; anything else, null included, is Dark.</summary>
        public static string Normalize(string? value) =>
            string.Equals(value?.Trim(), Light, StringComparison.OrdinalIgnoreCase) ? Light : Dark;
    }

    /// <summary>
    /// Every color MicaPad paints, for one theme. XAML reads them as <c>Pad.&lt;Name&gt;</c> brushes
    /// (see <see cref="Resources"/>); code that paints directly reads the properties. Only MicaPad
    /// uses this: the rest of MicaStats keeps its own colors.
    /// </summary>
    public sealed class PadPalette
    {
        private PadPalette() { }

        /// <summary>The theme name, <see cref="PadThemes.Dark"/> or <see cref="PadThemes.Light"/>.</summary>
        public string Name { get; private init; } = PadThemes.Dark;

        /// <summary>True for the dark theme; picks the ModernWpf theme and the title bar.</summary>
        public bool IsDark { get; private init; }

        /// <summary>Window, editor and history preview background.</summary>
        public PadColor Background { get; private init; }
        /// <summary>Tab strip, status bar, find bar and history pane.</summary>
        public PadColor Chrome { get; private init; }
        /// <summary>Rename box, closed-notes list, go-to-line box.</summary>
        public PadColor Popup { get; private init; }
        public PadColor PopupBorder { get; private init; }
        /// <summary>Thin separators between the bars.</summary>
        public PadColor Border { get; private init; }
        /// <summary>Flat button under the mouse.</summary>
        public PadColor Hover { get; private init; }
        /// <summary>The note's text.</summary>
        public PadColor Text { get; private init; }
        /// <summary>Default text of the window's own controls.</summary>
        public PadColor WindowText { get; private init; }
        /// <summary>Flat buttons and the history preview's text.</summary>
        public PadColor TextSoft { get; private init; }
        /// <summary>Secondary text: status bar, counts, hints.</summary>
        public PadColor Muted { get; private init; }
        /// <summary>Unsaved-file dot, primary actions, caret.</summary>
        public PadColor Accent { get; private init; }
        /// <summary>"Not saved — retrying" and find errors.</summary>
        public PadColor AlertRed { get; private init; }
        public PadColor TabHover { get; private init; }
        public PadColor TabActive { get; private init; }
        public PadColor TabTitle { get; private init; }
        public PadColor TabActiveTitle { get; private init; }
        /// <summary>The info bar that asks about files.</summary>
        public PadColor InfoBar { get; private init; }
        public PadColor InfoBarBorder { get; private init; }
        /// <summary>The "Viewing 14:30" banner above a history preview.</summary>
        public PadColor Banner { get; private init; }
        public PadColor BannerBorder { get; private init; }
        public PadColor LineNumbers { get; private init; }
        public PadColor Selection { get; private init; }
        public PadColor Caret { get; private init; }
        public PadColor CurrentLine { get; private init; }
        /// <summary>The box behind each find match.</summary>
        public PadColor FindMatch { get; private init; }

        /// <summary>Today's MicaPad colors.</summary>
        public static PadPalette Dark { get; } = new()
        {
            Name = PadThemes.Dark,
            IsDark = true,
            Background = PadColor.Parse("#0E0E13"),
            Chrome = PadColor.Parse("#141419"),
            Popup = PadColor.Parse("#1C1C24"),
            PopupBorder = PadColor.Parse("#33FFFFFF"),
            Border = PadColor.Parse("#1AFFFFFF"),
            Hover = PadColor.Parse("#1FFFFFFF"),
            Text = PadColor.Parse("#EDEDF2"),
            WindowText = PadColor.Parse("#E9EDEDF2"),
            TextSoft = PadColor.Parse("#CFEDEDF2"),
            Muted = PadColor.Parse("#88EDEDF2"),
            Accent = PadColor.Parse("#3FD2E4"),
            AlertRed = PadColor.Parse("#FF6B6B"),
            TabHover = PadColor.Parse("#16161C"),
            TabActive = PadColor.Parse("#1C1C24"),
            TabTitle = PadColor.Parse("#99EDEDF2"),
            TabActiveTitle = PadColor.Parse("#EDEDF2"),
            InfoBar = PadColor.Parse("#2A2410"),
            InfoBarBorder = PadColor.Parse("#55FFC857"),
            Banner = PadColor.Parse("#10283A"),
            BannerBorder = PadColor.Parse("#333FD2E4"),
            LineNumbers = PadColor.Parse("#66EDEDF2"),
            Selection = PadColor.Parse("#553FD2E4"),
            Caret = PadColor.Parse("#3FD2E4"),
            CurrentLine = PadColor.Parse("#0FFFFFFF"),
            FindMatch = PadColor.Parse("#40FFC857"),
        };

        /// <summary>
        /// Off-white page, near-black text. The accent is a darker cyan than the dark theme's,
        /// which is unreadable on white.
        /// </summary>
        public static PadPalette Light { get; } = new()
        {
            Name = PadThemes.Light,
            IsDark = false,
            Background = PadColor.Parse("#FBFBFD"),
            Chrome = PadColor.Parse("#F0F0F4"),
            Popup = PadColor.Parse("#FFFFFF"),
            PopupBorder = PadColor.Parse("#26000000"),
            Border = PadColor.Parse("#1A000000"),
            Hover = PadColor.Parse("#14000000"),
            Text = PadColor.Parse("#1B1B1F"),
            WindowText = PadColor.Parse("#1B1B1F"),
            TextSoft = PadColor.Parse("#2E2E36"),
            Muted = PadColor.Parse("#666670"),
            Accent = PadColor.Parse("#06707C"),
            AlertRed = PadColor.Parse("#C62828"),
            TabHover = PadColor.Parse("#E6E6EC"),
            TabActive = PadColor.Parse("#FFFFFF"),
            TabTitle = PadColor.Parse("#55555F"),
            TabActiveTitle = PadColor.Parse("#1B1B1F"),
            InfoBar = PadColor.Parse("#FFF4D6"),
            InfoBarBorder = PadColor.Parse("#80E0A800"),
            Banner = PadColor.Parse("#E3F4F7"),
            BannerBorder = PadColor.Parse("#4006707C"),
            LineNumbers = PadColor.Parse("#8A8A94"),
            Selection = PadColor.Parse("#3306707C"),
            Caret = PadColor.Parse("#06707C"),
            CurrentLine = PadColor.Parse("#0A000000"),
            FindMatch = PadColor.Parse("#66FFC857"),
        };

        /// <summary>The palette for a stored theme name; see <see cref="PadThemes.Normalize"/>.</summary>
        public static PadPalette For(string? theme) => PadThemes.Normalize(theme) == PadThemes.Light ? Light : Dark;

        /// <summary>Every color under the resource key MicaPad's XAML uses for it, <c>Pad.&lt;property name&gt;</c>.</summary>
        public IReadOnlyList<KeyValuePair<string, PadColor>> Resources() => new[]
        {
            Pair(nameof(Background), Background),
            Pair(nameof(Chrome), Chrome),
            Pair(nameof(Popup), Popup),
            Pair(nameof(PopupBorder), PopupBorder),
            Pair(nameof(Border), Border),
            Pair(nameof(Hover), Hover),
            Pair(nameof(Text), Text),
            Pair(nameof(WindowText), WindowText),
            Pair(nameof(TextSoft), TextSoft),
            Pair(nameof(Muted), Muted),
            Pair(nameof(Accent), Accent),
            Pair(nameof(AlertRed), AlertRed),
            Pair(nameof(TabHover), TabHover),
            Pair(nameof(TabActive), TabActive),
            Pair(nameof(TabTitle), TabTitle),
            Pair(nameof(TabActiveTitle), TabActiveTitle),
            Pair(nameof(InfoBar), InfoBar),
            Pair(nameof(InfoBarBorder), InfoBarBorder),
            Pair(nameof(Banner), Banner),
            Pair(nameof(BannerBorder), BannerBorder),
            Pair(nameof(LineNumbers), LineNumbers),
            Pair(nameof(Selection), Selection),
            Pair(nameof(Caret), Caret),
            Pair(nameof(CurrentLine), CurrentLine),
            Pair(nameof(FindMatch), FindMatch),
        };

        private static KeyValuePair<string, PadColor> Pair(string name, PadColor color) => new("Pad." + name, color);
    }
}
```

- [ ] **Step 5: Add `AppConfig.PadTheme`**

In `Models/SystemMetrics.cs`, in the `// ----- MicaPad ---` block, add the field after `private bool _padReopenAtLogin = true;`:

```csharp
        private string _padTheme = Kil0bitSystemMonitor.Services.Pad.PadThemes.Dark;
```

and the property after the `PadReopenAtLogin` property:

```csharp
        /// <summary>
        /// MicaPad's own theme, "Dark" or "Light"; any other value reads as Dark. Nothing else in
        /// MicaStats follows it.
        /// </summary>
        public string PadTheme
        {
            get => _padTheme;
            set { Set(ref _padTheme, Kil0bitSystemMonitor.Services.Pad.PadThemes.Normalize(value)); }
        }
```

- [ ] **Step 6: Run the tests to verify they pass**

Run: `DOTNET_CLI_TELEMETRY_OPTOUT=1 "$LOCALAPPDATA/Microsoft/dotnet/dotnet.exe" test tests/Kil0bitSystemMonitor.Tests/Kil0bitSystemMonitor.Tests.csproj --filter "FullyQualifiedName~PadPaletteTests|FullyQualifiedName~PadConfigTests" -nologo`
Expected: PASS, 0 failed.

- [ ] **Step 7: Run the whole suite**

Run the suite command without `--filter`. Expected: all pass (1242 before this task, plus the new ones).

- [ ] **Step 8: Commit**

```bash
git add Services/Pad/PadColor.cs Services/Pad/PadPalette.cs Models/SystemMetrics.cs tests/Kil0bitSystemMonitor.Tests/PadPaletteTests.cs tests/Kil0bitSystemMonitor.Tests/PadConfigTests.cs
git commit -m "feat(pad): palette for a dark and a light MicaPad theme" -m "Co-Authored-By: <your model name> <noreply@anthropic.com>"
```

---

### Task 2: Paint MicaPad from the palette, with a live theme switch

**Files:**
- Create: `Pad/PadThemeApplier.cs`
- Modify: `Pad/MicaPadWindow.xaml` (whole file below)
- Modify: `Pad/MicaPadWindow.xaml.cs` (constructor, `ConfigureEditor`, `OnConfigChanged`, `NewMenu`, `UpdateSaveText`, `OnSourceInitialized`, new `ApplyTheme`, `ToggleTheme`, `OnThemeButtonClick`)
- Modify: `Pad/FindReplaceBar.xaml` (whole file below), `Pad/FindReplaceBar.xaml.cs`
- Modify: `Pad/HistoryPane.xaml` (whole file below)
- Modify: `Pad/MatchHighlighter.cs`
- Modify: `SettingsWindow.xaml` (MicaPad section), `SettingsWindow.xaml.cs` (`LoadPadSettings`, new `OnPadThemeChanged`)
- Modify: `GUIDE.md`, `README.md`
- Create: `tests/Kil0bitSystemMonitor.Tests/PadThemeTests.cs`

**Interfaces:**
- Consumes (Task 1): `PadPalette`, `PadPalette.For`, `PadPalette.Resources()`, `PadThemes`, `AppConfig.PadTheme`.
- Produces:
  - `internal static class PadThemeApplier` with `Color ToColor(PadColor)`, `SolidColorBrush ToBrush(PadColor)` (frozen), `void ApplyResources(ResourceDictionary, PadPalette)`, `void ApplyTitleBar(Window, bool dark)`.
  - `MicaPadWindow.Palette` (`internal PadPalette`, the palette in use), `MicaPadWindow.ToggleTheme()` (`internal void`), `MicaPadWindow.ThemeButton` (XAML name).
  - `MicaPadWindow.NewMenu(UIElement, PlacementMode)` becomes an **instance** method that themes the menu from `Palette` (Tasks 3 and 4 use it).
  - `FindReplaceBar.ApplyPalette(PadPalette)` (`public void`) and `FindReplaceBar.MatchFill` (`internal Brush`).
  - `MatchHighlighter.Fill` becomes `public Brush Fill { get; set; }`.

- [ ] **Step 1: Write the failing tests**

Create `tests/Kil0bitSystemMonitor.Tests/PadThemeTests.cs`:

```csharp
using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows.Media;
using System.Windows.Threading;
using Kil0bitSystemMonitor.Models;
using Kil0bitSystemMonitor.Pad;
using Kil0bitSystemMonitor.Services.Pad;
using Xunit;

namespace Kil0bitSystemMonitor.Tests
{
    /// <summary>MicaPad's theme: no literal colors left, every key the XAML uses exists, and a switch repaints at once.</summary>
    public class PadThemeTests
    {
        private static readonly string[] PadXaml =
        {
            Path.Combine("Pad", "MicaPadWindow.xaml"),
            Path.Combine("Pad", "FindReplaceBar.xaml"),
            Path.Combine("Pad", "HistoryPane.xaml"),
        };

        private static void WithWindow(string theme, Action<MicaPadWindow, AppConfig> test) => UiThread.Run(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            using var env = new PadTestEnv(post: action => dispatcher.BeginInvoke(action));
            var config = new AppConfig { PadTheme = theme };
            var window = new MicaPadWindow(env.Workspace, config);
            try
            {
                window.LoadSession();
                test(window, config);
            }
            finally
            {
                window.CloseForExit();
            }
        });

        private static Color BrushColor(object? brush) => Assert.IsAssignableFrom<SolidColorBrush>(brush).Color;

        private static Color Wpf(PadColor c) => Color.FromArgb(c.A, c.R, c.G, c.B);

        [Fact]
        public void The_micapad_xaml_files_have_no_literal_colors()
        {
            var literal = new Regex("\"#[0-9A-Fa-f]{6}(?:[0-9A-Fa-f]{2})?\"");
            foreach (string file in PadXaml)
            {
                string xaml = File.ReadAllText(Path.Combine(PadWindowTests.RepoRoot(), file));
                var found = literal.Matches(xaml).Select(m => m.Value).ToList();
                Assert.True(found.Count == 0, file + " still has literal colors: " + string.Join(", ", found));
            }
        }

        [Fact]
        public void Every_pad_resource_the_ui_uses_is_in_the_palette()
        {
            var keys = PadPalette.Dark.Resources().Select(r => r.Key).ToHashSet();
            var used = new Regex(@"Pad\.[A-Za-z]+");
            var sources = PadXaml.Concat(new[]
            {
                Path.Combine("Pad", "MicaPadWindow.xaml.cs"),
                Path.Combine("Pad", "FindReplaceBar.xaml.cs"),
            });
            int count = 0;
            foreach (string file in sources)
            {
                string text = File.ReadAllText(Path.Combine(PadWindowTests.RepoRoot(), file));
                foreach (Match match in used.Matches(text))
                {
                    // Namespaces (Kil0bitSystemMonitor.Pad.X, Services.Pad.X) and words ending in
                    // "Pad" (MicaPad.X) are not resource keys: a key starts after a quote or space.
                    char before = match.Index > 0 ? text[match.Index - 1] : ' ';
                    if (before == '.' || char.IsLetterOrDigit(before)) continue;
                    count++;
                    Assert.True(keys.Contains(match.Value), file + " uses " + match.Value + ", which the palette does not have");
                }
            }
            Assert.True(count > 20, "expected the MicaPad UI to read its colors from Pad.* keys, found " + count);
        }

        [Fact]
        public void The_window_opens_in_the_configured_theme() => WithWindow("Light", (window, config) =>
        {
            var light = PadPalette.Light;
            Assert.Same(light, window.Palette);
            Assert.Equal(Wpf(light.Background), BrushColor(window.Resources["Pad.Background"]));
            Assert.Equal(Wpf(light.Background), BrushColor(window.Editor.Background));
            Assert.Equal(Wpf(light.Text), BrushColor(window.Editor.Foreground));
            Assert.Equal(Wpf(light.Selection), BrushColor(window.Editor.TextArea.SelectionBrush));
            Assert.Equal(Wpf(light.LineNumbers), BrushColor(window.Editor.LineNumbersForeground));
            Assert.Equal(ModernWpf.ElementTheme.Light, ModernWpf.ThemeManager.GetRequestedTheme(window));
            Assert.Equal("", window.ThemeButton.Content);
            Assert.Equal("Switch to dark theme", window.ThemeButton.ToolTip);
        });

        [Fact]
        public void The_theme_button_switches_live_and_remembers_the_choice() => WithWindow("Dark", (window, config) =>
        {
            Assert.Equal("", window.ThemeButton.Content);
            Assert.Equal("Switch to light theme", window.ThemeButton.ToolTip);

            window.ToggleTheme();

            Assert.Equal("Light", config.PadTheme);
            Assert.Same(PadPalette.Light, window.Palette);
            Assert.Equal(Wpf(PadPalette.Light.Background), BrushColor(window.Editor.Background));
            Assert.Equal(Wpf(PadPalette.Light.Caret), BrushColor(window.Editor.TextArea.Caret.CaretBrush));
            Assert.Equal(ModernWpf.ElementTheme.Light, ModernWpf.ThemeManager.GetRequestedTheme(window));

            window.ToggleTheme();

            Assert.Equal("Dark", config.PadTheme);
            Assert.Equal(Wpf(PadPalette.Dark.Background), BrushColor(window.Editor.Background));
        });

        [Fact]
        public void A_theme_set_elsewhere_repaints_an_open_window() => WithWindow("Dark", (window, config) =>
        {
            config.PadTheme = "Light";   // as Settings does

            Assert.Same(PadPalette.Light, window.Palette);
            Assert.Equal(Wpf(PadPalette.Light.Chrome), BrushColor(window.Resources["Pad.Chrome"]));
        });

        [Fact]
        public void A_switch_repaints_the_find_bar_and_the_preview_too() => WithWindow("Dark", (window, config) =>
        {
            window.Editor.Document.Text = "ab ab";
            window.FindBar.FindBox.Text = "ab";
            window.FindBar.Open(replace: false);
            Assert.Equal(Wpf(PadPalette.Dark.Muted), BrushColor(window.FindBar.CountText.Foreground));

            window.ToggleTheme();

            Assert.Equal(Wpf(PadPalette.Light.Muted), BrushColor(window.FindBar.CountText.Foreground));
            Assert.Equal(Wpf(PadPalette.Light.FindMatch), BrushColor(window.FindBar.MatchFill));
            Assert.Equal(Wpf(PadPalette.Light.Background), BrushColor(window.PreviewEditor.Background));
            Assert.Equal(Wpf(PadPalette.Light.TextSoft), BrushColor(window.PreviewEditor.Foreground));
            Assert.Equal(Wpf(PadPalette.Light.Muted), BrushColor(window.SaveText.Foreground));
        });

        [Fact]
        public void Menus_follow_the_theme() => WithWindow("Light", (window, config) =>
        {
            var menu = window.NewMenu(window.MenuButton, System.Windows.Controls.Primitives.PlacementMode.Bottom);
            Assert.Equal(ModernWpf.ElementTheme.Light, ModernWpf.ThemeManager.GetRequestedTheme(menu));
        });

        [Fact]
        public void Settings_offers_the_theme_choice()
        {
            string xaml = File.ReadAllText(Path.Combine(PadWindowTests.RepoRoot(), "SettingsWindow.xaml"));
            Assert.Contains("x:Name=\"PadThemeBox\"", xaml);
            Assert.Contains("SelectionChanged=\"OnPadThemeChanged\"", xaml);
        }
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `DOTNET_CLI_TELEMETRY_OPTOUT=1 "$LOCALAPPDATA/Microsoft/dotnet/dotnet.exe" test tests/Kil0bitSystemMonitor.Tests/Kil0bitSystemMonitor.Tests.csproj --filter "FullyQualifiedName~PadThemeTests" -nologo`
Expected: build FAILS (`'MicaPadWindow' does not contain a definition for 'Palette'`, `'ThemeButton'`, `'ToggleTheme'`, `'MatchFill'`; `NewMenu` is inaccessible).

- [ ] **Step 3: Write `Pad/PadThemeApplier.cs`**

```csharp
using System;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Kil0bitSystemMonitor.Helpers;
using Kil0bitSystemMonitor.Services.Pad;

using Color = System.Windows.Media.Color;

namespace Kil0bitSystemMonitor.Pad
{
    /// <summary>
    /// Puts a <see cref="PadPalette"/> onto WPF: frozen brushes under the <c>Pad.*</c> keys that
    /// MicaPad's XAML reads with DynamicResource, and a dark or light title bar.
    /// </summary>
    internal static class PadThemeApplier
    {
        public static Color ToColor(PadColor color) => Color.FromArgb(color.A, color.R, color.G, color.B);

        /// <summary>A frozen brush, safe to share and cheap to render.</summary>
        public static SolidColorBrush ToBrush(PadColor color)
        {
            var brush = new SolidColorBrush(ToColor(color));
            brush.Freeze();
            return brush;
        }

        /// <summary>
        /// Replaces every <c>Pad.*</c> brush in <paramref name="resources"/>. Elements that read them
        /// through DynamicResource repaint at once.
        /// </summary>
        public static void ApplyResources(ResourceDictionary resources, PadPalette palette)
        {
            foreach (var (key, color) in palette.Resources()) resources[key] = ToBrush(color);
        }

        /// <summary>
        /// A dark or light caption through <c>DWMWA_USE_IMMERSIVE_DARK_MODE</c>. A window without a
        /// handle yet is skipped; <c>SourceInitialized</c> applies it once the handle exists.
        /// </summary>
        public static void ApplyTitleBar(Window window, bool dark)
        {
            IntPtr hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero) return;
            int value = dark ? 1 : 0;
            Win32Helper.DwmSetWindowAttribute(hwnd, Win32Helper.DWMWA_USE_IMMERSIVE_DARK_MODE, ref value, sizeof(int));
        }
    }
}
```

- [ ] **Step 4: Replace `Pad/MicaPadWindow.xaml` with**

```xml
<Window
    x:Class="Kil0bitSystemMonitor.Pad.MicaPadWindow"
    xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
    xmlns:ui="http://schemas.modernwpf.com/2019"
    xmlns:avalon="http://icsharpcode.net/sharpdevelop/avalonedit"
    xmlns:pad="clr-namespace:Kil0bitSystemMonitor.Services.Pad"
    xmlns:local="clr-namespace:Kil0bitSystemMonitor.Pad"
    Title="MicaPad"
    Width="900" Height="640" MinWidth="420" MinHeight="260"
    WindowStartupLocation="CenterScreen"
    Background="{DynamicResource Pad.Background}"
    Foreground="{DynamicResource Pad.WindowText}"
    FontFamily="Segoe UI Variable Text, Segoe UI"
    ui:ThemeManager.RequestedTheme="Dark"
    UseLayoutRounding="True"
    SnapsToDevicePixels="True"
    PreviewKeyDown="OnPreviewKeyDown"
    PreviewMouseWheel="OnPreviewMouseWheel">

    <!--
      MicaPad: tabs of notes that save themselves. The window is a thin shell over PadWorkspace,
      which owns every rule about saving, history and closing. One AvalonEdit editor shows the
      active tab's TextDocument; each document keeps its own undo stack.

      Every color is a Pad.* brush from PadPalette, put into these resources by PadThemeApplier.
      DynamicResource everywhere, so the theme button repaints the whole window at once.
    -->

    <Window.Resources>
        <BooleanToVisibilityConverter x:Key="BoolToVisibility" />

        <Style x:Key="FlatButton" TargetType="Button">
            <Setter Property="Foreground" Value="{DynamicResource Pad.TextSoft}" />
            <Setter Property="FontSize" Value="12" />
            <Setter Property="Cursor" Value="Hand" />
            <Setter Property="Padding" Value="8,3" />
            <Setter Property="Template">
                <Setter.Value>
                    <ControlTemplate TargetType="Button">
                        <Border x:Name="Bg" Background="Transparent" CornerRadius="4" Padding="{TemplateBinding Padding}">
                            <ContentPresenter HorizontalAlignment="Center" VerticalAlignment="Center" />
                        </Border>
                        <ControlTemplate.Triggers>
                            <Trigger Property="IsMouseOver" Value="True">
                                <Setter TargetName="Bg" Property="Background" Value="{DynamicResource Pad.Hover}" />
                            </Trigger>
                        </ControlTemplate.Triggers>
                    </ControlTemplate>
                </Setter.Value>
            </Setter>
        </Style>

        <Style x:Key="GlyphButton" TargetType="Button" BasedOn="{StaticResource FlatButton}">
            <Setter Property="FontFamily" Value="Segoe Fluent Icons, Segoe MDL2 Assets" />
            <Setter Property="FontSize" Value="12" />
            <Setter Property="Padding" Value="8,6" />
        </Style>

        <Style x:Key="ScrollButton" TargetType="RepeatButton">
            <Setter Property="Foreground" Value="{DynamicResource Pad.TextSoft}" />
            <Setter Property="FontFamily" Value="Segoe Fluent Icons, Segoe MDL2 Assets" />
            <Setter Property="FontSize" Value="10" />
            <Setter Property="Cursor" Value="Hand" />
            <Setter Property="Template">
                <Setter.Value>
                    <ControlTemplate TargetType="RepeatButton">
                        <Border x:Name="Bg" Background="Transparent" CornerRadius="4" Padding="6,8">
                            <ContentPresenter HorizontalAlignment="Center" VerticalAlignment="Center" />
                        </Border>
                        <ControlTemplate.Triggers>
                            <Trigger Property="IsMouseOver" Value="True">
                                <Setter TargetName="Bg" Property="Background" Value="{DynamicResource Pad.Hover}" />
                            </Trigger>
                        </ControlTemplate.Triggers>
                    </ControlTemplate>
                </Setter.Value>
            </Setter>
        </Style>

        <DataTemplate x:Key="TabTemplate" DataType="{x:Type pad:OpenNote}">
            <Border x:Name="TabBorder" Background="Transparent" CornerRadius="6,6,0,0"
                    MinWidth="80" MaxWidth="200" Height="32" Padding="10,0,4,0" Margin="0,0,2,0"
                    Cursor="Hand" Tag="{Binding}" ToolTip="{Binding Meta.SourcePath}"
                    MouseLeftButtonDown="OnTabMouseLeftButtonDown" MouseUp="OnTabMouseUp">
                <DockPanel LastChildFill="True">
                    <Button DockPanel.Dock="Right" Style="{StaticResource GlyphButton}" Padding="5,3" FontSize="9"
                            Content="&#xE711;" Tag="{Binding}" Click="OnTabCloseClick" ToolTip="Close (Ctrl+W)" />
                    <TextBlock DockPanel.Dock="Right" Text="•" Foreground="{DynamicResource Pad.Accent}" Margin="4,0,2,0"
                               VerticalAlignment="Center" ToolTip="Edited since the file was last saved"
                               Visibility="{Binding HasUnsavedEdits, Converter={StaticResource BoolToVisibility}}" />
                    <TextBlock x:Name="TabTitle" Text="{Binding Title}" TextTrimming="CharacterEllipsis"
                               VerticalAlignment="Center" FontSize="12" Foreground="{DynamicResource Pad.TabTitle}" />
                </DockPanel>
            </Border>
            <DataTemplate.Triggers>
                <Trigger SourceName="TabBorder" Property="IsMouseOver" Value="True">
                    <Setter TargetName="TabBorder" Property="Background" Value="{DynamicResource Pad.TabHover}" />
                </Trigger>
                <DataTrigger Binding="{Binding IsActive}" Value="True">
                    <Setter TargetName="TabBorder" Property="Background" Value="{DynamicResource Pad.TabActive}" />
                    <Setter TargetName="TabTitle" Property="Foreground" Value="{DynamicResource Pad.TabActiveTitle}" />
                </DataTrigger>
            </DataTemplate.Triggers>
        </DataTemplate>
    </Window.Resources>

    <Grid>
        <Grid.RowDefinitions>
            <RowDefinition Height="Auto" />
            <RowDefinition Height="Auto" />
            <RowDefinition Height="Auto" />
            <RowDefinition Height="*" />
            <RowDefinition Height="Auto" />
        </Grid.RowDefinitions>

        <!-- Row 0: tabs. Row 1: info bar. Row 2: find bar. Row 3: editor and history. Row 4: status. -->
        <DockPanel Grid.Row="0" Background="{DynamicResource Pad.Chrome}" LastChildFill="True">
            <Button x:Name="MenuButton" DockPanel.Dock="Right" Style="{StaticResource GlyphButton}"
                    Content="&#xE700;" ToolTip="Menu" Click="OnMenuButtonClick" Margin="0,4,6,0" />
            <Button x:Name="ClosedNotesButton" DockPanel.Dock="Right" Style="{StaticResource GlyphButton}"
                    Content="&#xE70D;" ToolTip="Closed notes" Click="OnClosedNotesClick" Margin="0,4,0,0" />
            <Button x:Name="ThemeButton" DockPanel.Dock="Right" Style="{StaticResource GlyphButton}"
                    Click="OnThemeButtonClick" Margin="0,4,0,0" />
            <RepeatButton x:Name="ScrollRightButton" DockPanel.Dock="Right" Style="{StaticResource ScrollButton}"
                          Content="&#xE76C;" Click="OnScrollTabsRight" Visibility="Collapsed" />
            <RepeatButton x:Name="ScrollLeftButton" DockPanel.Dock="Left" Style="{StaticResource ScrollButton}"
                          Content="&#xE76B;" Click="OnScrollTabsLeft" Visibility="Collapsed" />
            <ScrollViewer x:Name="TabScroller" HorizontalScrollBarVisibility="Hidden" VerticalScrollBarVisibility="Disabled"
                          ScrollChanged="OnTabScrollChanged" Margin="6,6,0,0">
                <StackPanel Orientation="Horizontal">
                    <ItemsControl x:Name="TabStrip" ItemTemplate="{StaticResource TabTemplate}">
                        <ItemsControl.ItemsPanel>
                            <ItemsPanelTemplate>
                                <StackPanel Orientation="Horizontal" />
                            </ItemsPanelTemplate>
                        </ItemsControl.ItemsPanel>
                    </ItemsControl>
                    <Button Style="{StaticResource GlyphButton}" Content="&#xE710;" ToolTip="New note (Ctrl+N)"
                            Click="OnNewTabClick" VerticalAlignment="Center" Margin="2,0,0,0" />
                </StackPanel>
            </ScrollViewer>
        </DockPanel>

        <Border x:Name="InfoBar" Grid.Row="1" Background="{DynamicResource Pad.InfoBar}" BorderBrush="{DynamicResource Pad.InfoBarBorder}"
                BorderThickness="0,0,0,1" Padding="12,6" Visibility="Collapsed">
            <DockPanel LastChildFill="True">
                <Button DockPanel.Dock="Right" Style="{StaticResource GlyphButton}" Content="&#xE711;" FontSize="9"
                        ToolTip="Dismiss" Click="OnInfoCloseClick" />
                <Button x:Name="InfoSecondary" DockPanel.Dock="Right" Style="{StaticResource FlatButton}"
                        Margin="4,0,0,0" Click="OnInfoSecondaryClick" />
                <Button x:Name="InfoPrimary" DockPanel.Dock="Right" Style="{StaticResource FlatButton}"
                        Margin="8,0,0,0" Foreground="{DynamicResource Pad.Accent}" Click="OnInfoPrimaryClick" />
                <TextBlock x:Name="InfoText" VerticalAlignment="Center" TextWrapping="Wrap" FontSize="12" />
            </DockPanel>
        </Border>

        <local:FindReplaceBar x:Name="FindBar" Grid.Row="2" />

        <Grid x:Name="EditorArea" Grid.Row="3">
            <Grid.ColumnDefinitions>
                <ColumnDefinition Width="*" />
                <ColumnDefinition Width="Auto" />
            </Grid.ColumnDefinitions>

            <avalon:TextEditor x:Name="Editor" Grid.Column="0"
                               Background="{DynamicResource Pad.Background}" Foreground="{DynamicResource Pad.Text}"
                               FontFamily="Cascadia Mono, Consolas" FontSize="14"
                               ShowLineNumbers="True" WordWrap="True"
                               HorizontalScrollBarVisibility="Auto" VerticalScrollBarVisibility="Auto"
                               Padding="6,4,0,0" />

            <DockPanel x:Name="PreviewPanel" Grid.Column="0" Background="{DynamicResource Pad.Background}" Visibility="Collapsed">
                <Border DockPanel.Dock="Top" Background="{DynamicResource Pad.Banner}" BorderBrush="{DynamicResource Pad.BannerBorder}"
                        BorderThickness="0,0,0,1" Padding="12,6">
                    <DockPanel>
                        <Button DockPanel.Dock="Right" Style="{StaticResource FlatButton}" Content="Back" Click="OnPreviewBackClick" />
                        <Button DockPanel.Dock="Right" Style="{StaticResource FlatButton}" Content="Copy all" Click="OnPreviewCopyClick" />
                        <Button x:Name="RestoreButton" DockPanel.Dock="Right" Style="{StaticResource FlatButton}" Content="Restore"
                                Foreground="{DynamicResource Pad.Accent}" Click="OnPreviewRestoreClick" />
                        <TextBlock x:Name="PreviewText" VerticalAlignment="Center" FontSize="12" />
                    </DockPanel>
                </Border>
                <avalon:TextEditor x:Name="PreviewEditor" IsReadOnly="True"
                                   Background="{DynamicResource Pad.Background}" Foreground="{DynamicResource Pad.TextSoft}"
                                   ShowLineNumbers="True" FontFamily="Cascadia Mono, Consolas" FontSize="14"
                                   HorizontalScrollBarVisibility="Auto" VerticalScrollBarVisibility="Auto" />
            </DockPanel>

            <local:HistoryPane x:Name="HistoryPanel" Grid.Column="1" />

            <Border x:Name="GoToLineBox" Grid.Column="0" HorizontalAlignment="Right" VerticalAlignment="Top"
                    Margin="0,8,24,0" Padding="10,6" Background="{DynamicResource Pad.Popup}" BorderBrush="{DynamicResource Pad.PopupBorder}"
                    BorderThickness="1" CornerRadius="6" Visibility="Collapsed">
                <StackPanel Orientation="Horizontal">
                    <TextBlock Text="Go to line" VerticalAlignment="Center" Foreground="{DynamicResource Pad.Muted}" FontSize="12" Margin="0,0,8,0" />
                    <TextBox x:Name="GoToLineInput" Width="90" KeyDown="OnGoToLineKeyDown" LostKeyboardFocus="OnGoToLineLostFocus" />
                </StackPanel>
            </Border>
        </Grid>

        <Border Grid.Row="4" Background="{DynamicResource Pad.Chrome}" BorderBrush="{DynamicResource Pad.Border}" BorderThickness="0,1,0,0" Padding="10,3">
            <DockPanel LastChildFill="True">
                <Button x:Name="HistoryButton" DockPanel.Dock="Right" Style="{StaticResource FlatButton}" FontSize="11.5"
                        Content="History" ToolTip="Versions of this note (Ctrl+Shift+H)" Click="OnHistoryClick" />
                <TextBlock x:Name="SaveText" DockPanel.Dock="Right" VerticalAlignment="Center" FontSize="11.5"
                           Foreground="{DynamicResource Pad.Muted}" Margin="12,0,4,0" />
                <Button x:Name="EolButton" DockPanel.Dock="Right" Style="{StaticResource FlatButton}" FontSize="11.5"
                        ToolTip="Line ending" Click="OnEolClick" />
                <Button x:Name="EncodingButton" DockPanel.Dock="Right" Style="{StaticResource FlatButton}" FontSize="11.5"
                        ToolTip="Encoding used when saving to the file" Click="OnEncodingClick" />
                <TextBlock x:Name="CaretText" VerticalAlignment="Center" FontSize="11.5" Foreground="{DynamicResource Pad.Muted}" />
                <TextBlock x:Name="CharsText" VerticalAlignment="Center" FontSize="11.5" Foreground="{DynamicResource Pad.Muted}" Margin="16,0,0,0" />
            </DockPanel>
        </Border>

        <Popup x:Name="RenamePopup" StaysOpen="False" Placement="Bottom" AllowsTransparency="True">
            <Border Background="{DynamicResource Pad.Popup}" BorderBrush="{DynamicResource Pad.PopupBorder}" BorderThickness="1" CornerRadius="6" Padding="8">
                <TextBox x:Name="RenameBox" Width="220" KeyDown="OnRenameKeyDown" ui:ControlHelper.PlaceholderText="Tab name (empty for automatic)" />
            </Border>
        </Popup>

        <Popup x:Name="ClosedPopup" StaysOpen="False" Placement="Bottom" AllowsTransparency="True"
               PlacementTarget="{Binding ElementName=ClosedNotesButton}">
            <Border Background="{DynamicResource Pad.Popup}" BorderBrush="{DynamicResource Pad.PopupBorder}" BorderThickness="1" CornerRadius="8" Padding="8" Width="360">
                <StackPanel>
                    <TextBox x:Name="ClosedSearch" ui:ControlHelper.PlaceholderText="Search closed notes" TextChanged="OnClosedSearchChanged" />
                    <ListBox x:Name="ClosedList" MaxHeight="360" Margin="0,6,0,0" Background="Transparent" BorderThickness="0"
                             ScrollViewer.HorizontalScrollBarVisibility="Disabled">
                        <ListBox.ItemTemplate>
                            <DataTemplate>
                                <DockPanel Width="320">
                                    <Button DockPanel.Dock="Right" Style="{StaticResource FlatButton}" Content="Delete"
                                            Tag="{Binding}" Click="OnClosedDeleteClick" ToolTip="Move to the Recycle Bin" />
                                    <Button DockPanel.Dock="Right" Style="{StaticResource FlatButton}" Content="Reopen"
                                            Foreground="{DynamicResource Pad.Accent}" Tag="{Binding}" Click="OnClosedReopenClick" />
                                    <StackPanel>
                                        <TextBlock Text="{Binding Title}" TextTrimming="CharacterEllipsis" FontSize="12.5" />
                                        <TextBlock Text="{Binding Detail}" TextTrimming="CharacterEllipsis" FontSize="11" Foreground="{DynamicResource Pad.Muted}" />
                                    </StackPanel>
                                </DockPanel>
                            </DataTemplate>
                        </ListBox.ItemTemplate>
                    </ListBox>
                    <TextBlock x:Name="ClosedEmpty" Text="No closed notes." Foreground="{DynamicResource Pad.Muted}" Margin="4,8" Visibility="Collapsed" />
                </StackPanel>
            </Border>
        </Popup>
    </Grid>
</Window>
```

- [ ] **Step 5: Replace `Pad/FindReplaceBar.xaml` with**

```xml
<UserControl
    x:Class="Kil0bitSystemMonitor.Pad.FindReplaceBar"
    xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
    xmlns:ui="http://schemas.modernwpf.com/2019"
    Background="{DynamicResource Pad.Chrome}">

    <!-- Find and replace for MicaPad. AvalonEdit's own SearchPanel cannot replace, hence this.
         Colors are the MicaPad window's Pad.* brushes, so the bar follows its theme. -->

    <Border BorderBrush="{DynamicResource Pad.Border}" BorderThickness="0,0,0,1" Padding="10,6">
        <Grid>
            <Grid.RowDefinitions>
                <RowDefinition Height="Auto" />
                <RowDefinition Height="Auto" />
            </Grid.RowDefinitions>
            <Grid.ColumnDefinitions>
                <ColumnDefinition Width="280" />
                <ColumnDefinition Width="Auto" />
                <ColumnDefinition Width="*" />
                <ColumnDefinition Width="Auto" />
            </Grid.ColumnDefinitions>

            <TextBox x:Name="FindBox" Grid.Row="0" Grid.Column="0" ui:ControlHelper.PlaceholderText="Find"
                     TextChanged="OnFindTextChanged" KeyDown="OnFindKeyDown" />
            <StackPanel Grid.Row="0" Grid.Column="1" Orientation="Horizontal" Margin="6,0,0,0">
                <ToggleButton x:Name="CaseToggle" Content="Aa" ToolTip="Match case" Padding="8,4" Margin="0,0,4,0" Click="OnOptionChanged" />
                <ToggleButton x:Name="WordToggle" Content="W" ToolTip="Whole word" Padding="8,4" Margin="0,0,4,0" Click="OnOptionChanged" />
                <ToggleButton x:Name="RegexToggle" Content=".*" ToolTip="Regular expression" Padding="8,4" Margin="0,0,8,0" Click="OnOptionChanged" />
                <Button Content="&#xE74A;" FontFamily="Segoe Fluent Icons, Segoe MDL2 Assets" ToolTip="Previous (Shift+F3)"
                        Padding="8,4" Margin="0,0,4,0" Click="OnPreviousClick" />
                <Button Content="&#xE74B;" FontFamily="Segoe Fluent Icons, Segoe MDL2 Assets" ToolTip="Next (F3)"
                        Padding="8,4" Click="OnNextClick" />
            </StackPanel>
            <TextBlock x:Name="CountText" Grid.Row="0" Grid.Column="2" Margin="12,0" VerticalAlignment="Center"
                       FontSize="12" Foreground="{DynamicResource Pad.Muted}" TextTrimming="CharacterEllipsis" />
            <Button Grid.Row="0" Grid.Column="3" Content="&#xE711;" FontFamily="Segoe Fluent Icons, Segoe MDL2 Assets"
                    FontSize="10" ToolTip="Close (Esc)" Padding="8,6" Click="OnCloseClick" />

            <TextBox x:Name="ReplaceBox" Grid.Row="1" Grid.Column="0" Margin="0,6,0,0"
                     ui:ControlHelper.PlaceholderText="Replace" KeyDown="OnReplaceKeyDown" />
            <StackPanel x:Name="ReplaceButtons" Grid.Row="1" Grid.Column="1" Grid.ColumnSpan="2" Orientation="Horizontal" Margin="6,6,0,0">
                <Button x:Name="ReplaceButton" Content="Replace" Padding="10,4" Margin="0,0,6,0" Click="OnReplaceClick" />
                <Button x:Name="ReplaceAllButton" Content="Replace all" Padding="10,4" Click="OnReplaceAllClick" />
            </StackPanel>
        </Grid>
    </Border>
</UserControl>
```

- [ ] **Step 6: Change `Pad/FindReplaceBar.xaml.cs`**

1. Delete the two static fields `Muted` and `AlertRed` (lines 29–30) and the `Frozen` helper (around line 256) — nothing else uses them after the next change.
2. Replace every `CountText.Foreground = AlertRed;` (three places: in `Recompute`, `ShowTimedOut`, and the replace-all error path around line 221) with:

```csharp
                CountText.SetResourceReference(TextBlock.ForegroundProperty, "Pad.AlertRed");
```

3. Replace `CountText.Foreground = Muted;` in `UpdateCount` with:

```csharp
            CountText.SetResourceReference(TextBlock.ForegroundProperty, "Pad.Muted");
```

4. Add these members after `Attach`:

```csharp
        /// <summary>Takes the find-match color of the MicaPad theme and repaints the matches.</summary>
        public void ApplyPalette(PadPalette palette)
        {
            _highlighter.Fill = PadThemeApplier.ToBrush(palette.FindMatch);
            _editor?.TextArea.TextView.InvalidateLayer(_highlighter.Layer);
        }

        /// <summary>The brush behind each match, for tests.</summary>
        internal Brush MatchFill => _highlighter.Fill;
```

If the `using Color = System.Windows.Media.Color;` alias becomes unused, remove it (the build treats nothing as an error, but keep the file clean).

- [ ] **Step 7: Change `Pad/MatchHighlighter.cs`**

Replace

```csharp
        private static readonly Brush Fill = CreateFill();
```

with

```csharp
        /// <summary>The box behind each match; the MicaPad theme sets it (see <c>FindReplaceBar.ApplyPalette</c>).</summary>
        public Brush Fill { get; set; } = PadThemeApplier.ToBrush(PadPalette.Dark.FindMatch);
```

delete the `CreateFill` method, and remove the now-unused `using Color = System.Windows.Media.Color;` alias.

- [ ] **Step 8: Replace `Pad/HistoryPane.xaml` with**

```xml
<UserControl
    x:Class="Kil0bitSystemMonitor.Pad.HistoryPane"
    xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
    Width="280" Background="{DynamicResource Pad.Chrome}">

    <!-- A note's versions, newest first, grouped by day. Selecting one previews it in the window.
         Colors are the MicaPad window's Pad.* brushes, so the pane follows its theme. -->

    <Border BorderBrush="{DynamicResource Pad.Border}" BorderThickness="1,0,0,0">
        <DockPanel>
            <DockPanel DockPanel.Dock="Top" Margin="12,8,6,8">
                <Button DockPanel.Dock="Right" Content="&#xE711;" FontFamily="Segoe Fluent Icons, Segoe MDL2 Assets"
                        FontSize="10" Padding="8,6" ToolTip="Close (Ctrl+Shift+H)" Click="OnCloseClick" />
                <TextBlock Text="History" FontSize="14" FontWeight="SemiBold" VerticalAlignment="Center" />
            </DockPanel>
            <TextBlock x:Name="EmptyText" DockPanel.Dock="Top" Margin="12,0,12,8" TextWrapping="Wrap"
                       Foreground="{DynamicResource Pad.Muted}" FontSize="12" Visibility="Collapsed" />
            <ListBox x:Name="Versions" Background="Transparent" BorderThickness="0" IsSynchronizedWithCurrentItem="False"
                     ScrollViewer.HorizontalScrollBarVisibility="Disabled" SelectionChanged="OnSelectionChanged">
                <ListBox.GroupStyle>
                    <GroupStyle>
                        <GroupStyle.HeaderTemplate>
                            <DataTemplate>
                                <TextBlock Text="{Binding Name}" Margin="12,10,0,4" FontSize="11" FontWeight="SemiBold" Foreground="{DynamicResource Pad.Muted}" />
                            </DataTemplate>
                        </GroupStyle.HeaderTemplate>
                    </GroupStyle>
                </ListBox.GroupStyle>
                <ListBox.ItemTemplate>
                    <DataTemplate>
                        <DockPanel Margin="4,2">
                            <TextBlock DockPanel.Dock="Right" Text="{Binding Delta}" Foreground="{DynamicResource Pad.Muted}" FontSize="11" Margin="8,0,0,0" />
                            <TextBlock DockPanel.Dock="Right" Text="{Binding Size}" Foreground="{DynamicResource Pad.Muted}" FontSize="11" Margin="8,0,0,0" />
                            <TextBlock Text="{Binding Time}" FontSize="12.5" />
                        </DockPanel>
                    </DataTemplate>
                </ListBox.ItemTemplate>
            </ListBox>
        </DockPanel>
    </Border>
</UserControl>
```

- [ ] **Step 9: Change `Pad/MicaPadWindow.xaml.cs`**

1. Add the field after `private bool _exiting;`:

```csharp
        private PadPalette _palette = PadPalette.Dark;
```

2. In the constructor, after `FindBar.Attach(Editor);`, add:

```csharp
            ApplyTheme();
```

3. Replace the body of `ConfigureEditor` from `var area = Editor.TextArea;` to its end with (the colors move to `ApplyTheme`):

```csharp
            var area = Editor.TextArea;
            area.SelectionForeground = null;
            area.SelectionBorder = null;
            area.SelectionCornerRadius = 0;
            area.TextView.CurrentLineBorder = new Pen(Brushes.Transparent, 0);
```

4. Add after `ConfigureEditor`:

```csharp
        /// <summary>The palette MicaPad is painted with.</summary>
        internal PadPalette Palette => _palette;

        /// <summary>
        /// Paints MicaPad in the theme the config names: the Pad.* brushes the XAML reads, the
        /// ModernWpf controls, the editor and find highlights, the theme button and the title bar.
        /// Only this window changes; the rest of MicaStats keeps its look.
        /// </summary>
        private void ApplyTheme()
        {
            _palette = PadPalette.For(_config.PadTheme);
            PadThemeApplier.ApplyResources(Resources, _palette);
            ModernWpf.ThemeManager.SetRequestedTheme(this, _palette.IsDark ? ModernWpf.ElementTheme.Dark : ModernWpf.ElementTheme.Light);

            var area = Editor.TextArea;
            area.SelectionBrush = PadThemeApplier.ToBrush(_palette.Selection);
            area.Caret.CaretBrush = PadThemeApplier.ToBrush(_palette.Caret);
            area.TextView.CurrentLineBackground = PadThemeApplier.ToBrush(_palette.CurrentLine);
            Editor.LineNumbersForeground = PadThemeApplier.ToBrush(_palette.LineNumbers);
            PreviewEditor.LineNumbersForeground = Editor.LineNumbersForeground;
            PreviewEditor.TextArea.SelectionBrush = area.SelectionBrush;
            FindBar.ApplyPalette(_palette);

            ThemeButton.Content = _palette.IsDark ? "" : "";
            ThemeButton.ToolTip = _palette.IsDark ? "Switch to light theme" : "Switch to dark theme";
            PadThemeApplier.ApplyTitleBar(this, _palette.IsDark);
        }

        /// <summary>The theme button: switches between dark and light, remembered in the config.</summary>
        internal void ToggleTheme() => _config.PadTheme = _palette.IsDark ? PadThemes.Light : PadThemes.Dark;

        private void OnThemeButtonClick(object sender, RoutedEventArgs e) => ToggleTheme();
```

5. In `OnConfigChanged`, add a second branch after the existing `if` block:

```csharp
            else if (e.PropertyName == nameof(AppConfig.PadTheme))
            {
                if (Dispatcher.CheckAccess()) ApplyTheme();
                else Dispatcher.BeginInvoke(new Action(ApplyTheme));
            }
```

6. Make `NewMenu` an instance method that follows the theme:

```csharp
        internal ContextMenu NewMenu(UIElement target, PlacementMode placement)
        {
            var menu = new ContextMenu { PlacementTarget = target, Placement = placement };
            ModernWpf.ThemeManager.SetRequestedTheme(menu, _palette.IsDark ? ModernWpf.ElementTheme.Dark : ModernWpf.ElementTheme.Light);
            return menu;
        }
```

7. In `UpdateSaveText`, replace `SaveText.Foreground = (Brush)FindResource("AlertRed");` with `SaveText.SetResourceReference(TextBlock.ForegroundProperty, "Pad.AlertRed");` and `SaveText.Foreground = (Brush)FindResource("Muted");` with `SaveText.SetResourceReference(TextBlock.ForegroundProperty, "Pad.Muted");`.

8. At the end of `OnSourceInitialized`, add:

```csharp
            PadThemeApplier.ApplyTitleBar(this, _palette.IsDark);
```

9. If `using Brush = System.Windows.Media.Brush;` or `using Color = System.Windows.Media.Color;` become unused, remove them.

- [ ] **Step 10: Add the theme choice to Settings → MicaPad**

In `SettingsWindow.xaml`, inside `PadSection`, insert this card directly before the *Word wrap* card (the `Border` whose `FontIcon` glyph is `&#xE751;`):

```xml
                        <Border Background="{DynamicResource SystemControlBackgroundChromeMediumLowBrush}" BorderBrush="{DynamicResource SystemControlElevationBorderBrush}" BorderThickness="1" CornerRadius="8" Margin="0,0,0,12" Padding="20,16">
                            <Grid>
                                <Grid.ColumnDefinitions>
                                    <ColumnDefinition Width="Auto" />
                                    <ColumnDefinition Width="*" />
                                    <ColumnDefinition Width="Auto" />
                                </Grid.ColumnDefinitions>
                                <ui:FontIcon Glyph="&#xE706;" FontSize="20" Foreground="{DynamicResource SystemAccentColorBrush}" Margin="0,0,20,0" VerticalAlignment="Center"/>
                                <StackPanel Grid.Column="1" VerticalAlignment="Center" Margin="0,0,16,0">
                                    <TextBlock Text="Theme" FontWeight="SemiBold" FontSize="15"/>
                                    <TextBlock Text="Dark or light, for MicaPad only. The sun and moon button in MicaPad switches it too." Opacity="0.6" FontSize="12.5" TextWrapping="Wrap"/>
                                </StackPanel>
                                <ComboBox Grid.Column="2" x:Name="PadThemeBox" Width="120" VerticalAlignment="Center" SelectionChanged="OnPadThemeChanged">
                                    <ComboBoxItem Content="Dark"/>
                                    <ComboBoxItem Content="Light"/>
                                </ComboBox>
                            </Grid>
                        </Border>
```

In `SettingsWindow.xaml.cs`, in `LoadPadSettings`, after `PadLineNumbersToggle.IsOn = cfg.PadShowLineNumbers;` add:

```csharp
                PadThemeBox.SelectedIndex = cfg.PadTheme == Kil0bitSystemMonitor.Services.Pad.PadThemes.Light ? 1 : 0;
```

and after `OnPadHistoryChanged` add:

```csharp
        private void OnPadThemeChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_loadingPad || PadThemeBox.SelectedIndex < 0) return;
            _config.Config.PadTheme = PadThemeBox.SelectedIndex == 1
                ? Kil0bitSystemMonitor.Services.Pad.PadThemes.Light
                : Kil0bitSystemMonitor.Services.Pad.PadThemes.Dark;
            _config.SaveConfig();
        }
```

- [ ] **Step 11: Run the new tests, then the whole suite**

Run: `... test ... --filter "FullyQualifiedName~PadThemeTests" -nologo` — expected PASS.
Then the whole suite — expected all pass (existing `PadWindowTests` must still pass: the window still loads and behaves the same in the dark theme).

- [ ] **Step 12: Document it**

In `GUIDE.md`, in the `## 📝 MicaPad` section, insert before `### Where notes live`:

```markdown
### Light or dark

The sun button in MicaPad's tab strip switches MicaPad to a light theme; the moon button switches
it back. Only MicaPad changes — Settings and the rest of MicaStats keep their look. The choice is
remembered, and is also in **Settings → MicaPad → Theme**.

```

In `README.md`, in `### MicaPad: a notepad that never asks to save`, insert before the bullet that starts `* Notes are plain text files`:

```markdown
* **Light or dark**: MicaPad has its own theme switch (the sun and moon button), independent of the rest of MicaStats
```

and in the Thai section `### MicaPad: โน้ตแพดที่ไม่เคยถามให้บันทึก`, before the bullet that starts `* โน้ตเก็บเป็นไฟล์ข้อความธรรมดา`:

```markdown
* **ธีมสว่างหรือมืด**: MicaPad มีปุ่มสลับธีมของตัวเอง (ปุ่มดวงอาทิตย์และพระจันทร์) โดยส่วนอื่นของ MicaStats ไม่เปลี่ยน
```

- [ ] **Step 13: Commit**

```bash
git add Pad/PadThemeApplier.cs Pad/MicaPadWindow.xaml Pad/MicaPadWindow.xaml.cs Pad/FindReplaceBar.xaml Pad/FindReplaceBar.xaml.cs Pad/HistoryPane.xaml Pad/MatchHighlighter.cs SettingsWindow.xaml SettingsWindow.xaml.cs GUIDE.md README.md tests/Kil0bitSystemMonitor.Tests/PadThemeTests.cs
git commit -m "feat(pad): light and dark theme for MicaPad, switched live" -m "Co-Authored-By: <your model name> <noreply@anthropic.com>"
```

---

### Task 3: Right-click menu in the editor and the history preview

**Files:**
- Modify: `Pad/MicaPadWindow.xaml.cs` (constructor, `Item`, new menu members)
- Create: `tests/Kil0bitSystemMonitor.Tests/PadMenuTests.cs`

**Interfaces:**
- Consumes (Task 2): `MicaPadWindow.Palette`, instance `NewMenu`.
- Produces:
  - `internal ContextMenu EditorMenu { get; }`, `internal ContextMenu PreviewMenu { get; }`
  - `internal void RefreshEditorMenu()`, `internal void RefreshPreviewMenu()`
  - `internal void PlaceCaretForMenu(int offset)`
  - `Item(string header, string? gesture, Action action, bool enabled = true)` (existing helper gains `enabled`). Parts 2–5 add groups to `FillEditorMenu`.

- [ ] **Step 1: Write the failing tests**

Create `tests/Kil0bitSystemMonitor.Tests/PadMenuTests.cs`:

```csharp
using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Kil0bitSystemMonitor.Models;
using Kil0bitSystemMonitor.Pad;
using Kil0bitSystemMonitor.Services.Pad;
using Xunit;

namespace Kil0bitSystemMonitor.Tests
{
    /// <summary>MicaPad's right-click menus: which items there are, when they are enabled, and what they do.</summary>
    public class PadMenuTests
    {
        internal static void WithWindow(Action<MicaPadWindow, PadTestEnv> test) => UiThread.Run(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            using var env = new PadTestEnv(post: action => dispatcher.BeginInvoke(action));
            var window = new MicaPadWindow(env.Workspace, new AppConfig());
            try
            {
                window.LoadSession();
                test(window, env);
            }
            finally
            {
                window.CloseForExit();
            }
        });

        /// <summary>Headers in order, with "-" for each separator.</summary>
        internal static string[] Headers(ContextMenu menu) =>
            menu.Items.Cast<object>().Select(i => i is MenuItem m ? (string)m.Header : "-").ToArray();

        internal static MenuItem ItemOf(ContextMenu menu, string header) =>
            menu.Items.OfType<MenuItem>().Single(m => (string)m.Header == header);

        internal static void Click(MenuItem item) => item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));

        [Fact]
        public void The_editor_menu_lists_edit_then_find_items() => WithWindow((window, env) =>
        {
            window.RefreshEditorMenu();

            Assert.Equal(new[] { "Undo", "Redo", "-", "Cut", "Copy", "Paste", "Delete", "Select all", "-", "Find", "Replace", "Go to line…" },
                         Headers(window.EditorMenu));
            Assert.Equal("Ctrl+Z", ItemOf(window.EditorMenu, "Undo").InputGestureText);
            Assert.Equal("Ctrl+G", ItemOf(window.EditorMenu, "Go to line…").InputGestureText);
        });

        [Fact]
        public void With_no_text_and_no_selection_only_what_can_apply_is_enabled() => WithWindow((window, env) =>
        {
            window.RefreshEditorMenu();
            var menu = window.EditorMenu;

            Assert.False(ItemOf(menu, "Undo").IsEnabled);
            Assert.False(ItemOf(menu, "Redo").IsEnabled);
            Assert.False(ItemOf(menu, "Cut").IsEnabled);
            Assert.False(ItemOf(menu, "Copy").IsEnabled);
            Assert.False(ItemOf(menu, "Delete").IsEnabled);
            Assert.False(ItemOf(menu, "Select all").IsEnabled);
            Assert.True(ItemOf(menu, "Find").IsEnabled);
        });

        [Fact]
        public void A_selection_enables_cut_copy_and_delete() => WithWindow((window, env) =>
        {
            window.Editor.Document.Insert(0, "hello world");
            window.Editor.Select(0, 5);

            window.RefreshEditorMenu();
            var menu = window.EditorMenu;

            Assert.True(ItemOf(menu, "Undo").IsEnabled);
            Assert.True(ItemOf(menu, "Cut").IsEnabled);
            Assert.True(ItemOf(menu, "Copy").IsEnabled);
            Assert.True(ItemOf(menu, "Delete").IsEnabled);
            Assert.True(ItemOf(menu, "Select all").IsEnabled);
        });

        [Fact]
        public void Delete_removes_the_selection_as_one_undo_step() => WithWindow((window, env) =>
        {
            window.Editor.Document.Insert(0, "hello world");
            window.Editor.Select(0, 6);
            window.RefreshEditorMenu();

            Click(ItemOf(window.EditorMenu, "Delete"));
            Assert.Equal("world", window.Editor.Document.Text);

            window.Editor.Undo();
            Assert.Equal("hello world", window.Editor.Document.Text);
        });

        [Fact]
        public void Select_all_and_find_do_what_they_say() => WithWindow((window, env) =>
        {
            window.Editor.Document.Insert(0, "abc");
            window.RefreshEditorMenu();

            Click(ItemOf(window.EditorMenu, "Select all"));
            Assert.Equal(3, window.Editor.SelectionLength);

            Click(ItemOf(window.EditorMenu, "Find"));
            Assert.True(window.FindBar.IsOpen);
        });

        [Fact]
        public void A_right_click_inside_the_selection_keeps_it() => WithWindow((window, env) =>
        {
            window.Editor.Document.Insert(0, "hello world");
            window.Editor.Select(0, 5);

            window.PlaceCaretForMenu(3);

            Assert.Equal(0, window.Editor.SelectionStart);
            Assert.Equal(5, window.Editor.SelectionLength);
        });

        [Fact]
        public void A_right_click_outside_the_selection_moves_the_caret() => WithWindow((window, env) =>
        {
            window.Editor.Document.Insert(0, "hello world");
            window.Editor.Select(0, 5);

            window.PlaceCaretForMenu(8);

            Assert.Equal(0, window.Editor.SelectionLength);
            Assert.Equal(8, window.Editor.CaretOffset);

            window.PlaceCaretForMenu(500);   // past the end: clamped
            Assert.Equal(11, window.Editor.CaretOffset);
        });

        [Fact]
        public void The_preview_menu_only_copies() => WithWindow((window, env) =>
        {
            window.PreviewEditor.Text = "old version";
            window.RefreshPreviewMenu();

            Assert.Equal(new[] { "Copy", "Select all" }, Headers(window.PreviewMenu));
            Assert.False(ItemOf(window.PreviewMenu, "Copy").IsEnabled);
            Assert.True(ItemOf(window.PreviewMenu, "Select all").IsEnabled);
        });

        [Fact]
        public void The_editor_menu_follows_the_theme() => UiThread.Run(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            using var env = new PadTestEnv(post: action => dispatcher.BeginInvoke(action));
            var window = new MicaPadWindow(env.Workspace, new AppConfig { PadTheme = "Light" });
            try
            {
                window.LoadSession();
                window.RefreshEditorMenu();
                Assert.Equal(ModernWpf.ElementTheme.Light, ModernWpf.ThemeManager.GetRequestedTheme(window.EditorMenu));
            }
            finally
            {
                window.CloseForExit();
            }
        });
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `... test ... --filter "FullyQualifiedName~PadMenuTests" -nologo`
Expected: build FAILS (`'MicaPadWindow' does not contain a definition for 'RefreshEditorMenu'`, `'EditorMenu'`, `'PlaceCaretForMenu'`, `'RefreshPreviewMenu'`, `'PreviewMenu'`).

- [ ] **Step 3: Implement the menus in `Pad/MicaPadWindow.xaml.cs`**

1. Give the existing `Item` helper an `enabled` parameter:

```csharp
        private static MenuItem Item(string header, string? gesture, Action action, bool enabled = true)
        {
            var item = new MenuItem { Header = header, InputGestureText = gesture ?? "", IsEnabled = enabled };
            item.Click += (s, e) => action();
            return item;
        }
```

2. In the constructor, after `ApplyTheme();`, add:

```csharp
            Editor.ContextMenu = EditorMenu;
            Editor.ContextMenuOpening += (s, e) => RefreshEditorMenu();
            Editor.TextArea.PreviewMouseRightButtonDown += OnEditorRightButtonDown;
            PreviewEditor.ContextMenu = PreviewMenu;
            PreviewEditor.ContextMenuOpening += (s, e) => RefreshPreviewMenu();
```

3. Add a new region after the `☰` menu helpers (after `Check`):

```csharp
        // ---- right-click menus --------------------------------------------------------------

        /// <summary>The editor's right-click menu. One instance; its items are rebuilt each time it opens.</summary>
        internal ContextMenu EditorMenu { get; } = new();

        /// <summary>The history preview's right-click menu: copying only, because the preview is read-only.</summary>
        internal ContextMenu PreviewMenu { get; } = new();

        /// <summary>Rebuilds the editor menu for the current selection, undo state and theme.</summary>
        internal void RefreshEditorMenu() => FillEditorMenu(EditorMenu, Editor, readOnly: false);

        /// <summary>Rebuilds the history preview's menu.</summary>
        internal void RefreshPreviewMenu() => FillEditorMenu(PreviewMenu, PreviewEditor, readOnly: true);

        /// <summary>
        /// The items of a text menu, each disabled when it cannot apply. Later parts add their groups
        /// (Format, Lines, Tools) here, before the Find group.
        /// </summary>
        private void FillEditorMenu(ContextMenu menu, ICSharpCode.AvalonEdit.TextEditor editor, bool readOnly)
        {
            menu.Items.Clear();
            ModernWpf.ThemeManager.SetRequestedTheme(menu, _palette.IsDark ? ModernWpf.ElementTheme.Dark : ModernWpf.ElementTheme.Light);

            bool hasSelection = editor.SelectionLength > 0;
            bool hasText = editor.Document != null && editor.Document.TextLength > 0;

            if (!readOnly)
            {
                menu.Items.Add(Item("Undo", "Ctrl+Z", () => editor.Undo(), editor.CanUndo));
                menu.Items.Add(Item("Redo", "Ctrl+Y", () => editor.Redo(), editor.CanRedo));
                menu.Items.Add(new Separator());
                menu.Items.Add(Item("Cut", "Ctrl+X", () => editor.Cut(), hasSelection));
            }
            menu.Items.Add(Item("Copy", "Ctrl+C", () => editor.Copy(), hasSelection));
            if (!readOnly)
            {
                menu.Items.Add(Item("Paste", "Ctrl+V", () => editor.Paste(), ClipboardHasText()));
                menu.Items.Add(Item("Delete", "Del", () => editor.SelectedText = "", hasSelection));
            }
            menu.Items.Add(Item("Select all", "Ctrl+A", () => editor.SelectAll(), hasText));

            if (!readOnly)
            {
                menu.Items.Add(new Separator());
                menu.Items.Add(Item("Find", "Ctrl+F", () => FindBar.Open(replace: false)));
                menu.Items.Add(Item("Replace", "Ctrl+H", () => FindBar.Open(replace: true)));
                menu.Items.Add(Item("Go to line…", "Ctrl+G", ShowGoToLine));
            }
        }

        /// <summary>True when Paste has something to paste. A busy clipboard counts as yes: Paste itself then tries.</summary>
        private static bool ClipboardHasText()
        {
            try
            {
                return Clipboard.ContainsText();
            }
            catch (System.Runtime.InteropServices.ExternalException)
            {
                return true;
            }
        }

        private void OnEditorRightButtonDown(object sender, MouseButtonEventArgs e)
        {
            var position = Editor.GetPositionFromPoint(e.GetPosition(Editor));
            if (position is { } at) PlaceCaretForMenu(Editor.Document.GetOffset(at.Location));
        }

        /// <summary>
        /// A right-click moves the caret to the click, like Notepad, unless it lands inside the
        /// selection: then the selection stays, so Cut and Copy act on it.
        /// </summary>
        internal void PlaceCaretForMenu(int offset)
        {
            offset = Math.Clamp(offset, 0, Editor.Document.TextLength);
            int start = Editor.SelectionStart;
            int end = start + Editor.SelectionLength;
            if (Editor.SelectionLength > 0 && offset >= start && offset <= end) return;

            Editor.TextArea.ClearSelection();
            Editor.CaretOffset = offset;
        }
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `... test ... --filter "FullyQualifiedName~PadMenuTests" -nologo` — expected PASS. Then the whole suite — expected all pass.

- [ ] **Step 5: Commit**

```bash
git add Pad/MicaPadWindow.xaml.cs tests/Kil0bitSystemMonitor.Tests/PadMenuTests.cs
git commit -m "feat(pad): right-click menu in the editor and the history preview" -m "Co-Authored-By: <your model name> <noreply@anthropic.com>"
```

---

### Task 4: Right-click menu on tabs

**Files:**
- Modify: `Pad/MicaPadWindow.xaml` (the `TabBorder` in `TabTemplate`)
- Modify: `Pad/MicaPadWindow.xaml.cs` (new tab-menu members)
- Modify: `tests/Kil0bitSystemMonitor.Tests/PadMenuTests.cs`
- Modify: `GUIDE.md`, `README.md`

**Interfaces:**
- Consumes (Tasks 2–3): instance `NewMenu`, `Item(..., enabled)`, `PadMenuTests.WithWindow/Headers/ItemOf/Click`.
- Produces: `internal ContextMenu BuildTabMenu(OpenNote note, FrameworkElement? anchor)`, `internal void CloseOtherTabs(OpenNote keep)`, `internal void ShowInFolder(string path)`.

- [ ] **Step 1: Write the failing tests**

Add to `PadMenuTests` (inside the class):

```csharp
        [Fact]
        public void A_note_tab_menu_has_rename_close_and_close_others() => WithWindow((window, env) =>
        {
            var note = env.Workspace.Open[0];

            var menu = window.BuildTabMenu(note, null);

            Assert.Equal(new[] { "Rename…", "Close", "Close other tabs" }, Headers(menu));
            Assert.False(ItemOf(menu, "Close other tabs").IsEnabled);   // it is the only tab
        });

        [Fact]
        public void A_file_tab_menu_adds_copy_path_and_show_in_folder() => WithWindow((window, env) =>
        {
            string path = env.FileOf("notes.txt");
            File.WriteAllText(path, "text");
            window.OpenPath(path);
            var note = env.Workspace.Active!;

            var menu = window.BuildTabMenu(note, null);

            Assert.Equal(new[] { "Rename…", "Close", "Close other tabs", "-", "Copy file path", "Show in folder" }, Headers(menu));
            Assert.True(ItemOf(menu, "Close other tabs").IsEnabled);
        });

        [Fact]
        public void Close_from_the_tab_menu_closes_that_tab_only() => WithWindow((window, env) =>
        {
            var first = env.Workspace.Open[0];
            window.Editor.Document.Insert(0, "first");
            window.NewTab();
            window.Editor.Document.Insert(0, "second");
            var second = env.Workspace.Active!;

            Click(ItemOf(window.BuildTabMenu(first, null), "Close"));

            Assert.Same(second, Assert.Single(env.Workspace.Open));
            Assert.Equal("second", window.Editor.Document.Text);
        });

        [Fact]
        public void Close_other_tabs_keeps_every_note_recoverable() => WithWindow((window, env) =>
        {
            window.Editor.Document.Insert(0, "scratch one");
            var scratch = env.Workspace.Active!;

            string path = env.FileOf("edited.txt");
            File.WriteAllText(path, "on disk");
            window.OpenPath(path);
            window.Editor.Document.Insert(window.Editor.Document.TextLength, " plus my edit");
            var file = env.Workspace.Active!;

            window.NewTab();
            window.Editor.Document.Insert(0, "keep me");
            var keep = env.Workspace.Active!;

            Click(ItemOf(window.BuildTabMenu(keep, null), "Close other tabs"));
            env.Flush();

            Assert.Same(keep, Assert.Single(env.Workspace.Open));
            Assert.Equal("keep me", window.Editor.Document.Text);
            var closedIds = env.Workspace.ClosedNotes().Select(m => m.Id).ToList();
            Assert.Contains(scratch.Id, closedIds);
            Assert.Contains(file.Id, closedIds);
            Assert.Equal("on disk", File.ReadAllText(path));   // closing never writes the real file

            var back = env.Workspace.Reopen(file.Id);
            Assert.NotNull(back);
            Assert.Equal("on disk plus my edit", back!.TextProvider());
        });

        [Fact]
        public void Rename_from_the_tab_menu_opens_the_rename_box() => WithWindow((window, env) =>
        {
            window.Editor.Document.Insert(0, "Shopping list");
            var note = env.Workspace.Active!;

            Click(ItemOf(window.BuildTabMenu(note, null), "Rename…"));

            Assert.True(window.RenamePopup.IsOpen);
            Assert.Equal(note.Title, window.RenameBox.Text);
        });

        [Fact]
        public void Show_in_folder_on_a_missing_file_says_so() => WithWindow((window, env) =>
        {
            string path = env.FileOf("gone.txt");

            window.ShowInFolder(path);

            Assert.Equal(Visibility.Visible, window.InfoBar.Visibility);
            Assert.Equal("That file is no longer at " + path + ".", window.InfoText.Text);
        });
```

(`ClosedNotes()` returns `IReadOnlyList<NoteMeta>`; `NoteMeta.Id` and `OpenNote.Id` are the same note id, and `PadWorkspace.Reopen(string id)` returns the reopened `OpenNote` or null.)

- [ ] **Step 2: Run the tests to verify they fail**

Run: `... test ... --filter "FullyQualifiedName~PadMenuTests" -nologo`
Expected: build FAILS (`'MicaPadWindow' does not contain a definition for 'BuildTabMenu'`, `'ShowInFolder'`).

- [ ] **Step 3: Hook the right button on tabs**

In `Pad/MicaPadWindow.xaml`, on the `TabBorder` in `TabTemplate`, add `MouseRightButtonUp="OnTabMouseRightButtonUp"` after `MouseUp="OnTabMouseUp"`.

- [ ] **Step 4: Implement the tab menu in `Pad/MicaPadWindow.xaml.cs`**

Add after `OnTabCloseClick`:

```csharp
        /// <summary>Right-click on a tab: its menu, without switching to it first.</summary>
        private void OnTabMouseRightButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (sender is not FrameworkElement anchor || anchor.Tag is not OpenNote note) return;
            var menu = BuildTabMenu(note, anchor);
            menu.IsOpen = true;
            e.Handled = true;
        }

        /// <summary>
        /// A tab's right-click menu. Closing from it is the ordinary close: nothing is deleted and
        /// nothing is asked. A tab backed by a real file adds its path and folder.
        /// </summary>
        internal ContextMenu BuildTabMenu(OpenNote note, FrameworkElement? anchor)
        {
            FrameworkElement target = anchor ?? TabStrip;
            var menu = NewMenu(target, PlacementMode.MousePoint);
            menu.Items.Add(Item("Rename…", null, () => BeginRename(note, target)));
            menu.Items.Add(Item("Close", null, () => CloseTab(note)));
            menu.Items.Add(Item("Close other tabs", null, () => CloseOtherTabs(note), _workspace.Open.Count > 1));

            if (note.Meta.SourcePath is string path)
            {
                menu.Items.Add(new Separator());
                menu.Items.Add(Item("Copy file path", null, () => CopyFilePath(path)));
                menu.Items.Add(Item("Show in folder", null, () => ShowInFolder(path)));
            }
            return menu;
        }

        /// <summary>
        /// Closes every tab except <paramref name="keep"/>, each as an ordinary close: flushed,
        /// snapshotted and moved to Closed notes, with a file's unsaved edits kept.
        /// </summary>
        internal void CloseOtherTabs(OpenNote keep)
        {
            ShowNote(keep);
            foreach (var other in new List<OpenNote>(_workspace.Open))
                if (!ReferenceEquals(other, keep)) CloseTab(other);
        }

        private void CopyFilePath(string path)
        {
            try
            {
                Clipboard.SetText(path);
            }
            catch (System.Runtime.InteropServices.ExternalException ex)
            {
                DiagnosticsLog.Warn("pad", "Copying a file path failed: " + ex.Message);
                ShowInfo("The clipboard is busy. Try again in a moment.", null);
            }
        }

        /// <summary>
        /// Opens Explorer with the file selected. A file that is no longer there is reported rather
        /// than opening some other folder.
        /// </summary>
        internal void ShowInFolder(string path)
        {
            if (!File.Exists(path))
            {
                ShowInfo("That file is no longer at " + path + ".", null);
                return;
            }
            try
            {
                Process.Start(new ProcessStartInfo("explorer.exe", "/select,\"" + path + "\"") { UseShellExecute = true });
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                DiagnosticsLog.Warn("pad", "Could not show a file in its folder: " + ex.Message);
                ShowInfo("Explorer could not be opened.", null);
            }
        }
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `... test ... --filter "FullyQualifiedName~PadMenuTests" -nologo` — expected PASS. Then the whole suite — expected all pass.

- [ ] **Step 6: Document the menus**

In `GUIDE.md`, `## 📝 MicaPad`, insert before `### Where notes live` (after the *Light or dark* section from Task 2):

```markdown
### Right-click menus

Right-click in the text for **Undo**, **Redo**, **Cut**, **Copy**, **Paste**, **Delete**,
**Select all**, **Find**, **Replace** and **Go to line**. The caret moves to where you clicked,
unless you click inside the selection — then the selection stays, so Cut and Copy act on it.
Right-click a history version for **Copy** and **Select all**.

Right-click a tab for **Rename**, **Close** and **Close other tabs** (closed tabs go to Closed notes
as usual — nothing is deleted). A tab that is a real file also has **Copy file path** and
**Show in folder**.

```

In `README.md`, after the *Light or dark* bullet from Task 2, add:

```markdown
* **Right-click menus** in the text (cut, copy, paste, find) and on tabs (rename, close others, copy the file path, show in folder)
```

and in the Thai section, after the *ธีมสว่างหรือมืด* bullet:

```markdown
* **เมนูคลิกขวา** ในเนื้อความ (ตัด คัดลอก วาง ค้นหา) และบนแท็บ (เปลี่ยนชื่อ ปิดแท็บอื่น คัดลอกพาธไฟล์ เปิดโฟลเดอร์ที่เก็บไฟล์)
```

- [ ] **Step 7: Commit**

```bash
git add Pad/MicaPadWindow.xaml Pad/MicaPadWindow.xaml.cs tests/Kil0bitSystemMonitor.Tests/PadMenuTests.cs GUIDE.md README.md
git commit -m "feat(pad): right-click menu on tabs" -m "Co-Authored-By: <your model name> <noreply@anthropic.com>"
```

---

## After the plan (controller)

1. Whole suite green; build with 0 warnings.
2. Deploy for the owner's e2e (standing rule): stop MicaStats, build Release into `bin\Release\net8.0-windows`, relaunch.
3. Owner's manual checks for Part 1 (spec *Testing → Manual* items 1 and 2): switch the theme with tabs, find bar, history and popups open; restart; Settings and other MicaStats windows unchanged; title bar follows. Every right-click item on a scratch note and on a real file; Show in folder selects the file.
