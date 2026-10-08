using System.Text.Json;
using EriReborn.Cloud;
using Xunit;

namespace EriReborn.Core.Tests;

/// <summary>
/// The reader every provider now goes through.
///
/// <para>
/// Its whole job is to answer "no" instead of throwing when a cloud API answers with a shape nobody
/// expected — and that used to be fatal rather than local: a provider runs on a page command, so an
/// <see cref="InvalidOperationException"/> raised by <c>TryGetProperty</c> on a null element left the
/// dispatcher and closed EriReborn. Being refused while signing in to a drive was enough to do it.
/// </para>
/// </summary>
public sealed class CloudJsonTests
{
    private static JsonElement Parse(string json)
    {
        using var document = JsonDocument.Parse(json);

        // Cloned so the element outlives the document it came from.
        return document.RootElement.Clone();
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("\"text\"")]
    [InlineData("123")]
    [InlineData("{\"other\":1}")]
    public void An_unexpected_root_never_throws_and_never_claims_a_value(string json)
    {
        var root = Parse(json);

        Assert.False(CloudJson.TryObject(root, "data", out _));
        Assert.False(CloudJson.TryArray(root, "data", out _));
        Assert.False(CloudJson.TryString(root, "message", out _));
        Assert.False(CloudJson.TryInt(root, "code", out _));
        Assert.Null(CloudJson.TryInt64(root, "FileId"));
        Assert.Null(CloudJson.Code(root));
        Assert.Null(CloudJson.Message(root));
    }

    [Fact]
    public void A_null_data_member_is_not_read_as_an_object()
    {
        // The exact shape a refused or signed-out call answers with, and the exact step that used to throw.
        var root = Parse("""{"code":0,"data":null}""");

        Assert.False(CloudJson.TryObject(root, "data", out _));
        Assert.Equal(0, CloudJson.Code(root) ?? -1);
    }

    [Fact]
    public void A_member_of_the_wrong_kind_is_refused_rather_than_coerced()
    {
        var root = Parse("""{"code":"0","message":{"nested":true},"data":[]}""");

        // Not a number is not a code, and not a string is not a message: both say "this envelope carries
        // none" instead of guessing and instead of throwing.
        Assert.Null(CloudJson.Code(root));
        Assert.Null(CloudJson.Message(root));
        Assert.False(CloudJson.TryObject(root, "data", out _));
    }

    [Fact]
    public void Ids_are_read_when_they_are_numbers_and_refused_when_they_are_not()
    {
        var root = Parse("""{"FileId":24870376,"Size":"123"}""");

        Assert.Equal(24870376L, CloudJson.TryInt64(root, "FileId"));

        // A size that arrives as text is not silently turned into a number the downloader would trust.
        Assert.Null(CloudJson.TryInt64(root, "Size"));
    }

    [Fact]
    public void A_message_falls_back_to_the_short_member_the_apis_also_use()
    {
        var root = Parse("""{"msg":"需要登录"}""");

        Assert.Equal("需要登录", CloudJson.Message(root));
    }
}
