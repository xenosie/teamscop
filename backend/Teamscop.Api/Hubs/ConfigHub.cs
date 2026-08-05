using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace Teamscop.Api.Hubs;

[Authorize]
public sealed class ConfigHub : Hub
{
    public static string StaffGroup(Guid staffUserId) => $"staff:{staffUserId:N}";
    public static string CompanyGroup(Guid companyId) => $"company:{companyId:N}";

    public override async Task OnConnectedAsync()
    {
        var sub = Context.User?.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? Context.User?.FindFirstValue("sub");
        if (Guid.TryParse(sub, out var userId))
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, StaffGroup(userId));
        }

        var company = Context.User?.FindFirstValue("company_id");
        if (Guid.TryParse(company, out var companyId))
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, CompanyGroup(companyId));
        }

        await base.OnConnectedAsync();
    }
}
