using System.Globalization;
using IMed.Api.Application.Abstractions;
using IMed.Api.Application.LabReception;
using IMed.Api.Application.Organizations;
using IMed.Api.Application.Profile;
using IMed.Api.Domain.Catalogs.Lab;

namespace IMed.Api.Application.LabResults;

/// <summary>
/// Pide el informe de la orden a la que pertenece <see cref="ReportId"/>. Se entra por un informe
/// (lo que tienen a mano las pantallas de captura, validación y la historia del paciente) pero lo
/// que se imprime es la orden completa: hay una sola impresión.
/// </summary>
public sealed record GetLabReportPdfQuery(Guid OrganizationId, string ReportId);

public sealed record GetLabReportPdfResult(byte[] Bytes, string FileName);

/// <summary>
/// Arma el informe de resultados de una orden: el documento que se lleva el paciente y lee el
/// médico que pidió el estudio.
///
/// <list type="bullet">
///   <item><b>Una sola impresión por orden.</b> Una orden reúne paneles y pruebas individuales
///   (un <c>ServiceRequest</c> por cada uno, agrupados por <c>requisition</c>, y cada uno con su
///   informe FHIR). Todos salen en el mismo documento.</item>
///   <item><b>Solo lo validado.</b> Lo que no se liberó no se imprime; el documento avisa que
///   hay pendientes. Si no hay nada validado no hay informe (409).</item>
///   <item><b>Área → panel → subárea.</b> El área y la subárea salen de la prueba del catálogo
///   (<see cref="LabSubsections"/>). Lo que no tiene resultados no aparece, ni su título.</item>
///   <item><b>Fuera de rango con asteriscos</b> (* y **), sin color ni palabras: lo pidió el
///   cliente por la sensibilidad del paciente.</item>
/// </list>
/// </summary>
public sealed class GetLabReportPdfHandler(
    IHapiFhirClient hapi,
    ILabCatalogRepository catalog,
    ILabOperationsRepository ops,
    IOrganizationProfileRepository orgRepo,
    IOrganizationBrandingRepository branding,
    ILabReportPdfRenderer renderer,
    TimeProvider timeProvider)
{
    /// <summary>Estados FHIR de un informe liberado (validado).</summary>
    private static readonly HashSet<string> ReleasedStatuses = new(StringComparer.OrdinalIgnoreCase)
    {
        "final", "amended", "corrected", "appended",
    };

    /// <summary>Órdenes que ya no cuentan: ni se imprimen ni quedan como pendientes.</summary>
    private static readonly HashSet<string> DeadOrderStatuses = new(StringComparer.OrdinalIgnoreCase)
    {
        "revoked", "entered-in-error",
    };

    public async Task<GetLabReportPdfResult?> HandleAsync(
        GetLabReportPdfQuery query, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (string.IsNullOrWhiteSpace(query.ReportId))
        {
            return null;
        }

        var orgId = query.OrganizationId;
        var partition = LabRouting.PartitionOf(orgId);

        var entryReport = await hapi.GetDiagnosticReportAsync(query.ReportId, partition, ct);
        if (entryReport is null || string.IsNullOrWhiteSpace(entryReport.ServiceRequestFhirId))
        {
            return null;
        }
        var entryOrder = await hapi.GetLabOrderAsync(entryReport.ServiceRequestFhirId!, partition, ct);
        if (entryOrder is null)
        {
            return null;
        }

        // Las órdenes de la misma orden de laboratorio (requisition). Una orden vieja sin
        // requisition se imprime sola.
        var orders = string.IsNullOrWhiteSpace(entryOrder.Requisition)
            ? [entryOrder]
            : (await hapi.SearchLabOrdersByRequisitionAsync(entryOrder.Requisition!, partition, ct))
                .Where(o => !DeadOrderStatuses.Contains(o.Status))
                .OrderBy(o => o.AuthoredOn ?? DateTimeOffset.MaxValue)
                .ThenBy(o => o.Id, StringComparer.Ordinal)
                .ToList();
        if (orders.All(o => o.Id != entryOrder.Id))
        {
            orders = [entryOrder, .. orders];
        }

        var released = new List<ReleasedOrder>();
        var pending = 0;
        foreach (var order in orders)
        {
            var intake = await ops.GetIntakeByServiceRequestAsync(orgId, order.Id, ct);
            if (intake is null)
            {
                pending++;
                continue;
            }
            var reportId = await LabReportLookup.ResolveReportIdAsync(hapi, intake, order.PatientFhirId, partition, ct);
            var report = reportId is null ? null : await hapi.GetDiagnosticReportAsync(reportId, partition, ct);
            if (report is null || !ReleasedStatuses.Contains(report.Status))
            {
                pending++;
                continue;
            }
            released.Add(new ReleasedOrder(order, report, intake.ReceivedAt, intake.ValidatedAt,
                await ResolvePractitionerAsync(intake.ValidatedByPractitionerFhirId, partition, ct)));
        }

        if (released.Count == 0)
        {
            throw new InvalidOperationException(
                "Esta orden todavía no tiene resultados validados. El informe se imprime cuando se valida al menos una prueba.");
        }

        var areas = await BuildAreasAsync(orgId, partition, released, ct);

        var orgProfile = await orgRepo.GetByIdAsync(orgId, ct);
        var logo = await branding.GetLogoAsync(orgId, ct);
        var patient = await hapi.GetPatientAsync(entryOrder.PatientFhirId, partition, ct);
        var requestedBy = string.IsNullOrWhiteSpace(entryOrder.RequesterPractitionerId)
            ? null
            : await ResolvePractitionerAsync(entryOrder.RequesterPractitionerId, partition, ct);

        var validators = released
            .Select(r => r.ValidatedBy)
            .OfType<PdfPractitionerInfo>()
            .DistinctBy(v => (v.Prefix, v.DisplayName, v.LicenseNumber))
            .ToList();
        var releasedAt = released.Select(r => r.ValidatedAt ?? ParseIssued(r.Report.Issued)).Max();

        var model = new LabReportPdfModel(
            Organization: new PdfOrgInfo(
                orgProfile?.Name ?? "Laboratorio",
                TaxId: null,
                Logo: logo?.ToPdfLogo(orgId)),
            Patient: new PdfPatientInfo(
                DisplayName: patient?.DisplayName ?? $"Paciente {entryOrder.PatientFhirId}",
                BirthDate: patient?.BirthDate,
                Gender: patient?.Gender,
                IdentifierLabel: patient?.IdentifierLabel,
                IdentifierValue: patient?.IdentifierValue),
            Areas: areas,
            PendingCount: pending,
            Requisition: entryOrder.Requisition,
            ReceivedAt: released.Select(r => r.ReceivedAt).Where(d => d is not null).Min(),
            ReleasedAt: releasedAt,
            PrintedAt: timeProvider.GetUtcNow(),
            RequestedBy: requestedBy,
            Validators: validators,
            ExternalLabName: released.Select(r => r.Report.PerformerDisplay).FirstOrDefault(p => !string.IsNullOrWhiteSpace(p)));

        var bytes = renderer.Render(model);
        var safePatient = Sanitize(model.Patient.DisplayName);
        var stamp = (releasedAt ?? timeProvider.GetUtcNow()).ToLocalTime().ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        return new GetLabReportPdfResult(bytes, $"resultados-{safePatient}-{stamp}.pdf");
    }

    /// <summary>
    /// Reparte las observaciones de cada orden liberada en área → bloque → subárea. Un panel cuyas
    /// pruebas son de áreas distintas aparece en cada área con lo que le corresponde.
    /// </summary>
    private async Task<IReadOnlyList<PdfReportArea>> BuildAreasAsync(
        Guid orgId, string partition, IReadOnlyList<ReleasedOrder> released, CancellationToken ct)
    {
        var placed = new List<PlacedLine>();
        for (var blockIndex = 0; blockIndex < released.Count; blockIndex++)
        {
            var r = released[blockIndex];
            var expanded = await LabRouting.ExpandOrderAsync(catalog, orgId, r.Order.CatalogCode, ct);
            var tests = expanded.Select(e => e.Test).ToList();

            string? panelTitle = null;
            var observations = new List<ObservationSnapshot>();
            foreach (var obsId in r.Report.ResultObservationIds)
            {
                var o = await hapi.GetObservationAsync(obsId, partition, ct);
                if (o is null)
                {
                    continue;
                }
                // El "grouper" del panel no tiene valor: es el título del bloque, no una fila.
                if (o.ValueNumeric is null && string.IsNullOrWhiteSpace(o.ValueString))
                {
                    panelTitle ??= o.CodeText;
                    continue;
                }
                observations.Add(o);
            }
            if (observations.Count == 0)
            {
                continue;
            }

            // Un panel lleva título y "validado por"; una prueba individual va suelta en su área.
            var isPanel = panelTitle is not null || tests.Count > 1;
            var title = isPanel ? panelTitle ?? r.Order.CodeText ?? r.Report.CodeText ?? "Panel" : null;

            for (var i = 0; i < observations.Count; i++)
            {
                var o = observations[i];
                var test = MatchTest(o, tests);
                var department = test?.Department ?? tests.FirstOrDefault()?.Department ?? LabDepartment.Other;
                var subsection = test is null ? null : LabSubsections.Find(department, test.Subsection);
                var order = test is null ? tests.Count + i : tests.IndexOf(test);

                placed.Add(new PlacedLine(
                    department, isPanel ? blockIndex : -1, title, isPanel ? r.ValidatedBy : null,
                    r.Report.Conclusion,
                    subsection, order, blockIndex, ToLine(o)));
            }
        }

        var areas = new List<PdfReportArea>();
        foreach (var department in LabSubsections.AreaOrder)
        {
            var inArea = placed.Where(p => p.Department == department).ToList();
            if (inArea.Count == 0)
            {
                continue;
            }

            // Primero las pruebas individuales del área (un solo bloque sin título), después cada
            // panel en el orden de la orden.
            var blocks = inArea
                .GroupBy(p => p.BlockKey)
                .OrderBy(g => g.Key)
                .Select(g => new PdfReportBlock(
                    g.First().BlockTitle,
                    g.First().ValidatedBy is { } v ? FormatPractitioner(v) : null,
                    g.GroupBy(p => p.Subsection?.Code)
                        .OrderBy(s => LabSubsections.OrderOf(s.Key))
                        .Select(s => new PdfReportSubsection(
                            s.First().Subsection?.Label,
                            s.OrderBy(p => p.SourceOrder).ThenBy(p => p.TestOrder).Select(p => p.Line).ToList()))
                        .ToList(),
                    JoinConclusions(g)))
                .ToList();
            areas.Add(new PdfReportArea(LabSubsections.AreaLabel(department), blocks));
        }
        return areas;
    }

    /// <summary>Los comentarios del validador de las órdenes del bloque, sin repetir.</summary>
    private static string? JoinConclusions(IEnumerable<PlacedLine> lines)
    {
        var all = lines.Select(l => l.Conclusion).Where(c => !string.IsNullOrWhiteSpace(c))
            .Select(c => c!.Trim()).Distinct(StringComparer.Ordinal).ToList();
        return all.Count == 0 ? null : string.Join(Environment.NewLine, all);
    }

    /// <summary>
    /// La prueba del catálogo de una observación: por LOINC y, si no tiene (el catálogo admite
    /// pruebas sin LOINC), por el nombre con que se capturó, que es el nombre de la prueba.
    /// </summary>
    private static LabTestDefinition? MatchTest(ObservationSnapshot o, List<LabTestDefinition> tests) =>
        (string.IsNullOrWhiteSpace(o.LoincCode)
            ? null
            : tests.FirstOrDefault(t => string.Equals(t.LoincCode, o.LoincCode, StringComparison.OrdinalIgnoreCase)))
        ?? tests.FirstOrDefault(t => string.Equals(t.Name, o.CodeText?.Trim(), StringComparison.OrdinalIgnoreCase))
        ?? (tests.Count == 1 ? tests[0] : null);

    private static PdfReportLine ToLine(ObservationSnapshot o) => new(
        Name: o.CodeText ?? o.LoincCode ?? "Determinación",
        Value: FormatValue(o),
        Unit: o.ValueUnit,
        ReferenceRange: FormatRange(o.ReferenceLow, o.ReferenceHigh),
        Mark: MarkOf(o.FlagCode),
        IsCorrected: string.Equals(o.Status, "corrected", StringComparison.OrdinalIgnoreCase));

    /// <summary>"**" crítico, "*" fuera de rango o anormal, "" normal. Sin palabras.</summary>
    public static string MarkOf(string? flag) => flag switch
    {
        "HH" or "LL" => "**",
        "H" or "L" or "A" => "*",
        _ => string.Empty,
    };

    private async Task<PdfPractitionerInfo?> ResolvePractitionerAsync(
        string? practitionerFhirId, string partition, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(practitionerFhirId))
        {
            return null;
        }
        var p = await hapi.GetPractitionerAsync(practitionerFhirId!, partition, ct);
        if (p is null)
        {
            return null;
        }
        var name = string.Join(" ", p.Given.Append(p.Family ?? string.Empty)).Trim();
        return new PdfPractitionerInfo(
            DisplayName: string.IsNullOrWhiteSpace(name) ? "Profesional" : name,
            Prefix: p.Prefix,
            LicenseNumber: p.LicenseNumber,
            Specialty: null);
    }

    private static string FormatPractitioner(PdfPractitionerInfo p) =>
        string.IsNullOrWhiteSpace(p.Prefix) ? p.DisplayName : $"{p.Prefix} {p.DisplayName}";

    private static DateTimeOffset? ParseIssued(string? issued) =>
        DateTimeOffset.TryParse(issued, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt) ? dt : null;

    private static string FormatValue(ObservationSnapshot o) =>
        o.ValueNumeric?.ToString("0.####", CultureInfo.InvariantCulture) ?? o.ValueString ?? "—";

    private static string? FormatRange(decimal? low, decimal? high) => (low, high) switch
    {
        (null, null) => null,
        ({ } l, { } h) => FormattableString.Invariant($"{l:0.####} – {h:0.####}"),
        ({ } l, null) => FormattableString.Invariant($"≥ {l:0.####}"),
        (null, { } h) => FormattableString.Invariant($"≤ {h:0.####}"),
    };

    private static string Sanitize(string value)
    {
        var chars = value.Where(c => char.IsLetterOrDigit(c) || c is '-' or ' ').ToArray();
        var cleaned = new string(chars).Trim().Replace(' ', '-');
        return string.IsNullOrWhiteSpace(cleaned) ? "paciente" : cleaned.ToLowerInvariant();
    }

    private sealed record ReleasedOrder(
        LabOrderSnapshot Order,
        DiagnosticReportSnapshot Report,
        DateTimeOffset? ReceivedAt,
        DateTimeOffset? ValidatedAt,
        PdfPractitionerInfo? ValidatedBy);

    /// <summary>Una línea ya ubicada en su área, bloque y subárea.</summary>
    private sealed record PlacedLine(
        LabDepartment Department,
        /// <summary>-1 = pruebas individuales del área (van primero); si no, índice del panel.</summary>
        int BlockKey,
        string? BlockTitle,
        PdfPractitionerInfo? ValidatedBy,
        string? Conclusion,
        LabSubsection? Subsection,
        int TestOrder,
        int SourceOrder,
        PdfReportLine Line);
}
