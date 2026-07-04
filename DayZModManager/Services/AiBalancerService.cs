using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using DayZModManager.Models;

namespace DayZModManager.Services;

public class AiBalancerOptions
{
    public string ApiKey { get; set; } = string.Empty;
    public string Model { get; set; } = "gpt-5.4-nano-2026-03-17";
    public int Concurrency { get; set; } = 3;
    public int BatchSize { get; set; } = 30;
    public string ServerType { get; set; } = "PvE";
    public string Endpoint { get; set; } = "https://api.openai.com/v1/chat/completions";
}

public class AiBalancerRunResult
{
    public List<BalanceSuggestion> Suggestions { get; } = new();
    public int TotalTokensUsed { get; set; }
    public int TotalErrors { get; set; }
}

/// <summary>One zombie or animal spawn group's current live snapshot, used as AI prompt input.</summary>
internal sealed record SpawnGroupInput(
    string Kind, string ClassName, string EventName,
    int Alive, int Nominal, int Min, int Max, int Lifetime);

public sealed class AiBalancerService
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(2) };
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>Run AI balancing across all items, reporting progress per worker.</summary>
    public async Task<AiBalancerRunResult> RunAsync(
        EconomySnapshot snapshot,
        IReadOnlyList<EconomySnapshot> history,
        AiBalancerOptions options,
        IProgress<BalancerProgress>? progress,
        CancellationToken cancel)
    {
        if (snapshot?.Items == null || snapshot.Items.Count == 0)
            return new AiBalancerRunResult();

        if (string.IsNullOrWhiteSpace(options.ApiKey))
            throw new InvalidOperationException("OpenAI API key is required.");

        var batches = ItemGrouper.BuildBatches(snapshot.Items, options.BatchSize);
        var totalBatches = batches.Count;
        var completed = 0;
        var active = 0;
        var totalModified = 0;
        var totalErrors = 0;
        var totalTokens = 0;

        var result = new AiBalancerRunResult();
        var resultLock = new object();

        var sem = new SemaphoreSlim(Math.Max(1, options.Concurrency));
        var tasks = new List<Task>();

        var historyByClass = BuildHistoryIndex(history);

        for (var i = 0; i < batches.Count; i++)
        {
            await sem.WaitAsync(cancel).ConfigureAwait(false);
            cancel.ThrowIfCancellationRequested();

            var batchIndex = i;
            var batch = batches[i];
            var workerIndex = Interlocked.Increment(ref active);

            tasks.Add(Task.Run(async () =>
            {
                try
                {
                    Report(progress, totalBatches, completed, active, totalModified, totalErrors, totalTokens,
                        $"Batch {batchIndex + 1}/{totalBatches} \u2192 {batch.Items.Count} items ({batch.Group})...",
                        workerIndex, WorkerStatus.Running, batchIndex, 10);

                    var (items, tokens) = await CallOpenAiAsync(batch.Items, snapshot, historyByClass, options, cancel)
                        .ConfigureAwait(false);

                    var changes = 0;
                    lock (resultLock)
                    {
                        foreach (var ai in items)
                        {
                            var src = batch.Items.FirstOrDefault(b =>
                                string.Equals(b.ClassName, ai.ClassName, StringComparison.OrdinalIgnoreCase));
                            if (src == null) continue;

                            var sug = new BalanceSuggestion
                            {
                                ClassName = ai.ClassName,
                                Category = src.Category,
                                AiReason = ai.Reason,
                            };
                            AddChange(sug, "nominal", src.Nominal, ai.Nominal);
                            AddChange(sug, "min", src.Min, ai.Min);
                            AddChange(sug, "cost", src.Cost, ai.Cost);
                            AddChange(sug, "lifetime", src.Lifetime, ai.Lifetime);

                            if (sug.Changes.Count > 0)
                            {
                                result.Suggestions.Add(sug);
                                changes++;
                            }
                        }
                        result.TotalTokensUsed += tokens;
                    }

                    Interlocked.Add(ref totalModified, changes);
                    Interlocked.Add(ref totalTokens, tokens);
                    var done = Interlocked.Increment(ref completed);
                    Report(progress, totalBatches, done, active, totalModified, totalErrors, totalTokens,
                        $"Batch {batchIndex + 1}/{totalBatches} OK \u2014 {changes} change(s).",
                        workerIndex, WorkerStatus.Done, batchIndex, 100);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    Interlocked.Increment(ref totalErrors);
                    lock (resultLock) result.TotalErrors++;
                    var done = Interlocked.Increment(ref completed);
                    Report(progress, totalBatches, done, active, totalModified, totalErrors, totalTokens,
                        $"Batch {batchIndex + 1}/{totalBatches} ERROR: {ex.Message}",
                        workerIndex, WorkerStatus.Error, batchIndex, 100);
                }
                finally
                {
                    Interlocked.Decrement(ref active);
                    sem.Release();
                }
            }, cancel));
        }

        await Task.WhenAll(tasks).ConfigureAwait(false);

        var spawnInputs = BuildSpawnGroupInputs(snapshot);
        if (spawnInputs.Count > 0)
        {
            try
            {
                var (spawnItems, spawnTokens) = await CallOpenAiForSpawnGroupsAsync(spawnInputs, options, cancel)
                    .ConfigureAwait(false);
                result.TotalTokensUsed += spawnTokens;

                foreach (var ai in spawnItems)
                {
                    var src = spawnInputs.FirstOrDefault(s =>
                        string.Equals(s.ClassName, ai.ClassName, StringComparison.OrdinalIgnoreCase));
                    if (src == null || string.IsNullOrEmpty(src.EventName)) continue;

                    var sug = new BalanceSuggestion
                    {
                        ClassName = src.ClassName,
                        Category = src.Kind,
                        AiReason = ai.Reason,
                        Target = SuggestionTarget.EventsXml,
                        EventName = src.EventName,
                    };
                    AddChange(sug, "nominal", src.Nominal, ai.Nominal);
                    AddChange(sug, "min", src.Min, ai.Min);
                    AddChange(sug, "max", src.Max, ai.Max);
                    AddChange(sug, "lifetime", src.Lifetime, ai.Lifetime);

                    if (sug.Changes.Count > 0)
                        result.Suggestions.Add(sug);
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception)
            {
                result.TotalErrors++;
            }
        }

        return result;
    }

    private static List<SpawnGroupInput> BuildSpawnGroupInputs(EconomySnapshot snapshot)
    {
        var list = new List<SpawnGroupInput>();

        if (snapshot.Zombies?.TypeBreakdown != null)
            foreach (var z in snapshot.Zombies.TypeBreakdown)
                if (!string.IsNullOrEmpty(z.ClassName))
                    list.Add(new SpawnGroupInput("zombie", z.ClassName, z.EventName, z.Alive, z.Nominal, z.Min, z.Max, z.Lifetime));

        if (snapshot.Animals?.TypeBreakdown != null)
            foreach (var a in snapshot.Animals.TypeBreakdown)
                if (!string.IsNullOrEmpty(a.ClassName))
                    list.Add(new SpawnGroupInput("animal", a.ClassName, a.EventName, a.Alive, a.Nominal, a.Min, a.Max, a.Lifetime));

        return list;
    }

    private async Task<(List<AiSpawnGroupItem> items, int tokens)> CallOpenAiForSpawnGroupsAsync(
        List<SpawnGroupInput> inputs, AiBalancerOptions options, CancellationToken cancel)
    {
        var systemPrompt = BuildSpawnGroupSystemPrompt(options.ServerType);
        var userPayload = JsonSerializer.Serialize(new
        {
            groups = inputs.Select(i => new
            {
                kind = i.Kind,
                className = i.ClassName,
                eventName = i.EventName,
                current = new { alive = i.Alive, nominal = i.Nominal, min = i.Min, max = i.Max, lifetime = i.Lifetime },
            })
        }, JsonOptions);

        var requestBody = new
        {
            model = options.Model,
            messages = new object[]
            {
                new { role = "system", content = systemPrompt },
                new { role = "user", content = userPayload },
            },
            response_format = new { type = "json_object" },
        };

        var bodyJson = JsonSerializer.Serialize(requestBody, JsonOptions);

        var attempts = 0;
        while (true)
        {
            attempts++;
            using var req = new HttpRequestMessage(HttpMethod.Post, options.Endpoint);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.ApiKey);
            req.Content = new StringContent(bodyJson, Encoding.UTF8, "application/json");

            using var resp = await Http.SendAsync(req, cancel).ConfigureAwait(false);
            var text = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);

            if ((int)resp.StatusCode == 429 && attempts < 4)
            {
                var delay = TimeSpan.FromSeconds(Math.Pow(2, attempts));
                await Task.Delay(delay, cancel).ConfigureAwait(false);
                continue;
            }
            if (!resp.IsSuccessStatusCode)
                throw new InvalidOperationException($"OpenAI HTTP {(int)resp.StatusCode}: {Trunc(text, 240)}");

            return ParseSpawnGroupResponse(text);
        }
    }

    private static string BuildSpawnGroupSystemPrompt(string serverType) => $@"You are an expert DayZ server economy balancer, focused on zombie and animal spawn events (events.xml).
Server type: {serverType}

Analyze each spawn group's current alive count vs its configured nominal/min/max, and suggest adjustments.

RULES:
- alive/nominal ratio consistently near or above 1.0: consider raising nominal/max slightly if the server can handle the load
- alive consistently far below nominal: reduce nominal, or flag a possible min/max/lifetime mismatch
- min should stay below nominal; max should stay at or above nominal
- lifetime is how long the entity persists before despawn (seconds) - leave unchanged (omit from the response) unless there's a clear, justified reason to change it
- be conservative: prefer small adjustments (10-20%) unless the deviation is severe

OUTPUT: A JSON object with key 'groups' whose value is an array. No markdown, no commentary outside 'reason'.
Only include groups that need changes. Omit balanced groups.
Example: {{ ""groups"": [{{ ""className"":""ZmbM_HermitCitizen4_Autumn"", ""nominal"":9, ""min"":6, ""max"":12, ""reason"":""..."" }}] }}";

    private static (List<AiSpawnGroupItem> items, int tokens) ParseSpawnGroupResponse(string raw)
    {
        using var doc = JsonDocument.Parse(raw);
        var root = doc.RootElement;

        var tokens = 0;
        if (root.TryGetProperty("usage", out var usage) &&
            usage.TryGetProperty("total_tokens", out var tt) &&
            tt.ValueKind == JsonValueKind.Number)
            tokens = tt.GetInt32();

        var items = new List<AiSpawnGroupItem>();
        if (!root.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0)
            return (items, tokens);

        var content = choices[0].GetProperty("message").GetProperty("content").GetString() ?? string.Empty;
        var cleaned = StripCodeFences(content).Trim();
        if (string.IsNullOrEmpty(cleaned)) return (items, tokens);

        try
        {
            using var inner = JsonDocument.Parse(cleaned);
            JsonElement arr;
            var hasArr = false;

            if (inner.RootElement.ValueKind == JsonValueKind.Object &&
                inner.RootElement.TryGetProperty("groups", out arr) && arr.ValueKind == JsonValueKind.Array)
            {
                hasArr = true;
            }
            else if (inner.RootElement.ValueKind == JsonValueKind.Array)
            {
                arr = inner.RootElement;
                hasArr = true;
            }
            else
            {
                arr = default;
            }

            if (hasArr)
            {
                foreach (var el in arr.EnumerateArray())
                {
                    if (el.ValueKind != JsonValueKind.Object) continue;
                    var item = new AiSpawnGroupItem();
                    if (el.TryGetProperty("className", out var c) && c.ValueKind == JsonValueKind.String)
                        item.ClassName = c.GetString() ?? string.Empty;
                    if (string.IsNullOrEmpty(item.ClassName)) continue;
                    if (el.TryGetProperty("nominal", out var n) && n.ValueKind == JsonValueKind.Number) item.Nominal = n.GetInt32();
                    if (el.TryGetProperty("min", out var m) && m.ValueKind == JsonValueKind.Number) item.Min = m.GetInt32();
                    if (el.TryGetProperty("max", out var mx) && mx.ValueKind == JsonValueKind.Number) item.Max = mx.GetInt32();
                    if (el.TryGetProperty("lifetime", out var l) && l.ValueKind == JsonValueKind.Number) item.Lifetime = l.GetInt32();
                    if (el.TryGetProperty("reason", out var r) && r.ValueKind == JsonValueKind.String) item.Reason = r.GetString();
                    items.Add(item);
                }
            }
        }
        catch (JsonException)
        {
            // Bad JSON from model - skip this pass's suggestions, caller counts it as an error.
        }

        return (items, tokens);
    }

    private static void AddChange(BalanceSuggestion sug, string field, int oldVal, int? newVal)
    {
        if (!newVal.HasValue) return;
        if (newVal.Value == oldVal) return;
        sug.Changes[field] = new FieldChange { OldValue = oldVal, NewValue = newVal.Value };
    }

    private static void Report(IProgress<BalancerProgress>? p, int total, int completed, int active,
        int modified, int errors, int tokens, string log, int workerIndex, WorkerStatus status,
        int batchIndex, double pct)
    {
        p?.Report(new BalancerProgress
        {
            TotalBatches = total,
            CompletedBatches = completed,
            ActiveWorkers = active,
            TotalModified = modified,
            TotalErrors = errors,
            TotalTokensUsed = tokens,
            LogMessage = log,
            WorkerIndex = workerIndex,
            WorkerStatus = status,
            CurrentBatchIndex = batchIndex,
            WorkerProgressPct = pct,
        });
    }

    private static Dictionary<string, (double avg, int min, int max, string trend)> BuildHistoryIndex(
        IReadOnlyList<EconomySnapshot> history)
    {
        var dict = new Dictionary<string, (double, int, int, string)>(StringComparer.OrdinalIgnoreCase);
        if (history == null || history.Count == 0) return dict;

        // Per-className list of recent spawnedCount values across the history window.
        var perClass = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
        foreach (var snap in history)
        {
            if (snap?.Items == null) continue;
            foreach (var it in snap.Items)
            {
                if (string.IsNullOrEmpty(it.ClassName)) continue;
                if (!perClass.TryGetValue(it.ClassName, out var list))
                    perClass[it.ClassName] = list = new List<int>();
                list.Add(it.SpawnedCount);
            }
        }

        foreach (var kv in perClass)
        {
            var list = kv.Value;
            if (list.Count == 0) continue;
            var avg = list.Average();
            var min = list.Min();
            var max = list.Max();
            string trend = "stable";
            if (list.Count >= 3)
            {
                var firstHalf = list.Take(list.Count / 2).Average();
                var secondHalf = list.Skip(list.Count / 2).Average();
                var delta = secondHalf - firstHalf;
                if (Math.Abs(delta) > Math.Max(1, avg * 0.15))
                    trend = delta > 0 ? "rising" : "falling";
            }
            dict[kv.Key] = (avg, min, max, trend);
        }
        return dict;
    }

    private async Task<(List<AiBalanceItem> items, int tokens)> CallOpenAiAsync(
        List<ItemEconomy> batch,
        EconomySnapshot snapshot,
        Dictionary<string, (double avg, int min, int max, string trend)> historyIndex,
        AiBalancerOptions options,
        CancellationToken cancel)
    {
        var systemPrompt = BuildSystemPrompt(snapshot, options.ServerType);
        var userPayload = BuildUserPayload(batch, historyIndex);

        var requestBody = new
        {
            model = options.Model,
            messages = new object[]
            {
                new { role = "system", content = systemPrompt },
                new { role = "user", content = userPayload },
            },
            response_format = new { type = "json_object" },
        };

        var bodyJson = JsonSerializer.Serialize(requestBody, JsonOptions);

        // Exponential backoff on 429.
        var attempts = 0;
        while (true)
        {
            attempts++;
            using var req = new HttpRequestMessage(HttpMethod.Post, options.Endpoint);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.ApiKey);
            req.Content = new StringContent(bodyJson, Encoding.UTF8, "application/json");

            using var resp = await Http.SendAsync(req, cancel).ConfigureAwait(false);
            var text = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);

            if ((int)resp.StatusCode == 429 && attempts < 4)
            {
                var delay = TimeSpan.FromSeconds(Math.Pow(2, attempts));
                await Task.Delay(delay, cancel).ConfigureAwait(false);
                continue;
            }
            if (!resp.IsSuccessStatusCode)
                throw new InvalidOperationException($"OpenAI HTTP {(int)resp.StatusCode}: {Trunc(text, 240)}");

            return ParseResponse(text);
        }
    }

    private static string BuildSystemPrompt(EconomySnapshot snap, string serverType)
    {
        var uptimeH = Math.Max(0, snap.ServerUptime / 3600);
        return $@"You are an expert DayZ server economy balancer.
Server type: {serverType}
Current players: {snap.PlayersOnline}/{snap.PlayersMax}
Server uptime: {uptimeH}h

Analyze each item's economy data and suggest optimal spawn values.

BALANCING RULES:
- crafted=true items: ALWAYS nominal=0, min=0, never change these
- If spawnedCount/nominal ratio > 0.9 consistently: item over-spawns, reduce nominal
- If spawnedCount/nominal ratio < 0.3 consistently: under-spawning or hoarding, check lifetime
- min must always be <= nominal, aim for 60-80% of nominal
- cost controls spawn weight: 10=very common, 100=normal, 500=rare, 1000=very rare
- lifetime: how long items persist before despawning (seconds)
  Common loot: 3600-7200, Tools: 7200-14400, Weapons: 3600-7200, Crafted bases: 3888000
- Weapons and ammo in the same batch MUST be balanced together:
  ammo nominal should be 3-5x the weapon nominal
- Medical items: scale with player count (more players online = higher nominal for medical)
- Food/drink: high nominal (10-20) for survival, lower for military zones
- Military loot (weapons, military clothing): nominal 2-8, high cost (500-1000)
- If item has usages=[""Military""]: treat as rare
- If item has values=[""Tier1"",""Tier2""]: more common than Tier3/Tier4

OUTPUT: A JSON object with key 'items' whose value is an array. No markdown, no commentary outside 'reason'.
Only include items that need changes. Omit balanced items.
Example: {{ ""items"": [{{ ""className"":""AKM"", ""nominal"":5, ""min"":3, ""cost"":800, ""lifetime"":7200, ""reason"":""..."" }}] }}";
    }

    private static string BuildUserPayload(List<ItemEconomy> batch,
        Dictionary<string, (double avg, int min, int max, string trend)> hist)
    {
        var arr = batch.Select(it =>
        {
            historyOrDefault(it.ClassName, out var avg, out var min, out var max, out var trend);
            var dev = it.Nominal > 0 ? Math.Round((it.SpawnedCount - it.Nominal) / (double)it.Nominal * 100.0, 1) : 0;
            return new
            {
                className = it.ClassName,
                category = it.Category,
                usages = it.Usages,
                values = it.Values,
                flags = it.Flags,
                current = new { nominal = it.Nominal, min = it.Min, cost = it.Cost, lifetime = it.Lifetime, spawnedCount = it.SpawnedCount },
                history = new { avgSpawned = avg, minSpawned = min, maxSpawned = max, trend },
                deviationPct = dev,
            };
        }).ToList();

        return JsonSerializer.Serialize(new { items = arr }, JsonOptions);

        void historyOrDefault(string cn, out double a, out int mi, out int ma, out string t)
        {
            if (hist.TryGetValue(cn, out var v)) { a = Math.Round(v.avg, 2); mi = v.min; ma = v.max; t = v.trend; }
            else { a = 0; mi = 0; ma = 0; t = "unknown"; }
        }
    }

    private static (List<AiBalanceItem> items, int tokens) ParseResponse(string raw)
    {
        using var doc = JsonDocument.Parse(raw);
        var root = doc.RootElement;

        var tokens = 0;
        if (root.TryGetProperty("usage", out var usage) &&
            usage.TryGetProperty("total_tokens", out var tt) &&
            tt.ValueKind == JsonValueKind.Number)
            tokens = tt.GetInt32();

        if (!root.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0)
            return (new List<AiBalanceItem>(), tokens);

        var content = choices[0].GetProperty("message").GetProperty("content").GetString() ?? string.Empty;
        var cleaned = StripCodeFences(content).Trim();

        var items = new List<AiBalanceItem>();
        if (string.IsNullOrEmpty(cleaned)) return (items, tokens);

        try
        {
            using var inner = JsonDocument.Parse(cleaned);
            if (inner.RootElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var el in inner.RootElement.EnumerateArray())
                    AppendItem(items, el);
            }
            else if (inner.RootElement.ValueKind == JsonValueKind.Object)
            {
                if (inner.RootElement.TryGetProperty("items", out var arr) && arr.ValueKind == JsonValueKind.Array)
                {
                    foreach (var el in arr.EnumerateArray()) AppendItem(items, el);
                }
                else
                {
                    // single item
                    AppendItem(items, inner.RootElement);
                }
            }
        }
        catch (JsonException)
        {
            // Bad JSON from model — skip this batch's items, caller logs error.
        }

        return (items, tokens);
    }

    private static void AppendItem(List<AiBalanceItem> items, JsonElement el)
    {
        if (el.ValueKind != JsonValueKind.Object) return;
        var item = new AiBalanceItem();
        if (el.TryGetProperty("className", out var c) && c.ValueKind == JsonValueKind.String)
            item.ClassName = c.GetString() ?? string.Empty;
        if (string.IsNullOrEmpty(item.ClassName)) return;
        if (el.TryGetProperty("nominal", out var n) && n.ValueKind == JsonValueKind.Number) item.Nominal = n.GetInt32();
        if (el.TryGetProperty("min", out var m) && m.ValueKind == JsonValueKind.Number) item.Min = m.GetInt32();
        if (el.TryGetProperty("cost", out var co) && co.ValueKind == JsonValueKind.Number) item.Cost = co.GetInt32();
        if (el.TryGetProperty("lifetime", out var l) && l.ValueKind == JsonValueKind.Number) item.Lifetime = l.GetInt32();
        if (el.TryGetProperty("reason", out var r) && r.ValueKind == JsonValueKind.String) item.Reason = r.GetString();
        items.Add(item);
    }

    private static string StripCodeFences(string s)
    {
        var m = Regex.Match(s, "```(?:json)?\\s*(.*?)```", RegexOptions.Singleline);
        return m.Success ? m.Groups[1].Value : s;
    }

    private static string Trunc(string s, int len) => s.Length <= len ? s : s.Substring(0, len) + "...";
}
