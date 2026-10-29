using IMed.Api.Application.Abstractions;
using IMed.Api.Application.Locations;
using IMed.Api.Domain.Catalogs.Lab;
using IMed.Api.Domain.Lab;

namespace IMed.Api.Application.LabReception;

/// <summary>
/// Rediseño por estaciones — Fase 3 (Recepción técnica). Worklist de contenedores
/// que **llegan físicamente al bench** para QC: los **tomados on-site** por el
/// flebotomista (estado <c>Collected</c>) y los que **trae el paciente ya rotulados**
/// (orina/heces = <c>PatientCollected</c>, aún etiquetados, no "tomados"). El QC
/// (aceptar → <c>Received</c> / rechazar con motivo del catálogo) reusa el endpoint
/// <c>qc</c> de R6-F6. Acá **no se etiqueta** (el rótulo se aplica upstream).
///
/// <para><b>Agrupado por orden</b> (rediseño de UX): el material llega al bench en gradilla
/// por paciente, no como tubos sueltos. Devolver contenedores planos obligaba al técnico a
/// reconciliar mentalmente qué tubos eran del mismo paciente. Cada orden trae **todos** sus
/// contenedores —incluidos los ya recibidos— para que se vea "1 de 3" y se note si falta uno.</para>
/// </summary>
public sealed class GetTechnicalReceptionHandler(
    IHapiFhirClient hapi, ILabCatalogRepository catalog, ILabOperationsRepository ops,
    ResolveActiveLocationHandler activeLocation)
{
    private static readonly ContainerStatus[] Candidate =
        [ContainerStatus.Collected, ContainerStatus.LabelGenerated, ContainerStatus.LabelPrinted];

    /// <summary>
    /// Worklist del bench de la sede activa (FAC-9): los tubos que se tomaron en esta sucursal.
    /// El escaneo no se filtra: un tubo en la mano se resuelve venga de donde venga.
    /// </summary>
    public async Task<IReadOnlyList<TechnicalReceptionOrderDto>> HandleAsync(Guid orgId, Guid? userId, CancellationToken ct = default)
    {
        var sede = await activeLocation.ResolveScopeAsync(orgId, userId, ct);
        var rows = (await ops.ListSiblingContainersForTechnicalReceptionAsync(orgId, Candidate, ct))
            .Where(r => sede.Includes(r.LocationId))
            .ToList();
        if (rows.Count == 0)
        {
            return [];
        }

        var context = await LoadContextAsync(orgId, rows, ct);

        var orders = new List<TechnicalReceptionOrderDto>();
        foreach (var group in rows.GroupBy(r => r.Requisition))
        {
            var patientFhirId = group.First().PatientFhirId;
            var patient = context.Patients.GetValueOrDefault(patientFhirId);

            var samples = group
                .Select(r => BuildSample(r.Container, context))
                .OrderBy(s => s.Sequence)
                .ToList();

            // Una orden entra a la worklist solo si le queda algo por recibir.
            if (!samples.Any(s => s.Pending))
            {
                continue;
            }

            orders.Add(new TechnicalReceptionOrderDto(
                Requisition: group.Key,
                PatientFhirId: patientFhirId,
                PatientName: patient?.DisplayName,
                PatientBirthDate: patient?.BirthDate,
                Samples: samples));
        }

        // La más vieja primero: es la que corre riesgo de salirse de estabilidad.
        return orders
            .OrderBy(o => o.Samples.Where(s => s.Pending).Min(s => s.CollectedAt ?? DateTimeOffset.MaxValue))
            .ToList();
    }

    /// <summary>
    /// Resuelve el código disparado por el lector (o tecleado) a una muestra concreta y dice
    /// **qué se puede hacer con ella**. Cada desenlace tiene su propia respuesta: el peor
    /// resultado posible de un escaneo es que no pase nada.
    /// </summary>
    public async Task<ScanResultDto> ScanAsync(Guid orgId, string code, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            return ScanResultDto.NotFound(code);
        }

        var row = await ops.FindContainerByBarcodeAsync(orgId, code, ct);
        if (row is null)
        {
            return ScanResultDto.NotFound(code);
        }

        var context = await LoadContextAsync(orgId, [row], ct);
        var patient = context.Patients.GetValueOrDefault(row.PatientFhirId);
        var sample = BuildSample(row.Container, context);

        var order = new TechnicalReceptionOrderDto(
            row.Requisition, row.PatientFhirId, patient?.DisplayName, patient?.BirthDate, [sample]);

        var outcome = row.Container.Status switch
        {
            ContainerStatus.Received => ScanOutcome.AlreadyReceived,
            ContainerStatus.Stored => ScanOutcome.AlreadyReceived,
            ContainerStatus.Rejected => ScanOutcome.Rejected,
            _ when sample.Pending => ScanOutcome.Ready,
            // Etiquetada pero todavía no tomada, y no es de las que trae el paciente.
            _ => ScanOutcome.NotCollected,
        };

        return new ScanResultDto(outcome, code.Trim(), order, sample);
    }

    // ------------------------------------------------------------------

    /// <summary>Catálogos + pacientes + identidad verificada, resueltos en lote (sin N+1).</summary>
    private async Task<ReceptionContext> LoadContextAsync(
        Guid orgId, IReadOnlyList<TechnicalReceptionRow> rows, CancellationToken ct)
    {
        var modeByCode = (await catalog.ListSpecimenTypesAsync(orgId, ct))
            .ToDictionary(s => s.Code, s => s.CollectionMode);

        // El color de tapa es la ayuda visual que permite emparejar pantalla y mano sin leer
        // el accession. Vive en el catálogo de tubos, no en el contenedor del plan.
        var colorByTubeCode = (await catalog.ListTubeTypesAsync(orgId, ct))
            .Where(t => t.Code is not null)
            .ToDictionary(t => t.Code, t => t.Color, StringComparer.OrdinalIgnoreCase);

        var containerIds = rows.Select(r => r.Container.Id).ToList();
        var identityVerified = await ops.ListContainersWithEventAsync(
            containerIds, CollectionEventType.IdentityVerified, ct);

        var partition = LabRouting.PartitionOf(orgId);
        var patients = new Dictionary<string, PatientSnapshot?>(StringComparer.Ordinal);
        foreach (var patientFhirId in rows.Select(r => r.PatientFhirId).Distinct(StringComparer.Ordinal))
        {
            patients[patientFhirId] = await SafeGetPatientAsync(patientFhirId, partition, ct);
        }

        return new ReceptionContext(modeByCode, colorByTubeCode, identityVerified, patients);
    }

    private async Task<PatientSnapshot?> SafeGetPatientAsync(string id, string partition, CancellationToken ct)
    {
        try
        {
            return await hapi.GetPatientAsync(id, partition, ct);
        }
        catch
        {
            // Un blip de HAPI no debe vaciar la worklist: la muestra igual se identifica por
            // accession. Sin nombre el técnico no puede cotejar, pero ve que la muestra existe.
            return null;
        }
    }

    private static TechnicalReceptionSampleDto BuildSample(CollectionPlanContainer c, ReceptionContext ctx)
    {
        var mode = ctx.ModeBySpecimenCode.TryGetValue(c.SpecimenTypeCode, out var m) ? m : CollectionMode.OnSite;
        var patientBrought = mode == CollectionMode.PatientCollected;

        // On-site: entra al bench cuando está tomada (Collected).
        // Traída por paciente: entra cuando está etiquetada (llega rotulada), no pasa por toma.
        var pending = c.Status == ContainerStatus.Collected
            || (patientBrought && c.Status is ContainerStatus.LabelGenerated or ContainerStatus.LabelPrinted);

        var tubeColor = c.TubeTypeCode is { } code && ctx.ColorByTubeCode.TryGetValue(code, out var color)
            ? color
            : null;

        var identityVerified = ctx.IdentityVerified.Contains(c.Id);

        return new TechnicalReceptionSampleDto(
            ContainerId: c.Id,
            Sequence: c.Sequence,
            AccessionNumber: c.AccessionNumber,
            Barcode: c.Barcode,
            SpecimenTypeName: c.SpecimenTypeName,
            TubeTypeName: c.TubeTypeName,
            TubeColor: tubeColor,
            Additive: c.Additive,
            Status: c.Status.ToString(),
            Pending: pending,
            PatientBrought: patientBrought,
            IdentityVerified: identityVerified,
            // Las que trae el paciente nunca pasaron por la toma, así que la identificación
            // positiva no ocurrió en ningún punto anterior: el bench es el único lugar donde
            // todavía se puede cotejar antes de que la muestra entre a análisis.
            RequiresIdentityCheck: patientBrought && !identityVerified,
            CollectedAt: c.CollectedAt,
            ReceivedAt: c.ReceivedAt,
            RejectionReasonCode: c.RejectionReasonCode,
            Tests: c.Items.Select(i => i.LabTestName ?? i.LabTestCode).ToList());
    }

    private sealed record ReceptionContext(
        Dictionary<string, CollectionMode> ModeBySpecimenCode,
        Dictionary<string, string?> ColorByTubeCode,
        IReadOnlySet<Guid> IdentityVerified,
        Dictionary<string, PatientSnapshot?> Patients);
}

/// <summary>Una orden con todas sus muestras, tal como llega la gradilla al bench (Fase 3).</summary>
public sealed record TechnicalReceptionOrderDto(
    string Requisition,
    string PatientFhirId,
    string? PatientName,
    string? PatientBirthDate,
    IReadOnlyList<TechnicalReceptionSampleDto> Samples);

/// <summary>Una muestra física dentro de una orden (Fase 3).</summary>
public sealed record TechnicalReceptionSampleDto(
    Guid ContainerId,
    int Sequence,
    string? AccessionNumber,
    string? Barcode,
    string? SpecimenTypeName,
    string? TubeTypeName,
    /// <summary>Color de tapa del tubo ("lila", "dorado"…) — permite emparejar pantalla y mano.</summary>
    string? TubeColor,
    string? Additive,
    string Status,
    /// <summary>Sigue esperando QC. Las ya recibidas viajan igual, para mostrar "1 de 3".</summary>
    bool Pending,
    bool PatientBrought,
    bool IdentityVerified,
    /// <summary>Exige cotejar nombre + fecha de nacimiento antes de aceptar.</summary>
    bool RequiresIdentityCheck,
    DateTimeOffset? CollectedAt,
    DateTimeOffset? ReceivedAt,
    string? RejectionReasonCode,
    IReadOnlyList<string> Tests);

/// <summary>Qué se puede hacer con la muestra que se acaba de escanear.</summary>
public enum ScanOutcome
{
    /// <summary>Lista para QC.</summary>
    Ready = 0,

    /// <summary>Ya fue recibida (o almacenada) — evita procesarla dos veces.</summary>
    AlreadyReceived = 1,

    /// <summary>Rechazada en un QC anterior.</summary>
    Rejected = 2,

    /// <summary>Existe pero todavía no se tomó la muestra: pertenece a la estación de toma.</summary>
    NotCollected = 3,

    /// <summary>El código no corresponde a ninguna muestra de este laboratorio.</summary>
    NotFound = 4,
}

/// <summary>Resultado de escanear un código en el bench.</summary>
public sealed record ScanResultDto(
    ScanOutcome Outcome,
    string ScannedCode,
    TechnicalReceptionOrderDto? Order,
    TechnicalReceptionSampleDto? Sample)
{
    public static ScanResultDto NotFound(string code) =>
        new(ScanOutcome.NotFound, code?.Trim() ?? string.Empty, null, null);
}
