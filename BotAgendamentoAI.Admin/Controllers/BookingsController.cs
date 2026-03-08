using BotAgendamentoAI.Admin.Data;
using BotAgendamentoAI.Admin.Models;
using Microsoft.AspNetCore.Mvc;

namespace BotAgendamentoAI.Admin.Controllers;

public sealed class BookingsController : Controller
{
    private readonly IAdminRepository _repository;

    public BookingsController(IAdminRepository repository)
    {
        _repository = repository;
    }

    public async Task<IActionResult> Index(string tenant = "A", int limit = 200)
    {
        var safeLimit = Math.Clamp(limit, 1, 1000);
        var model = new BookingsPageViewModel
        {
            TenantId = string.IsNullOrWhiteSpace(tenant) ? "A" : tenant.Trim(),
            Bookings = await _repository.GetBookingsAsync(tenant, safeLimit)
        };

        ViewData["Limit"] = safeLimit;
        ViewData["Tenants"] = await _repository.GetTenantIdsAsync();
        return View(model);
    }

    [HttpGet]
    public async Task<IActionResult> Details(string tenant, string id)
    {
        var normalizedTenant = string.IsNullOrWhiteSpace(tenant) ? "A" : tenant.Trim();
        if (string.IsNullOrWhiteSpace(id))
        {
            return BadRequest(new { error = "Agendamento invalido." });
        }

        var details = await _repository.GetBookingDetailsAsync(normalizedTenant, id.Trim());
        if (details is null)
        {
            return NotFound(new { error = "Agendamento nao encontrado." });
        }

        return Json(details);
    }
}
