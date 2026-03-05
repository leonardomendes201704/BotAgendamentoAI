namespace BotAgendamentoAI.Domain;

public sealed class BookingStore : IBookingStore
{
    private readonly List<Booking> _items = new();
    private readonly Dictionary<string, Dictionary<string, ServiceCategory>> _categoriesByTenant = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _sync = new();
    private long _categorySequence = 1;

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
        var category = EnsureCategory(tenantId, serviceCategory);

        var booking = new Booking(
            Id: Guid.NewGuid().ToString("N"),
            TenantId: tenantId,
            CustomerPhone: customerPhone,
            CustomerName: customerName,
            ServiceCategory: category.Name,
            ServiceTitle: serviceTitle,
            StartLocal: startLocal,
            DurationMinutes: durationMinutes,
            Address: address,
            Notes: notes,
            TechnicianName: technicianName
        );

        lock (_sync)
        {
            _items.Add(booking);
        }

        return booking;
    }

    public IReadOnlyList<Booking> List(
        string tenantId,
        string? customerPhone = null,
        DateTime? from = null,
        DateTime? to = null)
    {
        lock (_sync)
        {
            IEnumerable<Booking> query = _items.Where(x => x.TenantId == tenantId);

            if (!string.IsNullOrWhiteSpace(customerPhone))
            {
                query = query.Where(x => x.CustomerPhone == customerPhone);
            }

            if (from is not null)
            {
                query = query.Where(x => x.StartLocal >= from.Value);
            }

            if (to is not null)
            {
                query = query.Where(x => x.StartLocal <= to.Value);
            }

            return query
                .OrderBy(x => x.StartLocal)
                .ToList();
        }
    }

    public bool Cancel(string tenantId, string bookingId)
    {
        lock (_sync)
        {
            var index = _items.FindIndex(x => x.TenantId == tenantId && x.Id == bookingId);
            if (index < 0)
            {
                return false;
            }

            _items.RemoveAt(index);
            return true;
        }
    }

    public Booking? Get(string tenantId, string bookingId)
    {
        lock (_sync)
        {
            return _items.FirstOrDefault(x => x.TenantId == tenantId && x.Id == bookingId);
        }
    }

    public Booking? Reschedule(string tenantId, string bookingId, DateTime newStartLocal)
    {
        lock (_sync)
        {
            var index = _items.FindIndex(x => x.TenantId == tenantId && x.Id == bookingId);
            if (index < 0)
            {
                return null;
            }

            var current = _items[index];
            var updated = current with { StartLocal = newStartLocal };
            _items[index] = updated;
            return updated;
        }
    }

    public IReadOnlyList<ServiceCategory> GetCategories(string tenantId)
    {
        lock (_sync)
        {
            EnsureDefaultCategoriesLocked(tenantId);
            var tenantKey = tenantId.Trim();
            return _categoriesByTenant.TryGetValue(tenantKey, out var categories)
                ? categories.Values.OrderBy(c => c.Name).ToList()
                : Array.Empty<ServiceCategory>();
        }
    }

    public ServiceCategory EnsureCategory(string tenantId, string categoryName)
    {
        lock (_sync)
        {
            EnsureDefaultCategoriesLocked(tenantId);

            var tenantKey = tenantId.Trim();
            if (!_categoriesByTenant.TryGetValue(tenantKey, out var categories))
            {
                categories = new Dictionary<string, ServiceCategory>(StringComparer.Ordinal);
                _categoriesByTenant[tenantKey] = categories;
            }

            var chosenName = ServiceCategoryRules.ChooseCategory(categoryName, categoryName);
            var normalized = ServiceCategoryRules.NormalizeKey(chosenName);
            if (categories.TryGetValue(normalized, out var existing))
            {
                return existing;
            }

            var created = new ServiceCategory(
                Id: _categorySequence++,
                TenantId: tenantKey,
                Name: chosenName,
                NormalizedName: normalized,
                CreatedAtUtc: DateTimeOffset.UtcNow);

            categories[normalized] = created;
            return created;
        }
    }

    private void EnsureDefaultCategoriesLocked(string tenantId)
    {
        var tenantKey = tenantId.Trim();
        if (!_categoriesByTenant.TryGetValue(tenantKey, out var categories))
        {
            categories = new Dictionary<string, ServiceCategory>(StringComparer.Ordinal);
            _categoriesByTenant[tenantKey] = categories;
        }

        foreach (var name in ServiceCategoryRules.PreferredCategories)
        {
            var normalized = ServiceCategoryRules.NormalizeKey(name);
            if (categories.ContainsKey(normalized))
            {
                continue;
            }

            categories[normalized] = new ServiceCategory(
                Id: _categorySequence++,
                TenantId: tenantKey,
                Name: name,
                NormalizedName: normalized,
                CreatedAtUtc: DateTimeOffset.UtcNow);
        }
    }
}
