# Inline output validation

Validated on macOS arm64 with .NET SDK 10.0.201, based on PR #5714 at
`2e5708d1ed35ae1d6bff895db1dfddca7426a7ca` (2026-10-13).

| Check | Result |
| --- | --- |
| Debug and Release solution builds | Passed; no new warnings in changed files |
| New inline output tests | 49 passed |
| Existing application dispatch tests | 46 passed |
| Existing physical output failure tests | 33 passed |
| Nonparallel suite | 33 passed |
| Integration suite | 437 passed |
| Full parallel suite | 17,899 passed; 4 failed; 17 skipped (17,920 total) |
| Unchanged #5714 full parallel comparison | 17,850 passed; the same 4 failed; 17 skipped (17,871 total) |
| macOS PTY, 50 columns x 12 rows | Shell sentinels, all 24 numbered messages, and submitted text preserved once and in order |
| PTY, full-height 50 x 3 live region | Passed the same history assertions |
| PTY, one-cell 1 x 1 terminal | Passed history assertions and exited cleanly |
| Controlling PTY with redirected stdout | Passed; redirected file contained no terminal bytes |
| tmux, 50 x 12 and full-height 50 x 3 | Actual pane history preserved shell sentinels and ordered messages without stale composer copies |
| Recorded visual output | GIF reviewed through a still capture; cast replayed into a history-aware terminal model |

The four full-suite failures reproduce on the unchanged dependency:

- `ViewDrawingClippingTests.Draw_WithBorderSubView_DrawsCorrectly`
- `SchemeColorNoneDerivationTests.Focus_WithWhiteTerminal_RendersResolvedColors`
- `SchemeColorNoneDerivationTests.Focus_WithNoDefaultAttribute_FallsBackToBlackAndWhite`
- `SchemeColorNoneDerivationTests.Focus_WithDarkTerminal_RendersResolvedColors`

Their expected RGB escape sequences differ from emitted 16-color sequences. Earlier
parallel runs intermittently also reported the Windows case of
`AllDriverTests.All_Drivers_When_Clipped_AddStr_Glyph_On_Second_Cell_Of_Wide_Glyph_Outputs_Correctly`;
it passed the final full-suite run. This PR does not change color quantization or those tests.
Existing warnings include dependency advisories, DropDownList XML documentation, and
an analyzer warning in RunAsyncTests. The final disposal cleanup guard was followed by
focused print and existing output-failure regression runs and another Release build.

The automated terminal model exhaustively covers small terminal geometries, including
multi-screen output, full-height regions, delayed autowrap and preceding shell output.
Failure tests cover each physical write in a multi-screen insertion, unknown-placement
suppression, cancellation after claim, live-frame retry, raster cleanup, captured UI
awaiters, reentrant callbacks, snapshot/teardown races and a blocked writer during disposal.
The model is not a substitute for platform terminal validation.

To reproduce PTY assertions, follow [the example README](README.md). The optional `pyte`
package is a test harness dependency and is not part of Terminal.Gui. `tuirec` alone does
not answer CPR; its missing-CPR fallback capture cannot establish shell-output preservation.

Windows ConPTY/Windows Terminal, conhost, a Linux PTY, real-terminal Unicode/font width
agreement, and resize during physical output remain unvalidated here. Resize preservation
is outside this PR; the existing resize clear/reset remains. These platform checks are
required before treating the draft as ready for merge.

## Copilot review follow-up: 2026-10-14

Row counting now traverses cached text and grapheme widths without constructing rows,
span objects, grapheme strings, or expanded tab cells. Rendering combines adjacent text
with equal attributes, expands tabs in row-sized runs, and carries the measured column
count into ANSI output so it does not segment the rendered text again. Counting and
rendering reuse the same grapheme-width policy as `StringExtensions.GetColumns`.

Maximum-payload regression tests cover both ASCII and tabs. After warming Unicode width
caches, counting must allocate no more than 4 KB. Rendering 4,194,303 ASCII characters at
4,096 columns produces 1,024 spans (one per physical row) with an allocation budget below
12 MiB. Mixed styles, cross-span graphemes, empty spans, combining marks, wide glyphs,
and tab wrapping remain covered.

The updated inline-output suite passed all 60 tests. Debug and Release builds passed;
nonparallel tests passed 33/33, and integration tests passed 437/437. The full parallel
suite reported 17,909 passed, 5 failed and 17 skipped (17,931 total): the four known
color-output failures above and the previously documented intermittent Windows clipping
case. No new failing test names were observed. The attached macOS PTY/history check
with redirected stdout also passed after the fix. Platform validation limits remain
as described above.
