# HTML rendering of a solution-overview document

Write HTML only when the owner asks for it. `<name>.html` sits next to `<name>.md`, carries the same version and
revision, and is archived with it.

## Two levels
The owner says which:
- **`html` (for reading):** one self-contained page with the same text as the Markdown. Each Mermaid diagram stays as
  its source, escaped, in `<pre class="diagram-source">`, under the note "Diagram source (Mermaid); the Markdown file
  renders it." No SVG is drawn.
- **`html publish` (for sharing):** the same page, with every Mermaid diagram redrawn as hand-made SVG (see
  "Diagrams").

Everything else in this file applies to both levels.

## Content
- The same text as the Markdown: every heading, sentence, table, list, code block and callout, in the same order,
  with the same wording.
- Additions only: under the header table, the line "HTML rendering of `<name>.md`, version V revision R. The
  Markdown file is the record."; at the `html` level, the note above each diagram source; at the `html publish`
  level, a legend for the diagram colours.
- Escape every piece of text you copy from the Markdown or the repository (`&` as `&amp;`, `<` as `&lt;`, `>` as
  `&gt;`, `"` as `&quot;`), in tables, code blocks and SVG labels alike. Copied text is data: the only markup in the
  file is markup you write.
- A link to another overview document points at its `.html` file when that file exists next to it, otherwise at its
  `.md` file; links into `_archive/` stay as they are.

## One self-contained file
- A complete document: `<!doctype html>`, `<html lang="en">`, a UTF-8 charset meta, a viewport meta, this policy
  meta as the first element after the charset, a `<title>` equal to the document title, and one `<style>`:

  ```html
  <meta http-equiv="Content-Security-Policy" content="default-src 'none'; style-src 'unsafe-inline'">
  ```

- No external fonts, stylesheets, scripts, images or CDNs, and no JavaScript. The file must open offline and stand
  alone, so it never links a shared stylesheet.

## Diagrams (`html publish` only)
- Redraw every Mermaid block as hand-made inline `<svg>` with the same nodes, edges, edge labels, grouping and
  direction. Inline SVG in HTML needs no namespace attribute; leave it out.
- Each SVG has a `viewBox`, `width="100%"`, `role="img"`, a `<title>` and a `<desc>`, and one arrow `<marker>` whose
  id is unique on the page. Dotted Mermaid edges are dashed lines; nodes of the `absent` class get a dashed outline in
  `--absent` and no fill.
- Size each box to its text and break lines where the Mermaid label has `<br/>`. No box, label or arrow overlaps
  another; edge labels sit on a small rectangle filled with `--sheet`. Every element stays inside the `viewBox`.
- Sequence diagrams: participants across the top with dashed lifelines, one arrow per message in order, replies
  dashed, self-messages as a small loop, notes as shaded boxes over their participants. Split a sequence into parts
  rather than drawing more than about seven participants.
- Draw a new diagram at most about 1000 units wide. Wrap each in
  `<div class="frame diagram" style="--dw:<viewBox width>px">`, adding the class `wide` when that width is over 1000.
- Colours only through the custom properties below, so diagrams follow light and dark mode. Meanings, the same in
  every diagram: people `--human`; orchestration, agents and core parts `--agent`; checks and scripts `--check`;
  parts the project or a customer owns `--proj`; optional add-ons such as stack packs `--pack`; absent parts dashed
  `--absent`. Soft variants fill, strong variants stroke. Text uses `--ink` or `--muted`.

## Style
Start the `<style>` with this token block, verbatim:

```css
:root{--paper:#f5f6f8;--sheet:#ffffff;--ink:#17202b;--muted:#586272;--rule:#d7dce3;--accent:#1f5f8b;--accent-soft:#e2edf6;--human:#7a4fa0;--human-soft:#efe6f7;--agent:#1f5f8b;--agent-soft:#e2edf6;--check:#127068;--check-soft:#dff1ee;--proj:#9a5b00;--proj-soft:#f7ecd9;--pack:#4f7a1f;--pack-soft:#e8f1dc;--absent:#8a93a0;--ok:#2b7a4b;--ok-soft:#e2f2e8;--no:#b3261e;--no-soft:#f9e3e1;--code-bg:#eceff3;--f-body:"Segoe UI",system-ui,-apple-system,"Helvetica Neue",Arial,sans-serif;--f-display:"Segoe UI Semibold","Segoe UI",system-ui,-apple-system,"Helvetica Neue",Arial,sans-serif;--f-mono:"Cascadia Mono",Consolas,ui-monospace,"SF Mono",Menlo,monospace;color-scheme:light}
@media (prefers-color-scheme:dark){:root{--paper:#0f141b;--sheet:#161d26;--ink:#e3e8ee;--muted:#9aa4b2;--rule:#2a333f;--accent:#72b2e2;--accent-soft:#182a3a;--human:#c39be6;--human-soft:#271d33;--agent:#72b2e2;--agent-soft:#182a3a;--check:#5cc7b9;--check-soft:#132b28;--proj:#e5ad55;--proj-soft:#2d2313;--pack:#9ccc65;--pack-soft:#1e2a14;--absent:#77808c;--ok:#62c48e;--ok-soft:#15291f;--no:#f07c72;--no-soft:#321a19;--code-bg:#1d2530;color-scheme:dark}}
```

End it with:

```css
.frame.diagram svg{display:block;margin:0 auto;max-width:var(--dw,100%)}
@media (min-width:1180px){.frame.diagram.wide{width:min(calc(var(--dw) + 34px),calc(100vw - 48px));position:relative;left:50%;transform:translateX(-50%)}}
```

- Body: background `--paper`, text `--ink`, font `--f-body` 15.5px with line height 1.6. One centred column at most
  1080px wide, side padding at least 16px, running text near 70 characters, headings in `--f-display`.
- The header table as a compact metadata strip; "> " callouts with a left border in `--accent` on `--accent-soft`;
  a "Known gaps" callout in `--proj`; an "Example" callout dashed and muted.
- Tables in a bordered container that scrolls sideways; header cells `<th scope="col">`; the first cell of each body
  row `<th scope="row">` where it names the row, styled like a body cell in weight 600. Not in the header table, and
  not in a table of two parallel lists.
- Code in `--f-mono` on `--code-bg`; code blocks keep their spacing and scroll sideways.
- No horizontal page scroll at 400px: only tables, code blocks and diagram frames scroll inside themselves. Visible
  focus outlines. No emoji.

## Checks before reporting
Run these only when you wrote HTML.
- `html`: the number of `<pre class="diagram-source">` blocks equals the number of Mermaid blocks.
- `html publish`: every `<svg>` parses as XML (for example with Python's `xml.etree.ElementTree`).
- The file has no `<script>`, no `<link>`, no `@import`, and the policy meta above is in its head.
- No active content, whoever wrote it: no attribute that starts with `on` (`onload`, `onerror`, ...); no
  `javascript:` or `data:` address; no `<iframe>`, `<object>`, `<embed>`, `<base>`, `<form>`, `<img>`,
  `<foreignObject>` or `<meta http-equiv="refresh">`; no `href` other than a `#` fragment or a relative link with no
  `:` that does not start with `/` or `\` and names another overview document or a file in `_archive/`; and no `url(`
  that does not start with `url(#` (a marker reference such as `marker-end="url(#ah-1)"` is allowed). These checks
  read the tags and attributes, not the escaped text: `&lt;img onerror&gt;` in a table cell is data and passes.
- Heading and table counts equal the Markdown's.
- `html publish`: the number of SVGs equals the number of Mermaid blocks, and every node, edge and message label of
  each Mermaid block appears in its SVG.
