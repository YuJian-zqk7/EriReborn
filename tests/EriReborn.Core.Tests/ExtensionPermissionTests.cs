using EriReborn.Core.Logging;
using EriReborn.Extension;
using Xunit;

namespace EriReborn.Core.Tests;

/// <summary>
/// Declaring a permission has to change something. Before this, an extension that
/// declared nothing could still write settings, so the permission list told the
/// user one thing and the code did another (spec 34/35).
///
/// And the limit is real: this is a gate on what the host hands over, not a
/// sandbox — an in-process assembly can still call the BCL. The point is that the
/// application never becomes the one handing out the capability.
/// </summary>
public sealed class ExtensionPermissionTests
{
    /// <summary>A plain host, exactly like the one the application supplies.</summary>
    private sealed class PlainHost : IExtensionHost
    {
        private readonly Dictionary<string, string> _settings = new(StringComparer.Ordinal);

        public List<string> Logs { get; } = new();

        public void Log(string level, string message) => Logs.Add($"{level}:{message}");

        public string GetSetting(string key, string? fallback = null)
            => _settings.TryGetValue(key, out var value) ? value : fallback ?? string.Empty;

        public void SetSetting(string key, string value) => _settings[key] = value;
    }

    private static PermissionCheckedExtensionHost Gate(PlainHost inner, params string[] declared)
        => new(inner, new ExtensionPermissions(declared), AppLog.For("Test"), "demo");

    [Fact]
    public void An_extension_that_declares_nothing_gets_nothing()
    {
        var inner = new PlainHost();
        inner.SetSetting("token", "secret");

        var host = Gate(inner);

        host.SetSetting("token", "overwritten");

        // The write must not have reached the host at all …
        Assert.Equal("secret", inner.GetSetting("token"));

        // … and the stored value must not leak back out. Returning the caller's
        // fallback is the least surprising answer: to this extension the key
        // simply is not set.
        Assert.Equal(string.Empty, host.GetSetting("token"));
        Assert.Equal("fallback", host.GetSetting("token", "fallback"));
    }

    [Fact]
    public void A_declared_permission_is_actually_granted()
    {
        var inner = new PlainHost();
        var host = Gate(inner, ExtensionPermissions.SettingsRead, ExtensionPermissions.SettingsWrite);

        host.SetSetting("theme", "dark");

        Assert.Equal("dark", host.GetSetting("theme"));
        Assert.Equal("dark", inner.GetSetting("theme"));
    }

    [Fact]
    public void Write_is_not_granted_by_read_alone()
    {
        var inner = new PlainHost();
        inner.SetSetting("theme", "existing");

        var host = Gate(inner, ExtensionPermissions.SettingsRead);

        // Reading is allowed …
        Assert.Equal("existing", host.GetSetting("theme"));

        // … writing is not.
        host.SetSetting("theme", "changed");
        Assert.Equal("existing", inner.GetSetting("theme"));
    }

    [Fact]
    public void Read_is_not_granted_by_write_alone()
    {
        var inner = new PlainHost();
        var host = Gate(inner, ExtensionPermissions.SettingsWrite);

        host.SetSetting("theme", "dark");

        // The value is stored, but this extension may not read it back.
        Assert.Equal("dark", inner.GetSetting("theme"));
        Assert.Equal("fallback", host.GetSetting("theme", "fallback"));
    }

    [Fact]
    public void Logging_is_never_gated()
    {
        var inner = new PlainHost();
        var host = Gate(inner);

        host.Log("warn", "something is wrong");

        // Hiding an extension's own errors would make every failure invisible.
        Assert.Contains("warn:something is wrong", inner.Logs);
    }

    [Fact]
    public void Permission_names_are_case_insensitive()
    {
        var inner = new PlainHost();
        var host = Gate(inner, "Settings.Write", "SETTINGS.READ");

        host.SetSetting("k", "v");

        Assert.Equal("v", host.GetSetting("k"));
    }

    [Fact]
    public void Refusing_does_not_throw_into_extension_code()
    {
        var inner = new PlainHost();
        var host = Gate(inner);

        // An extension that forgot to declare something must not be able to take
        // the application down by calling the host.
        var exception = Record.Exception(() =>
        {
            host.SetSetting("a", "b");
            _ = host.GetSetting("a");
            host.Log("info", "still alive");
        });

        Assert.Null(exception);
    }

    [Fact]
    public void An_extension_without_declarations_reports_none()
    {
        Assert.Empty(ExtensionPermissions.None.Declared);
        Assert.False(ExtensionPermissions.None.Allows(ExtensionPermissions.SettingsWrite));
        Assert.Equal("(无声明)", ExtensionPermissions.None.ToString());
    }

    [Fact]
    public void The_validator_accepts_the_settings_permissions()
    {
        var validator = new ExtensionValidator();
        var manifest = new ExtensionManifest
        {
            Id = "demo",
            Name = "Demo",
            Version = "1.0.0",
            Assembly = "Demo.dll",
            Directory = ".",
            Permissions = new[] { ExtensionPermissions.SettingsRead, ExtensionPermissions.SettingsWrite },
        };

        var result = validator.Validate(manifest);

        Assert.DoesNotContain(result.Issues, issue => issue.Code == "extension.unknown_permission");
    }

    [Fact]
    public void An_invented_permission_is_still_refused()
    {
        var validator = new ExtensionValidator();
        var manifest = new ExtensionManifest
        {
            Id = "demo",
            Name = "Demo",
            Version = "1.0.0",
            Assembly = "Demo.dll",
            Directory = ".",
            Permissions = new[] { "filesystem.everything" },
        };

        var result = validator.Validate(manifest);

        Assert.Contains(result.Issues, issue => issue.Code == "extension.unknown_permission");
    }
}
