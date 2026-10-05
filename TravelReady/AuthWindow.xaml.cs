using System.Windows;

namespace TravelReady;

public partial class AuthWindow : Window
{
    private readonly TravelDatabase _database;
    private bool _registering;
    private bool _awaitingRegistrationCode;
    private bool _resetting;
    private bool _awaitingResetCode;
    private string _challengeEmail = "";
    private string _challengeName = "";
    private string _resetEmail = "";
    private readonly EmailCodeChallenge _challenge = new();
    public AppUser? AuthenticatedUser { get; private set; }

    public AuthWindow(TravelDatabase database)
    {
        InitializeComponent();
        _database = database;
        SetMode(false);
        EmailBox.Focus();
    }

    private void SignInTab_Click(object sender, RoutedEventArgs e) => SetMode(false);
    private void RegisterTab_Click(object sender, RoutedEventArgs e) => SetMode(true);

    private void SetMode(bool register)
    {
        _registering = register;
        _resetting = false;
        _awaitingResetCode = false;
        _resetEmail = "";
        PasswordField.Visibility = Visibility.Visible;
        DisplayNameField.Visibility = register ? Visibility.Visible : Visibility.Collapsed;
        ConfirmPasswordField.Visibility = register ? Visibility.Visible : Visibility.Collapsed;
        VerificationField.Visibility = Visibility.Collapsed;
        VerificationCodeBox.MaxLength = 6;
        VerificationCodeBox.Clear();
        ResendResetButton.Visibility = Visibility.Collapsed;
        ResetPasswordButton.Visibility = register ? Visibility.Collapsed : Visibility.Visible;
        _awaitingRegistrationCode = false;
        RegisterTab.Background = register ? Brush("#3D294D") : Brush("#F4F1F8");
        RegisterTab.Foreground = register ? System.Windows.Media.Brushes.White : Brush("#71677A");
        SignInTab.Background = register ? Brush("#F4F1F8") : Brush("#3D294D");
        SignInTab.Foreground = register ? Brush("#71677A") : System.Windows.Media.Brushes.White;
        ContinueButton.Content = register ? "СОЗДАТЬ ПРОФИЛЬ  →" : "ОТКРЫТЬ ПУТЕВОЙ ЖУРНАЛ  →";
        FeedbackText.Text = register ? "Пароль: не менее 8 символов, буквы и цифры." : "";
    }

    private void ResetPassword_Click(object sender, RoutedEventArgs e)
    {
        SetMode(false);
        _resetting = true;
        PasswordField.Visibility = Visibility.Collapsed;
        ConfirmPasswordField.Visibility = Visibility.Collapsed;
        ResetPasswordButton.Visibility = Visibility.Collapsed;
        SignInTab.Background = Brush("#F4F1F8");
        SignInTab.Foreground = Brush("#71677A");
        ContinueButton.Content = "ПОЛУЧИТЬ КОД ВОССТАНОВЛЕНИЯ  →";
        FeedbackText.Text = "Укажите почту профиля. Код для смены пароля придёт письмом.";
        PasswordBox.Clear();
        ConfirmPasswordBox.Clear();
        EmailBox.Focus();
    }

    private void ResendReset_Click(object sender, RoutedEventArgs e)
    {
        if (!_resetting || !_awaitingResetCode) return;
        _awaitingResetCode = false;
        VerificationCodeBox.Clear();
        PasswordField.Visibility = Visibility.Collapsed;
        ConfirmPasswordField.Visibility = Visibility.Collapsed;
        VerificationField.Visibility = Visibility.Collapsed;
        ResendResetButton.Visibility = Visibility.Collapsed;
        ContinueButton.Content = "ПОЛУЧИТЬ КОД ВОССТАНОВЛЕНИЯ  →";
        Continue_Click(sender, e);
    }

    private async void Continue_Click(object sender, RoutedEventArgs e)
    {
        ContinueButton.IsEnabled = false;
        SignInTab.IsEnabled = false;
        RegisterTab.IsEnabled = false;
        ResendResetButton.IsEnabled = false;
        try
        {
            if (_resetting)
            {
                await ContinuePasswordResetAsync();
                return;
            }
            if (_registering)
            {
                if (PasswordBox.Password != ConfirmPasswordBox.Password)
                    throw new ArgumentException("Пароли не совпадают.");
                AccountSecurity.ValidateEmail(EmailBox.Text);
                AccountSecurity.ValidatePassword(PasswordBox.Password);
                if (!_awaitingRegistrationCode)
                {
                    if (string.IsNullOrWhiteSpace(DisplayNameBox.Text) || DisplayNameBox.Text.Trim().Length > 80)
                        throw new ArgumentException("Введите имя профиля длиной до 80 символов.");
                    var settings = new MailApiClient();
                    if (!settings.IsConfigured)
                    {
                        new MailApiSettingsWindow { Owner = this }.ShowDialog();
                        settings = new MailApiClient();
                        if (!settings.IsConfigured) throw new InvalidOperationException("Настройте Mail API, затем повторите отправку кода.");
                    }
                    _challengeEmail = EmailBox.Text.Trim(); _challengeName = DisplayNameBox.Text.Trim();
                    var code = _challenge.Issue();
                    await settings.SendRegistrationCodeAsync(_challengeEmail, _challengeName, code);
                    _awaitingRegistrationCode = true;
                    VerificationField.Visibility = Visibility.Visible;
                    ContinueButton.Content = "ПОДТВЕРДИТЬ ПОЧТУ И СОЗДАТЬ ПРОФИЛЬ  →";
                    FeedbackText.Text = "Код отправлен на " + _challengeEmail + ". Введите его выше.";
                    VerificationCodeBox.Focus();
                    return;
                }
                if (!EmailBox.Text.Trim().Equals(_challengeEmail, StringComparison.OrdinalIgnoreCase) || !DisplayNameBox.Text.Trim().Equals(_challengeName, StringComparison.Ordinal))
                    throw new InvalidOperationException("Почта или имя изменились. Запросите новый код подтверждения.");
                if (!_challenge.Verify(VerificationCodeBox.Text))
                {
                    _awaitingRegistrationCode = false;
                    VerificationCodeBox.Clear();
                    ContinueButton.Content = "ОТПРАВИТЬ НОВЫЙ КОД ИЛИ ПОДТВЕРДИТЬ  →";
                    throw new ArgumentException("Код неверный, исчерпаны попытки или истёк срок. Повторно нажмите кнопку, чтобы отправить код ещё раз.");
                }
                AuthenticatedUser = _database.Register(EmailBox.Text, DisplayNameBox.Text, PasswordBox.Password);
                TravelDiagnostics.Info("account.registered");
            }
            else
            {
                AuthenticatedUser = _database.SignIn(EmailBox.Text, PasswordBox.Password);
                TravelDiagnostics.Info("account.signed-in");
            }
            DialogResult = true;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.Net.Http.HttpRequestException or TaskCanceledException)
        {
            TravelDiagnostics.Warning(_resetting ? "account.reset-rejected" : _registering ? "account.registration-rejected" : "account.sign-in-rejected", ex);
            FeedbackText.Text = ex is System.Net.Http.HttpRequestException or TaskCanceledException
                ? "Почтовый сервер недоступен. Проверьте подключение и попробуйте снова."
                : ex.Message;
        }
        finally { ContinueButton.IsEnabled = true; SignInTab.IsEnabled = true; RegisterTab.IsEnabled = true; ResendResetButton.IsEnabled = true; }
    }

    private async Task ContinuePasswordResetAsync()
    {
        AccountSecurity.ValidateEmail(EmailBox.Text);
        if (!_awaitingResetCode)
        {
            var mail = new MailApiClient();
            if (!mail.IsConfigured)
            {
                new MailApiSettingsWindow { Owner = this }.ShowDialog();
                mail = new MailApiClient();
                if (!mail.IsConfigured) throw new InvalidOperationException("Настройте Mail API, затем повторите запрос кода.");
            }
            var request = _database.BeginPasswordReset(EmailBox.Text);
            if (request is not null)
            {
                try
                {
                    await mail.SendPasswordResetCodeAsync(request.Email, request.Code);
                    _database.MarkPasswordResetDelivered(request);
                }
                catch
                {
                    try { _database.CancelPasswordReset(request); }
                    catch (Exception ex) { TravelDiagnostics.Warning("account.reset-cancel-failed", ex); }
                    throw;
                }
            }
            _resetEmail = EmailBox.Text.Trim();
            _awaitingResetCode = true;
            PasswordField.Visibility = Visibility.Visible;
            ConfirmPasswordField.Visibility = Visibility.Visible;
            VerificationField.Visibility = Visibility.Visible;
            VerificationCodeBox.MaxLength = PasswordResetCode.Length;
            VerificationCodeBox.Clear();
            ResendResetButton.Visibility = Visibility.Visible;
            ContinueButton.Content = "СОХРАНИТЬ НОВЫЙ ПАРОЛЬ  →";
            FeedbackText.Text = "Если запрос допустим, проверьте письмо с кодом. При повторном запросе в течение минуты используйте предыдущий код.";
            VerificationCodeBox.Focus();
            return;
        }
        if (!EmailBox.Text.Trim().Equals(_resetEmail, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Адрес изменился. Вернитесь ко входу и запросите код заново.");
        if (PasswordBox.Password != ConfirmPasswordBox.Password)
            throw new ArgumentException("Пароли не совпадают.");
        _database.CompletePasswordReset(_resetEmail, VerificationCodeBox.Text, PasswordBox.Password);
        TravelDiagnostics.Info("account.password-reset");
        SetMode(false);
        PasswordBox.Clear();
        ConfirmPasswordBox.Clear();
        FeedbackText.Text = "Пароль изменён. Войдите с новым паролем.";
        PasswordBox.Focus();
    }

    private void MailSettings_Click(object sender, RoutedEventArgs e)
    {
        new MailApiSettingsWindow { Owner = this }.ShowDialog();
        FeedbackText.Text = new MailApiClient().IsConfigured ? "Mail API настроен для отправки писем." : "Mail API пока не настроен.";
    }

    private static System.Windows.Media.Brush Brush(string color) => new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(color));
}
