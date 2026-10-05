namespace TravelReady;

/// <summary>Явно помеченная резервная оценка для города, который не удалось проверить онлайн.</summary>
public static class OfflineWeatherFactory
{
    public static WeatherCard Create(string city, int routeIndex)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(city);
        var index = Math.Max(0, routeIndex);
        return new WeatherCard
        {
            City = city.Trim(),
            Min = 5 + index * 2,
            Max = 14 + index * 3,
            Rain = Math.Min(90, 25 + index * 20),
            Wind = 10 + index * 3,
            Icon = index % 2 == 0 ? "☁" : "☂",
            Offline = true
        };
    }
}
