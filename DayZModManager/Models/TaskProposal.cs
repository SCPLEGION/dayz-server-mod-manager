using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace DayZModManager.Models;

public enum TaskActionKind
{
    CfgSet,        // serverDZ.cfg: set key = value
    XmlSetValue,   // XML: set element/attr value at XPath
    TextReplace,   // generic file: replace one substring
    RunBalance,    // re-run the AI balancer
    Note,          // informational only (AI couldn't act)
}

public class TaskAction : INotifyPropertyChanged
{
    private bool _isApproved = true;

    public TaskActionKind Kind { get; set; }
    public string? TargetFile { get; set; }
    public string? Key { get; set; }       // for CfgSet
    public string? XPath { get; set; }     // for XmlSetValue
    public string? OldText { get; set; }   // for TextReplace
    public string? OldValue { get; set; }
    public string NewValue { get; set; } = string.Empty;
    public string? Reason { get; set; }

    public bool IsApproved
    {
        get => _isApproved;
        set { if (_isApproved != value) { _isApproved = value; OnPropertyChanged(); } }
    }

    public string Summary => Kind switch
    {
        TaskActionKind.CfgSet       => $"{System.IO.Path.GetFileName(TargetFile)}: {Key} = {NewValue}",
        TaskActionKind.XmlSetValue  => $"{System.IO.Path.GetFileName(TargetFile)} {XPath} → {NewValue}",
        TaskActionKind.TextReplace  => $"{System.IO.Path.GetFileName(TargetFile)}: replace text",
        TaskActionKind.RunBalance   => "Run AI economy balance",
        TaskActionKind.Note         => "Note",
        _ => string.Empty,
    };

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? n = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}

public class TaskProposal
{
    public string Title { get; set; } = string.Empty;
    public string? Notes { get; set; }
    public List<TaskAction> Actions { get; set; } = new();
    public int TokensUsed { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.Now;
}

public class TaskApplyResult
{
    public int Applied { get; set; }
    public int Skipped { get; set; }
    public List<string> Errors { get; set; } = new();
    public List<string> BackupPaths { get; set; } = new();
}
