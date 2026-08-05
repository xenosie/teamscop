using Teamscop.Engine.Auth;
using Teamscop.Engine.Lifecycle;

namespace Teamscop.AdminHost;

/// <summary>
/// Admin desktop host. Close / Ctrl+C fully ends this process.
/// Per-staff TOTP + flexible team/org structure management.
/// </summary>
public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        var policy = RolePolicy.For(AgentRole.Admin);
        Console.WriteLine("Teamscop Admin Host");
        Console.WriteLine($"Role policy: AllowClose={policy.AllowUserClose} InstallRoot={policy.RecommendedInstallRoot}");
        Console.WriteLine("Close this window or press Ctrl+C to exit completely.");

        var apiBase = args.FirstOrDefault(a => a.StartsWith("--api=", StringComparison.OrdinalIgnoreCase))
            ?[6..] ?? "https://teamscop.com";
        var store = new LocalAgentStore(AgentRole.Admin);
        var state = store.Load();
        state.ApiBaseUrl ??= apiBase;
        state.Role = "admin";

        if (string.IsNullOrWhiteSpace(state.DeviceKey))
        {
            state.DeviceKey = new DeviceKeyProvider().GetDeviceKey();
            store.Save(state);
        }

        Console.WriteLine($"DeviceKey: {state.DeviceKey}");
        Console.WriteLine($"State file: {store.StatePath}");
        Console.WriteLine();
        Console.WriteLine("Commands:");
        Console.WriteLine("  staff                              — list staff + TOTP status");
        Console.WriteLine("  enroll-totp <staffId>              — enroll per-staff TOTP (USB + uninstall)");
        Console.WriteLine("  code <staffId>                     — generate current 6-digit code");
        Console.WriteLine("  org                                — show company teams structure");
        Console.WriteLine("  team-create <name> <leaderId>      — create team with leader");
        Console.WriteLine("  team-rename <teamId> <name>        — rename team");
        Console.WriteLine("  team-leader <teamId> <staffId>     — change team leader");
        Console.WriteLine("  team-members <teamId> <id> [id…]   — replace member list");
        Console.WriteLine("  team-add <teamId> <staffId>        — add one member");
        Console.WriteLine("  team-remove <teamId> <staffId>     — remove one member");
        Console.WriteLine("  team-delete <teamId>               — delete team");
        Console.WriteLine("  packages                           — list authority packages");
        Console.WriteLine("  police                             — list policemen + packages");
        Console.WriteLine("  police-set <staffId> <pkg> [pkg…]  — make/update policeman");
        Console.WriteLine("  police-revoke <staffId>            — revoke policeman");
        Console.WriteLine("  quit");

        using var lifecycle = new LifecycleApiClient(state.ApiBaseUrl ?? apiBase);
        using var org = new OrgApiClient(state.ApiBaseUrl ?? apiBase);
        using var police = new PoliceApiClient(state.ApiBaseUrl ?? apiBase);
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };

        while (!cts.IsCancellationRequested)
        {
            Console.Write("> ");
            var line = Console.ReadLine();
            if (line is null || cts.IsCancellationRequested)
            {
                break;
            }

            var parts = line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0)
            {
                continue;
            }

            var cmd = parts[0].ToLowerInvariant();
            if (cmd is "quit" or "exit" or "close")
            {
                break;
            }

            var needsAuth = cmd is "staff" or "enroll-totp" or "code" or "org"
                or "team-create" or "team-rename" or "team-leader" or "team-members"
                or "team-add" or "team-remove" or "team-delete"
                or "packages" or "police" or "police-set" or "police-revoke";
            if (string.IsNullOrWhiteSpace(state.AccessToken) && needsAuth)
            {
                Console.WriteLine("Login first (set AccessToken in agent-state.json after auth).");
                continue;
            }

            try
            {
                if (cmd == "staff")
                {
                    var list = await lifecycle.ListStaffTotpAsync(state.AccessToken!, cts.Token);
                    if (list.Count == 0)
                    {
                        Console.WriteLine("(no staff)");
                        continue;
                    }

                    foreach (var s in list)
                    {
                        Console.WriteLine(
                            $"{s.StaffUserId}  {s.StaffUsername,-20}  totp={(s.Enabled ? "enrolled" : "missing")}");
                    }

                    continue;
                }

                if (cmd == "enroll-totp")
                {
                    if (parts.Length < 2 || !Guid.TryParse(parts[1], out var staffId))
                    {
                        Console.WriteLine("Usage: enroll-totp <staffUserId>");
                        continue;
                    }

                    var enroll = await lifecycle.EnrollTotpAsync(state.AccessToken!, staffId, cts.Token);
                    Console.WriteLine($"TOTP enrolled for {enroll.StaffUsername} (USB + uninstall).");
                    Console.WriteLine("Add to authenticator OR use `code <staffId>` generator:");
                    Console.WriteLine(enroll.OtpAuthUri);
                    Console.WriteLine($"Secret: {enroll.Secret}");
                    continue;
                }

                if (cmd == "code")
                {
                    if (parts.Length < 2 || !Guid.TryParse(parts[1], out var staffId))
                    {
                        Console.WriteLine("Usage: code <staffId>");
                        continue;
                    }

                    var code = await lifecycle.GetTotpCodeAsync(state.AccessToken!, staffId, cts.Token);
                    Console.WriteLine(
                        $"{code.StaffUsername}: {code.Code}  (valid ~{code.RemainingSeconds}s) — USB or uninstall");
                    continue;
                }

                if (cmd == "org")
                {
                    var structure = await org.GetStructureAsync(state.AccessToken!, cts.Token);
                    Console.WriteLine($"Company: {structure.CompanyName}  v{structure.StructureVersion}");
                    foreach (var team in structure.Teams)
                    {
                        Console.WriteLine($"  [{team.TeamId}] {team.Name}");
                        Console.WriteLine($"     Leader: {team.Leader.Username} ({team.Leader.UserId})");
                        foreach (var m in team.Members)
                        {
                            Console.WriteLine($"     Member: {m.Username} ({m.UserId})");
                        }
                    }

                    Console.WriteLine("  Unassigned:");
                    foreach (var u in structure.UnassignedStaff)
                    {
                        Console.WriteLine($"     {u.Username} ({u.UserId})");
                    }

                    continue;
                }

                if (cmd == "team-create")
                {
                    if (parts.Length < 3 || !Guid.TryParse(parts[^1], out var leaderId))
                    {
                        Console.WriteLine("Usage: team-create <name> <leaderId>");
                        continue;
                    }

                    var name = string.Join(' ', parts[1..^1]);
                    var team = await org.CreateTeamAsync(state.AccessToken!, name, leaderId, cts.Token);
                    Console.WriteLine($"Created team {team.Name} ({team.TeamId}) leader={team.Leader.Username}");
                    continue;
                }

                if (cmd == "team-rename")
                {
                    if (parts.Length < 3 || !Guid.TryParse(parts[1], out var teamId))
                    {
                        Console.WriteLine("Usage: team-rename <teamId> <name>");
                        continue;
                    }

                    var name = string.Join(' ', parts[2..]);
                    var team = await org.UpdateTeamAsync(state.AccessToken!, teamId, name, null, cts.Token);
                    Console.WriteLine($"Renamed → {team.Name}");
                    continue;
                }

                if (cmd == "team-leader")
                {
                    if (parts.Length < 3
                        || !Guid.TryParse(parts[1], out var teamId)
                        || !Guid.TryParse(parts[2], out var leaderId))
                    {
                        Console.WriteLine("Usage: team-leader <teamId> <staffId>");
                        continue;
                    }

                    var team = await org.UpdateTeamAsync(state.AccessToken!, teamId, null, leaderId, cts.Token);
                    Console.WriteLine($"Leader → {team.Leader.Username}");
                    continue;
                }

                if (cmd == "team-members")
                {
                    if (parts.Length < 2 || !Guid.TryParse(parts[1], out var teamId))
                    {
                        Console.WriteLine("Usage: team-members <teamId> <staffId> [staffId…]");
                        continue;
                    }

                    var ids = parts.Skip(2).Select(Guid.Parse).ToList();
                    var team = await org.SetMembersAsync(state.AccessToken!, teamId, ids, cts.Token);
                    Console.WriteLine($"Members set ({team.Members.Count}): {string.Join(", ", team.Members.Select(m => m.Username))}");
                    continue;
                }

                if (cmd == "team-add")
                {
                    if (parts.Length < 3
                        || !Guid.TryParse(parts[1], out var teamId)
                        || !Guid.TryParse(parts[2], out var staffId))
                    {
                        Console.WriteLine("Usage: team-add <teamId> <staffId>");
                        continue;
                    }

                    var team = await org.AddMemberAsync(state.AccessToken!, teamId, staffId, cts.Token);
                    Console.WriteLine($"Members now: {string.Join(", ", team.Members.Select(m => m.Username))}");
                    continue;
                }

                if (cmd == "team-remove")
                {
                    if (parts.Length < 3
                        || !Guid.TryParse(parts[1], out var teamId)
                        || !Guid.TryParse(parts[2], out var staffId))
                    {
                        Console.WriteLine("Usage: team-remove <teamId> <staffId>");
                        continue;
                    }

                    var team = await org.RemoveMemberAsync(state.AccessToken!, teamId, staffId, cts.Token);
                    Console.WriteLine($"Members now: {string.Join(", ", team.Members.Select(m => m.Username))}");
                    continue;
                }

                if (cmd == "team-delete")
                {
                    if (parts.Length < 2 || !Guid.TryParse(parts[1], out var teamId))
                    {
                        Console.WriteLine("Usage: team-delete <teamId>");
                        continue;
                    }

                    await org.DeleteTeamAsync(state.AccessToken!, teamId, cts.Token);
                    Console.WriteLine("Team deleted.");
                    continue;
                }

                if (cmd == "packages")
                {
                    foreach (var p in await police.ListPackagesAsync(state.AccessToken!, cts.Token))
                    {
                        Console.WriteLine($"  {p.Id,-24} {p.Label}");
                    }

                    continue;
                }

                if (cmd == "police")
                {
                    var list = await police.ListPolicemenAsync(state.AccessToken!, cts.Token);
                    if (list.Count == 0)
                    {
                        Console.WriteLine("(no policemen)");
                        continue;
                    }

                    foreach (var c in list)
                    {
                        Console.WriteLine($"{c.StaffUserId}  {c.Username,-20}  [{string.Join(',', c.Packages)}]");
                    }

                    continue;
                }

                if (cmd == "police-set")
                {
                    if (parts.Length < 3 || !Guid.TryParse(parts[1], out var staffId))
                    {
                        Console.WriteLine("Usage: police-set <staffId> <packageId> [packageId…]");
                        Console.WriteLine("Packages: " + string.Join(", ", AuthorityPackageIds.All));
                        continue;
                    }

                    var pkgs = parts.Skip(2).ToList();
                    var result = await police.UpsertPolicemanAsync(state.AccessToken!, staffId, pkgs, cts.Token);
                    Console.WriteLine($"Policeman {result.Username}: [{string.Join(',', result.Packages)}]");
                    continue;
                }

                if (cmd == "police-revoke")
                {
                    if (parts.Length < 2 || !Guid.TryParse(parts[1], out var staffId))
                    {
                        Console.WriteLine("Usage: police-revoke <staffId>");
                        continue;
                    }

                    await police.RevokePolicemanAsync(state.AccessToken!, staffId, cts.Token);
                    Console.WriteLine("Policeman revoked.");
                    continue;
                }

                if (cmd == "status")
                {
                    Console.WriteLine($"Token present: {!string.IsNullOrWhiteSpace(state.AccessToken)}");
                    continue;
                }

                Console.WriteLine("Unknown command.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed: {ex.Message}");
            }
        }

        Console.WriteLine("Admin host exiting.");
        return 0;
    }
}
