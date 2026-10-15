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

## Develop rebase and Windows verification: 2026-10-15

PR #5714 merged into `develop` at
`37a7e518855b075103fc7b059820cb17d606f928`. The six feature and review-fix
commits were rebased onto that head; `git range-diff` confirmed identical
patches for all six commits. PR #5715 now targets `develop` directly.

The rebased source at `cf8dc77fe` passed full Debug and Release solution
builds on Windows ARM64 with .NET SDK 10.0.401. With the true-color environment
above, the Debug parallel suite passed 17,928 tests with 17 skipped and no
failures, the nonparallel suite passed 33/33, and integration passed 437/437.
The Release application inline-output lifecycle suite passed 31/31.

Fresh ConPTY and WinPTY bridge checks passed at 50x12, full-height 50x3, 1x1,
and with redirected stdout. Both preserved the shell sentinels and all 24
numbered messages once and in order; redirected stdout remained empty.
Additional 50x12 checks passed caret editing during streaming output and
Escape while messages were pending, returning cleanly to the shell.

An initial WinPTY caret test sent text and an arrow in one batch and observed
the arrow before the text. A raw Windows VT-input probe reproduced this
reordering without Terminal.Gui. Delivering text and cursor keys in separate
writes passed on both backends; no Terminal.Gui change was required.

Actual Windows Terminal and classic conhost window rendering, native
clipboard/scrollback selection, and real-font Unicode width still require
interactive checks. The [example README](README.md#windows-terminal-and-conhost-checks)
now provides steps for both hosts, input and Escape behavior, and the existing
resize clear/reset behavior. Linux PTY validation also remains outstanding.

## Native conhost checks: 2026-10-15

Native Windows Console Host (`conhost.exe` 10.0.26100.8951) checks passed on
Windows ARM64 against the Release source at `82700dbf9`. The unmodified
InlineTranscript example was launched directly in a dedicated conhost window;
no commands were typed into a terminal GUI.

| Check | Result |
| --- | --- |
| Initial render | Readable title, editable input, and status in the reserved three-row composer |
| Caret editing | `helo`, Left, `l` submitted exactly one `you: hello` line and cleared the input |
| Modified arrow | Ctrl+Left in `alpha beta`, then `X`, submitted `you: alpha Xbeta` |
| Native paste and Unicode | BMP/combining paste and submission passed; the earlier native emoji-paste claim is unverified pending a conhost retest after the Windows VT input finding below |
| Native selection/copy | Mark mode selected a completed line; clipboard readback matched `you: hello` |
| Resize recovery | Maximize/restore reset the live region as documented; subsequent editing and insertion passed |
| Normal Escape | The example and its dedicated host exited without further GUI input |

An isolated test-only copy of the example exercised history and cancellation
against the same library source. It printed 20 preceding console lines and two
sentinels before initialization, kept the composer open after message 023, and
offered a slower producer for input/cancellation checks. These changes affected
only the temporary probe, not the shipped example or library.

The native accessibility document and native Select All/Copy clipboard readback
each contained both preceding sentinels and messages 000 through 023 exactly once
and in order. Native scrolling revealed earlier console output. The three-row
composer remained visible and editable while the producer was active; caret
editing and submission during streaming produced `you: hello` once.

An instrumented early-Escape run completed 6 of 24 messages, cancelled remaining
producer output, and exited with code 0. A separate teardown run also exited with
code 0 and restored the console state captured before initialization:

| State | Before initialization | After disposal |
| --- | --- | --- |
| Input mode | 503 | 503 |
| Output mode | 3 | 3 |
| Cursor visible | true | true |

No Terminal.Gui defect was found in these checks, so no production code changed.
The existing resize clear/reset limitation remains; this pass does not claim
preservation across resize or coverage of every font and Unicode sequence.

Windows Terminal 1.25.2733.0 launched the example in dedicated test windows, but
the Windows computer-use tool did not return either window in its target list,
including after the user brought one forward. No Windows Terminal GUI input was
attempted. Its native rendering, input, history/copy, and resize checks remain
unverified; passing ConPTY tests does not substitute for this coverage. Linux
PTY validation also remains outstanding.

All reported non-skipped CI checks passed on `82700dbf9`, including Windows,
Linux, and macOS unit/integration jobs, Windows/Linux builds, Linux performance
smoke tests, and documentation checks. The benchmark job was skipped.

## Windows Terminal application checks: 2026-10-15

Application-level tests subsequently passed inside actual Windows Terminal
1.25.2733.0 sessions on Windows ARM64 against the same library source. The probe
was launched directly with `wt.exe`; `WT_SESSION` was populated, stdin/stdout were
attached to the terminal, and the observed probe process was a direct descendant
of `WindowsTerminal.exe`. This coverage goes beyond the earlier standalone
ConPTY/WinPTY bridge checks.

An isolated test-only application used the ANSI driver, a real running inline
session, awaited styled output, and Terminal.Gui's `InputInjector` on the UI
thread. Input was injected inside the test application; no Windows Terminal GUI
input or shell commands were sent. The probe emitted preceding console sentinels
and a final sentinel after disposal, but did not assert Windows Terminal's
frontend scrollback contents.

| Actual terminal size | Styled insertions | Live height | Application-injected editing and Enter | Console restoration |
| --- | --- | --- | --- | --- |
| 100x45 | 24/24 | 3 | `hello`, `alpha Xbeta`, and Unicode submission passed | Passed |
| 50x12 | 24/24 | 3 | All three submissions passed | Passed |
| 50x3 | 24/24 | 3 | All three submissions passed | Passed |

During insertion, the checks injected `helo`, Left, and `l` to produce `hello`,
and Ctrl+Left plus `X` in `alpha beta` to produce `alpha Xbeta`. A third submission
contained accented, CJK, combining, and emoji text (`unicode café 界 é 🙂 end`).
Each submission cleared the input and awaited its completed print-above task.
The live application screen and composer remained three rows high throughout.

A separate 100x45 run injected Escape after four completed insertions; the
producer was cancelled before the remaining 20 messages. The result reported no
failure. Normal and early-stop runs restored the original input mode (503),
output mode (7), and visible cursor; the 50x12 and 50x3 runs also retained their
original console dimensions after disposal.

The user's screenshot of the unmodified example in `PR5715-WT-Native` also showed
the expected initial title, input row, and status line without overlap. This
confirms the initial layout only; it does not show output insertion or resize.

No Terminal.Gui defect was found, so no production code changed. These checks
exercise real terminal startup/output and application input handling, but do not
verify the host's physical keyboard translation, live rendering/font behavior, native
clipboard/selection, frontend scrollback, or interactive resize. The computer-use
API still returned `product policy blocks this app` for Windows Terminal after
the Full Access retry; those GUI checks remain unverified. Linux PTY validation
also remains outstanding.

All reported non-skipped CI checks also passed on documentation head `e4a99fdac`;
the benchmark job was skipped.

## Native Windows Terminal GUI checks: 2026-10-15

Native GUI checks passed in Windows Terminal 1.25.2733.0 on Windows ARM64
(Windows 11 build 26340) against the library source at `2ae6ed249`. The default
profile used the Solarized Dark scheme. Each run launched the probe directly
with `wt.exe` in a dedicated 100x30 window; no commands were typed into a
terminal GUI. The computer-use tool granted Windows Terminal at its "click"
tier, so the agent captured screenshots, clicked the scrollbar and caption
buttons, and read the clipboard. The user pressed the keys and made the native
selection. A probe log recorded the keys, text changes, submissions, and console
state that the application received.

The isolated test-only probe used the example's composer and producer, with
these changes:

- It printed 20 console lines and two sentinels before initialization, and
  `SHELL AFTER` after disposal.
- `--delay` set the producer interval (1.5, 6, or 15 seconds), and `--hold`
  kept the composer open after message 023.
- It logged keys, text changes, submissions, resolved input colors, and console
  modes and cursor visibility before and after the application.
- After disposal, it waited without reading input so the window remained
  observable.

The shipped example and library were not changed.

| Check | Result |
| --- | --- |
| Styled output above the composer | Messages appeared above the three-row composer during insertion; the title, input row, and status remained readable |
| Native scrollback | Scrollbar paging showed all 20 console lines, both sentinels, and messages 000-023 once and in order |
| Native selection/copy | WT Select All and copy returned 82 lines: the console lines, sentinels, and all 24 messages, each once and in order |
| Physical keyboard during output | `helo`, Left, and a character inserted before `o`; Ctrl+Left in `alpha beta`, then `X`, produced `alpha Xbeta`; Backspace and Enter worked; each submission printed once and cleared the input |
| Paste during output | Ctrl+V pasted `paste café 界 é` intact; the emoji arrived as two U+FFFD characters (see below) |
| Resize recovery | Maximize and restore reset the live region as documented; the composer redrew and later input worked |
| Normal Escape | Escape after message 023 ended the session; the producer completed 24/24 |
| Escape with output pending | Escape after message 002 cancelled the producer with 3/24 messages printed; `hi`, submitted first, printed once |
| Teardown | Every run restored input mode 503, output mode 7, a visible cursor, and the 100x30 size; exit code 0 |

Each resize clear left stale composer frames in Windows Terminal scrollback. The
copied text contained three title rows after one maximize and restore, but each
message appeared only once. This follows from the existing documented resize
clear/reset limitation.

The first WT runs inherited `NO_COLOR=1` from the agent's environment.
Terminal.Gui then selected `NoColor` capability, forced 16-color output, and
skipped OSC 10/11 detection. Solarized Dark rendered bold ANSI green as its gray
"bright green". It also rendered the text field's editable role (bright white
on white) nearly invisibly. With `NO_COLOR` unset, the probe detected TrueColor
and the terminal colors `#839496` on `#002B36`. Prefixes then rendered green,
and the input row was `#839496` on `#0C0E0E`. No Terminal.Gui change was
required; `NO_COLOR` handling is intentional.

The emoji replacement is a Windows console input limitation that predates this
PR, which does not change the input path. A raw `ReadFile` probe under ConPTY,
using input code page 65001 and VT input, received `61 EF BF BD EF BF BD 62` for
`a🙂b`; `界` arrived intact. Typed and bracketed-paste input through Terminal.Gui
produced the same two U+FFFD characters. Native conhost was not retested for
non-BMP characters.

Native Shift+drag selection did not select text on the first attempt,
possibly because the window was not focused. WT Select All and copy succeeded
after the window was focused. Mouse-mode selection bypass was not otherwise
investigated. Linux PTY validation remains outstanding.

The independent Windows input fix is proposed in
[PR #5721](https://github.com/tui-cs/Terminal.Gui/pull/5721), targeting `develop`
separately. It reads UTF-16 with `ReadConsoleW` and distinguishes a cancelled
zero-character read from the Ctrl+Z fallback. Independent native ConPTY checks
verified emoji typing, caret insertion, bracketed paste, long input, Ctrl+Z,
and blocked-read cancellation. The fix is not included in this inline-output PR;
the earlier native conhost emoji claim still requires a host-specific retest.
