# Markdig dependency audit

Reviewed on 2026-10-20 for Terminal.Gui PR #5736, upgrading Markdig to 1.4.2.

## Dependency history

The merged Markdown view first referenced Markdig 0.39.0 in commit `c6d67e362`
(#4955, 2026-04-17). The subsequent package bump commits below changed only
`Directory.Packages.props`; they did not add release-specific regression tests.
Existing CI coverage alone therefore does not establish coverage of each upstream fix.

| Date | Terminal.Gui commit | Version change |
| --- | --- | --- |
| 2026-04-18 | `0cde267fd` (#4989) | 0.39.0 → 1.1.2 |
| 2026-04-20 | `0c7dee4b4` (#5017) | 1.1.2 → 1.1.3 |
| 2026-06-15 | `8130f52f0` | 1.1.3 → 1.3.0 |
| 2026-06-17 | `676c9ec25` | 1.3.0 → 1.3.1 |
| 2026-06-19 | `491fadafb` (#5503) | 1.3.1 → 1.3.2 |
| 2026-09-21 | `22ef38a6d` (#5685) | 1.3.2 → 1.4.0 |
| PR #5736 | `56efec662` | 1.4.0 → 1.4.2 |

The May 5 package maintenance commit also mentions Markdig but retains 1.1.3.
The open 1.4.1 dependency PR is not a merged version transition.

## Scope and results

Reviewed upstream release notes from 0.39.1 through 1.4.2, relevant parser and
extension source, and upstream regression examples against Terminal.Gui's AST
lowering, table conversion, layout, ANSI rendering, and native smoke app.
The new integration tests exercise Terminal.Gui output and table data; they do
not attempt to duplicate Markdig's entire CommonMark test suite.

| Upstream versions | Relevant changes | Terminal.Gui result |
| --- | --- | --- |
| [0.39.1–0.43.0](https://github.com/xoofx/markdig/compare/0.39.0...0.43.0) | Grid bounds, definition-list parser safety, CommonMark 0.31.2, pipe tables after paragraphs, multiline inline code and backticks | Parser fixes apply automatically. Added integration assertions for open grid cells, tables after paragraphs, and code in table cells. Existing inline-code tests remain applicable. |
| [0.44.0–0.45.0](https://github.com/xoofx/markdig/compare/0.43.0...0.45.0) | Link normalization, supplementary Unicode, linear-time pipe parsing | Unicode is retained through the renderer. Supplementary-character coverage added. Parser performance applies automatically; Terminal.Gui still performs its own layout work. |
| [1.0.0–1.1.2](https://github.com/xoofx/markdig/compare/0.45.0...1.1.2) | Removed obsolete APIs, AST authoring APIs, CJK emphasis, emoji in table cells, reference source spans | Build verifies the APIs we use. Added custom-pipeline CJK and emoji table tests. AST mutation, source-span, HTML diagram and KaTeX changes do not add terminal rendering capabilities automatically. |
| [1.1.3](https://github.com/xoofx/markdig/releases/tag/1.1.3) | Abbreviations corrupting adjacent emphasis | Found an additional integration bug: Terminal.Gui discarded `AbbreviationInline` labels. Fixed both normal text and table extraction, with tests for labels before and inside emphasis. |
| [1.2.0–1.3.0](https://github.com/xoofx/markdig/compare/1.1.3...1.3.0) | Optional nested alerts, CJK entities, unmatched subscript in tables, configurable nesting limits, ordered lists in quotes | Added custom-pipeline nesting-limit and unmatched-subscript table assertions. Existing quote/list tests remain applicable. Nested alerts remain opt-in. |
| [1.3.1–1.3.2](https://github.com/xoofx/markdig/compare/1.3.0...1.3.2) | Non-ASCII slug normalization, superscripts, emoji table alignment | Added an emoji alignment/content test. Terminal.Gui generates its own heading slugs and does not call `LinkHelper.Urilize`. Superscripts continue using terminal emphasis rather than raised glyph positioning. |
| [1.4.0](https://github.com/xoofx/markdig/releases/tag/1.4.0) | Optional strict GFM tables, nested link labels, unmatched emphasis closers, emoji/emphasis interaction | Added strict-GFM column normalization, nested link label, and trailing-text regression tests. The default permissive table syntax remains unchanged. |
| [1.4.1–1.4.2](https://github.com/xoofx/markdig/compare/1.4.0...1.4.2) | Definition syntax and continuation, empty lists, HTML boundaries, setext headings, AOT metadata | Fixed dropped definition terms and empty item markers. Hard breaks now produce rendered lines with continuation indentation. Added parser-to-renderer regression tests and executable AOT assertions. |

## Deliberate boundaries and follow-up opportunities

- To enable strict GFM tables, supply a `MarkdownPipeline` built with
  `UsePipeTables (new PipeTableOptions { UseGfmRules = true })`. Integration tests
  confirm row padding and truncation. Switching the default would change accepted syntax.
- To enable CJK-friendly emphasis, emoji shortcodes, or nested alerts, supply a
  custom pipeline with the corresponding extension options. These are optional
  upstream features; this PR does not change the default pipeline.
- `InferColumnWidthsFromSeparator` (0.41.0) produces Markdig column-width metadata,
  but `TableData` currently carries only text and alignment. Honoring those widths
  needs a separate table-sizing API and layout design.
- Grid-table row/column spans and arbitrary block content exceed the current
  `MarkdownTable` model. The first grid row is displayed as its header. This PR
  verifies content preservation for the upstream open-cell regression, not full grid fidelity.
- Table cells are converted to Markdown-like text and parsed again by the simpler
  `MarkdownInlineParser`. This limits nested formatting, arbitrary code delimiters,
  and explicit multiline cell layout. Replacing that path with structured styled
  cells is a separate change.
- Alert kinds are not styled as dedicated terminal alert headers. Nested alert
  parsing can be enabled, but that does not imply full alert presentation support.
- `FindClosestBlock` / `FindBlockAtPosition` fixes concern reference/footnote groups.
  Terminal.Gui does not call them and discards the parsed document after lowering.
  Source-to-preview navigation would first require retained source mappings.
- Upstream HTML, normalization, and round-trip renderer changes are not directly
  consumed: Terminal.Gui parses Markdown and renders the AST itself.

## Verification

`MarkdownDependencyRegressionTests` covers the integration cases above.
The full Markdown test namespace also covers existing layout, drawing, selection,
links, code blocks, tables, and ANSI output.

`Tests/NativeAotSmoke/MarkdownSmokeTests.cs` checks actual native rendering of
headings, anchors, definitions, empty list items, hard breaks, abbreviations,
reference links, fenced code, tables, and optional emoji/CJK pipeline construction.
It throws on mismatched output so the existing Linux and Windows AOT CI steps fail
on a regression. The interactive AOT view gallery now includes Markdown too.

The [PTY recording](../docfx/images/views/MarkdownDependencyRegression.gif) shows
the setext hard break, definition term and indentation, empty bullet,
abbreviation label with adjacent emphasis, and table output. Its asciinema cast
was read back and a rendered frame inspected during validation.

Local verification used .NET 10.0.113 on Linux ARM64. The solution build passed,
all 417 Markdown tests passed, all 35 nonparallel tests passed, and all 438
integration tests passed. The native executable was published with GCC and its
`--smoke-test` assertions and application lifecycle passed. Existing CI uses its
platform toolchain on Linux and Windows.
