using EriReborn.Core.Validation;
using Xunit;

namespace EriReborn.Core.Tests;

/// <summary>Spec 15/16: official names are ASCII and invalid data is reported, never slugified.</summary>
public sealed class DirectoryNameValidatorTests
{
    [Theory]
    [InlineData("Games")]
    [InlineData("Visual_Cpp")]
    [InlineData("Java8")]
    [InlineData("dotnet_sdk_8")]
    [InlineData("7Zip")]
    public void Accepts_ascii_names(string name)
        => Assert.True(DirectoryNameValidator.ValidateName(name).IsValid, name);

    [Theory]
    [InlineData("游戏")]
    [InlineData("Good Games")]
    [InlineData("Good-Games")]
    [InlineData("Good.Games")]
    [InlineData("Good/Games")]
    [InlineData("Good\\Games")]
    public void Rejects_non_ascii_and_special_characters(string name)
        => Assert.False(DirectoryNameValidator.ValidateName(name).IsValid, name);

    [Theory]
    [InlineData("CON")]
    [InlineData("nul")]
    [InlineData("COM1")]
    [InlineData("LPT9")]
    public void Rejects_windows_reserved_names(string name)
        => Assert.False(DirectoryNameValidator.ValidateName(name).IsValid, name);

    [Theory]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("Name ")]
    [InlineData("Name.")]
    [InlineData("")]
    public void Rejects_relative_tokens_and_trailing_characters(string name)
        => Assert.False(DirectoryNameValidator.ValidateName(name).IsValid, name);

    [Fact]
    public void Reports_concrete_reasons_instead_of_slugifying()
    {
        var result = DirectoryNameValidator.ValidateName("我的 游戏");
        Assert.False(result.IsValid);
        Assert.Contains(result.Issues, i => i.Code == "dir.not_ascii");
        Assert.Contains(result.Issues, i => i.Code == "dir.illegal_char");
    }

    [Fact]
    public void Detects_case_insensitive_sibling_collisions()
    {
        var result = DirectoryNameValidator.ValidateSiblingSet(new[] { "Games", "games" });
        Assert.False(result.IsValid);
        Assert.Contains(result.Issues, i => i.Code == "dir.case_conflict");
    }

    [Fact]
    public void Detects_exact_duplicate_siblings()
    {
        var result = DirectoryNameValidator.ValidateSiblingSet(new[] { "Games", "Games" });
        Assert.False(result.IsValid);
        Assert.Contains(result.Issues, i => i.Code == "dir.conflict");
    }
}
