using Avalonia.Controls;
using EriReborn.Core.Diagnostics;

namespace EriReborn.UI.Avalonia.Views;

/// <summary>
/// Shown when startup fails. It always reports the failure instead of leaving
/// the user with an empty window (spec 66).
/// </summary>
public partial class StartupErrorWindow : Window
{
    public StartupErrorWindow()
    {
        InitializeComponent();
        DataContext = new StartupErrorViewModel(StartupReport.Failure(StartupFailureKind.Unknown, "未知错误。"));
    }

    public StartupErrorWindow(StartupReport report)
    {
        InitializeComponent();
        DataContext = new StartupErrorViewModel(report);
    }
}

public sealed class StartupErrorViewModel(StartupReport report)
{
    public string Summary { get; } = report.Summary;

    public string KindText { get; } = $"错误类别：{report.Kind}";

    public string Detail { get; } = report.Detail ?? "(无详细信息)";

    public string LogPath { get; } = report.LogPath ?? "(未写入日志)";

    public string StackTrace { get; } = report.StackTrace ?? "(无堆栈)";
}
