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
    IBillingAccountRepository accounts,
    IPartyRepository parties,
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
        string? EncounterFhirId);

    public async Task<LabChargeSummary> HandleAsync(
        Guid orgId, IReadOnlyList<LabOrderCharge> orders, CancellationToken ct = default)
    {
        var capturados = 0;
        var yaCobrados = 0;
        var sinPrecio = new List<string>();

        foreach (var orden in orders)
        {
            try
            {
                var cobrable = await catalog.FindByLabAsync(orgId, orden.LabTestId, orden.LabPanelId, ct);
                if (cobrable is null)
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
                    orgId, cuenta.Value, cobrable.Id,
                    ChargeSourceKind.LabOrder, orden.ServiceRequestId,
                    SourceFhirRef: $"ServiceRequest/{orden.ServiceRequestId}"), ct);

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
    /// La cuenta del encuentro, o la cuenta corriente del paciente si la orden es ambulatoria.
    /// Null si no se puede resolver ninguna.
    /// </summary>
    private async Task<Guid?> ResolverCuentaAsync(
        Guid orgId, LabOrderCharge orden, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(orden.EncounterFhirId))
        {
            var delEncuentro = await accounts.FindByEncounterAsync(orgId, orden.EncounterFhirId, ct);
            if (delEncuentro is not null)
            {
                return delEncuentro.AcceptsCharges ? delEncuentro.Id : null;
            }
        }

        // Orden externa o sin encuentro: la cuenta corriente abierta del paciente.
        if (string.IsNullOrWhiteSpace(orden.PatientFhirId))
        {
            return null;
        }

        var parte = await parties.FindByPatientAsync(orgId, orden.PatientFhirId, ct);
        if (parte is null)
        {
            return null;
        }

        var abiertas = await accounts.SearchAccountsAsync(
            orgId, orden.PatientFhirId, nameof(BillingAccountStatus.Open), 5, ct);
        return abiertas.Count > 0 ? abiertas[0].Id : null;
    }
}
