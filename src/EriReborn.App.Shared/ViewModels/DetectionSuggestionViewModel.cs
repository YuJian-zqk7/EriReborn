using CommunityToolkit.Mvvm.ComponentModel;
using EriReborn.Engine.Software;

namespace EriReborn.App.Shared.ViewModels;

/// <summary>
/// One proposed detection override, shown before anything is written.
///
/// Suggestions used to be applied in bulk the instant they were computed, so a
/// user saw a count and never the matches themselves. Containment matches are
/// the weakest evidence and are exactly the ones worth looking at, so every
/// suggestion is now listed and applied individually (spec 22).
/// </summary>
public sealed partial class DetectionSuggestionViewModel(DetectionSuggestion suggestion, bool isSelected)
    : ObservableObject
{
    public DetectionSuggestion Suggestion { get; } = suggestion;

    public string SoftwareName => Suggestion.SoftwareName;

    public string InstalledName => Suggestion.InstalledName;

    public string Version => string.IsNullOrWhiteSpace(Suggestion.Version) ? "—" : Suggestion.Version;

    public string Reason => Suggestion.Reason;

    public SuggestionConfidence Confidence => Suggestion.Confidence;

    public string ConfidenceText => Describe(Suggestion.Confidence);

    /// <summary>
    /// Containment means one name merely contains the other, which is the
    /// evidence most likely to be wrong. It is listed but never pre-selected.
    /// </summary>
    public bool IsWeak => Suggestion.Confidence == SuggestionConfidence.Containment;

    [ObservableProperty]
    private bool _isSelected = isSelected;

    public DetectionHint ToHint() => Suggestion.ToHint();

    public static string Describe(SuggestionConfidence confidence) => confidence switch
    {
        SuggestionConfidence.Exact => "精确",
        SuggestionConfidence.Normalized => "规范化",
        _ => "包含",
    };
}

/// <summary>One entry in the minimum-confidence selector.</summary>
public sealed record SuggestionFilterOption(string Label, SuggestionConfidence Minimum)
{
    /// <summary>
    /// A ComboBox without an item template shows ToString(), and the generated
    /// record text ("SuggestionFilterOption { Label = ... }") is not something a
    /// user should ever see.
    /// </summary>
    public override string ToString() => Label;
}
