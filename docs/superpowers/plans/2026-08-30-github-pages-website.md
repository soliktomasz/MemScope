# MemScope GitHub Pages Website Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build a polished, accessible MemScope landing page that deploys directly from `docs/` through GitHub Pages without screenshots or a frontend build step.

**Architecture:** A semantic static document in `docs/index.html` is styled by one token-driven stylesheet and progressively enhanced by one ES module. A small Python structural verifier and Node built-in tests cover static contracts and interactive helpers without adding repository dependencies.

**Tech Stack:** HTML5, CSS custom properties and Grid, browser ES modules, Python 3 standard library, Node.js built-in test runner, GitHub Pages

**Spec:** `docs/superpowers/specs/2026-08-30-github-pages-website-design.md`

## Global Constraints

- Keep the website in `docs/`; do not change any .NET project or solution file.
- Use only relative page assets so deployment below `/MemScope/` works.
- Add no package manager, generated bundle, CDN dependency, analytics, form, cookie, or runtime API.
- Use the repository URL `https://github.com/soliktomasz/MemScope` for every "View GitHub" action.
- Use no application screenshot, screenshot placeholder, fake dashboard, simulated terminal, generated photograph, or unsupported product claim.
- Preserve one system-aware theme, one blue accent, and one restrained 4-6px radius family across the page.
- Keep the hero headline to two desktop lines, its supporting sentence to 20 words or fewer, and its actions visible in the initial viewport.
- Use zero em-dash or en-dash characters in visible copy.
- Render all core content and links without JavaScript; honor `prefers-reduced-motion` when JavaScript is available.
- Use `rtk` before every shell command, including every command shown below.

## File Map

- Create `docs/index.html`: semantic landing-page content, metadata, navigation, future screenshot comments, and enhancement hooks.
- Create `docs/styles.css`: theme tokens, layout system, topology artwork, responsive rules, focus states, and reduced-motion behavior.
- Create `docs/script.js`: clipboard helper, copy-button controller, reveal observer, and current-year enhancement.
- Create `docs/404.html`: standalone GitHub Pages recovery page using the shared stylesheet.
- Create `docs/assets/favicon.svg`: compact geometric MemScope mark for browser chrome only.
- Create `tests/website/verify_site.py`: standard-library structural and copy-contract checks.
- Create `tests/website/script.test.mjs`: Node tests for clipboard success and failure behavior.
- Modify `README.md`: add website preview and GitHub Pages deployment instructions.

---

### Task 1: Semantic Landing Page and Static Contract

**Files:**
- Create: `docs/index.html`
- Create: `tests/website/verify_site.py`

**Interfaces:**
- Consumes: Feature descriptions and commands from `README.md`; the repository URL in Global Constraints.
- Produces: IDs `main-content`, `capabilities`, and `get-started`; hooks `[data-copy-button]`, `[data-copy-source]`, `[data-year]`, and `.reveal`; asset references `styles.css`, `script.js`, and `assets/favicon.svg`.

- [ ] **Step 1: Write the failing structural verifier**

Create `tests/website/verify_site.py` with a standard-library parser that checks the semantic and content contract:

```python
from __future__ import annotations

import re
import sys
from html.parser import HTMLParser
from pathlib import Path
from urllib.parse import urlparse

ROOT = Path(__file__).resolve().parents[2]
DOCS = ROOT / "docs"
INDEX = DOCS / "index.html"
REPOSITORY_URL = "https://github.com/soliktomasz/MemScope"


class SiteParser(HTMLParser):
    def __init__(self) -> None:
        super().__init__(convert_charrefs=True)
        self.tags: list[str] = []
        self.ids: set[str] = set()
        self.links: list[tuple[str, str]] = []
        self.scripts: list[dict[str, str | None]] = []
        self.visible_text: list[str] = []
        self._anchor_stack: list[tuple[str, list[str]]] = []
        self._hidden_depth = 0

    def handle_starttag(self, tag: str, attrs: list[tuple[str, str | None]]) -> None:
        values = dict(attrs)
        self.tags.append(tag)
        if values.get("id"):
            self.ids.add(values["id"] or "")
        if tag == "a" and values.get("href"):
            self._anchor_stack.append((values["href"] or "", []))
        if tag == "script":
            self.scripts.append(values)
        if tag in {"script", "style", "template"} or "hidden" in values:
            self._hidden_depth += 1

    def handle_endtag(self, tag: str) -> None:
        if tag == "a" and self._anchor_stack:
            href, parts = self._anchor_stack.pop()
            self.links.append((href, " ".join(parts).strip()))
        if tag in {"script", "style", "template"} and self._hidden_depth:
            self._hidden_depth -= 1

    def handle_data(self, data: str) -> None:
        if not self._hidden_depth and data.strip():
            self.visible_text.append(data.strip())
            if self._anchor_stack:
                self._anchor_stack[-1][1].append(data.strip())


def parse(path: Path) -> SiteParser:
    parser = SiteParser()
    parser.feed(path.read_text(encoding="utf-8"))
    return parser


def assert_relative_assets(parser: SiteParser) -> None:
    source = INDEX.read_text(encoding="utf-8")
    asset_values = re.findall(r'(?:href|src)="([^"]+)"', source)
    local_assets = [value for value in asset_values if not urlparse(value).scheme and not value.startswith(("#", "mailto:"))]
    assert local_assets, "expected local page assets"
    assert all(not value.startswith("/") for value in local_assets), local_assets


def main() -> int:
    assert INDEX.exists(), "docs/index.html is missing"
    parser = parse(INDEX)
    for landmark in ("header", "nav", "main", "section", "footer"):
        assert landmark in parser.tags, f"missing {landmark} landmark"
    assert {"main-content", "capabilities", "get-started"} <= parser.ids
    text = " ".join(parser.visible_text)
    assert "See what managed memory is doing." in text
    assert "—" not in text and "–" not in text
    github_labels = [label for href, label in parser.links if href == REPOSITORY_URL]
    assert len(github_labels) >= 2
    assert set(github_labels) == {"View GitHub"}, github_labels
    assert "fake" not in text.lower()
    assert_relative_assets(parser)
    print("website structure: ok")
    return 0


if __name__ == "__main__":
    sys.exit(main())
```

- [ ] **Step 2: Run the verifier to confirm it fails**

Run: `rtk python3 tests/website/verify_site.py`

Expected: FAIL with `docs/index.html is missing`.

- [ ] **Step 3: Create the semantic page**

Create `docs/index.html` with this exact content structure and copy:

```html
<!doctype html>
<html lang="en">
<head>
  <meta charset="utf-8">
  <meta name="viewport" content="width=device-width, initial-scale=1">
  <meta name="description" content="Inspect live .NET memory behavior and analyze managed heap dumps with MemScope.">
  <meta name="theme-color" content="#f5f7fa" media="(prefers-color-scheme: light)">
  <meta name="theme-color" content="#111318" media="(prefers-color-scheme: dark)">
  <meta property="og:title" content="MemScope - .NET memory profiler">
  <meta property="og:description" content="Inspect live .NET memory behavior and analyze managed heap dumps.">
  <meta property="og:type" content="website">
  <title>MemScope - .NET memory profiler</title>
  <link rel="icon" href="assets/favicon.svg" type="image/svg+xml">
  <link rel="stylesheet" href="styles.css">
  <script type="module" src="script.js"></script>
</head>
<body>
  <a class="skip-link" href="#main-content">Skip to content</a>
  <header class="site-header">
    <nav class="nav shell" aria-label="Primary navigation">
      <a class="brand" href="./" aria-label="MemScope home"><span aria-hidden="true">M</span>MemScope</a>
      <details class="nav-menu">
        <summary>Menu</summary>
        <div class="nav-links">
          <a href="#capabilities">Capabilities</a>
          <a href="#get-started">Get started</a>
          <a class="button button-secondary" href="https://github.com/soliktomasz/MemScope">View GitHub</a>
        </div>
      </details>
    </nav>
  </header>
  <main id="main-content">
    <section class="hero shell" aria-labelledby="hero-title">
      <div class="hero-copy">
        <p class="eyebrow">.NET memory profiler</p>
        <h1 id="hero-title">See what managed memory is doing.</h1>
        <p class="hero-lede">Watch live runtime metrics, capture dumps, and trace heap references from one focused desktop tool.</p>
        <div class="hero-actions">
          <a class="button button-primary" href="https://github.com/soliktomasz/MemScope">View GitHub</a>
          <a class="text-link" href="#get-started">Get started</a>
        </div>
      </div>
      <div class="topology" aria-hidden="true">
        <div class="topology-label"><span>Managed heap</span><strong>live</strong></div>
        <div class="generation generation-zero"><span>Gen 0</span><i></i><i></i><i></i><i></i></div>
        <div class="generation generation-one"><span>Gen 1</span><i></i><i></i><i></i></div>
        <div class="generation generation-two"><span>Gen 2</span><i></i><i></i></div>
        <div class="reference-line"></div>
      </div>
      <!-- Future media: hero product image, 1600 x 1200. -->
    </section>

    <section class="workflow shell reveal" aria-labelledby="workflow-title">
      <h2 id="workflow-title">From process to proof.</h2>
      <ol>
        <li><strong>Attach</strong><span>Choose an accessible .NET process.</span></li>
        <li><strong>Observe</strong><span>Follow heap and collection metrics live.</span></li>
        <li><strong>Capture</strong><span>Save a heap-bearing dump.</span></li>
        <li><strong>Inspect</strong><span>Trace types, instances, and references.</span></li>
      </ol>
    </section>

    <section class="capabilities shell reveal" id="capabilities" aria-labelledby="capabilities-title">
      <h2 id="capabilities-title">The memory workflow, connected.</h2>
      <div class="capability-grid">
        <article class="capability capability-wide"><h3>Find the right process</h3><p>Discover attachable .NET processes and inspect runtime details before connecting.</p></article>
        <article class="capability capability-metric"><h3>Read live pressure</h3><p>Monitor heap size, allocation rate, generations, collections, promotion, LOH, and POH.</p></article>
        <article class="capability capability-accent"><h3>Capture evidence</h3><p>Create a heap-bearing dump from the active profiling session.</p></article>
        <article class="capability"><h3>Browse the heap</h3><p>Sort and filter managed types, then inspect their object instances.</p></article>
        <article class="capability capability-wide"><h3>Follow references</h3><p>Move through incoming and outgoing references to understand why objects remain alive.</p></article>
      </div>
      <!-- Future media: heap analysis image, 1600 x 1000. -->
    </section>

    <section class="foundation shell reveal" aria-labelledby="foundation-title">
      <div class="foundation-copy"><h2 id="foundation-title">Built on the runtime's own diagnostics.</h2><p>MemScope uses established .NET diagnostics APIs and a cross-platform desktop shell.</p></div>
      <dl class="foundation-facts">
        <div><dt>EventPipe</dt><dd>Streams live runtime events without modifying the target application.</dd></div>
        <div><dt>ClrMD</dt><dd>Walks managed heaps and object references in captured dumps.</dd></div>
        <div><dt>Avalonia</dt><dd>Delivers the focused desktop interface across supported platforms.</dd></div>
      </dl>
    </section>

    <section class="get-started shell reveal" id="get-started" aria-labelledby="get-started-title">
      <div><h2 id="get-started-title">Run MemScope locally.</h2><p>Clone the repository and launch the Avalonia application with the .NET 10 SDK.</p></div>
      <div class="command-panel">
        <pre tabindex="0" data-copy-source><code>git clone https://github.com/soliktomasz/MemScope.git
cd MemScope
dotnet run --project src/MemoryProfiler.App</code></pre>
        <button class="button button-secondary" type="button" data-copy-button aria-describedby="copy-status">Copy commands</button>
        <p class="copy-status" id="copy-status" aria-live="polite"></p>
      </div>
    </section>
  </main>
  <footer class="site-footer shell">
    <div><a class="brand" href="./"><span aria-hidden="true">M</span>MemScope</a><p>Open-source memory inspection for .NET.</p></div>
    <div class="footer-links"><a href="https://github.com/soliktomasz/MemScope">View GitHub</a><a href="https://github.com/soliktomasz/MemScope/blob/main/LICENSE.md">MIT License</a></div>
    <p class="copyright">© <span data-year>2026</span> MemScope contributors.</p>
  </footer>
</body>
</html>
```

Keep the hero topology semantic-free with `aria-hidden="true"`; do not turn it into a simulated application window.

- [ ] **Step 4: Run the structural verifier**

Run: `rtk python3 tests/website/verify_site.py`

Expected: PASS with `website structure: ok`.

- [ ] **Step 5: Commit the semantic page**

```bash
rtk git add docs/index.html tests/website/verify_site.py
rtk git commit -m "feat: add MemScope website structure"
```

### Task 2: Responsive Visual System

**Files:**
- Create: `docs/styles.css`
- Modify: `tests/website/verify_site.py`

**Interfaces:**
- Consumes: HTML classes and IDs created in Task 1.
- Produces: CSS variables `--background`, `--surface`, `--surface-subtle`, `--border`, `--text`, `--muted`, `--accent`, and `--accent-contrast`; responsive behavior at 768px; `.is-reveal-ready` and `.is-visible` states used by Task 3.

- [ ] **Step 1: Extend the verifier with stylesheet contracts**

Add assertions that `docs/styles.css` exists and contains each token above, `@media (prefers-color-scheme: dark)`, `@media (prefers-reduced-motion: reduce)`, `min-height: 100dvh`, `:focus-visible`, and `@media (max-width: 767px)`. Assert that it does not contain `100vh`, `#000000`, or `#ffffff`.

- [ ] **Step 2: Run the verifier to confirm it fails**

Run: `rtk python3 tests/website/verify_site.py`

Expected: FAIL because `docs/styles.css` is missing.

- [ ] **Step 3: Implement tokens and base components**

Create `docs/styles.css`. Begin with these exact token and reset contracts:

```css
:root {
  color-scheme: light dark;
  --background: #f5f7fa;
  --surface: #fcfdfe;
  --surface-subtle: #edf1f5;
  --border: #d6dce4;
  --text: #171a1f;
  --muted: #596270;
  --accent: #2f6feb;
  --accent-contrast: #f9fbff;
  --shadow: 0 24px 80px rgb(46 64 91 / 0.14);
  --radius: 6px;
  --shell: min(1280px, calc(100% - 48px));
  font-family: "Avenir Next", Avenir, "Segoe UI Variable", "Segoe UI", sans-serif;
}

@media (prefers-color-scheme: dark) {
  :root {
    --background: #111318;
    --surface: #191c22;
    --surface-subtle: #232730;
    --border: #373d48;
    --text: #f1f4f7;
    --muted: #aab3bf;
    --accent: #5b8def;
    --accent-contrast: #0e172a;
    --shadow: 0 24px 80px rgb(2 6 16 / 0.34);
  }
}

*, *::before, *::after { box-sizing: border-box; }
html { scroll-behavior: smooth; }
body { margin: 0; background: var(--background); color: var(--text); line-height: 1.6; }
a { color: inherit; }
button, summary { font: inherit; }
.shell { width: var(--shell); margin-inline: auto; }
:focus-visible { outline: 3px solid var(--accent); outline-offset: 4px; }
```

Implement the navigation at 68px high, buttons with one-line labels and 44px minimum height, and the skip link. Use only `transform`, `opacity`, color, background, and border-color in transitions.

- [ ] **Step 4: Implement the desktop compositions**

Use CSS Grid for the hero, workflow, capability grid, foundation, and getting-started section. Required desktop grid contracts:

```css
.hero { min-height: calc(100dvh - 68px); display: grid; grid-template-columns: minmax(0, 1.05fr) minmax(360px, .95fr); align-items: center; gap: clamp(48px, 8vw, 112px); padding-block: clamp(56px, 8vh, 88px); }
.workflow ol { display: grid; grid-template-columns: repeat(4, 1fr); }
.capability-grid { display: grid; grid-template-columns: 1.35fr .65fr; grid-auto-flow: dense; gap: 12px; }
.capability-wide { grid-column: span 2; }
.foundation { display: grid; grid-template-columns: minmax(0, 1.1fr) minmax(360px, .9fr); gap: clamp(48px, 10vw, 144px); }
.get-started { display: grid; grid-template-columns: minmax(0, .8fr) minmax(420px, 1.2fr); gap: clamp(40px, 8vw, 112px); }
```

Give the five capability cells distinct treatments using only the locked palette: base surface, subtle surface, an accent rule, a sparse topology pattern, and a solid accent cell with verified contrast. Keep all cells at `var(--radius)`.

Build the hero topology from the existing `.generation` and `<i>` elements using borders, grids, and pseudo-elements. It should read as an abstract object graph, not a product screen. Use the blue accent only for selected nodes and the reference line.

- [ ] **Step 5: Add explicit mobile and motion fallbacks**

At `max-width: 767px`, set `--shell: min(100% - 32px, 1280px)`, collapse every multi-column grid to one column, reset `.capability-wide` to `grid-column: auto`, keep the topology below the hero copy, and make the footer vertical. Keep `.nav-links` hidden inside the closed `details` element and visible inside `[open]`.

Add reveal contracts:

```css
.is-reveal-ready { opacity: 0; transform: translateY(20px); }
.is-reveal-ready.is-visible { opacity: 1; transform: translateY(0); transition: opacity 600ms cubic-bezier(.16, 1, .3, 1), transform 600ms cubic-bezier(.16, 1, .3, 1); }

@media (prefers-reduced-motion: reduce) {
  html { scroll-behavior: auto; }
  *, *::before, *::after { animation-duration: .01ms !important; animation-iteration-count: 1 !important; transition-duration: .01ms !important; }
  .is-reveal-ready { opacity: 1; transform: none; }
}
```

- [ ] **Step 6: Run structural verification and manually inspect responsive rules**

Run: `rtk python3 tests/website/verify_site.py`

Expected: PASS with `website structure: ok`.

Run: `rtk grep -n "100vh\|#000000\|#ffffff\|—\|–" docs/index.html docs/styles.css`

Expected: no matches.

- [ ] **Step 7: Commit the visual system**

```bash
rtk git add docs/styles.css tests/website/verify_site.py
rtk git commit -m "feat: style responsive MemScope landing page"
```

### Task 3: Progressive Enhancements and Interaction Tests

**Files:**
- Create: `docs/script.js`
- Create: `tests/website/script.test.mjs`
- Modify: `tests/website/verify_site.py`

**Interfaces:**
- Consumes: `[data-copy-button]`, `[data-copy-source]`, `#copy-status`, `[data-year]`, and `.reveal` from Task 1; `.is-reveal-ready` and `.is-visible` from Task 2.
- Produces: exported async function `copyText(text, clipboard)` returning `"copied"` or `"select"`; exported function `setCurrentYear(element, year)`; exported function `initReveals(elements, Observer, reduceMotion)`.

- [ ] **Step 1: Write failing Node tests for pure helpers**

Create `tests/website/script.test.mjs`:

```javascript
import assert from "node:assert/strict";
import test from "node:test";
import { copyText, initReveals, setCurrentYear } from "../../docs/script.js";

test("copyText writes the supplied commands", async () => {
  let copied = "";
  const result = await copyText("dotnet run", { writeText: async value => { copied = value; } });
  assert.equal(result, "copied");
  assert.equal(copied, "dotnet run");
});

test("copyText returns a selectable fallback when clipboard access fails", async () => {
  const result = await copyText("dotnet run", { writeText: async () => { throw new Error("denied"); } });
  assert.equal(result, "select");
});

test("setCurrentYear writes a stable numeric year", () => {
  const element = { textContent: "" };
  setCurrentYear(element, 2026);
  assert.equal(element.textContent, "2026");
});

test("initReveals renders immediately when motion is reduced", () => {
  const element = { classList: { added: [], add(value) { this.added.push(value); } } };
  initReveals([element], class {}, true);
  assert.deepEqual(element.classList.added, ["is-visible"]);
});
```

- [ ] **Step 2: Run the tests to confirm they fail**

Run: `rtk node --test tests/website/script.test.mjs`

Expected: FAIL with module-not-found for `docs/script.js`.

- [ ] **Step 3: Implement the pure helpers and guarded browser bootstrap**

Create `docs/script.js` as an ES module. Start with these exports:

```javascript
export async function copyText(text, clipboard) {
  try {
    await clipboard.writeText(text);
    return "copied";
  } catch {
    return "select";
  }
}

export function setCurrentYear(element, year) {
  element.textContent = String(year);
}

export function initReveals(elements, Observer, reduceMotion) {
  if (reduceMotion || !Observer) {
    elements.forEach(element => element.classList.add("is-visible"));
    return null;
  }
  elements.forEach(element => element.classList.add("is-reveal-ready"));
  const observer = new Observer(entries => {
    entries.forEach(entry => {
      if (!entry.isIntersecting) return;
      entry.target.classList.add("is-visible");
      observer.unobserve(entry.target);
    });
  }, { threshold: 0.16 });
  elements.forEach(element => observer.observe(element));
  return observer;
}
```

Guard browser initialization with `if (typeof document !== "undefined")`. On `DOMContentLoaded`:

- Set `[data-year]` with the current year.
- Initialize reveals with `window.IntersectionObserver` and `matchMedia("(prefers-reduced-motion: reduce)").matches`.
- Attach one click handler to `[data-copy-button]`.
- Read commands from `[data-copy-source].textContent.trim()`.
- On `"copied"`, set button text to `Copied`, status text to `Commands copied to clipboard.`, and restore the original text after 1800ms.
- On `"select"`, set button text to `Select and copy`, status text to `Clipboard access was unavailable. The commands are selected.`, focus the `<pre>`, and use `window.getSelection()` with `document.createRange()` to select its contents.

- [ ] **Step 4: Extend static verification for script loading**

Assert that `docs/script.js` exists, the page loads it with `type="module"`, and the source contains no `addEventListener("scroll"` or `window.scrollY`.

- [ ] **Step 5: Run interaction and structural tests**

Run: `rtk node --test tests/website/script.test.mjs`

Expected: 4 tests PASS.

Run: `rtk python3 tests/website/verify_site.py`

Expected: PASS with `website structure: ok`.

- [ ] **Step 6: Commit progressive enhancement**

```bash
rtk git add docs/script.js tests/website/script.test.mjs tests/website/verify_site.py
rtk git commit -m "feat: add accessible website interactions"
```

### Task 4: Supporting Assets, Recovery Page, and Deployment Documentation

**Files:**
- Create: `docs/assets/favicon.svg`
- Create: `docs/404.html`
- Modify: `README.md`
- Modify: `tests/website/verify_site.py`

**Interfaces:**
- Consumes: shared `styles.css`, relative root `./`, and repository URL.
- Produces: favicon referenced by `index.html`; a standalone recovery route; exact README heading `## Website`.

- [ ] **Step 1: Extend the structural verifier for final assets**

Add assertions that:

- `docs/assets/favicon.svg` and `docs/404.html` exist.
- Every local `href` and `src` from `index.html` resolves to a file under `docs/`, except in-page anchors.
- `404.html` contains a link to `./` and the repository URL.
- `README.md` contains `## Website`, `Deploy from a branch`, and `/docs`.
- All visible HTML text in both pages contains no em-dash or en-dash.

- [ ] **Step 2: Run the verifier to confirm it fails**

Run: `rtk python3 tests/website/verify_site.py`

Expected: FAIL because the favicon or 404 page is missing.

- [ ] **Step 3: Add the favicon and recovery page**

Create `docs/assets/favicon.svg` as a 64 x 64 SVG with a `#171a1f` rounded 6px background and a single `#5b8def` geometric M path. This exception is allowed because the spec explicitly defines a favicon mark; do not reuse it as a decorative page illustration.

Create `docs/404.html` with the same metadata, stylesheet, favicon, skip link, brand treatment, and theme as `index.html`. Its main content is:

```html
<main id="main-content" class="error-page shell">
  <p class="eyebrow">Page not found</p>
  <h1>This memory address is unavailable.</h1>
  <p>The page may have moved, or the link may be incomplete.</p>
  <div class="hero-actions">
    <a class="button button-primary" href="./">Return home</a>
    <a class="text-link" href="https://github.com/soliktomasz/MemScope">View GitHub</a>
  </div>
</main>
```

Add `.error-page` styles to `styles.css` so the message is vertically centered using `min-height: calc(100dvh - 68px)` and remains left aligned.

- [ ] **Step 4: Document local preview and GitHub Pages setup**

Add this section to `README.md` before `## Development`:

```markdown
## Website

The project website is a static site under `docs/`. Preview it locally:

```bash
python3 -m http.server 8000 -d docs
```

Then open `http://localhost:8000`.

To publish with GitHub Pages, open the repository's **Settings > Pages**, choose **Deploy from a branch**, then select the default branch and the `/docs` folder. No frontend build step is required.
```

- [ ] **Step 5: Run all website tests**

Run: `rtk python3 tests/website/verify_site.py`

Expected: PASS with `website structure: ok`.

Run: `rtk node --test tests/website/script.test.mjs`

Expected: 4 tests PASS.

- [ ] **Step 6: Serve and inspect the site**

Run: `rtk python3 -m http.server 8000 -d docs`

In a second terminal, run: `rtk curl -I http://127.0.0.1:8000/`

Expected: `HTTP/1.0 200 OK` and `Content-type: text/html`.

Run: `rtk curl -I http://127.0.0.1:8000/styles.css`

Expected: `HTTP/1.0 200 OK` and a CSS content type.

Inspect the page at 375px, 768px, 1024px, and 1440px in both system color schemes. Verify keyboard focus order, the mobile `details` menu, copy success, clipboard failure fallback, 404 navigation, and reduced motion. Confirm there is no horizontal overflow and no CTA wraps.

Run Lighthouse from browser developer tools when it is available. Record LCP, INP, CLS, and accessibility results; if the current browser environment does not expose Lighthouse, record that limitation in the handoff instead of adding a package dependency.

- [ ] **Step 7: Run the .NET regression suite**

Run: `rtk dotnet test MemoryProfiler.sln`

Expected: all existing tests PASS. The exact count may differ from the historical count in `AGENTS.md` as the repository evolves.

- [ ] **Step 8: Run final pre-flight searches**

Run: `rtk grep -n "—\|–\|100vh\|window.scrollY\|addEventListener(\"scroll\"" docs/index.html docs/404.html docs/styles.css docs/script.js README.md`

Expected: no matches in website code or visible website copy.

Run: `rtk git diff --check`

Expected: no whitespace errors.

- [ ] **Step 9: Commit the deployable website**

```bash
rtk git add docs/assets/favicon.svg docs/404.html docs/styles.css README.md tests/website/verify_site.py
rtk git commit -m "docs: prepare website for GitHub Pages"
```

## Final Acceptance Review

- [ ] Confirm every acceptance criterion in `docs/superpowers/specs/2026-08-30-github-pages-website-design.md` has direct evidence from automated tests or the manual inspection checklist.
- [ ] Confirm `rtk git status --short` contains no unexpected changes.
- [ ] Record the website verifier, Node test, .NET test, and local HTTP results in the final handoff.
