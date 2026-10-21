# Issue #5738 implementation and validation

Implemented the [Astra plan](5738-markdown-table-layout.md) on
`codex/5738-markdown-table-layout`, based on `develop` at `3f65bcb5c`, including
merged #5737. Implementation commit: `58bc2f1d4`.

## Result

- Added library-owned column, row, and cell descriptions and `TableData.CreateGrid`;
  retained legacy constructors, inline-source semantics, mutable legacy arrays,
  pipe `Text` input, and the header-only table height/separator.
- Retained parsed blocks, explicit headers, source proportions, and row/column
  spans. AST-backed cells never serialize/reparse for display.
- Centralized cached bounds, track allocation, wrapped glyph positions, cell
  rectangles, borders, and link identities. Rendering, hit testing, partial copy,
  selection highlighting, and ANSI output use the resulting geometry.
- Included table overflow in document extents without feeding it back into the
  next allocation. Preserved focused links across resize and disabled invisible
  table navigation below the normal viewport minimum.
- Added Markdown Tables UICatalog examples, a sizing/copy guide, native assertions,
  and documentation recordings/casts at 40 and 100 terminal columns.

## Verification

Environment: Ubuntu Linux ARM64, .NET SDK 10.0.113. Unit tests used
`TERM=xterm-256color`, `COLORTERM=truecolor`, and `NO_COLOR` unset.

| Check | Result |
| --- | --- |
| `dotnet restore` | Passed. |
| `dotnet build --configuration Debug --no-restore` | Passed; zero errors, no new warning diagnostics. |
| Full parallelizable test project | **18,346 passed, 17 skipped**, zero failures; 18,363 total. |
| Full nonparallelizable test project | **35 passed**, zero failures/skips. |
| Markdown coverage | 599 cases in the final parallel run; the preceding focused run passed 598, followed by the added header-only compatibility case. |
| Navigation coverage | All 235 `AllViewsNavigationTests` cases passed after the zero-width correction. |
| Native publish and execution | Linux ARM64 Release AOT publish passed; `NativeAotSmoke --smoke-test` passed with the expanded Markdown assertions. |
| Actual wrapped spanning link under AOT | Asserted height exceeds one content line, then activated its original URL. |
| Formatting | Applied `dotnet format whitespace` only to changed C# files, using `.editorconfig`; completed `.claude/POST-GENERATION-VALIDATION.md` review and `git diff --check`. |
| Visual verification | Both casts have no error/unknown/usage output; F2/F3 resize and original spanning-cell URL activation are present. Inspected GIFs and extracted frames. |

Build diagnostics retained from the existing repository concern package advisory
warnings, two `DropDownList` XML references, and the existing `RunAsyncTests`
cancellation-token analyzer warning. No modified file introduces a new warning;
no new unsuppressed trimming/AOT diagnostics were emitted.

The focused ANSI/screen comparisons read complete graphemes and skip their buffer
continuation cells, matching the output driver's traversal. Tests cover CJK,
combining sequences across style boundaries, and ZWJ emoji. GIF font fallback can
display emoji components separately; driver/ANSI assertions verify actual cell
positions independently of that font rendering.

## Recording evidence

See [the guide and reproduction commands](../docfx/docs/markdown-tables.md#recordings).
The GIFs and casts are committed as linked documentation examples; ordinary
recording output remains ignored by the repository.

- [40-column GIF](../docfx/images/views/MarkdownTables40.gif) and
  [cast](../docfx/images/views/MarkdownTables40.cast).
- [100-column GIF](../docfx/images/views/MarkdownTables100.gif) and
  [cast](../docfx/images/views/MarkdownTables100.cast).

## Documented compatibility decisions

Partial table selection now copies only selected visible text, rather than
expanding to the entire table. Complete parsed grids preserve their source;
source-free structured grids use quoted tab-separated text. Legacy array
projections are deliberately lossy for rich grids. Nested tables and raster
images retain text/alt-text fallbacks rather than creating interactive cell
views. These decisions follow the plan and are documented in the public guide.
