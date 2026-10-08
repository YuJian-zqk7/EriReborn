using System.Collections.ObjectModel;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EriReborn.App.Shared.Services;
using EriReborn.Core.Validation;
using EriReborn.Extension;
using EriReborn.Persona;
using EriReborn.Skin;

namespace EriReborn.App.Shared.ViewModels;

/// <summary>
/// Creative Workshop, four areas (spec 38). Each one does something rather than
/// listing something:
///
/// <list type="bullet">
/// <item>皮肤文案 — reads the available skins' text, checks it, exports it.</item>
/// <item>布局编辑 — the schema-driven visual editor.</item>
/// <item>插件创建 — builds a resource plugin and writes it where the plugin page reads it.</item>
/// <item>拓展创建 — documents the extension format and exports a template.</item>
/// </list>
///
/// <para>
/// The old page was three areas of read-only lists — an extension list, a directory-name
/// box and a raw asset-sheet dump — which showed the user data and gave them nothing to
/// do with it.
/// </para>
/// </summary>
public sealed partial class WorkshopViewModel : ViewModelBase
{
    private readonly AppHost _host;

    public WorkshopViewModel(AppHost host)
    {
        _host = host;
        Title = "创意工坊";

        foreach (var skin in host.Skins.Available)
        {
            Skins.Add(new WorkshopSkinEntry(skin.Id, skin.Name, skin.Persona ?? string.Empty, skin.Layout, skin.BaseSkin));
        }

        foreach (var status in host.Extensions.Statuses)
        {
            Extensions.Add(new WorkshopExtensionEntry(
                status.Manifest.Id,
                status.Manifest.Name,
                status.Manifest.Version ?? "-",
                status.State.ToString()));
        }

        _userSkins = new UserSkinStore(host.Paths, host.Log.For("Skin"));

        SelectedSkin = Skins.FirstOrDefault();
        LayoutEditor = new LayoutEditorViewModel(host);
        PluginBuilder = new PluginBuilderViewModel(host);
        LayoutSchemaPreview = EriReborn.Layout.LayoutSerializer.Serialize(LayoutEditor.Document);
    }

    // ============================================================ A. 皮肤文案

    private readonly UserSkinStore _userSkins;

    /// <summary>Raised when the available skins changed, so the shell can rebuild its picker.</summary>
    public event EventHandler? SkinsChanged;

    public ObservableCollection<WorkshopSkinEntry> Skins { get; } = new();

    [ObservableProperty]
    private WorkshopSkinEntry? _selectedSkin;

    [ObservableProperty]
    private string _skinNameDraft = string.Empty;

    [ObservableProperty]
    private string _skinPersonaDraft = string.Empty;

    [ObservableProperty]
    private string _newSkinId = string.Empty;

    [ObservableProperty]
    private string _skinVerdict = "改完点「保存到这套皮肤」，或填一个新 id 另存为新皮肤。";

    [ObservableProperty]
    private string _skinText = string.Empty;

    [ObservableProperty]
    private string _skinExportPath = string.Empty;

    partial void OnSelectedSkinChanged(WorkshopSkinEntry? value)
    {
        SkinNameDraft = value?.Name ?? string.Empty;
        SkinPersonaDraft = value?.Persona ?? string.Empty;
        SkinVerdict = value is null
            ? "当前没有可用皮肤。"
            : _userSkins.IsUserSkin(value.Id)
                ? $"「{value.Name}」已选中，可以改了。界面暂时还是原来那套 —— 要让界面和预览都换成它，点「启用选中的皮肤」。"
                : "这套是随包皮肤，只读；要改它，先基于它创建一套你自己的。";
        SkinText = string.Empty;
        SkinExportPath = string.Empty;
        SelectedElement = null;

        // Selecting a skin selects it, and nothing else.
        //
        // This used to make a user skin the skin in force as soon as it was selected, so that the preview
        // — which draws the real interface, always in whatever skin is in force — would match what was
        // being edited. That is why a new skin changed the whole interface the moment it was created
        // (creating selects it), and why a save could appear to switch skins by itself. Switching is a
        // decision, and it belongs to 启用选中的皮肤.
        // The gallery draws a ring on the chosen entry, so exactly one entry carries the flag.
        foreach (var entry in Skins)
        {
            entry.IsSelected = ReferenceEquals(entry, value);
        }

        OnPropertyChanged(nameof(SelectedSkinIsUserSkin));
        RebuildAssetSlots();
        RebuildUiTexts();
        RebuildColors();
    }

    /// <summary>True when this skin is one the user created — it can be edited and deleted.</summary>
    public bool SelectedSkinIsUserSkin =>
        SelectedSkin is not null && _userSkins.IsUserSkin(SelectedSkin.Id);

    private SkinManifest? SelectedManifest =>
        SelectedSkin is null
            ? null
            : _host.Skins.Available.FirstOrDefault(skin => skin.Id == SelectedSkin.Id);

    /// <summary>
    /// The picture slots of the selected skin. Only the user's own skin is editable: the shipped
    /// packs are read-only, so the list stays empty for them.
    /// </summary>
    public ObservableCollection<WorkshopAssetSlot> AssetSlots { get; } = new();

    /// <summary>
    /// The pointer roles this skin may dress, one row each.
    ///
    /// <para>
    /// A cursor is the part of a skin the user meets on every single action, and it was the one part the
    /// editor never offered: the declaration existed in <c>skin.json</c> and nothing wrote it (spec 112).
    /// </para>
    /// </summary>
    public ObservableCollection<WorkshopCursor> CursorEntries { get; } = new();

    /// <summary>
    /// Selecting one of the five slots is the same act as clicking it in the real interface: one
    /// selection state, so the panel can never show one element while the highlight shows another.
    /// </summary>
    [RelayCommand]
    private void SelectSlot(WorkshopAssetSlot slot)
    {
        SelectElement(slot.Slot, null);

        foreach (var entry in AssetSlots)
        {
            entry.IsSelected = string.Equals(entry.Slot, SelectedElement?.AssetId, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// Every interface word this skin may replace, with the shipped wording beside it (spec 5/6).
    ///
    /// <para>
    /// Empty for a shipped pack, for the same reason pictures are: an edit that cannot be saved is
    /// worse than no editor.
    /// </para>
    /// </summary>
    public ObservableCollection<WorkshopUiText> UiTextEntries { get; } = new();

    /// <summary>
    /// The character's lines this skin's persona actually says, shown as a second group so the two
    /// systems stay visibly separate (spec 7): editing a UI word cannot reach the voice, and the
    /// other way round. Stored under a <c>persona.</c> prefix in the same <c>texts</c> section.
    /// </summary>
    public ObservableCollection<WorkshopUiText> PersonaTextEntries { get; } = new();

    private void RebuildUiTexts()
    {
        UiTextEntries.Clear();

        // Before the early return below: the pointer rows have to be cleared for a read-only skin too,
        // or the previous skin's rows would stay on screen after selecting a shipped pack.
        RebuildCursors();

        var manifest = SelectedManifest;
        if (manifest is null || !_userSkins.IsUserSkin(manifest.Id))
        {
            return;
        }

        var overrides = _userSkins.ReadTextOverrides(manifest.Id);

        foreach (var key in UiTexts.Keys)
        {
            overrides.TryGetValue(key, out var current);
            UiTextEntries.Add(new WorkshopUiText(key, UiTexts.Defaults[key], current));
        }

        RebuildPersonaTexts(manifest, overrides);
    }

    /// <summary>
    /// Lists the lines the skin's persona really says, as rows keyed <c>persona.&lt;line&gt;</c>. Only
    /// lines the pack defines are offered — an empty line cannot be a meaningful edit, so showing it
    /// would just be a row that does nothing.
    /// </summary>
    private void RebuildPersonaTexts(SkinManifest manifest, IReadOnlyDictionary<string, string> overrides)
    {
        PersonaTextEntries.Clear();

        var pack = string.IsNullOrWhiteSpace(manifest.Persona)
            ? PersonaCatalog.Neutral
            : _host.Personas.Find(manifest.Persona) ?? PersonaCatalog.Neutral;

        foreach (var (key, line) in pack.Messages)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            overrides.TryGetValue(PersonaVoice.Prefix + key, out var current);
            PersonaTextEntries.Add(new WorkshopUiText(PersonaVoice.Prefix + key, line, current));
        }
    }

    /// <summary>
    /// Lists the pointer roles with what this skin currently says for each, and what it may say.
    ///
    /// <para>
    /// The choices are the standard shapes and the sheet ids the library actually has, so whatever the editor
    /// saves is something the resolver understands — the same rule the slot editor follows, and the reason a
    /// free-text box was rejected here: a typo in a cursor name looks exactly like a skin that chose nothing
    /// (see <c>skin.cursor_unknown</c>).
    /// </para>
    /// </summary>
    private void RebuildCursors()
    {
        CursorEntries.Clear();
        OnPropertyChanged(nameof(HasCursorEntries));

        var manifest = SelectedManifest;
        if (manifest is null || !_userSkins.IsUserSkin(manifest.Id))
        {
            return;
        }

        var own = _userSkins.ReadCursors(manifest.Id);

        var choices = new List<string> { string.Empty };
        choices.AddRange(CursorRoles.KnownKeywords);
        choices.AddRange(_host.Assets.Sheets
            .Select(sheet => CursorSpec.AssetPrefix + sheet.Id)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(choice => choice, StringComparer.Ordinal));

        foreach (var role in CursorRoles.All)
        {
            var name = CursorRoles.NameOf(role);
            own.TryGetValue(name, out var current);

            // What this skin would use if it declared nothing, so the row can show what it inherits
            // instead of leaving the user to guess where the pointer comes from.
            manifest.Cursors.TryGetValue(name, out var inherited);

            CursorEntries.Add(new WorkshopCursor(name, DescribeCursorRole(role), choices, current, inherited));
        }

        OnPropertyChanged(nameof(HasCursorEntries));
    }

    /// <summary>The words the editor shows for a pointer role, next to the name a skin.json uses.</summary>
    private static string DescribeCursorRole(CursorRole role) => role switch
    {
        CursorRole.Default => "默认箭头（default）",
        CursorRole.Busy => "忙碌（busy）",
        CursorRole.Drag => "拖动（drag）",
        CursorRole.Forbidden => "禁止（forbidden）",
        CursorRole.ResizeHorizontal => "左右缩放（resizeHorizontal）",
        CursorRole.ResizeVertical => "上下缩放（resizeVertical）",
        CursorRole.ResizeCorner => "角缩放（resizeCorner）",
        CursorRole.Link => "可点击（link）",
        CursorRole.Text => "文本（text）",
        CursorRole.Crosshair => "十字（crosshair）",
        _ => CursorRoles.NameOf(role),
    };

    /// <summary>
    /// Writes the pointer declarations the user changed, and clears the ones emptied back to the base.
    /// Returns how many rows actually moved, so the save button can still say "没有需要保存的改动".
    /// </summary>
    private int WriteCursorOverrides(string skinId)
    {
        var own = _userSkins.ReadCursors(skinId);
        var written = 0;

        foreach (var row in CursorEntries)
        {
            var wanted = row.Spec?.Trim();
            var stored = own.TryGetValue(row.RoleName, out var declared) ? declared : null;

            var wantedValue = string.IsNullOrEmpty(wanted) ? null : wanted;

            // An empty row means "follow the base skin", which is the state it starts in — not an edit.
            if (string.Equals(wantedValue, stored, StringComparison.Ordinal))
            {
                continue;
            }

            if (_userSkins.SetCursor(skinId, row.RoleName, wantedValue))
            {
                written++;
            }
        }

        return written;
    }

    /// <summary>
    /// Points one pointer role at a picture of the user's own, which the library does not carry.
    ///
    /// <para>
    /// The picture is copied into the skin and registered as a sheet before the row is set to it: a
    /// value the resolver cannot read would leave the pointer silently back at the platform default,
    /// which is worse than offering no such choice. Saving is still the 保存 button's job, so an
    /// import can be abandoned like any other edit.
    /// </para>
    /// </summary>
    public void ImportCursorArt(WorkshopCursor row, string sourcePath)
    {
        var manifest = SelectedManifest;
        if (manifest is null || !_userSkins.IsUserSkin(manifest.Id))
        {
            SkinVerdict = "只能改你自己皮肤里的鼠标指针；随包皮肤是只读的。";
            return;
        }

        if (!_userSkins.ImportCursorArt(manifest.Id, row.RoleName, sourcePath, out var assetId, out var reason))
        {
            SkinVerdict = "导入失败：" + reason;
            return;
        }

        var spec = CursorSpec.AssetPrefix + assetId;

        // Offered before it is selected: a ComboBox whose item is missing shows an empty box, and an
        // empty box looks exactly like "follow the base skin".
        row.AddChoice(spec);
        row.Spec = spec;

        SkinVerdict = $"「{row.Title}」已用上你选的图片（{assetId}）。点上面的「保存」写进这套皮肤。";
    }

    /// <summary>
    /// Writes the rows the user changed.
    ///
    /// <para>
    /// Saved on a button rather than on every keystroke: a two-way text binding fires per character,
    /// and a file write per character is not a saving strategy.
    /// </para>
    /// </summary>
    [RelayCommand]
    private void SaveUiTexts()
    {
        var manifest = SelectedManifest;
        if (manifest is null || !_userSkins.IsUserSkin(manifest.Id))
        {
            return;
        }

        var written = WriteTextOverrides(manifest.Id);
        if (written == 0)
        {
            SkinVerdict = "界面文字没有改动。";
            return;
        }

        ReloadSkins(manifest.Id);
        SaveState = "已保存";
        SkinVerdict = $"已保存 {written} 条界面文字，界面立刻就用新说法。";
    }

    /// <summary>
    /// Writes the UI words and the persona lines the user changed, and says how many landed.
    ///
    /// <para>
    /// Split out of the save button so the editor's single 保存 can write words and colours in one
    /// pass without either half losing track of the other.
    /// </para>
    /// </summary>
    private int WriteTextOverrides(string skinId)
    {
        var overrides = _userSkins.ReadTextOverrides(skinId);
        var written = 0;

        // UI words and persona lines live in the same texts section but under different key spaces;
        // walking both keeps them one save button without letting one bleed into the other.
        foreach (var entry in UiTextEntries.Concat(PersonaTextEntries))
        {
            var wanted = entry.Value.Trim();

            // A blank row means "keep the base's wording", which is the state it starts in — not an
            // edit. Writing it as one made 保存 report changes that never happened, and rewrote the
            // file for nothing. Going back to the shipped wording is what 「恢复默认」 is for.
            if (wanted.Length == 0)
            {
                continue;
            }

            var current = overrides.TryGetValue(entry.Key, out var declared)
                ? declared
                : entry.Fallback;

            if (string.Equals(wanted, current, StringComparison.Ordinal))
            {
                continue;
            }

            // Typing the shipped wording back is not an override: writing it as one would make it
            // impossible to tell "deliberately the same" from "never touched", and the row would
            // keep offering a default it already is.
            var stored = string.Equals(wanted, entry.Fallback, StringComparison.Ordinal)
                ? null
                : wanted;

            if (_userSkins.SetText(skinId, entry.Key, stored))
            {
                written++;
            }
        }

        return written;
    }

    /// <summary>
    /// The editor's one 保存: the interface's words, the persona's lines, the colours, and the skin's
    /// own name/persona text. Pictures and icons are already written the moment they are picked, so
    /// they need no button — the panel says so instead of leaving the user to wonder.
    /// </summary>
    [RelayCommand]
    private void SaveAll()
    {
        var manifest = SelectedManifest;
        if (manifest is null)
        {
            return;
        }

        if (!_userSkins.IsUserSkin(manifest.Id))
        {
            SaveState = "未保存";
            SkinVerdict = "这套是随包皮肤，只读。要改它，先基于它创建一套你自己的。";
            return;
        }

        var texts = WriteTextOverrides(manifest.Id);
        var colours = WriteColourOverrides(manifest.Id);
        var cursors = WriteCursorOverrides(manifest.Id);

        // The name and the persona draft are part of "my skin" too, so the same button writes them.
        var name = SkinNameDraft?.Trim();
        var renamed = 0;
        if (!string.IsNullOrWhiteSpace(name)
            && !string.Equals(name, manifest.Name, StringComparison.Ordinal)
            && _userSkins.SaveText(manifest.Id, name!, SkinPersonaDraft))
        {
            renamed = 1;
        }

        if (texts == 0 && colours == 0 && cursors == 0 && renamed == 0)
        {
            SaveState = "未修改";
            SkinVerdict = "没有需要保存的改动。";
            return;
        }

        ReloadSkins(manifest.Id);
        SaveState = "已保存";
        SkinVerdict = $"已保存：{texts} 条文字、{colours} 个颜色"
            + (cursors > 0 ? $"，{cursors} 个鼠标光标" : string.Empty)
            + (renamed > 0 ? "，并改了皮肤名称" : string.Empty)
            + "。界面立刻用它。";
    }

    /// <summary>
    /// The skin editor's own state: whether the create form is open, whether a skin is being
    /// edited, and which tool the right panel is showing.
    /// </summary>
    [ObservableProperty]
    private bool _isCreatingSkin;

    [ObservableProperty]
    private bool _isEditingSkin;

    /// <summary>True while the list of skins is what the page shows, rather than the editor.</summary>
    public bool ShowSkinGallery => !IsEditingSkin;

    partial void OnIsEditingSkinChanged(bool value) => OnPropertyChanged(nameof(ShowSkinGallery));

    /// <summary>
    /// The skin the create form is building on: one of the cards the user picked, defaulting to
    /// whatever was selected when they pressed 新建皮肤.
    /// </summary>
    [ObservableProperty]
    private WorkshopSkinEntry? _createBaseSkin;

    /// <summary>The name the user typed. White space only is not a name.</summary>
    [ObservableProperty]
    private string _newSkinName = string.Empty;

    /// <summary>Which tool the right panel shows: 选择 / 图片 / 图标 / 文本 / 颜色 / 资源 / 光标 / 布局.</summary>
    [ObservableProperty]
    private string _paletteMode = "图片";

    /// <summary>
    /// True while the layout tool is the one in hand.
    ///
    /// <para>
    /// Layout work is a tool inside the skin editor rather than a page of its own: the same skin, the same
    /// page tree, one more thing you can do to it. While this is on, the editor gives the whole of its
    /// middle to the layout canvas and the components that go on it.
    /// </para>
    /// </summary>
    public bool ShowLayoutTool => string.Equals(PaletteMode, "布局", StringComparison.Ordinal);

    public bool ShowPicturePanel => PaletteMode is "图片" or "图标";

    public bool ShowTextPanel => string.Equals(PaletteMode, "文本", StringComparison.Ordinal);

    public bool ShowColorPanel => string.Equals(PaletteMode, "颜色", StringComparison.Ordinal);

    public bool ShowAssetPanel => string.Equals(PaletteMode, "资源", StringComparison.Ordinal);

    /// <summary>
    /// True while the pointer tool is the one in hand: every role a skin may declare, each with the
    /// shapes and library pictures it is allowed to point at.
    /// </summary>
    public bool ShowCursorPanel => string.Equals(PaletteMode, "光标", StringComparison.Ordinal);

    /// <summary>
    /// False for a shipped pack. Only a skin of your own can carry pointer overrides, so the panel says
    /// why it is empty instead of showing a blank list that reads as a broken tool.
    /// </summary>
    public bool HasCursorEntries => CursorEntries.Count > 0;

    /// <summary>
    /// True while the tool is the mouse itself.
    ///
    /// <para>
    /// The other tools say what the panel beside the page should be; this one says nothing should
    /// change, which is a distinct job: pointing at the interface to see what a thing is, before
    /// deciding to edit it. Without it, "select" was not a state at all — every click switched the panel
    /// to whatever the clicked element happened to be, which reads as the editor deciding for you.
    /// </para>
    /// </summary>
    public bool ShowSelectionPanel => string.Equals(PaletteMode, "选择", StringComparison.Ordinal);

    partial void OnPaletteModeChanged(string value)
    {
        OnPropertyChanged(nameof(ShowPicturePanel));
        OnPropertyChanged(nameof(ShowTextPanel));
        OnPropertyChanged(nameof(ShowColorPanel));
        OnPropertyChanged(nameof(ShowAssetPanel));
        OnPropertyChanged(nameof(ShowCursorPanel));
        OnPropertyChanged(nameof(ShowSelectionPanel));
        OnPropertyChanged(nameof(ShowLayoutTool));
    }

    [RelayCommand]
    private void SelectPalette(string mode)
    {
        PaletteMode = mode;
        SkinVerdict = mode switch
        {
            "选择" => "鼠标工具：点界面上的任何元素，右边只告诉它是什么，不改任何东西。",
            "布局" => "布局：中部换成了画布，可以加组件、拖位置、改大小和形状；下面还有同一份文档的实时预览。",
            "文本" => "在下面改界面上的字；改完点「保存界面文字」。",
            "颜色" => "改完点「保存颜色」，界面立刻换色。",
            "资源" => "立绘、搭档、标志、空态、加载——点一个就能换。",
            "光标" => "鼠标指针：给每个场合（默认箭头、忙碌、可点击…）挑形状或图片；改完点上面的「保存」。",
            _ => "在左边的真实界面上点一个图片或图标，右边就能替换它。",
        };
    }

    /// <summary>Opens the create form on the skin that is selected right now.</summary>
    [RelayCommand]
    private void BeginCreateSkin()
    {
        CreateBaseSkin = SelectedSkin;
        NewSkinName = string.Empty;
        NewSkinId = string.Empty;
        IsCreatingSkin = true;
        IsEditingSkin = false;
        SkinVerdict = "给你的新皮肤起个名字，底稿默认就是你现在这套（未修改的部分继续用它）。";
    }

    [RelayCommand]
    private void CancelCreateSkin()
    {
        IsCreatingSkin = false;
        SkinVerdict = "已取消。";
    }

    /// <summary>Leaves the editor, back to the list of skins.</summary>
    [RelayCommand]
    private void CloseEditor()
    {
        IsEditingSkin = false;
        SkinVerdict = "已回到我的皮肤。改动都已经写在你的皮肤文件里。";
    }

    /// <summary>
    /// Opens the editor on a skin from the list. A shipped pack opens as a read-only view with the
    /// offer to build on it, which is what "官方皮肤无法直接修改" means in practice.
    /// </summary>
    [RelayCommand]
    private void EditSkin(WorkshopSkinEntry? entry)
    {
        if (entry is null)
        {
            return;
        }

        SelectedSkin = entry;

        if (!_userSkins.IsUserSkin(entry.Id))
        {
            IsEditingSkin = false;

            // The only way forward from a read-only pack is to build on it, so the form is opened
            // here instead of leaving the user to go and find the button the message just named.
            // The base skin is already the one they clicked, which is what they meant by "edit this".
            BeginCreateSkin();
            SkinVerdict =
                $"「{entry.Name}」是官方皮肤，只读，不能直接改；已经打开「新建皮肤」表单，基于它建一套你自己的。";
            return;
        }

        IsEditingSkin = true;
        IsCreatingSkin = false;
        PaletteMode = "图片";
        SkinVerdict = $"正在编辑「{entry.Name}」。点界面上的图片或文字就能改。";
    }

    /// <summary>
    /// The name the skin will be stored under, offered rather than asked for.
    ///
    /// <para>
    /// A user should not have to invent an ASCII folder name to make a skin, so one is derived from
    /// what they typed and shown. When nothing usable can be derived — a name that is entirely
    /// Chinese, say — a plain fallback is used and made unique, which is honest about what the
    /// folder will be called.
    /// </para>
    /// </summary>
    public string SuggestSkinId()
    {
        var slug = new string(NewSkinName
            .ToLowerInvariant()
            .Select(ch => char.IsAsciiLetterOrDigit(ch) ? ch : '_')
            .ToArray())
            .Trim('_');

        while (slug.Contains("__", StringComparison.Ordinal))
        {
            slug = slug.Replace("__", "_", StringComparison.Ordinal);
        }

        if (slug.Length == 0 || !DirectoryNameValidator.ValidateName(slug).IsValid)
        {
            slug = "custom_skin";
        }

        var candidate = slug;
        var suffix = 1;
        while (Skins.Any(entry => string.Equals(entry.Id, candidate, StringComparison.Ordinal)))
        {
            candidate = $"{slug}_{++suffix}";
        }

        return candidate;
    }

    partial void OnNewSkinNameChanged(string value)
    {
        NewSkinId = string.IsNullOrWhiteSpace(value) ? string.Empty : SuggestSkinId();
        OnPropertyChanged(nameof(NewSkinNameCounter));
    }

    /// <summary>The "7 / 50" the create form shows, so the limit is visible before it is hit.</summary>
    public string NewSkinNameCounter => $"{NewSkinName.Length} / {MaxSkinNameLength}";

    /// <summary>Longer than this and a name stops fitting on a card.</summary>
    public const int MaxSkinNameLength = 50;

    /// <summary>
    /// Restores the skin to pure inheritance: nothing but the base and the name. Two steps, because
    /// it throws away work the user did by hand.
    /// </summary>
    [ObservableProperty]
    private bool _resetArmed;

    [RelayCommand]
    private void ResetSkin()
    {
        var manifest = SelectedManifest;
        if (manifest is null || !_userSkins.IsUserSkin(manifest.Id))
        {
            return;
        }

        if (!ResetArmed)
        {
            ResetArmed = true;
            SkinVerdict = "再点一次「重置」就会丢掉你所有的改动，只留底稿继承。";
            return;
        }

        ResetArmed = false;

        if (!_userSkins.ResetOverrides(manifest.Id))
        {
            SkinVerdict = "重置失败（见日志）。";
            return;
        }

        var id = manifest.Id;
        ReloadSkins(id);
        SaveState = "已保存";
        SkinVerdict = "已重置：所有改动都清掉了，未修改的部分继续用底稿。";
    }

    /// <summary>The colours this skin can change, with the base's value beside each.</summary>
    public ObservableCollection<WorkshopColor> ColorEntries { get; } = new();

    private void RebuildColors()
    {
        ColorEntries.Clear();

        var manifest = SelectedManifest;
        if (manifest is null || !_userSkins.IsUserSkin(manifest.Id))
        {
            return;
        }

        var baseManifest = manifest.BaseSkin is { Length: > 0 } baseId
            ? _host.Skins.Available.FirstOrDefault(skin => string.Equals(skin.Id, baseId, StringComparison.Ordinal))
            : null;

        var overrides = _userSkins.ReadColorOverrides(manifest.Id);

        foreach (var key in manifest.Colors.Keys.OrderBy(key => key, StringComparer.Ordinal))
        {
            var fallback = baseManifest?.Colors.GetValueOrDefault(key) ?? manifest.Colors[key];
            overrides.TryGetValue(key, out var current);
            ColorEntries.Add(new WorkshopColor(key, ColourLabel(key), fallback, current ?? fallback));
        }
    }

    /// <summary>
    /// What each manifest colour paints. The list is the skin schema's own vocabulary (spec 40), so
    /// an unknown key is shown as itself rather than hidden.
    /// </summary>
    private static string ColourLabel(string key) => key switch
    {
        "accent" => "强调色（按钮、选中）",
        "background" => "窗口底色",
        "surface" => "卡片底色",
        "surfaceAlt" => "次级底色（画布、条带）",
        "foreground" => "正文文字",
        "muted" => "次要文字",
        "border" => "边框",
        _ => key,
    };

    /// <summary>Writes the colours the user changed; unchanged rows are left alone.</summary>
    [RelayCommand]
    private void SaveColors()
    {
        var manifest = SelectedManifest;
        if (manifest is null || !_userSkins.IsUserSkin(manifest.Id))
        {
            return;
        }

        var written = WriteColourOverrides(manifest.Id);
        if (written == 0)
        {
            SkinVerdict = "颜色没有改动。";
            return;
        }

        ReloadSkins(manifest.Id);
        SaveState = "已保存";
        SkinVerdict = $"已保存 {written} 个颜色，界面立刻换成这套配色。";
    }

    /// <summary>Writes the colour rows the user changed, and says how many landed.</summary>
    private int WriteColourOverrides(string skinId)
    {
        var overrides = _userSkins.ReadColorOverrides(skinId);
        var written = 0;

        foreach (var entry in ColorEntries)
        {
            var wanted = entry.Value.Trim();
            var current = overrides.TryGetValue(entry.Key, out var declared) ? declared : entry.Fallback;

            if (string.Equals(wanted, current, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // Typing the base's own value back is not an override: it must be clearable again.
            var stored = string.Equals(wanted, entry.Fallback, StringComparison.OrdinalIgnoreCase) ? null : wanted;

            if (_userSkins.SetColor(skinId, entry.Key, stored))
            {
                written++;
            }
        }

        return written;
    }

    /// <summary>Drops one colour override, so the base's colour comes back.</summary>
    [RelayCommand]
    private void RestoreColor(WorkshopColor entry)
    {
        var manifest = SelectedManifest;
        if (manifest is null || !_userSkins.IsUserSkin(manifest.Id))
        {
            return;
        }

        if (!_userSkins.SetColor(manifest.Id, entry.Key, null))
        {
            SkinVerdict = $"「{entry.Key}」恢复失败（见日志）。";
            return;
        }

        var id = manifest.Id;
        ReloadSkins(id);
        SkinVerdict = $"「{entry.Label}」已恢复为底稿的颜色。";
    }

    /// <summary>Drops one override, so the shipped wording comes back.</summary>
    [RelayCommand]
    private void RestoreUiText(WorkshopUiText entry)
    {
        var manifest = SelectedManifest;
        if (manifest is null || !_userSkins.IsUserSkin(manifest.Id))
        {
            return;
        }

        if (!_userSkins.SetText(manifest.Id, entry.Key, null))
        {
            SkinVerdict = $"「{entry.Key}」恢复失败：写不进 {_userSkins.ManifestPath(manifest.Id)}。";
            return;
        }

        var id = manifest.Id;
        ReloadSkins(id);
        SkinVerdict = $"「{entry.Key}」已恢复为出厂文案。";
    }

    /// <summary>
    /// What the user clicked in the preview: something the real interface drew from a named picture
    /// or a named word. Nothing is guessed from a list — the control that was clicked said what it
    /// is.
    /// </summary>
    [ObservableProperty]
    private WorkshopPreviewElement? _selectedElement;

    /// <summary>
    /// What the right panel says while nothing is selected: an instruction, or the reason the thing
    /// that was clicked has nothing to change. A blank panel leaves the user unsure whether the
    /// click registered at all.
    /// </summary>
    [ObservableProperty]
    private string _selectionHint = "在中间的真实界面上点一个图片、图标或文字。";

    /// <summary>
    /// Where the edits stand: 未修改 / 已保存 / 保存失败. Without it a save button gives no answer
    /// to the only question the user has — did that stick?
    /// </summary>
    [ObservableProperty]
    private string _saveState = "未修改";

    /// <summary>
    /// Picks up something clicked in the real interface.
    ///
    /// <para>
    /// The view hands over the name the control draws from — an asset id for a picture, a text key
    /// for a word — and the page reads the rest from the skin: what it is now, what the base skin
    /// uses, and whether the user already changed it.
    /// </para>
    /// </summary>
    public void SelectElement(string? assetId, string? textKey)
    {
        var manifest = SelectedManifest;
        if (manifest is null)
        {
            SelectedElement = null;
            return;
        }

        if (!string.IsNullOrWhiteSpace(textKey))
        {
            var word = manifest.Texts.TryGetValue(textKey, out var declared) && !string.IsNullOrWhiteSpace(declared)
                ? declared
                : UiTexts.Defaults.GetValueOrDefault(textKey, textKey);

            SelectedElement = new WorkshopPreviewElement(
                "文本（Text）",
                textKey,
                AssetId: string.Empty,
                TextKey: textKey,
                Current: word,
                Default: UiTexts.Defaults.GetValueOrDefault(textKey, textKey),
                CurrentFile: null,
                IsOverridden: _userSkins.ReadTextOverrides(manifest.Id).ContainsKey(textKey),
                Info: null);

            SkinVerdict = $"已选中文字「{textKey}」：它的说法在下面的「界面文字」里改。";
            return;
        }

        if (string.IsNullOrWhiteSpace(assetId))
        {
            SelectedElement = null;
            return;
        }

        var overrides = _userSkins.ReadOverrides(manifest.Id);
        var overridden = overrides.ContainsKey(assetId);
        var currentId = overridden ? manifest.Assets.GetValueOrDefault(assetId, assetId) : assetId;
        var file = PreviewFileFor(manifest.Id, currentId);

        SelectedElement = new WorkshopPreviewElement(
            "图片（Image）",
            assetId,
            AssetId: assetId,
            TextKey: string.Empty,
            Current: currentId,
            Default: assetId,
            CurrentFile: file,
            IsOverridden: overridden,
            Info: ImageInfo.Describe(file));

        SkinVerdict = $"已选中图片「{assetId}」：换图、恢复默认都在右边。";
    }

    /// <summary>
    /// What the user clicked, described by the control itself rather than by a list the editor keeps.
    ///
    /// <para>
    /// This is the entry point for ordinary interface elements — a plain <c>TextBlock</c>, a
    /// <c>Button</c>, a <c>Border</c>, an icon — which the editor could not reach before, because
    /// only two controls used to announce their own names. Each kind is routed to the tool that can
    /// actually change it, and a container that has nothing to change says so instead of doing
    /// nothing.
    /// </para>
    /// </summary>
    public void SelectElement(SkinElementDescriptor element)
    {
        ArgumentNullException.ThrowIfNull(element);

        if (element.IsLayout)
        {
            SelectedElement = null;
            SelectionHint = $"「{element.DisplayName}」是布局／控件本身，没有可以直接改的皮肤内容。位置和大小请用左边的「布局」。";
            SkinVerdict = SelectionHint;
            return;
        }

        if (element.IsColor && !string.IsNullOrWhiteSpace(element.ColorKey))
        {
            SelectColourElement(element);
            return;
        }

        if (element.IsText && !string.IsNullOrWhiteSpace(element.TextKey))
        {
            PaletteMode = "文本";
            SelectElement(null, element.TextKey);
            AnnotateSelection(element);
            return;
        }

        if (element.IsArt && !string.IsNullOrWhiteSpace(element.AssetId))
        {
            PaletteMode = element.Kind == SkinElementKind.Icon ? "图标" : "图片";
            SelectElement(element.AssetId, null);
            AnnotateSelection(element);
            return;
        }

        // A button whose word is not in the text table, or an element with nothing declared. Saying
        // so beats a click that silently does nothing.
        SelectedElement = null;
        SelectionHint = $"「{element.DisplayName}」现在没有可改的内容。";
        SkinVerdict = SelectionHint;
    }

    /// <summary>Clears the selection, e.g. when the user clicks empty space in the preview.</summary>
    public void ClearSelection()
    {
        SelectedElement = null;
        SelectionHint = "在中间的真实界面上点一个图片、图标或文字。";
        SkinVerdict = SelectionHint;
    }

    /// <summary>Adds the clicked element's own name and type to what the right panel shows.</summary>
    private void AnnotateSelection(SkinElementDescriptor element)
    {
        if (SelectedElement is not { } selected)
        {
            return;
        }

        SelectedElement = selected with
        {
            ElementId = element.Id,
            DisplayName = element.DisplayName,
            ElementTypeText = element.KindText,
        };

        SkinVerdict = $"已选中「{element.DisplayName}」：在右边改。";
    }

    /// <summary>Selecting a coloured surface points the panel at the colour behind it.</summary>
    private void SelectColourElement(SkinElementDescriptor element)
    {
        var manifest = SelectedManifest;
        if (manifest is null || !_userSkins.IsUserSkin(manifest.Id))
        {
            SelectedElement = null;
            SelectionHint = "这套是随包皮肤，只读。要改它，先基于它创建一套你自己的。";
            SkinVerdict = SelectionHint;
            return;
        }

        var key = element.ColorKey!;
        var entry = ColorEntries.FirstOrDefault(candidate =>
            string.Equals(candidate.Key, key, StringComparison.OrdinalIgnoreCase));

        PaletteMode = "颜色";
        SelectedElement = new WorkshopPreviewElement(
            "颜色（Color）",
            element.DisplayName,
            AssetId: string.Empty,
            TextKey: string.Empty,
            Current: entry?.Value,
            Default: entry?.Fallback,
            CurrentFile: null,
            IsOverridden: entry?.IsOverridden ?? false,
            Info: null)
        {
            ElementId = element.Id,
            DisplayName = element.DisplayName,
            ElementTypeText = element.KindText,
            ColorKey = key,
        };

        SkinVerdict = $"已选中「{element.DisplayName}」：颜色在右边改，改完点「保存颜色」。";
    }

    /// <summary>
    /// Replaces any picture the interface names — a slot, a nav icon, a button's art.
    ///
    /// <para>
    /// Called from the view, which is the only layer that can open a file picker; copying the file
    /// into the skin and filing it under the clicked name belongs to the store.
    /// </para>
    /// </summary>
    public void ReplaceElementArt(string sourcePath)
    {
        var manifest = SelectedManifest;
        var element = SelectedElement;

        if (manifest is null || element is null || !element.IsArt)
        {
            return;
        }

        if (!_userSkins.IsUserSkin(manifest.Id))
        {
            SkinVerdict = "这套是随包皮肤，只读。要改它，先基于它创建一套你自己的。";
            return;
        }

        if (!_userSkins.ReplaceAsset(manifest.Id, element.AssetId, sourcePath, out var reason))
        {
            SkinVerdict = "替换失败：" + reason;
            return;
        }

        var id = manifest.Id;
        var name = element.AssetId;
        ReloadSkins(id);
        SelectElement(name, null);
        SaveState = "已保存";
        SkinVerdict = $"「{name}」已换成你选的图片，界面现在就用它。";
    }

    /// <summary>Drops the replacement, so the picture the base skin ships comes back.</summary>
    [RelayCommand]
    private void RestoreElementArt()
    {
        var manifest = SelectedManifest;
        var element = SelectedElement;

        if (manifest is null || element is null || !element.IsArt)
        {
            return;
        }

        if (!_userSkins.RestoreAsset(manifest.Id, element.AssetId))
        {
            SkinVerdict = $"恢复失败：写不进 {_userSkins.ManifestPath(manifest.Id)}。";
            return;
        }

        var id = manifest.Id;
        var name = element.AssetId;
        ReloadSkins(id);
        SelectElement(name, null);
        SkinVerdict = $"「{name}」已恢复为底稿的那张图。";
    }

    private void RebuildAssetSlots()
    {
        var manifest = SelectedManifest;
        if (manifest is null || !_userSkins.IsUserSkin(manifest.Id))
        {
            AssetSlots.Clear();
            return;
        }

        // Each slot names a sheet id, so "replace the icon" means "point it at another sheet".
        // The art has to exist in the asset manifest before a skin can name it; the empty entry
        // is how a slot is cleared back to "this skin has none".
        var choices = new List<string> { string.Empty };
        choices.AddRange(_host.Assets.Sheets
            .Select(sheet => sheet.Id)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(id => id, StringComparer.Ordinal));

        // Read from the skin's own file, not from the merged manifest: "this slot points at a
        // shipped picture" and "the user changed this slot" are different statements, and only the
        // second one has something to restore.
        var overrides = _userSkins.ReadOverrides(manifest.Id);

        // What "恢复默认" goes back to is the base skin's choice for the slot, so the base is looked
        // up rather than assumed to be the merged view.
        var baseManifest = manifest.BaseSkin is { Length: > 0 } baseId
            ? _host.Skins.Available.FirstOrDefault(skin => string.Equals(skin.Id, baseId, StringComparison.Ordinal))
            : null;

        // Updated in place rather than rebuilt: this runs from the picker's own change handler,
        // and clearing the items out from under the control mid-selection loses the selection.
        for (var index = 0; index < SkinAssets.Slots.Count; index++)
        {
            var slot = SkinAssets.Slots[index];
            var current = SkinAssets.IdFor(manifest, slot);
            var state = new WorkshopSlotState(
                slot,
                current,
                overrides.ContainsKey(slot),
                PreviewFileFor(manifest.Id, current),
                SkinAssets.IdFor(baseManifest, slot),
                choices);

            if (index < AssetSlots.Count && string.Equals(AssetSlots[index].Slot, slot, StringComparison.Ordinal))
            {
                AssetSlots[index].Update(state);
                continue;
            }

            AssetSlots.Add(new WorkshopAssetSlot(state, ApplySlot));
        }

        while (AssetSlots.Count > SkinAssets.Slots.Count)
        {
            AssetSlots.RemoveAt(AssetSlots.Count - 1);
        }

        // A rebuild must not drop what the user had picked; the highlight follows the slot name.
        foreach (var entry in AssetSlots)
        {
            entry.IsSelected = string.Equals(entry.Slot, SelectedElement?.AssetId, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// Where the picture for a slot actually lives.
    ///
    /// <para>
    /// The user's own pack is asked first: the app registers the art of the skin that is in force,
    /// and the skin being edited usually is not that one — so only the shipped path would resolve,
    /// and a replaced picture would appear to have done nothing.
    /// </para>
    /// </summary>
    private string? PreviewFileFor(string skinId, string? assetId)
        => string.IsNullOrWhiteSpace(assetId)
            ? null
            : _userSkins.ResolveArtPath(skinId, assetId) ?? _host.Assets.ResolvePath(assetId);

    /// <summary>
    /// Replaces one slot with a picture the user picked.
    ///
    /// <para>
    /// The file is copied into their skin and declared there rather than referenced where it was
    /// picked from: a skin pointing at the user's Downloads folder breaks the moment they move the
    /// original. Called from the view, which is the only layer that can open a file picker.
    /// </para>
    /// </summary>
    public void ReplaceSlotArt(string slot, string sourcePath)
    {
        var manifest = SelectedManifest;
        if (manifest is null || !_userSkins.IsUserSkin(manifest.Id))
        {
            SkinVerdict = "只能替换你自己皮肤里的图片；随包皮肤是只读的。";
            return;
        }

        if (!_userSkins.ImportArt(manifest.Id, slot, sourcePath, out var reason))
        {
            SkinVerdict = "替换失败：" + reason;
            return;
        }

        var id = manifest.Id;
        ReloadSkins(id);
        SkinVerdict = $"「{slot}」已换成你选的图片，界面现在就用它。";
    }

    /// <summary>Drops this slot's override, so the base skin's picture comes back.</summary>
    public void RestoreSlotArt(string slot)
    {
        var manifest = SelectedManifest;
        if (manifest is null || !_userSkins.IsUserSkin(manifest.Id))
        {
            return;
        }

        var id = manifest.Id;
        if (!_userSkins.SetSlot(id, slot, null))
        {
            SkinVerdict = $"恢复失败：写不进 {_userSkins.ManifestPath(id)}。";
            return;
        }

        ReloadSkins(id);
        SkinVerdict = $"「{slot}」已恢复为父皮肤的那张图。";
    }

    private void ApplySlot(string slot, string? assetId)
    {
        var manifest = SelectedManifest;
        if (manifest is null || !_userSkins.IsUserSkin(manifest.Id))
        {
            return;
        }

        var id = manifest.Id;
        if (!_userSkins.SetSlot(id, slot, assetId))
        {
            SkinVerdict = "改不了这套皮肤的资源槽（见日志）。";
            return;
        }

        ReloadSkins(id);
        SkinVerdict = string.IsNullOrWhiteSpace(assetId)
            ? $"已清空「{slot}」：界面会回到没有这张图的样子。"
            : $"「{slot}」已换成 {assetId}。";
    }

    /// <summary>
    /// Saves the edited text into one of the user's own skins.
    ///
    /// <para>
    /// A shipped pack is read-only, and the answer to "I want to change it" is not to edit it in
    /// place but to build on it — so a shipped skin is refused with the step that unblocks it.
    /// </para>
    /// </summary>
    [RelayCommand]
    private void SaveSkinText()
    {
        if (SelectedSkin is null)
        {
            SkinVerdict = "请先选择一个皮肤。";
            return;
        }

        var id = SelectedSkin.Id;
        if (!_userSkins.IsUserSkin(id))
        {
            SkinVerdict = $"「{SelectedSkin.Name}」是随包的皮肤，只读。先在下面填个新 id 点「创建」，基于它建一套你自己的，再改你的那套。";
            return;
        }

        var name = SkinNameDraft.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            SkinVerdict = "皮肤名称不能为空。";
            return;
        }

        if (!_userSkins.SaveText(id, name, SkinPersonaDraft))
        {
            SkinVerdict = $"保存失败：写不进 {_userSkins.ManifestPath(id)}（见日志）。";
            return;
        }

        ReloadSkins(id);
        SkinVerdict = $"已写入你自己的皮肤「{name}」（{_userSkins.ManifestPath(id)}）。";
    }

    /// <summary>
    /// Raised when this page wants a skin to become the one in force. The shell owns that decision —
    /// the page has no reference to it — and applying the skin is also what makes the preview show
    /// the user's own pictures rather than the base skin's.
    /// </summary>
    public event EventHandler<string>? ActivateSkinRequested;

    /// <summary>
    /// Creates a new skin that is a full copy of the selected one, under a new id, with the text
    /// currently in the boxes. Copying rather than starting empty is the point: the new skin
    /// already has the colours, sizes and pictures, and the user changes the parts they care
    /// about.
    /// </summary>
    [RelayCommand]
    private void CreateSkin()
    {
        var baseEntry = CreateBaseSkin ?? SelectedSkin;
        if (baseEntry is null)
        {
            SkinVerdict = "先选一个底稿皮肤。";
            return;
        }

        var baseManifest = _host.Skins.Available.FirstOrDefault(
            skin => string.Equals(skin.Id, baseEntry.Id, StringComparison.Ordinal));

        if (baseManifest is null)
        {
            SkinVerdict = "找不到这套底稿的清单。";
            return;
        }

        var name = NewSkinName.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            SkinVerdict = "新皮肤要有名字。";
            return;
        }

        if (name.Length > MaxSkinNameLength)
        {
            SkinVerdict = $"名字最多 {MaxSkinNameLength} 个字。";
            return;
        }

        // The folder name is derived from what the user typed and shown to them, so nobody has to
        // invent an ASCII id to make a skin.
        var id = string.IsNullOrWhiteSpace(NewSkinId) ? SuggestSkinId() : NewSkinId.Trim();

        if (!_userSkins.CreateFrom(baseManifest, id, name, SkinPersonaDraft, out var reason))
        {
            SkinVerdict = "创建失败：" + reason;
            return;
        }

        ReloadSkins(id);
        IsCreatingSkin = false;
        IsEditingSkin = true;
        PaletteMode = "图片";

        // Creating a skin opens it for editing and nothing more. It used to become the skin in force on
        // creation, so the interface changed under the user before they had decided anything — and if
        // the copy could not be resolved, the list fell back to the first pack and the interface
        // changed to that instead. Switching is the 启用 button's job.
        SkinVerdict = SelectedSkin is null
            ? $"已创建「{name}」（{id}），底稿是 {baseEntry.Name}，但它在皮肤列表里没读出来（见日志）。"
            : $"已创建「{name}」（{id}），底稿是 {baseEntry.Name}。现在编辑的就是它；要让界面换成它，点下面的「启用选中的皮肤」。";
    }

    /// <summary>Makes a skin the one in force — from a card, or from the editor.</summary>
    [RelayCommand]
    private void ActivateSkin(WorkshopSkinEntry? entry)
    {
        if (entry is null)
        {
            return;
        }

        SelectedSkin = entry;
        ActivateSkinRequested?.Invoke(this, entry.Id);
        SkinVerdict = $"已启用「{entry.Name}」，界面现在就是它。";
    }

    /// <summary>Deletes one of the user's own skins. A shipped pack is not ours to delete.</summary>
    [RelayCommand]
    private async Task DeleteSkinTextAsync()
    {
        if (SelectedSkin is null)
        {
            SkinVerdict = "请先选择一个皮肤。";
            return;
        }

        // Asked before anything is touched: the button sits next to 「启用」, and there is no undo for a
        // deleted skin anywhere in the interface (spec 56).
        if (!await _host.ConfirmAsync(
                "删除这套皮肤？",
                $"「{SelectedSkin.Name}」会被删掉，界面上没有撤销。",
                "删除",
                "取消"))
        {
            SkinVerdict = "已取消删除。";
            return;
        }

        var id = SelectedSkin.Id;

        if (!_userSkins.IsUserSkin(id))
        {
            SkinVerdict = $"「{SelectedSkin.Name}」是随包的皮肤，只读，也没有可删的东西。";
            return;
        }

        if (!_userSkins.Delete(id))
        {
            SkinVerdict = $"删不掉 {id}（见日志）。";
            return;
        }

        ReloadSkins(null);
        SkinVerdict = $"已删除你自己的皮肤 {id}。";
    }

    /// <summary>
    /// Re-reads the skins after an override changed. The selection is kept when it survives,
    /// and the shell is told so its picker can show a skin the user just created.
    /// </summary>
    private void ReloadSkins(string? selectId)
    {
        // Read before the list is touched, and read nothing afterwards.
        //
        // The skin list is bound to a ListBox whose SelectedItem is two-way, so the moment the list is
        // emptied the control writes null back into SelectedSkin. Reading the selection after the
        // rebuild therefore reads the control's write-back, not the user's choice — the id being edited
        // was already gone, and the fallback below then selected whatever came first, which is the first
        // shipped skin. That is what made 保存 look like it jumped to another skin while editing.
        var wantedId = selectId ?? SelectedSkin?.Id;

        _host.ReloadUserSkins();

        RebuildEntries();

        SelectedSkin = (wantedId is null ? null : Skins.FirstOrDefault(entry => entry.Id == wantedId))
            ?? Skins.FirstOrDefault();

        OnPropertyChanged(nameof(SelectedSkinIsUserSkin));
        RebuildAssetSlots();
        RebuildUiTexts();
        RebuildColors();
        SkinsChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Rebuilds the entries where they stand, never by emptying the list first.
    ///
    /// <para>
    /// A control bound to this list treats an emptied list as "nothing is selected" and answers by
    /// choosing for the user; replacing entries in place never shows it that state. The entries
    /// themselves are immutable, so a rebuilt entry is a new instance at the same index — which is
    /// exactly the notification a picker already knows how to follow.
    /// </para>
    /// </summary>
    private void RebuildEntries()
    {
        var available = _host.Skins.Available;

        for (var index = 0; index < available.Count; index++)
        {
            var skin = available[index];
            var entry = new WorkshopSkinEntry(
                skin.Id, skin.Name, skin.Persona ?? string.Empty, skin.Layout, skin.BaseSkin);

            if (index < Skins.Count && string.Equals(Skins[index].Id, skin.Id, StringComparison.Ordinal))
            {
                Skins[index] = entry;
            }
            else if (index < Skins.Count)
            {
                Skins.Insert(index, entry);
            }
            else
            {
                Skins.Add(entry);
            }
        }

        // Whatever the list still holds beyond the skins that exist is a skin that is gone — the one
        // the user deleted, most often — so it leaves from the tail.
        while (Skins.Count > available.Count)
        {
            Skins.RemoveAt(Skins.Count - 1);
        }
    }

    /// <summary>
    /// Checks what the workshop can honestly check: the id rule the loader enforces,
    /// and whether the skin carries the text a skin is supposed to carry.
    /// </summary>
    [RelayCommand]
    private void ValidateSkinText()
    {
        if (SelectedSkin is null)
        {
            SkinVerdict = "请先选择一个皮肤。";
            return;
        }

        var issues = new List<string>();

        if (!DirectoryNameValidator.ValidateName(SelectedSkin.Id).IsValid)
        {
            issues.Add($"id '{SelectedSkin.Id}' 不是合法目录名");
        }

        if (string.IsNullOrWhiteSpace(SelectedSkin.Persona))
        {
            issues.Add("persona 为空");
        }

        if (string.IsNullOrWhiteSpace(SelectedSkin.Layout))
        {
            issues.Add("layout 为空");
        }

        if (Skins.Count(skin => string.Equals(skin.Id, SelectedSkin.Id, StringComparison.Ordinal)) > 1)
        {
            issues.Add("id 重复");
        }

        SkinVerdict = issues.Count == 0
            ? $"{SelectedSkin.Name}：id、persona、layout 都齐了。"
            : string.Join("；", issues);
    }

    /// <summary>Exports every skin's text so it can be reviewed or translated outside the app.</summary>
    [RelayCommand]
    private void ExportSkinText()
    {
        try
        {
            var directory = Path.Combine(_host.Paths.UserDataDirectory, "workshop");
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, "skin-texts.json");

            var payload = new JsonArray();
            foreach (var skin in Skins)
            {
                payload.Add(new JsonObject
                {
                    ["id"] = skin.Id,
                    ["name"] = skin.Name,
                    ["persona"] = skin.Persona,
                    ["layout"] = skin.Layout,
                });
            }

            var json = payload.ToJsonString(Relaxed);
            File.WriteAllText(path, json);

            SkinText = json;
            SkinExportPath = path;
            SkinVerdict = $"已导出 {Skins.Count} 个皮肤的文案。";
        }
        catch (Exception ex)
        {
            SkinVerdict = $"导出失败：{ex.GetType().Name}: {ex.Message}";
        }
    }

    // ============================================================ B. 布局编辑

    /// <summary>The visual layout editor (spec 38/39).</summary>
    public LayoutEditorViewModel LayoutEditor { get; }

    /// <summary>Schema-driven layout, never raw XAML (spec 39).</summary>
    public string LayoutSchemaPreview { get; }

    // ============================================================ C. 插件创建

    /// <summary>Builds a .eriplugin.json into the folder the plugin page imports from.</summary>
    public PluginBuilderViewModel PluginBuilder { get; }

    // ============================================================ D. 拓展创建

    public ObservableCollection<WorkshopExtensionEntry> Extensions { get; } = new();

    [ObservableProperty]
    private string _extensionTemplatePath = string.Empty;

    /// <summary>The manifest fields the loader actually reads, in the order it reads them.</summary>
    public IReadOnlyList<WorkshopField> ExtensionManifestFields { get; } = new[]
    {
        new WorkshopField("id", "必填。目录名与标识，必须能当目录名用（ASCII、小写、无空格）。"),
        new WorkshopField("name", "显示名。缺省时退回 id。"),
        new WorkshopField("version", "版本号，建议语义化版本。"),
        new WorkshopField("assembly", "程序集文件名（.dll）。只做数据贡献的扩展可以省略。"),
        new WorkshopField("author", "作者。"),
        new WorkshopField("description", "一句话说明，会显示在扩展页。"),
        new WorkshopField("permissions", "声明的权限数组，见下表；未声明的能力拿不到。"),
        new WorkshopField("dependencies", "依赖的其它扩展 id，缺一个就不加载。"),
        new WorkshopField("categories", "本扩展贡献的分区 id，同样要符合官方命名规则。"),
        new WorkshopField("isolation", "\"process\" 表示在独立进程里跑；其它写法按进程内处理。"),
        new WorkshopField("trust", "文件里写什么都不算数——信任级别由签名校验决定。"),
    };

    /// <summary>The permissions the validator knows. Anything else is rejected on load.</summary>
    public IReadOnlyList<WorkshopField> ExtensionPermissions { get; } = new[]
    {
        new WorkshopField("network.http", "发起 HTTP 请求。贡献网盘 / 引擎的扩展通常需要。"),
        new WorkshopField("credentials.read", "读取凭据库里的账号令牌。"),
        new WorkshopField("process.run", "启动外部进程。"),
        new WorkshopField("filesystem.read", "读取文件。"),
        new WorkshopField("filesystem.write", "写入文件。"),
        new WorkshopField("registry.read", "读取注册表。"),
        new WorkshopField("catalog.contribute", "向软件目录贡献条目。"),
        new WorkshopField("skin.contribute", "贡献皮肤。"),
        new WorkshopField("ui.contribute", "贡献界面元素。"),
        new WorkshopField("cloud.contribute", "注册一个网盘平台。"),
        new WorkshopField("downloader.contribute", "注册一个下载引擎。"),
        new WorkshopField("settings.read", "读取扩展自己的设置。"),
        new WorkshopField("settings.write", "写入扩展自己的设置。"),
    };

    /// <summary>How a shipped extension is laid out on disk.</summary>
    public IReadOnlyList<string> ExtensionLayoutNotes { get; } = new[]
    {
        "一个扩展 = assets/extensions/ 下的一个 zip，压缩包根目录放 manifest.json。",
        "要执行代码就把程序集和 manifest.json 一起打进根目录，manifest 的 assembly 指向它。",
        "app 启动时会把这个 zip 解压到 assets/extensions/_extracted/ 下的独立目录，再扫描 manifest。",
        "扩展的 SDK 程序集由主程序提供，不要打进 zip，否则同一类型会被加载两次。",
        "禁用某个扩展后它不会被加载；被扩展注册的网盘 / 引擎会随之下线。",
    };

    /// <summary>A minimal manifest to start from.</summary>
    public string ExtensionManifestTemplate { get; } = """
    {
      "schema": 1,
      "id": "my_extension",
      "name": "我的扩展",
      "version": "1.0.0",
      "author": "",
      "description": "一句话说明这个扩展做什么。",
      "assembly": "MyExtension.dll",
      "permissions": [ "network.http" ],
      "isolation": "in-process"
    }
    """;

    [RelayCommand]
    private void ExportExtensionTemplate()
    {
        try
        {
            var directory = Path.Combine(_host.Paths.UserDataDirectory, "workshop", "my_extension");
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, "manifest.json");
            File.WriteAllText(path, ExtensionManifestTemplate);

            ExtensionTemplatePath = path;
            ExtensionsTemplateStatus = $"已写出模板：{path}（补上程序集打成 zip 放进 assets/extensions/ 即可）";
        }
        catch (Exception ex)
        {
            ExtensionsTemplateStatus = $"写出失败：{ex.GetType().Name}: {ex.Message}";
        }
    }

    [ObservableProperty]
    private string _extensionsTemplateStatus = "点「导出 manifest 模板」会在用户数据目录里生成一个可改的起点。";

    private static readonly JsonSerializerOptions Relaxed = new()
    {
        WriteIndented = true,

        // Without this every CJK character is escaped and an exported text file
        // becomes unreadable for the people it is exported for.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
}

/// <summary>
/// One entry in the workshop's skin gallery. Observable, so the gallery can draw its selection
/// ring on the entry that is chosen right now.
/// </summary>
public sealed partial class WorkshopSkinEntry : ObservableObject
{
    public WorkshopSkinEntry(string id, string name, string persona, string layout, string? baseSkin = null)
    {
        Id = id;
        Name = name;
        Persona = persona;
        Layout = layout;
        BaseSkin = baseSkin;
    }

    public string Id { get; }

    public string Name { get; }

    public string Persona { get; }

    public string Layout { get; }

    /// <summary>
    /// The skin this one was built on, when it was. The gallery shows the base's illustration for it:
    /// a skin the user just made has no card art of its own yet, and a blank card reads as a broken
    /// entry rather than as a new skin.
    /// </summary>
    public string? BaseSkin { get; }

    [ObservableProperty]
    private bool _isSelected;
}

public sealed record WorkshopExtensionEntry(string Id, string Name, string Version, string State);

/// <summary>A name/meaning row in the extension format documentation.</summary>
public sealed record WorkshopField(string Name, string Meaning);

/// <summary>
/// One picture slot of a user skin, shown as a picker. Changing it writes that skin's file, so
/// the choice is the skin's rather than something the page remembers.
/// </summary>
public sealed partial class WorkshopAssetSlot : ObservableObject
{
    private static readonly IReadOnlyDictionary<string, string> Titles = new Dictionary<string, string>
    {
        [SkinAssets.Character] = "角色立绘",
        [SkinAssets.Companion] = "搭档",
        [SkinAssets.Logo] = "标志",
        [SkinAssets.EmptyState] = "空列表插画",
        [SkinAssets.LoadingState] = "加载插画",
    };

    private readonly Action<string, string?> _apply;

    public WorkshopAssetSlot(WorkshopSlotState state, Action<string, string?> apply)
    {
        Slot = state.Slot;
        _assetId = state.AssetId;
        _isOverridden = state.IsOverridden;
        _previewFile = state.PreviewFile;
        _defaultAssetId = state.DefaultAssetId;
        Choices = state.Choices;
        _apply = apply;
    }

    /// <summary>The slot name the manifest uses, e.g. "logo".</summary>
    public string Slot { get; }

    /// <summary>What this slot draws in the app, so the picker is not a list of jargon.</summary>
    public string Title => Titles.TryGetValue(Slot, out var title) ? $"{title}（{Slot}）" : Slot;

    /// <summary>The sheet ids that can fill it, with an empty entry meaning "none".</summary>
    public IReadOnlyList<string> Choices { get; }

    private string? _assetId;

    /// <summary>
    /// The chosen sheet id. The picker writes it, and that write is what lands in the skin's
    /// file — so the callback lives in the setter rather than being bolted on by the caller.
    /// </summary>
    public string? AssetId
    {
        get => _assetId;
        set
        {
            if (SetProperty(ref _assetId, value))
            {
                _apply(Slot, value);
            }
        }
    }

    private bool _isOverridden;

    /// <summary>
    /// True when the user changed this slot, so there is a default to go back to. Written by the
    /// page rather than by this object's own setter, because it describes the file, not a choice.
    /// </summary>
    public bool IsOverridden
    {
        get => _isOverridden;
        set => SetProperty(ref _isOverridden, value);
    }

    private string? _previewFile;

    /// <summary>
    /// Absolute path of the picture to show. A path rather than a bitmap, so replacing the file is
    /// enough for the preview to change without the page having to hand out new images.
    /// </summary>
    public string? PreviewFile
    {
        get => _previewFile;
        set => SetProperty(ref _previewFile, value);
    }

    private string? _defaultAssetId;

    /// <summary>What the base skin uses here — i.e. exactly what "恢复默认" goes back to.</summary>
    public string? DefaultAssetId
    {
        get => _defaultAssetId;
        set => SetProperty(ref _defaultAssetId, value);
    }

    private bool _isSelected;

    /// <summary>True for the element picked in the preview, so it can be drawn as selected.</summary>
    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    /// <summary>What kind of thing this slot fills, in the words the detail panel uses.</summary>
    public string Kind => "图片（Image）";

    /// <summary>
    /// Refreshes the shown values from the skin's file without writing anything back. Setting the
    /// properties through <see cref="AssetId"/> would call back into the page, which is the wrong
    /// direction for a refresh.
    /// </summary>
    public void Update(WorkshopSlotState state)
    {
        SetProperty(ref _assetId, state.AssetId, nameof(AssetId));
        IsOverridden = state.IsOverridden;
        PreviewFile = state.PreviewFile;
        DefaultAssetId = state.DefaultAssetId;
    }
}

/// <summary>
/// Everything one slot shows, gathered by the page so the slot itself stays a dumb row: it holds
/// no store and no host, so the preview cannot accidentally write a skin from a click.
/// </summary>
public sealed record WorkshopSlotState(
    string Slot,
    string? AssetId,
    bool IsOverridden,
    string? PreviewFile,
    string? DefaultAssetId,
    IReadOnlyList<string> Choices);

/// <summary>
/// One thing in the real interface the user can edit: a named picture, or a named word.
///
/// <para>
/// The name comes from the control that was clicked rather than from a list the page keeps, which is
/// what makes "click the settings icon, replace it" work for anything a view draws — including
/// icons no editor page was ever told about.
/// </para>
/// </summary>
public sealed record WorkshopPreviewElement(
    string Kind,
    string Name,
    string AssetId,
    string TextKey,
    string? Current,
    string? Default,
    string? CurrentFile,
    bool IsOverridden,
    string? Info)
{
    /// <summary>True when this element is a picture, so it can be replaced with a file.</summary>
    public bool IsArt => !string.IsNullOrWhiteSpace(AssetId);

    /// <summary>
    /// The file name of the picture in use, which is what 「名称」 in the panel shows. The asset id is
    /// an internal name; the file the user actually has on disk is what they can recognise.
    /// </summary>
    public string? CurrentFileName
        => string.IsNullOrWhiteSpace(CurrentFile) ? null : System.IO.Path.GetFileName(CurrentFile);

    /// <summary>The stable id of the clicked element, when the control declared one.</summary>
    public string? ElementId { get; init; }

    /// <summary>The name the panel shows: the element's own name when it has one.</summary>
    public string? DisplayName { get; init; }

    /// <summary>What kind of element it is, in the panel's words.</summary>
    public string? ElementTypeText { get; init; }

    /// <summary>The colour key when the clicked surface is a colour; null otherwise.</summary>
    public string? ColorKey { get; init; }

    /// <summary>True when this element is a colour surface.</summary>
    public bool IsColor => !string.IsNullOrWhiteSpace(ColorKey);

    /// <summary>What 「已选择」 shows, falling back to the raw name when nothing prettier was given.</summary>
    public string Title => string.IsNullOrWhiteSpace(DisplayName) ? Name : DisplayName!;

    /// <summary>What 「类型」 shows.</summary>
    public string TypeText => string.IsNullOrWhiteSpace(ElementTypeText) ? Kind : ElementTypeText!;
}

/// <summary>
/// One colour the user's skin can change, shown next to the base's value. A plain row: nothing is
/// written until the page says so.
/// </summary>
public sealed partial class WorkshopColor : ObservableObject
{
    public WorkshopColor(string key, string label, string fallback, string? value)
    {
        Key = key;
        Label = label;
        Fallback = fallback;
        _value = value ?? fallback;
    }

    /// <summary>The manifest key, for example accent.</summary>
    public string Key { get; }

    /// <summary>What this colour paints, in the user's words.</summary>
    public string Label { get; }

    /// <summary>The base skin's value — exactly what "恢复默认" goes back to.</summary>
    public string Fallback { get; }

    [ObservableProperty]
    private string _value;

    /// <summary>True when this skin replaced the colour, so there is a default to go back to.</summary>
    public bool IsOverridden => !string.Equals(Value, Fallback, StringComparison.OrdinalIgnoreCase);

    partial void OnValueChanged(string value) => OnPropertyChanged(nameof(IsOverridden));
}

/// <summary>
/// One interface word the user's skin can change, shown as an editable row next to the shipped
/// wording. A plain row: it writes nothing itself, so nothing is saved until the page says so.
/// </summary>
public sealed partial class WorkshopUiText : ObservableObject
{
    public WorkshopUiText(string key, string fallback, string? value)
    {
        Key = key;
        Fallback = fallback;
        _value = value ?? string.Empty;
    }

    /// <summary>Stable name, for example nav.settings.</summary>
    public string Key { get; }

    /// <summary>The shipped wording — exactly what "恢复默认" goes back to.</summary>
    public string Fallback { get; }

    [ObservableProperty]
    private string _value;

    /// <summary>True when this skin has already replaced the word, so there is something to restore.</summary>
    public bool IsOverridden => !string.IsNullOrWhiteSpace(Value);

    partial void OnValueChanged(string value) => OnPropertyChanged(nameof(IsOverridden));
}
