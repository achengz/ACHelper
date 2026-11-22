using System.IO;
using System.Windows;

namespace LolMatchAssistant;

public partial class App : Application
{
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (e.Args is ["--check", var report, ..])
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(50));
            var (exit, result) = await Diagnostics.Check(timeout.Token, e.Args.Length > 2 ? e.Args[2] : null);
            File.WriteAllText(report, result);
            Shutdown(exit);
            return;
        }
        if (e.Args is ["--self-test", var testReport])
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var (exit, result) = await Diagnostics.SelfTest();
            File.WriteAllText(testReport, result);
            Shutdown(exit);
            return;
        }
        if (e.Args is ["--watch-roster", var rosterReport])
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
            var (exit, result) = await Diagnostics.WatchRoster(timeout.Token);
            File.WriteAllText(rosterReport, result);
            Shutdown(exit);
            return;
        }
        MainWindow = new MainWindow();
        MainWindow.Show();
    }
}
