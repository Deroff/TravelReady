using System.Net.Http;
using System.Text.Json;

namespace TravelReady;

public sealed record WeatherRouteResult(IReadOnlyList<WeatherCard> Cards, int OnlineCount)
{
    public int OfflineCount => Cards.Count - OnlineCount;
}

/// <summary>Обновляет прогноз по точкам независимо: ошибка одного города не сбрасывает остальные.</summary>
public static class WeatherRouteService
{
    public static async Task<WeatherRouteResult> RefreshAsync(
        IReadOnlyList<string> cities,
        Func<string, Task<WeatherCard>> fetchCity)
    {
        ArgumentNullException.ThrowIfNull(cities);
        ArgumentNullException.ThrowIfNull(fetchCity);

        var cards = new List<WeatherCard>(cities.Count);
        var onlineCount = 0;
        for (var index = 0; index < cities.Count; index++)
        {
            var city = cities[index].Trim();
            if (city.Length == 0) continue;

            try
            {
                cards.Add(await fetchCity(city));
                onlineCount++;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException or JsonException)
            {
                TravelDiagnostics.Warning("weather.city-fallback", ex);
                cards.Add(OfflineWeatherFactory.Create(city, index));
            }
        }

        return new WeatherRouteResult(cards, onlineCount);
    }
}
