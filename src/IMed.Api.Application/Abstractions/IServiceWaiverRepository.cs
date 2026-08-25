using IMed.Api.Domain.Billing.Gate;

namespace IMed.Api.Application.Abstractions;

/// <summary>Constancias de servicios liberados sin cobrar (FAC-7).</summary>
public interface IServiceWaiverRepository
{
    /// <summary>La constancia de un acto, si alguien ya lo liberó.</summary>
    Task<ServiceWaiver?> FindAsync(
        Guid organizationId, string sourceRef, CancellationToken ct = default);

    /// <summary>
    /// Las liberaciones de un rango de fechas. Alimenta el reporte de excepciones — sin él, "¿a
    /// cuántos les dimos servicio sin cobrar?" no tiene respuesta.
    /// </summary>
    Task<IReadOnlyList<ServiceWaiver>> ListAsync(
        Guid organizationId, DateOnly? since, DateOnly? until, CancellationToken ct = default);

    Task AddAsync(ServiceWaiver waiver, CancellationToken ct = default);
}
