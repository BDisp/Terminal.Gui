# Markdown table sizing and grid layout — issue #5738

## Status and scope

Implementation plan, prepared 2026-10-21 against `develop` at `3f65bcb5c`, which includes the merge of [PR #5737](https://github.com/tui-cs/Terminal.Gui/pull/5737) at `aad50b669`. Working branch: `codex/5738-markdown-table-layout`.

This plan covers [issue #5738](https://github.com/tui-cs/Terminal.Gui/issues/5738). The steps below are proposed work; no library implementation or test execution is claimed by this document.

## Problem statement

Markdig already parses column preferences, explicit header status, spans, and blocks inside grid cells. Terminal.Gui loses this structure while lowering the AST. Its table renderer then independently reconstructs geometry for measurement, drawing, links, and selection. Extending each of those calculations independently would make spans and resizing unreliable.

Implement one normalized table model and one computed layout shared by every consumer. Preserve the structured inline content introduced in #5737. Keep ordinary pipe-table sizing and existing standalone entry points compatible, with explicit, documented corrections for rich-table structure and partial selection.

Out of scope: dependency updates, default Markdown pipeline changes, general editing or source navigation, HTML/KaTeX/Mermaid rendering, and a general-purpose document layout framework.

## Findings from the current implementation

| Area | Existing behavior and consequence |
| --- | --- |
| `MarkdownView.Parsing.cs`, `HandleTableBlock` | Treats the first encountered row as the header, skips subsequent `IsHeader` rows, and derives column count from the first row's cell count. A spanning row therefore cannot define the logical grid correctly. |
| `ExtractCellSegments` / `ExtractCellText` | Process only paragraphs, joining paragraphs with spaces. Lists and code blocks are omitted. |
| `TableData.cs` | Public constructor and arrays model one header plus rectangular inline-source body rows. `ColumnCount` is `Headers.Length`; arrays remain publicly mutable. |
| `MarkdownTableContent.cs` | Retains parsed segments and cached intrinsic width bounds. This is valuable work from #5737 to retain and generalize. |
| `MarkdownTable.cs` | Widths include two padding cells; the minimum is three cells per column. Drawing and hit testing rewrap content. Header height, body heights, border positions, and link regions use separate calculations. |
| `DrawBorders` | Draws the outside border using `Frame.Width`, while column boundaries use computed widths. Narrow overflow and unused trailing width need a deliberate rule. |
| `MarkdownView.Layout.cs` / `Markdown.cs` | Tables use `Dim.Fill`, with height captured before `Add` to avoid stale-width relayout. Table placeholders have zero width, and table overflow does not contribute to `_maxLineWidth`. Widths below `MIN_WRAP_WIDTH` skip layout. |
| `MarkdownView.Selection.cs` | Selecting any table placeholder reconstructs the entire table as pipe syntax. Full-document copying correctly returns the original document. |
| `MarkdownView.Ansi.cs` | Already uses real headless layout and drawing. Reuse it rather than introduce an independent ANSI table renderer. |

Verified upstream behavior in pinned Markdig 1.4.2:

- [`TableColumnDefinition.Width`](https://github.com/xoofx/markdig/blob/1.4.2/src/Markdig/Extensions/Tables/TableColumnDefinition.cs) is a percentage; zero means unspecified.
- [`PipeTableOptions`](https://github.com/xoofx/markdig/blob/1.4.2/src/Markdig/Extensions/Tables/PipeTableOptions.cs) makes separator inference opt-in.
- [`GridTableParser`](https://github.com/xoofx/markdig/blob/1.4.2/src/Markdig/Extensions/Tables/GridTableParser.cs) supplies proportional widths by default, explicit column indices, row/column spans, and can mark several preceding rows as headers. Grid tables will therefore adopt source proportions without changing the default pipeline.
- [`TableCell`](https://github.com/xoofx/markdig/blob/1.4.2/src/Markdig/Extensions/Tables/TableCell.cs) defaults `ColumnIndex` to `-1` and spans to one. Lowering must distinguish implicit placement from an explicit index.
- The upstream [table documentation](https://github.com/xoofx/markdig/blob/1.4.2/site/docs/extensions/tables.md) warns that normalization to pipe syntax cannot preserve rich grid tables. Do not use that normalizer for rich-table clipboard output.

## Recommended design decisions

### 1. Add a small library-owned model; retain the legacy constructor

Extend `TableData` additively. Proposed public names:

| API | Purpose |
| --- | --- |
| `TableColumnData` | Alignment and nullable `PreferredWidthPercentage`. Zero/null means automatic. |
| `TableRowData` | `IsHeader` and an ordered, read-only collection of originating cells. |
| `TableCellData` | Zero-based `ColumnIndex`, positive `ColumnSpan` / `RowSpan`, and `Markdown` block source. The enclosing row supplies the row coordinate. |
| `TableData.Columns` / `TableData.TableRows` | Complete structured representation, independent of `Headers` and `Rows`. |
| `TableData.CreateGrid (columns, rows)` | Named factory for structured standalone data; avoids ambiguous overloads and makes block-source semantics explicit. |

Each public/internal type gets its own file. Use immutable descriptors and defensive snapshots for the new factory. Do not expose Markdig nodes or internal layout classes in public signatures.

Compatibility contract:

1. Keep `TableData (string[] headers, Alignment[] alignments, string[][] rows)`, `TryParse`, `Headers`, `Rows`, `ColumnAlignments`, `ColumnCount`, `MarkdownTable.TableData`, and the standalone pipe-string `Text` setter.
2. Legacy cell strings retain their documented **inline-source** semantics; do not start interpreting leading list markers as block syntax in those strings. Legacy data creates one header row and unit spans, with automatic widths and compact row borders.
3. Structured cells use block Markdown source. Parse standalone structured sources once on assignment with the existing default pipeline and common lowering routines. AST-backed tables bypass that parse and retain their already-lowered content, including custom-pipeline semantics.
4. For structured data, legacy arrays are documented lossy projections: `Headers` contains the first explicit header row, or is empty; `Rows` contains every other logical row in order, including additional header rows. Put an originating cell's source in its starting slot and empty strings in covered slots. `ColumnCount` comes from the grid, never the projection. Rendering and copy must use the complete model.
5. Preserve legacy array behavior. Reassigning `TableData` is the explicit refresh boundary after array edits; do not add an unnoticed mutation-tracking contract. Test mutation followed by reassignment to prevent stale retained content.
6. Keep the existing single-line `CalculateTableHeight (TableData)` result for legacy data. For structured data, calculate a topology-based single-line estimate and document that actual wrapped height requires layout. Do not present the static estimate as authoritative geometry.

Normalize placement once: derive column count from definitions and maximum cell extent; resolve implicit AST indices into the next unoccupied slot; retain empty logical rows occupied by row spans; fill uncovered slots with empty cells. Validate coordinates, positive spans, bounds, and overlaps before allocation. Reject invalid explicit `CreateGrid` input with documented argument exceptions and checked arithmetic. Preserve well-formed parsed tables without inventing a header. Nonfinite or negative public width preferences are invalid; zero is automatic.

### 2. Keep lowered cell content separate from public source strings

Generalize `MarkdownTableContent` into a normalized grid of cell origins, occupancy, row metadata, column preferences, and retained block content. Store each originating cell exactly once; covered positions point to its identity.

Reuse `InlineRun`, `StyledSegment`, `LowerCellInlines`, `MarkdownAttributeHelper`, and the word/grapheme behavior in `WrapSegments`. Carry block boundaries, indentation/continuation prefixes, code language, and wrap policy instead of flattening everything to inline text.

Extract the necessary paragraph/list/code lowering helpers to accept an explicit output sink or local context. Cell lowering must not append into the document's `_blocks`, mutate heading slug state, or create document-level table SubViews. Avoid constructing a `Markdown` view per cell and avoid serializing/reparsing the AST for display.

Required cell behavior:

- Paragraphs are separated by one blank display line; soft breaks remain spaces; explicit hard breaks retain their line boundaries.
- Lists preserve order, bullets/numbers, nesting indentation, empty items, and continuation indentation.
- Code blocks preserve whitespace, blank lines, and language for highlighting. Long code lines wrap at grapheme boundaries inside the cell; copying retains the original unwrapped source. There are no cell-local copy buttons or scrollbars.
- Quotes/headings can reuse existing textual prefixes/styles without adding document anchors. Unsupported cell block kinds retain a readable textual fallback rather than disappearing. Nested tables and raster images are not new interactive cell controls in this PR; document their fallback.
- Public source strings and clipboard serialization never become the input to AST-backed measurement or rendering.

### 3. Define width allocation in terminal cells

Separate intrinsic content measurement from width-dependent layout. Cache intrinsic bounds until content changes; calculate wrapping once per cell per effective width.

Definitions for `n` logical columns:

- `x[i]`: content width, excluding padding.
- Each ordinary column has one padding cell on each side; each border track occupies one terminal cell.
- Total occupied width is `sum(x) + 3*n + 1`.
- A cell spanning `k` columns has inner width `sum(x over span) + 3*(k - 1)`: the removed separators and intermediate padding become usable content area. Apply only one left/right padding pair to the merged cell.
- Natural width is the largest unwrapped display-line width; preferred minimum is the longest unbroken word, including required prefixes. The hard minimum permits at least one complete grapheme and is at least one terminal cell. Empty content has a hard minimum of one.

For spanning content, build interval constraints instead of counting its complete width in every covered column. Start with nonspanning bounds; process spans by increasing span length and then row/column order. Distribute each positive deficit evenly among covered columns, with leftover cells going left to right. Apply this separately to hard, preferred-minimum, and natural bounds. Overlapping intervals only increase bounds, so later constraints cannot invalidate earlier ones.

**No positive hints:** retain the existing natural-fit / widest-column collapse / left-wins tie behavior for ordinary pipe and legacy standalone tables. Generalize to span bounds without stretching natural content merely to fill the view. Preserve existing width algorithm assertions. The table's outer border can retain the legacy full-view envelope when wider than occupied tracks; represent the trailing blank region explicitly as non-cell space so hit testing never assigns it to the final cell.

**Positive hints:** use the available inner-content budget after borders and padding. Preferences apply to content widths, not the entire bordered width, and can expand a column beyond its natural width. There is no new public maximum-width setting in this PR.

1. With all columns specified, normalize positive percentages to a total of 100, even if input totals differ from 100.
2. With partial hints totaling less than 100, allocate the remaining percentage to automatic columns in proportion to their natural widths (at least one each).
3. With partial hints totaling 100 or more, reserve automatic columns' preferred minima and allocate the remaining budget among hinted columns in their specified ratio. If preferred minima cannot fit, use their hard minima instead. This handles inferred pipe separators with a zero-width column without starving that column.
4. Apply preferred minima when their combined requirement fits. Pin undersized columns to their minima, then redistribute the remaining budget among unpinned columns in proportion to their targets. If preferred minima cannot fit, use hard minima and permit word splitting. Span constraints contribute to these bounds.
5. Round by largest fractional remainder, breaking ties by logical column index. Apply rounding only after constrained allocation; verify that the allocated total matches the budget whenever fitting is possible. Use bounded passes that freeze a column or consume a positive deficit, not a loop that can stall on impossible minima.

Example: two hinted columns at 20% / 80% in a width of 47 have 40 content cells after seven cells of padding/borders. With inactive minima, allocate content widths 8 and 32; padded tracks are 10 and 34. In a width of 46, the 39 content cells round to 8 and 31. The exact 1:4 preference describes content space, not padded tracks.

**Narrow overflow:** never collapse borders, erase content, use negative widths, or split a grapheme to fit. Below the hard minimum, retain the minimum logical table width and expose horizontal overflow. Keep every cell's content in layout even when clipped by the viewport. A CJK/emoji cell may require a two-cell minimum; this deliberately corrects clipping at a one-cell content width. Width zero produces no visible drawing but must terminate and recover on resize. Exercise viewport widths 0–3 despite the current Markdown layout guard.

Use `string.GetColumns()` and complete grapheme clusters across adjacent style runs for every phase. For a grapheme split across style boundaries, keep the complete cluster at one coordinate; retain source/run metadata and use the base segment's rendering attributes and link ownership for the indivisible displayed cluster. Test and document that deterministic policy.

### 4. Compute one layout snapshot

Introduce an internal `MarkdownTableLayout` result and a small layout engine. Its output includes:

- Column tracks, row tracks, actual occupied width, outer envelope, and total height.
- Each originating cell's outer/content rectangles and cached positioned lines/graphemes.
- Border segments, including suppressed edges inside spans.
- Link occurrence identities and the exact rendered rectangles for each wrapped fragment.
- Text positions needed for selection, including logical cell identity and block/line offsets.

Build row heights after widths and wrapping. Give nonspanning cells at least one content row. Process row-span deficits by increasing span length and origin order, distributing extra height evenly with leftovers from top to bottom. A spanning cell can use suppressed internal border rows as content space. Top-align cells; do not add a vertical-alignment API.

Keep compact pipe-table borders: top/bottom, column separators, and the explicit header/body boundary. Grid tables and `CreateGrid` use row separators. Mark only actual header rows bold. At every row/column boundary, draw an edge only where adjacent occupancy belongs to different cells; split edges around spans. Header boundaries must not cross a spanning cell. Feed these segments into the existing `LineCanvas` intersection machinery.

The snapshot is the only source for drawing, height, link hit testing, and selection. None of those consumers should invoke `WrapSegments` independently. Preserve line/link metadata while slicing styled content; do not reduce links to URL equality, since two occurrences can have the same destination.

### 5. Make sizing and scrolling stable across resize

Use the viewport or explicitly assigned content width as the table constraint. Preserve the existing contribution from unrelated unwrapped document blocks where applicable. Compute all tables against that fixed constraint, then include table overflow in the document's final content extent. Do not feed the resulting overflow width back into hinted allocation, which would make tables expand on successive layout passes.

Give table SubViews dimensions derived from the layout result rather than allowing `Dim.Fill` against an expanding document width to change their measurement. Set standalone `MarkdownTable` content extent so horizontal scrolling reaches overflow. Clamp viewport offsets after resizing.

Replace zero-width table placeholders with references to the table instance/layout and local row index, plus usable display widths. Reserve exactly the snapshot's height, including border rows; following paragraphs and anchors must start at the correct row.

Invalidate layout on data assignment and constraint changes. Invalidate measured block content when the syntax highlighter can change returned text. Theme/attribute-only changes redraw without changing cell coordinates. Preserve existing focus behavior during rebuild, and retain a focused link by stable occurrence identity when it still exists. Repeated narrow → wide → narrow layout must yield the same geometry on the first and third pass.

### 6. Give copying an explicit representation

| Selection | Output |
| --- | --- |
| Whole document | Original document source, as today. |
| Complete ordinary pipe table inside a larger selection | Existing safe pipe-table serialization, preserving resolved links, escaping, and inline semantics from #5737. With inferred widths, retain original separator proportions where available. |
| Complete parsed rich table | Original table block source once, including grid geometry and blocks. Retain the exact table slice during parsing; verify boundaries with surrounding paragraphs, references, CRLF, and table-ending documents. |
| Partial table/cell/header selection | Selected visible text in display-row order. Include selected alignment/padding spaces between text, omit box-drawing glyphs, trim trailing layout padding, and preserve selected line breaks. Do not repeat a merged cell for its covered slots. |
| Programmatically constructed rich table without source | Plain text in logical cell-origin order: tab-separated columns, newline-separated rows, empty covered slots, with multiline cell fields quoted and embedded quotes doubled. This is a documented delimited-text fallback, not a promise of lossless Markdown serialization. |

This intentionally changes the current behavior where touching one table line copies the entire table. Determine complete-table coverage from selection coordinates and the table's actual extent; reaching a zero-width placeholder must no longer masquerade as selecting the whole document. Test full selection through `SelectAll` as well as manual keyboard and mouse selections.

For rich standalone data, synchronize `MarkdownTable.Text` using the same documented source/delimited-text policy; preserve pipe text behavior for legacy data. Its existing setter remains a pipe-table input entry point, so rich programmatic output is not promised to round-trip through that setter. Document this distinction rather than silently converting spans to a misleading pipe table.

Apply the same grapheme boundary policy to highlights and copying. A selection touching any occupied part of a wide grapheme includes the complete grapheme once. Translate screen/viewport/document/table coordinates in one place; borders and padding never activate links.

ANSI rendering continues through the existing headless driver path. At a constrained width it must match the visible on-screen viewport, including clipping of deliberate horizontal overflow; it must not silently render a different wider table.

## Implementation sequence

1. **Capture compatibility and AST fixtures.** Add granular tests for existing standalone constructors, natural widths, alignment, inline fidelity, data reassignment, and exact pipe rendering. Add parsed fixtures for inferred widths, headerless/multiple headers, row/column/mixed spans, paragraphs, lists, and code. Verify AST metadata before using a fixture as evidence for layout behavior. Use public behavior or typed internal helpers, not reflection.
2. **Add descriptors and normalize content.** Implement the additive `TableData` model, validation, projections, structured content, exact rich-table source retention, and common lowering helpers. Keep legacy rendering working through an adapter while the new geometry is introduced. Add source/metadata tests in the same commit.
3. **Implement sizing and layout.** First port existing rectangular behavior into the common snapshot, proving compatibility. Then add preferences, interval constraints, row spans, cached wrapping, and edge segments. Add independently specified width/height and border expectations plus deterministic resize invariants.
4. **Switch all render/input consumers.** Replace rectangular row loops and repeated wrapping with snapshot traversal. Update link navigation, click regions, focus restoration, content extent, placeholders, and ANSI integration together so a cell cannot draw at one position and respond at another.
5. **Implement selection and copy.** Use layout coordinates for partial selections and retained source for complete rich tables. Update old tests that intentionally asserted whole-table expansion for partial selection; retain their no-duplication intent and all #5737 escaping/URL tests.
6. **Document and demonstrate.** Add API XML docs and a table-layout guide; add a focused UICatalog scenario with width controls and inference toggle. Keep the inference option on that scenario's pipeline, not the global default. Include legacy/structured standalone examples.
7. **Validate and prepare the PR.** Run focused and full checks, publish/run native AOT, record and inspect multiple widths, review warnings and formatting, and prepare the PR against `develop` using the repository description conventions.

Use cohesive commits in this order in one feature branch. Internal adapters are transitional; remove duplicate geometry before completing the PR. The issue is complete only after rendering, interaction, clipboard, and validation all use the same model.

## Expected file changes

| Files | Planned change |
| --- | --- |
| `Terminal.Gui/Views/Markdown/TableData.cs` | Additive model, factory, validation/projections, compatibility docs. |
| New `TableColumnData.cs`, `TableRowData.cs`, `TableCellData.cs` | Library-owned public descriptors. |
| `MarkdownTableContent.cs`, `IntermediateBlock.cs`, `RenderedLine.cs` | Retained cell blocks, source provenance, occupancy, and table-line mapping. |
| New `MarkdownTableLayout.cs`, `MarkdownTableLayoutEngine.cs` and narrowly needed internal records | Pure measurement/placement, cell rectangles, border/link/text geometry; one type per file. |
| `MarkdownView.Parsing.cs`, `MarkdownInlineParser.cs` as needed | Faithful table lowering and reuse of existing inline helpers. |
| `MarkdownTable.cs` | Integrate layout, rendering, navigation, hit testing, standalone behavior; split partial files only where it improves reviewability. |
| `Markdown.cs`, `MarkdownView.Layout.cs` | Stable layout constraints, content extents, resize/focus handling, placeholder placement. |
| `MarkdownView.Selection.cs`, `MarkdownView.Drawing.cs`, `MarkdownView.Mouse.cs` | Partial/full selection mapping and consistent coordinate conversion. |
| `MarkdownView.Ansi.cs` | Prefer no new renderer; change only if extent integration requires it. |
| `Tests/UnitTestsParallelizable/Views/Markdown/` | Existing regression coverage plus focused sizing, topology, cell-block, interaction, and copy test classes. |
| `Tests/NativeAotSmoke/MarkdownSmokeTests.cs` | Assert proportional/headerless/spanning output and a wrapped spanning-cell link. |
| New `Examples/UICatalog/Scenarios/MarkdownTables.cs` | Demonstrate inference, spans, headers, blocks, and resizing through `ScenarioRunner`. |
| New `docfx/docs/markdown-tables.md`, relevant DocFX navigation, `docfx/docs/views.md` | Document allocation, overflow, standalone APIs, examples, and copy compatibility changes. |

Read the applicable `.claude/rules/` files and `POST-GENERATION-VALIDATION.md` before code changes. Follow `CONTRIBUTING.md`, avoid unrelated fragile-area refactors, and add the required AI attribution comments to new tests.

## Verification and acceptance matrix

| Requirement | Evidence |
| --- | --- |
| Inferred proportions and compatibility | Exact 20:80 allocation with inactive minima; deterministic rounding; all-zero/no-hint cases retain current ordinary table results. |
| Partial/invalid/overconstrained widths | Zero, null, partial, over-100 totals, nonfinite/negative public input, empty cells, long words, and widths around every minimum threshold. Bounded execution and nonnegative sizes. |
| Header fidelity | Rendered buffer text and bold attributes for no headers, one header, and multiple headers; every logical row appears once. |
| Span geometry | Small exact character-buffer fixtures for column, row, and mixed spans, including T/cross junctions, header transitions, and spans wider than their neighbors. Assert content is not overwritten by borders. |
| Block preservation | Multiple paragraphs, leading/consecutive/trailing hard breaks, nested and empty list items, code indentation/blank lines, and wrapping across styles. |
| Resize and scroll | 24 → 80 → 24 and below-minimum widths; compare widths, rectangles, heights, border maps, link regions, document extent, following-block positions, and clamped scroll offsets. Repeated identical layout is stable. |
| Links | Keyboard forward/backward traversal and Enter; mouse clicks on wrapped/aligned/spanning fragments; same-URL distinct occurrences; relative anchors; padding/border misses; nonzero view offsets and both scroll axes. |
| Selection/copy | Partial cells, headers, spans, full rich/pipe tables, mixed surrounding content, adjacent tables, SelectAll, CRLF, escaped pipes, code delimiters, and original URL targets. Assert each selected grapheme occurs once. |
| Unicode/ANSI | CJK, combining sequences across style boundaries, ZWJ emoji, and graphemes adjacent to cell edges. Compare ANSI-decoded positions/text with the driver buffer at the same width. |
| Legacy standalone APIs | Constructor, `TryParse`, pipe `Text`, `CalculateTableHeight`, projections, alignment, data replacement, and legacy array mutation followed by reassignment. |
| Native AOT | Publish and execute the existing smoke app; assert geometry/content and activation, not just construction. No new unsuppressed trim/AOT diagnostics. |
| Human-visible result | UICatalog recordings at narrow and wide terminal widths; inspect casts and rendered frames, including scrolling and activation. Attach a GIF to the PR and retain reproducible commands/casts as evidence. |

Prefer driver-buffer tests and pure internal layout tests for the bulk of coverage. Use instance applications/InputInjector only for behavior that needs focus, navigation, or application dispatch. Avoid global/static state and private reflection. Small deterministic generated grids can additionally assert occupancy, non-overlap, and repeatable allocation without a new testing dependency.

Run from the repository root during implementation:

```bash
dotnet restore
dotnet build --configuration Debug --no-restore
dotnet test --project Tests/UnitTestsParallelizable --no-build --filter-namespace '*Markdown*'
dotnet test --project Tests/UnitTestsParallelizable --no-build
dotnet test --project Tests/UnitTests.NonParallelizable --no-build
dotnet publish Tests/NativeAotSmoke/NativeAotSmoke.csproj --configuration Release --output ./aot-publish
DisableRealDriverIO=1 ./aot-publish/NativeAotSmoke --smoke-test
dotnet build Examples/ScenarioRunner/ScenarioRunner.csproj --configuration Release
```

Use the repository's xUnit v3/Microsoft Testing Platform filters; confirm supported namespace filtering with the local runner, or use `--filter-class` for the focused classes. Do not use the old `FullyQualifiedName~...` syntax. Run warning-sensitive buffer tests with a consistent terminal environment, following #5737's evidence (`TERM=xterm-256color`, `COLORTERM=truecolor`, `NO_COLOR` unset).

For visual verification, follow `Scripts/tuirec/README.md`. Record at least 40 and 100 columns, exercise the scenario's width control through narrow → wide → narrow, activate a wrapped link, and inspect both `.cast` and rendered frames. Unit tests remain authoritative for Unicode cell geometry where the GIF renderer has font limitations. Keep recordings as PR evidence rather than committing incidental generated files without a reason.

Before submission, run the repository's formatting/checklist on changed files and `git diff --check`. Report the actual build/test/AOT results, any existing failures, and warning deltas. Suggested PR title: `Fixes #5738. Preserve Markdown table sizing and grid geometry`. Include `- Fixes #5738` near the top and the required fork/branch checkout instructions at the end.

## Principal risks and controls

- **Accidental public API expansion:** keep block/layout objects internal; the public additions describe only columns, rows, cell source, and spans. Document the legacy projections before implementing consumers.
- **Undoing #5737:** retain parsed inline metadata and cached intrinsic measurements; make its existing regression suites a gate for each integration step.
- **Resize feedback and stale coordinates:** freeze the width constraint per pass and install one completed snapshot for all consumers. Include overflow widths and zero-width recovery in tests.
- **Misleading rich-table copy:** retain original table source and explicitly document the source-free delimited-text fallback. Do not claim that pipe serialization preserves spans.
- **Scope growth from cell blocks:** extract only the needed lowering/layout helpers; avoid a nested interactive view hierarchy or a rewrite of general Markdown rendering.
- **Border and grapheme discrepancies:** use occupancy-driven edge segments and positioned complete graphemes, with exact driver-buffer assertions across spans and clipping boundaries.
