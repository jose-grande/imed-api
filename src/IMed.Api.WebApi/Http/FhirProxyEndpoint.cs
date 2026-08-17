using System.Buffers;
using System.Diagnostics;
using System.Security.Claims;
using System.Text;
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

    /// <summary>Cuerpo máximo que se lee para evaluar. Más que eso no es un uso legítimo.</summary>
    private const int MaxBodyChars = 2 * 1024 * 1024;

    /// <summary>Trozo con que se recorre el cuerpo. Sale del pool: este camino ya no es raro.</summary>
    private const int ReadChunkChars = 8 * 1024;

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

        // El cuerpo solo se lee cuando alguna regla lo va a abrir —un Bundle, o una Observation
        // cuya categoría decide si pasa—. Para el resto se reenvía en streaming sin materializarlo:
        // bufferear cada escritura por si acaso sería pagar memoria en todos los requests.
        // Quién lo necesita lo decide el evaluador, para que agregar una regla que mire el cuerpo
        // no dependa de acordarse de tocar también este archivo.
        string? body = null;
        FhirAccessDecision? tooLarge = null;
        if (FhirAccessEvaluator.RequiresBody(descriptor))
        {
            body = await ReadBodyAsync(context.Request);
            if (body is null)
            {
                tooLarge = FhirAccessDecision.Deny(
                    $"El cuerpo supera los {MaxBodyChars} caracteres que se pueden evaluar.",
                    descriptor.ResourceType);
            }
        }

        var roles = context.User.FindAll("roles").Select(c => c.Value).ToList();
        var permissions = await resolver.ResolveAsync(orgId.Value, roles);

        // El Practitioner del llamante sale del token y no de la base. El claim lo poblamos
        // nosotros por el outbox contra la admin API de Keycloak, y viaja firmado: no es un dato
        // que el cliente pueda inventar. Vacío —perfil sin completar— hace que los intents que
        // exigen "tu propio profesional" no encajen, y la consulta cae al general, que pide
        // lectura amplia. Es el lado correcto para equivocarse.
        var caller = new FhirCallerContext(
            orgId.Value,
            ParseSub(context.User),
            permissions,
            context.User.FindFirstValue("practitioner_id") is { Length: > 0 } p ? p : null);

        var decision = tooLarge ?? await evaluator.EvaluateAsync(caller, descriptor, body);

        var options = gatewayOptions.Value;
        var mode = decision.Stage == FhirDecisionStage.Intent ? options.IntentMode : options.Mode;
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

        // Recorte de identificación. Se aplica reescribiendo la consulta saliente y no filtrando la
        // respuesta: filtrar obligaría a bufferear todo lo que HAPI devuelve, y el reenvío dejaría
        // de ser streaming para todos los requests por proteger uno.
        //
        // Solo en Enforce. En sombra la clasificación mira y anota; si además recortara campos,
        // "no cambia el comportamiento" dejaría de ser cierto y el modo no serviría para lo que
        // sirve.
        if (decision.Projection is { } elements && options.IntentMode == FhirGatewayMode.Enforce)
        {
            // El cliente no puede traer `_elements` propio: no está declarado en ningún intent, así
            // que una consulta que lo mande ni siquiera llega hasta acá.
            context.Request.QueryString = context.Request.QueryString.Add("_elements", elements);
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
    /// Lee el cuerpo para evaluarlo, dejándolo disponible para el reenvío.
    ///
    /// <para>Devuelve <c>null</c> si supera el tope: lo que no se puede evaluar no se aprueba, y
    /// el que decide qué hacer con eso es el llamador.</para>
    ///
    /// <para>Se recorre por trozos y con el buffer prestado del pool. Reservar el tope entero por
    /// request se podía tolerar mientras el único caso era el Bundle de cierre de consulta; ahora
    /// que cada signo vital pasa por acá, serían megabytes por captura de preclínica.</para>
    /// </summary>
    private static async Task<string?> ReadBodyAsync(HttpRequest request)
    {
        request.EnableBuffering();

        var buffer = ArrayPool<char>.Shared.Rent(ReadChunkChars);
        try
        {
            using var reader = new StreamReader(request.Body, leaveOpen: true);
            var builder = new StringBuilder();

            int read;
            while ((read = await reader.ReadBlockAsync(buffer, request.HttpContext.RequestAborted)) > 0)
            {
                builder.Append(buffer, 0, read);
                if (builder.Length > MaxBodyChars)
                {
                    return null;
                }
            }

            return builder.ToString();
        }
        finally
        {
            ArrayPool<char>.Shared.Return(buffer);
            request.Body.Position = 0;
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
                // Qué puerta decidió. Sin esto no se puede separar en la bitácora lo que negó la
                // lista blanca —que bloquea— de lo que negó la clasificación por forma, que
                // todavía está en sombra.
                stage = decision.Stage.ToString(),
                // El nombre de la forma reconocida. Es lo que vuelve consultable la pregunta de
                // ADR-007: `agenda.mine` y `patient.appointments` son la misma búsqueda de citas
                // para el verbo, y la diferencia entre ver la agenda propia y la de otro.
                intent = decision.Intent,
                // Las entradas del Bundle. Sin esto el registro dice `Create:Bundle` y nada más.
                entries = decision.BundleEntries,
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
