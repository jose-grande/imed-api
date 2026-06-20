using IMed.Api.Domain.Catalogs.Lab;

namespace IMed.Api.Domain.Lab;

/// <summary>
/// Recepción/enrutamiento de una orden de laboratorio (Épica LIS-3). Punto de
/// entrada a la worklist e inicio del reloj de TAT (<see cref="ReceivedAt"/>,
/// LIS-DEC-06). Complementa al `ServiceRequest`/`Specimen` FHIR con el estado
/// operativo que FHIR no modela.
/// </summary>
public sealed class OrderIntake
{
    public Guid Id { get; private set; }
    public Guid OrganizationId { get; private set; }

    /// <summary>Orden FHIR (interna del Encounter o externa creada en recepción).</summary>
    public string ServiceRequestFhirId { get; private set; } = string.Empty;

    /// <summary>Muestra creada al recibir (null mientras Pending/Blocked).</summary>
    public string? SpecimenFhirId { get; private set; }

    public string PatientFhirId { get; private set; } = string.Empty;

    public OrderRouting Routing { get; private set; }
    public LabRequestPriority Priority { get; private set; } = LabRequestPriority.Routine;

    /// <summary>Toma agendada a futuro; null = inmediata.</summary>
    public DateTimeOffset? ScheduledFor { get; private set; }
    public bool IsWalkIn { get; private set; }

    public bool FastingRequired { get; private set; }
    /// <summary>Null = no aplica/no verificado; true/false = resultado de la verificación.</summary>
    public bool? FastingSatisfied { get; private set; }

    public string? AccessionNumber { get; private set; }

    public OrderIntakeStatus Status { get; private set; } = OrderIntakeStatus.Pending;
    public string? BlockReason { get; private set; }

    public Guid? ReceivedBy { get; private set; }
    /// <summary>Inicio del reloj de TAT.</summary>
    public DateTimeOffset? ReceivedAt { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    private OrderIntake() { }

    public static OrderIntake Create(
        Guid organizationId,
        string serviceRequestFhirId,
        string patientFhirId,
        OrderRouting routing,
        LabRequestPriority priority,
        bool fastingRequired,
        DateTimeOffset? scheduledFor = null,
        bool isWalkIn = false,
        TimeProvider? timeProvider = null)
    {
        if (organizationId == Guid.Empty)
        {
            throw new ArgumentException("OrganizationId es obligatorio.", nameof(organizationId));
        }
        if (string.IsNullOrWhiteSpace(serviceRequestFhirId))
        {
            throw new ArgumentException("ServiceRequestFhirId es obligatorio.", nameof(serviceRequestFhirId));
        }
        if (string.IsNullOrWhiteSpace(patientFhirId))
        {
            throw new ArgumentException("PatientFhirId es obligatorio.", nameof(patientFhirId));
        }
        var now = (timeProvider ?? TimeProvider.System).GetUtcNow();
        return new OrderIntake
        {
            Id = Guid.CreateVersion7(now),
            OrganizationId = organizationId,
            ServiceRequestFhirId = serviceRequestFhirId.Trim(),
            PatientFhirId = patientFhirId.Trim(),
            Routing = routing,
            Priority = priority,
            FastingRequired = fastingRequired,
            ScheduledFor = scheduledFor,
            IsWalkIn = isWalkIn,
            Status = OrderIntakeStatus.Pending,
            CreatedAt = now,
            UpdatedAt = now,
        };
    }

    /// <summary>Recepción exitosa: muestra creada + accession + arranca TAT.</summary>
    public void MarkReceived(string specimenFhirId, string accessionNumber, Guid receivedBy, bool? fastingSatisfied, TimeProvider? timeProvider = null)
    {
        if (string.IsNullOrWhiteSpace(specimenFhirId))
        {
            throw new ArgumentException("SpecimenFhirId es obligatorio.", nameof(specimenFhirId));
        }
        var now = (timeProvider ?? TimeProvider.System).GetUtcNow();
        SpecimenFhirId = specimenFhirId.Trim();
        AccessionNumber = accessionNumber.Trim();
        FastingSatisfied = fastingSatisfied;
        ReceivedBy = receivedBy;
        ReceivedAt = now;
        Status = OrderIntakeStatus.Received;
        BlockReason = null;
        UpdatedAt = now;
    }

    /// <summary>Bloqueo por precondición no cumplida (ayuno, etc.) — sin crear muestra.</summary>
    public void MarkBlocked(string reason, Guid by, TimeProvider? timeProvider = null)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new ArgumentException("BlockReason es obligatorio.", nameof(reason));
        }
        var now = (timeProvider ?? TimeProvider.System).GetUtcNow();
        Status = OrderIntakeStatus.Blocked;
        BlockReason = reason.Trim();
        FastingSatisfied = false;
        ReceivedBy = by;
        UpdatedAt = now;
    }

    public void MarkCompleted(TimeProvider? timeProvider = null)
    {
        Status = OrderIntakeStatus.Completed;
        UpdatedAt = (timeProvider ?? TimeProvider.System).GetUtcNow();
    }
}
