# CPMigrate design system

> Source of truth for the site in `site/`. Written 2026-08-01.

## Thesis

**A precision instrument, documented beautifully.**

Blackened steel and warm paper, set like a technical manual that someone cared
about. The energy comes from typography, hairlines and live data — never from
glow.

The previous design was terminal cosplay: CRT scanlines, phosphor bloom, three
competing neons. It signalled "retro hacker". This product's actual promise is
*certainty* — one file holds every version, and it reverts the moment
`dotnet test` goes red. That deserves the visual language of instrumentation and
specification, not nostalgia.

**The one thing to remember:** this tool tells you the truth about your
dependency graph, and undoes itself when it is wrong.

## Typography

A serif display is the deliberate departure. Every developer-tool landing page
uses a grotesk; a high-contrast serif reads as *document*, *specification*,
*authority* — which is what a man page and an exit-code contract actually are.

| Role | Face | Notes |
|------|------|-------|
| Display | **Instrument Serif** 400 + italic | Headlines only. Tight tracking, huge sizes. The italic is the accent voice. |
| Interface | **Instrument Sans** 400/500/600 | Body, nav, buttons, labels. Pairs by design with the serif. |
| Technical | **JetBrains Mono** 400/500/700 | Commands, package names, versions, status output, all metadata. |

Loaded via `<link>` in each page head — never `@import`, which costs a
round-trip before any typeface starts downloading.

Scale (1.25 modular, 16px base):

```
--t-xs:   0.75rem     labels, chips, legends
--t-sm:   0.875rem    mono body, table cells
--t-base: 1rem        body
--t-md:   1.125rem    lede
--t-lg:   1.5rem      h3 / card titles
--t-xl:   2rem        h2 small
--t-2xl:  clamp(2.2rem, 4.2vw, 3.4rem)   h2
--t-hero: clamp(3rem, 7.5vw, 6.5rem)     h1
```

Headings use `text-wrap: balance`. Body measure caps at 68ch.

## Colour

Warm near-black, not blue-black. Blue-black plus neon is the generic dev-tool
look; warm charcoal with a single high-chroma signal reads as expensive.

```css
--bg:        #0B0B0C;  /* warm near-black canvas */
--surface:   #111113;
--surface-2: #17171A;
--elevated:  #1E1E22;
--line:      #26262B;  /* hairlines */
--line-2:    #33333A;
--text:      #F4F2EE;  /* warm paper white */
--muted:     #9C9A94;
--faint:     #6A6862;
--accent:    #FFB020;  /* signal amber — the identity colour */
--accent-2:  #FFD98A;
```

**Colour discipline:** amber is the only brand colour. Everything else must mean
an operational state, and severity collapses to three values instead of five:

```css
--red:   #FF5F52;  /* critical, high — failure or blocked */
--amber: #FFB020;  /* moderate — needs attention (same hue as identity, deliberately) */
--green: #7FD98A;  /* pass, verified */
--cyan:  #6FD3D8;  /* identifier — package and symbol names inside code only */
```

Low and info severities render as neutral chips with no hue. Previously every
analyzer row carried its own colour, which made the list read as noise; now the
serious ones are the only coloured things on the page, so they actually land.
The ticker dots use the same three-value ramp as the rows they preview.

**Cyan is a data colour, never chrome.** It marks identifiers inside terminal
output, code blocks and the exit-code man page. It must not appear on hover
states, borders, handles, buttons or any other interface furniture — those take
the accent. There are no `--pink*` tokens; if you find one, it is drift.

Code surfaces use `--code-bg: #08080A` and `--ink-2` for body text. Never a
blue-black; it reads cold against the warm canvas.

`color-scheme: dark` so the UA draws scrollbars and video controls to match.

## Surface and texture

Replace the CRT scanline veil and dot grid with a single restrained technical
grid — 48px, barely visible — plus one soft amber bloom anchored top-right.

```css
background:
  linear-gradient(var(--grid) 1px, transparent 1px),
  linear-gradient(90deg, var(--grid) 1px, transparent 1px),
  radial-gradient(900px 600px at 82% -8%, rgba(255,176,32,.07), transparent 60%),
  var(--bg);
background-size: 48px 48px, 48px 48px, 100% 100%, 100% 100%;
```

Depth comes from composition and hairlines, not drop shadows. Radii stay tight
(4px / 10px) — a bubbly uniform radius on everything is a slop signal.

## Layout

**Spacing:** 8px base. Sections 120px desktop, 72px mobile. Shell caps at
1240px.

**First viewport as a poster, not a document.** Left-aligned, oversized serif
statement occupying most of the fold, a single mono spec-strip, two actions.
The interactive terminal becomes a full-bleed instrument panel immediately
below, overlapping the hero boundary — depth through composition rather than a
50/50 column split competing with the headline.

**Evidence before explanation.** The analyzer scoreboard and the migration diff
lead; feature descriptions decode them afterwards.

**One job per section.** Section heads carry a stamped mono index
(`01 / ANALYSIS`) rather than an oversized ghost numeral behind the text.

## Motion

Three, each communicating something. Everything respects
`prefers-reduced-motion`.

1. **Headline mask-reveal** on load — lines rise from behind a clip, staggered
   ~90ms. Says: this was composed.
2. **Hairline draw** — section rules scale from 0 to full width on entry. Says:
   structure, measurement.
3. **Count-up and meter fill** — the scoreboard resolves to its real numbers.
   Says: this is measured, not asserted.

Dropped: scanline pulse, phosphor blink, glow throbs. Reveals trigger 200px
ahead of the viewport so no one meets a blank screen.

## Anti-slop rules

No purple gradients. No three-column icon-in-circle grid. No centred-everything.
No decorative blobs or wavy dividers. No glassmorphism. No emoji as design
elements. No `system-ui` as a display face. Every colour means an operational
state; every large number corresponds to a real capability.

## Accessibility

Body text ≥16px. Interactive targets ≥44px. Visible `:focus-visible` ring in
amber. Severity never encoded by colour alone — every chip carries its label.
Contrast: `--text` on `--bg` is ~16:1; `--muted` on `--bg` ~6.4:1; amber on
`--bg` ~10:1.
