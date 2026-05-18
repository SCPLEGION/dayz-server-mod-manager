using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using DayZModManager.Models;

namespace DayZModManager.Services;

public class AiTaskOptions
{
    public string ApiKey { get; set; } = string.Empty;
    public string Model { get; set; } = "gpt-5.4-nano-2026-03-17";
    public string ServerType { get; set; } = "PvE";
}

/// <summary>
/// Natural-language → structured task planner. Sends the user's request along with a snapshot of
/// the resolved server files to OpenAI and asks the model to call one of four tools:
/// <c>cfg_set</c>, <c>xml_set_value</c>, <c>text_replace</c>, or <c>run_balance</c>.
/// The collected tool calls become a <see cref="TaskProposal"/> the user can review and approve.
/// </summary>
public class AiTaskService
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(2) };

    public async Task<TaskProposal> ProposeAsync(
        string userRequest,
        ServerFilesSnapshot files,
        AiTaskOptions opts,
        IProgress<string>? log,
        CancellationToken ct)
    {
        var proposal = new TaskProposal
        {
            Title = Truncate(userRequest, 80),
        };

        if (string.IsNullOrWhiteSpace(opts.ApiKey))
        {
            proposal.Notes = "No OpenAI API key configured.";
            return proposal;
        }

        var systemPrompt = BuildSystemPrompt(files, opts.ServerType);
        var tools = BuildToolSchema();

        var requestBody = new
        {
            model = opts.Model,
            messages = new object[]
            {
                new { role = "system", content = systemPrompt },
                new { role = "user",   content = userRequest },
            },
            tools,
            tool_choice = "auto",
            temperature = 0.2,
        };

        log?.Report($"AI task: requesting plan from {opts.Model}");
        var json = JsonSerializer.Serialize(requestBody);
        using var req = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/chat/completions")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", opts.ApiKey);

        HttpResponseMessage resp;
        try { resp = await Http.SendAsync(req, ct).ConfigureAwait(false); }
        catch (Exception ex) { proposal.Notes = "HTTP error: " + ex.Message; return proposal; }

        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            proposal.Notes = $"OpenAI {(int)resp.StatusCode}: {Truncate(body, 400)}";
            log?.Report(proposal.Notes);
            return proposal;
        }

        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.TryGetProperty("usage", out var usage) && usage.TryGetProperty("total_tokens", out var tok))
                proposal.TokensUsed = tok.GetInt32();

            var choice = root.GetProperty("choices")[0].GetProperty("message");
            if (choice.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String)
            {
                proposal.Notes = content.GetString();
            }
            if (choice.TryGetProperty("tool_calls", out var calls) && calls.ValueKind == JsonValueKind.Array)
            {
                foreach (var call in calls.EnumerateArray())
                {
                    var action = ParseToolCall(call, files);
                    if (action != null) proposal.Actions.Add(action);
                }
            }
        }
        catch (Exception ex)
        {
            proposal.Notes = (proposal.Notes ?? "") + " parse error: " + ex.Message;
        }

        if (proposal.Actions.Count == 0 && string.IsNullOrEmpty(proposal.Notes))
            proposal.Notes = "(AI produced no actions)";

        log?.Report($"AI task: {proposal.Actions.Count} action(s), {proposal.TokensUsed} tokens");
        return proposal;
    }

    private static TaskAction? ParseToolCall(JsonElement call, ServerFilesSnapshot files)
    {
        if (!call.TryGetProperty("function", out var fn)) return null;
        var name = fn.GetProperty("name").GetString();
        var argsJson = fn.GetProperty("arguments").GetString() ?? "{}";
        JsonDocument argsDoc;
        try { argsDoc = JsonDocument.Parse(argsJson); }
        catch { return null; }
        var args = argsDoc.RootElement;

        switch (name)
        {
            case "cfg_set":
            {
                var key = TryStr(args, "key");
                var val = TryStr(args, "value");
                var reason = TryStr(args, "reason");
                if (string.IsNullOrEmpty(key)) return null;
                var target = files.Files.FirstOrDefault(f => f.Kind == ServerFileKind.ServerCfg)?.AbsolutePath;
                return new TaskAction
                {
                    Kind = TaskActionKind.CfgSet,
                    TargetFile = target,
                    Key = key,
                    NewValue = val ?? string.Empty,
                    Reason = reason,
                };
            }
            case "xml_set_value":
            {
                var file = TryStr(args, "file");
                var xpath = TryStr(args, "xpath");
                var val = TryStr(args, "value");
                var reason = TryStr(args, "reason");
                if (string.IsNullOrEmpty(file) || string.IsNullOrEmpty(xpath)) return null;
                var target = ResolveByName(files, file);
                return new TaskAction
                {
                    Kind = TaskActionKind.XmlSetValue,
                    TargetFile = target,
                    XPath = xpath,
                    NewValue = val ?? string.Empty,
                    Reason = reason,
                };
            }
            case "text_replace":
            {
                var file = TryStr(args, "file");
                var oldText = TryStr(args, "old_text");
                var newText = TryStr(args, "new_text");
                var reason = TryStr(args, "reason");
                if (string.IsNullOrEmpty(file) || string.IsNullOrEmpty(oldText)) return null;
                var target = ResolveByName(files, file);
                return new TaskAction
                {
                    Kind = TaskActionKind.TextReplace,
                    TargetFile = target,
                    OldText = oldText,
                    NewValue = newText ?? string.Empty,
                    Reason = reason,
                };
            }
            case "run_balance":
                return new TaskAction
                {
                    Kind = TaskActionKind.RunBalance,
                    Reason = TryStr(args, "reason") ?? "AI requested an economy rebalance",
                };
            case "note":
                return new TaskAction
                {
                    Kind = TaskActionKind.Note,
                    NewValue = TryStr(args, "text") ?? string.Empty,
                };
            default:
                return null;
        }
    }

    private static string? ResolveByName(ServerFilesSnapshot files, string fileName)
    {
        var match = files.Files.FirstOrDefault(f =>
            string.Equals(System.IO.Path.GetFileName(f.RelativePath), fileName, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(f.RelativePath, fileName, StringComparison.OrdinalIgnoreCase));
        return match?.AbsolutePath;
    }

    private static string? TryStr(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() : null;

    private static string Truncate(string s, int max) =>
        string.IsNullOrEmpty(s) ? s : (s.Length <= max ? s : s.Substring(0, max));

    private static string BuildSystemPrompt(ServerFilesSnapshot files, string serverType)
    {
        var sb = new StringBuilder();
        sb.AppendLine("You are an assistant for a DayZ server admin. Convert the user's natural-language request");
        sb.AppendLine("into a sequence of structured tool calls that modify the server files. Be conservative and");
        sb.AppendLine("only propose changes that match the request. If unsure, emit a 'note' tool call instead.");
        sb.AppendLine();
        sb.AppendLine("Server profile:");
        sb.AppendLine("  type: " + serverType);
        sb.AppendLine("  root: " + (files.ServerRoot ?? ""));
        sb.AppendLine("  mission: " + (files.MissionFolder ?? ""));
        sb.AppendLine();
        sb.AppendLine("Available files (use the file name when calling xml_set_value / text_replace):");
        foreach (var f in files.Files)
        {
            sb.Append("  - ").Append(System.IO.Path.GetFileName(f.RelativePath))
              .Append(" [").Append(f.Format).Append("]")
              .Append(f.Exists ? "" : " (missing)")
              .AppendLine();
        }
        sb.AppendLine();
        sb.AppendLine("Useful serverDZ.cfg keys (use cfg_set):");
        sb.AppendLine("  serverTimeAcceleration       (float, day-cycle multiplier; 1.0 = real time)");
        sb.AppendLine("  serverNightTimeAcceleration  (float, night multiplier on top of the day accel)");
        sb.AppendLine("  disableRespawnDialog, disable3rdPerson, allowFilePatching, maxPing,");
        sb.AppendLine("  motd[] / motdInterval, hostname, password, passwordAdmin, timeStampFormat.");
        sb.AppendLine();
        sb.AppendLine("Examples of common requests → tool calls:");
        sb.AppendLine("  \"make a full day 2 hours and night 15 minutes\" →");
        sb.AppendLine("     cfg_set(key=\"serverTimeAcceleration\", value=\"12\")           // 24h / 2h = 12x");
        sb.AppendLine("     cfg_set(key=\"serverNightTimeAcceleration\", value=\"4\")      // night 60min / 15min = 4x on top of day accel");
        sb.AppendLine("  \"rebalance loot for PvE\" → run_balance(reason=...)");
        sb.AppendLine("  \"raise zombie count\" → xml_set_value(file=\"globals.xml\", xpath=\"/globals/var[@name='ZombieMaxCount']\", value=\"...\")");
        return sb.ToString();
    }

    private static object[] BuildToolSchema()
    {
        return new object[]
        {
            new
            {
                type = "function",
                function = new
                {
                    name = "cfg_set",
                    description = "Set a key=value in serverDZ.cfg (e.g. serverTimeAcceleration).",
                    parameters = new
                    {
                        type = "object",
                        properties = new
                        {
                            key = new { type = "string", description = "Exact serverDZ.cfg key name." },
                            value = new { type = "string", description = "New value as a string. Numbers should be plain numerics; strings will be quoted automatically." },
                            reason = new { type = "string", description = "Short explanation of why this change satisfies the request." },
                        },
                        required = new[] { "key", "value" },
                    },
                },
            },
            new
            {
                type = "function",
                function = new
                {
                    name = "xml_set_value",
                    description = "Set the text or attribute value of an XML node identified by XPath. Use for events.xml, globals.xml, types.xml, etc.",
                    parameters = new
                    {
                        type = "object",
                        properties = new
                        {
                            file = new { type = "string", description = "File name, e.g. globals.xml, events.xml, types.xml." },
                            xpath = new { type = "string", description = "XPath selecting a single node or attribute (e.g. /globals/var[@name='ZombieMaxCount']/@value)." },
                            value = new { type = "string", description = "New value as a string." },
                            reason = new { type = "string" },
                        },
                        required = new[] { "file", "xpath", "value" },
                    },
                },
            },
            new
            {
                type = "function",
                function = new
                {
                    name = "text_replace",
                    description = "Replace a literal substring in a text file (init.c, json, etc.). Use sparingly — old_text must match exactly once.",
                    parameters = new
                    {
                        type = "object",
                        properties = new
                        {
                            file = new { type = "string" },
                            old_text = new { type = "string" },
                            new_text = new { type = "string" },
                            reason = new { type = "string" },
                        },
                        required = new[] { "file", "old_text", "new_text" },
                    },
                },
            },
            new
            {
                type = "function",
                function = new
                {
                    name = "run_balance",
                    description = "Trigger the AI economy balancer on the current snapshot.",
                    parameters = new
                    {
                        type = "object",
                        properties = new { reason = new { type = "string" } },
                    },
                },
            },
            new
            {
                type = "function",
                function = new
                {
                    name = "note",
                    description = "Add an informational note when no concrete action is possible.",
                    parameters = new
                    {
                        type = "object",
                        properties = new { text = new { type = "string" } },
                        required = new[] { "text" },
                    },
                },
            },
        };
    }
}
