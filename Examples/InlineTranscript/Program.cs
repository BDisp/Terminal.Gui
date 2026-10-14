using Terminal.Gui.App;
using Terminal.Gui.Drivers;
using Terminal.Gui.Drawing;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;
using TgAttribute = Terminal.Gui.Drawing.Attribute;

using IApplication app = Application.Create ();
app.AppModel = AppModel.Inline;
app.Init (DriverRegistry.Names.ANSI);
using CancellationTokenSource lifetime = new ();
using Runnable composer = new () { Width = Dim.Fill (), Height = 3 };
Label title = new () { Text = "Inline transcript · Enter sends · Esc exits", Width = Dim.Fill () };
TextField input = new () { Y = 1, Width = Dim.Fill () };
Label status = new () { Y = 2, Text = "Completed messages appear above this three-row composer.", Width = Dim.Fill () };
composer.Add (title, input, status);
bool demo = args.Contains ("--demo");
Task producer = Task.CompletedTask;
Task latestSubmission = Task.CompletedTask;
SessionToken? owner = null;
EventHandler<SessionTokenEventArgs> sessionBegun = (_, e) =>
{
    owner = e.State;
    if (demo)
    {
        producer = ProduceAsync (owner);
    }
};
app.SessionBegun += sessionBegun;
input.Accepted += (_, _) =>
{
    if (owner is null || input.Text.Length == 0)
    {
        return;
    }
    string message = input.Text;
    input.Text = string.Empty;
    latestSubmission = SubmitAsync (owner, [new InlineOutputLine ([new InlineOutputSpan ("you: ", new TgAttribute (Color.Cyan, Color.None)), new InlineOutputSpan (message)])]);
};
app.Run (composer);
app.SessionBegun -= sessionBegun;
lifetime.Cancel ();
await Task.WhenAll (producer, latestSubmission).ConfigureAwait (false);

async Task SubmitAsync (SessionToken session, IReadOnlyList<InlineOutputLine> lines)
{
    try
    {
        await app.PrintAboveAsync (session, lines, lifetime.Token).ConfigureAwait (false);
    }
    catch (OperationCanceledException)
    {
        // Ending the owning session cancels output that never reached a render boundary.
    }
    catch (Exception error)
    {
        await ReportAndStopAsync (session, error).ConfigureAwait (false);
    }
}

async Task ProduceAsync (SessionToken session)
{
    try
    {
        for (var i = 0; i < 24; i++)
        {
            await app.PrintAboveAsync (session,
                                      [new InlineOutputLine ([new InlineOutputSpan ($"message {i:D3}: ", new TgAttribute (Color.Green, Color.None, TextStyle.Bold)),
                                                              new InlineOutputSpan ("completed output")])],
                                      lifetime.Token).ConfigureAwait (false);
            await Task.Delay (150, lifetime.Token).ConfigureAwait (false);
        }
        await app.InvokeAsync (session, () => app.RequestStop (), lifetime.Token).ConfigureAwait (false);
    }
    catch (OperationCanceledException)
    {
        // The caller can close the composer while a producer is awaiting its next insertion.
    }
    catch (Exception error)
    {
        await ReportAndStopAsync (session, error).ConfigureAwait (false);
    }
}

async Task ReportAndStopAsync (SessionToken session, Exception error)
{
    try
    {
        await app.InvokeAsync (session, () =>
        {
            status.Text = error.Message;
            app.RequestStop ();
        }, lifetime.Token).ConfigureAwait (false);
    }
    catch (OperationCanceledException)
    {
        // Teardown already owns pending UI work.
    }
    catch (NotInitializedException)
    {
        // The application was disposed before the diagnostic could be dispatched.
    }
}
