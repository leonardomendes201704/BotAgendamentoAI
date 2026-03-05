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
}
