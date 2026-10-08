using System;

namespace EriReborn.UI.Avalonia.Controls;

/// <summary>
/// What the shared UI needs to know about a hosted browser kernel. The Windows implementation
/// lives in the app project (WebView2 drags in WPF's WindowsBase, which the shared net8.0 build
/// cannot take), so this is the seam between them.
/// </summary>
public interface IBrowserHost
{
    /// <summary>Raised when the kernel cannot start, carrying a message fit to show the user.</summary>
    event EventHandler<string>? Failed;

    /// <summary>
    /// Raised once the sign-in page has set the cookies the platform knows a session by, carrying the
    /// raw "name=value; name=value" string — the same shape the manual entry box accepts, so both
    /// paths share one parser.
    /// </summary>
    event EventHandler<string>? CredentialFound;
}
