using System.Text.RegularExpressions;

namespace Terminal.Gui.App;

internal partial class ApplicationImpl
{
    private InlineResize? _inlineResize;

    private void BeginInlineResize (Rectangle region, Size size)
    {
        if (Driver is not { } currentDriver)
        {
            return;
        }
        if (currentDriver is not DriverImpl driver || driver.GetOutput () is not AnsiOutput { IsInlineOutputAvailable: true } output)
        {
            // Headless/custom drivers have no physical cursor to recover. Preserve their known origin.
            int height = Math.Min (region.Height, Math.Max (0, size.Height));
            int origin = Math.Clamp (region.Y, 0, Math.Max (0, size.Height - height));
            currentDriver.InlinePosition = new Point (0, origin);
            Screen = new Rectangle (0, origin, Math.Max (0, size.Width), height);
            currentDriver.ClearContents ();
            return;
        }
        if (_inlineResize is { } pending)
        {
            if (pending.Size == size)
            {
                return;
            }
            pending.Size = size;
            pending.Revision++;
            pending.Row = null;
            pending.Failed = false;
            return;
        }

        InlineResize resize = new ()
        {
            Region = region,
            Size = size,
            CursorOffset = Math.Clamp (driver.GetCursor ().Position?.Y ?? 0, 0, Math.Max (0, region.Height - 1)),
            Generation = _inlineOutputGeneration
        };
        _inlineResize = resize;
        output.IsInlineResizePending = true;
        if (size.Width > 0 && size.Height > 0)
        {
            QueryInlineResizeCursor (resize, driver);
        }
    }

    private void QueryInlineResizeCursor (InlineResize resize, DriverImpl driver)
    {
        Size size = resize.Size;
        int revision = resize.Revision;
        int query = ++resize.Query;
        resize.QueryRevision = revision;
        resize.QueryPending = true;
        resize.Started = _timeProvider.Now;
        driver.QueueAnsiRequest (new AnsiEscapeSequenceRequest
        {
            Request = EscSeqUtils.CSI_RequestCursorPositionReport.Request,
            Terminator = EscSeqUtils.CSI_RequestCursorPositionReport.Terminator,
            ResponseReceived = response =>
            {
                if (!ReferenceEquals (_inlineResize, resize) || resize.Query != query)
                {
                    return;
                }
                resize.QueryPending = false;
                if (resize.Revision != revision)
                {
                    return;
                }
                Match match = Regex.Match (response ?? string.Empty, @"^\x1b\[\??(\d+);(\d+)R$");
                if (match.Success && int.TryParse (match.Groups [1].Value, out int row) && row > 0 && row <= size.Height
                    && int.TryParse (match.Groups [2].Value, out int column) && column > 0 && column <= size.Width)
                {
                    resize.Row = row - 1;
                    return;
                }
                resize.Failed = true;
            },
            Abandoned = () =>
            {
                if (!ReferenceEquals (_inlineResize, resize) || resize.Query != query)
                {
                    return;
                }
                resize.QueryPending = false;
                resize.Failed = resize.Revision == revision;
            }
        });
    }

    // Called after size polling, before layout/printing. CPR callbacks only record a position:
    // user callbacks and a second physical resize must not publish stale geometry mid-input dispatch.
    private bool CompleteInlineResize ()
    {
        if (_inlineResize is not { } resize || Driver is not DriverImpl driver)
        {
            return true;
        }
        if (resize.Size.Width <= 0 || resize.Size.Height <= 0)
        {
            return false;
        }
        if (!Initialized || _dispatchStopping || resize.Generation != _inlineOutputGeneration)
        {
            return false;
        }
        bool timedOut = resize.QueryPending && _timeProvider.Now - resize.Started > TimeSpan.FromSeconds (2);
        if (timedOut && resize.QueryRevision != resize.Revision)
        {
            QueryInlineResizeCursor (resize, driver);
            return false;
        }
        if (resize.Failed || timedOut)
        {
            if (driver.GetOutput () is IInlineOutput output)
            {
                output.LoseInlinePlacement ();
            }
            lock (_dispatchLock)
            {
                _inlineOutputFailure = new IOException ("Could not locate the inline region after terminal resize.");
            }
            EndInlineOutput (fault: true);
            return false;
        }
        if (resize.Row is not { } row)
        {
            if (!resize.QueryPending)
            {
                QueryInlineResizeCursor (resize, driver);
            }
            return false;
        }

        // Reserve the old height before re-layout. A terminal can clamp a stale caret to its
        // last row while shrinking; truncating the reservation there and growing it again
        // would scroll the still-visible old frame into history.
        int height = Math.Min (resize.Region.Height, resize.Size.Height);
        int origin = Math.Clamp (row - resize.CursorOffset, 0, resize.Size.Height - height);
        StringBuilder erase = new (EscSeqUtils.OSC_EndHyperlink () + EscSeqUtils.CSI_ResetAttributes);
        for (int ownedRow = origin; ownedRow < origin + height; ownedRow++)
        {
            erase.Append (EscSeqUtils.CSI_SetCursorPosition (ownedRow + 1, 1));
            erase.Append (EscSeqUtils.CSI + "2K");
        }
        erase.Append (EscSeqUtils.CSI_SetCursorPosition (origin + 1, 1));
        if (!WriteInlineMovement (erase.ToString ()))
        {
            return false;
        }

        _inlineResize = null;
        if (driver.GetOutput () is AnsiOutput ansiOutput)
        {
            ansiOutput.IsInlineResizePending = false;
            ansiOutput.ResetInlineTextState ();
        }
        _screen = new Rectangle (0, origin, resize.Size.Width, height);
        driver.InlinePosition = new Point (0, origin);
        driver.ResizeOutputBuffer (resize.Size.Width, height);
        _inlineScreenSized = height > 0;
        foreach (View runnable in SessionStack?.Select (token => token.Runnable).OfType<View> () ?? [])
        {
            runnable.SetNeedsLayout ();
            runnable.SetNeedsDraw ();
        }
        if (Popovers?.GetActivePopover () is View popover)
        {
            popover.SetNeedsLayout ();
            popover.SetNeedsDraw ();
        }
        driver.SetCursorNeedsUpdate (true);
        RaiseScreenChangedEvent (Screen);
        return Initialized && !_dispatchStopping && HasRunningSession && Driver == driver && !InlinePlacementLost && _inlineResize is null;
    }

    private sealed class InlineResize
    {
        internal Rectangle Region { get; init; }
        internal Size Size { get; set; }
        internal int CursorOffset { get; init; }
        internal long Generation { get; init; }
        internal DateTime Started { get; set; }
        internal int? Row { get; set; }
        internal bool QueryPending { get; set; }
        internal bool Failed { get; set; }
        internal int Revision { get; set; }
        internal int Query { get; set; }
        internal int QueryRevision { get; set; }
    }
}
