using Payments.Core.Observability;

namespace Payments.Api.Observability;

/// <summary>
/// Gives every request a correlation id, logs under it, and returns it.
/// </summary>
/// <remarks>
/// Registered before anything that logs, so there is no window at the start of a
/// request where lines come out unattributed. The id goes back on the response
/// because a caller reporting a problem can then quote it, which turns "a payment
/// failed this morning" into one query.
/// </remarks>
public sealed class CorrelationIdMiddleware(RequestDelegate next, ILogger<CorrelationIdMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var correlationId = CorrelationId.Accept(context.Request.Headers[CorrelationId.Header]);

        context.Items[CorrelationId.LogProperty] = correlationId;
        context.Response.Headers[CorrelationId.Header] = correlationId;

        using (logger.BeginScope(new Dictionary<string, object> { [CorrelationId.LogProperty] = correlationId }))
        {
            await next(context);
        }
    }
}

public static class CorrelationIdExtensions
{
    public static IApplicationBuilder UseCorrelationId(this IApplicationBuilder app)
        => app.UseMiddleware<CorrelationIdMiddleware>();

    /// <summary>The id for the request being handled, for anything that has to store it.</summary>
    public static string CorrelationId(this HttpContext context)
        => context.Items[Core.Observability.CorrelationId.LogProperty] as string ?? "";
}
