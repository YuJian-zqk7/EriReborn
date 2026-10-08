namespace EriReborn.App.Shared.Services;

/// <summary>
/// Where the AI configuration keeps the one thing that must not be in a file.
///
/// <para>
/// One constant instead of a literal in each caller: the key has to be the same in the page that
/// saves it and in every page that runs a capability with it, and two copies of a string like this
/// drift the moment one of them is renamed (spec 59/213.22).
/// </para>
/// </summary>
public static class AiSettingsKeys
{
    /// <summary>Credential-store key for the API key. Written to the secure store, never to a document.</summary>
    public const string ApiKey = "ai/provider/api_key";
}
