using System.Globalization;
using IMed.Api.Application.Abstractions;
using IMed.Api.Application.LabReception;
using IMed.Api.Application.Organizations;
using IMed.Api.Application.Profile;

namespace IMed.Api.Application.LabResults;

public sealed record GetLabReportPdfQuery(Guid OrganizationId, string ReportId);

public sealed record GetLabReportPdfResult(byte[] Bytes, string FileName);

/// <summary>
/// Arma el informe de resultados imprimible: el documento que se lleva el paciente y lee el
/// médico que pidió el estudio.
///
/// <para><b>Agrupación:</b> cuando el informe es un panel, la observación "grouper" de FHIR no
/// tiene valor propio — existe para colgar los componentes. Acá se convierte en el <b>título del
/// bloque</b> y sus analitos en las filas, en vez de imprimirse como una determinación vacía.</para>
///
/// <para><b>Preliminar:</b> se permite imprimir un informe no liberado, porque en la operación
/// real hace falta (un adelanto al médico tratante), pero sale con un aviso imposible de
/// ignorar. Bloquearlo empujaría a la captura de pantalla, que es peor: una imagen sin marca
/// alguna.</para>
/// </summary>
public sealed class GetLabReportPdfHandler(
    IHapiFhirClient hapi,
    ILabOperationsRepository ops,
    IOrganizationProfileRepository orgRepo,
    IOrganizationBrandingRepository branding,
    ILabReportPdfRenderer renderer,
    TimeProvider timeProvider)
{
    public async Task<GetLabReportPdfResult?> HandleAsync(
        GetLabReportPdfQuery query, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (string.IsNullOrWhiteSpace(query.ReportId))
        {
            return null;
        }

        var partition = LabRouting.PartitionOf(query.OrganizationId);

        var report = await hapi.GetDiagnosticReportAsync(query.ReportId, partition, ct);
        if (report is null)
        {
            return null;
        }

        var orgProfile = await orgRepo.GetByIdAsync(query.OrganizationId, ct);
        var logo = await branding.GetLogoAsync(query.OrganizationId, ct);
        var patient = await hapi.GetPatientAsync(report.PatientFhirId, partition, ct);

        OrderIntakeContext intakeCtx = default;
        if (!string.IsNullOrWhiteSpace(report.ServiceRequestFhirId))
        {
            var intake = await ops.GetIntakeByServiceRequestAsync(
                query.OrganizationId, report.ServiceRequestFhirId!, ct);
            if (intake is not null)
            {
                intakeCtx = new OrderIntakeContext(
                    intake.AccessionNumber, intake.Requisition,
                    intake.ReceivedAt, intake.ValidatedAt, intake.ValidatedByPractitionerFhirId);
            }
        }

        var groups = await BuildGroupsAsync(report, partition, ct);

        var validatedBy = await ResolvePractitionerAsync(intakeCtx.ValidatedByPractitionerFhirId, partition, ct);
        var requestedBy = await ResolveRequesterAsync(report.ServiceRequestFhirId, partition, ct);

        var isFinal = string.Equals(report.Status, "final", StringComparison.OrdinalIgnoreCase);

        var model = new LabReportPdfModel(
            Organization: new PdfOrgInfo(
                orgProfile?.Name ?? "Laboratorio",
                TaxId: null,
                Logo: logo?.ToPdfLogo(query.OrganizationId)),
            Patient: new PdfPatientInfo(
                DisplayName: patient?.DisplayName ?? $"Paciente {report.PatientFhirId}",
                BirthDate: patient?.BirthDate,
                Gender: patient?.Gender,
                IdentifierLabel: patient?.IdentifierLabel,
                IdentifierValue: patient?.IdentifierValue),
            Groups: groups,
            IsFinal: isFinal,
            Requisition: intakeCtx.Requisition,
            AccessionNumber: intakeCtx.AccessionNumber,
            OrderNumber: null,
            CollectedAt: null,
            ReceivedAt: intakeCtx.ReceivedAt,
            // Un informe liberado sin fecha local cae al `issued` de FHIR antes que quedar vacío.
            ReleasedAt: intakeCtx.ValidatedAt ?? ParseIssued(report.Issued, isFinal),
            PrintedAt: timeProvider.GetUtcNow(),
            RequestedBy: requestedBy,
            ValidatedBy: validatedBy,
            Conclusion: report.Conclusion,
            ExternalLabName: report.PerformerDisplay);

        var bytes = renderer.Render(model);
        var safePatient = Sanitize(model.Patient.DisplayName);
        var stamp = (intakeCtx.ValidatedAt ?? timeProvider.GetUtcNow()).ToLocalTime().ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        var suffix = isFinal ? string.Empty : "-PRELIMINAR";
        return new GetLabReportPdfResult(bytes, $"resultados-{safePatient}-{stamp}{suffix}.pdf");
    }

    /// <summary>
    /// Convierte las observaciones del informe en bloques imprimibles. La "grouper" del panel
    /// —sin valor numérico ni textual— pasa a ser el encabezado del bloque.
    /// </summary>
    private async Task<IReadOnlyList<PdfReportGroup>> BuildGroupsAsync(
        DiagnosticReportSnapshot report, string partition, CancellationToken ct)
    {
        string? groupTitle = null;
        string? groupLoinc = null;
        var lines = new List<PdfReportLine>();

        foreach (var obsId in report.ResultObservationIds)
        {
            var o = await hapi.GetObservationAsync(obsId, partition, ct);
            if (o is null)
            {
                continue;
            }

            var isGrouper = o.ValueNumeric is null && string.IsNullOrWhiteSpace(o.ValueString);
            if (isGrouper)
            {
                groupTitle ??= o.CodeText ?? report.CodeText;
                groupLoinc ??= o.LoincCode;
                continue;
            }

            var critical = o.FlagCode is "HH" or "LL";
            var abnormal = critical || o.FlagCode is "H" or "L" or "A";

            lines.Add(new PdfReportLine(
                Name: o.CodeText ?? o.LoincCode ?? "Determinación",
                LoincCode: o.LoincCode,
                Value: FormatValue(o),
                Unit: o.ValueUnit,
                ReferenceRange: FormatRange(o.ReferenceLow, o.ReferenceHigh),
                FlagCode: o.FlagCode,
                FlagLabel: FlagLabel(o.FlagCode),
                IsAbnormal: abnormal,
                IsCritical: critical,
                IsCorrected: string.Equals(o.Status, "corrected", StringComparison.OrdinalIgnoreCase)));
        }

        return
        [
            new PdfReportGroup(
                groupTitle ?? report.CodeText ?? "Resultados",
                groupLoinc ?? report.LoincCode,
                lines)
        ];
    }

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

    private async Task<PdfPractitionerInfo?> ResolveRequesterAsync(
        string? serviceRequestId, string partition, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(serviceRequestId))
        {
            return null;
        }
        var order = await hapi.GetLabOrderAsync(serviceRequestId!, partition, ct);
        if (order is null)
        {
            return null;
        }
        // El solicitante viaja como referencia al Practitioner, sea interno o el externo que la
        // recepción registró (LIS-DEC-10): en ambos casos se resuelve igual.
        return string.IsNullOrWhiteSpace(order.RequesterPractitionerId)
            ? null
            : await ResolvePractitionerAsync(order.RequesterPractitionerId, partition, ct);
    }

    private static DateTimeOffset? ParseIssued(string? issued, bool isFinal) =>
        isFinal && DateTimeOffset.TryParse(issued, out var dt) ? dt : null;

    private static string FormatValue(ObservationSnapshot o) =>
        o.ValueNumeric?.ToString("0.####", CultureInfo.InvariantCulture) ?? o.ValueString ?? "—";

    private static string? FormatRange(decimal? low, decimal? high) => (low, high) switch
    {
        (null, null) => null,
        ({ } l, { } h) => FormattableString.Invariant($"{l:0.####} – {h:0.####}"),
        ({ } l, null) => FormattableString.Invariant($"≥ {l:0.####}"),
        (null, { } h) => FormattableString.Invariant($"≤ {h:0.####}"),
    };

    private static string? FlagLabel(string? flag) => flag switch
    {
        "H" => "Alto",
        "L" => "Bajo",
        "HH" => "Crítico alto",
        "LL" => "Crítico bajo",
        "A" => "Anormal",
        "N" => null,
        _ => null,
    };

    private static string Sanitize(string value)
    {
        var chars = value.Where(c => char.IsLetterOrDigit(c) || c is '-' or ' ').ToArray();
        var cleaned = new string(chars).Trim().Replace(' ', '-');
        return string.IsNullOrWhiteSpace(cleaned) ? "paciente" : cleaned.ToLowerInvariant();
    }

    private readonly record struct OrderIntakeContext(
        string? AccessionNumber,
        string? Requisition,
        DateTimeOffset? ReceivedAt,
        DateTimeOffset? ValidatedAt,
        string? ValidatedByPractitionerFhirId);
}
