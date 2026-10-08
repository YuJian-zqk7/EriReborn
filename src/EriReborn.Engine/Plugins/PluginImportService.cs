using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EriReborn.Core.Catalog;
using EriReborn.Core.Domain;
using EriReborn.Core.Logging;
using EriReborn.Core.Validation;

namespace EriReborn.Engine.Plugins;

/// <summary>The stages of an import, in the order they run (spec 47).</summary>
public enum PluginImportStage
{
    Read,
    SchemaValidate,
    SignatureVerify,
    ResourceValidate,
    ProviderValidate,
    Register,
}

public sealed record PluginImportResult(
    PluginImportStage Stage,
    bool Succeeded,
    string Message,
    IReadOnlyList<SoftwareDefinition> Accepted,
    IReadOnlyList<ValidationIssue> Issues)
{
    /// <summary>The id the plugin file declares, once it has been parsed.</summary>
    public string? PluginId { get; init; }

    /// <summary>The version the plugin file declares.</summary>
    public string? PluginVersion { get; init; }

    /// <summary>Resources the plugin offered but that were not accepted.</summary>
    public IReadOnlyList<ValidationIssue> Rejections { get; init; } = Array.Empty<ValidationIssue>();
}

/// <summary>
/// Imports a resource plugin.
///
/// Everything here is data. There is no code path that could execute anything a
/// plugin contains, and that is a property of the design rather than a check
/// that could be forgotten (spec 42).
///
/// A plugin may only <em>append</em> resources. An id that already exists is
/// rejected: third-party data never overrides established data (spec 84).
/// </summary>
public sealed class PluginImportService(IReadOnlyCollection<string> knownProviderIds, IAppLogger log)
{
    private readonly HashSet<string> _knownProviders = new(knownProviderIds, StringComparer.OrdinalIgnoreCase);
    private readonly IAppLogger _log = log;

    public PluginImportResult Import(string json, string? signatureJson, IReadOnlyCollection<string> existingIds)
    {
        // ---------------------------------------------------------------- parse
        var (plugin, parseIssues) = PluginReader.Parse(json);
        if (plugin is null)
        {
            return Fail(PluginImportStage.Read, "插件无法解析。", parseIssues);
        }

        // ------------------------------------------------------- schema stage
        var schemaIssues = new List<ValidationIssue>(parseIssues);
        if (string.IsNullOrWhiteSpace(plugin.Id))
        {
            schemaIssues.Add(new ValidationIssue("plugin.no_id", "插件缺少 id。"));
        }

        if (string.IsNullOrWhiteSpace(plugin.Name))
        {
            schemaIssues.Add(new ValidationIssue("plugin.no_name", "插件缺少 name。"));
        }

        if (string.IsNullOrWhiteSpace(plugin.Version))
        {
            schemaIssues.Add(new ValidationIssue("plugin.no_version", "插件缺少 version。"));
        }

        if (plugin.Resources.Count == 0)
        {
            schemaIssues.Add(new ValidationIssue("plugin.empty", "插件没有声明任何资源。"));
        }

        var duplicates = plugin.Resources
            .GroupBy(r => r.Id, StringComparer.Ordinal)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key);

        foreach (var duplicate in duplicates)
        {
            schemaIssues.Add(new ValidationIssue("plugin.duplicate_resource", "插件内资源 id 重复。", duplicate));
        }

        if (schemaIssues.Count > 0)
        {
            return Fail(PluginImportStage.SchemaValidate, "插件结构校验未通过。", schemaIssues);
        }

        // ---------------------------------------------------- signature stage
        var trust = PluginTrust.Unknown;
        if (!string.IsNullOrWhiteSpace(signatureJson))
        {
            var (valid, message) = VerifySignature(json, signatureJson);
            if (!valid)
            {
                _log.Error("plugin.signature", message, null);
                return Fail(
                    PluginImportStage.SignatureVerify,
                    $"插件签名校验失败：{message}",
                    new[] { new ValidationIssue("plugin.bad_signature", message, plugin.Id) });
            }

            trust = PluginTrust.Official;
        }

        var signed = plugin with { Trust = trust };
        _log.Info("plugin.trust", $"插件 '{plugin.Id}' 信任级别 {trust}。");

        // ------------------------------------------------ resource + provider
        var resourceIssues = new List<ValidationIssue>();
        foreach (var resource in signed.Resources)
        {
            if (resource.Sources.Count == 0)
            {
                resourceIssues.Add(new ValidationIssue(
                    "plugin.resource_no_source",
                    "资源没有声明任何下载来源。",
                    resource.Id));
            }
        }

        if (resourceIssues.Count > 0)
        {
            return Fail(PluginImportStage.ResourceValidate, "插件资源校验未通过。", resourceIssues);
        }

        var providerIssues = new List<ValidationIssue>();
        foreach (var resource in signed.Resources)
        {
            foreach (var source in resource.Sources)
            {
                if (source.Kind != SourceKind.CloudShare)
                {
                    continue;
                }

                if (string.IsNullOrWhiteSpace(source.ProviderId) || !_knownProviders.Contains(source.ProviderId))
                {
                    providerIssues.Add(new ValidationIssue(
                        "plugin.unknown_provider",
                        $"来源引用了未知的网盘平台 '{source.ProviderId ?? "(空)"}'。",
                        resource.Id));
                }
            }
        }

        if (providerIssues.Count > 0)
        {
            return Fail(PluginImportStage.ProviderValidate, "插件来源的平台未知。", providerIssues);
        }

        // ------------------------------------------------------ register stage
        var known = new HashSet<string>(existingIds, StringComparer.Ordinal);
        var accepted = new List<SoftwareDefinition>();
        var conflicts = new List<ValidationIssue>();

        // 按树前序注册：分组与文件夹分支作为 PluginGroupPath 带到软件定义上，
        // 软件页据此还原树；去重仍只按稳定资源 id，与结构无关。
        foreach (var (resourceNode, groupPath) in PluginTree.EnumerateResourcesWithGroupPath(signed.Nodes))
        {
            var resource = resourceNode.Resource;

            if (!known.Add(resource.Id))
            {
                // The rule the whole scheme exists for: append, never override.
                conflicts.Add(new ValidationIssue(
                    "plugin.id_conflict",
                    "该 id 已存在，插件只能新增资源，不能覆盖既有数据。",
                    resource.Id));
                continue;
            }

            accepted.Add(ToDefinition(resource, signed, groupPath));
        }

        var summary = accepted.Count == 0
            ? $"插件 '{signed.Id}' 没有可新增的资源（{conflicts.Count} 条与既有 id 冲突）。"
            : $"插件 '{signed.Id}' 新增 {accepted.Count} 条资源"
              + (conflicts.Count > 0 ? $"，{conflicts.Count} 条因 id 冲突被拒绝。" : "。");

        _log.Info("plugin.import", summary);

        return new PluginImportResult(PluginImportStage.Register, true, summary, accepted, Array.Empty<ValidationIssue>())
        {
            Rejections = conflicts,
            PluginId = signed.Id,
            PluginVersion = signed.Version,
        };
    }

    /// <summary>
    /// A plugin resource is the same shape the catalog uses, so an imported
    /// plugin needs no second data model and flows straight into the software
    /// engine.
    /// </summary>
    private static SoftwareDefinition ToDefinition(
        PluginResource resource,
        ResourcePlugin plugin,
        IReadOnlyList<string> groupPath) => new()
    {
        Id = resource.Id,
        Name = resource.Name,
        // plugin JSON 里 categoryId 经常是 AI/作者自造的占位值（"Uncategorized"、空串等），
        // SoftwareEngine 的 ValidateOfficialCategoryId 会拒绝任何不在官方注册表里的 id，
        // 触发 category.unknown 让整条都装不了。对 plugin 来源的条目做兜底：只要不是
        // 已知官方分类名，统一改成 "Utility"（官方顶级分类"实用工具"，对杂项云盘文件
        // 也合理）——显示名走 Name，CategoryId 只过校验，不影响用户视觉。
        CategoryId = string.IsNullOrWhiteSpace(resource.CategoryId)
                     || resource.CategoryId.Equals("Uncategorized", StringComparison.OrdinalIgnoreCase)
            ? "Utility"
            : resource.CategoryId,
        SubcategoryId = resource.SubcategoryId,
        DirectoryName = resource.DirectoryName,
        Description = resource.Description,
        Homepage = resource.Homepage,
        Version = resource.Version,
        Tier = resource.Tier,
        Mode = resource.Mode,

        // Official catalog trust and plugin trust are different things: a signed
        // plugin earns "verified", never "official catalog data" (spec 83).
        Trust = plugin.Trust == PluginTrust.Official ? SoftwareTrust.Verified : SoftwareTrust.Community,
        Provenance = CatalogProvenance.ThirdParty,

        // A plugin may not ship a detector: detection stays the catalog's job.
        Detector = DetectorSpec.None,
        Sources = resource.Sources,
        Architecture = resource.Architecture,

        // CatalogId 同时承担「来源插件 id」（OriginPluginId）的职责，不另设第二份字段。
        CatalogId = plugin.Id,
        IsPluginProvided = true,
        PluginGroupPath = groupPath,
    };

    /// <summary>
    /// Verifies the detached signature over the plugin file's own bytes, against
    /// the pinned trust anchor. A key that is not pinned gains nothing, which is
    /// what stops a plugin from signing itself official (spec 46).
    /// </summary>
    private (bool Valid, string Message) VerifySignature(string json, string signatureJson)
    {
        PluginSignature? signature;
        try
        {
            signature = JsonSerializer.Deserialize<PluginSignature>(
                signatureJson,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (JsonException ex)
        {
            return (false, $"签名文件无法解析：{ex.Message}");
        }

        if (signature is null || string.IsNullOrWhiteSpace(signature.Signature))
        {
            return (false, "签名文件为空或缺少签名字段。");
        }

        var key = CatalogTrustAnchor.Find(signature.KeyId);
        if (key is null)
        {
            return (false, $"签名密钥 '{signature.KeyId}' 不在内置信任根中。");
        }

        byte[] signatureBytes;
        try
        {
            signatureBytes = Convert.FromBase64String(signature.Signature);
        }
        catch (FormatException)
        {
            return (false, "签名不是合法的 base64。");
        }

        var payload = Encoding.UTF8.GetBytes(json);

        try
        {
            using var ecdsa = ECDsa.Create();
            ecdsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(key.PublicKey), out _);

            return ecdsa.VerifyData(payload, signatureBytes, HashAlgorithmName.SHA256)
                ? (true, $"签名有效（{key.KeyId}）。")
                : (false, "签名与插件内容不匹配，可能已被篡改。");
        }
        catch (CryptographicException ex)
        {
            return (false, $"签名校验异常：{ex.Message}");
        }
    }

    private static PluginImportResult Fail(PluginImportStage stage, string message, IReadOnlyList<ValidationIssue> issues)
        => new(stage, false, message, Array.Empty<SoftwareDefinition>(), issues);
}
