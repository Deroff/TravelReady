using Microsoft.Win32;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;

namespace TravelReady;

public partial class MainWindow : Window
{
    private readonly ObservableCollection<PackingEntry> _items = [];
    private readonly List<WeatherCard> _weather = [];
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(6) };
    private readonly WeatherApiClient _weatherApi = new();
    private ICollectionView? _view;
    private readonly TravelDatabase _database;
    private readonly AppUser _user;
    private readonly System.Windows.Threading.DispatcherTimer _autosave;
    private bool _isReady;

    public MainWindow(TravelDatabase database, AppUser user)
    {
        InitializeComponent();
        _database = database;
        _user = user;
        AccountNameText.Text = user.DisplayName;
        SidebarAccountName.Text = user.DisplayName;
        SidebarEmailText.Text = user.Email;
        _view = CollectionViewSource.GetDefaultView(_items); _view.Filter = FilterItem; ItemsGrid.ItemsSource = _view;
        _items.CollectionChanged += Items_CollectionChanged;
        _autosave = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
        _autosave.Tick += (_, _) => { _autosave.Stop(); PersistProject(silent: true); };
        var saved = _database.LoadLatestTrip(_user.Id);
        if (saved is null)
        {
            ProjectNameBox.Clear();
            CitiesBox.Clear();
            DaysBox.SelectedIndex = 0;
            TypeBox.SelectedIndex = 0;
            LimitBox.Text = "10";
            WorkspaceTitle.Text = "Новая поездка";
            WorkspaceSubtitle.Text = "Задайте маршрут и выберите шаблон сборов";
            _isReady = true;
            RefreshView();
            FooterText.Text = "Чистый проект · заполните маршрут, чтобы начать";
        }
        else
        {
            LoadProject(saved);
            _isReady = true;
            FooterText.Text = $"Из локальной базы · сохранено {saved.SavedAt:dd.MM.yyyy HH:mm}";
        }
    }

    private void LoadProject(PackingProject project)
    {
        ProjectNameBox.Text = project.Name;
        CitiesBox.Text = project.Cities;
        LimitBox.Text = project.WeightLimit.ToString("0.0", System.Globalization.CultureInfo.CurrentCulture);
        var dayIndex = DaysBox.Items.Cast<ComboBoxItem>().ToList().FindIndex(x => x.Content.ToString() == project.Days.ToString());
        DaysBox.SelectedIndex = dayIndex >= 0 ? dayIndex : 1;
        var typeIndex = TypeBox.Items.Cast<ComboBoxItem>().ToList().FindIndex(x => x.Content.ToString() == project.TripType);
        TypeBox.SelectedIndex = typeIndex >= 0 ? typeIndex : 0;
        _items.Clear(); foreach (var item in project.Items) _items.Add(item);
        _weather.Clear(); _weather.AddRange(project.Weather);
        WorkspaceTitle.Text = project.Name;
        RenderWeather(); RefreshView();
    }

    private PackingProject CaptureProject() => new()
    {
        Name = ProjectNameBox.Text.Trim(),
        Cities = CitiesBox.Text.Trim(),
        Days = Days,
        TripType = TripType,
        WeightLimit = Limit,
        Items = _items.ToList(),
        Weather = _weather.ToList(),
        SavedAt = DateTime.Now
    };

    private void PersistProject(bool silent)
    {
        if (!_isReady) return;
        if (string.IsNullOrWhiteSpace(ProjectNameBox.Text) || string.IsNullOrWhiteSpace(CitiesBox.Text) || Limit <= 0)
        {
            if (!silent) ValidationText.Text = "Заполните название, маршрут и корректный лимит багажа.";
            return;
        }
        try
        {
            _database.SaveTrip(_user.Id, CaptureProject());
            if (!silent) FooterText.Text = "Сохранено в профиль · " + DateTime.Now.ToString("HH:mm:ss");
            else FooterText.Text = "АВТОСОХРАНЕНИЕ · " + DateTime.Now.ToString("HH:mm:ss");
        }
        catch (Exception ex)
        {
            TravelDiagnostics.Error("trip.autosave-failed", ex);
            FooterText.Text = "Ошибка автосохранения: " + ex.Message;
        }
    }

    private void QueueAutosave()
    {
        if (!_isReady) return;
        _autosave.Stop();
        _autosave.Start();
    }

    private void Items_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) => QueueAutosave();
    private void ProjectField_Changed(object sender, TextChangedEventArgs e) => QueueAutosave();
    private void ProjectSelection_Changed(object sender, SelectionChangedEventArgs e) => QueueAutosave();
    private void SignOut_Click(object sender, RoutedEventArgs e) { PersistProject(silent: true); Application.Current.Shutdown(); }
    private int Days => DaysBox.SelectedItem is ComboBoxItem day && int.TryParse(day.Content.ToString(), out var value) ? value : 3;
    private string TripType => TypeBox.SelectedItem is ComboBoxItem type ? type.Content.ToString()! : "городская";
    private double Limit => double.TryParse(LimitBox.Text.Replace(',', '.'), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var value) && value > 0 ? value : 0;
    private void Add(string name, string category, string person, int quantity, double weight, bool required, string status = "Не готово") => _items.Add(new PackingEntry { Name = name, Category = category, Person = person, Quantity = quantity, UnitWeight = weight, Required = required, Status = status });
    private void Generate_Click(object sender, RoutedEventArgs e) { GenerateProject(); QueueAutosave(); }
    private void GenerateProject()
    {
        ValidationText.Text = ""; if (string.IsNullOrWhiteSpace(ProjectNameBox.Text)) { ValidationText.Text = "Введите название проекта."; return; }
        if (string.IsNullOrWhiteSpace(CitiesBox.Text)) { ValidationText.Text = "Добавьте маршрут."; return; }
        if (Limit <= 0) { ValidationText.Text = "Лимит веса должен быть больше нуля."; return; }
        WorkspaceTitle.Text = ProjectNameBox.Text; AdjustForTrip(); WorkspaceSubtitle.Text = $"{_items.Count} позиций · {Days} дней · {TripType}"; RenderWeather(); RefreshAnalytics(); FooterText.Text = $"Проект обновлён {DateTime.Now:HH:mm:ss}"; QueueAutosave();
    }
    private void AdjustForTrip()
    {
        var shirts = _items.FirstOrDefault(x => x.Name == "Футболка"); if (shirts is not null) shirts.Quantity = Math.Max(2, (int)Math.Ceiling(Days / 2d)); var socks = _items.FirstOrDefault(x => x.Name == "Носки"); if (socks is not null) socks.Quantity = Days;
        if (TripType is "деловая" or "смешанная") Ensure("Деловой комплект", "Одежда", .9, true); if (TripType is "активный отдых" or "смешанная") { Ensure("Спортивная форма", "Одежда", .65, true); Ensure("Бутылка для воды", "Здоровье", .22, false); }
    }
    private void Ensure(string name, string category, double weight, bool required) { if (_items.All(x => x.Name != name)) Add(name, category, "Студент", 1, weight, required); }
    private void Template_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button) return;
        var template = PackingTemplateFactory.Create(button.Tag?.ToString() ?? "city", Days);
        foreach (var item in template)
        {
            if (_items.All(existing => !string.Equals(existing.Name, item.Name, StringComparison.OrdinalIgnoreCase)))
                _items.Add(item);
        }
        if (string.IsNullOrWhiteSpace(ProjectNameBox.Text)) ProjectNameBox.Text = "Моя поездка";
        RefreshView();
        QueueAutosave();
    }
    private void AddItem_Click(object sender, RoutedEventArgs e) { var item = new PackingEntry { Name = "Новая вещь", Category = "Одежда", Person = "Студент", Quantity = 1, UnitWeight = .1, Status = "Не готово" }; _items.Add(item); RefreshView(); ItemsGrid.SelectedItem = item; ItemsGrid.ScrollIntoView(item); ItemsGrid.BeginEdit(); }
    private void DeleteItem_Click(object sender, RoutedEventArgs e) { if (ItemsGrid.SelectedItem is PackingEntry item) { _items.Remove(item); RefreshView(); } }
    private void BalanceWeight_Click(object sender, RoutedEventArgs e)
    {
        var studentWeight = 0d;
        var companionWeight = 0d;
        var movable = _items.Where(x => x.Person != "Общее").OrderByDescending(x => x.TotalWeight).ToList();
        foreach (var item in movable)
        {
            if (studentWeight <= companionWeight)
            {
                item.Person = "Студент";
                studentWeight += item.TotalWeight;
            }
            else
            {
                item.Person = "Сопровождающий";
                companionWeight += item.TotalWeight;
            }
        }
        ItemsGrid.Items.Refresh();
        _view?.Refresh();
        BalanceStatusText.Text = $"LOAD BALANCER // {studentWeight:0.0} кг ↔ {companionWeight:0.0} кг";
        FooterText.Text = $"Личный багаж распределён · разница {Math.Abs(studentWeight - companionWeight):0.0} кг";
        RefreshAnalytics();
        QueueAutosave();
    }
    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) => RefreshView();
    private void Filter_Changed(object sender, SelectionChangedEventArgs e) { if (IsLoaded) RefreshView(); }
    private bool FilterItem(object obj)
    {
        if (obj is not PackingEntry x) return false; var search = SearchBox?.Text?.Trim() ?? ""; var category = CategoryFilter?.SelectedItem is ComboBoxItem c ? c.Content.ToString()! : "Все категории"; var status = StatusFilter?.SelectedItem is ComboBoxItem s ? s.Content.ToString()! : "Все статусы"; var person = PersonFilter?.SelectedItem is ComboBoxItem p ? p.Content.ToString()! : "Все пассажиры"; return (search.Length == 0 || x.Name.Contains(search, StringComparison.OrdinalIgnoreCase)) && (category == "Все категории" || x.Category == category) && (status == "Все статусы" || x.Status == status) && (person == "Все пассажиры" || x.Person == person);
    }
    private void RefreshView() { _view?.Refresh(); WorkspaceSubtitle.Text = $"{_items.Count} позиций · {Days} дней · {TripType}"; RefreshAnalytics(); }
    private void ItemsGrid_CellEditEnding(object sender, DataGridCellEditEndingEventArgs e) => Dispatcher.BeginInvoke(() => { RefreshAnalytics(); QueueAutosave(); });
    private void RefreshAnalytics()
    {
        var totalUnits = _items.Sum(x => Math.Max(0, x.Quantity));
        var packedUnits = _items.Where(x => x.Status == "Уложено").Sum(x => Math.Max(0, x.Quantity));
        var percent = totalUnits == 0 ? 0 : packedUnits / (double)totalUnits * 100;
        var weight = _items.Where(x => x.Status == "Уложено").Sum(x => x.TotalWeight);
        var missingRequired = _items.Where(x => x.Required && x.Status != "Уложено").Sum(x => Math.Max(0, x.Quantity));
        var toBuyUnits = _items.Where(x => x.Status == "Купить").Sum(x => Math.Max(0, x.Quantity));
        ReadyPercentText.Text = $"{percent:0}%";
        ReadinessSummaryText.Text = $"{packedUnits} собрано · {toBuyUnits} купить";
        ReadyBar.Value = percent;
        WeightText.Text = $"{weight:0.0} кг";
        WeightLimitText.Text = $"лимит {Limit:0.0} кг";
        MissingText.Text = missingRequired.ToString();
        BuildRisks(weight, missingRequired);
        BuildCategoryProgress();
        BuildPeopleLoad();
    }
    private void BuildPeopleLoad()
    {
        PeopleLoadPanel.Children.Clear();
        var people = _items.Where(x => !string.Equals(x.Person, "Общее", StringComparison.OrdinalIgnoreCase))
            .Select(x => x.Person.Trim()).Where(x => x.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x).ToList();
        if (people.Count == 0)
        {
            PeopleLoadPanel.Children.Add(new TextBlock { Text = "Добавьте пассажиров к позициям, чтобы увидеть персональную нагрузку.", Foreground = Brush("#71667A"), FontSize = 10, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 4) });
            return;
        }

        var sharedWeight = _items.Where(x => x.Status == "Уложено" && string.Equals(x.Person, "Общее", StringComparison.OrdinalIgnoreCase)).Sum(x => x.TotalWeight);
        var perPersonLimit = Limit / people.Count;
        foreach (var person in people)
        {
            var personalWeight = _items.Where(x => x.Status == "Уложено" && string.Equals(x.Person, person, StringComparison.OrdinalIgnoreCase)).Sum(x => x.TotalWeight);
            var allocatedWeight = personalWeight + sharedWeight / people.Count;
            var row = new StackPanel { Margin = new Thickness(0, 0, 0, 9) };
            var header = new Grid();
            header.Children.Add(new TextBlock { Text = person, Foreground = Brush("#2C2334"), FontSize = 10, FontWeight = FontWeights.SemiBold });
            header.Children.Add(new TextBlock { Text = $"{allocatedWeight:0.0} / {perPersonLimit:0.0} кг", Foreground = Brush(allocatedWeight > perPersonLimit ? "#D84C68" : "#71667A"), FontSize = 9, HorizontalAlignment = HorizontalAlignment.Right });
            row.Children.Add(header);
            row.Children.Add(new ProgressBar { Maximum = Math.Max(.1, perPersonLimit), Value = Math.Min(allocatedWeight, Math.Max(.1, perPersonLimit)), Height = 5, Foreground = Brush(allocatedWeight > perPersonLimit ? "#D84C68" : "#3D294D"), Background = Brush("#D8CFE1"), Margin = new Thickness(0, 4, 0, 0) });
            PeopleLoadPanel.Children.Add(row);
        }
        if (sharedWeight > 0)
            PeopleLoadPanel.Children.Add(new TextBlock { Text = $"Общие вещи {sharedWeight:0.0} кг · доля уже учтена у каждого", Foreground = Brush("#71667A"), FontSize = 9, TextWrapping = TextWrapping.Wrap });
    }
    private void BuildRisks(double weight, int missingRequired)
    {
        RiskPanel.Children.Clear(); AddRisk(weight > Limit, weight > Limit ? $"Превышение лимита на {weight - Limit:0.0} кг" : $"Запас по весу: {Limit - weight:0.0} кг"); AddRisk(missingRequired > 0, missingRequired > 0 ? $"Не готово обязательных позиций: {missingRequired}" : "Все обязательные позиции готовы"); AddRisk(_items.Any(x => x.Status == "Купить"), _items.Any(x => x.Status == "Купить") ? $"Купить: {_items.Count(x => x.Status == "Купить")}" : "Список покупок закрыт"); AddRisk(_items.Any(x => x.Category == "Документы" && x.Required && x.Status != "Уложено"), "Проверьте документы перед выездом");
    }
    private void AddRisk(bool warning, string text) { var border = new Border { Background = Brush(warning ? "#FBECEF" : "#EEE7F4"), BorderBrush = Brush(warning ? "#D84C68" : "#8B6B9F"), BorderThickness = new Thickness(3, 0, 0, 0), Padding = new Thickness(10, 8, 8, 8), Margin = new Thickness(0, 0, 0, 6) }; border.Child = new TextBlock { Text = (warning ? "!  " : "✓  ") + text, TextWrapping = TextWrapping.Wrap, Foreground = Brush("#2C2334") }; RiskPanel.Children.Add(border); }
    private void BuildCategoryProgress()
    {
        CategoryProgressPanel.Children.Clear(); foreach (var g in _items.GroupBy(x => x.Category).OrderBy(x => x.Key)) { var ready = g.Count(x => x.Status == "Уложено"); var grid = new Grid { Margin = new Thickness(0, 0, 0, 8) }; grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); grid.Children.Add(new TextBlock { Text = g.Key, FontSize = 11, Foreground = Brush("#2C2334") }); var count = new TextBlock { Text = $"{ready}/{g.Count()}", FontSize = 10, Foreground = Brush("#71667A"), HorizontalAlignment = HorizontalAlignment.Right }; grid.Children.Add(count); var bar = new ProgressBar { Maximum = g.Count(), Value = ready, Height = 6, Foreground = Brush("#3D294D"), Background = Brush("#D8CFE1"), Margin = new Thickness(0, 5, 0, 0) }; Grid.SetRow(bar, 1); grid.Children.Add(bar); CategoryProgressPanel.Children.Add(grid); }
    }
    private async void Weather_Click(object sender, RoutedEventArgs e)
    {
        var cities = CitiesBox.Text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (cities.Length == 0)
        {
            FooterText.Text = "Добавьте в маршрут хотя бы один город, чтобы запросить погоду.";
            return;
        }

        WeatherButton.IsEnabled = false;
        WeatherButton.Content = "ИЩУ ПРОГНОЗ…";
        try
        {
            var apiKey = WeatherApiKeyStore.Load();
            var usingWeatherApi = !string.IsNullOrWhiteSpace(apiKey);
            var routeForecast = await WeatherRouteService.RefreshAsync(cities,
                usingWeatherApi ? city => _weatherApi.GetForecastAsync(city, apiKey) : FetchWeather);
            _weather.Clear();
            _weather.AddRange(routeForecast.Cards);

            FooterText.Text = usingWeatherApi
                ? routeForecast.OfflineCount == 0
                    ? $"WeatherAPI · прогноз на 7 дней · {routeForecast.OnlineCount} городов"
                    : $"WeatherAPI: онлайн {routeForecast.OnlineCount} · резервная оценка {routeForecast.OfflineCount}; проверьте ключ и названия городов"
                : routeForecast.OfflineCount == 0
                    ? $"Open-Meteo · прогноз на 7 дней · {routeForecast.OnlineCount} городов"
                    : $"Погода проверена: онлайн {routeForecast.OnlineCount} · офлайн-оценка {routeForecast.OfflineCount}";
            if (routeForecast.OnlineCount > 0) TravelDiagnostics.Info("weather.partial-or-full-success");
        }
        catch (Exception ex)
        {
            TravelDiagnostics.Error("weather.refresh-failed", ex);
            BuildFallbackWeather();
            FooterText.Text = "Сеть недоступна · отображена офлайн-оценка";
        }
        finally
        {
            WeatherButton.IsEnabled = true;
            WeatherButton.Content = "ОБНОВИТЬ ПРОГНОЗ";
        }

        ApplyWeatherItems();
        RenderWeather();
        RefreshAnalytics();
        QueueAutosave();
    }
    private void WeatherSettings_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new WeatherApiSettingsWindow { Owner = this };
        if (dialog.ShowDialog() == true)
            FooterText.Text = string.IsNullOrWhiteSpace(WeatherApiKeyStore.Load())
                ? "Ключ WeatherAPI удалён · Open-Meteo остаётся доступен"
                : "Ключ WeatherAPI сохранён для текущей учётной записи Windows";
    }
    private async Task<WeatherCard> FetchWeather(string city)
    {
        var geoText = await _http.GetStringAsync($"https://geocoding-api.open-meteo.com/v1/search?name={Uri.EscapeDataString(city)}&count=1&language=ru&format=json"); using var geo = JsonDocument.Parse(geoText); var results = geo.RootElement.GetProperty("results"); if (results.GetArrayLength() == 0) throw new InvalidOperationException(); var p = results[0]; var lat = p.GetProperty("latitude").GetDouble().ToString(System.Globalization.CultureInfo.InvariantCulture); var lon = p.GetProperty("longitude").GetDouble().ToString(System.Globalization.CultureInfo.InvariantCulture); var name = p.GetProperty("name").GetString() ?? city; var forecastText = await _http.GetStringAsync($"https://api.open-meteo.com/v1/forecast?latitude={lat}&longitude={lon}&daily=weather_code,temperature_2m_max,temperature_2m_min,precipitation_probability_max,wind_speed_10m_max&timezone=auto&forecast_days=7"); using var doc = JsonDocument.Parse(forecastText); var d = doc.RootElement.GetProperty("daily"); var min = (int)Math.Round(d.GetProperty("temperature_2m_min").EnumerateArray().Min(x => x.GetDouble())); var max = (int)Math.Round(d.GetProperty("temperature_2m_max").EnumerateArray().Max(x => x.GetDouble())); var rain = (int)Math.Round(d.GetProperty("precipitation_probability_max").EnumerateArray().Max(x => x.GetDouble())); var wind = (int)Math.Round(d.GetProperty("wind_speed_10m_max").EnumerateArray().Max(x => x.GetDouble())); var code = d.GetProperty("weather_code")[0].GetInt32(); return new WeatherCard { City = name, Min = min, Max = max, Rain = rain, Wind = wind, Icon = WeatherIcon(code) };
    }
    private void BuildFallbackWeather() { _weather.Clear(); var cities = CitiesBox.Text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries); for (var i = 0; i < cities.Length; i++) _weather.Add(OfflineWeatherFactory.Create(cities[i], i)); }
    private void ApplyWeatherItems() { if (_weather.Any(x => x.Min < 10)) Ensure("Тёплый слой", "По погоде", .55, true); if (_weather.Any(x => x.Rain > 40)) Ensure("Дождевик", "По погоде", .3, true); if (_weather.Any(x => x.Max > 24)) Ensure("Солнцезащитный крем", "Здоровье", .2, false); }
    private void RenderWeather() { WeatherPanel.Children.Clear(); foreach (var w in _weather) { var grid = new Grid { Margin = new Thickness(0, 0, 0, 6), Background = Brush("#EEE7F4") }; grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(38) }); grid.ColumnDefinitions.Add(new ColumnDefinition()); grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); grid.Children.Add(new TextBlock { Text = w.Icon, FontSize = 18, Foreground = Brush("#D84C68"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }); var info = new StackPanel { Margin = new Thickness(4, 7, 0, 7) }; info.Children.Add(new TextBlock { Text = w.City + (w.Offline ? " · офлайн-оценка" : ""), FontWeight = FontWeights.SemiBold, Foreground = Brush("#2C2334") }); info.Children.Add(new TextBlock { Text = $"осадки {w.Rain}% · ветер {w.Wind} км/ч", Foreground = Brush("#71667A"), FontSize = 9 }); Grid.SetColumn(info, 1); grid.Children.Add(info); var temp = new TextBlock { Text = $"{Signed(w.Min)}…{Signed(w.Max)}°", Foreground = Brush("#3D294D"), FontWeight = FontWeights.Bold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(7) }; Grid.SetColumn(temp, 2); grid.Children.Add(temp); WeatherPanel.Children.Add(grid); } }
    private void Save_Click(object sender, RoutedEventArgs e) { PersistProject(silent: false); var d = new SaveFileDialog { Filter = "Проект Travel Ready (*.ready.json)|*.ready.json", FileName = "travel-ready.ready.json" }; if (d.ShowDialog() != true) return; var p = CaptureProject(); File.WriteAllText(d.FileName, JsonSerializer.Serialize(p, new JsonSerializerOptions { WriteIndented = true })); FooterText.Text = "Резервная копия сохранена: " + d.FileName; }
    private void Open_Click(object sender, RoutedEventArgs e) { var d = new OpenFileDialog { Filter = "Проект Travel Ready (*.ready.json)|*.ready.json" }; if (d.ShowDialog() != true) return; try { var p = JsonSerializer.Deserialize<PackingProject>(File.ReadAllText(d.FileName)) ?? throw new InvalidOperationException("Пустой проект."); ProjectNameBox.Text = p.Name; CitiesBox.Text = p.Cities; LimitBox.Text = p.WeightLimit.ToString("0.0"); _items.Clear(); foreach (var x in p.Items) _items.Add(x); _weather.Clear(); _weather.AddRange(p.Weather); WorkspaceTitle.Text = p.Name; RenderWeather(); RefreshView(); FooterText.Text = "Открыт: " + d.FileName; } catch (Exception ex) { ValidationText.Text = ex.Message; } }
    private static string Csv(string? value) => "\"" + (value ?? "").Replace("\"", "\"\"") + "\"";
    private void Export_Click(object sender, RoutedEventArgs e) { var d = new SaveFileDialog { Filter = "CSV (*.csv)|*.csv", FileName = "packing-list.csv" }; if (d.ShowDialog() != true) return; var lines = new List<string> { "name;category;person;quantity;unit_weight;required;status" }; lines.AddRange(_items.Select(x => $"{Csv(x.Name)};{Csv(x.Category)};{Csv(x.Person)};{x.Quantity};{x.UnitWeight.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)};{x.Required};{Csv(x.Status)}")); File.WriteAllLines(d.FileName, lines, new UTF8Encoding(true)); FooterText.Text = "Экспортировано: " + d.FileName; }
    private static string WeatherIcon(int code) => code >= 71 ? "❄" : code >= 61 ? "☂" : code >= 3 ? "☁" : "☀"; private static string Signed(int n) => n > 0 ? "+" + n : n.ToString(); private static SolidColorBrush Brush(string hex) => new((Color)ColorConverter.ConvertFromString(hex));
}
