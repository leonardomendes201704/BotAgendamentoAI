using System.Globalization;
using BotAgendamentoAI.Domain;
using Microsoft.Data.Sqlite;

namespace BotAgendamentoAI.Data;

public sealed class SqliteBookingStore : IBookingStore
{
    private readonly string _connectionString;

    private const string SchemaSql = """
CREATE TABLE IF NOT EXISTS tg_bookings (
  id TEXT PRIMARY KEY,
  tenant_id TEXT NOT NULL,
  customer_phone TEXT NOT NULL,
  customer_name TEXT NOT NULL,
  service_category TEXT NOT NULL,
  service_title TEXT NOT NULL,
  start_local TEXT NOT NULL,
  duration_minutes INTEGER NOT NULL,
  address TEXT NOT NULL,
  notes TEXT NOT NULL,
  technician_name TEXT NOT NULL,
  created_at_utc TEXT NOT NULL
);

CREATE INDEX IF NOT EXISTS idx_bookings_tenant_phone_start
ON tg_bookings(tenant_id, customer_phone, start_local);

CREATE INDEX IF NOT EXISTS idx_bookings_tenant_start
ON tg_bookings(tenant_id, start_local);

CREATE TABLE IF NOT EXISTS tg_service_categories (
  id INTEGER PRIMARY KEY AUTOINCREMENT,
  tenant_id TEXT NOT NULL,
  name TEXT NOT NULL,
  normalized_name TEXT NOT NULL,
  created_at_utc TEXT NOT NULL,
  UNIQUE(tenant_id, normalized_name)
);

CREATE INDEX IF NOT EXISTS idx_service_categories_tenant_name
ON tg_service_categories(tenant_id, name);
""";

    public SqliteBookingStore(string sqlitePath)
    {
        var fullPath = Path.GetFullPath(sqlitePath);
        var dir = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrWhiteSpace(dir))
        {
            Directory.CreateDirectory(dir);
        }

        _connectionString = $"Data Source={fullPath}";
        EnsureSchema();
    }

    public Booking Create(
        string tenantId,
        string customerPhone,
        string customerName,
        string serviceCategory,
        string serviceTitle,
        DateTime startLocal,
        int durationMinutes,
        string address,
        string notes,
        string technicianName)
    {
        using var connection = CreateConnection();
        connection.Open();

        var tenant = tenantId.Trim();
        var category = EnsureCategoryInternal(connection, tenant, serviceCategory);

        var booking = new Booking(
            Id: Guid.NewGuid().ToString("N"),
            TenantId: tenant,
            CustomerPhone: customerPhone.Trim(),
            CustomerName: customerName.Trim(),
            ServiceCategory: category.Name,
            ServiceTitle: serviceTitle.Trim(),
            StartLocal: startLocal,
            DurationMinutes: durationMinutes,
            Address: address.Trim(),
            Notes: notes?.Trim() ?? string.Empty,
            TechnicianName: technicianName.Trim());

        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO tg_bookings
            (id, tenant_id, customer_phone, customer_name, service_category, service_title, start_local, duration_minutes, address, notes, technician_name, created_at_utc)
            VALUES
            (@id, @tenant_id, @customer_phone, @customer_name, @service_category, @service_title, @start_local, @duration_minutes, @address, @notes, @technician_name, @created_at_utc);
            """;

        command.Parameters.AddWithValue("@id", booking.Id);
        command.Parameters.AddWithValue("@tenant_id", booking.TenantId);
        command.Parameters.AddWithValue("@customer_phone", booking.CustomerPhone);
        command.Parameters.AddWithValue("@customer_name", booking.CustomerName);
        command.Parameters.AddWithValue("@service_category", booking.ServiceCategory);
        command.Parameters.AddWithValue("@service_title", booking.ServiceTitle);
        command.Parameters.AddWithValue("@start_local", ToLocalText(booking.StartLocal));
        command.Parameters.AddWithValue("@duration_minutes", booking.DurationMinutes);
        command.Parameters.AddWithValue("@address", booking.Address);
        command.Parameters.AddWithValue("@notes", booking.Notes);
        command.Parameters.AddWithValue("@technician_name", booking.TechnicianName);
        command.Parameters.AddWithValue("@created_at_utc", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));

        command.ExecuteNonQuery();
        return booking;
    }

    public IReadOnlyList<Booking> List(
        string tenantId,
        string? customerPhone = null,
        DateTime? from = null,
        DateTime? to = null)
    {
        var output = new List<Booking>();

        using var connection = CreateConnection();
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT id, tenant_id, customer_phone, customer_name, service_category, service_title, start_local, duration_minutes, address, notes, technician_name
            FROM tg_bookings
            WHERE tenant_id = @tenant_id
              AND (@customer_phone IS NULL OR customer_phone = @customer_phone)
              AND (@from_local IS NULL OR start_local >= @from_local)
              AND (@to_local IS NULL OR start_local <= @to_local)
            ORDER BY start_local ASC;
            """;

        command.Parameters.AddWithValue("@tenant_id", tenantId.Trim());
        command.Parameters.AddWithValue("@customer_phone", (object?)NormalizePhone(customerPhone) ?? DBNull.Value);
        command.Parameters.AddWithValue("@from_local", from.HasValue ? ToLocalText(from.Value) : DBNull.Value);
        command.Parameters.AddWithValue("@to_local", to.HasValue ? ToLocalText(to.Value) : DBNull.Value);

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            output.Add(new Booking(
                Id: reader.GetString(0),
                TenantId: reader.GetString(1),
                CustomerPhone: reader.GetString(2),
                CustomerName: reader.GetString(3),
                ServiceCategory: ReadCategoryOrInfer(reader.IsDBNull(4) ? string.Empty : reader.GetString(4), reader.GetString(5)),
                ServiceTitle: reader.GetString(5),
                StartLocal: ParseLocalDateTime(reader.GetString(6)),
                DurationMinutes: reader.GetInt32(7),
                Address: reader.GetString(8),
                Notes: reader.GetString(9),
                TechnicianName: reader.GetString(10)));
        }

        return output;
    }

    public bool Cancel(string tenantId, string bookingId)
    {
        using var connection = CreateConnection();
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText =
            """
            DELETE FROM tg_bookings
            WHERE tenant_id = @tenant_id AND id = @id;
            """;

        command.Parameters.AddWithValue("@tenant_id", tenantId.Trim());
        command.Parameters.AddWithValue("@id", bookingId.Trim());

        return command.ExecuteNonQuery() > 0;
    }

    public Booking? Get(string tenantId, string bookingId)
    {
        using var connection = CreateConnection();
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT id, tenant_id, customer_phone, customer_name, service_category, service_title, start_local, duration_minutes, address, notes, technician_name
            FROM tg_bookings
            WHERE tenant_id = @tenant_id AND id = @id
            LIMIT 1;
            """;

        command.Parameters.AddWithValue("@tenant_id", tenantId.Trim());
        command.Parameters.AddWithValue("@id", bookingId.Trim());

        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        return new Booking(
            Id: reader.GetString(0),
            TenantId: reader.GetString(1),
            CustomerPhone: reader.GetString(2),
            CustomerName: reader.GetString(3),
            ServiceCategory: ReadCategoryOrInfer(reader.IsDBNull(4) ? string.Empty : reader.GetString(4), reader.GetString(5)),
            ServiceTitle: reader.GetString(5),
            StartLocal: ParseLocalDateTime(reader.GetString(6)),
            DurationMinutes: reader.GetInt32(7),
            Address: reader.GetString(8),
            Notes: reader.GetString(9),
            TechnicianName: reader.GetString(10));
    }

    public Booking? Reschedule(string tenantId, string bookingId, DateTime newStartLocal)
    {
        using var connection = CreateConnection();
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE tg_bookings
            SET start_local = @new_start_local
            WHERE tenant_id = @tenant_id AND id = @id;
            """;

        command.Parameters.AddWithValue("@new_start_local", ToLocalText(newStartLocal));
        command.Parameters.AddWithValue("@tenant_id", tenantId.Trim());
        command.Parameters.AddWithValue("@id", bookingId.Trim());

        var affected = command.ExecuteNonQuery();
        if (affected <= 0)
        {
            return null;
        }

        return Get(tenantId, bookingId);
    }

    public IReadOnlyList<ServiceCategory> GetCategories(string tenantId)
    {
        using var connection = CreateConnection();
        connection.Open();

        var tenant = tenantId.Trim();
        EnsureDefaultCategoriesInternal(connection, tenant);

        var output = new List<ServiceCategory>();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT id, tenant_id, name, normalized_name, created_at_utc
            FROM tg_service_categories
            WHERE tenant_id = @tenant_id
            ORDER BY name ASC;
            """;
        command.Parameters.AddWithValue("@tenant_id", tenant);

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            output.Add(new ServiceCategory(
                Id: reader.GetInt64(0),
                TenantId: reader.GetString(1),
                Name: reader.GetString(2),
                NormalizedName: reader.GetString(3),
                CreatedAtUtc: ParseUtc(reader.GetString(4))));
        }

        return output;
    }

    public ServiceCategory EnsureCategory(string tenantId, string categoryName)
    {
        using var connection = CreateConnection();
        connection.Open();
        return EnsureCategoryInternal(connection, tenantId.Trim(), categoryName);
    }

    private void EnsureSchema()
    {
        using var connection = CreateConnection();
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = SchemaSql;
        command.ExecuteNonQuery();

        EnsureBookingServiceCategoryColumn(connection);
        BackfillLegacyBookingCategories(connection);
    }

    private static void EnsureBookingServiceCategoryColumn(SqliteConnection connection)
    {
        if (HasColumn(connection, "tg_bookings", "service_category"))
        {
            return;
        }

        using var command = connection.CreateCommand();
        command.CommandText = "ALTER TABLE tg_bookings ADD COLUMN service_category TEXT NULL;";
        command.ExecuteNonQuery();
    }

    private static bool HasColumn(SqliteConnection connection, string tableName, string columnName)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info({tableName});";

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var name = reader.IsDBNull(1) ? string.Empty : reader.GetString(1);
            if (string.Equals(name, columnName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private void BackfillLegacyBookingCategories(SqliteConnection connection)
    {
        var pending = new List<(string Id, string TenantId, string CategoryName)>();

        using (var command = connection.CreateCommand())
        {
            command.CommandText =
                """
                SELECT id, tenant_id, service_title, notes
                FROM tg_bookings
                WHERE service_category IS NULL OR TRIM(service_category) = '';
                """;

            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var id = reader.GetString(0);
                var tenantId = reader.GetString(1);
                var serviceTitle = reader.GetString(2);
                var notes = reader.IsDBNull(3) ? string.Empty : reader.GetString(3);

                var chosen = ServiceCategoryRules.ChooseCategory(null, serviceTitle, notes);
                pending.Add((id, tenantId, chosen));
            }
        }

        foreach (var item in pending)
        {
            var category = EnsureCategoryInternal(connection, item.TenantId, item.CategoryName);

            using var update = connection.CreateCommand();
            update.CommandText =
                """
                UPDATE tg_bookings
                SET service_category = @service_category
                WHERE tenant_id = @tenant_id AND id = @id;
                """;
            update.Parameters.AddWithValue("@service_category", category.Name);
            update.Parameters.AddWithValue("@tenant_id", item.TenantId);
            update.Parameters.AddWithValue("@id", item.Id);
            update.ExecuteNonQuery();
        }
    }

    private ServiceCategory EnsureCategoryInternal(SqliteConnection connection, string tenantId, string categoryName)
    {
        EnsureDefaultCategoriesInternal(connection, tenantId);

        var chosen = ServiceCategoryRules.ChooseCategory(categoryName, categoryName);
        var normalized = ServiceCategoryRules.NormalizeKey(chosen);
        var createdAt = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);

        using (var insert = connection.CreateCommand())
        {
            insert.CommandText =
                """
                INSERT OR IGNORE INTO tg_service_categories (tenant_id, name, normalized_name, created_at_utc)
                VALUES (@tenant_id, @name, @normalized_name, @created_at_utc);
                """;
            insert.Parameters.AddWithValue("@tenant_id", tenantId);
            insert.Parameters.AddWithValue("@name", chosen);
            insert.Parameters.AddWithValue("@normalized_name", normalized);
            insert.Parameters.AddWithValue("@created_at_utc", createdAt);
            insert.ExecuteNonQuery();
        }

        using var select = connection.CreateCommand();
        select.CommandText =
            """
            SELECT id, tenant_id, name, normalized_name, created_at_utc
            FROM tg_service_categories
            WHERE tenant_id = @tenant_id AND normalized_name = @normalized_name
            LIMIT 1;
            """;
        select.Parameters.AddWithValue("@tenant_id", tenantId);
        select.Parameters.AddWithValue("@normalized_name", normalized);

        using var reader = select.ExecuteReader();
        if (reader.Read())
        {
            return new ServiceCategory(
                Id: reader.GetInt64(0),
                TenantId: reader.GetString(1),
                Name: reader.GetString(2),
                NormalizedName: reader.GetString(3),
                CreatedAtUtc: ParseUtc(reader.GetString(4)));
        }

        return new ServiceCategory(
            Id: 0,
            TenantId: tenantId,
            Name: chosen,
            NormalizedName: normalized,
            CreatedAtUtc: DateTimeOffset.UtcNow);
    }

    private static void EnsureDefaultCategoriesInternal(SqliteConnection connection, string tenantId)
    {
        foreach (var category in ServiceCategoryRules.PreferredCategories)
        {
            using var insert = connection.CreateCommand();
            insert.CommandText =
                """
                INSERT OR IGNORE INTO tg_service_categories (tenant_id, name, normalized_name, created_at_utc)
                VALUES (@tenant_id, @name, @normalized_name, @created_at_utc);
                """;
            insert.Parameters.AddWithValue("@tenant_id", tenantId);
            insert.Parameters.AddWithValue("@name", category);
            insert.Parameters.AddWithValue("@normalized_name", ServiceCategoryRules.NormalizeKey(category));
            insert.Parameters.AddWithValue("@created_at_utc", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            insert.ExecuteNonQuery();
        }
    }

    private SqliteConnection CreateConnection() => new(_connectionString);

    private static string ReadCategoryOrInfer(string storedCategory, string serviceTitle)
    {
        if (!string.IsNullOrWhiteSpace(storedCategory))
        {
            return storedCategory;
        }

        return ServiceCategoryRules.ChooseCategory(null, serviceTitle);
    }

    private static string ToLocalText(DateTime value)
        => value.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

    private static DateTime ParseLocalDateTime(string value)
    {
        if (DateTime.TryParseExact(
                value,
                "yyyy-MM-dd HH:mm:ss",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var parsedExact))
        {
            return parsedExact;
        }

        if (DateTime.TryParse(
                value,
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var parsed))
        {
            return parsed;
        }

        return DateTime.MinValue;
    }

    private static DateTimeOffset ParseUtc(string value)
    {
        if (DateTimeOffset.TryParse(
                value,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var parsed))
        {
            return parsed;
        }

        return DateTimeOffset.UtcNow;
    }

    private static string? NormalizePhone(string? phone)
    {
        if (string.IsNullOrWhiteSpace(phone))
        {
            return null;
        }

        return phone.Trim();
    }
}

