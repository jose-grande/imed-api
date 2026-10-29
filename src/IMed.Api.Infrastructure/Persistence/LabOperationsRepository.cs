using IMed.Api.Application.Abstractions;
using IMed.Api.Domain.Catalogs.Lab;
using IMed.Api.Domain.Lab;
using Microsoft.EntityFrameworkCore;

namespace IMed.Api.Infrastructure.Persistence;

/// <summary>Implementación EF Core de la operación del laboratorio (Épica LIS-3, L3-F2).</summary>
public sealed class LabOperationsRepository(AppDbContext db) : ILabOperationsRepository
{
    public Task<OrderIntake?> GetIntakeByServiceRequestAsync(
        Guid organizationId, string serviceRequestFhirId, CancellationToken cancellationToken = default) =>
        db.OrderIntakes.AsNoTracking()
            .Where(x => x.OrganizationId == organizationId && x.ServiceRequestFhirId == serviceRequestFhirId)
            .OrderByDescending(x => x.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);

    public Task<OrderIntake?> GetIntakeByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        db.OrderIntakes.FirstOrDefaultAsync(x => x.Id == id, cancellationToken);

    public async Task AddIntakeAsync(OrderIntake intake, CancellationToken cancellationToken = default)
    {
        db.OrderIntakes.Add(intake);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task UpdateIntakeAsync(OrderIntake intake, CancellationToken cancellationToken = default)
    {
        db.OrderIntakes.Update(intake);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task AddSpecimenEventAsync(SpecimenEvent specimenEvent, CancellationToken cancellationToken = default)
    {
        db.SpecimenEvents.Add(specimenEvent);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<int> NextAccessionSeqAsync(
        Guid organizationId, Guid locationId, DateOnly day, CancellationToken cancellationToken = default)
    {
        // Upsert atómico: inserta seq=1 o incrementa el existente, devolviendo el nuevo valor.
        // Evita duplicados bajo concurrencia sin transacción explícita (L3-D5).
        // La clave incluye la sede: cada una lleva su propia numeración contigua (ADR-008).
        var seqs = await db.Database
            .SqlQuery<int>($"""
                INSERT INTO lab_accession_counter (organization_id, location_id, day, seq)
                VALUES ({organizationId}, {locationId}, {day}, 1)
                ON CONFLICT (organization_id, location_id, day)
                DO UPDATE SET seq = lab_accession_counter.seq + 1
                RETURNING seq AS "Value"
                """)
            .ToListAsync(cancellationToken);
        return seqs[0];
    }

    public async Task CheckInOrderAsync(LabOrderCheckIn checkIn, CancellationToken cancellationToken = default)
    {
        // Idempotente: si la orden ya fue ingresada, no-op.
        var exists = await db.LabOrderCheckIns.AsNoTracking().AnyAsync(
            x => x.OrganizationId == checkIn.OrganizationId && x.Requisition == checkIn.Requisition,
            cancellationToken);
        if (exists)
        {
            return;
        }
        db.LabOrderCheckIns.Add(checkIn);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlySet<string>> GetCheckedInRequisitionsAsync(
        Guid organizationId, CancellationToken cancellationToken = default)
    {
        var rows = await db.LabOrderCheckIns.AsNoTracking()
            .Where(x => x.OrganizationId == organizationId)
            .Select(x => x.Requisition)
            .ToListAsync(cancellationToken);
        return rows.ToHashSet(StringComparer.Ordinal);
    }

    public async Task<IReadOnlyDictionary<string, Guid?>> GetCheckInLocationsAsync(
        Guid organizationId, CancellationToken cancellationToken = default)
    {
        var rows = await db.LabOrderCheckIns.AsNoTracking()
            .Where(x => x.OrganizationId == organizationId)
            .Select(x => new { x.Requisition, x.LocationId })
            .ToListAsync(cancellationToken);
        return rows
            .GroupBy(x => x.Requisition, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First().LocationId, StringComparer.Ordinal);
    }

    public async Task<int> NextOrderSeqAsync(
        Guid organizationId, Guid locationId, DateOnly day, CancellationToken cancellationToken = default)
    {
        // Mismo upsert atómico que el accession, contra el contador de órdenes (F1), por sede.
        var seqs = await db.Database
            .SqlQuery<int>($"""
                INSERT INTO lab_order_counter (organization_id, location_id, day, seq)
                VALUES ({organizationId}, {locationId}, {day}, 1)
                ON CONFLICT (organization_id, location_id, day)
                DO UPDATE SET seq = lab_order_counter.seq + 1
                RETURNING seq AS "Value"
                """)
            .ToListAsync(cancellationToken);
        return seqs[0];
    }

    public Task<CollectionPlan?> GetCollectionPlanByRequisitionAsync(
        Guid organizationId, string requisition, CancellationToken cancellationToken = default) =>
        db.CollectionPlans.AsNoTracking()
            .Include(p => p.Containers.OrderBy(c => c.Sequence))
                .ThenInclude(c => c.Items)
            .Include(p => p.UnavailableItems)
            .FirstOrDefaultAsync(
                p => p.OrganizationId == organizationId && p.Requisition == requisition, cancellationToken);

    public async Task UpsertCollectionPlanAsync(CollectionPlan plan, CancellationToken cancellationToken = default)
    {
        // Recalcular = reemplazar: borra el plan anterior (cascade a contenedores/items) y agrega el nuevo.
        var existing = await db.CollectionPlans
            .Where(p => p.OrganizationId == plan.OrganizationId && p.Requisition == plan.Requisition)
            .ToListAsync(cancellationToken);
        if (existing.Count > 0)
        {
            db.CollectionPlans.RemoveRange(existing);
            await db.SaveChangesAsync(cancellationToken);
        }
        db.CollectionPlans.Add(plan);
        await db.SaveChangesAsync(cancellationToken);
    }

    public Task<CollectionPlan?> GetCollectionPlanForUpdateAsync(
        Guid organizationId, string requisition, CancellationToken cancellationToken = default) =>
        db.CollectionPlans
            .Include(p => p.Containers.OrderBy(c => c.Sequence))
                .ThenInclude(c => c.Items)
            .FirstOrDefaultAsync(
                p => p.OrganizationId == organizationId && p.Requisition == requisition, cancellationToken);

    public Task<CollectionPlanContainer?> GetContainerForUpdateAsync(
        Guid organizationId, Guid containerId, CancellationToken cancellationToken = default) =>
        db.CollectionPlanContainers
            .Include(c => c.Items)
            .FirstOrDefaultAsync(
                c => c.Id == containerId
                  && db.CollectionPlans.Any(p => p.Id == c.CollectionPlanId && p.OrganizationId == organizationId),
                cancellationToken);

    public Task<CollectionPlan?> GetCollectionPlanByContainerIdAsync(
        Guid organizationId, Guid containerId, CancellationToken cancellationToken = default) =>
        db.CollectionPlans
            .Include(p => p.Containers.OrderBy(c => c.Sequence))
                .ThenInclude(c => c.Items)
            .FirstOrDefaultAsync(
                p => p.OrganizationId == organizationId
                  && p.Containers.Any(c => c.Id == containerId),
                cancellationToken);

    public async Task SaveCollectionEventsAsync(
        IEnumerable<CollectionEvent> events, CancellationToken cancellationToken = default)
    {
        db.CollectionEvents.AddRange(events);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<CollectionEvent>> ListCollectionEventsAsync(
        Guid containerId, CancellationToken cancellationToken = default) =>
        await db.CollectionEvents.AsNoTracking()
            .Where(e => e.CollectionPlanContainerId == containerId)
            .OrderBy(e => e.At)
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<TechnicalReceptionRow>> ListContainersForTechnicalReceptionAsync(
        Guid organizationId, IReadOnlyList<ContainerStatus> statuses, CancellationToken cancellationToken = default)
    {
        // 1) contenedores candidatos + info del plan (join plano, sin navegación).
        var flat = await (
            from c in db.CollectionPlanContainers.AsNoTracking()
            join p in db.CollectionPlans.AsNoTracking() on c.CollectionPlanId equals p.Id
            where p.OrganizationId == organizationId && statuses.Contains(c.Status)
            orderby c.CollectedAt
            select new { c.Id, p.Requisition, p.PatientFhirId, p.LocationId })
            .ToListAsync(cancellationToken);

        if (flat.Count == 0)
        {
            return [];
        }

        // 2) contenedores con sus items (Include garantizado al no proyectar).
        var ids = flat.Select(f => f.Id).ToList();
        var containers = await db.CollectionPlanContainers.AsNoTracking()
            .Include(c => c.Items)
            .Where(c => ids.Contains(c.Id))
            .ToListAsync(cancellationToken);
        var byId = containers.ToDictionary(c => c.Id);

        return flat
            .Where(f => byId.ContainsKey(f.Id))
            .Select(f => new TechnicalReceptionRow(byId[f.Id], f.Requisition, f.PatientFhirId, f.LocationId))
            .ToList();
    }

    public async Task<IReadOnlyList<TechnicalReceptionRow>> ListSiblingContainersForTechnicalReceptionAsync(
        Guid organizationId, IReadOnlyList<ContainerStatus> pendingStatuses, CancellationToken cancellationToken = default)
    {
        // 1) Planes que tienen al menos un contenedor esperando bench. El técnico recibe la
        //    gradilla por orden, así que la worklist se arma por plan, no por contenedor suelto.
        var planIds = await (
            from c in db.CollectionPlanContainers.AsNoTracking()
            join p in db.CollectionPlans.AsNoTracking() on c.CollectionPlanId equals p.Id
            where p.OrganizationId == organizationId && pendingStatuses.Contains(c.Status)
            select p.Id)
            .Distinct()
            .ToListAsync(cancellationToken);

        if (planIds.Count == 0)
        {
            return [];
        }

        // 2) TODOS los contenedores de esos planes — también los ya recibidos, para poder
        //    mostrar "1 de 3" y que el técnico note si le falta un tubo de la gradilla.
        var plans = await db.CollectionPlans.AsNoTracking()
            .Include(p => p.Containers.OrderBy(c => c.Sequence))
                .ThenInclude(c => c.Items)
            .Where(p => planIds.Contains(p.Id))
            .ToListAsync(cancellationToken);

        return plans
            .SelectMany(p => p.Containers.Select(c => new TechnicalReceptionRow(c, p.Requisition, p.PatientFhirId, p.LocationId)))
            .ToList();
    }

    public async Task<TechnicalReceptionRow?> FindContainerByBarcodeAsync(
        Guid organizationId, string code, CancellationToken cancellationToken = default)
    {
        var trimmed = code.Trim();

        // El lector puede disparar el barcode o el técnico teclear el accession: son el mismo
        // valor hoy (R6-F4), pero se buscan por separado por si algún día divergen.
        var flat = await (
            from c in db.CollectionPlanContainers.AsNoTracking()
            join p in db.CollectionPlans.AsNoTracking() on c.CollectionPlanId equals p.Id
            where p.OrganizationId == organizationId
               && (c.Barcode == trimmed || c.AccessionNumber == trimmed)
            select new { c.Id, p.Requisition, p.PatientFhirId, p.LocationId })
            .FirstOrDefaultAsync(cancellationToken);

        if (flat is null)
        {
            return null;
        }

        var container = await db.CollectionPlanContainers.AsNoTracking()
            .Include(c => c.Items)
            .FirstOrDefaultAsync(c => c.Id == flat.Id, cancellationToken);

        return container is null ? null : new TechnicalReceptionRow(container, flat.Requisition, flat.PatientFhirId, flat.LocationId);
    }

    public async Task<IReadOnlySet<Guid>> ListContainersWithEventAsync(
        IReadOnlyList<Guid> containerIds, CollectionEventType type, CancellationToken cancellationToken = default)
    {
        if (containerIds.Count == 0)
        {
            return new HashSet<Guid>();
        }
        var ids = await db.CollectionEvents.AsNoTracking()
            .Where(e => containerIds.Contains(e.CollectionPlanContainerId) && e.Type == type)
            .Select(e => e.CollectionPlanContainerId)
            .Distinct()
            .ToListAsync(cancellationToken);
        return ids.ToHashSet();
    }

    // --- Validación: devolución de informes al técnico ---

    public async Task<IReadOnlyList<ValidationRejectionReason>> ListValidationRejectionReasonsAsync(
        Guid organizationId, CancellationToken cancellationToken = default) =>
        await db.ValidationRejectionReasons.AsNoTracking()
            .Where(x => x.Active && (x.OrganizationId == null || x.OrganizationId == organizationId))
            .OrderBy(x => x.SortOrder).ThenBy(x => x.Name)
            .ToListAsync(cancellationToken);

    public Task<ValidationRejectionReason?> GetValidationRejectionReasonByCodeAsync(
        Guid organizationId, string code, CancellationToken cancellationToken = default)
    {
        var normalized = (code ?? string.Empty).Trim().ToUpperInvariant();
        // La fila de la org gana sobre la plantilla global si ambas existen.
        return db.ValidationRejectionReasons.AsNoTracking()
            .Where(x => x.Active && x.Code == normalized
                && (x.OrganizationId == null || x.OrganizationId == organizationId))
            .OrderByDescending(x => x.OrganizationId != null)
            .FirstOrDefaultAsync(cancellationToken);
    }

    public async Task AddValidationRejectionAsync(
        ResultValidationRejection rejection, CancellationToken cancellationToken = default)
    {
        db.ResultValidationRejections.Add(rejection);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<ResultValidationRejection>> ListOpenValidationRejectionsAsync(
        Guid organizationId, CancellationToken cancellationToken = default) =>
        await db.ResultValidationRejections.AsNoTracking()
            .Where(x => x.OrganizationId == organizationId && x.ResolvedAt == null)
            .ToListAsync(cancellationToken);

    public async Task<int> ResolveValidationRejectionsAsync(
        Guid organizationId, string reportFhirId, CancellationToken cancellationToken = default)
    {
        var open = await db.ResultValidationRejections
            .Where(x => x.OrganizationId == organizationId
                && x.ReportFhirId == reportFhirId && x.ResolvedAt == null)
            .ToListAsync(cancellationToken);
        if (open.Count == 0)
        {
            return 0;
        }
        foreach (var r in open)
        {
            r.Resolve();
        }
        await db.SaveChangesAsync(cancellationToken);
        return open.Count;
    }

    public async Task<IReadOnlyList<OrderIntake>> ListInHouseIntakesAsync(
        Guid organizationId, OrderIntakeStatus? status, CancellationToken cancellationToken = default)
    {
        var q = db.OrderIntakes.AsNoTracking()
            .Where(x => x.OrganizationId == organizationId && x.Routing == OrderRouting.InHouse);
        if (status is { } s)
        {
            q = q.Where(x => x.Status == s);
        }
        return await q
            .OrderByDescending(x => x.Priority)
            .ThenBy(x => x.ReceivedAt)
            .ToListAsync(cancellationToken);
    }
}
