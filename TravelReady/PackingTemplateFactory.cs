namespace TravelReady;

public static class PackingTemplateFactory
{
    public static IReadOnlyList<PackingEntry> Create(string template, int days)
    {
        days = Math.Clamp(days, 1, 365);
        var entries = new List<PackingEntry>
        {
            Item("Паспорт", "Документы", "Общее", 1, .05, true),
            Item("Билеты и маршрут", "Документы", "Общее", 1, .02, true),
            Item("Подтверждение проживания", "Документы", "Общее", 1, .01, true),
            Item("Телефон", "Техника", "Студент", 1, .22, true),
            Item("Зарядное устройство", "Техника", "Студент", 1, .18, true),
            Item("Аптечка", "Здоровье", "Общее", 1, .35, true),
            Item("Средства гигиены", "Здоровье", "Студент", 1, .3, true),
            Item("Футболка", "Одежда", "Студент", Math.Max(2, (int)Math.Ceiling(days / 2d)), .18, true),
            Item("Носки", "Одежда", "Студент", days, .05, true),
            Item("Нижнее бельё", "Одежда", "Студент", days, .06, true),
            Item("Удобные брюки", "Одежда", "Студент", 1, .55, true)
        };

        switch (template.Trim().ToLowerInvariant())
        {
            case "business":
                entries.AddRange([
                    Item("Ноутбук", "Техника", "Студент", 1, 1.4, true),
                    Item("Деловой комплект", "Одежда", "Студент", 1, .9, true),
                    Item("Папка с документами", "Документы", "Общее", 1, .25, false),
                    Item("Переходник для зарядки", "Техника", "Студент", 1, .12, false)
                ]);
                break;
            case "active":
                entries.AddRange([
                    Item("Спортивная форма", "Одежда", "Студент", 1, .65, true),
                    Item("Бутылка для воды", "Здоровье", "Студент", 1, .22, true),
                    Item("Фонарь", "Техника", "Студент", 1, .18, false),
                    Item("Термос", "Здоровье", "Студент", 1, .45, false),
                    Item("Пластырь от мозолей", "Здоровье", "Общее", 1, .03, true)
                ]);
                break;
            default:
                entries.AddRange([
                    Item("Городской рюкзак", "Одежда", "Студент", 1, .45, false),
                    Item("Пауэрбанк", "Техника", "Студент", 1, .32, false),
                    Item("Складной зонт", "По погоде", "Общее", 1, .28, false),
                    Item("Удобная обувь", "Одежда", "Студент", 1, .8, true)
                ]);
                break;
        }

        return entries;
    }

    private static PackingEntry Item(string name, string category, string person, int quantity, double weight, bool required) =>
        new() { Name = name, Category = category, Person = person, Quantity = quantity, UnitWeight = weight, Required = required, Status = "Не готово" };
}
