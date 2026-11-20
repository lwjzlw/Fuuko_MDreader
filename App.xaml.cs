using System.IO;
using System.Windows;

namespace MarkdownReader;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var settings = AppSettings.Load();
        var path = e.Args.Length > 0 ? e.Args[0] : null;
        var window = new MainWindow(settings, path);
        MainWindow = window;
        window.Show();
    }
}
