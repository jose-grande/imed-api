using IMed.Api.Application.Abstractions;
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
        Guid organizationId, DateOnly day, CancellationToken cancellationToken = default)
    {
        // Upsert atómico: inserta seq=1 o incrementa el existente, devolviendo el nuevo valor.
        // Evita duplicados bajo concurrencia sin transacción explícita (L3-D5).
        var seqs = await db.Database
            .SqlQuery<int>($"""
                INSERT INTO lab_accession_counter (organization_id, day, seq)
                VALUES ({organizationId}, {day}, 1)
                ON CONFLICT (organization_id, day)
                DO UPDATE SET seq = lab_accession_counter.seq + 1
                RETURNING seq AS "Value"
                """)
            .ToListAsync(cancellationToken);
        return seqs[0];
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
