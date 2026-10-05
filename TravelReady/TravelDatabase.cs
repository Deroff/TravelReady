using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;
using System.Data;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;

namespace TravelReady;

public sealed record AppUser(long Id, string Email, string DisplayName);
public sealed record PasswordResetRequest(long UserId, string Email, string Code);

/// <summary>Основное хранилище Travel Ready в SQL Server LocalDB; при первом старте импортирует прежнюю SQLite-базу.</summary>
public sealed class TravelDatabase
{
    private static readonly Regex SafeDatabaseName = new("^[A-Za-z][A-Za-z0-9_]{0,62}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    public string Server { get; }
    public string DatabaseName { get; }
    public string DatabasePath => $"{Server} / {DatabaseName}";
    public string LegacyDatabasePath { get; }

    public TravelDatabase(string? legacyDatabasePath = null, string? server = null, string? databaseName = null)
    {
        var requestedServer = server ?? Environment.GetEnvironmentVariable("TRAVELREADY_SQL_SERVER") ?? @"(localdb)\MSSQLLocalDB";
        Server = requestedServer;
        DatabaseName = databaseName ?? Environment.GetEnvironmentVariable("TRAVELREADY_DATABASE_NAME") ?? "TravelReadyDb";
        if (!SafeDatabaseName.IsMatch(DatabaseName)) throw new ArgumentException("Недопустимое имя базы данных.", nameof(databaseName));
        LegacyDatabasePath = legacyDatabasePath ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TravelReady", "travelready.db");
        EnsureDatabase();
        Initialize();
        MigrateLegacySqlite();
        EnsureDefaultLookups();
    }

    private SqlConnectionStringBuilder Builder(string catalog) => new()
    {
        DataSource = LocalDbPipeResolver.ResolveServer(Server),
        InitialCatalog = catalog,
        IntegratedSecurity = true,
        Encrypt = SqlConnectionEncryptOption.Optional,
        TrustServerCertificate = true,
        ConnectTimeout = 15,
        ApplicationName = "Travel Ready"
    };

    private SqlConnection Open()
    {
        var connection = new SqlConnection(Builder(DatabaseName).ConnectionString);
        connection.Open();
        return connection;
    }

    private void EnsureDatabase()
    {
        using var connection = new SqlConnection(Builder("master").ConnectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"IF DB_ID(N'{DatabaseName}') IS NULL CREATE DATABASE [{DatabaseName}];";
        command.ExecuteNonQuery();
    }

    private void Initialize()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            IF OBJECT_ID(N'dbo.AppUsers',N'U') IS NULL CREATE TABLE dbo.AppUsers(
                UserId BIGINT IDENTITY(1,1) NOT NULL PRIMARY KEY, Email NVARCHAR(320) NOT NULL,
                EmailNormalized NVARCHAR(320) NOT NULL UNIQUE, DisplayName NVARCHAR(48) NOT NULL,
                PasswordSalt VARBINARY(64) NOT NULL, PasswordHash VARBINARY(64) NOT NULL,
                PasswordIterations INT NOT NULL, CreatedUtc DATETIME2(7) NOT NULL);
            IF OBJECT_ID(N'dbo.TripTypes',N'U') IS NULL CREATE TABLE dbo.TripTypes(TripTypeId INT IDENTITY(1,1) NOT NULL PRIMARY KEY, Name NVARCHAR(80) NOT NULL UNIQUE);
            IF OBJECT_ID(N'dbo.ItemCategories',N'U') IS NULL CREATE TABLE dbo.ItemCategories(CategoryId INT IDENTITY(1,1) NOT NULL PRIMARY KEY, Name NVARCHAR(100) NOT NULL UNIQUE);
            IF OBJECT_ID(N'dbo.ItemStatuses',N'U') IS NULL CREATE TABLE dbo.ItemStatuses(StatusId INT IDENTITY(1,1) NOT NULL PRIMARY KEY, Name NVARCHAR(80) NOT NULL UNIQUE);
            IF OBJECT_ID(N'dbo.Trips',N'U') IS NULL CREATE TABLE dbo.Trips(
                TripId BIGINT IDENTITY(1,1) NOT NULL PRIMARY KEY, UserId BIGINT NOT NULL REFERENCES dbo.AppUsers(UserId) ON DELETE CASCADE,
                Title NVARCHAR(160) NOT NULL, Days INT NOT NULL CHECK(Days BETWEEN 1 AND 365),
                TripTypeId INT NOT NULL REFERENCES dbo.TripTypes(TripTypeId), WeightLimit FLOAT NOT NULL CHECK(WeightLimit>0), UpdatedUtc DATETIME2(7) NOT NULL);
            IF NOT EXISTS(SELECT 1 FROM sys.indexes WHERE object_id=OBJECT_ID(N'dbo.Trips') AND name=N'IX_Trips_UserUpdated') CREATE INDEX IX_Trips_UserUpdated ON dbo.Trips(UserId,UpdatedUtc DESC);
            IF OBJECT_ID(N'dbo.Travelers',N'U') IS NULL CREATE TABLE dbo.Travelers(TravelerId BIGINT IDENTITY(1,1) NOT NULL PRIMARY KEY, TripId BIGINT NOT NULL REFERENCES dbo.Trips(TripId) ON DELETE CASCADE, Name NVARCHAR(80) NOT NULL, CONSTRAINT UQ_Travelers_Trip_Name UNIQUE(TripId,Name));
            IF OBJECT_ID(N'dbo.TripCities',N'U') IS NULL CREATE TABLE dbo.TripCities(TripId BIGINT NOT NULL REFERENCES dbo.Trips(TripId) ON DELETE CASCADE, Position INT NOT NULL CHECK(Position>=0), City NVARCHAR(160) NOT NULL, CONSTRAINT PK_TripCities PRIMARY KEY(TripId,Position), CONSTRAINT UQ_TripCities_Trip_City UNIQUE(TripId,City));
            IF OBJECT_ID(N'dbo.PackingItems',N'U') IS NULL CREATE TABLE dbo.PackingItems(
                ItemId BIGINT IDENTITY(1,1) NOT NULL PRIMARY KEY, TripId BIGINT NOT NULL REFERENCES dbo.Trips(TripId) ON DELETE CASCADE,
                Name NVARCHAR(160) NOT NULL, CategoryId INT NOT NULL REFERENCES dbo.ItemCategories(CategoryId), TravelerId BIGINT NOT NULL REFERENCES dbo.Travelers(TravelerId),
                Quantity INT NOT NULL CHECK(Quantity>0), UnitWeight FLOAT NOT NULL CHECK(UnitWeight>=0), IsRequired BIT NOT NULL,
                StatusId INT NOT NULL REFERENCES dbo.ItemStatuses(StatusId));
            IF NOT EXISTS(SELECT 1 FROM sys.indexes WHERE object_id=OBJECT_ID(N'dbo.PackingItems') AND name=N'IX_PackingItems_Trip') CREATE INDEX IX_PackingItems_Trip ON dbo.PackingItems(TripId);
            IF OBJECT_ID(N'dbo.TripWeather',N'U') IS NULL CREATE TABLE dbo.TripWeather(
                WeatherId BIGINT IDENTITY(1,1) NOT NULL PRIMARY KEY, TripId BIGINT NOT NULL REFERENCES dbo.Trips(TripId) ON DELETE CASCADE,
                City NVARCHAR(160) NOT NULL, MinTemp INT NOT NULL, MaxTemp INT NOT NULL, RainPercent INT NOT NULL,
                WindKmh INT NOT NULL, Icon NVARCHAR(40) NOT NULL, IsOffline BIT NOT NULL, CONSTRAINT UQ_TripWeather_Trip_City UNIQUE(TripId,City));
            IF OBJECT_ID(N'dbo.PasswordResetChallenges',N'U') IS NULL CREATE TABLE dbo.PasswordResetChallenges(
                UserId BIGINT NOT NULL PRIMARY KEY REFERENCES dbo.AppUsers(UserId) ON DELETE CASCADE,
                CodeHash VARBINARY(32) NOT NULL, IssuedUtc DATETIME2(7) NOT NULL,
                ExpiresUtc DATETIME2(7) NOT NULL, DeliveredUtc DATETIME2(7) NULL,
                FailedAttempts INT NOT NULL CHECK(FailedAttempts BETWEEN 0 AND 5));
            IF OBJECT_ID(N'dbo.MigrationHistory',N'U') IS NULL CREATE TABLE dbo.MigrationHistory(MigrationName NVARCHAR(200) NOT NULL PRIMARY KEY, AppliedUtc DATETIME2(7) NOT NULL);
            IF NOT EXISTS(SELECT 1 FROM sys.indexes WHERE object_id=OBJECT_ID(N'dbo.Travelers') AND name=N'UX_Travelers_Id_Trip')
                CREATE UNIQUE INDEX UX_Travelers_Id_Trip ON dbo.Travelers(TravelerId,TripId);
            IF NOT EXISTS(SELECT 1 FROM sys.foreign_keys WHERE parent_object_id=OBJECT_ID(N'dbo.PackingItems') AND name=N'FK_PackingItems_TravelerTrip')
                ALTER TABLE dbo.PackingItems WITH CHECK ADD CONSTRAINT FK_PackingItems_TravelerTrip FOREIGN KEY(TravelerId,TripId) REFERENCES dbo.Travelers(TravelerId,TripId);
            IF NOT EXISTS(SELECT 1 FROM dbo.MigrationHistory WHERE MigrationName=N'schema-traveler-trip-v2')
                INSERT dbo.MigrationHistory(MigrationName,AppliedUtc) VALUES(N'schema-traveler-trip-v2',SYSUTCDATETIME());
            """;
        command.ExecuteNonQuery();
    }

    private void EnsureDefaultLookups()
    {
        using var connection = Open(); using var command = connection.CreateCommand();
        command.CommandText = """
            IF NOT EXISTS(SELECT 1 FROM dbo.TripTypes WHERE Name=N'городская') INSERT dbo.TripTypes(Name) VALUES(N'городская');
            IF NOT EXISTS(SELECT 1 FROM dbo.TripTypes WHERE Name=N'деловая') INSERT dbo.TripTypes(Name) VALUES(N'деловая');
            IF NOT EXISTS(SELECT 1 FROM dbo.TripTypes WHERE Name=N'смешанная') INSERT dbo.TripTypes(Name) VALUES(N'смешанная');
            IF NOT EXISTS(SELECT 1 FROM dbo.TripTypes WHERE Name=N'активный отдых') INSERT dbo.TripTypes(Name) VALUES(N'активный отдых');
            IF NOT EXISTS(SELECT 1 FROM dbo.ItemCategories WHERE Name=N'Документы') INSERT dbo.ItemCategories(Name) VALUES(N'Документы');
            IF NOT EXISTS(SELECT 1 FROM dbo.ItemCategories WHERE Name=N'Одежда') INSERT dbo.ItemCategories(Name) VALUES(N'Одежда');
            IF NOT EXISTS(SELECT 1 FROM dbo.ItemCategories WHERE Name=N'Техника') INSERT dbo.ItemCategories(Name) VALUES(N'Техника');
            IF NOT EXISTS(SELECT 1 FROM dbo.ItemCategories WHERE Name=N'Здоровье') INSERT dbo.ItemCategories(Name) VALUES(N'Здоровье');
            IF NOT EXISTS(SELECT 1 FROM dbo.ItemCategories WHERE Name=N'По погоде') INSERT dbo.ItemCategories(Name) VALUES(N'По погоде');
            IF NOT EXISTS(SELECT 1 FROM dbo.ItemStatuses WHERE Name=N'Не готово') INSERT dbo.ItemStatuses(Name) VALUES(N'Не готово');
            IF NOT EXISTS(SELECT 1 FROM dbo.ItemStatuses WHERE Name=N'Уложено') INSERT dbo.ItemStatuses(Name) VALUES(N'Уложено');
            IF NOT EXISTS(SELECT 1 FROM dbo.ItemStatuses WHERE Name=N'Купить') INSERT dbo.ItemStatuses(Name) VALUES(N'Купить');
            IF NOT EXISTS(SELECT 1 FROM dbo.ItemStatuses WHERE Name=N'Постирать') INSERT dbo.ItemStatuses(Name) VALUES(N'Постирать');
            """;
        command.ExecuteNonQuery();
    }

    private void MigrateLegacySqlite()
    {
        if (!File.Exists(LegacyDatabasePath)) return;
        using var sql = Open();
        using (var check = sql.CreateCommand())
        {
            check.CommandText = "IF EXISTS(SELECT 1 FROM dbo.MigrationHistory WHERE MigrationName=N'legacy-sqlite-v1') SELECT 1 ELSE SELECT 0;";
            if (Convert.ToInt32(check.ExecuteScalar(), CultureInfo.InvariantCulture) == 1) return;
            check.CommandText = "SELECT COUNT(*) FROM dbo.AppUsers;";
            if (Convert.ToInt32(check.ExecuteScalar(), CultureInfo.InvariantCulture) != 0) return;
        }

        using var legacy = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = LegacyDatabasePath, Mode = SqliteOpenMode.ReadOnly }.ToString());
        legacy.Open();
        if (!SqliteTableExists(legacy, "AppUsers")) return;
        using var transaction = sql.BeginTransaction();
        try
        {
            CopyTable(legacy, sql, transaction, "AppUsers", "UserId,Email,EmailNormalized,DisplayName,PasswordSalt,PasswordHash,PasswordIterations,CreatedUtc", "UserId", "CreatedUtc");
            CopyTable(legacy, sql, transaction, "TripTypes", "TripTypeId,Name", "TripTypeId");
            CopyTable(legacy, sql, transaction, "ItemCategories", "CategoryId,Name", "CategoryId");
            CopyTable(legacy, sql, transaction, "ItemStatuses", "StatusId,Name", "StatusId");
            CopyTable(legacy, sql, transaction, "Trips", "TripId,UserId,Title,Days,TripTypeId,WeightLimit,UpdatedUtc", "TripId", "UpdatedUtc");
            CopyTable(legacy, sql, transaction, "Travelers", "TravelerId,TripId,Name", "TravelerId");
            if (SqliteTableExists(legacy, "TripCities"))
                CopyTable(legacy, sql, transaction, "TripCities", "TripId,Position,City", null);
            else
                CopyLegacyCities(legacy, sql, transaction);
            CopyTable(legacy, sql, transaction, "PackingItems", "ItemId,TripId,Name,CategoryId,TravelerId,Quantity,UnitWeight,IsRequired,StatusId", "ItemId");
            CopyTable(legacy, sql, transaction, "TripWeather", "WeatherId,TripId,City,MinTemp,MaxTemp,RainPercent,WindKmh,Icon,IsOffline", "WeatherId");
            using var marker = sql.CreateCommand(); marker.Transaction = transaction;
            marker.CommandText = "INSERT dbo.MigrationHistory(MigrationName,AppliedUtc) VALUES(N'legacy-sqlite-v1',SYSUTCDATETIME());";
            marker.ExecuteNonQuery();
            transaction.Commit();
        }
        catch { transaction.Rollback(); throw; }
    }

    private static bool SqliteTableExists(SqliteConnection connection, string name)
    {
        using var command = connection.CreateCommand(); command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name=$name;";
        command.Parameters.AddWithValue("$name", name); return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture) > 0;
    }

    private static void CopyTable(SqliteConnection source, SqlConnection target, SqlTransaction transaction, string table, string columns, string? identityColumn, string? dateColumns = null)
    {
        if (!SqliteTableExists(source, table)) return;
        var names = columns.Split(',');
        using var read = source.CreateCommand(); read.CommandText = $"SELECT {columns} FROM [{table}];";
        using var reader = read.ExecuteReader();
        if (identityColumn is not null) ExecuteSql(target, transaction, $"SET IDENTITY_INSERT dbo.[{table}] ON;");
        try
        {
            while (reader.Read())
            {
                using var insert = target.CreateCommand(); insert.Transaction = transaction;
                insert.CommandText = $"INSERT dbo.[{table}]({string.Join(',', names.Select(n => $"[{n}]"))}) VALUES({string.Join(',', names.Select((_, i) => $"@p{i}"))});";
                for (var i = 0; i < names.Length; i++)
                {
                    object value = reader.IsDBNull(i) ? DBNull.Value : reader.GetValue(i);
                    if (dateColumns?.Split(',').Contains(names[i], StringComparer.OrdinalIgnoreCase) == true && value is string dateText)
                        value = DateTime.Parse(dateText, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
                    if (value is long number && (names[i].Equals("IsRequired", StringComparison.OrdinalIgnoreCase) || names[i].Equals("IsOffline", StringComparison.OrdinalIgnoreCase))) value = number != 0;
                    insert.Parameters.AddWithValue($"@p{i}", value);
                }
                insert.ExecuteNonQuery();
            }
        }
        finally { if (identityColumn is not null) ExecuteSql(target, transaction, $"SET IDENTITY_INSERT dbo.[{table}] OFF;"); }
    }

    private static void CopyLegacyCities(SqliteConnection source, SqlConnection target, SqlTransaction transaction)
    {
        if (!SqliteTableExists(source, "Trips")) return;
        using var info = source.CreateCommand(); info.CommandText = "PRAGMA table_info(Trips);";
        using var columns = info.ExecuteReader(); bool hasCities = false;
        while (columns.Read()) if (columns.GetString(1).Equals("Cities", StringComparison.OrdinalIgnoreCase)) hasCities = true;
        columns.Close(); if (!hasCities) return;
        using var read = source.CreateCommand(); read.CommandText = "SELECT TripId,Cities FROM Trips ORDER BY TripId;";
        using var rows = read.ExecuteReader();
        while (rows.Read())
        {
            var tripId = rows.GetInt64(0); var cities = rows.IsDBNull(1) ? "" : rows.GetString(1);
            var parts = cities.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            for (var i = 0; i < parts.Count; i++)
            {
                using var insert = target.CreateCommand(); insert.Transaction = transaction;
                insert.CommandText = "IF NOT EXISTS(SELECT 1 FROM dbo.TripCities WHERE TripId=@trip AND City=@city) INSERT dbo.TripCities(TripId,Position,City) VALUES(@trip,@position,@city);";
                insert.Parameters.AddWithValue("@trip", tripId); insert.Parameters.AddWithValue("@position", i); insert.Parameters.AddWithValue("@city", parts[i]); insert.ExecuteNonQuery();
            }
        }
    }

    private static void ExecuteSql(SqlConnection connection, SqlTransaction transaction, string text)
    { using var command = connection.CreateCommand(); command.Transaction = transaction; command.CommandText = text; command.ExecuteNonQuery(); }

    public AppUser Register(string email, string displayName, string password)
    {
        AccountSecurity.ValidateEmail(email);
        if (string.IsNullOrWhiteSpace(displayName) || displayName.Trim().Length > 48) throw new ArgumentException("Укажите имя профиля длиной до 48 символов.");
        var (salt, hash) = AccountSecurity.HashPassword(password);
        using var connection = Open(); using var cmd = connection.CreateCommand();
        cmd.CommandText = "INSERT dbo.AppUsers(Email,EmailNormalized,DisplayName,PasswordSalt,PasswordHash,PasswordIterations,CreatedUtc) OUTPUT INSERTED.UserId VALUES(@email,@normalized,@name,@salt,@hash,@iterations,SYSUTCDATETIME());";
        cmd.Parameters.AddWithValue("@email", email.Trim()); cmd.Parameters.AddWithValue("@normalized", AccountSecurity.NormalizeEmail(email)); cmd.Parameters.AddWithValue("@name", displayName.Trim());
        cmd.Parameters.AddWithValue("@salt", salt); cmd.Parameters.AddWithValue("@hash", hash); cmd.Parameters.AddWithValue("@iterations", AccountSecurity.Iterations);
        try { var id = Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture); return new AppUser(id, email.Trim(), displayName.Trim()); }
        catch (SqlException ex) when (ex.Number is 2601 or 2627) { throw new InvalidOperationException("Пользователь с такой почтой уже зарегистрирован."); }
    }

    public AppUser SignIn(string email, string password)
    {
        AccountSecurity.ValidateEmail(email); using var connection = Open(); using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT UserId,Email,DisplayName,PasswordSalt,PasswordHash,PasswordIterations FROM dbo.AppUsers WHERE EmailNormalized=@email;";
        cmd.Parameters.AddWithValue("@email", AccountSecurity.NormalizeEmail(email)); using var reader = cmd.ExecuteReader();
        if (!reader.Read() || !AccountSecurity.VerifyPassword(password, (byte[])reader[3], (byte[])reader[4], reader.GetInt32(5))) throw new InvalidOperationException("Почта или пароль указаны неверно.");
        return new AppUser(reader.GetInt64(0), reader.GetString(1), reader.GetString(2));
    }

    public PasswordResetRequest? BeginPasswordReset(string email)
    {
        AccountSecurity.ValidateEmail(email);
        using var connection = Open();
        using var transaction = connection.BeginTransaction(IsolationLevel.Serializable);
        long userId;
        string storedEmail;
        using (var user = connection.CreateCommand())
        {
            user.Transaction = transaction;
            user.CommandText = "SELECT UserId,Email FROM dbo.AppUsers WITH (UPDLOCK,HOLDLOCK) WHERE EmailNormalized=@email;";
            user.Parameters.AddWithValue("@email", AccountSecurity.NormalizeEmail(email));
            using var reader = user.ExecuteReader();
            if (!reader.Read()) return null;
            userId = reader.GetInt64(0);
            storedEmail = reader.GetString(1);
        }
        var now = DateTime.UtcNow;
        using (var previous = connection.CreateCommand())
        {
            previous.Transaction = transaction;
            previous.CommandText = "SELECT IssuedUtc FROM dbo.PasswordResetChallenges WHERE UserId=@user;";
            previous.Parameters.AddWithValue("@user", userId);
            var issued = previous.ExecuteScalar();
            if (issued is DateTime lastIssued && lastIssued.AddMinutes(1) > now) return null;
        }
        var code = PasswordResetCode.Create();
        PasswordResetCode.TryHash(code, out var hash);
        using (var save = connection.CreateCommand())
        {
            save.Transaction = transaction;
            save.CommandText = """
                IF EXISTS(SELECT 1 FROM dbo.PasswordResetChallenges WHERE UserId=@user)
                    UPDATE dbo.PasswordResetChallenges SET CodeHash=@hash,IssuedUtc=@issued,ExpiresUtc=@expires,DeliveredUtc=NULL,FailedAttempts=0 WHERE UserId=@user;
                ELSE
                    INSERT dbo.PasswordResetChallenges(UserId,CodeHash,IssuedUtc,ExpiresUtc,DeliveredUtc,FailedAttempts) VALUES(@user,@hash,@issued,@expires,NULL,0);
                """;
            save.Parameters.AddWithValue("@user", userId);
            save.Parameters.AddWithValue("@hash", hash);
            save.Parameters.AddWithValue("@issued", now);
            save.Parameters.AddWithValue("@expires", now.AddMinutes(10));
            save.ExecuteNonQuery();
        }
        transaction.Commit();
        return new PasswordResetRequest(userId, storedEmail, code);
    }

    public void MarkPasswordResetDelivered(PasswordResetRequest request)
    {
        PasswordResetCode.TryHash(request.Code, out var hash);
        using var connection = Open(); using var command = connection.CreateCommand();
        command.CommandText = "UPDATE dbo.PasswordResetChallenges SET DeliveredUtc=SYSUTCDATETIME() WHERE UserId=@user AND CodeHash=@hash AND DeliveredUtc IS NULL AND ExpiresUtc>SYSUTCDATETIME();";
        command.Parameters.AddWithValue("@user", request.UserId);
        command.Parameters.AddWithValue("@hash", hash);
        if (command.ExecuteNonQuery() != 1) throw new InvalidOperationException("Код восстановления больше не действует. Запросите новый.");
    }

    public void CancelPasswordReset(PasswordResetRequest request)
    {
        PasswordResetCode.TryHash(request.Code, out var hash);
        using var connection = Open(); using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM dbo.PasswordResetChallenges WHERE UserId=@user AND CodeHash=@hash AND DeliveredUtc IS NULL;";
        command.Parameters.AddWithValue("@user", request.UserId);
        command.Parameters.AddWithValue("@hash", hash);
        command.ExecuteNonQuery();
    }

    public void CompletePasswordReset(string email, string code, string newPassword)
    {
        AccountSecurity.ValidateEmail(email);
        var (salt, passwordHash) = AccountSecurity.HashPassword(newPassword);
        var validFormat = PasswordResetCode.TryHash(code, out var codeHash);
        using var connection = Open();
        using var transaction = connection.BeginTransaction(IsolationLevel.Serializable);
        long userId;
        bool valid;
        using (var challenge = connection.CreateCommand())
        {
            challenge.Transaction = transaction;
            challenge.CommandText = """
                SELECT r.UserId,r.CodeHash,r.ExpiresUtc,r.DeliveredUtc,r.FailedAttempts
                FROM dbo.PasswordResetChallenges r WITH (UPDLOCK,HOLDLOCK)
                JOIN dbo.AppUsers u ON u.UserId=r.UserId WHERE u.EmailNormalized=@email;
                """;
            challenge.Parameters.AddWithValue("@email", AccountSecurity.NormalizeEmail(email));
            using var reader = challenge.ExecuteReader();
            if (!reader.Read()) throw new InvalidOperationException("Код восстановления неверен или истёк. Запросите новый код.");
            userId = reader.GetInt64(0);
            var expected = (byte[])reader[1];
            var expires = reader.GetDateTime(2);
            valid = validFormat && !reader.IsDBNull(3) && reader.GetInt32(4) < 5 &&
                    expires > DateTime.UtcNow && System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(expected, codeHash);
            if (!valid && (reader.IsDBNull(3) || reader.GetInt32(4) >= 5 || expires <= DateTime.UtcNow))
                throw new InvalidOperationException("Код восстановления неверен или истёк. Запросите новый код.");
        }
        if (!valid)
        {
            using var failed = connection.CreateCommand(); failed.Transaction = transaction;
            failed.CommandText = "UPDATE dbo.PasswordResetChallenges SET FailedAttempts=FailedAttempts+1 WHERE UserId=@user AND FailedAttempts<5;";
            failed.Parameters.AddWithValue("@user", userId); failed.ExecuteNonQuery();
            transaction.Commit();
            throw new InvalidOperationException("Код восстановления неверен или истёк. Запросите новый код.");
        }
        using (var update = connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText = "UPDATE dbo.AppUsers SET PasswordSalt=@salt,PasswordHash=@hash,PasswordIterations=@iterations WHERE UserId=@user; DELETE FROM dbo.PasswordResetChallenges WHERE UserId=@user;";
            update.Parameters.AddWithValue("@salt", salt);
            update.Parameters.AddWithValue("@hash", passwordHash);
            update.Parameters.AddWithValue("@iterations", AccountSecurity.Iterations);
            update.Parameters.AddWithValue("@user", userId);
            update.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    public PackingProject? LoadLatestTrip(long userId)
    {
        using var connection = Open(); long tripId; var project = new PackingProject();
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = "SELECT TOP(1) t.TripId,t.Title,t.Days,tt.Name,t.WeightLimit,t.UpdatedUtc FROM dbo.Trips t JOIN dbo.TripTypes tt ON tt.TripTypeId=t.TripTypeId WHERE t.UserId=@user ORDER BY t.UpdatedUtc DESC;";
            cmd.Parameters.AddWithValue("@user", userId); using var r = cmd.ExecuteReader(); if (!r.Read()) return null;
            tripId = r.GetInt64(0); project.Name = r.GetString(1); project.Days = r.GetInt32(2); project.TripType = r.GetString(3); project.WeightLimit = r.GetDouble(4); project.SavedAt = DateTime.SpecifyKind(r.GetDateTime(5), DateTimeKind.Utc).ToLocalTime();
        }
        using (var cmd = connection.CreateCommand())
        { cmd.CommandText = "SELECT City FROM dbo.TripCities WHERE TripId=@trip ORDER BY Position;"; cmd.Parameters.AddWithValue("@trip", tripId); using var r = cmd.ExecuteReader(); var cities = new List<string>(); while (r.Read()) cities.Add(r.GetString(0)); project.Cities = string.Join(", ", cities); }
        using (var cmd = connection.CreateCommand())
        { cmd.CommandText = "SELECT p.Name,c.Name,tr.Name,p.Quantity,p.UnitWeight,p.IsRequired,s.Name FROM dbo.PackingItems p JOIN dbo.ItemCategories c ON c.CategoryId=p.CategoryId JOIN dbo.Travelers tr ON tr.TravelerId=p.TravelerId JOIN dbo.ItemStatuses s ON s.StatusId=p.StatusId WHERE p.TripId=@trip ORDER BY p.ItemId;"; cmd.Parameters.AddWithValue("@trip", tripId); using var r = cmd.ExecuteReader(); while (r.Read()) project.Items.Add(new PackingEntry { Name=r.GetString(0), Category=r.GetString(1), Person=r.GetString(2), Quantity=r.GetInt32(3), UnitWeight=r.GetDouble(4), Required=r.GetBoolean(5), Status=r.GetString(6) }); }
        using (var cmd = connection.CreateCommand())
        { cmd.CommandText = "SELECT City,MinTemp,MaxTemp,RainPercent,WindKmh,Icon,IsOffline FROM dbo.TripWeather WHERE TripId=@trip ORDER BY WeatherId;"; cmd.Parameters.AddWithValue("@trip", tripId); using var r = cmd.ExecuteReader(); while (r.Read()) project.Weather.Add(new WeatherCard { City=r.GetString(0), Min=r.GetInt32(1), Max=r.GetInt32(2), Rain=r.GetInt32(3), Wind=r.GetInt32(4), Icon=r.GetString(5), Offline=r.GetBoolean(6) }); }
        return project;
    }

    public void SaveTrip(long userId, PackingProject project)
    {
        using var connection = Open(); using var tx = connection.BeginTransaction(); long tripId = 0;
        using (var cmd = connection.CreateCommand())
        {
            cmd.Transaction=tx; cmd.CommandText="SELECT TOP(1) TripId FROM dbo.Trips WHERE UserId=@user ORDER BY UpdatedUtc DESC;"; cmd.Parameters.AddWithValue("@user",userId); var found=cmd.ExecuteScalar();
            if (found is null or DBNull) cmd.CommandText="INSERT dbo.Trips(UserId,Title,Days,TripTypeId,WeightLimit,UpdatedUtc) OUTPUT INSERTED.TripId VALUES(@user,@title,@days,(SELECT TripTypeId FROM dbo.TripTypes WHERE Name=@type),@limit,SYSUTCDATETIME());";
            else { tripId=Convert.ToInt64(found,CultureInfo.InvariantCulture); cmd.CommandText="UPDATE dbo.Trips SET Title=@title,Days=@days,TripTypeId=(SELECT TripTypeId FROM dbo.TripTypes WHERE Name=@type),WeightLimit=@limit,UpdatedUtc=SYSUTCDATETIME() WHERE TripId=@trip;"; cmd.Parameters.AddWithValue("@trip",tripId); }
            cmd.Parameters.AddWithValue("@title",project.Name); cmd.Parameters.AddWithValue("@days",Math.Clamp(project.Days,1,365)); cmd.Parameters.AddWithValue("@type",project.TripType); cmd.Parameters.AddWithValue("@limit",Math.Max(.1,project.WeightLimit));
            if (found is null or DBNull) tripId=Convert.ToInt64(cmd.ExecuteScalar(),CultureInfo.InvariantCulture); else cmd.ExecuteNonQuery();
        }
        using (var cmd=connection.CreateCommand()) { cmd.Transaction=tx; cmd.CommandText="DELETE FROM dbo.TripCities WHERE TripId=@trip; DELETE FROM dbo.PackingItems WHERE TripId=@trip; DELETE FROM dbo.TripWeather WHERE TripId=@trip; DELETE FROM dbo.Travelers WHERE TripId=@trip;"; cmd.Parameters.AddWithValue("@trip",tripId); cmd.ExecuteNonQuery(); }
        var cities=SplitCities(project.Cities); for(var position=0;position<cities.Count;position++) { using var cmd=connection.CreateCommand(); cmd.Transaction=tx; cmd.CommandText="INSERT dbo.TripCities(TripId,Position,City) VALUES(@trip,@position,@city);"; cmd.Parameters.AddWithValue("@trip",tripId); cmd.Parameters.AddWithValue("@position",position); cmd.Parameters.AddWithValue("@city",cities[position]); cmd.ExecuteNonQuery(); }
        var travelerIds=new Dictionary<string,long>(StringComparer.OrdinalIgnoreCase);
        foreach(var person in project.Items.Select(x=>x.Person).Distinct(StringComparer.OrdinalIgnoreCase).DefaultIfEmpty("Общее")) { using var cmd=connection.CreateCommand(); cmd.Transaction=tx; cmd.CommandText="INSERT dbo.Travelers(TripId,Name) OUTPUT INSERTED.TravelerId VALUES(@trip,@name);"; cmd.Parameters.AddWithValue("@trip",tripId); cmd.Parameters.AddWithValue("@name",person); travelerIds[person]=Convert.ToInt64(cmd.ExecuteScalar(),CultureInfo.InvariantCulture); }
        foreach(var item in project.Items) { var category=EnsureLookup(connection,tx,"ItemCategories","CategoryId",item.Category); var status=EnsureLookup(connection,tx,"ItemStatuses","StatusId",item.Status); using var cmd=connection.CreateCommand(); cmd.Transaction=tx; cmd.CommandText="INSERT dbo.PackingItems(TripId,Name,CategoryId,TravelerId,Quantity,UnitWeight,IsRequired,StatusId) VALUES(@trip,@name,@category,@person,@quantity,@weight,@required,@status);"; cmd.Parameters.AddWithValue("@trip",tripId); cmd.Parameters.AddWithValue("@name",item.Name); cmd.Parameters.AddWithValue("@category",category); cmd.Parameters.AddWithValue("@person",travelerIds.GetValueOrDefault(item.Person,travelerIds.Values.First())); cmd.Parameters.AddWithValue("@quantity",Math.Max(1,item.Quantity)); cmd.Parameters.AddWithValue("@weight",Math.Max(0,item.UnitWeight)); cmd.Parameters.AddWithValue("@required",item.Required); cmd.Parameters.AddWithValue("@status",status); cmd.ExecuteNonQuery(); }
        foreach(var weather in project.Weather) { using var cmd=connection.CreateCommand(); cmd.Transaction=tx; cmd.CommandText="INSERT dbo.TripWeather(TripId,City,MinTemp,MaxTemp,RainPercent,WindKmh,Icon,IsOffline) VALUES(@trip,@city,@min,@max,@rain,@wind,@icon,@offline);"; cmd.Parameters.AddWithValue("@trip",tripId); cmd.Parameters.AddWithValue("@city",weather.City); cmd.Parameters.AddWithValue("@min",weather.Min); cmd.Parameters.AddWithValue("@max",weather.Max); cmd.Parameters.AddWithValue("@rain",weather.Rain); cmd.Parameters.AddWithValue("@wind",weather.Wind); cmd.Parameters.AddWithValue("@icon",weather.Icon); cmd.Parameters.AddWithValue("@offline",weather.Offline); cmd.ExecuteNonQuery(); }
        tx.Commit();
    }

    private static int EnsureLookup(SqlConnection connection,SqlTransaction tx,string table,string idColumn,string name)
    { using var cmd=connection.CreateCommand(); cmd.Transaction=tx; cmd.CommandText=$"IF NOT EXISTS(SELECT 1 FROM dbo.[{table}] WHERE Name=@name) INSERT dbo.[{table}](Name) VALUES(@name); SELECT [{idColumn}] FROM dbo.[{table}] WHERE Name=@name;"; cmd.Parameters.AddWithValue("@name",name); return Convert.ToInt32(cmd.ExecuteScalar(),CultureInfo.InvariantCulture); }

    private static List<string> SplitCities(string value) => value.Split(',',StringSplitOptions.TrimEntries|StringSplitOptions.RemoveEmptyEntries).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    public (int Users,int Trips,int Items) CountRows() { using var c=Open(); return (Count(c,"AppUsers"),Count(c,"Trips"),Count(c,"PackingItems")); }
    private static int Count(SqlConnection c,string table) { using var cmd=c.CreateCommand(); cmd.CommandText=$"SELECT COUNT(*) FROM dbo.[{table}];"; return Convert.ToInt32(cmd.ExecuteScalar(),CultureInfo.InvariantCulture); }
    public (string Integrity,int ForeignKeyViolations) VerifyIntegrity() { using var c=Open(); using var cmd=c.CreateCommand(); cmd.CommandText="SELECT COUNT(*) FROM sys.foreign_keys WHERE is_not_trusted=1;"; var bad=Convert.ToInt32(cmd.ExecuteScalar(),CultureInfo.InvariantCulture); return ("ok",bad); }
}
