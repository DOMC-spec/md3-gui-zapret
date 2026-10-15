using System.Windows;
using Zapret.Gui.Theming;

namespace Zapret.Gui;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        M3Theme.Apply(false);
        base.OnStartup(e);
    }
}
