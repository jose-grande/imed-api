using System.Security.Claims;
using IMed.Api.Application.LabReception;

namespace IMed.Api.WebApi.Endpoints;

/// <summary>
/// Endpoints de recepción de laboratorio (Épica LIS-3, L3-F2). Punto de entrada
/// unificado: resolver orden interna, capturar orden externa, y recibir la
/// muestra (con enrutamiento + precondiciones). Política <c>LabTechnician</c>.
/// </summary>
public static class LabReceptionEndpoints
{
    public static IEndpointRouteBuilder MapLabReceptionEndpoints(this IEndpointRouteBuilder app)
    {
        // Baseline lectura (LabRead); las escrituras exigen LabTechnician.
        var group = app.MapGroup("/api/lab/reception")
            .WithTags("LabReception")
            .RequireAuthorization("LabRead");

        group.MapGet("/order/{serviceRequestId}", ResolveAsync)
            .WithName("ResolveReceptionOrder")
            .WithSummary("Resuelve una orden interna: paciente, examen, enrutamiento y precondiciones.");

        group.MapPost("/external-order", CreateExternalAsync)
            .WithName("CreateExternalOrder")
            .WithSummary("Crea una orden externa (médico externo + ServiceRequest) para un paciente ya resuelto.")
            .RequireAuthorization("LabTechnician");

        group.MapPost("/receive", ReceiveAsync)
            .WithName("ReceiveSpecimen")
            .WithSummary("Recibe la muestra: crea Specimen/accession/intake o bloquea por precondición.")
            .RequireAuthorization("LabTechnician");

        group.MapGet("/requisition/{encounterId}", ResolveRequisitionAsync)
            .WithName("ResolveRequisition")
            .WithSummary("Resuelve una visita como requisición: órdenes agrupadas por tipo de muestra a recolectar.");

        group.MapGet("/by-order/{requisition}", ResolveByOrderAsync)
            .WithName("ResolveByOrder")
            .WithSummary("Resuelve una orden por su número (requisition) — buscador de recepción (F2).");

        group.MapPost("/check-in", CheckInAsync)
            .WithName("CheckInOrder")
            .WithSummary("Registra el ingreso de una orden a la cola (el paciente la hará en la clínica) — F2.")
            .RequireAuthorization("LabTechnician");

        group.MapPost("/refer-external", ReferExternalAsync)
            .WithName("ReferOrderExternal")
            .WithSummary("Refiere una orden completa a un laboratorio externo (trazabilidad) — F3.")
            .RequireAuthorization("LabTechnician");

        group.MapPost("/receive-requisition", ReceiveRequisitionAsync)
            .WithName("ReceiveRequisition")
            .WithSummary("Recibe una requisición: una muestra por grupo (N:M), con rechazo/bloqueo por grupo.")
            .RequireAuthorization("LabTechnician");

        group.MapGet("/awaiting-admission", GetAwaitingAdmissionAsync)
            .WithName("GetAwaitingAdmission")
            .WithSummary("Órdenes solicitadas por un médico aún no admitidas (worklist de la estación Recepción, Fase 1).");

        group.MapPost("/external-order-multi", CreateExternalOrderMultiAsync)
            .WithName("CreateExternalOrderMulti")
            .WithSummary("Crea una orden externa con varios exámenes/paneles (médico/institución externa) — Fase 1.")
            .RequireAuthorization("LabTechnician");

        group.MapGet("/pending", GetPendingAsync)
            .WithName("GetPendingOrders")
            .WithSummary("Cola de órdenes de laboratorio activas aún no recibidas.");

        group.MapGet("/label/{specimenFhirId}", GetLabelAsync)
            .WithName("GetSpecimenLabel")
            .WithSummary("Datos de la etiqueta/código de barras de una muestra (accession + paciente + tipo).");

        group.MapGet("/collection-queue", GetCollectionQueueAsync)
            .WithName("GetCollectionQueue")
            .WithSummary("Cola de toma on-site: órdenes activas cuyo tipo de muestra se toma en el laboratorio.");

        group.MapGet("/technical-reception", GetTechnicalReceptionAsync)
            .WithName("GetTechnicalReception")
            .WithSummary("Worklist de recepción técnica agrupada por orden: muestras esperando QC, con las ya recibidas para el conteo (Fase 3).");

        group.MapGet("/technical-reception/scan/{code}", ScanTechnicalReceptionAsync)
            .WithName("ScanTechnicalReception")
            .WithSummary("Resuelve el código de barras escaneado en el bench a una muestra e indica qué se puede hacer con ella (Fase 3).");

        group.MapPost("/collection-plan", ComputePlanAsync)
            .WithName("ComputeCollectionPlan")
            .WithSummary("Calcula y persiste el plan de colección de una requisición: contenedores/tubos/volúmenes consolidados (R6-F2).")
            .RequireAuthorization("LabTechnician");

        group.MapGet("/collection-plan/{requisition}", GetPlanAsync)
            .WithName("GetCollectionPlan")
            .WithSummary("Devuelve el plan de colección persistido de una requisición (R6-F2).");

        group.MapPost("/collection-plan/{requisition}/labels", GenerateLabelsAsync)
            .WithName("GenerateCollectionLabels")
            .WithSummary("Genera accession/barcode por contenedor del plan, antes de la toma (R6-F4).")
            .RequireAuthorization("LabTechnician");

        // Impresión real de etiqueta (R6-F4b). Es POST y no GET porque emitir una etiqueta
        // muta el ciclo de vida del contenedor y deja rastro en la cadena de custodia.
        group.MapPost("/collection-plan/container/{containerId:guid}/print", PrintContainerLabelAsync)
            .WithName("PrintCollectionContainerLabel")
            .WithSummary("Devuelve el PDF de la etiqueta de un contenedor, con el tamaño configurado para su tubo, y registra la impresión (R6-F4b).")
            .RequireAuthorization("LabTechnician");

        group.MapPost("/collection-plan/{requisition}/print", PrintPlanLabelsAsync)
            .WithName("PrintCollectionPlanLabels")
            .WithSummary("Devuelve un PDF con todas las etiquetas del plan (una por página, en order of draw) y registra las impresiones (R6-F4b).")
            .RequireAuthorization("LabTechnician");

        group.MapPost("/collection-plan/container/{containerId:guid}/advance", AdvanceContainerAsync)
            .WithName("AdvanceCollectionContainer")
            .WithSummary("Avanza el ciclo de vida de un contenedor (imprimir/toma/recibir/rechazar/almacenar) — R6-F3.")
            .RequireAuthorization("LabTechnician");

        group.MapGet("/collection-plan/container/{containerId:guid}/timeline", GetTimelineAsync)
            .WithName("GetCollectionContainerTimeline")
            .WithSummary("Timeline de cadena de custodia de un contenedor (R6-F3).");

        group.MapPost("/collection-plan/container/{containerId:guid}/verify-identity", VerifyIdentityAsync)
            .WithName("VerifyCollectionContainerIdentity")
            .WithSummary("Identificación positiva del paciente (nombre + fecha de nacimiento) en admisión/toma (R6-F6).")
            .RequireAuthorization("LabTechnician");

        group.MapPost("/collection-plan/container/{containerId:guid}/qc", QualityControlAsync)
            .WithName("CollectionContainerQualityControl")
            .WithSummary("Control de calidad preanalítico: acepta (recibe) o rechaza con motivo del catálogo (R6-F6).")
            .RequireAuthorization("LabTechnician");

        return app;
    }

    private static async Task<IResult> ResolveAsync(
        string serviceRequestId, ClaimsPrincipal principal, ResolveReceptionOrderHandler handler, CancellationToken ct)
    {
        var orgId = ParseOrganizationId(principal);
        if (orgId is null)
        {
            return Forbidden();
        }
        var result = await handler.HandleAsync(orgId.Value, serviceRequestId, ct);
        return result is null ? Results.NotFound() : Results.Ok(result);
    }

    private static async Task<IResult> CreateExternalAsync(
        CreateExternalOrderRequest request, ClaimsPrincipal principal, CreateExternalOrderHandler handler, CancellationToken ct)
    {
        var orgId = ParseOrganizationId(principal);
        if (orgId is null)
        {
            return Forbidden();
        }
        try
        {
            var result = await handler.HandleAsync(new CreateExternalOrderCommand(
                orgId.Value, request.PatientFhirId, request.RequesterName, request.RequesterIdentifier,
                request.LoincCode, request.CodeText, request.Priority, request.OccurrenceDateTime,
                request.ReasonText, request.Note), ct);
            return Results.Created($"/api/lab/reception/order/{result.ServiceRequestId}", result);
        }
        catch (ArgumentException ex)
        {
            return Results.Problem(title: "Solicitud inválida", detail: ex.Message, statusCode: StatusCodes.Status400BadRequest);
        }
    }

    private static async Task<IResult> ReceiveAsync(
        ReceiveRequest request, ClaimsPrincipal principal, ReceiveSpecimenHandler handler, CancellationToken ct)
    {
        var orgId = ParseOrganizationId(principal);
        var userId = ParseSub(principal);
        if (orgId is null || userId is null)
        {
            return Forbidden();
        }
        try
        {
            var result = await handler.HandleAsync(new ReceiveCommand(
                orgId.Value, userId.Value, request.ServiceRequestId, request.FastingSatisfied,
                request.IsWalkIn, request.ScheduledFor, request.TypeText, request.CollectedDateTime,
                request.Note), ct);
            return Results.Ok(result);
        }
        catch (KeyNotFoundException) { return Results.NotFound(); }
        catch (InvalidOperationException ex)
        {
            return Results.Problem(title: "Conflicto", detail: ex.Message, statusCode: StatusCodes.Status409Conflict);
        }
        catch (ArgumentException ex)
        {
            return Results.Problem(title: "Solicitud inválida", detail: ex.Message, statusCode: StatusCodes.Status400BadRequest);
        }
    }

    private static async Task<IResult> ResolveRequisitionAsync(
        string encounterId, ClaimsPrincipal principal, ResolveRequisitionHandler handler, CancellationToken ct)
    {
        var orgId = ParseOrganizationId(principal);
        if (orgId is null)
        {
            return Forbidden();
        }
        var result = await handler.HandleAsync(orgId.Value, encounterId, ct);
        return result is null ? Results.NotFound() : Results.Ok(result);
    }

    private static async Task<IResult> ResolveByOrderAsync(
        string requisition, ClaimsPrincipal principal, ResolveRequisitionHandler handler, CancellationToken ct)
    {
        var orgId = ParseOrganizationId(principal);
        if (orgId is null)
        {
            return Forbidden();
        }
        var result = await handler.HandleByOrderAsync(orgId.Value, requisition, ct);
        return result is null ? Results.NotFound() : Results.Ok(result);
    }

    private static async Task<IResult> CheckInAsync(
        CheckInRequest request, ClaimsPrincipal principal, CheckInOrderHandler handler, CancellationToken ct)
    {
        var orgId = ParseOrganizationId(principal);
        var userId = ParseSub(principal);
        if (orgId is null || userId is null)
        {
            return Forbidden();
        }
        if (string.IsNullOrWhiteSpace(request.Requisition))
        {
            return Results.Problem(title: "Solicitud inválida", detail: "Falta el número de orden.", statusCode: StatusCodes.Status400BadRequest);
        }
        var result = await handler.HandleAsync(orgId.Value, request.Requisition, userId.Value, ct);
        return result is null ? Results.NotFound() : Results.Ok(result);
    }

    private static async Task<IResult> ReferExternalAsync(
        CheckInRequest request, ClaimsPrincipal principal, ReferOrderExternalHandler handler, CancellationToken ct)
    {
        var orgId = ParseOrganizationId(principal);
        var userId = ParseSub(principal);
        if (orgId is null || userId is null)
        {
            return Forbidden();
        }
        if (string.IsNullOrWhiteSpace(request.Requisition))
        {
            return Results.Problem(title: "Solicitud inválida", detail: "Falta el número de orden.", statusCode: StatusCodes.Status400BadRequest);
        }
        var referred = await handler.HandleAsync(orgId.Value, request.Requisition, userId.Value, ct);
        return referred is null ? Results.NotFound() : Results.Ok(new { referred });
    }

    private static async Task<IResult> ReceiveRequisitionAsync(
        ReceiveRequisitionRequest request, ClaimsPrincipal principal, ReceiveRequisitionHandler handler, CancellationToken ct)
    {
        var orgId = ParseOrganizationId(principal);
        var userId = ParseSub(principal);
        if (orgId is null || userId is null)
        {
            return Forbidden();
        }
        try
        {
            var result = await handler.HandleAsync(new ReceiveRequisitionCommand(
                orgId.Value, userId.Value, request.Requisition, request.Groups), ct);
            return Results.Ok(result);
        }
        catch (ArgumentException ex)
        {
            return Results.Problem(title: "Solicitud inválida", detail: ex.Message, statusCode: StatusCodes.Status400BadRequest);
        }
    }

    private static async Task<IResult> GetPendingAsync(
        ClaimsPrincipal principal, GetPendingOrdersHandler handler, CancellationToken ct)
    {
        var orgId = ParseOrganizationId(principal);
        if (orgId is null)
        {
            return Forbidden();
        }
        return Results.Ok(await handler.HandleAsync(orgId.Value, ct));
    }

    private static async Task<IResult> GetAwaitingAdmissionAsync(
        ClaimsPrincipal principal, GetAwaitingAdmissionHandler handler, CancellationToken ct)
    {
        var orgId = ParseOrganizationId(principal);
        if (orgId is null)
        {
            return Forbidden();
        }
        return Results.Ok(await handler.HandleAsync(orgId.Value, ct));
    }

    private static async Task<IResult> CreateExternalOrderMultiAsync(
        CreateExternalOrderMultiRequest request, ClaimsPrincipal principal,
        CreateExternalOrderMultiHandler handler, CancellationToken ct)
    {
        var orgId = ParseOrganizationId(principal);
        if (orgId is null)
        {
            return Forbidden();
        }
        try
        {
            var result = await handler.HandleAsync(new CreateExternalOrderMultiCommand(
                orgId.Value, request.PatientFhirId, request.RequesterName, request.RequesterIdentifier,
                request.RequesterInstitution, request.Priority, request.OccurrenceDateTime, request.ReasonText,
                request.Note, request.Exams ?? []), ct);
            return Results.Created($"/api/lab/reception/by-order/{result.Requisition}", result);
        }
        catch (ArgumentException ex)
        {
            return Results.Problem(title: "Solicitud inválida", detail: ex.Message, statusCode: StatusCodes.Status400BadRequest);
        }
    }

    private static async Task<IResult> GetLabelAsync(
        string specimenFhirId, ClaimsPrincipal principal, GetSpecimenLabelHandler handler, CancellationToken ct)
    {
        var orgId = ParseOrganizationId(principal);
        if (orgId is null)
        {
            return Forbidden();
        }
        var result = await handler.HandleAsync(orgId.Value, specimenFhirId, ct);
        return result is null ? Results.NotFound() : Results.Ok(result);
    }

    private static async Task<IResult> GetCollectionQueueAsync(
        ClaimsPrincipal principal, GetCollectionQueueHandler handler, CancellationToken ct)
    {
        var orgId = ParseOrganizationId(principal);
        if (orgId is null)
        {
            return Forbidden();
        }
        return Results.Ok(await handler.HandleAsync(orgId.Value, ct));
    }

    private static async Task<IResult> GetTechnicalReceptionAsync(
        ClaimsPrincipal principal, GetTechnicalReceptionHandler handler, CancellationToken ct)
    {
        var orgId = ParseOrganizationId(principal);
        if (orgId is null)
        {
            return Forbidden();
        }
        return Results.Ok(await handler.HandleAsync(orgId.Value, ct));
    }

    private static async Task<IResult> ScanTechnicalReceptionAsync(
        string code, ClaimsPrincipal principal, GetTechnicalReceptionHandler handler, CancellationToken ct)
    {
        var orgId = ParseOrganizationId(principal);
        if (orgId is null)
        {
            return Forbidden();
        }
        // Siempre 200: "no encontrado" es un desenlace legítimo del escaneo que la UI tiene
        // que poder mostrar con su propio mensaje, no un 404 que se pierda como error genérico.
        return Results.Ok(await handler.ScanAsync(orgId.Value, code, ct));
    }

    private static async Task<IResult> ComputePlanAsync(
        ComputeCollectionPlanRequest request, ClaimsPrincipal principal, ComputeCollectionPlanHandler handler, CancellationToken ct)
    {
        var orgId = ParseOrganizationId(principal);
        if (orgId is null)
        {
            return Forbidden();
        }
        var result = await handler.HandleAsync(orgId.Value, request.Requisition, ParseSub(principal), ct);
        return result is null ? Results.NotFound() : Results.Ok(result);
    }

    private static async Task<IResult> GetPlanAsync(
        string requisition, ClaimsPrincipal principal, GetCollectionPlanHandler handler, CancellationToken ct)
    {
        var orgId = ParseOrganizationId(principal);
        if (orgId is null)
        {
            return Forbidden();
        }
        var result = await handler.HandleAsync(orgId.Value, requisition, ct);
        return result is null ? Results.NotFound() : Results.Ok(result);
    }

    private static async Task<IResult> GenerateLabelsAsync(
        string requisition, ClaimsPrincipal principal, GenerateLabelsHandler handler, CancellationToken ct)
    {
        var orgId = ParseOrganizationId(principal);
        if (orgId is null)
        {
            return Forbidden();
        }
        var result = await handler.HandleAsync(orgId.Value, requisition, ParseSub(principal), ct);
        return result is null ? Results.NotFound() : Results.Ok(result);
    }

    private static async Task<IResult> AdvanceContainerAsync(
        Guid containerId, AdvanceContainerRequest request, ClaimsPrincipal principal,
        AdvanceContainerHandler handler, CancellationToken ct)
    {
        var orgId = ParseOrganizationId(principal);
        if (orgId is null)
        {
            return Forbidden();
        }
        try
        {
            var result = await handler.HandleAsync(orgId.Value, containerId, request.Action, request.Detail, ParseSub(principal), ct);
            return result is null ? Results.NotFound() : Results.Ok(result);
        }
        catch (ArgumentException ex)
        {
            return Results.Problem(title: "Solicitud inválida", detail: ex.Message, statusCode: StatusCodes.Status400BadRequest);
        }
    }

    private static async Task<IResult> PrintContainerLabelAsync(
        Guid containerId, PrintLabelsRequest? request, ClaimsPrincipal principal,
        PrintSpecimenLabelsHandler handler, CancellationToken ct)
    {
        var orgId = ParseOrganizationId(principal);
        if (orgId is null)
        {
            return Forbidden();
        }
        try
        {
            var result = await handler.HandleContainerAsync(
                orgId.Value, containerId, request?.Reason, ParseSub(principal), ct);
            return result is null
                ? Results.NotFound()
                : Results.File(result.Bytes, "application/pdf", fileDownloadName: result.FileName);
        }
        catch (ArgumentException ex)
        {
            return Results.Problem(title: "Solicitud inválida", detail: ex.Message, statusCode: StatusCodes.Status400BadRequest);
        }
    }

    private static async Task<IResult> PrintPlanLabelsAsync(
        string requisition, PrintLabelsRequest? request, ClaimsPrincipal principal,
        PrintSpecimenLabelsHandler handler, CancellationToken ct)
    {
        var orgId = ParseOrganizationId(principal);
        if (orgId is null)
        {
            return Forbidden();
        }
        try
        {
            var result = await handler.HandlePlanAsync(
                orgId.Value, requisition, request?.Reason, ParseSub(principal), ct);
            return result is null
                ? Results.NotFound()
                : Results.File(result.Bytes, "application/pdf", fileDownloadName: result.FileName);
        }
        catch (ArgumentException ex)
        {
            return Results.Problem(title: "Solicitud inválida", detail: ex.Message, statusCode: StatusCodes.Status400BadRequest);
        }
    }

    private static async Task<IResult> GetTimelineAsync(
        Guid containerId, GetContainerTimelineHandler handler, CancellationToken ct)
    {
        return Results.Ok(await handler.HandleAsync(containerId, ct));
    }

    private static async Task<IResult> VerifyIdentityAsync(
        Guid containerId, VerifyIdentityRequest request, ClaimsPrincipal principal,
        VerifyIdentityHandler handler, CancellationToken ct)
    {
        var orgId = ParseOrganizationId(principal);
        if (orgId is null)
        {
            return Forbidden();
        }
        try
        {
            var result = await handler.HandleAsync(
                orgId.Value, containerId, request.VerifiedName, request.VerifiedBirthDate,
                request.Stage ?? "Collection", request.PatientName, request.PatientBirthDate, ParseSub(principal), ct);
            return result is null ? Results.NotFound() : Results.Ok(result);
        }
        catch (ArgumentException ex)
        {
            return Results.Problem(title: "Solicitud inválida", detail: ex.Message, statusCode: StatusCodes.Status400BadRequest);
        }
    }

    private static async Task<IResult> QualityControlAsync(
        Guid containerId, QualityControlRequest request, ClaimsPrincipal principal,
        QualityControlHandler handler, CancellationToken ct)
    {
        var orgId = ParseOrganizationId(principal);
        if (orgId is null)
        {
            return Forbidden();
        }
        try
        {
            var result = await handler.HandleAsync(
                orgId.Value, containerId, request.Accepted, request.RejectionReasonCode, request.Note, ParseSub(principal), ct);
            return result is null ? Results.NotFound() : Results.Ok(result);
        }
        catch (ArgumentException ex)
        {
            return Results.Problem(title: "Solicitud inválida", detail: ex.Message, statusCode: StatusCodes.Status400BadRequest);
        }
    }

    private static IResult Forbidden() =>
        Results.Problem(title: "Sin organización", statusCode: StatusCodes.Status403Forbidden);

    private static Guid? ParseOrganizationId(ClaimsPrincipal principal) =>
        Guid.TryParse(principal.FindFirstValue("organization_id"), out var id) ? id : null;

    private static Guid? ParseSub(ClaimsPrincipal principal)
    {
        var raw = principal.FindFirstValue(ClaimTypes.NameIdentifier) ?? principal.FindFirstValue("sub");
        return Guid.TryParse(raw, out var sub) ? sub : null;
    }
}

public sealed record CreateExternalOrderRequest(
    string PatientFhirId,
    string RequesterName,
    string? RequesterIdentifier,
    string? LoincCode,
    string CodeText,
    string? Priority,
    string? OccurrenceDateTime,
    string? ReasonText,
    string? Note);

public sealed record ReceiveRequest(
    string ServiceRequestId,
    bool? FastingSatisfied,
    bool IsWalkIn,
    DateTimeOffset? ScheduledFor,
    string? TypeText,
    string? CollectedDateTime,
    string? Note);

public sealed record ReceiveRequisitionRequest(
    string Requisition,
    IReadOnlyList<ReceiveGroup> Groups);

public sealed record CheckInRequest(string Requisition);

/// <summary>Fase 1: crear orden externa con varios exámenes/paneles.</summary>
public sealed record CreateExternalOrderMultiRequest(
    string PatientFhirId,
    string RequesterName,
    string? RequesterIdentifier,
    string? RequesterInstitution,
    string? Priority,
    string? OccurrenceDateTime,
    string? ReasonText,
    string? Note,
    IReadOnlyList<ExternalOrderExam> Exams);

public sealed record ComputeCollectionPlanRequest(string Requisition);

public sealed record AdvanceContainerRequest(string Action, string? Detail);

/// <summary>R6-F4b: impresión de etiqueta. <c>Reason</c> es obligatorio solo cuando se
/// reimprime una muestra que ya avanzó del estado "etiqueta generada" (D9).</summary>
public sealed record PrintLabelsRequest(string? Reason);

/// <summary>R6-F6: identificación positiva (nombre + fecha de nacimiento). Stage = "Collection" (toma) o "Admission".</summary>
public sealed record VerifyIdentityRequest(
    bool VerifiedName, bool VerifiedBirthDate, string? Stage, string? PatientName, string? PatientBirthDate);

/// <summary>R6-F6: control de calidad preanalítico. Aceptar (recibe) o rechazar con motivo del catálogo.</summary>
public sealed record QualityControlRequest(bool Accepted, string? RejectionReasonCode, string? Note);
