using EriReborn.Extension;

namespace EriReborn.SampleExtension;

/// <summary>
/// Minimal but real extension. It is packaged by the marketplace test to prove
/// that installing an extension actually loads and initialises it (spec 69).
/// </summary>
public sealed class HelloExtension : IExtension
{
    public string Id => "sample_hello";

    public string DisplayName => "Hello Extension";

    public string Version => "1.0.0";

    public IExtensionHost? Host { get; private set; }

    public void Initialize(IExtensionHost host)
    {
        Host = host;
        host.Log("info", "HelloExtension initialized.");
        host.SetSetting("initializedAt", DateTimeOffset.Now.ToString("O"));
    }

    public string Greet(string who) => $"Hello {who} from {DisplayName}";
}
