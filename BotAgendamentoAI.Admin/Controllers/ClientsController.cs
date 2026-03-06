using BotAgendamentoAI.Admin.Data;
using BotAgendamentoAI.Admin.Models;
using Microsoft.AspNetCore.Mvc;

namespace BotAgendamentoAI.Admin.Controllers;

public sealed class ClientsController : Controller
{
    private readonly IAdminRepository _repository;

    public ClientsController(IAdminRepository repository)
    {
        _repository = repository;
    }

    public async Task<IActionResult> Index(string tenant = "A", int limit = 200)
    {
        var safeTenant = string.IsNullOrWhiteSpace(tenant) ? "A" : tenant.Trim();
        var safeLimit = Math.Clamp(limit, 1, 1000);

        var model = new ClientsPageViewModel
        {
            TenantId = safeTenant,
            Clients = await _repository.GetClientsAsync(safeTenant, safeLimit),
            Tenants = await _repository.GetTenantIdsAsync()
        };

        ViewData["Limit"] = safeLimit;
        return View(model);
    }
}
