using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using EriReborn.App.Shared.ViewModels;

namespace EriReborn.UI.Avalonia.Views;

public partial class SettingsView : UserControl
{
    public SettingsView()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Picks the install root with the platform's own folder chooser.
    ///
    /// <para>
    /// It fills the draft and stops there. Choosing a folder is not the same as deciding to install into
    /// it: applying is still the button beside this one, so a folder picked by mistake is visible as a
    /// path first (spec 56). A platform without a chooser says so instead of silently doing nothing.
    /// </para>
    /// </summary>
    private async void OnBrowseInstallRootClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not SettingsViewModel settings)
        {
            return;
        }

        if (TopLevel.GetTopLevel(this) is not { } top)
        {
            settings.ConfigStatus = "这个平台没有文件夹选择器，请手动填写路径。";
            return;
        }

        try
        {
            var picked = await top.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "选择安装根目录",
                AllowMultiple = false,
            });

            if (picked.Count == 0)
            {
                return;
            }

            var path = picked[0].TryGetLocalPath();
            if (string.IsNullOrWhiteSpace(path))
            {
                // Some sources hand over a stream and no path; saying so beats doing nothing.
                settings.ConfigStatus = "这个来源给不出本地文件夹路径，请手动填写。";
                return;
            }

            settings.InstallRootDraft = path;
            settings.ConfigStatus = "已选择：" + path + "（点「应用」才会生效）";
        }
        catch (Exception ex)
        {
            // A chooser that fails is a message, not a crash: this handler runs on the dispatcher.
            settings.ConfigStatus = "选择文件夹失败：" + ex.GetType().Name + " — " + ex.Message;
        }
    }

    private async void OnBrowseDownloadDirectoryClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not SettingsViewModel settings)
        {
            return;
        }

        if (TopLevel.GetTopLevel(this) is not { } top)
        {
            settings.ConfigStatus = "这个平台没有文件夹选择器，请手动填写路径。";
            return;
        }

        try
        {
            var picked = await top.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "选择下载目录",
                AllowMultiple = false,
            });

            if (picked.Count == 0)
            {
                return;
            }

            var path = picked[0].TryGetLocalPath();
            if (string.IsNullOrWhiteSpace(path))
            {
                settings.ConfigStatus = "这个来源给不出本地文件夹路径，请手动填写。";
                return;
            }

            settings.DownloadDirectoryDraft = path;
            settings.ConfigStatus = "已选择：" + path + "（点「应用」才会生效）";
        }
        catch (Exception ex)
        {
            settings.ConfigStatus = "选择文件夹失败：" + ex.GetType().Name + " — " + ex.Message;
        }
    }
}
