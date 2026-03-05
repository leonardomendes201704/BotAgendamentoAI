using BotAgendamentoAI.Admin.Data;
using BotAgendamentoAI.Admin.Models;
using Microsoft.AspNetCore.Mvc;

namespace BotAgendamentoAI.Admin.Controllers;

public sealed class ConversationsController : Controller
{
    private readonly IAdminRepository _repository;

    public ConversationsController(IAdminRepository repository)
    {
        _repository = repository;
    }

    public async Task<IActionResult> Index(string tenant = "A", int limit = 100)
    {
        ViewData["Tenant"] = tenant;
        ViewData["Limit"] = Math.Clamp(limit, 1, 500);
        ViewData["Tenants"] = await _repository.GetTenantIdsAsync();
        var items = await _repository.GetConversationThreadsAsync(tenant, Math.Clamp(limit, 1, 500));
        return View(items);
    }

    public async Task<IActionResult> Details(string tenant, string phone, int limit = 250)
    {
        if (string.IsNullOrWhiteSpace(tenant) || string.IsNullOrWhiteSpace(phone))
        {
            return RedirectToAction(nameof(Index));
        }

        var model = new ConversationDetailsViewModel
        {
            TenantId = tenant.Trim(),
            Phone = phone.Trim(),
            Messages = await _repository.GetConversationMessagesAsync(tenant, phone, Math.Clamp(limit, 1, 1000))
        };

        return View(model);
    }
}
