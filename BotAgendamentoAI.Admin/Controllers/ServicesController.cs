using BotAgendamentoAI.Admin.Data;
using BotAgendamentoAI.Admin.Models;
using Microsoft.AspNetCore.Mvc;

namespace BotAgendamentoAI.Admin.Controllers;

public sealed class ServicesController : Controller
{
    private readonly IAdminRepository _repository;

    public ServicesController(IAdminRepository repository)
    {
        _repository = repository;
    }

    public async Task<IActionResult> Index(string tenant = "A")
    {
        var model = new ServicesPageViewModel
        {
            TenantId = string.IsNullOrWhiteSpace(tenant) ? "A" : tenant.Trim(),
            Services = await _repository.GetServicesAsync(tenant),
            Tenants = await _repository.GetTenantIdsAsync()
        };
        return View(model);
    }

    public async Task<IActionResult> Create(string tenant = "A")
    {
        var categories = await _repository.GetCategoriesAsync(tenant);
        return View(new ServiceEditViewModel
        {
            TenantId = string.IsNullOrWhiteSpace(tenant) ? "A" : tenant.Trim(),
            AvailableCategories = categories.Select(c => c.Name).ToList()
        });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(ServiceEditViewModel input)
    {
        try
        {
            await _repository.CreateServiceAsync(input);
            return RedirectToAction(nameof(Index), new { tenant = input.TenantId });
        }
        catch (Exception ex)
        {
            input.AvailableCategories = (await _repository.GetCategoriesAsync(input.TenantId)).Select(c => c.Name).ToList();
            ModelState.AddModelError(string.Empty, ex.Message);
            return View(input);
        }
    }

    public async Task<IActionResult> Edit(string tenant, long id)
    {
        var item = await _repository.GetServiceByIdAsync(tenant, id);
        if (item is null)
        {
            return RedirectToAction(nameof(Index), new { tenant });
        }

        return View(new ServiceEditViewModel
        {
            Id = item.Id,
            TenantId = item.TenantId,
            Title = item.Title,
            CategoryName = item.CategoryName,
            DefaultDurationMinutes = item.DefaultDurationMinutes,
            IsActive = item.IsActive,
            AvailableCategories = (await _repository.GetCategoriesAsync(item.TenantId)).Select(c => c.Name).ToList()
        });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(ServiceEditViewModel input)
    {
        if (!input.Id.HasValue)
        {
            return RedirectToAction(nameof(Index), new { tenant = input.TenantId });
        }

        try
        {
            await _repository.UpdateServiceAsync(input);
            return RedirectToAction(nameof(Index), new { tenant = input.TenantId });
        }
        catch (Exception ex)
        {
            input.AvailableCategories = (await _repository.GetCategoriesAsync(input.TenantId)).Select(c => c.Name).ToList();
            ModelState.AddModelError(string.Empty, ex.Message);
            return View(input);
        }
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Delete(string tenant, long id)
    {
        await _repository.DeleteServiceAsync(tenant, id);
        return RedirectToAction(nameof(Index), new { tenant });
    }
}
