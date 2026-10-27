using IMed.Api.Application.Abstractions;
using IMed.Api.Domain.Billing.Accounts;
using Microsoft.Extensions.Logging;

namespace IMed.Api.Application.Billing;

/// <summary>
/// Cuántos cargos entraron al admitir una orden, y cuántos no encontraron cobrable.
/// </summary>
/// <param name="Unpriced">
/// Órdenes cuyo examen no está enlazado a ningún cobrable. No es un error del laboratorio: es una
/// deuda del catálogo, y hay que poder verla en vez de que el examen se haga gratis en silencio.
/// </param>
public sealed record LabChargeSummary(
    int Captured, int AlreadyCharged, IReadOnlyList<string> Unpriced);

/// <summary>
/// Captura los cargos de las órdenes de laboratorio de una requisición (FAC-4, origen
/// <c>lab_order</c>).
///
/// <para><b>Nunca bloquea al laboratorio.</b> Es la costura 4 del modelo: si la facturación falla
/// —no hay cuenta, no hay cobrable, la base no responde—, la orden se admite igual y el paciente
/// se atiende. Un laboratorio que no puede recibir una muestra porque el módulo de facturación
/// tuvo un problema es peor que un cargo que hay que agregar a mano después.</para>
/// </summary>
public sealed class CaptureLabOrderChargesHandler(
    IBillingCatalogRepository catalog,
    IBillableItemProvisioner provisioner,
    IEncounterAccountResolver accountResolver,
    CaptureChargeHandler capture,
    ILogger<CaptureLabOrderChargesHandler> logger)
{
    /// <summary>Una orden a cobrar: su id, su código de catálogo y a quién pertenece.</summary>
    public sealed record LabOrderCharge(
        string ServiceRequestId,
        Guid? LabTestId,
        Guid? LabPanelId,
        string Display,
        string PatientFhirId,
        string? EncounterFhirId,
        string? Requisition = null);

    /// <param name="locationId">Sede donde se admitieron las órdenes: ahí se presta el servicio (FAC-9).</param>
    public async Task<LabChargeSummary> HandleAsync(
        Guid orgId, IReadOnlyList<LabOrderCharge> orders, Guid? locationId = null, CancellationToken ct = default)
    {
        var capturados = 0;
        var yaCobrados = 0;
        var sinPrecio = new List<string>();

        foreach (var orden in orders)
        {
            try
            {
                var cobrableId = await CobrableAsync(orgId, orden, ct);
                if (cobrableId is null)
                {
                    sinPrecio.Add(orden.Display);
                    continue;
                }

                var cuenta = await ResolverCuentaAsync(orgId, orden, ct);
                if (cuenta is null)
                {
                    sinPrecio.Add(orden.Display);
                    continue;
                }

                var r = await capture.HandleAsync(new CaptureChargeCommand(
                    orgId, cuenta.Value, cobrableId.Value,
                    ChargeSourceKind.LabOrder, orden.ServiceRequestId,
                    SourceFhirRef: $"ServiceRequest/{orden.ServiceRequestId}",
                    LocationId: locationId), ct);

                if (r.AlreadyCharged)
                {
                    yaCobrados++;
                }
                else if (r.Succeeded)
                {
                    capturados++;
                }
                else
                {
                    logger.LogWarning(
                        "No se pudo cobrar la orden {ServiceRequest}: {Motivo}",
                        orden.ServiceRequestId, r.Rejected);
                    sinPrecio.Add(orden.Display);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // La admisión de la orden ya ocurrió y no se deshace por esto.
                logger.LogError(ex,
                    "Falló la captura del cargo de la orden {ServiceRequest}. La orden queda admitida.",
                    orden.ServiceRequestId);
                sinPrecio.Add(orden.Display);
            }
        }

        return new LabChargeSummary(capturados, yaCobrados, sinPrecio);
    }

    /// <summary>
    /// El cobrable del examen. Si la prueba o el panel no tiene uno —se creó antes del alta
    /// automática—, se crea ahora sin precio (D1): el examen se cobra cuando alguien fije el
    /// precio, en vez de hacerse gratis en silencio. Null solo si no hay régimen fiscal o el
    /// código no corresponde a nada del catálogo.
    /// </summary>
    private async Task<Guid?> CobrableAsync(Guid orgId, LabOrderCharge orden, CancellationToken ct)
    {
        if (await catalog.FindByLabAsync(orgId, orden.LabTestId, orden.LabPanelId, ct) is { } activo)
        {
            return activo.Id;
        }
        var nuevo = orden.LabPanelId is { } panel
            ? await provisioner.EnsureForLabPanelAsync(orgId, panel, ct)
            : orden.LabTestId is { } prueba
                ? await provisioner.EnsureForLabTestAsync(orgId, prueba, ct)
                : null;
        // Un cobrable que alguien desactivó no se reactiva solo: desactivar es una decisión.
        return nuevo is { Active: true } ? nuevo.Id : null;
    }

    /// <summary>
    /// La cuenta de la orden (D2): la de la consulta si salió de una, o una propia por orden
    /// —agrupada por requisición— si el paciente llegó con una orden externa.
    /// </summary>
    private Task<Guid?> ResolverCuentaAsync(Guid orgId, LabOrderCharge orden, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(orden.PatientFhirId))
        {
            return Task.FromResult<Guid?>(null);
        }
        if (!string.IsNullOrWhiteSpace(orden.EncounterFhirId))
        {
            return accountResolver.GetOrOpenForEncounterAsync(
                orgId, orden.EncounterFhirId, orden.PatientFhirId, null, ct);
        }
        return accountResolver.GetOrOpenStandaloneAsync(
            orgId, BillingAccountOrigin.LabOrder,
            string.IsNullOrWhiteSpace(orden.Requisition) ? orden.ServiceRequestId : orden.Requisition,
            orden.PatientFhirId, null, ct);
    }
}
