using System.Windows;

namespace ReplayAnonymizer;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        if (e.Args.Length == 4 && e.Args[0] == "--anonymize")
        {
            OsrAnonymizer.WriteAnonymizedCopy(e.Args[1], e.Args[2], e.Args[3]);
            Shutdown();
            return;
        }

        base.OnStartup(e);
        MainWindow = new MainWindow();
        MainWindow.Show();
    }
}
