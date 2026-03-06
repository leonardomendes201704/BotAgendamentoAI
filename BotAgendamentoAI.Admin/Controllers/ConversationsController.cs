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

    [HttpGet]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public async Task<IActionResult> ActiveThreads(int limit = 150)
    {
        var safeLimit = Math.Clamp(limit, 1, 500);
        var tenants = (await _repository.GetTenantIdsAsync())
            .Where(static tenant => !string.IsNullOrWhiteSpace(tenant))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (tenants.Length == 0)
        {
            return Json(new
            {
                items = Array.Empty<object>(),
                total = 0,
                atUtc = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture)
            });
        }

        var perTenantLimit = Math.Clamp(safeLimit, 20, 200);
        var threadsByTenant = await Task.WhenAll(tenants.Select(async tenant => new
        {
            TenantId = tenant,
            Threads = await _repository.GetConversationThreadsAsync(tenant, perTenantLimit)
        }));

        var items = threadsByTenant
            .SelectMany(group => group.Threads.Select(thread => new
            {
                tenantId = group.TenantId,
                phone = thread.Phone,
                lastMessagePreview = thread.LastMessagePreview,
                menuContext = thread.MenuContext,
                isInHumanHandoff = thread.IsInHumanHandoff,
                isAwaitingHumanReply = thread.IsAwaitingHumanReply,
                lastMessageAtUtc = thread.LastMessageAtUtc
            }))
            .OrderByDescending(static item => item.lastMessageAtUtc)
            .Take(safeLimit)
            .Select(item => new
            {
                tenantId = item.tenantId,
                phone = item.phone,
                lastMessagePreview = item.lastMessagePreview,
                menuContext = item.menuContext,
                isInHumanHandoff = item.isInHumanHandoff,
                isAwaitingHumanReply = item.isAwaitingHumanReply,
                lastMessageAtUtc = item.lastMessageAtUtc.ToString("O", CultureInfo.InvariantCulture)
            })
            .ToList();

        var awaitingHumanReplyCount = items.Count(static item => item.isAwaitingHumanReply);

        return Json(new
        {
            items,
            total = items.Count,
            awaitingHumanReplyCount,
            atUtc = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture)
        });
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
            Handoff = await _repository.GetConversationHandoffStatusAsync(tenant, phone),
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
        var handoff = await _repository.GetConversationHandoffStatusAsync(normalizedTenant, normalizedPhone);

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
            messages = visibleMessages,
            handoff = SerializeHandoff(handoff)
        });
    }

    [HttpGet]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public async Task<IActionResult> HandoffStatus(string tenant, string phone)
    {
        if (string.IsNullOrWhiteSpace(tenant) || string.IsNullOrWhiteSpace(phone))
        {
            return BadRequest(new { error = "tenant e phone sao obrigatorios." });
        }

        var status = await _repository.GetConversationHandoffStatusAsync(tenant, phone);
        return Json(new
        {
            tenantId = tenant.Trim(),
            phone = phone.Trim(),
            handoff = SerializeHandoff(status)
        });
    }

    [HttpPost]
    public async Task<IActionResult> HandoffOpen(string tenant, string phone)
    {
        if (string.IsNullOrWhiteSpace(tenant) || string.IsNullOrWhiteSpace(phone))
        {
            return BadRequest(new { ok = false, error = "tenant e phone sao obrigatorios." });
        }

        var status = await _repository.OpenConversationHandoffAsync(tenant, phone, ResolveAgent());
        return Json(new
        {
            ok = true,
            tenantId = tenant.Trim(),
            phone = phone.Trim(),
            handoff = SerializeHandoff(status)
        });
    }

    [HttpPost]
    public async Task<IActionResult> HandoffClose(string tenant, string phone, string? reason = null)
    {
        if (string.IsNullOrWhiteSpace(tenant) || string.IsNullOrWhiteSpace(phone))
        {
            return BadRequest(new { ok = false, error = "tenant e phone sao obrigatorios." });
        }

        var status = await _repository.CloseConversationHandoffAsync(tenant, phone, ResolveAgent(), reason);
        return Json(new
        {
            ok = true,
            tenantId = tenant.Trim(),
            phone = phone.Trim(),
            handoff = SerializeHandoff(status)
        });
    }

    [HttpPost]
    public async Task<IActionResult> SendHumanMessage(string tenant, string phone, string message)
    {
        if (string.IsNullOrWhiteSpace(tenant) || string.IsNullOrWhiteSpace(phone))
        {
            return BadRequest(new { ok = false, error = "tenant e phone sao obrigatorios." });
        }

        var result = await _repository.SendHumanMessageAsync(tenant, phone, message, ResolveAgent());
        if (!result.Success)
        {
            return BadRequest(new
            {
                ok = false,
                error = string.IsNullOrWhiteSpace(result.Error) ? "Nao foi possivel enviar a mensagem." : result.Error,
                handoff = SerializeHandoff(result.Handoff)
            });
        }

        return Json(new
        {
            ok = true,
            telegramMessageId = result.TelegramMessageId,
            handoff = SerializeHandoff(result.Handoff)
        });
    }

    private string ResolveAgent()
    {
        var identityName = User?.Identity?.Name?.Trim();
        return string.IsNullOrWhiteSpace(identityName) ? "admin" : identityName;
    }

    private static object SerializeHandoff(ConversationHandoffStatus handoff)
    {
        return new
        {
            tenantId = handoff.TenantId,
            phone = handoff.Phone,
            isTelegramThread = handoff.IsTelegramThread,
            isOpen = handoff.IsOpen,
            requestedByRole = handoff.RequestedByRole,
            assignedAgent = handoff.AssignedAgent,
            previousState = handoff.PreviousState,
            closeReason = handoff.CloseReason,
            requestedAtUtc = handoff.RequestedAtUtc?.ToString("O", CultureInfo.InvariantCulture),
            acceptedAtUtc = handoff.AcceptedAtUtc?.ToString("O", CultureInfo.InvariantCulture),
            closedAtUtc = handoff.ClosedAtUtc?.ToString("O", CultureInfo.InvariantCulture),
            lastMessageAtUtc = handoff.LastMessageAtUtc?.ToString("O", CultureInfo.InvariantCulture)
        };
    }
}
