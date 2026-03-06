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
        model.TelegramUsers = await _repository.GetTelegramUsersAsync(tenant);
        return View(model);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Index(BotConfigViewModel input)
    {
        await _repository.SaveBotConfigAsync(input);
        TempData["StatusMessage"] = "Configuracoes salvas com sucesso.";
        return RedirectToAction(nameof(Index), new { tenant = input.TenantId });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ResetTelegramMemory(string tenantId, long telegramUserId, bool clearHistory = true)
    {
        var tenant = string.IsNullOrWhiteSpace(tenantId) ? "A" : tenantId.Trim();
        if (telegramUserId <= 0)
        {
            TempData["ErrorMessage"] = "Informe um Telegram User ID valido.";
            return RedirectToAction(nameof(Index), new { tenant });
        }

        var result = await _repository.ResetTelegramMemoryAsync(tenant, telegramUserId, clearHistory);
        if (!result.FoundUser)
        {
            TempData["ErrorMessage"] = $"Usuario Telegram {telegramUserId} nao encontrado no tenant {tenant}.";
            return RedirectToAction(nameof(Index), new { tenant });
        }

        TempData["StatusMessage"] =
            $"Memoria resetada para {telegramUserId}. " +
            $"Sessoes: {result.SessionsReset}, " +
            $"Logs Telegram: {result.TelegramMessagesDeleted}, " +
            $"Legacy msgs: {result.LegacyConversationMessagesDeleted}, " +
            $"Legacy state: {result.LegacyConversationStateDeleted}.";

        return RedirectToAction(nameof(Index), new { tenant });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ResetTenantOperationalData(string tenantId)
    {
        var tenant = string.IsNullOrWhiteSpace(tenantId) ? "A" : tenantId.Trim();
        var result = await _repository.ResetTenantOperationalDataAsync(tenant);

        TempData["StatusMessage"] =
            $"Reset geral concluido no tenant {tenant}. " +
            $"Legacy msgs: {result.LegacyConversationMessagesDeleted}, " +
            $"Legacy state: {result.LegacyConversationStateDeleted}, " +
            $"Legacy bookings: {result.LegacyBookingsDeleted}, " +
            $"Legacy geocode: {result.LegacyBookingGeocodeCacheDeleted}, " +
            $"TG logs: {result.TelegramMessagesDeleted}, " +
            $"TG jobs: {result.TelegramJobsDeleted}, " +
            $"TG users: {result.TelegramUsersDeleted}.";

        return RedirectToAction(nameof(Index), new { tenant });
    }
}
