#nullable enable

namespace UICatalog.Scenarios;

[ScenarioMetadata ("CustomMenuBarBorderStyle", "Custom MenuBar BorderStyle")]
[ScenarioCategory ("Controls")]
public sealed class CustomMenuBarBorderStyle : Scenario
{
    public override void Main ()
    {
        TuiConfigurationBuilder configurationBuilder = new ("test");
        string previousTheme = ThemeManager.GetCurrentThemeName ();

        using IApplication app = Application.Create ();
        configurationBuilder.ThemeManager.SwitchTheme ("test");

        app.Init ();

        using Window window = new ();

        MenuBar menu = new ([
                                new MenuBarItem ("_Test",
                                                 [
                                                     new MenuItem (commandText: "_default", action: () => configurationBuilder.ThemeManager.SwitchTheme ("default")),
                                                     new MenuItem (commandText: "t_est", action: () => configurationBuilder.ThemeManager.SwitchTheme ("test")),
                                                 ]),
                            ]);

        window.Add (menu);

        app.Run (window);

        configurationBuilder.ThemeManager.SwitchTheme (previousTheme);
    }
}
