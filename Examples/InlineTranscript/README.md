# Inline transcript

To run an editable three-row composer against local source:

```sh
dotnet run --project Examples/InlineTranscript
```

To generate 24 completed styled messages and exit, append `-- --demo`. Enter submits
the typed message; Escape ends the owning session and cancels pending producer output.
Each producer awaits insertion; terminal writes remain on the rendering thread.

![Styled output above the live composer](InlineTranscript.gif)

The accompanying [cast](InlineTranscript.cast) was recorded in a macOS PTY with native
cursor-position replies and replayed into a history-aware terminal model. The numbered
messages and both preceding shell sentinels occur once and in order. The live composer
retains its reserved height. ConPTY and WinPTY bridge checks are recorded in
[VALIDATION.md](VALIDATION.md); actual Windows Terminal/conhost window rendering
remains outstanding.

To reproduce the Unix PTY/history check after building Release:

```sh
python3 -m venv /tmp/inline-verify
/tmp/inline-verify/bin/pip install pyte
/tmp/inline-verify/bin/python Scripts/tuirec/verify-inline-output.py --cast /tmp/inline.cast
/tmp/inline-verify/bin/python Scripts/tuirec/verify-inline-output.py --rows 3
/tmp/inline-verify/bin/python Scripts/tuirec/verify-inline-output.py --redirect-stdout
```

To record using `tuirec`, follow [the recording guide](../../Scripts/tuirec/README.md).
An unattended recorder that does not answer CPR cannot verify preservation of prior
shell output: the documented missing-CPR fallback uses row zero. Use the history check
above for that assertion. Real terminal width and resize reflow may differ from the model.
