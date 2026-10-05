using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Http;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;

namespace TravelReady;

public sealed record MailApiSettings(string BaseUrl = "", string Token = "");

public static class MailApiSettingsStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("TravelReady.MailApi.v1");
    private static string FilePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TravelReady", "mail-api.dat");
    public static MailApiSettings Load()
    {
        var saved = Read();
        var url = Environment.GetEnvironmentVariable("TRAVELREADY_MAIL_API_URL");
        var token = Environment.GetEnvironmentVariable("TRAVELREADY_MAIL_API_TOKEN");
        var shared = ReadRouteStudioSettings();
        return new(string.IsNullOrWhiteSpace(url) ? (string.IsNullOrWhiteSpace(saved.BaseUrl) ? shared.BaseUrl : saved.BaseUrl) : url,
            string.IsNullOrWhiteSpace(token) ? (string.IsNullOrWhiteSpace(saved.Token) ? shared.Token : saved.Token) : token);
    }
    private static MailApiSettings ReadRouteStudioSettings()
    {
        var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RouteStudio"); string url = "", token = "";
        try
        {
            var file = Path.Combine(folder, "provider-credentials.dat");
            if (File.Exists(file))
            {
                var plain = ProtectedData.Unprotect(File.ReadAllBytes(file), Encoding.UTF8.GetBytes("RouteStudio.ProviderCredentials.v1"), DataProtectionScope.CurrentUser);
                try { using var json = JsonDocument.Parse(plain); var root = json.RootElement;
                    if (root.TryGetProperty("MailApiBaseUrl", out var value)) url = value.GetString() ?? "";
                    if (root.TryGetProperty("MailApiToken", out value)) token = value.GetString() ?? "";
                } finally { CryptographicOperations.ZeroMemory(plain); }
            }
            if (string.IsNullOrWhiteSpace(token))
            {
                file = Path.Combine(folder, "mail-api-token.dat");
                if (File.Exists(file))
                {
                    var plain = ProtectedData.Unprotect(File.ReadAllBytes(file), Encoding.UTF8.GetBytes("RouteStudio.MailApi.LocalToken.v1"), DataProtectionScope.CurrentUser);
                    try { token = Encoding.UTF8.GetString(plain); } finally { CryptographicOperations.ZeroMemory(plain); }
                }
            }
            if (!string.IsNullOrWhiteSpace(token) && string.IsNullOrWhiteSpace(url)) url = "http://127.0.0.1:5080";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException or JsonException) { return new(); }
        return new(url, token);
    }
    private static MailApiSettings Read()
    {
        try
        {
            var encrypted = File.ReadAllBytes(FilePath); var data = ProtectedData.Unprotect(encrypted, Entropy, DataProtectionScope.CurrentUser);
            try { return JsonSerializer.Deserialize<MailApiSettings>(data) ?? new(); }
            finally { CryptographicOperations.ZeroMemory(data); }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException or JsonException) { return new(); }
    }
    public static void Save(MailApiSettings settings)
    {
        var data = JsonSerializer.SerializeToUtf8Bytes(settings);
        try
        {
            var encrypted = ProtectedData.Protect(data, Entropy, DataProtectionScope.CurrentUser);
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!); var temp = FilePath + ".tmp";
            File.WriteAllBytes(temp, encrypted); File.Move(temp, FilePath, true);
        }
        finally { CryptographicOperations.ZeroMemory(data); }
    }
}

public sealed class MailApiClient
{
    private static readonly HttpClient Http = new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(20) };
    private readonly MailApiSettings _settings;
    private readonly HttpClient _http;
    public MailApiClient() : this(MailApiSettingsStore.Load(), Http) { }
    public MailApiClient(MailApiSettings settings, HttpClient http) { _settings = settings; _http = http; }
    public bool IsConfigured => Uri.TryCreate(_settings.BaseUrl, UriKind.Absolute, out var uri) &&
        (uri.Scheme == Uri.UriSchemeHttps || (uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback)) &&
        string.IsNullOrEmpty(uri.UserInfo) && string.IsNullOrEmpty(uri.Query) && string.IsNullOrEmpty(uri.Fragment) && _settings.Token.Trim().Length >= 24;

    public Task SendRegistrationCodeAsync(string email, string displayName, string code) =>
        SendCodeAsync(email, code, "registration", new { appName = "Travel Ready", email, displayName, purpose = "registration", code }, displayName);

    public Task SendPasswordResetCodeAsync(string email, string code) =>
        SendCodeAsync(email, code, "password-reset", new { appName = "Travel Ready", email, purpose = "password-reset", code });

    private async Task SendCodeAsync(string email, string code, string purpose, object payload, string? displayName = null)
    {
        if (!IsConfigured) throw new InvalidOperationException("Почта не настроена. Укажите HTTPS-адрес Mail API и токен в настройках почты.");
        using var request = new HttpRequestMessage(HttpMethod.Post, _settings.BaseUrl.TrimEnd('/') + "/api/mail/account-code")
        { Content = JsonContent.Create(payload) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _settings.Token.Trim());
        using var response = await _http.SendAsync(request);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound && purpose == "registration")
        {
            using var legacy = new HttpRequestMessage(HttpMethod.Post, _settings.BaseUrl.TrimEnd('/') + "/api/mail/registration-code")
            { Content = JsonContent.Create(new { email, displayName, code }) };
            legacy.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _settings.Token.Trim());
            using var legacyResponse = await _http.SendAsync(legacy);
            if (legacyResponse.IsSuccessStatusCode) return;
            string? legacyDetail = null;
            try { using var json = await JsonDocument.ParseAsync(await legacyResponse.Content.ReadAsStreamAsync()); if (json.RootElement.TryGetProperty("error", out var error)) legacyDetail = error.GetString(); } catch (JsonException) { }
            throw new InvalidOperationException(legacyDetail ?? "Сервер почты устарел или не смог отправить код (HTTP " + (int)legacyResponse.StatusCode + ").");
        }
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            throw new InvalidOperationException("Почтовый сервер не поддерживает отправку кода восстановления. Обновите Mail API.");
        if (response.IsSuccessStatusCode) return;
        string? detail = null;
        try { using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync()); if (json.RootElement.TryGetProperty("error", out var error)) detail = error.GetString(); } catch (JsonException) { }
        throw new InvalidOperationException(detail ?? response.StatusCode switch
        {
            System.Net.HttpStatusCode.Unauthorized => "Mail API отклонил токен. Проверьте настройки доступа.",
            System.Net.HttpStatusCode.TooManyRequests => "Слишком много запросов. Подождите минуту и попробуйте снова.",
            _ => "Почтовый сервер не смог отправить письмо (HTTP " + (int)response.StatusCode + ")."
        });
    }
}

public sealed class EmailCodeChallenge
{
    private readonly byte[] _hash = SHA256.HashData(RandomNumberGenerator.GetBytes(32));
    private DateTime _expiresUtc;
    private int _failedAttempts;
    public string Issue()
    {
        var code = RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6");
        var bytes = Encoding.UTF8.GetBytes(code); var hash = SHA256.HashData(bytes); CryptographicOperations.ZeroMemory(bytes);
        CryptographicOperations.ZeroMemory(_hash); hash.CopyTo(_hash, 0); _expiresUtc = DateTime.UtcNow.AddMinutes(10); _failedAttempts = 0; return code;
    }
    public bool Verify(string code)
    {
        code = code.Trim(); if (DateTime.UtcNow > _expiresUtc || _failedAttempts >= 5 || code.Length != 6 || !code.All(char.IsAsciiDigit)) return false;
        var bytes = Encoding.UTF8.GetBytes(code); var candidate = SHA256.HashData(bytes); CryptographicOperations.ZeroMemory(bytes);
        var valid = CryptographicOperations.FixedTimeEquals(_hash, candidate); CryptographicOperations.ZeroMemory(candidate);
        if (valid) CryptographicOperations.ZeroMemory(_hash); else if (++_failedAttempts >= 5) CryptographicOperations.ZeroMemory(_hash); return valid;
    }
}

public sealed class MailApiSettingsWindow : Window
{
    private readonly TextBox _url = new() { MinWidth = 390, Margin = new Thickness(0, 4, 0, 14), Padding = new Thickness(10), Background = new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#FFFFFF")), Foreground = new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#30263A")), BorderBrush = new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#DDD5E2")) };
    private readonly PasswordBox _token = new() { MinWidth = 390, Margin = new Thickness(0, 4, 0, 14), Padding = new Thickness(10), Background = new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#FFFFFF")), Foreground = new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#30263A")), BorderBrush = new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#DDD5E2")) };
    public MailApiSettingsWindow()
    {
        Title = "Travel Ready · почтовые настройки"; Width = 520; Height = 340; ResizeMode = ResizeMode.NoResize; WindowStartupLocation = WindowStartupLocation.CenterOwner; Background = new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#F4F1F8")); Foreground = new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#30263A"));
        var settings = MailApiSettingsStore.Load(); _url.Text = settings.BaseUrl; _token.Password = settings.Token;
        var stack = new StackPanel { Margin = new Thickness(24) };
        stack.Children.Add(new TextBlock { Text = "Почтовые настройки", FontSize = 20, FontFamily = new System.Windows.Media.FontFamily("Georgia"), FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 8), Foreground = new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#3D294D")) });
        stack.Children.Add(new TextBlock { Text = "HTTPS-адрес API (HTTP допустим только для localhost). SMTP-секрет остаётся на сервере; токен API защищён DPAPI для текущего профиля Windows.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12), Foreground = new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#71677A")) });
        stack.Children.Add(new TextBlock { Text = "АДРЕС MAIL API", Foreground = new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#71677A")) }); stack.Children.Add(_url); stack.Children.Add(new TextBlock { Text = "ТОКЕН ДОСТУПА", Foreground = new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#71677A")) }); stack.Children.Add(_token);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(new Button { Content = "Отмена", Padding = new Thickness(16, 8, 16, 8), Margin = new Thickness(0, 0, 8, 0), IsCancel = true, Background = new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#ECE7F1")), Foreground = new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#30263A")), BorderThickness = new Thickness(0) });
        var save = new Button { Content = "Сохранить", Padding = new Thickness(16, 8, 16, 8), IsDefault = true, Background = new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#3D294D")), Foreground = System.Windows.Media.Brushes.White, BorderThickness = new Thickness(0) };
        save.Click += (_, _) => { if (!Uri.TryCreate(_url.Text.Trim(), UriKind.Absolute, out var uri) || (uri.Scheme != "https" && !(uri.Scheme == "http" && uri.IsLoopback)) || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment) || _token.Password.Trim().Length < 24) { MessageBox.Show(this, "Введите HTTPS/локальный HTTP-адрес API без логина и query-параметров и токен не короче 24 символов.", "Почта не настроена", MessageBoxButton.OK, MessageBoxImage.Warning); return; } MailApiSettingsStore.Save(new(_url.Text.Trim().TrimEnd('/'), _token.Password.Trim())); DialogResult = true; };
        buttons.Children.Add(save); stack.Children.Add(buttons); Content = stack;
    }
}
