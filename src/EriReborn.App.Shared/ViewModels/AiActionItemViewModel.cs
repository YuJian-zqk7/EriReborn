using CommunityToolkit.Mvvm.ComponentModel;
using EriReborn.Engine.Ai;

namespace EriReborn.App.Shared.ViewModels;

/// <summary>
/// One proposed change, as the review list shows it.
///
/// <para>
/// Selected by default but never trusted: the item carries the <b>id</b> the model
/// named, and the thing that actually runs is looked up from the catalog again at
/// execution time. A label can be edited by a model; an id is checked against the
/// catalogue.
/// </para>
/// </summary>
public sealed partial class AiActionItemViewModel(AiProposedAction action, string targetName) : ObservableObject
{
    public AiProposedAction Action { get; } = action;

    /// <summary>The catalogue's own name, not the model's wording.</summary>
    public string TargetName { get; } = targetName;

    public string TargetId => Action.Target;

    public string KindText => Action.Kind switch
    {
        AiActionKind.Install => "安装",
        AiActionKind.Hint => "记录检测方式",
        _ => Action.Kind.ToString(),
    };

    public string Reason => string.IsNullOrWhiteSpace(Action.Reason) ? "（模型没有给出理由）" : Action.Reason;

    /// <summary>What exactly will change, in one line.</summary>
    public string Effect => Action.Kind switch
    {
        AiActionKind.Install => $"将安装 {TargetName}（{TargetId}）",
        AiActionKind.Hint => $"将为 {TargetName}（{TargetId}）记录检测方式：{Action.Value}",
        _ => $"未知动作：{Action.Kind}",
    };

    [ObservableProperty]
    private bool _isSelected = true;

    public override string ToString() => Effect;
}
