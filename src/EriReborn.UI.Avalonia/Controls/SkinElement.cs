using Avalonia;
using Avalonia.Controls;
using EriReborn.Skin;

namespace EriReborn.UI.Avalonia.Controls;

/// <summary>
/// Says what a control is, for the skin editor.
///
/// <para>
/// A view marks an element it wants to be editable by writing an id on the control that draws it:
/// </para>
///
/// <code>&lt;TextBlock controls:SkinElement.Id="nav.settings" /&gt;</code>
///
/// <para>
/// The editor walks up from whatever was clicked until it meets a control carrying one, so a click
/// anywhere on a real element selects it — a plain <c>TextBlock</c>, a <c>Button</c>, a
/// <c>Border</c>, an icon — rather than only the two controls that used to know their own names.
/// The id is internal: the panel shows <see cref="SkinElementCatalog"/>'s human name for it.
/// </para>
///
/// <para>
/// Everything else is optional. An element that is not in the catalog may declare its own kind,
/// name, text key, asset or colour; anything left blank is filled in from the catalog.
/// </para>
/// </summary>
public static class SkinElement
{
    /// <summary>The stable name of the editable slot, e.g. <c>home.character</c>.</summary>
    public static readonly AttachedProperty<string?> IdProperty =
        AvaloniaProperty.RegisterAttached<Control, string?>("Id", typeof(SkinElement));

    /// <summary>What kind of element this is. Defaults to the catalog's answer for the id.</summary>
    public static readonly AttachedProperty<SkinElementKind?> KindProperty =
        AvaloniaProperty.RegisterAttached<Control, SkinElementKind?>("Kind", typeof(SkinElement));

    /// <summary>The name the panel shows. Defaults to the catalog's name for the id.</summary>
    public static readonly AttachedProperty<string?> DisplayNameProperty =
        AvaloniaProperty.RegisterAttached<Control, string?>("DisplayName", typeof(SkinElement));

    /// <summary>The interface word this element shows, when it is text.</summary>
    public static readonly AttachedProperty<string?> TextKeyProperty =
        AvaloniaProperty.RegisterAttached<Control, string?>("TextKey", typeof(SkinElement));

    /// <summary>The asset id this element draws from, when it is art.</summary>
    public static readonly AttachedProperty<string?> AssetIdProperty =
        AvaloniaProperty.RegisterAttached<Control, string?>("AssetId", typeof(SkinElement));

    /// <summary>The skin colour key this element is painted with, when it is a colour surface.</summary>
    public static readonly AttachedProperty<string?> ColorKeyProperty =
        AvaloniaProperty.RegisterAttached<Control, string?>("ColorKey", typeof(SkinElement));

    public static string? GetId(Control control) => control.GetValue(IdProperty);

    public static void SetId(Control control, string? value) => control.SetValue(IdProperty, value);

    public static SkinElementKind? GetKind(Control control) => control.GetValue(KindProperty);

    public static void SetKind(Control control, SkinElementKind? value) => control.SetValue(KindProperty, value);

    public static string? GetDisplayName(Control control) => control.GetValue(DisplayNameProperty);

    public static void SetDisplayName(Control control, string? value) => control.SetValue(DisplayNameProperty, value);

    public static string? GetTextKey(Control control) => control.GetValue(TextKeyProperty);

    public static void SetTextKey(Control control, string? value) => control.SetValue(TextKeyProperty, value);

    public static string? GetAssetId(Control control) => control.GetValue(AssetIdProperty);

    public static void SetAssetId(Control control, string? value) => control.SetValue(AssetIdProperty, value);

    public static string? GetColorKey(Control control) => control.GetValue(ColorKeyProperty);

    public static void SetColorKey(Control control, string? value) => control.SetValue(ColorKeyProperty, value);

    /// <summary>
    /// What this control declares, or null when it declares nothing. The catalog fills in the parts
    /// the view left out, so a well-known element only has to give its id.
    /// </summary>
    public static SkinElementDescriptor? Describe(Control control)
    {
        var id = GetId(control);
        return string.IsNullOrWhiteSpace(id)
            ? null
            : SkinElementCatalog.Resolve(
                id!,
                GetKind(control),
                GetDisplayName(control),
                GetTextKey(control),
                GetAssetId(control),
                GetColorKey(control));
    }
}
