using System.Security.Claims;
using System.Text.Json;
using IMed.Api.Application.Notifications;

namespace IMed.Api.WebApi.Endpoints;

/// <summary>
/// SSE endpoint para que el cliente reciba eventos en vivo del backend
/// (cambios de tier, asignación de organization tras bootstrap diferido, etc).
///
/// <para>Protocolo: <see href="https://html.spec.whatwg.org/multipage/server-sent-events.html"/></para>
/// <para>Heartbeat cada 30s para evitar timeouts de proxies. El cliente debe
/// reconectarse automáticamente (default del <c>EventSource</c> nativo).</para>
/// </summary>
public static class MeStreamEndpoint
{
    private static readonly JsonSerializerOptions PayloadJsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(30);

    public static IEndpointRouteBuilder MapMeStreamEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/me/stream", StreamAsync)
            .WithTags("Me")
            .WithName("StreamMyNotifications")
            .WithSummary("SSE: notificaciones en vivo para el usuario autenticado.")
            .RequireAuthorization()
            // Excluir de OpenAPI: SSE no se modela bien en OpenAPI 3.x.
            .ExcludeFromDescription();

        return app;
    }

    private static async Task StreamAsync(
        HttpContext httpContext,
        ClaimsPrincipal principal,
        IUserNotificationBus bus,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var sub = principal.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? principal.FindFirstValue("sub");

        if (string.IsNullOrWhiteSpace(sub) || !Guid.TryParse(sub, out var keycloakUserId))
        {
            httpContext.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        httpContext.Response.Headers.ContentType = "text/event-stream";
        httpContext.Response.Headers.CacheControl = "no-cache";
        httpContext.Response.Headers.Connection = "keep-alive";
        // Cuando hay nginx/CloudFront delante, esto evita que bufferéen el stream.
        httpContext.Response.Headers["X-Accel-Buffering"] = "no";

        // Mensaje inicial confirma la conexión.
        await httpContext.Response.WriteAsync(": connected\n\n", cancellationToken);
        await httpContext.Response.Body.FlushAsync(cancellationToken);

        var heartbeatTask = HeartbeatLoopAsync(httpContext, timeProvider, cancellationToken);
        try
        {
            await foreach (var notification in bus.SubscribeAsync(keycloakUserId, cancellationToken))
            {
                await WriteEventAsync(httpContext, notification, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Cliente cerró la conexión — flow normal.
        }
        finally
        {
            // Cancelar heartbeat sin awaitear errores ya logueados.
            try { await heartbeatTask; } catch { /* ignored */ }
        }
    }

    private static async Task WriteEventAsync(HttpContext ctx, UserNotification n, CancellationToken ct)
    {
        // Formato SSE:
        //   event: <Event>
        //   data: <json>
        //   <blank line>
        var dataJson = JsonSerializer.Serialize(new { reason = n.Reason, data = n.Data }, PayloadJsonOptions);
        var line = $"event: {n.Event}\ndata: {dataJson}\n\n";
        await ctx.Response.WriteAsync(line, ct);
        await ctx.Response.Body.FlushAsync(ct);
    }

    private static async Task HeartbeatLoopAsync(HttpContext ctx, TimeProvider timeProvider, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(HeartbeatInterval, timeProvider, ct);
                // Comentario SSE (línea que empieza con `:`) — el cliente lo ignora,
                // sirve para mantener viva la conexión a través de proxies.
                await ctx.Response.WriteAsync(": ping\n\n", ct);
                await ctx.Response.Body.FlushAsync(ct);
            }
        }
        catch (OperationCanceledException) { /* normal */ }
    }
}
