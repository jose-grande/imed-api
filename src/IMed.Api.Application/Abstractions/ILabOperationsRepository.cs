using IMed.Api.Domain.Lab;

namespace IMed.Api.Application.Abstractions;

/// <summary>
/// Repositorio de la operación del laboratorio (Épica LIS-3): recepción de
/// órdenes (`order_intake`), eventos de muestra (`specimen_event`) y contador de
/// accession.
/// </summary>
public interface ILabOperationsRepository
{
    Task<OrderIntake?> GetIntakeByServiceRequestAsync(Guid organizationId, string serviceRequestFhirId, CancellationToken cancellationToken = default);
    Task<OrderIntake?> GetIntakeByIdAsync(Guid id, CancellationToken cancellationToken = default);
    Task AddIntakeAsync(OrderIntake intake, CancellationToken cancellationToken = default);
    Task UpdateIntakeAsync(OrderIntake intake, CancellationToken cancellationToken = default);
    Task AddSpecimenEventAsync(SpecimenEvent specimenEvent, CancellationToken cancellationToken = default);

    /// <summary>Incremento atómico del contador diario; devuelve el nuevo `seq` (L3-D5).</summary>
    Task<int> NextAccessionSeqAsync(Guid organizationId, DateOnly day, CancellationToken cancellationToken = default);

    /// <summary>Worklist: intakes in-house por org filtrados por estado (LIS-3 F3).</summary>
    Task<IReadOnlyList<OrderIntake>> ListInHouseIntakesAsync(Guid organizationId, OrderIntakeStatus? status, CancellationToken cancellationToken = default);
}
