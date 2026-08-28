using StackExchange.Redis;

namespace Payments.Core.Caching;

/// <summary>
/// The Redis connection, or the fact that there isn't one.
/// </summary>
/// <remarks>
/// A type rather than a nullable service registration. The container's
/// <c>AddSingleton&lt;TService&gt;</c> constrains its argument to a non-nullable
/// reference type, so registering <c>IConnectionMultiplexer?</c> is telling the
/// container something it is not built to hear.
///
/// It also states the design out loud. Redis is optional here, and a holder whose
/// whole purpose is to say "possibly absent" is clearer than a nullable
/// dependency that every consumer has to remember might be null.
/// </remarks>
public sealed class RedisConnection(IConnectionMultiplexer? multiplexer) : IDisposable
{
    public IConnectionMultiplexer? Multiplexer { get; } = multiplexer;

    public bool IsAvailable => Multiplexer is not null;

    public IDatabase? Database => Multiplexer?.GetDatabase();

    public void Dispose() => Multiplexer?.Dispose();
}
