using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Teamscop.Api.Data;
using Teamscop.Api.Services.Access;
using Teamscop.Engine.Lifecycle;

namespace Teamscop.Api.Hubs;

/// <summary>
/// Push channel for configuration that must reach clients without polling.
///
/// Three groups, deliberately separated by sensitivity:
///   staff:{userId}          — per-user. Tracking config and effective authorities.
///   company:{companyId}     — EVERY connection in the company. Non-sensitive company-wide
///                             settings only; the staff agent needs these (business clock).
///   company:{companyId}:mgmt — admins and team_management holders only. Payloads that REST
///                             restricts to those principals (org chart, policeman roster).
///
/// Splitting company from mgmt matters: scoping the whole company group to management cut
/// plain staff agents off from BusinessTimeUpdated, which every agent subscribes to.
/// </summary>
[Authorize]
public sealed class ConfigHub : Hub
{
    public static string StaffGroup(Guid staffUserId) => $"staff:{staffUserId:N}";

    /// <summary>Every connection in the company. Non-sensitive broadcasts only.</summary>
    public static string CompanyGroup(Guid companyId) => $"company:{companyId:N}";

    /// <summary>Admins and team_management holders. Privileged broadcasts.</summary>
    public static string CompanyManagementGroup(Guid companyId) => $"company:{companyId:N}:mgmt";

    public override async Task OnConnectedAsync()
    {
        var sub = Context.User?.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? Context.User?.FindFirstValue("sub");
        if (!Guid.TryParse(sub, out var userId))
        {
            await base.OnConnectedAsync();
            return;
        }

        await Groups.AddToGroupAsync(Context.ConnectionId, StaffGroup(userId));

        var company = Context.User?.FindFirstValue("company_id");
        if (Guid.TryParse(company, out var companyId))
        {
            // Everyone joins the company group — the staff agent needs the business clock.
            await Groups.AddToGroupAsync(Context.ConnectionId, CompanyGroup(companyId));

            if (await IsManagementAsync(userId))
            {
                await Groups.AddToGroupAsync(Context.ConnectionId, CompanyManagementGroup(companyId));
            }
        }

        await base.OnConnectedAsync();
    }

    private async Task<bool> IsManagementAsync(Guid userId)
    {
        var role = Context.User?.FindFirstValue(ClaimTypes.Role);
        if (string.Equals(role, UserRole.Admin.ToString(), StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var http = Context.GetHttpContext();
        if (http is null)
        {
            return false;
        }

        var access = http.RequestServices.GetRequiredService<IAccessPolicy>();
        return await access.HasAsync(userId, AuthorityPackageIds.TeamManagement, Context.ConnectionAborted);
    }
}
