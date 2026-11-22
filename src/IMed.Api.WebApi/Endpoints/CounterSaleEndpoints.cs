using System.Security.Claims;
using IMed.Api.Application.Abstractions;
using IMed.Api.Application.Billing;
using IMed.Api.Application.LabReception;
using IMed.Api.Application.Patients;
using IMed.Api.Domain.Authorization;
using IMed.Api.WebApi.Http;

namespace IMed.Api.WebApi.Endpoints;

/// <summary>
/// Vender en la Caja (UX-Cobro-Anticipado F2): cotizar, crear la venta y dar de alta rápida al
/// paciente. Bajo <c>/api/billing/sales</c> y <c>/api/patients/quick</c>.
///
/// <para>Vender pide <c>billing.collect</c>: lo hace quien cobra. La venta crea la orden de
/// laboratorio sin pedir permisos del laboratorio a propósito: el cajero no opera el laboratorio,
/// solo le manda la orden, igual que un médico externo.</para>
/// </summary>
public static class CounterSaleEndpoints
{
    public static IEndpointRouteBuilder MapCounterSaleEndpoints(this IEndpointRouteBuilder app)
    {
        var sales = app.MapGroup("/api/billing/sales")
            .WithTags("Billing · Venta en caja")
            .RequireAuthorization(PermissionPolicy.For(PermissionCatalog.BillingCollect));

        sales.MapGet("/catalog", SearchAsync)
            .WithName("SearchSellable")
            .WithSummary("Qué se puede vender: exámenes ofertados, paneles activos y procedimientos de mostrador, con precio.");

        sales.MapGet("/appointments", AppointmentsAsync)
            .WithName("SaleAppointments")
            .WithSummary("Consultas agendadas de un paciente que se pueden cobrar por adelantado (modo \"antes\" o \"mixto\").");

        sales.MapPost("/quote", QuoteAsync)
            .WithName("QuoteCounterSale")
            .WithSummary("Precio de cada examen y procedimiento antes de vender. No crea nada.");

        sales.MapPost("/", CreateAsync)
            .WithName("CreateCounterSale")
            .WithSummary("Crea la orden de laboratorio (admitida) y carga los procedimientos. Devuelve la cuenta a cobrar.");

        // Para la Agenda: la ve quien agenda, que no necesariamente cobra. Solo dice si está pagada.
        app.MapGet("/api/billing/sales/appointment-status", AppointmentStatusAsync)
            .WithTags("Billing · Venta en caja")
            .WithName("AppointmentPaymentStatus")
            .WithSummary("Si la consulta de cada cita (ids separados por coma) ya se cobró por adelantado.")
            .RequireAuthorization(PermissionPolicy.For(PermissionCatalog.RecordSearchDemographics));

        app.MapPost("/api/patients/quick", QuickRegisterAsync)
            .WithTags("Patients")
            .WithName("QuickRegisterPatient")
            .WithSummary("Alta rápida desde un mostrador: identificación, nombres, apellidos y fecha de nacimiento. Si la identificación ya existe, devuelve ese paciente.")
            .RequireAuthorization();

        return app;
    }

    private static Task<IResult> SearchAsync(
        string? q, int? limit, ClaimsPrincipal principal, CounterSaleHandler handler, CancellationToken ct) =>
        EndpointRun.Org(principal, async org => Results.Ok(await handler.SearchAsync(org, EndpointRun.User(principal), q, limit ?? 20, ct)));

    private static Task<IResult> AppointmentsAsync(
        string patientFhirId, ClaimsPrincipal principal, CounterSaleHandler handler, CancellationToken ct) =>
        EndpointRun.Org(principal, async org => Results.Ok(await handler.ListAppointmentsAsync(org, patientFhirId, ct)));

    private static Task<IResult> AppointmentStatusAsync(
        string? ids, ClaimsPrincipal principal, CounterSaleHandler handler, CancellationToken ct) =>
        EndpointRun.Org(principal, async org => Results.Ok(await handler.AppointmentPaymentStatusAsync(
            org, (ids ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries), ct)));

    private static Task<IResult> QuoteAsync(
        SaleQuoteRequest body, ClaimsPrincipal principal, CounterSaleHandler handler, CancellationToken ct) =>
        EndpointRun.Org(principal, async org =>
            Results.Ok(await handler.QuoteAsync(org, body.Exams, body.Procedures, ct)));

    private static Task<IResult> CreateAsync(
        CounterSaleInput body, ClaimsPrincipal principal, CounterSaleHandler handler, CancellationToken ct) =>
        EndpointRun.Org(principal, async org => EndpointRun.User(principal) is not { } user
            ? EndpointRun.Forbidden("El token no trae usuario.")
            : Results.Ok(await handler.CreateAsync(org, user, body, ct)));

    /// <summary>
    /// Lo puede hacer quien cobra o quien escribe el expediente: el cajero registra al paciente que
    /// tiene enfrente sin poder tocar nada clínico.
    /// </summary>
    private static Task<IResult> QuickRegisterAsync(
        QuickRegisterRequest body, ClaimsPrincipal principal, QuickRegisterPatientHandler handler,
        IPermissionResolver permisos, CancellationToken ct) =>
        EndpointRun.Org(principal, async org =>
        {
            if (!await EndpointRun.HasAsync(principal, permisos, org, PermissionCatalog.BillingCollect, ct)
                && !await EndpointRun.HasAsync(principal, permisos, org, PermissionCatalog.RecordWrite, ct))
            {
                return EndpointRun.Forbidden("El alta rápida de pacientes es de caja o de quien escribe el expediente.");
            }
            var r = await handler.HandleAsync(new QuickRegisterPatientCommand(
                org, body.CountryCode ?? "HN", body.IdentifierType ?? "IdentidadNacional", body.IdentifierValue ?? string.Empty,
                body.GivenNames ?? string.Empty, body.FamilyNames ?? string.Empty,
                body.BirthDate ?? throw new ArgumentException("La fecha de nacimiento es obligatoria."),
                body.Gender, body.Phone), ct);
            return Results.Ok(r);
        });
}

public sealed record SaleQuoteRequest(
    IReadOnlyList<ExternalOrderExam>? Exams,
    IReadOnlyList<CounterSaleProcedure>? Procedures);

public sealed record QuickRegisterRequest(
    string? CountryCode,
    string? IdentifierType,
    string? IdentifierValue,
    string? GivenNames,
    string? FamilyNames,
    DateOnly? BirthDate,
    string? Gender,
    string? Phone);
