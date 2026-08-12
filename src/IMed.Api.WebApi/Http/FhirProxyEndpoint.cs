using System.Diagnostics;
using System.Security.Claims;
using System.Text.Json;
using IMed.Api.Application.Abstractions;
using IMed.Api.Application.FhirGateway;
using IMed.Api.Domain.Auditing;
using IMed.Api.Infrastructure.Hapi;
using IMed.Api.Infrastructure.Persistence;
using Microsoft.Extensions.Options;
using Yarp.ReverseProxy.Forwarder;

namespace IMed.Api.WebApi.Http;

/// <summary>
/// Proxy autorizador de <c>/fhir/**</c> hacia HAPI (ADR-007, SEC-3).
///
/// <para><c>https://&lt;dominio&gt;/fhir</c> <b>sigue siendo un endpoint FHIR conforme</b>: mismas
/// búsquedas, mismos Bundles. Lo que deja de ser público es el proceso HAPI, no la API. La
/// interoperabilidad no se pierde, se vuelve gobernada.</para>
///
/// <para><b>El token del usuario se reenvía tal cual.</b> HAPI valida la firma y resuelve la
/// partición desde el claim <c>organization_id</c>, igual que cuando el navegador le hablaba
/// directo — así el aislamiento multi-tenant no depende de que el proxy lo replique bien. SEC-5
/// cambiará esto por el service account, cuando HAPI deje de aceptar tokens de usuario.</para>
/// </summary>
public static class FhirProxyEndpoint
{
    /// <summary>Categoría del registro de acceso a datos clínicos en <c>app_audit_log</c>.</summary>
    public const string PhiAuditCategory = "phi_access";

    /// <summary>Cuerpo máximo que se lee para evaluar un Bundle. Más que eso no es un uso legítimo.</summary>
    private const int MaxBodyBytes = 2 * 1024 * 1024;

    public static IEndpointRouteBuilder MapFhirProxy(this IEndpointRouteBuilder app)
    {
        app.Map("/fhir/{**path}", HandleAsync)
            .WithTags("FhirProxy")
            .RequireAuthorization()
            .WithSummary("Proxy autorizador hacia HAPI FHIR: default-deny, Bundles evaluados entrada por entrada.");

        return app;
    }

    private static async Task HandleAsync(
        HttpContext context,
        IOptions<FhirGatewayOptions> gatewayOptions,
        IOptions<HapiFhirOptions> hapiOptions,
        IPermissionResolver resolver,
        FhirAccessEvaluator evaluator,
        IHttpForwarder forwarder,
        HttpMessageInvoker invoker,
        AppDbContext db,
        TimeProvider timeProvider,
        ILoggerFactory loggerFactory)
    {
        var logger = loggerFactory.CreateLogger("FhirProxy");
        var started = Stopwatch.GetTimestamp();

        var orgId = ParseOrganizationId(context.User);
        if (orgId is null)
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsJsonAsync(Problem("El token no trae organization_id."));
            return;
        }

        var path = context.Request.Path.Value?["/fhir".Length..] ?? string.Empty;
        var url = path + context.Request.QueryString.Value;
        var descriptor = FhirRequestParser.Parse(context.Request.Method, url);
        if (descriptor is null)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsJsonAsync(Problem("No se pudo interpretar la ruta FHIR."));
            return;
        }

        // El cuerpo solo se lee cuando hace falta abrirlo — un Bundle. Para el resto se reenvía
        // en streaming sin materializarlo: bufferear cada escritura por si acaso sería pagar
        // memoria en todos los requests para un caso que es uno solo.
        string? body = null;
        if (descriptor.IsBundle)
        {
            context.Request.EnableBuffering();
            using var reader = new StreamReader(context.Request.Body, leaveOpen: true);
            var buffer = new char[MaxBodyBytes];
            var read = await reader.ReadBlockAsync(buffer);
            body = new string(buffer, 0, read);
            context.Request.Body.Position = 0;
        }

        var roles = context.User.FindAll("roles").Select(c => c.Value).ToList();
        var permissions = await resolver.ResolveAsync(orgId.Value, roles);
        var decision = await evaluator.EvaluateAsync(orgId.Value, permissions, descriptor, body);

        var mode = gatewayOptions.Value.Mode;
        var blocked = !decision.Allowed && mode == FhirGatewayMode.Enforce;

        await AuditAsync(db, context, orgId.Value, descriptor, decision, mode, timeProvider);

        if (!decision.Allowed)
        {
            // En sombra se registra y se deja pasar. Es el punto del modo: descubrir los olvidos
            // de la lista blanca en un log y no rompiendo una pantalla.
            logger.LogWarning(
                "FHIR {Mode}: {Verb} {Resource} — {Reason}",
                mode == FhirGatewayMode.Enforce ? "BLOQUEADO" : "SOMBRA",
                descriptor.Verb, descriptor.ResourceType, decision.Reason);

            if (blocked)
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                await context.Response.WriteAsJsonAsync(Problem(decision.Reason ?? "Acceso no permitido."));
                return;
            }
        }

        // Server-Timing desde el día uno: sin medir, la discusión sobre el costo del hop es una
        // opinión. Se engancha ANTES de reenviar porque YARP escribe las cabeceras de la respuesta
        // en cuanto HAPI contesta, y después ya no se pueden tocar. Lo que se mide es el tiempo
        // hasta el primer byte, que es el número que importa.
        context.Response.OnStarting(() =>
        {
            if (!context.Response.Headers.ContainsKey("Server-Timing"))
            {
                var elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                context.Response.Headers.Append("Server-Timing", $"gw;dur={elapsed:F1}");
            }
            return Task.CompletedTask;
        });

        var transform = new ForwarderRequestConfig { ActivityTimeout = TimeSpan.FromSeconds(100) };
        var error = await forwarder.SendAsync(
            context, HapiRoot(hapiOptions.Value.BaseUrl), invoker, transform);

        if (error != ForwarderError.None)
        {
            logger.LogError("Fallo al reenviar a HAPI: {Error}", error);
        }
    }

    /// <summary>
    /// Registro de acceso a datos clínicos.
    ///
    /// <para>Es lo que permite contestar "¿quién leyó este expediente?" — la pregunta que hoy no
    /// tiene respuesta. Se registra <b>todo</b> acceso, permitido o no: un intento negado dice
    /// tanto como uno concedido, y a veces más.</para>
    ///
    /// <para>Best-effort: un fallo escribiendo la bitácora no puede tumbar la consulta clínica.
    /// Se pierde una línea de auditoría, no la atención del paciente.</para>
    /// </summary>
    private static async Task AuditAsync(
        AppDbContext db,
        HttpContext context,
        Guid organizationId,
        FhirRequestDescriptor descriptor,
        FhirAccessDecision decision,
        FhirGatewayMode mode,
        TimeProvider timeProvider)
    {
        try
        {
            var payload = JsonSerializer.Serialize(new
            {
                resource = descriptor.ResourceType,
                verb = descriptor.Verb.ToString(),
                id = descriptor.ResourceId,
                allowed = decision.Allowed,
                reason = decision.Reason,
                mode = mode.ToString(),
                path = context.Request.Path.Value,
                query = context.Request.QueryString.Value,
            });

            db.AppAuditLog.Add(AppAuditLogEntry.Create(
                category: PhiAuditCategory,
                action: $"{descriptor.Verb}:{descriptor.ResourceType}",
                severity: decision.Allowed ? AuditSeverity.Info : AuditSeverity.Warning,
                actorUserId: ParseSub(context.User),
                organizationId: organizationId,
                target: descriptor.ResourceId is null
                    ? descriptor.ResourceType
                    : $"{descriptor.ResourceType}/{descriptor.ResourceId}",
                correlationId: context.TraceIdentifier,
                remoteIp: context.Connection.RemoteIpAddress?.ToString(),
                payloadJson: payload,
                timeProvider: timeProvider));

            await db.SaveChangesAsync(context.RequestAborted);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            context.RequestServices.GetRequiredService<ILoggerFactory>()
                .CreateLogger("FhirProxy")
                .LogError(ex, "No se pudo registrar el acceso a PHI (best-effort).");
        }
    }

    /// <summary>
    /// Raíz del servidor HAPI, sin el sufijo <c>/fhir</c>.
    ///
    /// <para>El reenvío conserva el path entrante —<c>/fhir/Patient/1</c>— así que el destino
    /// tiene que ser el host pelado: si se pasara la base configurada tal cual, HAPI recibiría
    /// <c>/fhir/fhir/Patient/1</c>.</para>
    /// </summary>
    private static string HapiRoot(string configuredBaseUrl)
    {
        var trimmed = configuredBaseUrl.TrimEnd('/');
        return trimmed.EndsWith("/fhir", StringComparison.OrdinalIgnoreCase)
            ? trimmed[..^"/fhir".Length]
            : trimmed;
    }

    private static object Problem(string detail) => new
    {
        type = "https://tools.ietf.org/html/rfc9110#section-15.5.4",
        title = "Acceso no permitido",
        status = StatusCodes.Status403Forbidden,
        detail,
    };

    private static Guid? ParseOrganizationId(ClaimsPrincipal principal) =>
        Guid.TryParse(principal.FindFirstValue("organization_id"), out var id) ? id : null;

    private static Guid? ParseSub(ClaimsPrincipal principal)
    {
        var raw = principal.FindFirstValue(ClaimTypes.NameIdentifier) ?? principal.FindFirstValue("sub");
        return Guid.TryParse(raw, out var sub) ? sub : null;
    }
}
