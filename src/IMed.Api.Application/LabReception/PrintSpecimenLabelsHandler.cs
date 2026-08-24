using IMed.Api.Application.Abstractions;
using IMed.Api.Domain.Catalogs.Lab;
using IMed.Api.Domain.Lab;

namespace IMed.Api.Application.LabReception;

/// <summary>Resultado de una impresión: el PDF y con qué nombre ofrecerlo.</summary>
public sealed record SpecimenLabelPdfResult(byte[] Bytes, string FileName);

/// <summary>
/// R6-F4b: imprime la(s) etiqueta(s) de un plan de colección.
///
/// <para>Cierra el hueco que hacía que "Imprimir etiqueta" no hiciera nada visible: hasta
/// ahora la acción <c>print</c> solo movía el estado del contenedor en la base y jamás
/// producía una etiqueta. Este handler genera el PDF real —con el Code128 y el tamaño
/// físico que el laboratorio configuró para ese tubo— y recién entonces registra el evento
/// de cadena de custodia.</para>
///
/// <para>Es <b>idempotente hacia adelante</b>: si algún contenedor todavía no tiene accession
/// se lo asigna primero (delegando en <see cref="GenerateLabelsHandler"/>), para que el
/// técnico no tenga que acordarse de pulsar "Generar etiquetas" antes de imprimir.</para>
///
/// <para>La reimpresión de una muestra que ya avanzó exige motivo (decisión §8 / D9) y se
/// registra como <see cref="CollectionEventType.LabelReprinted"/>: una etiqueta duplicada
/// circulando es un riesgo de identificación, así que queda auditada.</para>
/// </summary>
public sealed class PrintSpecimenLabelsHandler(
    ILabOperationsRepository ops,
    ILabCatalogRepository catalog,
    IHapiFhirClient hapi,
    ISpecimenLabelPdfRenderer renderer,
    GenerateLabelsHandler generateLabels,
    TimeProvider timeProvider)
{
    /// <summary>Imprime un solo contenedor.</summary>
    public async Task<SpecimenLabelPdfResult?> HandleContainerAsync(
        Guid orgId, Guid containerId, string? reason, Guid? userId, CancellationToken ct = default)
    {
        // Se toma el contenedor desde el plan (y no suelto) para que la instancia que se muta
        // sea la misma que trackea el plan: si no, el MarkPrinted se aplicaría a otra copia.
        var plan = await ops.GetCollectionPlanByContainerIdAsync(orgId, containerId, ct);
        var container = plan?.Containers.FirstOrDefault(c => c.Id == containerId);
        if (plan is null || container is null)
        {
            return null;
        }

        return await RenderAndRecordAsync(orgId, plan, [container], reason, userId, ct);
    }

    /// <summary>Imprime todas las etiquetas pendientes del plan, en order of draw.</summary>
    public async Task<SpecimenLabelPdfResult?> HandlePlanAsync(
        Guid orgId, string requisition, string? reason, Guid? userId, CancellationToken ct = default)
    {
        var plan = await ops.GetCollectionPlanForUpdateAsync(orgId, requisition, ct);
        if (plan is null || plan.Containers.Count == 0)
        {
            return null;
        }

        // Las rechazadas no se reimprimen junto con el resto: esa muestra ya no se toma.
        var targets = plan.Containers
            .Where(c => c.Status != ContainerStatus.Rejected)
            .OrderBy(c => c.Sequence)
            .ToList();
        if (targets.Count == 0)
        {
            return null;
        }

        return await RenderAndRecordAsync(orgId, plan, targets, reason, userId, ct);
    }

    private async Task<SpecimenLabelPdfResult?> RenderAndRecordAsync(
        Guid orgId,
        CollectionPlan plan,
        List<CollectionPlanContainer> containers,
        string? reason,
        Guid? userId,
        CancellationToken ct)
    {
        // 1. Asegurar accession. Sin él la etiqueta no identifica nada.
        if (containers.Any(c => c.AccessionNumber is null))
        {
            await generateLabels.HandleAsync(orgId, plan.Requisition, userId, ct);
        }

        // 2. Exigir motivo si alguna es reimpresión (D9), antes de emitir el PDF.
        var isReprint = containers.Any(c => c.Status is not (ContainerStatus.PlanCalculated or ContainerStatus.LabelGenerated));
        if (isReprint && string.IsNullOrWhiteSpace(reason))
        {
            throw new ArgumentException(
                "La reimpresión de una etiqueta ya emitida requiere un motivo.", nameof(reason));
        }

        // 3. Resolver formato por contenedor + datos del paciente.
        var formats = await catalog.ListLabelFormatsAsync(orgId, includeInactive: true, ct);
        var assignments = await catalog.ListLabelFormatAssignmentsAsync(orgId, ct);
        var patient = await LoadPatientAsync(orgId, plan.PatientFhirId, ct);
        var now = timeProvider.GetUtcNow();

        var pages = new List<SpecimenLabelPage>(containers.Count);
        foreach (var c in containers)
        {
            if (c.AccessionNumber is null)
            {
                continue; // no se pudo etiquetar (plan sin contenedores válidos); no se imprime en blanco
            }

            var format = ResolveFormat(c, formats, assignments);
            pages.Add(new SpecimenLabelPage(
                Format: format,
                Accession: c.AccessionNumber,
                BarcodeValue: c.Barcode ?? c.AccessionNumber,
                PatientName: patient?.DisplayName,
                PatientBirthDate: patient?.BirthDate,
                ContainerLabel: BuildContainerLabel(c),
                Sequence: c.Sequence,
                RequiredVolumeMl: c.RequiredVolumeMl,
                Tests: BuildTests(c),
                OrderNumber: plan.Requisition,
                PrintedAt: now));
        }

        if (pages.Count == 0)
        {
            return null;
        }

        var bytes = renderer.Render(new SpecimenLabelSheet(pages));

        // 4. Recién con el PDF en mano se mueve el estado y se audita.
        var events = new List<CollectionEvent>(containers.Count);
        foreach (var c in containers)
        {
            if (c.AccessionNumber is null)
            {
                continue;
            }
            if (c.Status is ContainerStatus.PlanCalculated or ContainerStatus.LabelGenerated)
            {
                c.MarkPrinted();
                events.Add(CollectionEvent.Create(
                    c.Id, CollectionEventType.LabelPrinted, userId, c.AccessionNumber, timeProvider));
            }
            else
            {
                // Reimpresión: no se retrocede el estado — la muestra ya está en toma o tomada.
                events.Add(CollectionEvent.Create(
                    c.Id, CollectionEventType.LabelReprinted, userId, reason, timeProvider));
            }
        }
        await ops.SaveCollectionEventsAsync(events, ct);

        var fileName = pages.Count == 1
            ? $"etiqueta-{pages[0].Accession}.pdf"
            : $"etiquetas-{plan.Requisition}.pdf";
        return new SpecimenLabelPdfResult(bytes, fileName);
    }

    /// <summary>
    /// Cascada de resolución: asignación por tubo → por tipo de muestra → default de la org →
    /// primer formato disponible. El último escalón evita que un laboratorio que borró su
    /// default se quede sin poder imprimir.
    /// </summary>
    private static SpecimenLabelFormat ResolveFormat(
        CollectionPlanContainer container,
        IReadOnlyList<LabLabelFormat> formats,
        IReadOnlyList<LabLabelFormatAssignment> assignments)
    {
        LabLabelFormat? match = null;

        if (container.TubeTypeCode is { } tube)
        {
            var byTube = assignments.FirstOrDefault(
                a => string.Equals(a.TubeTypeCode, tube, StringComparison.OrdinalIgnoreCase));
            if (byTube is not null)
            {
                match = formats.FirstOrDefault(f => f.Id == byTube.LabLabelFormatId);
            }
        }

        if (match is null)
        {
            var bySpecimen = assignments.FirstOrDefault(
                a => string.Equals(a.SpecimenTypeCode, container.SpecimenTypeCode, StringComparison.OrdinalIgnoreCase));
            if (bySpecimen is not null)
            {
                match = formats.FirstOrDefault(f => f.Id == bySpecimen.LabLabelFormatId);
            }
        }

        match ??= formats.FirstOrDefault(f => f.IsDefault && f.Active)
            ?? formats.FirstOrDefault(f => f.Active)
            ?? (formats.Count > 0 ? formats[0] : null);

        return match is null
            ? FallbackFormat
            : new SpecimenLabelFormat(
                match.Name, match.WidthMm, match.HeightMm, match.MarginMm, match.OffsetXMm, match.OffsetYMm);
    }

    /// <summary>
    /// Último recurso si la org no tiene ningún formato (aprovisionamiento incompleto): la
    /// medida más común del mercado. Imprimir en un tamaño razonable es mejor que no imprimir.
    /// </summary>
    private static readonly SpecimenLabelFormat FallbackFormat =
        new("Tubo estándar — 51 × 25 mm", 51m, 25m, 1.5m, 0m, 0m);

    private static string? BuildContainerLabel(CollectionPlanContainer c)
    {
        var name = c.TubeTypeName ?? c.SpecimenTypeName ?? c.SpecimenTypeCode;
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }
        return string.IsNullOrWhiteSpace(c.Additive) ? name : $"{name} · {c.Additive}";
    }

    private static string? BuildTests(CollectionPlanContainer c)
    {
        var names = c.Items
            .Select(i => i.LabTestName ?? i.LabTestCode)
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .ToList();
        return names.Count == 0 ? null : string.Join(", ", names);
    }

    private async Task<PatientSnapshot?> LoadPatientAsync(Guid orgId, string patientFhirId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(patientFhirId))
        {
            return null;
        }
        try
        {
            return await hapi.GetPatientAsync(patientFhirId, $"org-{orgId}", ct);
        }
        catch
        {
            // HAPI caído no puede impedir rotular el tubo: la etiqueta sale con accession
            // y barcode, que es lo que la vincula a la orden. El nombre es ayuda visual.
            return null;
        }
    }

}
