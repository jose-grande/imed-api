using IMed.Api.Application.Abstractions;
using IMed.Api.Domain.Catalogs.Lab;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace IMed.Api.Infrastructure.Persistence;

/// <summary>
/// Implementación EF Core del aprovisionamiento copy-on-create del catálogo de
/// laboratorio. Clona las plantillas globales (org_id NULL) a filas propias de la
/// org, remapeando las FKs internas (rangos y items de panel → prueba clonada).
/// Los tipos de muestra/tubos quedan como referencia compartida (no se clonan): las
/// pruebas clonadas conservan su <c>SpecimenTypeId</c>/<c>TubeTypeId</c> apuntando a
/// la fila estándar. Todo en una sola transacción (un <c>SaveChanges</c>), atómico
/// por org.
/// </summary>
public sealed class LabCatalogProvisioning(
    AppDbContext db,
    TimeProvider timeProvider,
    ILogger<LabCatalogProvisioning> logger) : ILabCatalogProvisioning
{
    public async Task<bool> ProvisionOrganizationAsync(Guid organizationId, CancellationToken cancellationToken = default)
    {
        if (organizationId == Guid.Empty)
        {
            throw new ArgumentException("organizationId es obligatorio.", nameof(organizationId));
        }

        // Los formatos de etiqueta van primero y con guard propio, para que una org
        // creada antes de R6-F4b (que ya tiene catálogo y salta el guard de abajo)
        // igual los reciba en el backfill de arranque.
        var labelsProvisioned = await ProvisionLabelFormatsAsync(organizationId, cancellationToken);

        // Idempotencia: si la org ya tiene pruebas propias, ya fue aprovisionada.
        if (await db.LabTests.AnyAsync(x => x.OrganizationId == organizationId, cancellationToken))
        {
            return labelsProvisioned;
        }

        var today = DateOnly.FromDateTime(timeProvider.GetUtcNow().UtcDateTime);

        // 1. Pruebas: clonar cada global y mapear old-id → prueba clonada (para remapear
        //    rangos e items de panel por su prueba de origen).
        var globalTests = await db.LabTests.AsNoTracking()
            .Where(x => x.OrganizationId == null)
            .ToListAsync(cancellationToken);

        var newTestByOldId = new Dictionary<Guid, LabTestDefinition>();
        foreach (var t in globalTests)
        {
            var clone = LabTestDefinition.Create(
                t.Code, t.Name, t.Datatype, t.LoincCode, t.Unit, t.Department,
                t.FastingHoursMin, t.FastingHoursMax, t.CollectionType, t.PatientInstruction,
                organizationId, t.Active, timeProvider);
            // Tipos de muestra/tubos son referencia compartida → se conservan los ids.
            clone.SetSpecimenMapping(t.SpecimenTypeId, t.TubeTypeId, t.CollectionMode, timeProvider);
            clone.SetCollectionRequirements(t.MinVolumeMl, t.PreferredVolumeMl, t.AnalyzerMinVolumeMl, t.StabilityMinutes, timeProvider);
            db.LabTests.Add(clone);
            newTestByOldId[t.Id] = clone;
        }

        // 2. Rangos de referencia: remapear a la prueba clonada.
        var globalRanges = await db.ReferenceRanges.AsNoTracking()
            .Where(x => x.OrganizationId == null)
            .ToListAsync(cancellationToken);
        foreach (var r in globalRanges)
        {
            if (!newTestByOldId.TryGetValue(r.LabTestId, out var clonedTest))
            {
                continue; // rango de una prueba no global; nada que remapear
            }
            db.ReferenceRanges.Add(ReferenceRange.Create(
                clonedTest.Id, r.Sex, r.AgeMinYears, r.AgeMaxYears, r.Pregnancy, r.Method,
                r.Low, r.High, r.CriticalLow, r.CriticalHigh, r.Unit, organizationId, timeProvider));
        }

        // 3. Paneles: clonar panel + su versión activa como v1 + items remapeados.
        var globalPanels = await db.LabPanels.AsNoTracking()
            .Where(x => x.OrganizationId == null)
            .ToListAsync(cancellationToken);
        foreach (var p in globalPanels)
        {
            var clonePanel = LabPanel.Create(p.Code, p.Name, p.Description, organizationId, p.Active, p.LoincCode, timeProvider);
            db.LabPanels.Add(clonePanel);

            var activeVersion = await db.LabPanelVersions.AsNoTracking()
                .Where(v => v.LabPanelId == p.Id && v.Status == PanelVersionStatus.Active)
                .OrderByDescending(v => v.Version)
                .FirstOrDefaultAsync(cancellationToken);
            if (activeVersion is null)
            {
                continue;
            }

            var cloneVersion = LabPanelVersion.Create(clonePanel.Id, 1, today, PanelVersionStatus.Active, timeProvider);
            db.LabPanelVersions.Add(cloneVersion);

            var items = await db.LabPanelVersionItems.AsNoTracking()
                .Where(i => i.LabPanelVersionId == activeVersion.Id)
                .OrderBy(i => i.DisplayOrder)
                .ToListAsync(cancellationToken);
            foreach (var item in items)
            {
                if (!newTestByOldId.TryGetValue(item.LabTestId, out var clonedTest))
                {
                    continue;
                }
                db.LabPanelVersionItems.Add(LabPanelVersionItem.Create(cloneVersion.Id, clonedTest.Id, item.DisplayOrder));
            }
        }

        // 4. Reglas: clonar regla + su versión activa como v1 (targetLoinc/logic son por código).
        var globalRules = await db.LabRules.AsNoTracking()
            .Where(x => x.OrganizationId == null)
            .ToListAsync(cancellationToken);
        foreach (var ru in globalRules)
        {
            var cloneRule = LabRule.Create(
                ru.Code, ru.Name, ru.Scope, ru.Action, ru.TargetLoinc, ru.Severity, ru.Message, organizationId, ru.Active, timeProvider);
            db.LabRules.Add(cloneRule);

            var activeVersion = await db.LabRuleVersions.AsNoTracking()
                .Where(v => v.LabRuleId == ru.Id && v.Status == RuleVersionStatus.Active)
                .OrderByDescending(v => v.Version)
                .FirstOrDefaultAsync(cancellationToken);
            if (activeVersion is null)
            {
                continue;
            }
            db.LabRuleVersions.Add(LabRuleVersion.Create(
                cloneRule.Id, 1, activeVersion.LogicJson, today, RuleVersionStatus.Active, timeProvider));
        }

        // 5. Fórmulas calculadas (referencian pruebas por código → sin remapeo de id).
        var globalDerived = await db.DerivedCalculations.AsNoTracking()
            .Where(x => x.OrganizationId == null)
            .ToListAsync(cancellationToken);
        foreach (var d in globalDerived)
        {
            db.DerivedCalculations.Add(DerivedCalculation.Create(
                d.OutputTestCode, d.Name, d.FormulaJson, d.InputsJson, organizationId, d.Active, timeProvider));
        }

        // 6. Objetivos de TAT (referencian por código).
        var globalTat = await db.TatTargets.AsNoTracking()
            .Where(x => x.OrganizationId == null)
            .ToListAsync(cancellationToken);
        foreach (var tt in globalTat)
        {
            db.TatTargets.Add(TatTarget.Create(
                tt.Scope, tt.TargetCode, tt.Priority, tt.Minutes, organizationId, timeProvider));
        }

        // 7. Motivos de rechazo de muestra.
        var globalReasons = await db.SpecimenRejectionReasons.AsNoTracking()
            .Where(x => x.OrganizationId == null)
            .ToListAsync(cancellationToken);
        foreach (var reason in globalReasons)
        {
            db.SpecimenRejectionReasons.Add(SpecimenRejectionReason.Create(
                reason.Code, reason.Name, reason.Description, reason.RequiresNewSample, reason.SortOrder,
                organizationId, reason.Active, timeProvider));
        }

        await db.SaveChangesAsync(cancellationToken);
        logger.LogInformation(
            "Catálogo de laboratorio aprovisionado para org {OrgId}: {Tests} pruebas, {Panels} paneles, {Rules} reglas.",
            organizationId, globalTests.Count, globalPanels.Count, globalRules.Count);
        return true;
    }

    /// <summary>
    /// Clona los formatos de etiqueta y sus asignaciones (R6-F4b). El tamaño del rollo es
    /// configuración que cada laboratorio posee (compra la impresora que quiere), así que
    /// se clona igual que el resto del catálogo editable.
    ///
    /// <para>Tiene <b>guard propio</b> ("la org ya tiene formatos") en vez de colgarse del
    /// guard de pruebas: las orgs creadas antes de R6-F4b ya tienen catálogo, así que el
    /// guard principal las saltaría y se quedarían sin formatos para siempre. Con guard
    /// separado, el backfill de arranque se las completa.</para>
    /// </summary>
    private async Task<bool> ProvisionLabelFormatsAsync(Guid organizationId, CancellationToken cancellationToken)
    {
        if (await db.LabLabelFormats.AnyAsync(x => x.OrganizationId == organizationId, cancellationToken))
        {
            return false;
        }

        var globalFormats = await db.LabLabelFormats.AsNoTracking()
            .Where(x => x.OrganizationId == null)
            .ToListAsync(cancellationToken);
        if (globalFormats.Count == 0)
        {
            return false;
        }

        var newFormatByOldId = new Dictionary<Guid, LabLabelFormat>();
        foreach (var f in globalFormats)
        {
            var clone = LabLabelFormat.Create(
                f.Code, f.Name, f.WidthMm, f.HeightMm, f.MarginMm, f.OffsetXMm, f.OffsetYMm,
                f.IsDefault, f.SortOrder, organizationId, f.Active, timeProvider);
            db.LabLabelFormats.Add(clone);
            newFormatByOldId[f.Id] = clone;
        }

        // Las asignaciones referencian tubo/tipo de muestra por **código** (esas tablas son
        // referencia compartida entre tenants), así que solo hay que remapear el formato.
        var globalAssignments = await db.LabLabelFormatAssignments.AsNoTracking()
            .Where(x => x.OrganizationId == null)
            .ToListAsync(cancellationToken);
        foreach (var a in globalAssignments)
        {
            if (!newFormatByOldId.TryGetValue(a.LabLabelFormatId, out var clonedFormat))
            {
                continue; // asignación a un formato no global; nada que remapear
            }
            db.LabLabelFormatAssignments.Add(a.TubeTypeCode is { } tube
                ? LabLabelFormatAssignment.ForTube(tube, clonedFormat.Id, organizationId, timeProvider)
                : LabLabelFormatAssignment.ForSpecimen(a.SpecimenTypeCode!, clonedFormat.Id, organizationId, timeProvider));
        }

        await db.SaveChangesAsync(cancellationToken);
        logger.LogInformation(
            "Formatos de etiqueta aprovisionados para org {OrgId}: {Formats} formato(s).",
            organizationId, globalFormats.Count);
        return true;
    }
}
