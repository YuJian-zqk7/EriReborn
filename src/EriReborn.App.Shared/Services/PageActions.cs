using System.Windows.Input;

namespace EriReborn.App.Shared.Services;

/// <summary>One action a page contributes to the shell's top bar.</summary>
/// <param name="ActionKey">
/// What the action *is* ("install", "refresh"), not which file draws it: the art is looked
/// up per skin family in the UI layer, so a page never names one skin's picture.
/// </param>
public sealed record PageAction(string Title, ICommand? Command, bool Primary = false, string? ActionKey = null);

/// <summary>
/// A page that contributes a one-line subtitle and its main actions to the shell.
///
/// The reference launcher keeps a page's main actions on the bar above the body. A page
/// that only renders its own buttons inside the scrolling content is a page whose
/// actions the user has to hunt for, so actions live up here and the page body is left
/// to the content.
/// </summary>
public interface IPageActions
{
    /// <summary>Shown under the page title; empty when the page has nothing to summarise.</summary>
    string Subtitle { get; }

    /// <summary>Top-bar actions in order. A Primary action is drawn as the main button.</summary>
    IReadOnlyList<PageAction> GetPageActions();
}
