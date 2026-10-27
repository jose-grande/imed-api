using IMed.Api.Domain.Billing.Invoicing;

namespace IMed.Api.Application.Abstractions;

/// <summary>Notas de crédito (A9).</summary>
public interface ICreditNoteRepository
{
    Task<CreditNote?> GetAsync(Guid organizationId, Guid creditNoteId, CancellationToken ct = default);

    Task<CreditNote?> GetForUpdateAsync(Guid organizationId, Guid creditNoteId, CancellationToken ct = default);

    Task<IReadOnlyList<CreditNote>> ListByInvoiceAsync(Guid organizationId, Guid invoiceId, CancellationToken ct = default);

    /// <summary>Cantidad ya acreditada por línea de factura, sumando todas las notas emitidas.</summary>
    Task<IReadOnlyDictionary<Guid, decimal>> CreditedQuantitiesAsync(
        Guid organizationId, Guid invoiceId, CancellationToken ct = default);

    /// <summary>
    /// Guarda la nota emitida, su documento fiscal y el nuevo saldo de la factura en una sola
    /// transacción: o queda todo o no queda nada (el correlativo ya se reservó aparte).
    /// </summary>
    Task SaveIssuedAsync(CreditNote creditNote, FiscalDocument document, Invoice invoice, CancellationToken ct = default);

    Task SaveAsync(CancellationToken ct = default);
}
