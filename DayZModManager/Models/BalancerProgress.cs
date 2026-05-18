namespace DayZModManager.Models;

public enum WorkerStatus { Idle, Running, Done, Error }

public class BalancerProgress
{
    public int TotalBatches { get; set; }
    public int CompletedBatches { get; set; }
    public int ActiveWorkers { get; set; }
    public int TotalModified { get; set; }
    public int TotalErrors { get; set; }
    public int TotalTokensUsed { get; set; }
    public string? LogMessage { get; set; }
    public int WorkerIndex { get; set; }
    public WorkerStatus WorkerStatus { get; set; }
    public int CurrentBatchIndex { get; set; }
    public double WorkerProgressPct { get; set; }
}
