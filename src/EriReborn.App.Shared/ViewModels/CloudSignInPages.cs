namespace EriReborn.App.Shared.ViewModels;

/// <summary>
/// The platform's own sign-in page, used by the embedded browser. A missing entry means we have no
/// page worth opening, and the button stays hidden instead of opening something useless.
/// </summary>
public static class CloudSignInPages
{
    /// <summary>Returns the sign-in page for a provider id, or null when we have none.</summary>
    public static string? For(string? providerId) => providerId switch
    {
        // The sign-in page itself, not the marketing landing page: the landing page has no form at
        // all, which is why "click the login link and hide the rest" never had anything to hide.
        "baidu" => "https://passport.baidu.com/v2/?login&tpl=netdisk",
        "123" => "https://www.123pan.com/login",
        // Quark has no /login route: it redirects straight back to the landing page and opens the
        // sign-in modal there, so the landing page is the only entry point it offers.
        "quark" => "https://pan.quark.cn/",
        // www.lanzou.com 302s here; pc.woozooo.com serves the same page. 蓝奏云 resolves share links
        // without an account, but it does have accounts, so a signed-in session is still worth offering.
        "lanzou" => "https://up.woozooo.com/",
        // pan.xunlei.com/login is a real page; the bare host is the marketing landing page.
        "xunlei" => "https://pan.xunlei.com/login",
        _ => null,
    };
}
