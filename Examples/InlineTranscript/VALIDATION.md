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

Actual Windows Terminal/conhost window rendering, a Linux PTY, real-terminal
Unicode/font width agreement, and resize during physical output remain
unvalidated here. Resize preservation is outside this PR; the existing resize
clear/reset remains. These platform checks are required before treating the
draft as ready for merge.

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

## Windows review follow-up: 2026-10-14

Validated PR #5715 on Windows ARM64 with .NET SDK 10.0.401. The two new Copilot
findings reproduced before the fix: a zero-height runnable allowed a queued
message to print against the physical screen, and a throwing `ClearedContents`
subscriber left the already-printed request incomplete. Focused regressions now
verify zero-height deferral, no erase of preceding shell output, resumption after
growth, successful task settlement, live redraw invalidation, and release of
all 256 queue slots after the callback throws.

Debug and Release solution builds passed with no new warnings in changed files.
All 62 inline-output tests passed, as did the 33 nonparallel and 437 integration
tests. The full parallel rerun reported 17,905 passed, 4 failed, and 16 skipped
(17,925 total); the four failed methods also failed on the unchanged stacked
base on this machine. One earlier full run exceeded the allocation test's 4 KB
budget by 3,800 bytes; both isolated cases and the full rerun passed without a
layout change.

The inline transcript also passed a pywinpty/pyte history check through both
Windows ConPTY and the legacy WinPTY bridge at 50x12, full-height 50x3, and
1x1. Each run preserved two preceding shell sentinels and all 24 numbered
messages once and in order. The submitted text was preserved at 50x12 and
50x3; the redirected-stdout case emitted no terminal bytes to stdout. These
checks do not inspect an actual Windows Terminal or conhost window. Linux PTY
validation remains outstanding.

## Copilot nested-modal and payload follow-up: 2026-10-14

The rendering guard now tracks active sessions. A synchronous modal opened by
`ScreenChanged` during insertion or by `LayoutAndDrawComplete` can render its
own session while the callback's original session remains protected from
recursive drawing. A nested pass cannot drain another pending print request.
Regressions cover insertion callbacks and completion callbacks in both inline
and fullscreen modes. String submissions now count normalized lines and their
single spans against the layout unit limit before splitting. A payload with
2,097,153 newline characters is rejected within a 64 KB allocation budget.

On Windows ARM64, the focused application and layout suites passed 24/24 and
33/33. With `TERM=xterm-256color`, `COLORTERM=truecolor`, and `NO_COLOR`
unset, the full parallel suite passed 17,920 tests with 17 skipped; the
nonparallel and integration suites passed 33/33 and 437/437. Without those
terminal settings, the same four color-output tests documented above failed.
The Release inline transcript passed ConPTY and WinPTY bridge history checks
at 50x12, 50x3, and 1x1, plus redirected stdout. Actual Windows Terminal
and conhost window rendering remains unvalidated.

## Copilot final-session teardown follow-up: 2026-10-14

Session teardown now captures pending inline output under the same dispatch lock
and lifetime decision as pending UI dispatches. Cancellation after releasing
the lock operates only on those captured requests, preserving output admitted
by a new session while the previous session finishes teardown. A deterministic
worker/UI regression reproduced cancellation of the new session's request
before the fix and now verifies that the old request cancels while the new
request remains pending and prints successfully.

On Windows ARM64 with the true-color environment above, all 25 application
inline-output tests passed. The full parallel suite passed 17,921 tests with
17 skipped; the nonparallel and integration suites passed 33/33 and 437/437.

## Copilot worker-disposal follow-up: 2026-10-14

Both the nested run loop and the retry after a vetoed session end now honor
the shutdown flag published before coordinator cleanup waits for the rendering
lock. Coordinator iterations also stop before accessing the loop after cleanup.
Output cleanup remains serialized with physical writes by the same rendering
lock; the existing blocked-insertion/disposal regression continues to pass.

Six bounded worker-disposal regressions reproduced the deadlock before the fix.
They open and render a synchronous modal from insertion `ScreenChanged` or
`LayoutAndDrawComplete`, then start disposal from a worker. They cover inline
and fullscreen completion events through direct drawing and the main run loop.
Each now finishes without the watchdog requesting an external modal close.
A separate regression verifies that an iteration after coordinator stop does
not use the stopped loop.

Windows ARM64 validation passed all 31 application inline-output tests in both
Debug and Release. The Debug suites passed 17,928 parallel tests with 17 skipped,
33/33 nonparallel tests, and 437/437 integration tests with the true-color
environment described above.
