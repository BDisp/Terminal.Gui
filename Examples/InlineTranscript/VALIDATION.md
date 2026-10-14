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
