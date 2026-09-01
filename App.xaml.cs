using System.Windows;

namespace ReplayAnonymizer;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        if (e.Args.Length is 4 or 5 && e.Args[0] == "--anonymize")
        {
            bool removeSmoke = e.Args.Length == 5 && e.Args[4] == "--remove-smoke";
            OsrAnonymizer.WriteAnonymizedCopy(e.Args[1], e.Args[2], e.Args[3], removeSmoke);
            Shutdown();
            return;
        }

        base.OnStartup(e);
        MainWindow = new MainWindow();
        MainWindow.Show();
    }
}
