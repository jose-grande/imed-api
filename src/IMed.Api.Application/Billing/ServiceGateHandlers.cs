using IMed.Api.Application.Abstractions;
using IMed.Api.Domain.Billing.Accounts;
using IMed.Api.Domain.Billing.Catalog;
using IMed.Api.Domain.Billing.Gate;
using IMed.Api.Domain.Billing.Invoicing;

namespace IMed.Api.Application.Billing;

public sealed record GateDecisionDto(
    string Verdict,
    string Reason,
    string Message,
    Guid? WaivedBy,
    string? WaiverReason,
    decimal? AmountDue);

/// <summary>Lo que el módulo clínico pregunta: ¿este servicio puede ejecutarse?</summary>
public sealed record GateQuery(
    Guid OrganizationId,
    /// <summary>Referencia del acto: <c>ServiceRequest/2405</c>.</summary>
    string SourceRef,
    /// <summary>Id del acto dentro de su origen, para encontrar el cargo.</summary>
    string SourceId,
    ChargeSourceKind SourceKind,
    string? Priority = null);

/// <summary>
/// La puerta de prepago (FAC-7, hueco G2).
///
/// <para><b>El LIS consulta, no calcula.</b> Toda la lógica de si algo está pagado vive acá; el
/// módulo clínico recibe un veredicto y un texto para mostrar. Repartir esta decisión entre los
/// módulos garantiza que cada uno la implemente distinto.</para>
///
/// <para><b>Orden de las respuestas, y por qué.</b> Primero la urgencia clínica —un <c>stat</c>
/// nunca espera—, después la exoneración explícita —alguien ya decidió—, después el estado del
/// cobro. Preguntar por el pago antes que por la urgencia haría que una emergencia con la cuenta
/// impaga muestre "pendiente de pago", que es exactamente el momento en que ese cartel estorba.</para>
/// </summary>
public sealed class EvaluateServiceGateHandler(
    IBillingCatalogRepository catalog,
    IBillingAccountRepository accounts,
    IInvoiceRepository invoices,
    IServiceWaiverRepository waivers)
{
    public async Task<GateDecisionDto> HandleAsync(GateQuery query, CancellationToken ct = default)
    {
        // 1. La emergencia siempre pasa. Criterio clínico, no configuración.
        if (ClinicalUrgency.BypassesPayment(query.Priority))
        {
            return ToDto(GateDecision.Urgent());
        }

        // 2. ¿Alguien ya lo liberó? Preguntarlo antes del cobro evita mostrar "pendiente" sobre
        //    algo que un supervisor ya resolvió.
        if (await waivers.FindAsync(query.OrganizationId, query.SourceRef, ct) is { } waiver)
        {
            return ToDto(GateDecision.Waived(waiver.WaivedBy, waiver.Reason));
        }

        // 3. ¿Hay cargo? Sin cargo no hay nada que cobrar — y eso no es "autorizado", es una
        //    deuda del catálogo que alguien tiene que ver.
        var cargo = await accounts.FindAnyLiveChargeBySourceAsync(
            query.OrganizationId, query.SourceKind, query.SourceId, ct);

        if (cargo is null)
        {
            return ToDto(GateDecision.NotCharged());
        }

        // 4. ¿El cobrable es prepago? Si no, no bloquea nunca.
        var item = await catalog.GetItemAsync(query.OrganizationId, cargo.BillableItemId, ct);
        if (item is null || item.PaymentTiming != PaymentTiming.Prepaid)
        {
            return ToDto(GateDecision.Postpaid());
        }

        // 5. ¿Está cobrado? Un cargo facturado con la factura pagada está listo.
        if (cargo.Status != ChargeStatus.Invoiced)
        {
            return ToDto(GateDecision.Pending(cargo.Total));
        }

        var facturas = await invoices.ListByAccountAsync(query.OrganizationId, cargo.AccountId, ct);
        var suya = facturas.FirstOrDefault(f => f.Lines.Any(l => l.ChargeId == cargo.Id));

        if (suya is null || suya.Status == InvoiceStatus.Voided)
        {
            return ToDto(GateDecision.Pending(cargo.Total));
        }

        return ToDto(suya.Balance <= 0
            ? GateDecision.Paid()
            : GateDecision.Pending(suya.Balance));
    }

    private static GateDecisionDto ToDto(GateDecision d) =>
        new(d.Verdict.ToString(), d.Reason.ToString(), d.Message,
            d.WaivedBy, d.WaiverReason, d.AmountDue);
}

/// <summary>
/// Implementación del puerto que consumen los módulos clínicos. Traduce el veredicto completo a
/// lo mínimo que el laboratorio necesita: qué mostrar y cuánto falta.
/// </summary>
public sealed class ServiceGateAdapter(EvaluateServiceGateHandler handler) : IServiceGate
{
    public async Task<ServiceGateStatus> EvaluateAsync(
        Guid organizationId, string serviceRequestId, string? priority, CancellationToken ct = default)
    {
        var d = await handler.HandleAsync(new GateQuery(
            organizationId,
            $"ServiceRequest/{serviceRequestId}",
            serviceRequestId,
            ChargeSourceKind.LabOrder,
            priority), ct);

        return new ServiceGateStatus(d.Verdict, d.Message, d.AmountDue);
    }
}

public sealed record WaiveServiceCommand(
    Guid OrganizationId,
    string SourceRef,
    Guid WaivedBy,
    string Reason,
    Guid? AccountId);

public sealed record ServiceWaiverDto(
    Guid Id, string SourceRef, Guid WaivedBy, string Reason, DateTimeOffset WaivedAt);

/// <summary>
/// Deja pasar un servicio sin cobrarlo, dejando constancia.
///
/// <para>Idempotente: liberar dos veces el mismo servicio devuelve la constancia que ya existe.
/// El técnico que aprieta el botón otra vez porque la pantalla tardó no debería generar dos
/// registros de excepción sobre el mismo acto.</para>
/// </summary>
public sealed class WaiveServiceHandler(IServiceWaiverRepository waivers)
{
    public async Task<ServiceWaiverDto> HandleAsync(
        WaiveServiceCommand cmd, CancellationToken ct = default)
    {
        if (await waivers.FindAsync(cmd.OrganizationId, cmd.SourceRef, ct) is { } existente)
        {
            return ToDto(existente);
        }

        var waiver = ServiceWaiver.Create(
            cmd.OrganizationId, cmd.SourceRef, cmd.WaivedBy, cmd.Reason, cmd.AccountId);

        await waivers.AddAsync(waiver, ct);
        return ToDto(waiver);
    }

    private static ServiceWaiverDto ToDto(ServiceWaiver w) =>
        new(w.Id, w.SourceRef, w.WaivedBy, w.Reason, w.WaivedAt);
}

/// <summary>
/// Los servicios que se dieron sin cobrar en un rango de fechas. Es el reporte que el dueño va a
/// pedir el primer mes, y la razón por la que la excepción se registra en vez de solo permitirse.
/// </summary>
public sealed class ListServiceWaiversHandler(IServiceWaiverRepository waivers)
{
    public async Task<IReadOnlyList<ServiceWaiverDto>> HandleAsync(
        Guid orgId, DateOnly? since, DateOnly? until, CancellationToken ct = default)
    {
        var rows = await waivers.ListAsync(orgId, since, until, ct);
        return rows
            .Select(w => new ServiceWaiverDto(w.Id, w.SourceRef, w.WaivedBy, w.Reason, w.WaivedAt))
            .ToList();
    }
}
