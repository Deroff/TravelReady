using System.Configuration;
using System.Data;
using System.Windows;

namespace TravelReady;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        try
        {
            var database = new TravelDatabase();
            var auth = new AuthWindow(database);
            if (auth.ShowDialog() != true || auth.AuthenticatedUser is null)
            {
                Shutdown();
                return;
            }
            var main = new MainWindow(database, auth.AuthenticatedUser);
            MainWindow = main;
            main.Show();
            ShutdownMode = ShutdownMode.OnLastWindowClose;
        }
        catch (Exception ex)
        {
            TravelDiagnostics.Error("startup.failed", ex);
            MessageBox.Show("Не удалось запустить локальную базу приложения.\n" + ex.Message, "Travel Ready", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(-1);
        }
    }
}

