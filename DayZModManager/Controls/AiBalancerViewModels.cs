using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace DayZModManager.Controls;

public class EconomyRowViewModel : INotifyPropertyChanged
{
    public string ClassName { get; set; } = string.Empty;
    public string? Category { get; set; }
    public int SpawnedCount { get; set; }
    public int Nominal { get; set; }
    public string RatioText => Nominal > 0 ? $"{(int)(SpawnedCount * 100.0 / Nominal)}" : "-";
    public string Trend { get; set; } = "\u2192";
    public string Status { get; set; } = "Balanced";
    public string LastSeen { get; set; } = string.Empty;
    public bool IsCrafted { get; set; }

    public double RatioPct => Nominal > 0 ? SpawnedCount * 100.0 / Nominal : 0;

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public class SuggestionRowViewModel : INotifyPropertyChanged
{
    private bool _isApproved = true;

    public string ClassName { get; set; } = string.Empty;
    public string? Category { get; set; }
    public string Field { get; set; } = string.Empty;
    public int OldValue { get; set; }
    public int NewValue { get; set; }
    public int Delta => NewValue - OldValue;
    public string DeltaText => Delta > 0 ? $"\u2191 {Delta}" : Delta < 0 ? $"\u2193 {Delta}" : "\u2192 0";
    public string? Reason { get; set; }
    public string ReasonShort => string.IsNullOrEmpty(Reason)
        ? string.Empty
        : (Reason.Length <= 60 ? Reason : Reason.Substring(0, 57) + "...");

    public bool IsSevere => OldValue > 0 && Math.Abs(Delta) * 100.0 / OldValue >= 50.0;

    public bool IsApproved
    {
        get => _isApproved;
        set { if (_isApproved != value) { _isApproved = value; OnPropertyChanged(); } }
    }

    /// <summary>Back-pointer to source suggestion so apply step can map back.</summary>
    public DayZModManager.Models.BalanceSuggestion? Source { get; set; }

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public class WorkerCardViewModel : INotifyPropertyChanged
{
    private string _title = "Worker";
    private string _status = "idle";
    private double _progressPct;

    public string Title { get => _title; set { _title = value; OnPropertyChanged(); } }
    public string Status { get => _status; set { _status = value; OnPropertyChanged(); } }
    public double ProgressPct { get => _progressPct; set { _progressPct = value; OnPropertyChanged(); } }

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
