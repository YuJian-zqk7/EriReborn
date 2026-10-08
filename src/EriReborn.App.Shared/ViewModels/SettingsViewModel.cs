using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EriReborn.App.Shared.Services;

namespace EriReborn.App.Shared.ViewModels;

/// <summary>Settings surface: skins, paths, credentials and environment facts (spec 61).</summary>
public sealed partial class SettingsViewModel : ViewModelBase
{
    private readonly AppHost _host;

    public SettingsViewModel(AppHost host)
    {
        _host = host;
        Title = "设置";

        AssetsRoot = host.Paths.AssetsRoot;
        CatalogDirectory = host.Paths.CatalogDirectory;
        SkinsDirectory = host.Paths.SkinsDirectory;
        ExtensionsDirectory = host.Paths.ExtensionsDirectory;
        UserDataDirectory = host.Paths.UserDataDirectory;
        LogFile = host.Paths.LogFile;
        InstallRoot = host.Environment.RootPath;
        PlatformId = host.PlatformId;
        CredentialStore = host.CredentialStoreDescription;

        foreach (var skin in host.Skins.Available)
        {
            Skins.Add(skin);
        }

        SelectedSkin = host.Skins.Active ?? host.Skins.Available.FirstOrDefault();
        InstallRootDraft = host.Environment.RootPath;
        DownloadDirectory = host.DownloadDirectory;
        DownloadDirectoryDraft = host.DownloadDirectory;
        ReloadCredentialsCommand.Execute(null);

        foreach (var link in BuildSupportLinks())
        {
            SupportLinks.Add(link);
        }

        SponsorImagePath = FindSponsorImage(host.Paths.AssetsRoot);

        LinkStatus = $"可以点开的入口 {SupportLinks.Count} 个，用系统默认浏览器打开。";
    }

    // ------------------------------------------------------- 关于与支持作者

    /// <summary>What this program is, in one sentence a first-time reader can use.</summary>
    public string AboutText =>
        "Windows / Android 上的装机与环境整理工具：软件来自官方目录与网盘资源插件，"
        + "装到哪个目录由你决定；插件与扩展是数据，不是随包执行的可执行代码。";

    /// <summary>
    /// Where the author can be reached. Built from <see cref="BuildSupportLinks"/>, which lists only
    /// addresses this build really has.
    /// </summary>
    public ObservableCollection<SupportLink> SupportLinks { get; } = new();

    /// <summary>
    /// The author's payment code, when the build ships one; empty when it does not.
    /// </summary>
    /// <remarks>
    /// A payment code is a picture, not an address, so it cannot be an entry in
    /// <see cref="SupportLinks"/>: the shell can open an address, and nothing can open a QR code on the
    /// user's behalf. It is shown as an image instead. Building the path here and letting the page hide
    /// the whole block when the file is absent is the same rule the link list follows — a build with no
    /// code says nothing about sponsoring rather than drawing a control that cannot work.
    /// </remarks>
    public string SponsorImagePath { get; }

    /// <summary>True when this build really has a payment code to show.</summary>
    public bool HasSponsorImage => SponsorImagePath.Length > 0;

    /// <summary>The file name the payment code ships under in <c>assets/brand</c>.</summary>
    private const string SponsorImageFileName = "b4cbcfd0fa4579fa9296452a8d4d430f.jpg";

    private static string FindSponsorImage(string assetsRoot)
    {
        var path = Path.Combine(assetsRoot, "brand", SponsorImageFileName);
        return File.Exists(path) ? path : string.Empty;
    }

    [ObservableProperty]
    private string _linkStatus = string.Empty;

    /// <summary>
    /// Opens one entry with the platform's own handler. The scheme rule lives in the shell service, so
    /// this cannot become the place a shell association slips through (spec 5/67).
    /// </summary>
    [RelayCommand]
    private void OpenSupportLink(SupportLink? link)
    {
        if (link is null || string.IsNullOrWhiteSpace(link.Url))
        {
            LinkStatus = "这条还没有可用的地址。";
            return;
        }

        var failure = _host.Platform.Shell.OpenUrl(link.Url);

        // A failure keeps the address on screen, because the reason may be that this machine has no
        // browser at all — and then copying it by hand is the only thing that still works.
        LinkStatus = failure is null
            ? $"已用系统浏览器打开：{link.Label}"
            : $"打开「{link.Label}」失败：{failure}（下面的地址可以直接复制）";
    }

    /// <summary>
    /// The addresses this build actually has.
    ///
    /// <para>
    /// A sponsor page, an e-mail address or a QQ number belongs here as one more entry the moment it is
    /// supplied. Until then nothing is shown for them: a button that cannot go anywhere is exactly the
    /// placeholder this project refuses to keep, and inventing a payment address to fill the gap would
    /// be worse than the gap.
    /// </para>
    /// </summary>
    private static IEnumerable<SupportLink> BuildSupportLinks() => new[]
    {
        new SupportLink("B 站", "作者主页与更新动态", "https://space.bilibili.com/689572905?spm_id_from=333.1007.0.0"),
        new SupportLink("GitHub", "源码与问题反馈", "https://github.com/YuJian-zqk7/EriReborn3.0"),
    };

    public ObservableCollection<EriReborn.Skin.SkinManifest> Skins { get; } = new();

    public ObservableCollection<string> CredentialKeys { get; } = new();

    public string AssetsRoot { get; }

    public string CatalogDirectory { get; }

    public string SkinsDirectory { get; }

    public string ExtensionsDirectory { get; }

    public string UserDataDirectory { get; }

    public string LogFile { get; }

    public string InstallRoot { get; }

    /// <summary>The directory downloads currently land in (effective value).</summary>
    public string DownloadDirectory { get; private set; }

    public string PlatformId { get; }

    public string CredentialStore { get; }

    [ObservableProperty]
    private EriReborn.Skin.SkinManifest? _selectedSkin;

    /// <summary>
    /// Raised when this page wants a skin to become the one in force, so the shell can keep its
    /// own copy — and therefore the words and pictures every page draws — in step. Without it the
    /// engine would switch while the interface still spoke the old skin.
    /// </summary>
    public event EventHandler<string>? ActivateSkinRequested;

    /// <summary>
    /// Selecting a skin here used to do nothing at all: the property existed and was
    /// bound, but nothing ever applied it, so the picker silently lied. It now goes
    /// through the same engine call the top bar uses.
    /// </summary>
    partial void OnSelectedSkinChanged(EriReborn.Skin.SkinManifest? value)
    {
        if (value is not null)
        {
            _ = _host.Skins.ApplyAsync(value);
            ActivateSkinRequested?.Invoke(this, value.Id);
        }
    }

    /// <summary>Applies one skin from the gallery.</summary>
    [RelayCommand]
    private void SelectSkin(EriReborn.Skin.SkinManifest? skin)
    {
        if (skin is not null)
        {
            SelectedSkin = skin;
        }
    }

    [ObservableProperty]
    private string _credentialSummary = string.Empty;

    [ObservableProperty]
    private string _installRootDraft = string.Empty;

    [ObservableProperty]
    private string _downloadDirectoryDraft = string.Empty;

    [ObservableProperty]
    private string _configStatus = string.Empty;

    public string ConfigFile => _host.Paths.UserConfigFile;

    /// <summary>
    /// Applies the user's chosen install root (spec 17). The value is validated
    /// as a Windows path before it is accepted; official directory naming rules
    /// deliberately do not apply to it.
    /// </summary>
    [RelayCommand]
    private void ApplyInstallRoot()
    {
        var candidate = InstallRootDraft?.Trim();
        if (string.IsNullOrWhiteSpace(candidate))
        {
            ConfigStatus = "安装根目录不能为空。";
            return;
        }

        var validation = EriReborn.Core.Validation.DirectoryNameValidator.ValidateFullPath(candidate);
        if (!validation.IsValid)
        {
            ConfigStatus = $"路径非法：{validation.Describe()}";
            return;
        }

        _host.Environment = _host.Environment with { RootPath = candidate! };
        var preferences = _host.UserConfig.Current with { InstallRoot = candidate };
        _host.UserConfig.Save(preferences);

        ConfigStatus = $"已保存安装根目录：{_host.Environment.RootPath}";
        OnPropertyChanged(nameof(InstallRoot));
    }

    /// <summary>
    /// Applies the user's chosen download directory. Both cloud downloads and install packages
    /// use it, so a large game archive does not have to sit on the system drive.
    /// </summary>
    [RelayCommand]
    private void ApplyDownloadDirectory()
    {
        var candidate = DownloadDirectoryDraft?.Trim();
        if (string.IsNullOrWhiteSpace(candidate))
        {
            ConfigStatus = "下载目录不能为空。";
            return;
        }

        var validation = EriReborn.Core.Validation.DirectoryNameValidator.ValidateFullPath(candidate);
        if (!validation.IsValid)
        {
            ConfigStatus = $"路径非法：{validation.Describe()}";
            return;
        }

        var preferences = _host.UserConfig.Current with { DownloadDirectory = candidate };
        _host.UserConfig.Save(preferences);

        // The engine keeps its own copy for install requests, and DownloadDirectory on the host is
        // recomputed lazily from preferences — so both see the new value immediately.
        _host.SoftwareEngine.DownloadDirectory = candidate;
        DownloadDirectory = _host.DownloadDirectory;

        ConfigStatus = $"已保存下载目录：{DownloadDirectory}";
        OnPropertyChanged(nameof(DownloadDirectory));
    }

    [RelayCommand]
    private async Task ReloadCredentialsAsync()
    {
        CredentialKeys.Clear();
        var keys = await _host.Platform.Credentials.ListKeysAsync().ConfigureAwait(true);
        foreach (var key in keys)
        {
            CredentialKeys.Add(key);
        }

        CredentialSummary = $"安全凭据条目：{CredentialKeys.Count}（仅显示散列文件名，不显示明文）。";
    }
}

/// <summary>
/// One external place the settings page can send the user to.
///
/// <para>
/// Label, one line of explanation and the address itself. The address is shown beside the button rather
/// than hidden in it, because a machine with no browser is a real case and a copied address still works
/// there.
/// </para>
/// </summary>
public sealed record SupportLink(string Label, string Detail, string Url);
