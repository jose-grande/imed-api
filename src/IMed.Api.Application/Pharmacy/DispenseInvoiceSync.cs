using IMed.Api.Application.Abstractions;
using IMed.Api.Application.Inventory;
using IMed.Api.Domain.Pharmacy;

namespace IMed.Api.Application.Pharmacy;

/// <summary>
/// Marca como facturadas las ventas libres cuyos cargos salieron en una factura emitida por otro
/// camino que la propia venta: típicamente, un producto vendido en la Caja junto con exámenes, que
/// factura el cobro de la cuenta (UX-Cobro-Anticipado F4).
///
/// <para>Sin esto la venta quedaría "sin factura" en Farmacia, con las opciones de facturarla otra
/// vez o de anularla —devolviendo mercadería ya cobrada—. Con la factura marcada, anular se rechaza
/// y la corrección va por nota de crédito, como cualquier venta facturada.</para>
/// </summary>
public sealed class DispenseInvoiceSync(IInventoryRepository repo, IInventoryUnitOfWork uow) : IInvoiceIssuedObserver
{
    public async Task OnInvoiceIssuedAsync(
        Guid organizationId, Guid accountId, Guid invoiceId, IReadOnlyCollection<Guid> chargeIds,
        CancellationToken ct = default)
    {
        if (chargeIds.Count == 0)
        {
            return;
        }
        var ventas = await repo.ListDispensesByAccountAsync(organizationId, accountId, ct);
        var cambio = false;
        foreach (var v in ventas.Where(v => v.Kind == PharmacyDispenseKind.OverTheCounter
                                            && v.InvoiceId is null
                                            && v.Status != PharmacyDispenseStatus.Cancelled))
        {
            if (v.Lines.Any(l => l.ChargeId is { } c && chargeIds.Contains(c)))
            {
                v.MarkInvoiced(invoiceId);
                cambio = true;
            }
        }
        if (cambio)
        {
            await uow.SaveChangesAsync(ct);
        }
    }
}
