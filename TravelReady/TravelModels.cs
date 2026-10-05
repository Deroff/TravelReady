namespace TravelReady;

public sealed class PackingEntry
{
    public string Name { get; set; } = "";
    public string Category { get; set; } = "";
    public string Person { get; set; } = "Общее";
    public int Quantity { get; set; } = 1;
    public double UnitWeight { get; set; }
    public bool Required { get; set; }
    public string Status { get; set; } = "Не готово";
    public double TotalWeight => Quantity * UnitWeight;
}

public sealed class WeatherCard
{
    public string City { get; set; } = "";
    public int Min { get; set; }
    public int Max { get; set; }
    public int Rain { get; set; }
    public int Wind { get; set; }
    public string Icon { get; set; } = "☀";
    public bool Offline { get; set; }
}

public sealed class PackingProject
{
    public string Name { get; set; } = "";
    public string Cities { get; set; } = "";
    public int Days { get; set; }
    public string TripType { get; set; } = "";
    public double WeightLimit { get; set; }
    public List<PackingEntry> Items { get; set; } = [];
    public List<WeatherCard> Weather { get; set; } = [];
    public DateTime SavedAt { get; set; }
}
