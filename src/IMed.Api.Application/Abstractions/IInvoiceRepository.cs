using IMed.Api.Domain.Billing.Accounts;
using IMed.Api.Domain.Billing.Invoicing;

namespace IMed.Api.Application.Abstractions;

public interface IInvoiceRepository
{
    Task<Invoice?> GetAsync(Guid organizationId, Guid invoiceId, CancellationToken ct = default);

    /// <summary>
    /// Factura y su documento fiscal leídos <b>para modificarlos</b> — anular cambia los dos.
    /// Va aparte por la misma razón que en cargos: adjuntar una instancia distinta de una fila
    /// que el contexto ya rastrea es un conflicto de identidad, no un caso de negocio.
    /// </summary>
    Task<(Invoice? Invoice, FiscalDocument? Document)> GetForUpdateAsync(
        Guid organizationId, Guid invoiceId, CancellationToken ct = default);

    Task<FiscalDocument?> GetFiscalDocumentAsync(
        Guid organizationId, Guid invoiceId, CancellationToken ct = default);

    Task<IReadOnlyList<Invoice>> ListByAccountAsync(
        Guid organizationId, Guid accountId, CancellationToken ct = default);

    /// <summary>Busca por el número que el paciente trae escrito en el papel.</summary>
    Task<FiscalDocument?> FindByFullNumberAsync(
        Guid organizationId, string fullNumber, CancellationToken ct = default);

    /// <summary>Código de establecimiento fiscal de una sede, o null si no lo tiene.</summary>
    Task<string?> GetEstablishmentCodeAsync(
        Guid organizationId, Guid locationId, CancellationToken ct = default);

    /// <summary>
    /// Guarda la emisión completa —factura, documento fiscal y los cargos ya facturados— en
    /// <b>una transacción</b>.
    ///
    /// <para>Si esto se partiera en tres escrituras, una caída en el medio dejaría un correlativo
    /// consumido sin documento que lo explique, o una factura timbrada cuyos cargos siguen
    /// figurando como pendientes y se vuelven a facturar. Las dos son cosas que una auditoría
    /// encuentra y nadie puede justificar.</para>
    /// </summary>
    Task SaveIssuedAsync(
        Invoice invoice,
        FiscalDocument document,
        IReadOnlyList<BillingCharge> invoicedCharges,
        CancellationToken ct = default);

    Task SaveVoidAsync(Invoice invoice, FiscalDocument document, CancellationToken ct = default);
}
