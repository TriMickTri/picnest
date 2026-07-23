using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using LibVLCSharp.Shared;

namespace PicNest;

public partial class App : Application
{
    public override void OnFrameworkInitializationCompleted()
    {
        Core.Initialize();
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.MainWindow = new MainWindow();
        base.OnFrameworkInitializationCompleted();
    }
}
