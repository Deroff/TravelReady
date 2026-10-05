using TravelReady;
using Microsoft.Data.Sqlite;
using Microsoft.Data.SqlClient;

if (args.Contains("--migrate-production", StringComparer.Ordinal))
{
    var production = new TravelDatabase();
    var counts = production.CountRows();
    var integrity = production.VerifyIntegrity();
    if (integrity.Integrity != "ok" || integrity.ForeignKeyViolations != 0) throw new InvalidOperationException("Проверка целостности production-базы не пройдена.");
    Console.WriteLine($"PRODUCTION_DB_READY: {production.DatabasePath}; users={counts.Users}; trips={counts.Trips}; items={counts.Items}; integrity={integrity.Integrity}");
    return 0;
}

var root = Path.Combine(Path.GetTempPath(), "travel-ready-test-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
var server = Environment.GetEnvironmentVariable("TRAVELREADY_SQL_SERVER") ?? @"(localdb)\MSSQLLocalDB";
var databaseName = "TravelReadyTest_" + Guid.NewGuid().ToString("N");
var testCatalogs = new List<string> { databaseName };
var db = new TravelDatabase(Path.Combine(root, "integration.db"), server, databaseName);
var passed = 0;
var failed = 0;
void Check(string name, Action action)
{
    try { action(); passed++; Console.WriteLine("PASS " + name); }
    catch (Exception ex) { failed++; Console.WriteLine("FAIL " + name + " — " + ex.Message); }
}
void Throws<T>(Action action) where T : Exception
{
    try { action(); throw new Exception("Ожидалось исключение " + typeof(T).Name); }
    catch (T) { }
}

Check("почтовый код подтверждения действует один раз", () =>
{
    var challenge = new EmailCodeChallenge(); var code = challenge.Issue();
    if (code.Length != 6 || !code.All(char.IsAsciiDigit) || !challenge.Verify(code) || challenge.Verify(code))
        throw new Exception("Код регистрации не прошёл одноразовую проверку.");
    var limited = new EmailCodeChallenge(); var correct = limited.Issue(); var wrong = correct == "000000" ? "000001" : "000000";
    for (var attempt = 0; attempt < 5; attempt++) if (limited.Verify(wrong)) throw new Exception("Неверный код принят.");
    if (limited.Verify(correct)) throw new Exception("Код остался действителен после пяти ошибок.");
});

Check("база создаёт пустую схему без данных пользователя", () =>
{
    var counts = db.CountRows();
    if (counts.Users != 0 || counts.Trips != 0 || counts.Items != 0) throw new Exception($"Неожиданные строки: {counts}");
    using var connection = new SqlConnection(new SqlConnectionStringBuilder { DataSource = server, InitialCatalog = databaseName, IntegratedSecurity = true, Encrypt = SqlConnectionEncryptOption.Optional, TrustServerCertificate = true }.ConnectionString); connection.Open();
    var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    using (var columns = connection.CreateCommand())
    {
        columns.CommandText = "SELECT COLUMN_NAME FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_SCHEMA='dbo' AND TABLE_NAME='Trips';";
        using var reader = columns.ExecuteReader();
        while (reader.Read()) names.Add(reader.GetString(0));
    }
    if (names.Contains("Cities") || !names.Contains("TripId")) throw new Exception("Trips хранит составной список городов или не имеет ключа.");
    using var table = connection.CreateCommand(); table.CommandText = "SELECT COUNT(*) FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_SCHEMA='dbo' AND TABLE_NAME='TripCities';";
    if (Convert.ToInt32(table.ExecuteScalar()) != 1) throw new Exception("Нет нормализованной таблицы TripCities.");
});
Check("валидный адрес регистрируется", () => { AccountSecurity.ValidateEmail("student@example.org"); });
Check("почта без локальной части отклоняется", () => Throws<ArgumentException>(() => AccountSecurity.ValidateEmail("@example.org")));
Check("почта без доменной части отклоняется", () => Throws<ArgumentException>(() => AccountSecurity.ValidateEmail("student@.org")));
Check("двойной @ отклоняется", () => Throws<ArgumentException>(() => AccountSecurity.ValidateEmail("student@@example.org")));
Check("короткий пароль отклоняется", () => Throws<ArgumentException>(() => AccountSecurity.HashPassword("A1b")));
Check("пароль без цифры отклоняется", () => Throws<ArgumentException>(() => AccountSecurity.HashPassword("StrongPassword")));
Check("распространённый пароль отклоняется", () => Throws<ArgumentException>(() => AccountSecurity.HashPassword("Password123")));
Check("шаблон городского багажа рассчитывает комплект по числу дней", () =>
{
    var city = PackingTemplateFactory.Create("city", 5);
    if (city.Count < 10 || city.First(x => x.Name == "Футболка").Quantity != 3 || city.First(x => x.Name == "Носки").Quantity != 5)
        throw new Exception("Число вещей не адаптировано к пятидневной поездке.");
    if (city.Select(x => x.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != city.Count)
        throw new Exception("Шаблон содержит повторяющиеся позиции.");
});
Check("деловой шаблон включает оборудование и документы", () =>
{
    var business = PackingTemplateFactory.Create("business", 3);
    if (!business.Any(x => x.Name == "Ноутбук" && x.Required) || !business.Any(x => x.Name == "Папка с документами"))
        throw new Exception("В комплекте нет делового оборудования или документов.");
});
Check("шаблон активного отдыха включает безопасность и воду", () =>
{
    var active = PackingTemplateFactory.Create("active", 7);
    if (!active.Any(x => x.Name == "Пластырь от мозолей" && x.Required) || !active.Any(x => x.Name == "Бутылка для воды"))
        throw new Exception("В комплекте нет ключевых вещей для похода.");
});
Check("дни шаблона ограничиваются безопасным диапазоном", () =>
{
    var shortTrip = PackingTemplateFactory.Create("city", 0);
    if (shortTrip.Any(x => x.Quantity < 1) || shortTrip.First(x => x.Name == "Футболка").Quantity != 2)
        throw new Exception("Крайнее значение дней не нормализовано.");
});
Check("офлайн-погода явно маркируется и зависит от позиции города", () =>
{
    var firstCity = OfflineWeatherFactory.Create("Казань", 0);
    var secondCity = OfflineWeatherFactory.Create("Пермь", 1);
    if (!firstCity.Offline || !secondCity.Offline || firstCity.City != "Казань" || secondCity.City != "Пермь")
        throw new Exception("Резервная погода не помечена либо потеряла город маршрута.");
    if (firstCity.Min == secondCity.Min || firstCity.Max == secondCity.Max)
        throw new Exception("Резервная оценка не различается для последовательных точек маршрута.");
    Throws<ArgumentException>(() => OfflineWeatherFactory.Create(" ", 0));
});
Check("ошибка одного прогноза не сбрасывает успешные города маршрута", () =>
{
    var route = WeatherRouteService.RefreshAsync(
        ["Казань", "Несуществующий город"],
        city => city == "Казань"
            ? Task.FromResult(new WeatherCard { City = city, Min = 7, Max = 16, Rain = 10, Wind = 8 })
            : Task.FromException<WeatherCard>(new InvalidOperationException("not found")))
        .GetAwaiter().GetResult();

    if (route.Cards.Count != 2 || route.OnlineCount != 1 || route.OfflineCount != 1)
        throw new Exception("Ожидалась одна онлайн-карточка и одна резервная.");
    if (route.Cards[0].City != "Казань" || route.Cards[0].Offline || route.Cards[1].City != "Несуществующий город" || !route.Cards[1].Offline)
        throw new Exception("Порядок маршрута или метки источника погоды потеряны.");
});
Check("WeatherAPI разбирает недельный прогноз и передаёт API-ключ в запросе", () =>
{
    const string key = "test-key-0123456789";
    var json = """
        {"location":{"name":"Казань"},"forecast":{"forecastday":[
          {"day":{"mintemp_c":2.2,"maxtemp_c":12.4,"daily_chance_of_rain":25,"maxwind_kph":16.3,"condition":{"code":1000}}},
          {"day":{"mintemp_c":-1.1,"maxtemp_c":9.6,"daily_chance_of_rain":70,"maxwind_kph":22.2,"condition":{"code":1003}}}
        ]}}
        """;
    using var http = new HttpClient(new FakeWeatherApiHandler(request =>
    {
        var uri = request.RequestUri ?? throw new Exception("Нет URI запроса.");
        if (uri.Host != "api.weatherapi.com" || !uri.AbsolutePath.EndsWith("/forecast.json", StringComparison.Ordinal) || !uri.Query.Contains(Uri.EscapeDataString(key), StringComparison.Ordinal) || !uri.Query.Contains("q=%D0%9A%D0%B0%D0%B7%D0%B0%D0%BD%D1%8C", StringComparison.Ordinal))
            throw new Exception("Параметры WeatherAPI сформированы неверно.");
        return new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent(json) };
    }));
    var forecast = new WeatherApiClient(http).GetForecastAsync("Казань", key).GetAwaiter().GetResult();
    if (forecast.City != "Казань" || forecast.Min != -1 || forecast.Max != 12 || forecast.Rain != 70 || forecast.Wind != 22 || forecast.Offline)
        throw new Exception("Агрегированные значения недельного прогноза рассчитаны неверно.");
});
AppUser first = new(0, "", "");
Check("профиль регистрируется и получает идентификатор", () => { first = db.Register("First@example.org", "Первый профиль", "StrongPass2026!"); if (first.Id < 1) throw new Exception("Идентификатор не назначен."); });
Check("почта сравнивается без учёта регистра", () => { var session = db.SignIn("FIRST@EXAMPLE.ORG", "StrongPass2026!"); if (session.Id != first.Id) throw new Exception("Неверный профиль."); });
Check("неверный пароль отклоняется", () => Throws<InvalidOperationException>(() => db.SignIn("first@example.org", "WrongPass2026!")));
Check("повторная почта не допускается", () => Throws<InvalidOperationException>(() => db.Register("FIRST@example.org", "Дубликат", "SecondPass2026!")));
AppUser second = new(0, "", "");
Check("второй профиль создаётся отдельно", () => { second = db.Register("second@example.org", "Второй профиль", "AnotherPass2026!"); });
Check("код восстановления передаётся в Mail API с назначением и авторизацией", () =>
{
    const string apiToken = "test-mail-token-0123456789-abcdef";
    var requests = 0;
    using var http = new HttpClient(new FakeWeatherApiHandler(request =>
    {
        requests++;
        var json = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
        if (request.RequestUri?.AbsolutePath != "/api/mail/account-code" ||
            request.Headers.Authorization?.Scheme != "Bearer" || request.Headers.Authorization.Parameter != apiToken ||
            !json.Contains("\"appName\":\"Travel Ready\"", StringComparison.Ordinal) ||
            !json.Contains("\"purpose\":\"password-reset\"", StringComparison.Ordinal) ||
            !json.Contains("\"code\":\"ABCDEFGHJKLMNPQR\"", StringComparison.Ordinal))
            throw new Exception("Запрос восстановления сформирован неверно.");
        return new HttpResponseMessage(System.Net.HttpStatusCode.OK);
    }));
    new MailApiClient(new MailApiSettings("http://127.0.0.1:5080", apiToken), http)
        .SendPasswordResetCodeAsync("first@example.org", "ABCDEFGHJKLMNPQR").GetAwaiter().GetResult();
    if (requests != 1) throw new Exception("Письмо не было отправлено ровно один раз.");
});
Check("устаревший Mail API не выдаёт ложный успех для восстановления", () =>
{
    var requests = 0;
    using var http = new HttpClient(new FakeWeatherApiHandler(_ => { requests++; return new HttpResponseMessage(System.Net.HttpStatusCode.NotFound); }));
    Throws<InvalidOperationException>(() => new MailApiClient(new MailApiSettings("http://127.0.0.1:5080", "test-mail-token-0123456789-abcdef"), http)
        .SendPasswordResetCodeAsync("first@example.org", "ABCDEFGHJKLMNPQR").GetAwaiter().GetResult());
    if (requests != 1) throw new Exception("Код ошибочно отправлен через несовместимый legacy endpoint.");
});
Check("несуществующий профиль не получает код восстановления", () =>
{
    if (db.BeginPasswordReset("absent@example.org") is not null) throw new Exception("Создан код для несуществующего профиля.");
});
PasswordResetRequest? firstReset = null;
Check("код восстановления случаен и в базе хранится только его хеш", () =>
{
    firstReset = db.BeginPasswordReset("FIRST@EXAMPLE.ORG") ?? throw new Exception("Код не создан.");
    if (firstReset.Email != first.Email || firstReset.Code.Length != PasswordResetCode.Length ||
        firstReset.Code.Any(character => !char.IsAsciiLetterUpper(character) && !char.IsAsciiDigit(character)))
        throw new Exception("Формат кода или адрес получателя неверен.");
    using var connection = new SqlConnection(new SqlConnectionStringBuilder { DataSource = server, InitialCatalog = databaseName, IntegratedSecurity = true, Encrypt = SqlConnectionEncryptOption.Optional, TrustServerCertificate = true }.ConnectionString);
    connection.Open(); using var command = connection.CreateCommand();
    command.CommandText = "SELECT CodeHash,DeliveredUtc FROM dbo.PasswordResetChallenges WHERE UserId=@user;";
    command.Parameters.AddWithValue("@user", first.Id);
    using var reader = command.ExecuteReader();
    if (!reader.Read() || ((byte[])reader[0]).Length != 32 || !reader.IsDBNull(1) ||
        System.Text.Encoding.ASCII.GetString((byte[])reader[0]) == firstReset.Code)
        throw new Exception("Код хранится в открытом виде или преждевременно активирован.");
});
Check("без подтверждения отправки код нельзя применить", () =>
    Throws<InvalidOperationException>(() => db.CompletePasswordReset(first.Email, firstReset!.Code, "Recovered2026!")));
Check("повторный запрос в течение минуты ограничен", () =>
{
    if (db.BeginPasswordReset(first.Email) is not null) throw new Exception("Выдан второй код сразу после первого.");
});
Check("доставленный код меняет пароль ровно один раз", () =>
{
    db.MarkPasswordResetDelivered(firstReset!);
    Throws<InvalidOperationException>(() => db.CompletePasswordReset(first.Email, "AAAAAAAAAAAAAAAA", "Recovered2026!"));
    db.CompletePasswordReset(first.Email, firstReset!.Code.ToLowerInvariant(), "Recovered2026!");
    Throws<InvalidOperationException>(() => db.SignIn(first.Email, "StrongPass2026!"));
    if (db.SignIn(first.Email, "Recovered2026!").Id != first.Id) throw new Exception("Новый пароль не работает.");
    Throws<InvalidOperationException>(() => db.CompletePasswordReset(first.Email, firstReset!.Code, "AnotherPass2026!"));
});
Check("истёкший код и пять неверных попыток блокируют восстановление", () =>
{
    var expired = db.BeginPasswordReset(second.Email) ?? throw new Exception("Код не создан.");
    db.MarkPasswordResetDelivered(expired);
    using (var connection = new SqlConnection(new SqlConnectionStringBuilder { DataSource = server, InitialCatalog = databaseName, IntegratedSecurity = true, Encrypt = SqlConnectionEncryptOption.Optional, TrustServerCertificate = true }.ConnectionString))
    {
        connection.Open(); using var command = connection.CreateCommand();
        command.CommandText = "UPDATE dbo.PasswordResetChallenges SET ExpiresUtc=DATEADD(MINUTE,-1,SYSUTCDATETIME()),IssuedUtc=DATEADD(MINUTE,-2,SYSUTCDATETIME()) WHERE UserId=@user;";
        command.Parameters.AddWithValue("@user", second.Id); command.ExecuteNonQuery();
    }
    Throws<InvalidOperationException>(() => db.CompletePasswordReset(second.Email, expired.Code, "Recovered2026!"));
    var limited = db.BeginPasswordReset(second.Email) ?? throw new Exception("После истечения код не перевыпущен.");
    db.MarkPasswordResetDelivered(limited);
    for (var attempt = 0; attempt < 5; attempt++)
        Throws<InvalidOperationException>(() => db.CompletePasswordReset(second.Email, "AAAAAAAAAAAAAAAA", "Recovered2026!"));
    Throws<InvalidOperationException>(() => db.CompletePasswordReset(second.Email, limited.Code, "Recovered2026!"));
    if (db.SignIn(second.Email, "AnotherPass2026!").Id != second.Id) throw new Exception("Исходный пароль изменился после неуспешных попыток.");
});
Check("неотправленный код отменяется и не мешает новой попытке", () =>
{
    using (var connection = new SqlConnection(new SqlConnectionStringBuilder { DataSource = server, InitialCatalog = databaseName, IntegratedSecurity = true, Encrypt = SqlConnectionEncryptOption.Optional, TrustServerCertificate = true }.ConnectionString))
    {
        connection.Open(); using var command = connection.CreateCommand();
        command.CommandText = "UPDATE dbo.PasswordResetChallenges SET IssuedUtc=DATEADD(MINUTE,-2,SYSUTCDATETIME()) WHERE UserId=@user;";
        command.Parameters.AddWithValue("@user", second.Id); command.ExecuteNonQuery();
    }
    var undelivered = db.BeginPasswordReset(second.Email) ?? throw new Exception("Код не создан.");
    db.CancelPasswordReset(undelivered);
    Throws<InvalidOperationException>(() => db.CompletePasswordReset(second.Email, undelivered.Code, "Recovered2026!"));
    var replacement = db.BeginPasswordReset(second.Email) ?? throw new Exception("Отмена заблокировала повторную отправку.");
    if (replacement.Code == undelivered.Code) throw new Exception("Повторный код совпал с отменённым.");
    db.MarkPasswordResetDelivered(replacement);
    db.CompletePasswordReset(second.Email, replacement.Code, "Recovered2026!");
    if (db.SignIn(second.Email, "Recovered2026!").Id != second.Id) throw new Exception("Повторный код не изменил пароль.");
});

var project = new PackingProject
{
    Name = "Полевой выезд",
    Cities = "Казань, Йошкар-Ола",
    Days = 5,
    TripType = "активный отдых",
    WeightLimit = 12.5,
    Items = [
        new PackingEntry { Name = "Аптечка", Category = "Здоровье", Person = "Профиль A", Quantity = 2, UnitWeight = .35, Required = true, Status = "Уложено" },
        new PackingEntry { Name = "Спутниковый маяк", Category = "Навигация", Person = "Профиль B", Quantity = 1, UnitWeight = .42, Required = false, Status = "Проверить" }
    ],
    Weather = [new WeatherCard { City = "Казань", Min = 4, Max = 13, Rain = 60, Wind = 18, Icon = "☂", Offline = true }]
};
Check("поездка с неизвестной категорией и статусом сохраняется атомарно", () => db.SaveTrip(first.Id, project));
Check("поездка восстанавливает поля, вещи, пассажиров и погоду", () =>
{
    var loaded = db.LoadLatestTrip(first.Id) ?? throw new Exception("Проект не найден.");
    if (loaded.Name != project.Name || loaded.Cities != project.Cities || loaded.Days != project.Days || loaded.TripType != project.TripType || Math.Abs(loaded.WeightLimit - project.WeightLimit) > .001) throw new Exception("Поля проекта изменились.");
    if (loaded.Items.Count != 2 || loaded.Items[0].Person != "Профиль A" || loaded.Items[1].Category != "Навигация" || loaded.Items[1].Status != "Проверить") throw new Exception("Позиции не восстановлены.");
    if (loaded.Weather.Count != 1 || loaded.Weather[0].Rain != 60 || !loaded.Weather[0].Offline) throw new Exception("Погода не восстановлена.");
});
Check("пользователь не видит поездки другого профиля", () => { if (db.LoadLatestTrip(second.Id) is not null) throw new Exception("Обнаружена чужая поездка."); });
Check("повторное сохранение обновляет поездку вместо дубликата", () =>
{
    project.Name = "Полевой выезд — обновлён"; project.Items[0].Quantity = 4; db.SaveTrip(first.Id, project);
    var counts = db.CountRows(); var loaded = db.LoadLatestTrip(first.Id)!;
    if (counts.Trips != 1 || loaded.Name != project.Name || loaded.Items[0].Quantity != 4) throw new Exception("Upsert не сохранил обновление.");
});
Check("раздельные таблицы 3НФ содержат независимые справочники", () =>
{
    var counts = db.CountRows(); if (counts.Users != 2 || counts.Trips != 1 || counts.Items != 2) throw new Exception($"Проверка сущностей не пройдена: {counts}");
    using var connection = new SqlConnection(new SqlConnectionStringBuilder { DataSource = server, InitialCatalog = databaseName, IntegratedSecurity = true, Encrypt = SqlConnectionEncryptOption.Optional, TrustServerCertificate = true }.ConnectionString); connection.Open();
    using var command = connection.CreateCommand(); command.CommandText = "SELECT Position,City FROM dbo.TripCities WHERE TripId=(SELECT TripId FROM dbo.Trips WHERE UserId=@user) ORDER BY Position;"; command.Parameters.AddWithValue("@user", first.Id);
    using var reader = command.ExecuteReader();
    if (!reader.Read() || reader.GetInt32(0) != 0 || reader.GetString(1) != "Казань" || !reader.Read() || reader.GetInt32(0) != 1 || reader.GetString(1) != "Йошкар-Ола" || reader.Read())
        throw new Exception("Города не сохранены атомарно в правильном порядке.");
});
Check("вещь не может ссылаться на пассажира другой поездки", () =>
{
    using var connection = new SqlConnection(new SqlConnectionStringBuilder { DataSource = server, InitialCatalog = databaseName, IntegratedSecurity = true, Encrypt = SqlConnectionEncryptOption.Optional, TrustServerCertificate = true }.ConnectionString);
    connection.Open();
    using var transaction = connection.BeginTransaction();
    try
    {
        using var createTrip = new SqlCommand("INSERT dbo.Trips(UserId,Title,Days,TripTypeId,WeightLimit,UpdatedUtc) OUTPUT INSERTED.TripId SELECT @user,N'Проверка связи',1,MIN(TripTypeId),1,SYSUTCDATETIME() FROM dbo.TripTypes;", connection, transaction);
        createTrip.Parameters.AddWithValue("@user", second.Id);
        var otherTripId = Convert.ToInt64(createTrip.ExecuteScalar());
        using var crossTripItem = new SqlCommand("INSERT dbo.PackingItems(TripId,Name,CategoryId,TravelerId,Quantity,UnitWeight,IsRequired,StatusId) SELECT @trip,N'Чужой пассажир',MIN(c.CategoryId),(SELECT TOP(1) TravelerId FROM dbo.Travelers WHERE TripId IN (SELECT TripId FROM dbo.Trips WHERE UserId=@owner)),1,0,0,MIN(s.StatusId) FROM dbo.ItemCategories c CROSS JOIN dbo.ItemStatuses s;", connection, transaction);
        crossTripItem.Parameters.AddWithValue("@trip", otherTripId);
        crossTripItem.Parameters.AddWithValue("@owner", first.Id);
        try { crossTripItem.ExecuteNonQuery(); throw new Exception("Связь с пассажиром другой поездки была принята."); }
        catch (SqlException ex) when (ex.Number == 547) { }
    }
    finally { transaction.Rollback(); }
});
Check("старая база переносит города из поля Trips в нормализованную таблицу", () =>
{
    var oldDbPath = Path.Combine(root, "legacy.db");
    using (var legacy = new SqliteConnection($"Data Source={oldDbPath};Foreign Keys=True"))
    {
        legacy.Open(); using var command = legacy.CreateCommand();
        command.CommandText = """
            CREATE TABLE AppUsers(UserId INTEGER PRIMARY KEY, Email TEXT NOT NULL, EmailNormalized TEXT NOT NULL UNIQUE, DisplayName TEXT NOT NULL, PasswordSalt BLOB NOT NULL, PasswordHash BLOB NOT NULL, PasswordIterations INTEGER NOT NULL, CreatedUtc TEXT NOT NULL);
            INSERT INTO AppUsers VALUES(71,'legacy@example.org','LEGACY@EXAMPLE.ORG','Старый профиль',X'01',X'02',240000,'2026-01-01T00:00:00Z');
            CREATE TABLE TripTypes(TripTypeId INTEGER PRIMARY KEY, Name TEXT NOT NULL UNIQUE);
            INSERT INTO TripTypes VALUES(1,'городская');
            CREATE TABLE Trips(TripId INTEGER PRIMARY KEY, UserId INTEGER NOT NULL REFERENCES AppUsers(UserId) ON DELETE CASCADE, Title TEXT NOT NULL, Cities TEXT NOT NULL, Days INTEGER NOT NULL, TripTypeId INTEGER NOT NULL REFERENCES TripTypes(TripTypeId), WeightLimit REAL NOT NULL, UpdatedUtc TEXT NOT NULL);
            INSERT INTO Trips VALUES(91,71,'Старая поездка','Казань, Йошкар-Ола',3,1,8,'2026-01-02T00:00:00Z');
            """;
        command.ExecuteNonQuery();
    }
    var migratedName = "TravelReadyMigrationTest_" + Guid.NewGuid().ToString("N");
    testCatalogs.Add(migratedName);
    var migratedDatabase = new TravelDatabase(oldDbPath, server, migratedName);
    var restored = migratedDatabase.LoadLatestTrip(71) ?? throw new Exception("Старая поездка потеряна.");
    if (restored.Cities != "Казань, Йошкар-Ола") throw new Exception("Порядок городов старой поездки не сохранён.");
    using var check = new SqliteConnection($"Data Source={oldDbPath}"); check.Open();
    using var pragma = check.CreateCommand(); pragma.CommandText = "PRAGMA table_info(Trips);";
    using var columns = pragma.ExecuteReader(); var preserved = false; while (columns.Read()) if (columns.GetString(1) == "Cities") preserved = true;
    if (!preserved) throw new Exception("Исходная SQLite-база должна оставаться нетронутой для отката.");
});
Check("реальная пользовательская SQLite-база переносится в отдельную SQL Server-копию без потери основных строк", () =>
{
    var realPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TravelReady", "travelready.db");
    if (!File.Exists(realPath)) throw new Exception("Исходная пользовательская SQLite-база не найдена.");
    var snapshot = Path.Combine(root, "real-user-snapshot.db"); File.Copy(realPath, snapshot);
    foreach (var suffix in new[] { "-wal", "-shm" }) if (File.Exists(realPath + suffix)) File.Copy(realPath + suffix, snapshot + suffix);
    int users, trips, items;
    using (var legacy = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource=snapshot, Mode=SqliteOpenMode.ReadOnly }.ToString()))
    {
        legacy.Open();
        int Count(string table) { using var count=legacy.CreateCommand(); count.CommandText=$"SELECT COUNT(*) FROM [{table}];"; return Convert.ToInt32(count.ExecuteScalar()); }
        users=Count("AppUsers"); trips=Count("Trips"); items=Count("PackingItems");
    }
    var migratedName="TravelReadySnapshotTest_"+Guid.NewGuid().ToString("N"); testCatalogs.Add(migratedName);
    var migrated=new TravelDatabase(snapshot,server,migratedName); var actual=migrated.CountRows();
    if(actual.Users!=users||actual.Trips!=trips||actual.Items!=items) throw new Exception($"Ожидали users/trips/items {users}/{trips}/{items}, получено {actual.Users}/{actual.Trips}/{actual.Items}.");
});
Check("SQL Server сообщает об ограничениях внешних ключей без нарушений", () =>
{
    var result = db.VerifyIntegrity(); if (result.Integrity != "ok" || result.ForeignKeyViolations != 0) throw new Exception($"integrity={result.Integrity}; FK={result.ForeignKeyViolations}");
});

Console.WriteLine($"TRAVEL_READY_TESTS: {passed} passed, {failed} failed");
SqliteConnection.ClearAllPools();
SqlConnection.ClearAllPools();
foreach (var catalog in testCatalogs) DropDatabase(server, catalog);
Directory.Delete(root, recursive: true);
return failed == 0 ? 0 : 1;

static void DropDatabase(string serverName, string catalog)
{
    using var connection = new SqlConnection(new SqlConnectionStringBuilder { DataSource = serverName, InitialCatalog = "master", IntegratedSecurity = true, Encrypt = SqlConnectionEncryptOption.Optional, TrustServerCertificate = true }.ConnectionString);
    connection.Open(); using var command = connection.CreateCommand();
    command.CommandText = $"IF DB_ID(N'{catalog}') IS NOT NULL BEGIN ALTER DATABASE [{catalog}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{catalog}]; END";
    command.ExecuteNonQuery();
}

sealed class FakeWeatherApiHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(respond(request));
}
