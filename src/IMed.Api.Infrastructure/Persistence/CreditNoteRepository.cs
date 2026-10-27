using IMed.Api.Application.Abstractions;
using IMed.Api.Domain.Billing.Invoicing;
using Microsoft.EntityFrameworkCore;

namespace IMed.Api.Infrastructure.Persistence;

public sealed class CreditNoteRepository(AppDbContext db) : ICreditNoteRepository
{
    public Task<CreditNote?> GetAsync(Guid organizationId, Guid creditNoteId, CancellationToken ct = default) =>
        db.CreditNotes.AsNoTracking().Include(x => x.Lines)
            .FirstOrDefaultAsync(x => x.OrganizationId == organizationId && x.Id == creditNoteId, ct);

    public Task<CreditNote?> GetForUpdateAsync(Guid organizationId, Guid creditNoteId, CancellationToken ct = default) =>
        db.CreditNotes.Include(x => x.Lines)
            .FirstOrDefaultAsync(x => x.OrganizationId == organizationId && x.Id == creditNoteId, ct);

    public async Task<IReadOnlyList<CreditNote>> ListByInvoiceAsync(
        Guid organizationId, Guid invoiceId, CancellationToken ct = default) =>
        await db.CreditNotes.AsNoTracking().Include(x => x.Lines)
            .Where(x => x.OrganizationId == organizationId && x.InvoiceId == invoiceId && x.FiscalDocumentId != null)
            .OrderBy(x => x.CreatedAt)
            .ToListAsync(ct);

    public async Task<IReadOnlyDictionary<Guid, decimal>> CreditedQuantitiesAsync(
        Guid organizationId, Guid invoiceId, CancellationToken ct = default) =>
        await (from l in db.CreditNoteLines
               join n in db.CreditNotes on l.CreditNoteId equals n.Id
               where n.OrganizationId == organizationId && n.InvoiceId == invoiceId && n.FiscalDocumentId != null
               group l by l.InvoiceLineId into g
               select new { g.Key, Cantidad = g.Sum(x => x.Quantity) })
            .ToDictionaryAsync(x => x.Key, x => x.Cantidad, ct);

    public async Task SaveIssuedAsync(
        CreditNote creditNote, FiscalDocument document, Invoice invoice, CancellationToken ct = default)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        db.CreditNotes.Add(creditNote);
        db.FiscalDocuments.Add(document);
        if (db.Entry(invoice).State == EntityState.Detached)
        {
            db.Invoices.Attach(invoice);
            db.Entry(invoice).Property(x => x.Balance).IsModified = true;
            db.Entry(invoice).Property(x => x.CreditedTotal).IsModified = true;
            db.Entry(invoice).Property(x => x.Status).IsModified = true;
            db.Entry(invoice).Property(x => x.UpdatedAt).IsModified = true;
        }
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
    }

    public Task SaveAsync(CancellationToken ct = default) => db.SaveChangesAsync(ct);
}
