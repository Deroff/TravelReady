using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.IO;
using TravelReady;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        var databaseName = "TravelReadyUiTest_" + Guid.NewGuid().ToString("N");
        var server = Environment.GetEnvironmentVariable("TRAVELREADY_SQL_SERVER") ?? @"(localdb)\MSSQLLocalDB";
        var noLegacyFile = Path.Combine(Path.GetTempPath(), "travel-ready-ui-" + Guid.NewGuid().ToString("N"), "no-legacy.db");
        var database = new TravelDatabase(noLegacyFile, server, databaseName);
        var user = database.Register("ui-smoke@example.org", "Студент", "TravelReady2026!");
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var checks = new List<string>();
        try
        {
            if (args.Contains("--auth-visual-hold", StringComparer.OrdinalIgnoreCase))
            {
                var auth = new AuthWindow(database);
                auth.Show();
                Console.WriteLine("AUTH_VISUAL_HOLD: окно профиля открыто на 45 секунд");
                Pump(TimeSpan.FromSeconds(45));
                auth.Close();
                app.Shutdown();
                return 0;
            }

            var window = new MainWindow(database, user);
            window.Show();
            Pump(TimeSpan.FromMilliseconds(180));
            if (!window.IsVisible) throw new Exception("Основное окно не показано.");
            var readinessPanel = (Border?)window.FindName("ReadinessPanel") ?? throw new Exception("Нет панели контроля готовности.");
            var panelBounds = readinessPanel.TransformToAncestor(window).TransformBounds(new Rect(new Point(0, 0), readinessPanel.RenderSize));
            Console.WriteLine($"UI_BOUNDS: window={window.ActualWidth:0}x{window.ActualHeight:0}; right-panel={readinessPanel.ActualWidth:0}; right={panelBounds.Right:0}");
            var dpi = VisualTreeHelper.GetDpi(window);
            Console.WriteLine($"UI_SCALE: dpi={dpi.DpiScaleX:0.00}x{dpi.DpiScaleY:0.00}; primary={SystemParameters.PrimaryScreenWidth:0}x{SystemParameters.PrimaryScreenHeight:0}; work={SystemParameters.WorkArea.Width:0}x{SystemParameters.WorkArea.Height:0}");
            if (readinessPanel.ActualWidth < 280 || panelBounds.Right > window.ActualWidth + 1)
                throw new Exception("Панель контроля готовности обрезана границами окна.");
            checks.Add("двухстраничный макет помещает панель готовности в окно без обрезки");
            var name = (TextBox?)window.FindName("ProjectNameBox") ?? throw new Exception("Нет поля названия поездки.");
            var cities = (TextBox?)window.FindName("CitiesBox") ?? throw new Exception("Нет поля маршрута.");
            if (!string.IsNullOrWhiteSpace(name.Text) || !string.IsNullOrWhiteSpace(cities.Text) || database.LoadLatestTrip(user.Id) is not null)
                throw new Exception("Новый профиль должен начинаться с пустого маршрута без демонстрационных данных.");
            checks.Add("новый профиль открывается с пустым маршрутом и не создаёт демоданные");

            name.Text = "Автосохранённая поездка";
            cities.Text = "Казань";
            Pump(TimeSpan.FromMilliseconds(1200));
            var saved = database.LoadLatestTrip(user.Id) ?? throw new Exception("Автосохранение не создало поездку.");
            if (saved.Name != name.Text || saved.Cities != cities.Text || saved.Items.Count != 0) throw new Exception("Метаданные новой поездки некорректны.");
            checks.Add("изменение полей автоматически записывается в SQL Server LocalDB");

            var cityTemplate = FindButton(window, "Городская поездка") ?? throw new Exception("Городской шаблон не найден.");
            cityTemplate.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Pump(TimeSpan.FromMilliseconds(1200));
            var templated = database.LoadLatestTrip(user.Id)!;
            if (templated.Items.Count < 10 || !templated.Items.Any(x => x.Name == "Паспорт" && x.Required))
                throw new Exception("Шаблон не создал полноценный контрольный список.");
            var peoplePanel = (StackPanel?)window.FindName("PeopleLoadPanel") ?? throw new Exception("Нет панели нагрузки пассажиров.");
            if (peoplePanel.Children.Count == 0) throw new Exception("Панель не показала распределение пассажирского веса.");
            checks.Add("городской шаблон создаёт список вещей, пассажирскую нагрузку и сохраняет его автоматически");

            var add = (Button?)window.FindName("AddItem_Click") ?? (Button?)FindButton(window, "+ ВЕЩЬ");
            if (add is null) throw new Exception("Кнопка добавления позиции не найдена.");
            add.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Pump(TimeSpan.FromMilliseconds(1200));
            var afterAdd = database.LoadLatestTrip(user.Id)!;
            if (afterAdd.Items.Count != templated.Items.Count + 1) throw new Exception("Добавленная вещь не автосохранилась.");
            checks.Add("добавление позиции багажа автосохраняется");
            window.Close();

            var restoredWindow = new MainWindow(database, user);
            restoredWindow.Show();
            Pump(TimeSpan.FromMilliseconds(160));
            var restoredName = (TextBox?)restoredWindow.FindName("ProjectNameBox");
            if (restoredName?.Text != "Автосохранённая поездка") throw new Exception("Экран не загрузил данные текущего профиля.");
            checks.Add("следующий запуск восстанавливает проект профиля");
            var previewArg = args.FirstOrDefault(x => x.StartsWith("--render-preview=", StringComparison.OrdinalIgnoreCase));
            if (previewArg is not null)
            {
                var previewPath = Path.GetFullPath(previewArg["--render-preview=".Length..]);
                Directory.CreateDirectory(Path.GetDirectoryName(previewPath)!);
                var visual = new RenderTargetBitmap((int)restoredWindow.ActualWidth, (int)restoredWindow.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                visual.Render(restoredWindow);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(visual));
                using var output = File.Create(previewPath);
                encoder.Save(output);
                Console.WriteLine("UI_PREVIEW: " + previewPath);
            }
            if (args.Contains("--visual-hold", StringComparer.OrdinalIgnoreCase))
            {
                Console.WriteLine("VISUAL_HOLD: окно открыто на 45 секунд");
                Pump(TimeSpan.FromSeconds(45));
            }
            restoredWindow.Close();
            app.Shutdown();
            Microsoft.Data.SqlClient.SqlConnection.ClearAllPools();
            using (var cleanup = new Microsoft.Data.SqlClient.SqlConnection(new Microsoft.Data.SqlClient.SqlConnectionStringBuilder { DataSource = server, InitialCatalog = "master", IntegratedSecurity = true, Encrypt = Microsoft.Data.SqlClient.SqlConnectionEncryptOption.Optional, TrustServerCertificate = true }.ConnectionString))
            { cleanup.Open(); using var command = cleanup.CreateCommand(); command.CommandText = $"IF DB_ID(N'{databaseName}') IS NOT NULL BEGIN ALTER DATABASE [{databaseName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{databaseName}]; END"; command.ExecuteNonQuery(); }
            foreach (var check in checks) Console.WriteLine("PASS " + check);
            Console.WriteLine($"TRAVEL_READY_UI_SMOKE: {checks.Count} passed, 0 failed");
            return 0;
        }
        catch (Exception ex)
        {
            app.Shutdown();
            Console.Error.WriteLine("UI_SMOKE: FAIL " + ex);
            return 1;
        }
    }

    private static Button? FindButton(DependencyObject root, string content)
    {
        for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            if (child is Button button && button.Content?.ToString() == content) return button;
            var found = FindButton(child, content);
            if (found is not null) return found;
        }
        return null;
    }

    private static void Pump(TimeSpan duration)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = duration };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }
}
