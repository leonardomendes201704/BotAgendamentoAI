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
}
