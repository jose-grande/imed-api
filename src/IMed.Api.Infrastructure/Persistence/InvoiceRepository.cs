using IMed.Api.Application.Abstractions;
using IMed.Api.Domain.Billing.Accounts;
using IMed.Api.Domain.Billing.Invoicing;
using Microsoft.EntityFrameworkCore;

namespace IMed.Api.Infrastructure.Persistence;

/// <summary>Facturas y documentos fiscales sobre EF Core (FAC-5).</summary>
public sealed class InvoiceRepository(AppDbContext db) : IInvoiceRepository
{
    public Task<Invoice?> GetAsync(Guid organizationId, Guid invoiceId, CancellationToken ct = default) =>
        db.Invoices.AsNoTracking()
            .Include(x => x.Lines)
            .FirstOrDefaultAsync(x => x.OrganizationId == organizationId && x.Id == invoiceId, ct);

    public async Task<(Invoice? Invoice, FiscalDocument? Document)> GetForUpdateAsync(
        Guid organizationId, Guid invoiceId, CancellationToken ct = default)
    {
        var factura = await db.Invoices
            .Include(x => x.Lines)
            .FirstOrDefaultAsync(x => x.OrganizationId == organizationId && x.Id == invoiceId, ct);
        var documento = await db.FiscalDocuments
            .FirstOrDefaultAsync(x => x.OrganizationId == organizationId && x.InvoiceId == invoiceId, ct);
        return (factura, documento);
    }

    public Task<FiscalDocument?> GetFiscalDocumentAsync(
        Guid organizationId, Guid invoiceId, CancellationToken ct = default) =>
        db.FiscalDocuments.AsNoTracking()
            .FirstOrDefaultAsync(x => x.OrganizationId == organizationId && x.InvoiceId == invoiceId, ct);

    public async Task<IReadOnlyList<Invoice>> ListByAccountAsync(
        Guid organizationId, Guid accountId, CancellationToken ct = default) =>
        await db.Invoices.AsNoTracking()
            .Include(x => x.Lines)
            .Where(x => x.OrganizationId == organizationId && x.AccountId == accountId)
            .OrderByDescending(x => x.CreatedAt)
            .ToListAsync(ct);

    public Task<FiscalDocument?> FindByFullNumberAsync(
        Guid organizationId, string fullNumber, CancellationToken ct = default)
    {
        var n = fullNumber.Trim();
        return db.FiscalDocuments.AsNoTracking()
            .FirstOrDefaultAsync(x => x.OrganizationId == organizationId && x.FullNumber == n, ct);
    }

    public async Task<string?> GetEstablishmentCodeAsync(
        Guid organizationId, Guid locationId, CancellationToken ct = default) =>
        await db.OrganizationLocations.AsNoTracking()
            .Where(x => x.OrganizationId == organizationId && x.Id == locationId)
            .Select(x => x.FiscalEstablishmentCode)
            .FirstOrDefaultAsync(ct);

    /// <inheritdoc />
    public async Task SaveIssuedAsync(
        Invoice invoice,
        FiscalDocument document,
        IReadOnlyList<BillingCharge> invoicedCharges,
        CancellationToken ct = default)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        db.Invoices.Add(invoice);
        db.FiscalDocuments.Add(document);

        // Los cargos vienen rastreados desde `ListCapturedChargesForUpdateAsync`: el cambio de
        // estado que ya les aplicó el handler viaja en este mismo SaveChanges, dentro de la
        // transacción. Solo los que llegan sueltos —otro camino de lectura— hay que adjuntar.
        foreach (var c in invoicedCharges.Where(c => db.Entry(c).State == EntityState.Detached))
        {
            db.BillingCharges.Attach(c);
            db.Entry(c).Property(x => x.Status).IsModified = true;
            db.Entry(c).Property(x => x.UpdatedAt).IsModified = true;
        }

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
    }

    public async Task SaveVoidAsync(
        Invoice invoice, FiscalDocument document, CancellationToken ct = default)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        if (db.Entry(invoice).State == EntityState.Detached)
        {
            db.Invoices.Attach(invoice);
            db.Entry(invoice).Property(x => x.Status).IsModified = true;
            db.Entry(invoice).Property(x => x.Balance).IsModified = true;
            db.Entry(invoice).Property(x => x.UpdatedAt).IsModified = true;
            foreach (var linea in invoice.Lines)
            {
                db.Entry(linea).Property(x => x.Voided).IsModified = true;
            }
        }
        if (db.Entry(document).State == EntityState.Detached)
        {
            db.FiscalDocuments.Attach(document);
            db.Entry(document).Property(x => x.Status).IsModified = true;
            db.Entry(document).Property(x => x.VoidedAt).IsModified = true;
            db.Entry(document).Property(x => x.VoidReason).IsModified = true;
        }

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
    }
}
