using System.Security.Cryptography;
using System.Text;

namespace TravelReady;

public static class AccountSecurity
{
    public const int Iterations = 240_000;

    public static (byte[] Salt, byte[] Hash) HashPassword(string password)
    {
        ValidatePassword(password);
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, 32);
        return (salt, hash);
    }

    public static bool VerifyPassword(string password, byte[] salt, byte[] expected, int iterations)
    {
        var actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, expected.Length);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    public static void ValidatePassword(string password)
    {
        if (password.Length < 8 || password.Length > 128)
            throw new ArgumentException("Пароль должен содержать от 8 до 128 символов.");
        if (!password.Any(char.IsLetter) || !password.Any(char.IsDigit))
            throw new ArgumentException("Добавьте в пароль хотя бы одну букву и одну цифру.");
        if (password.Distinct().Count() < 4)
            throw new ArgumentException("Слишком простой пароль: используйте больше разных символов.");
        var normalized = password.Trim().ToLowerInvariant();
        if (new[] { "password", "qwerty", "123456", "admin", "letmein" }.Any(normalized.Contains))
            throw new ArgumentException("Пароль содержит слишком распространённую последовательность.");
    }

    public static string NormalizeEmail(string email) => email.Trim().ToUpperInvariant();

    public static void ValidateEmail(string email)
    {
        var value = email.Trim();
        var at = value.IndexOf('@');
        var dot = value.LastIndexOf('.');
        if (at < 1 || at != value.LastIndexOf('@') || dot <= at + 1 || dot == value.Length - 1 || value.Contains(' ') || value.Any(char.IsControl))
            throw new ArgumentException("Введите почту в формате name@example.com.");
    }
}

public static class PasswordResetCode
{
    private const string Alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
    public const int Length = 16;

    public static string Create()
    {
        var random = RandomNumberGenerator.GetBytes(Length);
        try
        {
            return new string(random.Select(value => Alphabet[value & 31]).ToArray());
        }
        finally { CryptographicOperations.ZeroMemory(random); }
    }

    public static bool TryHash(string? code, out byte[] hash)
    {
        var normalized = code?.Trim().ToUpperInvariant();
        if (normalized is null || normalized.Length != Length || normalized.Any(character => !Alphabet.Contains(character)))
        {
            hash = new byte[32];
            return false;
        }
        var bytes = Encoding.ASCII.GetBytes(normalized);
        try { hash = SHA256.HashData(bytes); return true; }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
}
