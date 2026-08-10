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

    /// <summary>Incremento atómico del contador diario de número de orden (F1); devuelve el nuevo `seq`.</summary>
    Task<int> NextOrderSeqAsync(Guid organizationId, DateOnly day, CancellationToken cancellationToken = default);

    /// <summary>Worklist: intakes in-house por org filtrados por estado (LIS-3 F3).</summary>
    Task<IReadOnlyList<OrderIntake>> ListInHouseIntakesAsync(Guid organizationId, OrderIntakeStatus? status, CancellationToken cancellationToken = default);

    // --- F2: check-in de orden (ingreso a la cola si el paciente lo solicita) ---

    /// <summary>Marca una orden como ingresada (idempotente). No falla si ya existe.</summary>
    Task CheckInOrderAsync(LabOrderCheckIn checkIn, CancellationToken cancellationToken = default);

    /// <summary>Conjunto de requisitions ingresadas (en cola) de la organización.</summary>
    Task<IReadOnlySet<string>> GetCheckedInRequisitionsAsync(Guid organizationId, CancellationToken cancellationToken = default);

    // --- R6-F2: plan de colección ---

    /// <summary>Plan de colección de una requisición (con contenedores + items). Null si no se calculó.</summary>
    Task<CollectionPlan?> GetCollectionPlanByRequisitionAsync(Guid organizationId, string requisition, CancellationToken cancellationToken = default);

    /// <summary>Persiste el plan reemplazando cualquier plan previo de la misma requisición (recalcular = idempotente).</summary>
    Task UpsertCollectionPlanAsync(CollectionPlan plan, CancellationToken cancellationToken = default);

    // --- R6-F3/F4: ciclo de vida del contenedor + timeline ---

    /// <summary>Plan tracked (con contenedores+items) para mutar su ciclo de vida (etiquetas/estados).</summary>
    Task<CollectionPlan?> GetCollectionPlanForUpdateAsync(Guid organizationId, string requisition, CancellationToken cancellationToken = default);

    /// <summary>Contenedor tracked (scoped por org) para aplicar una transición.</summary>
    Task<CollectionPlanContainer?> GetContainerForUpdateAsync(Guid organizationId, Guid containerId, CancellationToken cancellationToken = default);

    /// <summary>Plan tracked (con contenedores+items) al que pertenece un contenedor — la etiqueta
    /// necesita datos del plan (paciente, requisición) partiendo del contenedor.</summary>
    Task<CollectionPlan?> GetCollectionPlanByContainerIdAsync(Guid organizationId, Guid containerId, CancellationToken cancellationToken = default);

    /// <summary>Persiste los eventos de timeline + cualquier cambio tracked (contenedores) en una sola operación.</summary>
    Task SaveCollectionEventsAsync(IEnumerable<CollectionEvent> events, CancellationToken cancellationToken = default);

    /// <summary>Timeline de cadena de custodia de un contenedor (orden cronológico).</summary>
    Task<IReadOnlyList<CollectionEvent>> ListCollectionEventsAsync(Guid containerId, CancellationToken cancellationToken = default);

    /// <summary>Fase 3: contenedores en ciertos estados, con su requisición y paciente (worklist de recepción técnica).</summary>
    Task<IReadOnlyList<TechnicalReceptionRow>> ListContainersForTechnicalReceptionAsync(
        Guid organizationId, IReadOnlyList<ContainerStatus> statuses, CancellationToken cancellationToken = default);

    /// <summary>
    /// Fase 3 (rediseño): <b>todos</b> los contenedores de los planes que tienen al menos uno
    /// esperando bench — incluidos los ya recibidos. La worklist se agrupa por orden, así que
    /// el técnico necesita ver el juego completo ("1 de 3") y notar si le falta un tubo.
    /// </summary>
    Task<IReadOnlyList<TechnicalReceptionRow>> ListSiblingContainersForTechnicalReceptionAsync(
        Guid organizationId, IReadOnlyList<ContainerStatus> pendingStatuses, CancellationToken cancellationToken = default);

    /// <summary>Resuelve un contenedor por el código escaneado (barcode o accession). Null si no existe en la org.</summary>
    Task<TechnicalReceptionRow?> FindContainerByBarcodeAsync(
        Guid organizationId, string code, CancellationToken cancellationToken = default);

    /// <summary>Subconjunto de contenedores que ya registraron un evento del tipo dado (lookup en lote, sin N+1).</summary>
    Task<IReadOnlySet<Guid>> ListContainersWithEventAsync(
        IReadOnlyList<Guid> containerIds, CollectionEventType type, CancellationToken cancellationToken = default);
}

/// <summary>Un contenedor con los datos de su plan (requisición + paciente) para la recepción técnica.</summary>
public sealed record TechnicalReceptionRow(CollectionPlanContainer Container, string Requisition, string PatientFhirId);
