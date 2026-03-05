using BotAgendamentoAI.Admin.Data;
using BotAgendamentoAI.Admin.Models;
using Microsoft.AspNetCore.Mvc;

namespace BotAgendamentoAI.Admin.Controllers;

public sealed class DashboardController : Controller
{
    private readonly IAdminRepository _repository;

    public DashboardController(IAdminRepository repository)
    {
        _repository = repository;
    }

    public async Task<IActionResult> Index(string tenant = "A", int days = 30)
    {
        var model = await _repository.GetDashboardAsync(tenant, days);
        model.Tenants = await _repository.GetTenantIdsAsync();
        return View(model);
    }

    [HttpGet]
    public async Task<IActionResult> MapPins(string tenant = "A", string? sinceUtc = null, int limit = 300)
    {
        DateTimeOffset? parsedSince = null;
        if (!string.IsNullOrWhiteSpace(sinceUtc) &&
            DateTimeOffset.TryParse(
                sinceUtc,
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
                out var parsed))
        {
            parsedSince = parsed;
        }

        var pins = await _repository.GetDashboardMapPinsAsync(tenant, parsedSince, limit);
        return Json(new
        {
            tenantId = tenant,
            nowUtc = DateTimeOffset.UtcNow,
            count = pins.Count,
            pins
        });
    }
}
