using System.Net.Http;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace TravelReady;

public static class WeatherApiKeyStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("TravelReady.WeatherApi.v1");
    private static string FilePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TravelReady", "weather-api.dat");

    public static string Load()
    {
        try
        {
            var cipher = File.ReadAllBytes(FilePath);
            var clear = ProtectedData.Unprotect(cipher, Entropy, DataProtectionScope.CurrentUser);
            try { return Encoding.UTF8.GetString(clear); }
            finally { CryptographicOperations.ZeroMemory(clear); }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException) { return ""; }
    }

    public static void Save(string apiKey)
    {
        apiKey = apiKey.Trim();
        if (apiKey.Length is < 8 or > 160) throw new ArgumentException("Ключ должен содержать от 8 до 160 символов.");
        var clear = Encoding.UTF8.GetBytes(apiKey);
        try
        {
            var cipher = ProtectedData.Protect(clear, Entropy, DataProtectionScope.CurrentUser);
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            var temporaryFile = FilePath + ".tmp";
            File.WriteAllBytes(temporaryFile, cipher);
            File.Move(temporaryFile, FilePath, true);
        }
        finally { CryptographicOperations.ZeroMemory(clear); }
    }

    public static bool Clear()
    {
        try { if (File.Exists(FilePath)) File.Delete(FilePath); return true; }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }
}

public sealed class WeatherApiClient(HttpClient? httpClient = null)
{
    private readonly HttpClient _http = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(10) };

    public async Task<WeatherCard> GetForecastAsync(string city, string apiKey, CancellationToken cancellationToken = default)
    {
        city = city.Trim();
        apiKey = apiKey.Trim();
        if (city.Length == 0) throw new ArgumentException("Укажите город маршрута.");
        if (apiKey.Length < 8) throw new InvalidOperationException("WeatherAPI-ключ не задан.");

        var url = $"https://api.weatherapi.com/v1/forecast.json?key={Uri.EscapeDataString(apiKey)}&q={Uri.EscapeDataString(city)}&days=7&lang=ru&aqi=no&alerts=no";
        using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(response.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden
                ? "WeatherAPI не принял ключ. Проверьте ключ в настройках погоды."
                : "WeatherAPI не вернул прогноз. Проверьте город и доступность сети.");

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        try
        {
            var root = document.RootElement;
            if (root.TryGetProperty("error", out var error))
            {
                var errorCode = error.TryGetProperty("code", out var codeElement) ? codeElement.ToString() : "unknown";
                throw new InvalidOperationException($"WeatherAPI returned error {errorCode}.");
            }
            var location = root.GetProperty("location");
            var days = root.GetProperty("forecast").GetProperty("forecastday").EnumerateArray().ToArray();
            if (days.Length == 0) throw new InvalidOperationException("WeatherAPI returned no forecast days.");

            var min = (int)Math.Round(days.Min(d => d.GetProperty("day").GetProperty("mintemp_c").GetDouble()));
            var max = (int)Math.Round(days.Max(d => d.GetProperty("day").GetProperty("maxtemp_c").GetDouble()));
            var rain = (int)Math.Round(days.Max(d => d.GetProperty("day").GetProperty("daily_chance_of_rain").GetDouble()));
            var wind = (int)Math.Round(days.Max(d => d.GetProperty("day").GetProperty("maxwind_kph").GetDouble()));
            var code = days[0].GetProperty("day").GetProperty("condition").GetProperty("code").GetInt32();
            return new WeatherCard
            {
                City = location.GetProperty("name").GetString() ?? city,
                Min = min,
                Max = max,
                Rain = rain,
                Wind = wind,
                Icon = IconFor(code),
                Offline = false
            };
        }
        catch (Exception ex) when (ex is KeyNotFoundException or InvalidOperationException or FormatException)
        {
            throw new InvalidOperationException("WeatherAPI returned an invalid forecast. Check the key and city name.");
        }
    }

    private static string IconFor(int code) => code switch
    {
        1000 => "☀",
        1003 => "◐",
        1006 or 1009 => "☁",
        1030 or 1135 or 1147 => "≋",
        1063 or 1150 or 1153 or 1168 or 1171 or 1180 or 1183 or 1186 or 1189 or 1192 or 1195 or 1240 or 1243 or 1246 => "☂",
        1066 or 1069 or 1072 or 1114 or 1117 or 1204 or 1207 or 1210 or 1213 or 1216 or 1219 or 1222 or 1225 or 1237 or 1255 or 1258 or 1261 or 1264 => "❄",
        1087 or 1273 or 1276 or 1279 or 1282 => "⚡",
        _ => "☁"
    };
}

public sealed class WeatherApiSettingsWindow : Window
{
    private readonly PasswordBox _key = new() { Padding = new Thickness(11), Margin = new Thickness(0, 7, 0, 16), MinWidth = 420, FontSize = 14 };

    public WeatherApiSettingsWindow()
    {
        Title = "Travel Ready · ключ прогноза погоды";
        Width = 560;
        Height = 360;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Brush("#F2F5F4");
        Foreground = Brush("#26332F");

        var stack = new StackPanel { Margin = new Thickness(26) };
        stack.Children.Add(new TextBlock { Text = "Погода по ключу API", FontFamily = new FontFamily("Times New Roman"), FontSize = 25, FontWeight = FontWeights.Bold, Foreground = Brush("#155544"), Margin = new Thickness(0, 0, 0, 8) });
        stack.Children.Add(new TextBlock { Text = "Вставьте личный ключ WeatherAPI. Он хранится с шифрованием Windows DPAPI и передаётся поставщику по HTTPS только при запросе прогноза.", TextWrapping = TextWrapping.Wrap, Foreground = Brush("#687973"), Margin = new Thickness(0, 0, 0, 15) });
        stack.Children.Add(new TextBlock { Text = "КЛЮЧ WEATHERAPI", Foreground = Brush("#52655E"), FontSize = 10, FontWeight = FontWeights.SemiBold });
        stack.Children.Add(_key);
        stack.Children.Add(new TextBlock { Text = "Без ключа остаётся доступен Open-Meteo. WeatherAPI используется для прогноза до 7 дней по городам маршрута.", TextWrapping = TextWrapping.Wrap, Foreground = Brush("#687973"), FontSize = 10, Margin = new Thickness(0, 0, 0, 14) });

        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var clear = MakeButton("УДАЛИТЬ КЛЮЧ", "#EEF3F1", "#155544");
        clear.Margin = new Thickness(0, 0, 8, 0);
        clear.Click += (_, _) =>
        {
            if (WeatherApiKeyStore.Clear()) DialogResult = true;
            else MessageBox.Show(this, "Не удалось удалить сохранённый ключ. Проверьте права доступа к папке приложения.", "Ключ не удалён", MessageBoxButton.OK, MessageBoxImage.Warning);
        };
        var save = MakeButton("СОХРАНИТЬ", "#155544", "#FFFFFF");
        save.IsDefault = true;
        save.Click += (_, _) =>
        {
            try { WeatherApiKeyStore.Save(_key.Password); DialogResult = true; }
            catch (ArgumentException ex) { MessageBox.Show(this, ex.Message, "Проверьте ключ", MessageBoxButton.OK, MessageBoxImage.Information); }
        };
        actions.Children.Add(clear);
        actions.Children.Add(save);
        stack.Children.Add(actions);
        Content = stack;
    }

    private static Button MakeButton(string title, string background, string foreground) => new()
    {
        Content = title,
        Padding = new Thickness(14, 10, 14, 10),
        Background = Brush(background),
        Foreground = Brush(foreground),
        BorderThickness = new Thickness(0),
        Cursor = System.Windows.Input.Cursors.Hand
    };

    private static SolidColorBrush Brush(string color) => new((Color)ColorConverter.ConvertFromString(color));
}
