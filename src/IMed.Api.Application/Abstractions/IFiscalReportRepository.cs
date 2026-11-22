using IMed.Api.Domain.Billing.Accounts;
using IMed.Api.Domain.Billing.Invoicing;

namespace IMed.Api.Application.Abstractions;

/// <summary>
/// Lecturas de reporte (FAC-8). Van aparte de los repositorios de escritura porque son barridos
/// por rango de fecha: mezclarlas con las consultas del camino caliente esconde que estas pueden
/// devolver miles de filas y necesitan sus propios índices.
/// </summary>
public interface IFiscalReportRepository
{
    /// <summary>Documentos fiscales emitidos en un rango, anulados incluidos.</summary>
    Task<IReadOnlyList<FiscalDocument>> ListDocumentsAsync(
        Guid organizationId, DateOnly since, DateOnly until, CancellationToken ct = default);

    /// <summary>Documentos de una autorización, para el libro y la detección de huecos.</summary>
    Task<IReadOnlyList<FiscalDocument>> ListByAuthorizationAsync(
        Guid organizationId, Guid authorizationId, CancellationToken ct = default);

    /// <summary>Cargos tecleados a mano: la excepción del sistema.</summary>
    Task<IReadOnlyList<BillingCharge>> ListManualChargesAsync(
        Guid organizationId, DateOnly since, DateOnly until, CancellationToken ct = default);

    /// <summary>Cargos con descuento que alguien tuvo que autorizar.</summary>
    Task<IReadOnlyList<BillingCharge>> ListAuthorizedDiscountsAsync(
        Guid organizationId, DateOnly since, DateOnly until, CancellationToken ct = default);

    /// <summary>
    /// Consultas cobradas por adelantado (cargo con origen cita) que siguen vivas: ni reversadas ni
    /// con nota de crédito. Pagadas antes del fin del rango y con la cita desde 90 días antes de su
    /// inicio. Si la cita se canceló, ese dinero quedó en una cita que ya no se va a atender.
    /// </summary>
    Task<IReadOnlyList<BillingCharge>> ListLivePrepaidConsultationsAsync(
        Guid organizationId, DateOnly since, DateOnly until, CancellationToken ct = default);
}
