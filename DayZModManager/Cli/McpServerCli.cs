using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using DayZModManager.Models;
using DayZModManager.Services;

namespace DayZModManager.Cli;

/// <summary>
/// Minimal Model Context Protocol (MCP) stdio server. Speaks line-delimited JSON-RPC 2.0 over
/// stdin/stdout so external AI clients (Claude Desktop, custom hosts) can drive the DayZ server
/// manager. Implements <c>initialize</c>, <c>tools/list</c>, and <c>tools/call</c>. The exposed
/// tools mirror the GUI capabilities: list/read/write server files, structured cfg get/set,
/// snapshot retrieval, and proposing AI balance changes.
/// </summary>
public static class McpServerCli
{
    private const string ProtocolVersion = "2024-11-05";
    private const string ServerName = "dayz-server-mod-manager";
    private const string ServerVersion = "0.1.0";

    private static readonly ServerFilesService Sf = new();
    private static readonly CfgFileService CfgSvc = new();

    public static int Run(string[] args)
    {
        // Stdout MUST stay clean (only JSON-RPC). Diagnostics go to stderr.
        Console.OpenStandardOutput();
        var stdout = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true };
        var stdin = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));
        Console.Error.WriteLine("[mcp] dayz-server-mod-manager MCP server starting...");

        string? line;
        while ((line = stdin.ReadLine()) != null)
        {
            line = line.Trim();
            if (line.Length == 0) continue;

            JsonObject? response;
            try { response = Handle(JsonNode.Parse(line)?.AsObject()); }
            catch (Exception ex)
            {
                Console.Error.WriteLine("[mcp] handler error: " + ex.Message);
                response = Error(null, -32603, "Internal error: " + ex.Message);
            }
            if (response != null)
                stdout.WriteLine(response.ToJsonString(new JsonSerializerOptions { WriteIndented = false }));
        }
        Console.Error.WriteLine("[mcp] stdin closed, shutting down.");
        return 0;
    }

    // ───────────────── JSON-RPC dispatch ─────────────────

    private static JsonObject? Handle(JsonObject? req)
    {
        if (req == null) return Error(null, -32600, "Invalid request");
        var id = req["id"];
        var method = req["method"]?.GetValue<string>();
        var paramsNode = req["params"] as JsonObject;

        // Notifications (no id) — process but never reply.
        if (id == null) return method switch
        {
            "notifications/initialized" => null,
            _ => null,
        };

        return method switch
        {
            "initialize"   => Success(id, Initialize()),
            "tools/list"   => Success(id, new JsonObject { ["tools"] = ToolDefinitions() }),
            "tools/call"   => Success(id, CallTool(paramsNode)),
            "ping"         => Success(id, new JsonObject()),
            _              => Error(id, -32601, "Method not found: " + method),
        };
    }

    private static JsonObject Initialize() => new()
    {
        ["protocolVersion"] = ProtocolVersion,
        ["capabilities"] = new JsonObject
        {
            ["tools"] = new JsonObject(),
        },
        ["serverInfo"] = new JsonObject
        {
            ["name"] = ServerName,
            ["version"] = ServerVersion,
        },
    };

    private static JsonObject Success(JsonNode? id, JsonNode? result) => new()
    {
        ["jsonrpc"] = "2.0",
        ["id"] = id?.DeepClone(),
        ["result"] = result,
    };

    private static JsonObject Error(JsonNode? id, int code, string message) => new()
    {
        ["jsonrpc"] = "2.0",
        ["id"] = id?.DeepClone(),
        ["error"] = new JsonObject { ["code"] = code, ["message"] = message },
    };

    // ───────────────── Tool registry ─────────────────

    private static JsonArray ToolDefinitions() => new()
    {
        Tool("list_server_files",
            "List all known DayZ server files (serverDZ.cfg, init.c, types.xml, events.xml, globals.xml, ...) with their resolved paths.",
            new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() }),

        Tool("read_server_file",
            "Read the text contents of one server file. Use either kind (enum) or file name.",
            new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["kind"] = new JsonObject { ["type"] = "string", ["description"] = "Optional: ServerCfg|InitC|TypesXml|EventsXml|GlobalsXml|SpawnableTypesXml|CfgEconomyCoreXml|MessagesXml|CfgEventSpawnsXml|CfgGameplayJson" },
                    ["file"] = new JsonObject { ["type"] = "string", ["description"] = "Optional: file name e.g. serverDZ.cfg" },
                },
            }),

        Tool("write_server_file",
            "Write text to a server file, creating a timestamped .backup copy first (unless backup=false).",
            new JsonObject
            {
                ["type"] = "object",
                ["required"] = new JsonArray { "contents" },
                ["properties"] = new JsonObject
                {
                    ["kind"] = new JsonObject { ["type"] = "string" },
                    ["file"] = new JsonObject { ["type"] = "string" },
                    ["contents"] = new JsonObject { ["type"] = "string" },
                    ["backup"] = new JsonObject { ["type"] = "boolean", ["default"] = true },
                },
            }),

        Tool("cfg_get",
            "Read a serverDZ.cfg key value, or all keys when 'key' is omitted.",
            new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["key"] = new JsonObject { ["type"] = "string" },
                },
            }),

        Tool("cfg_set",
            "Set one or more keys in serverDZ.cfg. Pass either {key,value} or {changes:{k:v,...}}.",
            new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["key"] = new JsonObject { ["type"] = "string" },
                    ["value"] = new JsonObject { ["type"] = "string" },
                    ["changes"] = new JsonObject { ["type"] = "object", ["additionalProperties"] = new JsonObject { ["type"] = "string" } },
                    ["backup"] = new JsonObject { ["type"] = "boolean", ["default"] = true },
                },
            }),

        Tool("get_latest_snapshot",
            "Return the most recent in-game economy snapshot persisted by the GUI listener.",
            new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() }),

        Tool("propose_balance",
            "Run the AI economy balancer on the latest snapshot and return its suggestions.",
            new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["model"] = new JsonObject { ["type"] = "string" },
                    ["concurrency"] = new JsonObject { ["type"] = "integer" },
                    ["batch_size"] = new JsonObject { ["type"] = "integer" },
                },
            }),

        Tool("get_config",
            "Return the saved AI balancer configuration (paths, model, server type, etc.). API key is NOT returned.",
            new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() }),
    };

    private static JsonObject Tool(string name, string description, JsonObject inputSchema) => new()
    {
        ["name"] = name,
        ["description"] = description,
        ["inputSchema"] = inputSchema,
    };

    // ───────────────── tools/call ─────────────────

    private static JsonObject CallTool(JsonObject? @params)
    {
        var name = @params?["name"]?.GetValue<string>() ?? "";
        var argsNode = @params?["arguments"] as JsonObject ?? new JsonObject();

        try
        {
            return name switch
            {
                "list_server_files"   => ToolResult(ListServerFiles()),
                "read_server_file"    => ToolResult(ReadServerFile(argsNode)),
                "write_server_file"   => ToolResult(WriteServerFile(argsNode)),
                "cfg_get"             => ToolResult(CfgGet(argsNode)),
                "cfg_set"             => ToolResult(CfgSet(argsNode)),
                "get_latest_snapshot" => ToolResult(GetLatestSnapshot()),
                "propose_balance"     => ToolResult(ProposeBalance(argsNode)),
                "get_config"          => ToolResult(GetConfig()),
                _ => ToolResult($"Unknown tool: {name}", isError: true),
            };
        }
        catch (Exception ex)
        {
            return ToolResult("Tool error: " + ex.Message, isError: true);
        }
    }

    private static JsonObject ToolResult(object payload, bool isError = false)
    {
        var text = payload is string s ? s : JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });
        return new JsonObject
        {
            ["content"] = new JsonArray
            {
                new JsonObject { ["type"] = "text", ["text"] = text },
            },
            ["isError"] = isError,
        };
    }

    // ───────────────── Tool implementations ─────────────────

    private static AiBalancerConfig LoadCfg() => AppConfigStore.Load().AiBalancer ?? new AiBalancerConfig();

    private static object ListServerFiles()
    {
        var cfg = LoadCfg();
        var snap = Sf.Discover(cfg.ServerRootPath, cfg.MissionPath);
        return new
        {
            serverRoot = snap.ServerRoot,
            missionFolder = snap.MissionFolder,
            files = snap.Files.Select(f => new
            {
                kind = f.Kind.ToString(),
                format = f.Format.ToString(),
                relativePath = f.RelativePath,
                absolutePath = f.AbsolutePath,
                exists = f.Exists,
                sizeBytes = f.SizeBytes,
                missionScoped = f.IsMissionScoped,
            }),
        };
    }

    private static object ReadServerFile(JsonObject args)
    {
        var (info, snap) = ResolveFile(args);
        if (info == null || !info.Exists)
            return new { error = "File not found in registry or on disk.", snapshot = SnapshotMeta(snap) };
        var text = File.ReadAllText(info.AbsolutePath);
        return new
        {
            kind = info.Kind.ToString(),
            absolutePath = info.AbsolutePath,
            sizeBytes = info.SizeBytes,
            contents = text,
        };
    }

    private static object WriteServerFile(JsonObject args)
    {
        var (info, _) = ResolveFile(args);
        if (info == null) return new { error = "Unknown file." };
        var contents = args["contents"]?.GetValue<string>() ?? "";
        var backup = args["backup"]?.GetValue<bool>() ?? true;
        var backupPath = Sf.WriteText(info, contents, backup);
        return new
        {
            written = true,
            absolutePath = info.AbsolutePath,
            backupPath,
        };
    }

    private static object CfgGet(JsonObject args)
    {
        var info = FindServerCfg(out var error);
        if (info == null) return new { error };
        var dict = CfgSvc.ReadFile(info.AbsolutePath);
        if (args["key"] is JsonNode keyNode)
        {
            var k = keyNode.GetValue<string>();
            return dict.TryGetValue(k, out var v)
                ? (object)new { key = k, value = v, path = info.AbsolutePath }
                : new { key = k, value = (string?)null, path = info.AbsolutePath };
        }
        return new { path = info.AbsolutePath, keys = dict };
    }

    private static object CfgSet(JsonObject args)
    {
        var info = FindServerCfg(out var error);
        if (info == null) return new { error };
        var changes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (args["changes"] is JsonObject c)
            foreach (var kv in c)
                changes[kv.Key] = kv.Value?.GetValue<string>() ?? "";
        if (args["key"] is JsonNode k && args["value"] is JsonNode v)
            changes[k.GetValue<string>()] = v.GetValue<string>();
        if (changes.Count == 0) return new { error = "No changes provided." };
        var backup = args["backup"]?.GetValue<bool>() ?? true;
        var result = CfgSvc.ApplyChanges(info.AbsolutePath, changes, backup);
        return new
        {
            applied = result.Applied,
            skipped = result.Skipped,
            errors = result.Errors,
            backupPath = result.BackupPath,
            path = info.AbsolutePath,
        };
    }

    private static object GetLatestSnapshot()
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "DayZModManager", "snapshots");
        if (!Directory.Exists(dir)) return new { error = "No snapshots yet." };
        var latest = new DirectoryInfo(dir).GetFiles("*.json")
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .FirstOrDefault();
        if (latest == null) return new { error = "No snapshots yet." };
        var text = File.ReadAllText(latest.FullName);
        return new { path = latest.FullName, snapshot = JsonNode.Parse(text) };
    }

    private static object ProposeBalance(JsonObject args)
    {
        var cfg = LoadCfg();
        var apiKey = ApiKeyProtection.Unprotect(cfg.OpenAiApiKeyEncrypted);
        if (string.IsNullOrWhiteSpace(apiKey)) return new { error = "OpenAI API key not configured in GUI Settings." };

        var snap = LoadLatestSnapshotObject();
        if (snap == null) return new { error = "No snapshot available. Start the GUI listener first." };

        var opts = new AiBalancerOptions
        {
            ApiKey = apiKey,
            Model = args["model"]?.GetValue<string>() ?? cfg.OpenAiModel,
            Concurrency = args["concurrency"]?.GetValue<int>() ?? cfg.Concurrency,
            BatchSize = args["batch_size"]?.GetValue<int>() ?? cfg.BatchSize,
            ServerType = cfg.ServerType,
        };

        var ai = new AiBalancerService();
        var run = ai.RunAsync(snap, new List<EconomySnapshot> { snap }, opts, null, CancellationToken.None)
            .GetAwaiter().GetResult();
        return new
        {
            tokensUsed = run.TotalTokensUsed,
            errors = run.TotalErrors,
            suggestions = run.Suggestions.Select(s => new
            {
                s.ClassName,
                s.Category,
                reason = s.AiReason,
                changes = s.Changes.ToDictionary(kv => kv.Key, kv => new { kv.Value.OldValue, kv.Value.NewValue }),
            }),
        };
    }

    private static object GetConfig()
    {
        var cfg = LoadCfg();
        return new
        {
            cfg.ListenerPort,
            cfg.OpenAiModel,
            cfg.ServerType,
            cfg.Concurrency,
            cfg.BatchSize,
            cfg.ServerRootPath,
            cfg.MissionPath,
            cfg.TypesXmlPath,
            cfg.EventsXmlPath,
            cfg.GlobalsXmlPath,
            cfg.SpawnableTypesXmlPath,
            cfg.BackupBeforeApply,
            hasApiKey = !string.IsNullOrEmpty(cfg.OpenAiApiKeyEncrypted),
        };
    }

    // ───────────────── Helpers ─────────────────

    private static (ServerFileInfo? info, ServerFilesSnapshot snap) ResolveFile(JsonObject args)
    {
        var cfg = LoadCfg();
        var snap = Sf.Discover(cfg.ServerRootPath, cfg.MissionPath);
        ServerFileInfo? info = null;
        if (args["kind"] is JsonNode kn && Enum.TryParse<ServerFileKind>(kn.GetValue<string>(), ignoreCase: true, out var kind))
            info = snap.Files.FirstOrDefault(f => f.Kind == kind);
        if (info == null && args["file"] is JsonNode fn)
        {
            var fname = fn.GetValue<string>();
            info = snap.Files.FirstOrDefault(f =>
                string.Equals(Path.GetFileName(f.RelativePath), fname, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(f.RelativePath, fname, StringComparison.OrdinalIgnoreCase));
        }
        return (info, snap);
    }

    private static ServerFileInfo? FindServerCfg(out string? error)
    {
        var cfg = LoadCfg();
        var snap = Sf.Discover(cfg.ServerRootPath, cfg.MissionPath);
        var info = snap.Files.FirstOrDefault(f => f.Kind == ServerFileKind.ServerCfg);
        if (info == null || !info.Exists)
        {
            error = "serverDZ.cfg not found. Set the server root in GUI Settings.";
            return null;
        }
        error = null;
        return info;
    }

    private static EconomySnapshot? LoadLatestSnapshotObject()
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "DayZModManager", "snapshots");
        if (!Directory.Exists(dir)) return null;
        var latest = new DirectoryInfo(dir).GetFiles("*.json")
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .FirstOrDefault();
        if (latest == null) return null;
        try
        {
            return JsonSerializer.Deserialize<EconomySnapshot>(File.ReadAllText(latest.FullName),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch { return null; }
    }

    private static object SnapshotMeta(ServerFilesSnapshot snap) => new
    {
        serverRoot = snap.ServerRoot,
        missionFolder = snap.MissionFolder,
        knownFiles = snap.Files.Count,
    };
}
