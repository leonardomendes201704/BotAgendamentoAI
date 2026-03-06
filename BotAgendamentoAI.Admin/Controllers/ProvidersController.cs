using BotAgendamentoAI.Admin.Data;
using BotAgendamentoAI.Admin.Models;
using Microsoft.AspNetCore.Mvc;

namespace BotAgendamentoAI.Admin.Controllers;

public sealed class ProvidersController : Controller
{
    private readonly IAdminRepository _repository;

    public ProvidersController(IAdminRepository repository)
    {
        _repository = repository;
    }

    public async Task<IActionResult> Index(string tenant = "A", int limit = 200)
    {
        var safeTenant = string.IsNullOrWhiteSpace(tenant) ? "A" : tenant.Trim();
        var safeLimit = Math.Clamp(limit, 1, 1000);

        var model = new ProvidersPageViewModel
        {
            TenantId = safeTenant,
            Providers = await _repository.GetProvidersAsync(safeTenant, safeLimit),
            Tenants = await _repository.GetTenantIdsAsync()
        };

        ViewData["Limit"] = safeLimit;
        return View(model);
    }
}
