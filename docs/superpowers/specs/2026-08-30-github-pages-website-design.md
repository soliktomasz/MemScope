# MemScope GitHub Pages Website Design

## Purpose

Create a polished public website for MemScope that explains the product to .NET developers and provides a direct path to the GitHub repository. The site will be deployable through GitHub Pages without a frontend build tool and will not alter the desktop application's runtime or build.

This first version intentionally contains no application screenshots. The composition must feel complete without fake product UI, while leaving clear extension points for real screenshots in a future feature.

## Audience and Message

The primary audience is a .NET developer investigating managed-memory behavior in a local process or captured dump. The page should answer three questions quickly:

1. What does MemScope do?
2. What workflows does it support?
3. How can I run it?

The primary message is: "See what managed memory is doing." Supporting copy should remain technical, direct, and consistent with the repository README. It must not claim performance characteristics, platform support, releases, or adoption that the repository does not substantiate.

## Design Direction

Reading this as a developer-tool landing page for .NET engineers, with a precise desktop-instrument language, leaning toward native static HTML, CSS, and JavaScript for frictionless GitHub Pages deployment.

- `DESIGN_VARIANCE: 7`: asymmetric compositions and varied section layouts, with strict single-column mobile fallbacks.
- `MOTION_INTENSITY: 5`: motivated entrance and interaction feedback only, with reduced-motion fallbacks.
- `VISUAL_DENSITY: 4`: enough technical detail to establish credibility without resembling a dashboard.

The visual language extends the Avalonia application's existing tokens:

- Light background: cool blue-gray near `#F5F7FA`
- Dark background: near-black blue-gray near `#111318`
- Accent: MemScope blue near `#2F6FEB` in light mode and `#5B8DEF` in dark mode
- Typography: a modern system sans stack and a system monospace stack, avoiding remote font dependencies
- Shape: restrained 4-6px radii for surfaces and controls, matching the desktop application
- Theme: system-aware light and dark modes using CSS custom properties

Only the blue accent is used across the page. Sections may vary surface tone within the current theme but must not invert the page theme.

## Architecture

The website will live in `docs/`, the GitHub Pages-supported directory on the default branch.

```text
docs/
  index.html
  styles.css
  script.js
  404.html
  assets/
    favicon.svg
  superpowers/
    specs/
    plans/
```

Existing design and plan documents remain in place and are not linked from the public page. GitHub Pages serves only explicitly referenced assets, so their presence under `docs/` does not expose them through navigation.

The page has no package manager, generated bundle, CDN dependency, or runtime API. All links and assets use relative paths so the site works at a repository subpath such as `/MemScope/`.

`index.html` owns semantic structure and content. `styles.css` owns design tokens, responsive layout, theme behavior, and motion. `script.js` owns only progressive enhancements: mobile navigation state, current year, and reveal observation. The page remains readable and navigable if JavaScript is unavailable.

## Information Architecture

The page contains six distinct layout families:

1. **Navigation**
   - MemScope wordmark, two in-page links, and one "View GitHub" action.
   - Single-line desktop navigation under 80px high.
   - Compact disclosure menu below 768px with accurate `aria-expanded` state.

2. **Asymmetric hero**
   - One short eyebrow: ".NET memory profiler".
   - Headline: "See what managed memory is doing."
   - Supporting sentence of no more than 20 words.
   - Primary "View GitHub" action and secondary "Get started" anchor.
   - A real, semantic memory-topology composition built from CSS primitives and live page text. It represents generations and object relationships, not a fake application screenshot.
   - The hero fits the initial viewport on common desktop sizes and uses `min-height: 100dvh`, never `100vh`.

3. **Workflow rail**
   - A horizontal desktop sequence for attach, observe, capture, and inspect.
   - The actual verbs are the labels; no numbered step labels.
   - A vertical mobile fallback.

4. **Asymmetric capabilities field**
   - Five capability cells with varied spans and surface treatments.
   - Capabilities: process discovery, live metrics, dump capture, heap browsing, and reference navigation.
   - Exactly five cells, with no empty filler cell and no equal three-card row.

5. **Technical foundation**
   - A full-width statement followed by three sparse implementation facts: EventPipe, ClrMD, and Avalonia.
   - Facts use spacing and a single group divider rather than boxed cards or a long bordered table.

6. **Getting started and footer**
   - A copyable three-line command block based on the README.
   - Inline success and failure feedback for the copy action. Without JavaScript, commands remain selectable.
   - Final "View GitHub" action uses the same label and destination as every repository CTA.
   - Footer links to the repository and MIT license without build/version decoration.

## Screenshot Extension Points

No screenshot, screenshot placeholder, fake dashboard, or simulated terminal appears in this version.

The HTML will include non-rendered comments marking two future placements:

- Hero product image, approximately 1600 x 1200, replacing or sitting behind the memory-topology composition.
- Heap analysis image, approximately 1600 x 1000, inserted between the capabilities and technical-foundation sections.

Future screenshot work must use optimized `picture` sources with explicit dimensions, descriptive alt text, and WebP or AVIF output. Adding those assets is outside this task.

## Interaction and Motion

Motion communicates hierarchy and state:

- Hero elements enter once in a short CSS cascade to establish reading order.
- Major sections reveal once when entering the viewport using `IntersectionObserver`.
- Buttons translate by one pixel on active press to acknowledge input.
- The copy control changes its accessible label and visible text after success, then returns to its original state.

There are no scroll listeners, parallax effects, marquees, perpetual animations, or scroll hijacking. Under `prefers-reduced-motion: reduce`, content renders immediately and all nonessential transitions are removed.

## Responsive Behavior

At widths below 768px:

- Navigation collapses into an accessible disclosure menu.
- Hero, workflow, capabilities, technical facts, and getting-started layouts become single-column.
- Decorative topology elements simplify and remain within their container.
- Buttons remain one line and meet a minimum 44px touch target.
- Page gutters reduce to 16px, while content never creates horizontal scrolling.

At wider breakpoints, content is constrained to a maximum width near 1280px.

## Accessibility

- Semantic landmarks: `header`, `nav`, `main`, labeled `section` elements, and `footer`.
- A skip link becomes visible on focus.
- Keyboard-visible focus styles use the single blue accent with sufficient separation from the background.
- Navigation disclosure and copy feedback communicate state to assistive technology.
- Text and controls target WCAG AA contrast in both color schemes.
- Decorative topology nodes are hidden from assistive technology; meaningful labels are provided as adjacent text.
- No information depends on color alone.

## Metadata and GitHub Pages

The document includes a concise title, description, theme colors for both schemes, canonical-ready relative assets, and Open Graph metadata that does not depend on an image in this version. The favicon is a simple geometric "M" mark built specifically as a favicon, not a decorative page illustration.

Deployment documentation will be added to the README:

1. Open repository Settings, then Pages.
2. Select "Deploy from a branch".
3. Select the default branch and `/docs` folder.

No workflow file is required. The site can also be previewed locally with a static server rooted at `docs/`.

## Error, Loading, and Empty States

The static page has no asynchronous data-loading state. Progressive enhancements account for failure explicitly:

- If JavaScript does not load, all content and links remain usable and commands remain selectable.
- If Clipboard API access fails, the copy control reports "Select and copy" and focuses the command block.
- The mobile navigation is visible in the normal document flow when scripting is unavailable.
- `404.html` provides a direct link back to the site root and the repository.

## Testing and Verification

Implementation verification will include:

- Validate local links and required files.
- Serve `docs/` at a repository-style subpath and confirm relative assets resolve.
- Exercise navigation, mobile disclosure, copy success, and copy failure paths.
- Check keyboard navigation and visible focus states.
- Test widths at 375px, 768px, 1024px, and 1440px.
- Test light mode, dark mode, reduced motion, JavaScript disabled, and the 404 page.
- Search visible copy for forbidden dash characters and unsupported claims.
- Run the existing .NET test suite to confirm the static addition does not affect the product.
- Run Lighthouse where the environment permits it, targeting LCP below 2.5 seconds, INP below 200ms, CLS below 0.1, and no critical accessibility errors.

## Scope Boundaries

Included:

- One responsive landing page
- One custom 404 page
- Local favicon
- Minimal progressive enhancement script
- README deployment instructions
- GitHub Pages-compatible relative paths

Excluded:

- Application screenshots or generated photography
- Download/release automation
- Analytics, forms, newsletter signup, or cookies
- Documentation portal or additional routes
- Domain configuration
- GitHub Pages settings changes, which require repository-owner action

## Acceptance Criteria

- Opening `docs/index.html` through a static server produces a complete, responsive landing page with no missing asset.
- The page accurately describes the current MemScope feature set from the README.
- The page contains no application screenshots, fake screenshots, or visible empty screenshot placeholders.
- All repository CTAs use the label "View GitHub" and point to `https://github.com/soliktomasz/MemScope`.
- Light and dark themes follow the system preference and preserve contrast.
- The primary content and links work without JavaScript.
- Mobile navigation and command-copy feedback work with JavaScript enabled.
- Reduced-motion users receive a static experience.
- The site works when hosted below the `/MemScope/` path.
- Existing .NET builds and tests remain unaffected.
