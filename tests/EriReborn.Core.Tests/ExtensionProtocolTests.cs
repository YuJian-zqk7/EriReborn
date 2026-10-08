using EriReborn.Extension;
using Xunit;

namespace EriReborn.Core.Tests;

/// <summary>
/// A line protocol works until the first message containing a newline or the first
/// unrecognised kind. Both are cheap to get wrong and expensive to find in a child
/// process that only says nothing.
/// </summary>
public sealed class ExtensionProtocolTests
{
    [Fact]
    public void Every_message_kind_survives_a_round_trip()
    {
        var messages = new[]
        {
            ExtensionMessage.Hello("demo", "1.0.0", new[] { "settings.read" }, new Dictionary<string, string> { ["k"] = "v" }),
            ExtensionMessage.Ready("demo", "1.0.0"),
            ExtensionMessage.Request(new ExtensionRequest("greet", "Eri", "req-1")),
            ExtensionMessage.Response(new ExtensionRequest("greet", Id: "req-1"), ExtensionResponse.Done("ok")),
            ExtensionMessage.Logged("warn", "注意"),
            ExtensionMessage.Failure("失败"),
        };

        foreach (var message in messages)
        {
            var line = ExtensionProtocol.Write(message);

            Assert.DoesNotContain('\n', line);
            Assert.True(ExtensionProtocol.TryRead(line, out var parsed, out var error), error);
            Assert.Equal(message.Kind, parsed!.Kind);
        }
    }

    [Fact]
    public void The_handshake_fields_survive_the_wire()
    {
        var hello = ExtensionMessage.Hello(
            "demo",
            "2.1.0",
            new[] { "settings.read", "settings.write" },
            new Dictionary<string, string> { ["note"] = "跨进程" });

        Assert.True(ExtensionProtocol.TryRead(ExtensionProtocol.Write(hello), out var parsed, out _));

        Assert.Equal("demo", parsed!.Id);
        Assert.Equal("2.1.0", parsed.Version);
        Assert.Equal(2, parsed.Permissions!.Count);
        Assert.Equal("跨进程", parsed.Settings!["note"]);
    }

    [Fact]
    public void A_response_carries_its_request_id_back()
    {
        var request = new ExtensionRequest("greet", "x", "req-7");

        Assert.True(ExtensionProtocol.TryRead(
            ExtensionProtocol.Write(ExtensionMessage.Response(request, ExtensionResponse.Failed("nope"))),
            out var parsed,
            out _));

        // Without the id, two concurrent turns cannot be told apart.
        Assert.Equal("req-7", parsed!.Id);
        Assert.False(parsed.Ok);
        Assert.Equal("nope", parsed.Error);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json at all")]
    [InlineData("{\"kind\":\"\"}")]
    [InlineData("{\"id\":\"x\"}")]
    public void A_bad_line_is_refused_with_a_reason(string? line)
    {
        Assert.False(ExtensionProtocol.TryRead(line, out var message, out var error));
        Assert.Null(message);
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Fact]
    public void An_unknown_kind_is_a_version_mismatch_not_a_no_op()
    {
        // Quietly ignoring it would hide a mismatched host until something timed out.
        Assert.False(ExtensionProtocol.TryRead("{\"kind\":\"teleport\"}", out _, out var error));
        Assert.Contains("teleport", error);
    }

    [Fact]
    public void A_message_that_would_break_the_framing_is_refused()
    {
        // The serializer escapes newlines, so a value containing one cannot split a
        // message in two — and this proves the invariant rather than assuming it.
        var awkward = ExtensionMessage.Logged("info", "第一行\n第二行");

        var line = ExtensionProtocol.Write(awkward);
        Assert.DoesNotContain('\n', line);

        Assert.True(ExtensionProtocol.TryRead(line, out var parsed, out var error), error);
        Assert.Equal("第一行\n第二行", parsed!.Message);
    }

    [Fact]
    public void The_runtime_commands_are_the_same_names_on_both_sides()
    {
        // Both the parent and the child branch on these; a private copy on one side
        // would make shutdown a request the extension was never meant to implement.
        Assert.Equal("service.shutdown", ServiceCommands.Shutdown);
        Assert.Equal("service.settings", ServiceCommands.DumpSettings);
    }
}
