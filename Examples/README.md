# Terminal.Gui Examples

This repository keeps:

- `UICatalog` - the main demo app
- `ScenarioRunner` - scenario automation tool
- `InlineTranscript` - a fixed-height inline composer with awaitable styled output above it

To run the inline composer, run `dotnet run --project Examples/InlineTranscript`.
To generate a finite stream for terminal verification, append `-- --demo`.

All other examples were moved to the [tui-cs/Examples](https://github.com/tui-cs/Examples) repository.

## Building Examples Against Local Source

The [tui-cs/Examples](https://github.com/tui-cs/Examples) repo supports building against your local Terminal.Gui source (instead of the NuGet package) to catch breaking changes immediately:

```bash
cd ../Examples
dotnet build -p:TerminalGuiRoot=../Terminal.Gui
```
