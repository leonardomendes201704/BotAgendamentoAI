using BotAgendamentoAI.Admin.Data;
using BotAgendamentoAI.Admin.Models;
using Microsoft.AspNetCore.Mvc;

namespace BotAgendamentoAI.Admin.Controllers;

public sealed class CategoriesController : Controller
{
    private readonly IAdminRepository _repository;

    public CategoriesController(IAdminRepository repository)
    {
        _repository = repository;
    }

    public async Task<IActionResult> Index(string tenant = "A")
    {
        var model = new CategoriesPageViewModel
        {
            TenantId = string.IsNullOrWhiteSpace(tenant) ? "A" : tenant.Trim(),
            Categories = await _repository.GetCategoriesAsync(tenant),
            Tenants = await _repository.GetTenantIdsAsync()
        };
        return View(model);
    }

    public IActionResult Create(string tenant = "A")
    {
        return View(new CategoryEditViewModel { TenantId = string.IsNullOrWhiteSpace(tenant) ? "A" : tenant.Trim() });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(CategoryEditViewModel input)
    {
        try
        {
            await _repository.CreateCategoryAsync(input.TenantId, input.Name);
            return RedirectToAction(nameof(Index), new { tenant = input.TenantId });
        }
        catch (Exception ex)
        {
            ModelState.AddModelError(string.Empty, ex.Message);
            return View(input);
        }
    }

    public async Task<IActionResult> Edit(string tenant, long id)
    {
        var item = await _repository.GetCategoryByIdAsync(tenant, id);
        if (item is null)
        {
            return RedirectToAction(nameof(Index), new { tenant });
        }

        return View(new CategoryEditViewModel
        {
            Id = item.Id,
            TenantId = item.TenantId,
            Name = item.Name
        });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(CategoryEditViewModel input)
    {
        if (!input.Id.HasValue)
        {
            return RedirectToAction(nameof(Index), new { tenant = input.TenantId });
        }

        try
        {
            var updated = await _repository.UpdateCategoryAsync(input.TenantId, input.Id.Value, input.Name);
            if (updated is null)
            {
                return RedirectToAction(nameof(Index), new { tenant = input.TenantId });
            }

            return RedirectToAction(nameof(Index), new { tenant = input.TenantId });
        }
        catch (Exception ex)
        {
            ModelState.AddModelError(string.Empty, ex.Message);
            return View(input);
        }
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Delete(string tenant, long id)
    {
        await _repository.DeleteCategoryAsync(tenant, id);
        return RedirectToAction(nameof(Index), new { tenant });
    }
}
