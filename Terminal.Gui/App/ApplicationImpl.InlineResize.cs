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
            if (size.Width <= 0 || size.Height <= 0)
            {
                return;
            }
            // Headless/custom drivers have no physical cursor to recover. Preserve their known origin.
            int height = Math.Min (region.Height, Math.Max (0, size.Height));
            int origin = Math.Clamp (region.Y, 0, Math.Max (0, size.Height - height));
            currentDriver.InlinePosition = new Point (0, origin);
            _screen = new Rectangle (0, origin, Math.Max (0, size.Width), height);
            _inlineScreenHasValidOwnership = true;
            (currentDriver as DriverImpl)?.ResizeOutputBuffer (size.Width, height);
            currentDriver.ClearContents ();
            foreach (View runnable in SessionStack?.Select (token => token.Runnable).OfType<View> () ?? [])
            {
                runnable.SetNeedsDraw ();
            }
            RaiseScreenChangedEvent (Screen);
            return;
        }
        if (_inlineResize is { } pending)
        {
            pending.ObserveSize (size);
            if (pending.Size == size)
            {
                return;
            }
            pending.Size = size;
            pending.Revision++;
            pending.Row = null;
            if (!pending.QueryPending)
            {
                pending.Started = null;
            }
            return;
        }

        InlineResize resize = new ()
        {
            Region = region,
            Size = size,
            CursorOffset = output.InlineCursorRow.GetValueOrDefault () - region.Y,
            ObservedSize = output.InlineCursorSize,
            MinimumCursorRow = output.InlineCursorRow.GetValueOrDefault (),
            MaximumCursorRow = output.InlineCursorRow.GetValueOrDefault (),
            Generation = _inlineOutputGeneration,
            Failed = !_inlineScreenHasValidOwnership || output.InlineCursorRow is null || output.InlineCursorRow < region.Y
                     || output.InlineCursorRow >= region.Y + Math.Max (1, region.Height)
        };
        resize.ObserveSize (size);
        _inlineResize = resize;
        output.IsInlineResizePending = true;
        if (!resize.Failed && size.Width > 0 && size.Height > 0)
        {
            QueryInlineResizeCursor (resize, driver);
        }
    }

    private void QueryInlineResizeCursor (InlineResize resize, DriverImpl driver)
    {
        if (resize.Failed || driver.AnsiRequestScheduler.HasAbandonedCursorPositionRequest)
        {
            resize.Failed = true;
            return;
        }
        Size size = resize.Size;
        int revision = resize.Revision;
        resize.Started ??= _timeProvider.Now;
        if (driver.GetInputProcessor ().GetParser ().IsExpecting ("R", null))
        {
            return;
        }
        resize.QueryPending = true;
        bool sent = driver.AnsiRequestScheduler.TrySendImmediately (driver, new AnsiEscapeSequenceRequest
        {
            Request = EscSeqUtils.CSI_RequestCursorPositionReport.Request,
            Terminator = EscSeqUtils.CSI_RequestCursorPositionReport.Terminator,
            ResponseReceived = response =>
            {
                if (!ReferenceEquals (_inlineResize, resize))
                {
                    return;
                }
                resize.QueryPending = false;
                if (driver.AnsiRequestScheduler.HasAbandonedCursorPositionRequest)
                {
                    resize.Failed = true;
                    return;
                }
                Match match = Regex.Match (response ?? string.Empty, @"^\x1b\[\??(\d+);(\d+)R$");
                if (!match.Success || !int.TryParse (match.Groups [1].Value, out int row) || row <= 0
                    || !int.TryParse (match.Groups [2].Value, out int column) || column <= 0)
                {
                    resize.Failed = true;
                    return;
                }
                // A top-edge report cannot establish how much of the live reservation survived.
                // Keep this evidence even if the query has already been superseded.
                if (row == 1)
                {
                    resize.Failed = true;
                    return;
                }
                bool nativeChanged = driver.GetOutput () is AnsiOutput output && ObserveNativeInlineResizeSize (resize, output);
                if (resize.Revision != revision || nativeChanged)
                {
                    resize.Started = null;
                    return;
                }
                // At the bottom edge CPR cannot distinguish scrolling from a clamped cursor.
                // Even offset zero may point at transcript when the entire live region was clipped.
                if (row >= size.Height || column > size.Width)
                {
                    resize.Failed = true;
                    return;
                }
                resize.Row = row - 1;
            },
            Abandoned = () =>
            {
                if (!ReferenceEquals (_inlineResize, resize))
                {
                    return;
                }
                resize.QueryPending = false;
                resize.Failed = true;
            }
        });
        if (!sent)
        {
            resize.QueryPending = false;
        }
    }

    private static bool ObserveNativeInlineResizeSize (InlineResize resize, AnsiOutput output)
    {
        if (output.NativeSizeQuery?.Invoke () is not { } size)
        {
            return false;
        }
        resize.ObserveSize (size);
        return resize.Failed || size != resize.Size;
    }

    // Called after size polling, before layout/printing. CPR callbacks only record a position:
    // user callbacks and a second physical resize must not publish stale geometry mid-input dispatch.
    private bool CompleteInlineResize ()
    {
        if (_inlineResize is not { } resize || Driver is not DriverImpl driver)
        {
            return true;
        }
        if (!Initialized || _dispatchStopping || resize.Generation != _inlineOutputGeneration)
        {
            return false;
        }
        bool nativeChanged = driver.GetOutput () is AnsiOutput currentOutput && ObserveNativeInlineResizeSize (resize, currentOutput);
        bool timedOut = resize.Row is null && resize.Started is { } started && _timeProvider.Now - started > TimeSpan.FromSeconds (2);
        if (resize.Failed || timedOut || driver.AnsiRequestScheduler.HasAbandonedCursorPositionRequest)
        {
            // CPR has no request id: a late reply must never be accepted by a replacement query.
            resize.Failed = true;
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
        if (resize.Size.Width <= 0 || resize.Size.Height <= 0)
        {
            return false;
        }
        if (nativeChanged)
        {
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

        // Never move ownership upward to fit the old height. Erase surviving rows first;
        // ordinary growth can then scroll the cleared region to reserve any missing height.
        int recoveredOrigin = row - resize.CursorOffset;
        int origin = Math.Max (0, recoveredOrigin);
        int height = Math.Min (Math.Max (0, resize.Region.Height + Math.Min (0, recoveredOrigin)), resize.Size.Height - origin);
        StringBuilder erase = new (EscSeqUtils.OSC_EndHyperlink () + EscSeqUtils.CSI_ResetAttributes);
        for (int ownedRow = origin; ownedRow < origin + height; ownedRow++)
        {
            erase.Append (EscSeqUtils.CSI_SetCursorPosition (ownedRow + 1, 1));
            erase.Append (EscSeqUtils.CSI + "2K");
        }
        erase.Append (EscSeqUtils.CSI_SetCursorPosition (origin + 1, 1));
        if (!WriteInlineMovement (erase.ToString (), resize.Size))
        {
            return false;
        }

        _inlineResize = null;
        if (driver.GetOutput () is AnsiOutput ansiOutput)
        {
            ansiOutput.InlineCursorRow = origin;
            ansiOutput.IsInlineResizePending = false;
            ansiOutput.ResetInlineTextState ();
        }
        _screen = new Rectangle (0, origin, resize.Size.Width, height);
        _inlineScreenHasValidOwnership = true;
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
        internal DateTime? Started { get; set; }
        internal int? Row { get; set; }
        internal bool QueryPending { get; set; }
        internal bool Failed { get; set; }
        internal int Revision { get; set; }
        internal Size ObservedSize { get; set; }
        internal int MinimumCursorRow { get; set; }
        internal int MaximumCursorRow { get; set; }
        internal bool MayHaveClamped { get; set; }

        internal void ObserveSize (Size size)
        {
            if (size.Width <= 0 || size.Height <= 0 || size == ObservedSize)
            {
                return;
            }
            // Once a resize could have clamped the cursor, a later resize can move it back
            // into the interior. A new CPR then cannot prove ownership was preserved.
            Failed |= MayHaveClamped || ObservedSize.Height <= 0;
            int removed = Math.Max (0, ObservedSize.Height - size.Height);
            MayHaveClamped |= MinimumCursorRow <= removed || MaximumCursorRow >= size.Height;
            MinimumCursorRow = Math.Max (0, MinimumCursorRow - removed);
            MaximumCursorRow = Math.Min (size.Height - 1, MaximumCursorRow + Math.Max (0, size.Height - ObservedSize.Height));
            ObservedSize = size;
        }
    }
}
