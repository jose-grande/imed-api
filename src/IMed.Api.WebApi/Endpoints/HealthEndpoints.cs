using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using System.Text.Json;

namespace IMed.Api.WebApi.Endpoints;

public static class HealthEndpoints
{
    public static IEndpointRouteBuilder MapHealthEndpoints(this IEndpointRouteBuilder app)
    {
        // /healthz  — liveness: responde si el proceso está vivo.
        app.MapGet("/healthz", () => Results.Ok(new { status = "live" }))
            .WithTags("Health")
            .AllowAnonymous();

        // /readyz   — readiness: incluye checks de dependencias (Postgres).
        app.MapHealthChecks("/readyz", new HealthCheckOptions
        {
            ResponseWriter = WriteReadinessResponse
        })
        .WithTags("Health")
        .AllowAnonymous();

        return app;
    }

    private static Task WriteReadinessResponse(HttpContext context, HealthReport report)
    {
        context.Response.ContentType = "application/json";

        var payload = JsonSerializer.Serialize(new
        {
            status = report.Status.ToString(),
            totalDuration = report.TotalDuration,
            checks = report.Entries.Select(kv => new
            {
                name = kv.Key,
                status = kv.Value.Status.ToString(),
                duration = kv.Value.Duration,
                description = kv.Value.Description,
                error = kv.Value.Exception?.Message
            })
        });

        return context.Response.WriteAsync(payload);
    }
}
