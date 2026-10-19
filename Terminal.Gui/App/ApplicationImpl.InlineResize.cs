using System.Text.RegularExpressions;

namespace Terminal.Gui.App;

internal partial class ApplicationImpl
{
    private InlineResize? _inlineResize;

    private void BeginInlineResize (Rectangle region, Size size, bool observePolledSize = true)
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
            ConsumeInlineSizeObservations (pending, output);
            pending.ObserveSize (size);
            if (pending.Size == size)
            {
                return;
            }

            pending.Size = size;
            return;
        }

        InlineResize resize = new ()
        {
            Region = region,
            Size = size,
            CursorOffset = output.InlineCursorRow.GetValueOrDefault () - region.Y,
            ObservedSize = output.InlineCursorSize,
            OriginalSize = output.InlineCursorSize,
            Generation = _inlineOutputGeneration,
            NeedsFreshRegion = !_inlineScreenHasValidOwnership || output.InlineCursorRow is null || output.InlineCursorRow < region.Y
                               || output.InlineCursorRow >= region.Y + Math.Max (1, region.Height)
                               || output.HasInlineRasterState (driver.GetOutputBuffer ())
        };
        ConsumeInlineSizeObservations (resize, output);
        if (observePolledSize)
        {
            resize.ObserveSize (size);
        }

        _inlineResize = resize;
        output.IsInlineResizePending = true;
        if (!resize.NeedsFreshRegion && size.Width > 0 && size.Height > 0 && resize.ObservedSize == size)
        {
            QueryInlineResizeCursor (resize, driver);
        }
    }

    private void QueryInlineResizeCursor (InlineResize resize, DriverImpl driver)
    {
        if (resize.NeedsFreshRegion || driver.AnsiRequestScheduler.HasAbandonedCursorPositionRequest)
        {
            resize.NeedsFreshRegion = true;
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
                    resize.NeedsFreshRegion = true;
                    return;
                }

                Match match = Regex.Match (response ?? string.Empty, @"^\x1b\[\??(\d+);(\d+)R$");
                if (!match.Success || !int.TryParse (match.Groups [1].Value, out int row) || row <= 0
                    || !int.TryParse (match.Groups [2].Value, out int column) || column <= 0)
                {
                    resize.NeedsFreshRegion = true;
                    return;
                }

                // A top-edge report cannot establish how much of the live reservation survived.
                // Keep this evidence even if the query has already been superseded.
                if (row == 1 && size.Height != resize.OriginalSize.Height)
                {
                    resize.NeedsFreshRegion = true;
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
                if (row > size.Height || column > size.Width
                    || row == size.Height && size.Height != resize.OriginalSize.Height)
                {
                    resize.NeedsFreshRegion = true;
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
                resize.NeedsFreshRegion = true;
            }
        });
        if (!sent)
        {
            resize.QueryPending = false;
        }
    }

    private static bool ObserveNativeInlineResizeSize (InlineResize resize, AnsiOutput output)
    {
        int revision = resize.Revision;
        Size? native = output.QueryNativeInlineSize ();
        ConsumeInlineSizeObservations (resize, output);
        return revision != resize.Revision || resize.ObservedSize != resize.Size || native is { } size && size != resize.Size;
    }

    private static void ConsumeInlineSizeObservations (InlineResize resize, AnsiOutput output)
    {
        while (output.TryDequeueInlineSize (out Size size))
        {
            resize.ObserveSize (size);
        }

        resize.NeedsFreshRegion |= output.InlineSizeObservationOverflow;
    }

    // Called after size polling, before layout/printing. CPR callbacks only record a position:
    // user callbacks and a second physical resize must not publish stale geometry mid-input dispatch.
    private bool CompleteInlineResize ()
    {
        if (Driver is not DriverImpl driver)
        {
            return true;
        }

        if (_inlineResize is null && driver.GetOutput () is AnsiOutput { IsInlineOutputAvailable: true } observedOutput && _screen is { } ownedRegion)
        {
            observedOutput.QueryNativeInlineSize ();
            if (observedOutput.HasInlineSizeObservations)
            {
                BeginInlineResize (ownedRegion, driver.Screen.Size, observePolledSize: false);
            }
        }

        if (_inlineResize is not { } resize)
        {
            return true;
        }

        if (!Initialized || _dispatchStopping || resize.Generation != _inlineOutputGeneration)
        {
            return false;
        }

        bool nativeChanged = driver.GetOutput () is AnsiOutput currentOutput && ObserveNativeInlineResizeSize (resize, currentOutput);
        bool timedOut = resize.Row is null && resize.Started is { } started && _timeProvider.Now - started > TimeSpan.FromSeconds (1);
        resize.NeedsFreshRegion |= timedOut || driver.AnsiRequestScheduler.HasAbandonedCursorPositionRequest;
        // Out-of-band graphics do not have a text-only ownership proof. Preserve the viewport;
        // raster history itself remains outside the inline text preservation guarantee.
        resize.NeedsFreshRegion |= driver.GetOutput () is AnsiOutput rasterOutput && rasterOutput.HasInlineRasterState (driver.GetOutputBuffer ());
        if (resize.Size.Width <= 0 || resize.Size.Height <= 0)
        {
            return false;
        }

        if (nativeChanged)
        {
            return false;
        }

        if (!resize.NeedsFreshRegion && resize.Row is null)
        {
            if (!resize.QueryPending)
            {
                QueryInlineResizeCursor (resize, driver);
            }

            return false;
        }

        // Never move ownership upward to fit the old height. Erase surviving rows first;
        // ordinary growth can then scroll the cleared region to reserve any missing height.
        int recoveredOrigin = resize.Row.GetValueOrDefault () - resize.CursorOffset;
        int origin = resize.NeedsFreshRegion ? 0 : Math.Max (0, recoveredOrigin);
        int height = resize.NeedsFreshRegion
                         ? Math.Clamp (resize.Region.Height, 0, resize.Size.Height)
                         : Math.Min (Math.Max (0, resize.Region.Height + Math.Min (0, recoveredOrigin)), resize.Size.Height - origin);
        StringBuilder erase = new (EscSeqUtils.OSC_EndHyperlink () + EscSeqUtils.CSI_ResetAttributes);
        if (resize.NeedsFreshRegion)
        {
            // Reflow/clamping cannot prove ownership. Preserve the entire viewport in history,
            // including any old live rows, and reserve a new blank region without erasing cells.
            erase.Append (EscSeqUtils.CSI_SetCursorPosition (resize.Size.Height, 1));
            erase.Append ('\n', resize.Size.Height);
        }
        else
        {
            for (int ownedRow = origin; ownedRow < origin + height; ownedRow++)
            {
                erase.Append (EscSeqUtils.CSI_SetCursorPosition (ownedRow + 1, 1));
                erase.Append (EscSeqUtils.CSI + "2K");
            }
        }

        erase.Append (EscSeqUtils.CSI_SetCursorPosition (origin + 1, 1));
        if (!WriteInlineMovement (erase.ToString (), resize.Size))
        {
            return false;
        }

        _inlineResize = null;
        if (driver.GetOutput () is AnsiOutput ansiOutput)
        {
            ansiOutput.CompleteInlineSizeRecovery ();
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
        if (resize.NeedsFreshRegion)
        {
            Logging.Warning ("Inline resize preserved the previous viewport in history and reserved a fresh live region.");
        }

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
        internal bool NeedsFreshRegion { get; set; }
        internal Size OriginalSize { get; init; }
        internal int Revision { get; set; }
        internal Size ObservedSize { get; set; }

        internal void ObserveSize (Size size)
        {
            if (size == ObservedSize)
            {
                return;
            }

            ObservedSize = size;
            Revision++;
            Row = null;
            if (!QueryPending)
            {
                Started = null;
            }

            // Reflow can change the cursor's offset within the live frame. Multiple changes
            // can hide earlier clamping, even if the final CPR is interior. Neither case
            // establishes ownership, so preserve all rows instead of guessing their extent.
            NeedsFreshRegion |= Revision > 1 || size.Width != OriginalSize.Width;
        }
    }
}
