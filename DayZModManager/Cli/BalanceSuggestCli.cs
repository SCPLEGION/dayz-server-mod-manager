using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Xml.Linq;
using DayZModManager.Models;
using DayZModManager.Services;

namespace DayZModManager.Cli;

/// <summary>
/// CLI entry: DayZModManager.exe balance-suggest &lt;typesXml&gt; [--api-key sk-...]
///   [--model gpt-5.4-nano] [--server-type PvE] [--concurrency 3] [--batch-size 30]
///   [--output suggestions.json]
/// Reads types.xml, runs the AI balancer, writes suggestions JSON.
/// </summary>
public static class BalanceSuggestCli
{
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool AttachConsole(int processId);

    public static int Run(string[] args)
    {
        try { AttachConsole(-1); } catch { /* ignore on non-windows or already attached */ }

        if (args.Length < 2)
        {
            WriteErr("Usage: DayZModManager.exe balance-suggest <typesXml> [--api-key ...] [--model ...] [--server-type ...] [--concurrency N] [--batch-size N] [--output suggestions.json]");
            return 1;
        }

        var typesPath = args[1];
        if (!File.Exists(typesPath))
        {
            WriteErr($"types.xml not found: {typesPath}");
            return 1;
        }

        var stored = AppConfigStore.Load().AiBalancer ?? new AiBalancerConfig();

        var apiKey = GetOpt(args, "--api-key") ?? ApiKeyProtection.Unprotect(stored.OpenAiApiKeyEncrypted);
        var model = GetOpt(args, "--model") ?? stored.OpenAiModel ?? "gpt-5.4-nano-2026-03-17";
        var serverType = GetOpt(args, "--server-type") ?? stored.ServerType ?? "PvE";
        var concurrencyStr = GetOpt(args, "--concurrency");
        var batchSizeStr = GetOpt(args, "--batch-size");
        var outFile = GetOpt(args, "--output") ?? "suggestions.json";

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            WriteErr("OpenAI API key not provided (use --api-key or set it in the GUI Settings).");
            return 1;
        }

        var concurrency = int.TryParse(concurrencyStr, out var ci) ? ci : stored.Concurrency;
        var batchSize = int.TryParse(batchSizeStr, out var bi) ? bi : stored.BatchSize;
        if (concurrency < 1) concurrency = 3;
        if (batchSize < 1) batchSize = 30;

        WriteOut($"Reading types.xml: {typesPath}");
        var items = ReadItemsFromTypesXml(typesPath);
        WriteOut($"  {items.Count} item(s) loaded.");

        var snapshot = new EconomySnapshot
        {
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            Items = items,
        };

        var opts = new AiBalancerOptions
        {
            ApiKey = apiKey,
            Model = model,
            Concurrency = concurrency,
            BatchSize = batchSize,
            ServerType = serverType,
        };

        var ai = new AiBalancerService();
        var progress = new Progress<BalancerProgress>(p =>
        {
            if (!string.IsNullOrEmpty(p.LogMessage)) WriteOut(p.LogMessage);
        });

        AiBalancerRunResult run;
        try
        {
            run = ai.RunAsync(snapshot, new List<EconomySnapshot> { snapshot }, opts, progress, CancellationToken.None)
                .GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            WriteErr("AI run failed: " + ex.Message);
            return 1;
        }

        WriteOut($"Suggestions: {run.Suggestions.Count}  |  Tokens: {run.TotalTokensUsed}  |  Errors: {run.TotalErrors}");

        try
        {
            var outPath = Path.IsPathRooted(outFile) ? outFile : Path.Combine(Environment.CurrentDirectory, outFile);
            var payload = run.Suggestions.Select(s => new
            {
                s.ClassName,
                s.Category,
                Reason = s.AiReason,
                Changes = s.Changes.ToDictionary(kv => kv.Key, kv => new { kv.Value.OldValue, kv.Value.NewValue }),
            });
            File.WriteAllText(outPath, JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
            WriteOut("Wrote: " + outPath);
            return 0;
        }
        catch (Exception ex)
        {
            WriteErr("Write failed: " + ex.Message);
            return 1;
        }
    }

    private static List<ItemEconomy> ReadItemsFromTypesXml(string path)
    {
        var items = new List<ItemEconomy>();
        try
        {
            var doc = XDocument.Load(path);
            var root = doc.Root;
            if (root == null) return items;

            foreach (var t in root.Elements("type"))
            {
                var name = t.Attribute("name")?.Value;
                if (string.IsNullOrEmpty(name)) continue;
                var item = new ItemEconomy
                {
                    ClassName = name,
                    Nominal = ParseInt(t.Element("nominal")?.Value),
                    Min = ParseInt(t.Element("min")?.Value),
                    Lifetime = ParseInt(t.Element("lifetime")?.Value),
                    Cost = ParseInt(t.Element("cost")?.Value),
                    Category = t.Element("category")?.Attribute("name")?.Value,
                };
                foreach (var u in t.Elements("usage"))
                {
                    var n = u.Attribute("name")?.Value;
                    if (!string.IsNullOrEmpty(n)) item.Usages.Add(n);
                }
                foreach (var v in t.Elements("value"))
                {
                    var n = v.Attribute("name")?.Value;
                    if (!string.IsNullOrEmpty(n)) item.Values.Add(n);
                }
                var flags = t.Element("flags");
                if (flags != null)
                {
                    item.Flags.Crafted = flags.Attribute("crafted")?.Value == "1";
                    item.Flags.Deloot = flags.Attribute("deloot")?.Value == "1";
                    item.Flags.CountInMap = flags.Attribute("count_in_map")?.Value == "1";
                    item.Flags.CountInCargo = flags.Attribute("count_in_cargo")?.Value == "1";
                    item.Flags.CountInHoarder = flags.Attribute("count_in_hoarder")?.Value == "1";
                    item.Flags.CountInPlayer = flags.Attribute("count_in_player")?.Value == "1";
                }
                items.Add(item);
            }
        }
        catch { /* return whatever parsed */ }
        return items;
    }

    private static int ParseInt(string? s) =>
        int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : 0;

    private static string? GetOpt(string[] args, string flag)
    {
        for (var i = 0; i < args.Length - 1; i++)
            if (string.Equals(args[i], flag, StringComparison.OrdinalIgnoreCase))
                return args[i + 1];
        return null;
    }

    private static void WriteOut(string s) => Console.Out.WriteLine("[balance-suggest] " + s);
    private static void WriteErr(string s) => Console.Error.WriteLine("[balance-suggest] " + s);
}
