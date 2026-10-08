namespace EriReborn.Platform.Abstractions;

/// <summary>HTTP access abstraction so providers can be tested and proxied.</summary>
public interface INetworkService
{
    HttpClient Client { get; }

    /// <summary>
    /// A client with its own cookie jar, for the handshakes that only work with one.
    ///
    /// <para>
    /// The shared client deliberately has cookies turned off: providers must not
    /// see each other's sessions, and a silently shared jar is how one platform's
    /// login leaks into another's request. A platform that needs cookies gets its
    /// own jar here instead of turning them on for everyone.
    /// </para>
    ///
    /// <para>
    /// Defaults to the shared client so no existing implementation has to change;
    /// an implementation that cannot provide a jar leaves the default, and callers
    /// that need one must handle their handshake failing.
    /// </para>
    /// </summary>
    HttpClient CreateSession() => Client;
}
