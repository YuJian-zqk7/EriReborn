using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using EriReborn.App.Shared.ViewModels;

namespace EriReborn.UI.Avalonia.Views;

public partial class SoftwareView : UserControl
{
    public SoftwareView()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Picks an install directory for the selected entry.
    ///
    /// <para>
    /// Unlike the install root on the settings page — a draft until 「应用」 is pressed — this takes
    /// effect at once, because it is one entry's own choice and 「恢复默认位置」 sits right beside it.
    /// A platform without a folder chooser says so instead of silently doing nothing (spec 56).
    /// </para>
    /// </summary>
    private async void OnBrowseInstallPathClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not SoftwareViewModel software)
        {
            return;
        }

        if (software.SelectedItem is null)
        {
            software.PathOverrideStatus = "请先选择一条软件。";
            return;
        }

        if (TopLevel.GetTopLevel(this) is not { } top)
        {
            software.PathOverrideStatus = "这个平台没有文件夹选择器，请手动填写路径。";
            return;
        }

        try
        {
            var picked = await top.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "选择这个软件的安装目录",
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
                software.PathOverrideStatus = "这个来源给不出本地文件夹路径，请手动填写。";
                return;
            }

            software.SetInstallPathForSelected(path);
        }
        catch (Exception ex)
        {
            // This handler runs on the dispatcher, where an exception takes the window with it.
            software.PathOverrideStatus = "选择文件夹失败：" + ex.GetType().Name + " — " + ex.Message;
        }
    }

    /// <summary>
    /// 插件资源树行内的「安装 / 准备」：文件叶子与文件夹分支共用，先选中该行再走既有安装计划，
    /// 不为树另造下载链路（AC-12）。
    /// </summary>
    private void OnTreeNodePrepareClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: SoftwareTreeNodeVm node }
            && DataContext is SoftwareViewModel software)
        {
            software.PrepareNode(node);
        }
    }

    /// <summary>展开/折叠按钮：切换 IsExpanded。按钮在行最前面，不依赖 TreeView 内置箭头。</summary>
    private void OnTreeNodeToggleClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: SoftwareTreeNodeVm node })
        {
            node.IsExpanded = !node.IsExpanded;
        }
    }
}
