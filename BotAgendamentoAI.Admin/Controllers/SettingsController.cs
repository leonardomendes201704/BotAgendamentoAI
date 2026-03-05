using BotAgendamentoAI.Admin.Data;
using BotAgendamentoAI.Admin.Models;
using Microsoft.AspNetCore.Mvc;

namespace BotAgendamentoAI.Admin.Controllers;

public sealed class SettingsController : Controller
{
    private readonly IAdminRepository _repository;

    public SettingsController(IAdminRepository repository)
    {
        _repository = repository;
    }

    public async Task<IActionResult> Index(string tenant = "A")
    {
        var model = await _repository.GetBotConfigAsync(tenant);
        model.Tenants = await _repository.GetTenantIdsAsync();
        return View(model);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Index(BotConfigViewModel input)
    {
        await _repository.SaveBotConfigAsync(input);
        return RedirectToAction(nameof(Index), new { tenant = input.TenantId });
    }
}
