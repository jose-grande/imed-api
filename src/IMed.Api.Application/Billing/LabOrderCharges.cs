using IMed.Api.Application.Abstractions;
using IMed.Api.Application.LabReception;
using IMed.Api.Domain.Billing.Accounts;
using IMed.Api.Domain.Lab;
using Microsoft.Extensions.Logging;

namespace IMed.Api.Application.Billing;

/// <summary>
/// Traduce una orden del LIS a lo que la facturación necesita: el id de catálogo de su panel o de
/// su prueba. Se resuelve panel primero, igual que <see cref="LabRouting.ExpandOrderAsync"/>: un
/// código puede existir en las dos tablas, y si ganara la prueba se cobraría el analito suelto en
/// vez del panel entero.
/// </summary>
public static class LabOrderChargeDescriber
{
    public static async Task<CaptureLabOrderChargesHandler.LabOrderCharge> DescribeAsync(
        ILabCatalogRepository catalog, Guid orgId, LabOrderSnapshot o, CancellationToken ct)
    {
        Guid? panelId = null;
        Guid? testId = null;

        if (!string.IsNullOrWhiteSpace(o.CatalogCode))
        {
            var panel = await catalog.GetPanelByCodeOrLoincAsync(orgId, o.CatalogCode, ct);
            if (panel is not null)
            {
                panelId = panel.Id;
            }
            else
            {
                testId = (await catalog.GetTestByCodeOrLoincAsync(orgId, o.CatalogCode, ct))?.Id;
            }
        }

        return new CaptureLabOrderChargesHandler.LabOrderCharge(
            o.Id, testId, panelId,
            o.CodeText ?? o.CodeDisplay ?? o.CatalogCode ?? "Estudio de laboratorio",
            o.PatientFhirId, o.EncounterFhirId, o.Requisition);
    }
}

/// <summary>Evento outbox que pide reconciliar el cargo de una orden de laboratorio (A5).</summary>
public static class LabOrderBillingSyncEvent
{
    public const string OutboxEventType = "lab-order.billing-sync";

    public sealed record Payload(Guid OrganizationId, string ServiceRequestId);
}

/// <summary>
/// Mantiene el cargo de una orden de laboratorio de una consulta (D5, A5): se cobra <b>al
/// ordenarla</b> —cuando el médico la emite y pasa a activa—, en la cuenta de la consulta y en la
/// sede de la consulta. El paciente puede pagar antes de llegar al laboratorio.
///
/// <list type="bullet">
///   <item>Borrador: todavía no es una orden; nada que cobrar.</item>
///   <item>Activa, en pausa o completada: se asegura el cargo (idempotente).</item>
///   <item>Revocada o anulada: se reversa si no está facturado.</item>
/// </list>
///
/// <para><b>Las órdenes sin consulta</b> (el paciente llega con una orden externa) se siguen
/// cobrando al recibirlas en el laboratorio: es el primer momento en que la clínica sabe de ellas y
/// en qué sede se atienden. Tampoco se cobra acá lo que el laboratorio refiere afuera: lo paga el
/// paciente donde se lo hagan.</para>
/// </summary>
public sealed class LabOrderChargeReconciler(
    IHapiFhirClient hapi,
    ILabCatalogRepository catalog,
    IBillingAccountRepository accounts,
    CaptureLabOrderChargesHandler capture,
    ILogger<LabOrderChargeReconciler> logger)
{
    private static readonly HashSet<string> Cobrables = ["active", "on-hold", "completed"];
    private static readonly HashSet<string> Anuladas = ["revoked", "entered-in-error"];

    public async Task<string> ReconcileAsync(Guid orgId, string serviceRequestId, CancellationToken ct = default)
    {
        var particion = $"org-{orgId}";
        var orden = await hapi.GetLabOrderAsync(serviceRequestId, particion, ct);
        if (orden is null)
        {
            return "inexistente";
        }

        if (Anuladas.Contains(orden.Status))
        {
            var vivos = await accounts.ListLiveChargesBySourceForUpdateAsync(
                orgId, ChargeSourceKind.LabOrder, serviceRequestId, ct);
            if (vivos.Any(c => c.Status == ChargeStatus.Invoiced))
            {
                // Lo facturado se revierte con nota de crédito: lo decide una persona.
                logger.LogWarning("Orden {Order} anulada con el cargo facturado: requiere nota de crédito.", serviceRequestId);
                return "facturada";
            }
            foreach (var c in vivos)
            {
                c.Reverse("Orden de laboratorio anulada");
                await accounts.UpdateChargeAsync(c, ct);
            }
            return vivos.Count > 0 ? "reversada" : "sin-cargo";
        }

        if (!Cobrables.Contains(orden.Status) || string.IsNullOrWhiteSpace(orden.EncounterFhirId))
        {
            return "no-cobra-aun";
        }

        var pruebas = await LabRouting.ExpandOrderAsync(catalog, orgId, orden.CatalogCode, ct);
        if (pruebas.Count == 0 || !pruebas.Any(t => t.Routing == OrderRouting.InHouse))
        {
            return "referida";
        }

        var encuentro = await hapi.ReadResourceAsync("Encounter", orden.EncounterFhirId, particion, ct);
        var sede = encuentro is null ? null : EncounterChargeReconciler.SedeDe(encuentro);
        if (sede is null)
        {
            logger.LogWarning(
                "Orden {Order}: su consulta {Encounter} no tiene sede; se cobrará al recibirla en el laboratorio.",
                serviceRequestId, orden.EncounterFhirId);
            return "sin-sede";
        }

        var cargo = await LabOrderChargeDescriber.DescribeAsync(catalog, orgId, orden, ct);
        var r = await capture.HandleAsync(orgId, [cargo], sede, ct);
        return r.Captured > 0 ? "capturada" : r.AlreadyCharged > 0 ? "ya-cobrada" : "sin-cobrable";
    }
}
