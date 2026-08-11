namespace IMed.Api.Domain.Lab;

/// <summary>Estado del plan de colección (R6-F2). Evoluciona en fases posteriores
/// (etiqueta generada/impresa, etc.); por ahora solo se calcula.</summary>
public enum CollectionPlanStatus
{
    Calculated = 0,
}

/// <summary>Ciclo de vida de un contenedor/muestra física (R6-F3). Estados visibles
/// del workbench: del plan calculado hasta el almacenamiento, con rechazo lateral.</summary>
public enum ContainerStatus
{
    PlanCalculated = 0,
    LabelGenerated = 1,
    LabelPrinted = 2,
    CollectionStarted = 3,
    Collected = 4,
    Received = 5,
    Rejected = 6,
    Stored = 7,
}

/// <summary>
/// Plan de colección de una requisición (R6-F2, decisión UX §8.9/§8.10). Deriva de
/// la orden los **contenedores físicos** a tomar: tipo de muestra, tubo, aditivo y
/// volumen, consolidando pruebas compatibles por (tipo, aditivo) hasta la capacidad
/// del tubo. Es la unidad operativa que el workbench (R6-F5) muestra antes de
/// etiquetar (R6-F4). Agregado: plan → contenedores → items.
/// </summary>
public sealed class CollectionPlan
{
    public Guid Id { get; private set; }
    public Guid OrganizationId { get; private set; }

    /// <summary>
    /// Sede donde se toma la muestra (ADR-008). Nullable mientras conviva con planes anteriores
    /// al eje de sede, que el backfill de arranque ancla a la sede principal.
    /// </summary>
    public Guid? LocationId { get; private set; }

    /// <summary>Número de orden/requisición que el plan deriva (clave funcional).</summary>
    public string Requisition { get; private set; } = string.Empty;

    public string? EncounterFhirId { get; private set; }
    public string PatientFhirId { get; private set; } = string.Empty;

    public CollectionPlanStatus Status { get; private set; } = CollectionPlanStatus.Calculated;

    /// <summary>Usuario (técnico) que calculó el plan. Best-effort.</summary>
    public Guid? ComputedBy { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    private readonly List<CollectionPlanContainer> _containers = [];
    public IReadOnlyList<CollectionPlanContainer> Containers => _containers;

    private readonly List<CollectionPlanUnavailableItem> _unavailableItems = [];

    /// <summary>
    /// Pruebas/paneles de la orden que NO se pudieron planificar porque no están en
    /// el catálogo del tenant (copy-on-create, rev 53): sin metadata de muestra/tubo
    /// no se deriva contenedor. Se conservan para no perderlas en silencio — el
    /// workbench las muestra para referirlas o agregarlas al catálogo y recalcular.
    /// </summary>
    public IReadOnlyList<CollectionPlanUnavailableItem> UnavailableItems => _unavailableItems;

    private CollectionPlan() { }

    public static CollectionPlan Create(
        Guid organizationId,
        string requisition,
        string patientFhirId,
        string? encounterFhirId = null,
        Guid? computedBy = null,
        Guid? locationId = null,
        TimeProvider? timeProvider = null)
    {
        if (string.IsNullOrWhiteSpace(requisition))
        {
            throw new ArgumentException("Requisition es obligatoria.", nameof(requisition));
        }

        var now = (timeProvider ?? TimeProvider.System).GetUtcNow();
        return new CollectionPlan
        {
            Id = Guid.CreateVersion7(now),
            OrganizationId = organizationId,
            LocationId = locationId,
            Requisition = requisition.Trim(),
            PatientFhirId = patientFhirId,
            EncounterFhirId = string.IsNullOrWhiteSpace(encounterFhirId) ? null : encounterFhirId,
            Status = CollectionPlanStatus.Calculated,
            ComputedBy = computedBy,
            CreatedAt = now,
            UpdatedAt = now,
        };
    }

    /// <summary>Agrega un contenedor físico al plan (un tubo/recipiente). El volumen
    /// requerido arranca con el volumen muerto del tubo y crece por cada prueba.</summary>
    public CollectionPlanContainer AddContainer(
        int sequence,
        string specimenTypeCode,
        string? specimenTypeName,
        string? tubeTypeCode,
        string? tubeTypeName,
        string? additive,
        decimal deadVolumeMl,
        TimeProvider? timeProvider = null)
    {
        var container = CollectionPlanContainer.Create(
            Id, sequence, specimenTypeCode, specimenTypeName, tubeTypeCode, tubeTypeName, additive, deadVolumeMl, timeProvider);
        _containers.Add(container);
        return container;
    }

    /// <summary>Registra una prueba/panel ordenado que no está en el catálogo del tenant
    /// (no planificable). Dedupe por (serviceRequest, código) — no agrega duplicados.</summary>
    public CollectionPlanUnavailableItem? AddUnavailableItem(
        string serviceRequestFhirId, string? loincCode, string display, string reason)
    {
        var key = $"{serviceRequestFhirId}|{loincCode}";
        if (_unavailableItems.Any(i => $"{i.ServiceRequestFhirId}|{i.LoincCode}" == key))
        {
            return null;
        }
        var item = CollectionPlanUnavailableItem.Create(Id, serviceRequestFhirId, loincCode, display, reason);
        _unavailableItems.Add(item);
        return item;
    }
}

/// <summary>Prueba/panel de la orden que no se pudo planificar por no estar en el
/// catálogo del tenant (rev 54). No genera contenedor; se muestra para acción manual.</summary>
public sealed class CollectionPlanUnavailableItem
{
    public Guid Id { get; private set; }
    public Guid CollectionPlanId { get; private set; }
    public string ServiceRequestFhirId { get; private set; } = string.Empty;
    public string? LoincCode { get; private set; }
    public string Display { get; private set; } = string.Empty;

    /// <summary>Motivo (código corto): p. ej. <c>not-in-catalog</c>.</summary>
    public string Reason { get; private set; } = string.Empty;

    private CollectionPlanUnavailableItem() { }

    internal static CollectionPlanUnavailableItem Create(
        Guid collectionPlanId, string serviceRequestFhirId, string? loincCode, string display, string reason) => new()
    {
        Id = Guid.NewGuid(),
        CollectionPlanId = collectionPlanId,
        ServiceRequestFhirId = serviceRequestFhirId,
        LoincCode = string.IsNullOrWhiteSpace(loincCode) ? null : loincCode.Trim(),
        Display = string.IsNullOrWhiteSpace(display) ? "(sin descripción)" : display.Trim(),
        Reason = string.IsNullOrWhiteSpace(reason) ? "not-in-catalog" : reason.Trim(),
    };
}

/// <summary>Un contenedor/muestra física del plan (típicamente un tubo). Cubre N
/// pruebas compatibles (mismo tipo de muestra + aditivo) hasta su capacidad.</summary>
public sealed class CollectionPlanContainer
{
    public Guid Id { get; private set; }
    public Guid CollectionPlanId { get; private set; }

    /// <summary>Orden de extracción (order of draw) dentro del evento de toma.</summary>
    public int Sequence { get; private set; }

    public string SpecimenTypeCode { get; private set; } = string.Empty;
    public string? SpecimenTypeName { get; private set; }
    public string? TubeTypeCode { get; private set; }
    public string? TubeTypeName { get; private set; }
    public string? Additive { get; private set; }

    /// <summary>Volumen requerido = volumen muerto + Σ consumo de las pruebas que cubre.</summary>
    public decimal RequiredVolumeMl { get; private set; }

    // Accession/barcode (R6-F4) — se asignan al generar la etiqueta, antes de la toma.
    public string? AccessionNumber { get; private set; }
    public string? Barcode { get; private set; }
    // Materialización del Specimen FHIR (integración posterior con el flujo de recepción).
    public string? SpecimenFhirId { get; private set; }

    // Ciclo de vida (R6-F3).
    public ContainerStatus Status { get; private set; } = ContainerStatus.PlanCalculated;
    public DateTimeOffset? CollectedAt { get; private set; }
    public DateTimeOffset? ReceivedAt { get; private set; }
    public string? StorageLocation { get; private set; }
    public string? RejectionReasonCode { get; private set; }

    private readonly List<CollectionPlanItem> _items = [];
    public IReadOnlyList<CollectionPlanItem> Items => _items;

    private CollectionPlanContainer() { }

    internal static CollectionPlanContainer Create(
        Guid collectionPlanId,
        int sequence,
        string specimenTypeCode,
        string? specimenTypeName,
        string? tubeTypeCode,
        string? tubeTypeName,
        string? additive,
        decimal deadVolumeMl,
        TimeProvider? timeProvider = null)
    {
        var now = (timeProvider ?? TimeProvider.System).GetUtcNow();
        return new CollectionPlanContainer
        {
            Id = Guid.CreateVersion7(now),
            CollectionPlanId = collectionPlanId,
            Sequence = sequence,
            SpecimenTypeCode = specimenTypeCode,
            SpecimenTypeName = specimenTypeName,
            TubeTypeCode = tubeTypeCode,
            TubeTypeName = tubeTypeName,
            Additive = additive,
            RequiredVolumeMl = deadVolumeMl,
        };
    }

    /// <summary>Asigna una prueba a este contenedor y acumula su volumen.</summary>
    public CollectionPlanItem AddItem(string serviceRequestFhirId, string labTestCode, string? labTestName, decimal volumeMl)
    {
        var item = CollectionPlanItem.Create(Id, serviceRequestFhirId, labTestCode, labTestName);
        _items.Add(item);
        RequiredVolumeMl += volumeMl;
        return item;
    }

    // ── Ciclo de vida (R6-F3/F4) ───────────────────────────────────────────
    // No se enforcan transiciones estrictas: el workbench solo ofrece la "próxima
    // acción segura" por estado. Reglas de orden (order of draw, QC) viven en la UI.

    /// <summary>R6-F4: asigna accession + barcode (etiqueta generada, antes de la toma).
    /// Reimprimir/regenerar conserva el estado actual y solo refresca los datos.</summary>
    public void AssignLabel(string accessionNumber, string barcode)
    {
        AccessionNumber = accessionNumber;
        Barcode = barcode;
        if (Status == ContainerStatus.PlanCalculated)
        {
            Status = ContainerStatus.LabelGenerated;
        }
    }

    public void MarkPrinted() => Status = ContainerStatus.LabelPrinted;
    public void StartCollection() => Status = ContainerStatus.CollectionStarted;

    public void MarkCollected(DateTimeOffset collectedAt)
    {
        CollectedAt = collectedAt;
        Status = ContainerStatus.Collected;
    }

    public void MarkReceived(DateTimeOffset receivedAt)
    {
        ReceivedAt = receivedAt;
        Status = ContainerStatus.Received;
    }

    public void Reject(string reasonCode)
    {
        RejectionReasonCode = reasonCode;
        Status = ContainerStatus.Rejected;
    }

    public void Store(string? location)
    {
        StorageLocation = location;
        Status = ContainerStatus.Stored;
    }

    public void SetSpecimenFhirId(string specimenFhirId) => SpecimenFhirId = specimenFhirId;
}

/// <summary>Puente contenedor↔prueba: qué resultable (y su orden de origen) cubre el contenedor.</summary>
public sealed class CollectionPlanItem
{
    public Guid Id { get; private set; }
    public Guid CollectionPlanContainerId { get; private set; }
    public string ServiceRequestFhirId { get; private set; } = string.Empty;
    public string LabTestCode { get; private set; } = string.Empty;
    public string? LabTestName { get; private set; }

    private CollectionPlanItem() { }

    internal static CollectionPlanItem Create(
        Guid collectionPlanContainerId, string serviceRequestFhirId, string labTestCode, string? labTestName) => new()
    {
        Id = Guid.NewGuid(),
        CollectionPlanContainerId = collectionPlanContainerId,
        ServiceRequestFhirId = serviceRequestFhirId,
        LabTestCode = labTestCode,
        LabTestName = labTestName,
    };
}
