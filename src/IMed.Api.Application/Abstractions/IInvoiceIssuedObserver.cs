namespace IMed.Api.Application.Abstractions;

/// <summary>
/// Aviso de que se emitió una factura de una cuenta, con los cargos que tomó.
///
/// <para><b>Para qué existe.</b> Algunos módulos guardan su propio "ya se facturó": la venta libre
/// de farmacia, por ejemplo, para no dejar anular lo cobrado. Hasta ahora lo marcaba ella misma al
/// emitir; cuando un producto se vende en la Caja y lo factura el cobro de la cuenta, nadie se lo
/// decía. Con este aviso, cualquier emisión —Caja, Facturas, Cuentas— lo mantiene al día sin que
/// facturación dependa de esos módulos.</para>
///
/// <para>Se avisa después de guardar la factura. Un observador que falla no deshace la emisión: la
/// factura ya tiene número fiscal.</para>
/// </summary>
public interface IInvoiceIssuedObserver
{
    Task OnInvoiceIssuedAsync(
        Guid organizationId, Guid accountId, Guid invoiceId, IReadOnlyCollection<Guid> chargeIds,
        CancellationToken ct = default);
}
