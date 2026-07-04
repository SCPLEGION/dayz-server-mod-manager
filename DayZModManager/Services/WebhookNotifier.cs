using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace DayZModManager.Services;

/// <summary>
/// Fire-and-forget POST of a Discord-style payload (<c>{"content": message}</c>) to a webhook
/// URL. Works with Discord's incoming webhooks and anything else that accepts that same shape;
/// other webhook receivers (e.g. Slack's "text" field) would need payload translation, which
/// this class does not do. Failures are swallowed — a notification going missing must never
/// affect server start/stop/restart/update flow.
/// </summary>
internal static class WebhookNotifier
{
    private static readonly HttpClient Http = new();

    public static async Task NotifyAsync(string? webhookUrl, string message)
    {
        if (string.IsNullOrWhiteSpace(webhookUrl)) return;
        try
        {
            var payload = JsonSerializer.Serialize(new { content = message });
            using var content = new StringContent(payload, Encoding.UTF8, "application/json");
            using var resp = await Http.PostAsync(webhookUrl, content).ConfigureAwait(false);
        }
        catch
        {
            // Best-effort notification only.
        }
    }
}
