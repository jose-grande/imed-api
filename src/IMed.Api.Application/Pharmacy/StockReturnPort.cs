using IMed.Api.Application.Abstractions;
using IMed.Api.Application.Inventory;
using IMed.Api.Domain.Inventory;
using IMed.Api.Domain.Pharmacy;
using Microsoft.Extensions.Logging;

namespace IMed.Api.Application.Pharmacy;

/// <summary>
/// Devolución al inventario por nota de crédito (B6, D10): implementa la costura
/// <see cref="IStockReturnPort"/> que facturación llama al emitir una nota de crédito con productos.
///
/// <para><b>Al lote original, a su mismo costo.</b> El cargo de un despacho o una venta nace con su
/// línea de entrega como origen (<c>PharmacyDispense</c> + id de la línea), y esa línea sabe de qué
/// lotes salió y a qué costo. La devolución entra a esos lotes, en la bodega de donde salieron, y no
/// mueve el promedio de forma arbitraria: entra a lo que costó.</para>
///
/// <para><b>Idempotente por nota de crédito + cargo.</b> La constancia (<see cref="StockReturnRecord"/>)
/// tiene índice único: si la nota de crédito se reintenta, la línea ya procesada se salta. Y nunca
/// vuelve más de lo que salió: cada porción de lote lleva su cantidad devuelta.</para>
///
/// <para><b>Lo que no se puede rastrear no se inventa.</b> Un producto cobrado a mano (sin despacho)
/// de un artículo con lote no dice a qué lote vuelve: se deja constancia sin mover inventario y se
/// avisa en el registro, para que bodega lo ingrese con un ajuste.</para>
/// </summary>
public sealed class StockReturnPort(
    IInventoryRepository repo,
    IInventoryUnitOfWork uow,
    IStockLedger ledger,
    ILogger<StockReturnPort> logger) : IStockReturnPort
{
    public async Task ReturnFromCreditNoteAsync(
        Guid organizationId, IReadOnlyList<StockReturnLine> lines, CancellationToken ct = default)
    {
        foreach (var linea in lines)
        {
            if (linea.Quantity <= 0)
            {
                continue;
            }
            if (await repo.ReturnRecordExistsAsync(organizationId, linea.CreditNoteId, linea.ChargeId, ct))
            {
                continue;
            }
            try
            {
                await uow.ExecuteAsync(t => DevolverAsync(organizationId, linea, t), ct);
            }
            catch (Exception ex) when (uow.IsUniqueViolation(ex))
            {
                // Otro intento de la misma nota de crédito la procesó a la vez: ya está hecha.
            }
        }
    }

    private async Task<bool> DevolverAsync(Guid organizationId, StockReturnLine linea, CancellationToken ct)
    {
        var despacho = Guid.TryParse(linea.ChargeSourceId, out var lineaId)
            ? await repo.FindDispenseByLineAsync(organizationId, lineaId, ct)
            : null;
        var entregada = despacho?.Lines.FirstOrDefault(l => l.Id == lineaId);

        if (despacho is null || entregada is null || entregada.StockItemId != linea.StockItemId)
        {
            await SinRastroAsync(organizationId, linea, ct);
            return true;
        }

        // Se devuelve primero lo que salió último (el lote de vencimiento más lejano queda en el
        // estante): así el lote que vence antes sigue saliendo primero por FEFO.
        var falta = linea.Quantity;
        var patas = new List<StockPosting>();
        foreach (var porcion in entregada.Lots.Reverse())
        {
            if (falta <= 0)
            {
                break;
            }
            var toma = Math.Min(falta, porcion.Returnable);
            if (toma <= 0)
            {
                continue;
            }
            porcion.RegisterReturn(toma);
            patas.Add(new StockPosting(
                StockMovementType.CustomerReturn, entregada.StockItemId, porcion.StockLotId, despacho.WarehouseId, +1, toma,
                entregada.Id, UnitCost: porcion.UnitCost, Notes: $"Nota de crédito {linea.CreditNoteId}"));
            falta -= toma;
        }

        var devuelto = linea.Quantity - falta;
        if (patas.Count > 0)
        {
            await ledger.PostAsync(organizationId, Guid.CreateVersion7(), StockSources.CreditNote, linea.CreditNoteId, null, patas, ct);
        }
        var nota = falta > 0
            ? $"Solo se podían devolver {StockOutPlanner.Fmt(devuelto)}: el resto ya había vuelto con otra nota de crédito."
            : null;
        if (nota is not null)
        {
            logger.LogWarning("Nota de crédito {CreditNoteId}, cargo {ChargeId}: {Nota}", linea.CreditNoteId, linea.ChargeId, nota);
        }
        await repo.AddReturnRecordAsync(StockReturnRecord.Create(
            organizationId, linea.CreditNoteId, linea.ChargeId, linea.StockItemId, linea.Quantity, devuelto, nota), ct);
        await uow.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>
    /// Producto sin despacho de origen: si el artículo no exige lote, vuelve al lote técnico de la
    /// bodega general de la sede; si exige lote, no hay a cuál volver y queda solo la constancia.
    /// </summary>
    private async Task SinRastroAsync(Guid organizationId, StockReturnLine linea, CancellationToken ct)
    {
        var item = await repo.GetItemAsync(organizationId, linea.StockItemId, ct);
        var bodega = (await repo.ListWarehousesAsync(organizationId, linea.LocationId, activeOnly: true, ct))
            .FirstOrDefault(w => w.IsGeneral);
        decimal devuelto = 0;
        string nota;
        if (item is not null && !item.RequiresLot && bodega is not null)
        {
            var lote = await repo.GetOrCreateUntrackedLotAsync(organizationId, item.Id, ct);
            var costo = await ledger.GetAverageCostAsync(bodega.Id, item.Id, ct);
            await ledger.PostAsync(organizationId, Guid.CreateVersion7(), StockSources.CreditNote, linea.CreditNoteId, null,
                [new StockPosting(StockMovementType.CustomerReturn, item.Id, lote.Id, bodega.Id, +1, linea.Quantity,
                    UnitCost: costo, Notes: $"Nota de crédito {linea.CreditNoteId}")], ct);
            devuelto = linea.Quantity;
            nota = "El cargo no venía de un despacho: volvió a la bodega general de la sede.";
        }
        else
        {
            nota = "El cargo no venía de un despacho y el artículo exige lote: no se sabe a qué lote vuelve. " +
                   "Bodega lo ingresa con un ajuste.";
            logger.LogWarning(
                "Nota de crédito {CreditNoteId}, cargo {ChargeId}: devolución sin rastro de lote ({ItemId}). {Nota}",
                linea.CreditNoteId, linea.ChargeId, linea.StockItemId, nota);
        }
        await repo.AddReturnRecordAsync(StockReturnRecord.Create(
            organizationId, linea.CreditNoteId, linea.ChargeId, linea.StockItemId, linea.Quantity, devuelto, nota), ct);
        await uow.SaveChangesAsync(ct);
    }
}
