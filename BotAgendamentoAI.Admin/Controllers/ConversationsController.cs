using BotAgendamentoAI.Admin.Data;
using BotAgendamentoAI.Admin.Models;
using Microsoft.AspNetCore.Mvc;
using System.Globalization;

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

        var safeLimit = Math.Clamp(limit, 1, 1000);
        var model = new ConversationDetailsViewModel
        {
            TenantId = tenant.Trim(),
            Phone = phone.Trim(),
            Messages = await _repository.GetConversationMessagesAsync(tenant, phone, safeLimit)
        };
        ViewData["Limit"] = safeLimit;

        return View(model);
    }

    [HttpGet]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public async Task<IActionResult> Snapshot(string tenant, string phone, int limit = 250)
    {
        if (string.IsNullOrWhiteSpace(tenant) || string.IsNullOrWhiteSpace(phone))
        {
            return BadRequest(new { error = "tenant e phone sao obrigatorios." });
        }

        var normalizedTenant = tenant.Trim();
        var normalizedPhone = phone.Trim();
        var messages = await _repository.GetConversationMessagesAsync(
            normalizedTenant,
            normalizedPhone,
            Math.Clamp(limit, 1, 1000));

        var visibleMessages = messages
            .Where(message => !string.Equals(message.Role, "tool", StringComparison.OrdinalIgnoreCase))
            .Select(message => new
            {
                id = message.Id,
                direction = message.Direction,
                role = message.Role,
                content = message.Content,
                createdAtUtc = message.CreatedAtUtc.ToString("O", CultureInfo.InvariantCulture)
            })
            .ToList();

        return Json(new
        {
            tenantId = normalizedTenant,
            phone = normalizedPhone,
            atUtc = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            messages = visibleMessages
        });
    }
}
