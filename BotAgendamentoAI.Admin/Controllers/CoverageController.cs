using BotAgendamentoAI.Admin.Data;
using BotAgendamentoAI.Admin.Models;
using Microsoft.AspNetCore.Mvc;

namespace BotAgendamentoAI.Admin.Controllers;

public sealed class CoverageController : Controller
{
    private readonly IAdminRepository _repository;

    public CoverageController(IAdminRepository repository)
    {
        _repository = repository;
    }

    public async Task<IActionResult> Index(string tenant = "A", int limit = 500)
    {
        var safeTenant = string.IsNullOrWhiteSpace(tenant) ? "A" : tenant.Trim();
        var safeLimit = Math.Clamp(limit, 1, 2000);

        var providers = await _repository.GetProviderCoverageAsync(safeTenant, safeLimit);
        var neighborhoods = providers
            .SelectMany(static provider => provider.Neighborhoods)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(static value => value, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var model = new CoveragePageViewModel
        {
            TenantId = safeTenant,
            Tenants = await _repository.GetTenantIdsAsync(),
            Providers = providers,
            Neighborhoods = neighborhoods
        };

        ViewData["Limit"] = safeLimit;
        return View(model);
    }
}
