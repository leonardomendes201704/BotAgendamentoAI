using System.Text.Json;
using BotAgendamentoAI.Telegram.Domain.Entities;
using BotAgendamentoAI.Telegram.Domain.Enums;
using BotAgendamentoAI.Telegram.Domain.Fsm;
using BotAgendamentoAI.Telegram.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace BotAgendamentoAI.Telegram.Application.Services;

public sealed class UserContextService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    public async Task<AppUser> EnsureUserAsync(
        BotDbContext db,
        string tenantId,
        global::BotAgendamentoAI.Telegram.TelegramCompat.Types.User from,
        CancellationToken cancellationToken)
    {
        var normalizedTenant = NormalizeTenant(tenantId);
        var tgId = from.Id;

        var user = await db.Users
            .Include(x => x.ProviderProfile)
            .Include(x => x.Session)
            .FirstOrDefaultAsync(
                x => x.TenantId == normalizedTenant && x.TelegramUserId == tgId,
                cancellationToken);

        var now = DateTimeOffset.UtcNow;
        var displayName = BuildDisplayName(from);
        var username = from.Username ?? string.Empty;

        if (user is null)
        {
            user = new AppUser
            {
                TenantId = normalizedTenant,
                TelegramUserId = tgId,
                Name = displayName,
                Username = username,
                Role = UserRole.Client,
                IsActive = true,
                CreatedAt = now,
                UpdatedAt = now,
                Session = new UserSession
                {
                    State = BotStates.NONE,
                    DraftJson = "{}",
                    UpdatedAt = now
                }
            };

            db.Users.Add(user);
            await db.SaveChangesAsync(cancellationToken);
            return user;
        }

        var changed = false;
        if (!string.Equals(user.Name, displayName, StringComparison.Ordinal))
        {
            user.Name = displayName;
            changed = true;
        }

        if (!string.Equals(user.Username, username, StringComparison.Ordinal))
        {
            user.Username = username;
            changed = true;
        }

        if (!user.IsActive)
        {
            user.IsActive = true;
            changed = true;
        }

        if (user.Session is null)
        {
            user.Session = new UserSession
            {
                UserId = user.Id,
                State = BotStates.NONE,
                DraftJson = "{}",
                UpdatedAt = now
            };
            changed = true;
        }

        if (changed)
        {
            user.UpdatedAt = now;
            await db.SaveChangesAsync(cancellationToken);
        }

        return user;
    }

    public static bool IsSessionExpired(UserSession? session, int expiryMinutes)
    {
        if (session is null)
        {
            return true;
        }

        var safeMinutes = Math.Clamp(expiryMinutes, 5, 1440);
        return DateTimeOffset.UtcNow - session.UpdatedAt > TimeSpan.FromMinutes(safeMinutes);
    }

    public static UserDraft ParseDraft(UserSession? session)
    {
        if (session is null || string.IsNullOrWhiteSpace(session.DraftJson))
        {
            return UserDraft.Empty();
        }

        try
        {
            return JsonSerializer.Deserialize<UserDraft>(session.DraftJson, JsonOptions) ?? UserDraft.Empty();
        }
        catch
        {
            return UserDraft.Empty();
        }
    }

    public static void SaveDraft(UserSession session, UserDraft draft)
    {
        session.DraftJson = JsonSerializer.Serialize(draft, JsonOptions);
        session.UpdatedAt = DateTimeOffset.UtcNow;
    }

    public static void SetState(UserSession session, string state)
    {
        session.State = string.IsNullOrWhiteSpace(state) ? BotStates.NONE : state.Trim();
        session.UpdatedAt = DateTimeOffset.UtcNow;
    }

    public static void ResetSession(UserSession session, string targetState)
    {
        session.State = targetState;
        session.DraftJson = "{}";
        session.ActiveJobId = null;
        session.ChatJobId = null;
        session.ChatPeerUserId = null;
        session.IsChatActive = false;
        session.UpdatedAt = DateTimeOffset.UtcNow;
    }

    public static string HomeStateForRole(UserRole role)
    {
        return role switch
        {
            UserRole.Provider => BotStates.P_HOME,
            UserRole.Both => BotStates.C_HOME,
            _ => BotStates.C_HOME
        };
    }

    private static string NormalizeTenant(string? tenantId)
        => string.IsNullOrWhiteSpace(tenantId) ? "A" : tenantId.Trim();

    private static string BuildDisplayName(global::BotAgendamentoAI.Telegram.TelegramCompat.Types.User from)
    {
        var first = from.FirstName?.Trim() ?? string.Empty;
        var last = from.LastName?.Trim() ?? string.Empty;
        var full = $"{first} {last}".Trim();
        return string.IsNullOrWhiteSpace(full) ? "Usuario Telegram" : full;
    }
}
