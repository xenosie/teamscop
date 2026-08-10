using Teamscop.Engine.Lifecycle;
using Teamscop.Engine.Sync;
using Teamscop.Engine.Usb;

namespace Teamscop.UninstallGuard;

/// <summary>
/// §7.4 — an authorised uninstall is an app-history event, so it has to reach the server. It is
/// enqueued to the same durable outbox everything else uses and then flushed once, briefly.
///
/// An offline uninstall's audit record is best-effort by physics: the machine is about to lose the
/// agent that would have retried. The event stays in <c>outbox\pending</c>, which the uninstaller
/// deliberately preserves, so a reinstall on the same device key still delivers it.
/// </summary>
internal static class UninstallAudit
{
    private static readonly TimeSpan FlushTimeout = TimeSpan.FromSeconds(8);

    public static async Task RecordAsync(string agentRoot, string? apiBaseUrl, string? accessToken, string? deviceKey)
    {
        var item = OutboxItem.Create(AgentEventTypes.Uninstall, new
        {
            kind = AgentEventTypes.Uninstall,
            reason = "authorized_uninstall",
            deviceKey,
            verifiedOffline = true
        });

        // Sent directly, ahead of the queue.
        //
        // This used to be enqueued to the shared outbox and flushed once. The outbox is strict FIFO
        // and a flush sends at most BatchSize items, so on a machine with any real backlog — which
        // is exactly the offline-then-uninstalling case — the uninstall record sat behind hundreds
        // of screenshots and never left. That made an authorised removal indistinguishable from a
        // machine that simply went quiet. A single-item POST does not care how deep the queue is.
        if (!string.IsNullOrWhiteSpace(apiBaseUrl) && !string.IsNullOrWhiteSpace(accessToken))
        {
            try
            {
                using var cts = new CancellationTokenSource(FlushTimeout);
                using var http = new HttpClient { Timeout = FlushTimeout };
                using var api = new SyncApiClient(apiBaseUrl, http);
                await api.PushBatchAsync(accessToken!, [item], cts.Token);
                return;
            }
            catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or IOException)
            {
                // Offline, or the API refused. Fall through to the durable queue.
            }
        }

        // Offline: keep it. The uninstaller preserves outbox\pending, so a reinstall on the same
        // device key still delivers it.
        try
        {
            IOutboxQueue outbox = new FileOutboxQueue(agentRoot);
            await outbox.EnqueueAsync(item);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The record is lost. Nothing further can be done from a machine being removed.
        }
    }

    /// <summary>
    /// The last authorized moment before the product is removed: give the machine its USB back and
    /// clear the leftover approval state. §6 removed the stored secret entirely — codes are derived
    /// from the device key on demand — so there is no credential to destroy here any more; only the
    /// USB gate (registry state that outlives an uninstall) and the replay/lockout scratch file are
    /// swept.
    /// </summary>
    public static void RestoreMachineAndForgetSecrets(string agentRoot)
    {
        try
        {
            UsbGateRestore.RestoreMachine();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or InvalidOperationException)
        {
            // Setup deletes the policy key as well, so USB still comes back.
        }

        foreach (var leftover in new[] { "approval-state.json", "business-clock.json" })
        {
            try
            {
                var path = Path.Combine(agentRoot, leftover);
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Harmless leftover; it carries no secret.
            }
        }
    }

    public static string ResolveAgentRoot(LocalAgentStore store)
        => Path.GetDirectoryName(store.StatePath)
           ?? Path.Combine(
               Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
               "Teamscop",
               "Agent");
}
