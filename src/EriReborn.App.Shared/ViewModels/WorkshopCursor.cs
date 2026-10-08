using System.Collections.Generic;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using EriReborn.Skin;

namespace EriReborn.App.Shared.ViewModels;

/// <summary>
/// One pointer role in the skin editor: what the role is for, what this skin says about it, and what it may
/// say.
///
/// <para>
/// The value is the same string a <c>skin.json</c> carries — a standard shape name (<c>wait</c>) or a
/// picture from the library (<c>asset:sheet_id</c>) — and the choices are exactly the ones the resolver
/// understands, so nothing this editor can save is a value the app will ignore.
/// </para>
/// </summary>
public sealed partial class WorkshopCursor : ObservableObject
{
    public WorkshopCursor(
        string roleName,
        string title,
        IEnumerable<string> choices,
        string? current,
        string? inherited)
    {
        RoleName = roleName;
        Title = title;
        Choices = new ObservableCollection<string>(choices);
        Inherited = string.IsNullOrWhiteSpace(inherited) ? "平台默认" : inherited;
        _spec = current ?? string.Empty;
    }

    /// <summary>The name a skin.json writes, e.g. <c>resizeHorizontal</c>.</summary>
    public string RoleName { get; }

    /// <summary>The words the editor shows for it, with the name a skin.json uses in brackets.</summary>
    public string Title { get; }

    /// <summary>
    /// The shapes and pictures this role may point at, plus "follow the base skin". A growing list
    /// rather than a fixed one, because a picture the user imports joins it while the editor is open.
    /// </summary>
    public ObservableCollection<string> Choices { get; }

    /// <summary>
    /// Offers a choice the row did not start with — a picture just imported for this role. Kept out of
    /// the box until one exists, so the drop-down only ever holds values the resolver can read.
    /// </summary>
    public void AddChoice(string value)
    {
        if (!string.IsNullOrWhiteSpace(value) && !Choices.Contains(value))
        {
            Choices.Add(value);
        }
    }

    /// <summary>True once this row points at a picture the user brought in, rather than a shipped one.</summary>
    public bool HasImportedArt => Spec?.StartsWith(CursorSpec.AssetPrefix, StringComparison.Ordinal) == true;

    /// <summary>What the pointer would be if this skin declared nothing — shown so the row is not a riddle.</summary>
    public string Inherited { get; }

    public string InheritedHint => "继承：" + Inherited;

    /// <summary>What this skin declares for the role; empty means "follow the base skin".</summary>
    [ObservableProperty]
    private string _spec = string.Empty;
}
