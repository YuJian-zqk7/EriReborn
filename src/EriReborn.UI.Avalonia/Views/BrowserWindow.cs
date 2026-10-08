using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using EriReborn.UI.Avalonia.Controls;

namespace EriReborn.UI.Avalonia.Views;

/// <summary>
/// Our own shell around a platform's sign-in page: our title bar, our close button, the page only
/// inside the content area. The page is the platform's real page on purpose — the user has to see
/// where their password goes.
///
/// <para>
/// A kernel that cannot start says so in the content area instead of leaving it blank: a silent
/// white rectangle is what made the first attempt impossible to diagnose.
/// </para>
/// </summary>
public sealed class BrowserWindow : Window
{
    /// <param name="platform">Shown in our own title bar.</param>
    /// <param name="loginPage">The platform's real sign-in page.</param>
    /// <param name="onCredential">
    /// Called with the captured "name=value; name=value" string once the page sets the sign-in
    /// cookies. After it returns, this window closes itself — the user only had to sign in.
    /// </param>
    public BrowserWindow(string platform, Uri loginPage, Func<string, Task>? onCredential = null)
    {
        Title = platform + " 登录 · EriReborn";
        Width = 980;
        Height = 720;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var heading = new TextBlock
        {
            Text = platform + " · 官方登录页",
            FontWeight = FontWeight.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(16, 0, 0, 0),
        };

        var hint = new TextBlock
        {
            Text = onCredential is null
                ? "登录完成后可以关闭这个窗口"
                : "登录成功后会自动保存凭据并关闭这个窗口",
            Opacity = 0.7,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(16, 0, 0, 0),
        };

        var close = new Button
        {
            Content = "关闭",
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 0, 16, 0),
        };
        close.Click += (_, _) => Close();

        var bar = new DockPanel { Height = 44, LastChildFill = true };
        DockPanel.SetDock(close, Dock.Right);
        bar.Children.Add(close);
        var titles = new StackPanel { Orientation = Orientation.Horizontal };
        titles.Children.Add(heading);
        titles.Children.Add(hint);
        bar.Children.Add(titles);

        var failure = new TextBlock
        {
            Margin = new Thickness(16, 12, 16, 16),
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Top,
            IsVisible = false,
        };

        var content = new Grid();
        var host = BrowserSlot.Create(loginPage);

        if (host is IBrowserHost browser)
        {
            browser.Failed += (_, message) =>
            {
                failure.Text = message;
                failure.IsVisible = true;
            };

            if (onCredential is not null)
            {
                browser.CredentialFound += async (_, raw) =>
                {
                    try
                    {
                        await onCredential(raw).ConfigureAwait(true);
                        BrowserLog.Write("credential stored; closing the sign-in window");
                        Close();
                    }
                    catch (Exception ex)
                    {
                        // Keep the window open: the page still holds a valid session, and the user can
                        // fall back to the manual box.
                        failure.Text = "拿到了登录凭据，但保存失败：" + ex.GetType().Name + " — " + ex.Message;
                        failure.IsVisible = true;
                    }
                };
            }
        }

        if (host is not null)
        {
            content.Children.Add(host);
        }
        else
        {
            failure.Text = "这个构建里没有可用的浏览器内核，请使用手动填写 Token / Cookie 的方式。";
            failure.IsVisible = true;
        }

        content.Children.Add(failure);

        var root = new DockPanel();
        DockPanel.SetDock(bar, Dock.Top);
        root.Children.Add(bar);
        root.Children.Add(content);

        BrowserLog.Write("window built for " + platform);
        Content = root;
    }
}
