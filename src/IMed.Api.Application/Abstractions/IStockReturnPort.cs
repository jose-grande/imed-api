namespace IMed.Api.Application.Abstractions;

/// <summary>
/// Una línea de producto que vuelve al inventario por una nota de crédito.
/// </summary>
/// <param name="CreditNoteId">Documento que origina la devolución: es la clave de idempotencia.</param>
/// <param name="ChargeId">Cargo original. Su origen (<c>PharmacyDispense</c>) dice de qué salida vino.</param>
/// <param name="ChargeSourceId">Id del acto que generó el cargo (el despacho o la venta).</param>
/// <param name="StockItemId">Artículo de inventario del cobrable.</param>
/// <param name="Quantity">Cantidad devuelta, en la unidad del cargo.</param>
/// <param name="LocationId">Sede donde se prestó el servicio: su bodega recibe la devolución.</param>
public sealed record StockReturnLine(
    Guid CreditNoteId,
    Guid ChargeId,
    string ChargeSourceId,
    Guid StockItemId,
    decimal Quantity,
    Guid LocationId);

/// <summary>
/// Costura entre la nota de crédito (facturación, Stream A) y el inventario (Stream B) —
/// plan de salida a producción, D10 y F0.5.
///
/// <para><b>Facturación no sabe de bodegas ni de lotes.</b> Le entrega a este puerto qué productos
/// se devolvieron y el inventario decide a qué bodega y a qué lote vuelven (al lote original de la
/// salida, si se puede rastrear).</para>
///
/// <para>Debe ser <b>idempotente</b> por <see cref="StockReturnLine.CreditNoteId"/> +
/// <see cref="StockReturnLine.ChargeId"/>: la nota de crédito puede reintentarse.</para>
/// </summary>
public interface IStockReturnPort
{
    Task ReturnFromCreditNoteAsync(
        Guid organizationId, IReadOnlyList<StockReturnLine> lines, CancellationToken ct = default);
}

/// <summary>
/// Implementación vacía mientras el inventario no exista. El Stream B la reemplaza en DI.
/// </summary>
public sealed class NoStockReturnPort : IStockReturnPort
{
    public Task ReturnFromCreditNoteAsync(
        Guid organizationId, IReadOnlyList<StockReturnLine> lines, CancellationToken ct = default) =>
        Task.CompletedTask;
}
