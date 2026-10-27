using IMed.Api.Application.Inventory;
using IMed.Api.Domain.Inventory;
using IMed.Api.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace IMed.Api.Infrastructure.Inventory;

/// <summary>Transacción del inventario sobre el <see cref="AppDbContext"/> del request.</summary>
public sealed class InventoryUnitOfWork(AppDbContext db) : IInventoryUnitOfWork
{
    private const string UniqueViolation = "23505";

    public async Task<T> ExecuteAsync<T>(Func<CancellationToken, Task<T>> work, CancellationToken ct = default)
    {
        // Ya hay una transacción (otro módulo nos llamó dentro de la suya): nos unimos y la cierra
        // quien la abrió. EF guarda cada SaveChanges detrás de un savepoint, así que un choque de
        // índice único adentro no envenena la transacción de afuera.
        if (db.Database.CurrentTransaction is not null)
        {
            var r = await work(ct);
            await db.SaveChangesAsync(ct);
            return r;
        }

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        try
        {
            var resultado = await work(ct);
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return resultado;
        }
        catch
        {
            await tx.RollbackAsync(CancellationToken.None);
            // Lo rastreado refleja escrituras que la base acaba de deshacer: soltarlo evita que un
            // SaveChanges posterior del mismo request las reintente.
            db.ChangeTracker.Clear();
            throw;
        }
    }

    public Task SaveChangesAsync(CancellationToken ct = default) => db.SaveChangesAsync(ct);

    public void DiscardChanges() => db.ChangeTracker.Clear();

    public bool IsUniqueViolation(Exception ex)
    {
        for (var e = ex; e is not null; e = e.InnerException)
        {
            if (e is PostgresException { SqlState: UniqueViolation })
            {
                return true;
            }
        }
        return false;
    }
}

/// <summary>
/// 🔴 El libro de movimientos sobre Postgres (FSF §8, D13, P6).
///
/// <para><b>Cómo se evita vender la última caja dos veces.</b> Antes de mover nada se bloquea, con
/// <c>SELECT … FOR UPDATE</c> y en un orden fijo (bodega, artículo), la fila de costo de cada
/// artículo en cada bodega que la operación toca. Dos despachos simultáneos del mismo artículo se
/// atienden de a uno; el orden fijo evita que dos operaciones con varios artículos se bloqueen
/// mutuamente. Además, la salida descuenta el saldo con un <c>UPDATE … WHERE quantity_on_hand >=
/// cantidad</c>: si no alcanza, no afecta ninguna fila y la operación entera se deshace. La
/// restricción <c>quantity_on_hand >= 0</c> de la tabla es la última red.</para>
///
/// <para><b>Costeo.</b> Las entradas recalculan el promedio ponderado de la bodega; las salidas se
/// llevan el promedio vigente y no lo cambian (D13). La anulación de una compra lo deshace.</para>
/// </summary>
public sealed class StockLedger(AppDbContext db, TimeProvider time) : IStockLedger
{
    private sealed record CostRow(decimal QuantityOnHand, decimal AverageCost);

    public async Task<IReadOnlyList<StockMovement>> PostAsync(
        Guid organizationId,
        Guid groupId,
        string sourceKind,
        Guid sourceId,
        Guid? performedBy,
        IReadOnlyList<StockPosting> postings,
        CancellationToken ct = default)
    {
        if (db.Database.CurrentTransaction is null)
        {
            throw new InvalidOperationException(
                "El libro de inventario se escribe dentro de una transacción (IInventoryUnitOfWork).");
        }
        if (postings.Count == 0)
        {
            return [];
        }
        var now = time.GetUtcNow();

        await VerificarLotesAsync(postings, ct);

        // ── 1. Candados, en orden fijo ──
        var claves = postings.Select(p => (p.WarehouseId, p.StockItemId))
            .Concat(postings.Where(p => p.CostFromWarehouseId is not null).Select(p => (p.CostFromWarehouseId!.Value, p.StockItemId)))
            .Distinct()
            .OrderBy(k => k.Item1)
            .ThenBy(k => k.Item2)
            .ToList();
        var estado = new Dictionary<(Guid Warehouse, Guid Item), (decimal Qty, decimal Avg)>();
        foreach (var (bodega, item) in claves)
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO stock_item_cost (organization_id, warehouse_id, stock_item_id, quantity_on_hand, average_cost, updated_at)
                VALUES ({organizationId}, {bodega}, {item}, 0, 0, {now})
                ON CONFLICT (warehouse_id, stock_item_id) DO NOTHING
                """, ct);
            var fila = (await db.Database.SqlQuery<CostRow>($"""
                SELECT quantity_on_hand AS "QuantityOnHand", average_cost AS "AverageCost"
                FROM stock_item_cost
                WHERE warehouse_id = {bodega} AND stock_item_id = {item}
                FOR UPDATE
                """).ToListAsync(ct)).Single();
            estado[(bodega, item)] = (fila.QuantityOnHand, fila.AverageCost);
        }

        // ── 2. Cada pata, en el orden en que llegó ──
        var movimientos = new List<StockMovement>(postings.Count);
        foreach (var p in postings)
        {
            if (p.Quantity <= 0)
            {
                throw new ArgumentException("La cantidad de un movimiento tiene que ser positiva.");
            }
            var clave = (p.WarehouseId, p.StockItemId);
            var (qty, avg) = estado[clave];
            decimal costo;
            decimal nuevoPromedio;

            if (p.Direction == -1)
            {
                var afectadas = await db.Database.ExecuteSqlInterpolatedAsync($"""
                    UPDATE stock_balance
                    SET quantity_on_hand = quantity_on_hand - {p.Quantity}, updated_at = {now}
                    WHERE warehouse_id = {p.WarehouseId} AND stock_lot_id = {p.StockLotId} AND quantity_on_hand >= {p.Quantity}
                    """, ct);
                if (afectadas == 0)
                {
                    throw new InsufficientStockException(await FaltanteAsync(p, ct));
                }
                if (p.ReversesReceipt)
                {
                    costo = p.UnitCost ?? avg;
                    nuevoPromedio = StockItemCost.AverageAfterReceiptReversal(qty, avg, p.Quantity, costo);
                }
                else
                {
                    costo = avg;
                    nuevoPromedio = avg;
                }
                qty -= p.Quantity;
            }
            else if (p.Direction == 1)
            {
                costo = p.UnitCost
                    ?? (p.CostFromWarehouseId is { } origen ? estado[(origen, p.StockItemId)].Avg : avg);
                await db.Database.ExecuteSqlInterpolatedAsync($"""
                    INSERT INTO stock_balance (id, organization_id, warehouse_id, stock_item_id, stock_lot_id, quantity_on_hand, updated_at)
                    VALUES ({Guid.CreateVersion7(now)}, {organizationId}, {p.WarehouseId}, {p.StockItemId}, {p.StockLotId}, {p.Quantity}, {now})
                    ON CONFLICT (warehouse_id, stock_lot_id)
                    DO UPDATE SET quantity_on_hand = stock_balance.quantity_on_hand + EXCLUDED.quantity_on_hand,
                                  updated_at = EXCLUDED.updated_at
                    """, ct);
                nuevoPromedio = StockItemCost.AverageAfterReceipt(qty, avg, p.Quantity, costo);
                qty += p.Quantity;
            }
            else
            {
                throw new ArgumentOutOfRangeException(nameof(postings), "El sentido de un movimiento es +1 o −1.");
            }

            nuevoPromedio = Math.Round(nuevoPromedio, 6, MidpointRounding.AwayFromZero);
            estado[clave] = (qty, nuevoPromedio);

            var mov = StockMovement.Record(
                organizationId, groupId, p.Type, now, p.StockItemId, p.StockLotId, p.WarehouseId, p.Direction,
                p.Quantity, costo, qty, nuevoPromedio, sourceKind, sourceId, p.SourceLineId, performedBy,
                p.ReversalOfMovementId, p.Notes);
            db.StockMovements.Add(mov);
            movimientos.Add(mov);
        }

        // ── 3. El costo y la existencia por artículo y bodega, ya calculados ──
        foreach (var ((bodega, item), (qty, avg)) in estado)
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE stock_item_cost
                SET quantity_on_hand = {qty}, average_cost = {avg}, updated_at = {now}
                WHERE warehouse_id = {bodega} AND stock_item_id = {item}
                """, ct);
        }

        await db.SaveChangesAsync(ct);
        return movimientos;
    }

    public async Task<decimal> GetAverageCostAsync(Guid warehouseId, Guid stockItemId, CancellationToken ct = default) =>
        await db.StockItemCosts.AsNoTracking()
            .Where(c => c.WarehouseId == warehouseId && c.StockItemId == stockItemId)
            .Select(c => (decimal?)c.AverageCost)
            .FirstOrDefaultAsync(ct) ?? 0m;

    /// <summary>Un lote es de un artículo: moverlo con otro artículo corrompería los dos kardex.</summary>
    private async Task VerificarLotesAsync(IReadOnlyList<StockPosting> postings, CancellationToken ct)
    {
        var ids = postings.Select(p => p.StockLotId).Distinct().ToList();
        var lotes = await db.StockLots.AsNoTracking()
            .Where(l => ids.Contains(l.Id))
            .ToDictionaryAsync(l => l.Id, l => l.StockItemId, ct);
        foreach (var p in postings)
        {
            if (!lotes.TryGetValue(p.StockLotId, out var item) || item != p.StockItemId)
            {
                throw new InvalidOperationException("Un lote del movimiento no corresponde a su artículo.");
            }
        }
    }

    private async Task<string> FaltanteAsync(StockPosting p, CancellationToken ct)
    {
        var item = await db.StockItems.AsNoTracking().Where(i => i.Id == p.StockItemId)
            .Select(i => new { i.Name, i.BaseUnit }).FirstOrDefaultAsync(ct);
        var lote = await db.StockLots.AsNoTracking().Where(l => l.Id == p.StockLotId)
            .Select(l => l.LotCode).FirstOrDefaultAsync(ct);
        var bodega = await db.Warehouses.AsNoTracking().Where(w => w.Id == p.WarehouseId)
            .Select(w => w.Name).FirstOrDefaultAsync(ct);
        var hay = await db.StockBalances.AsNoTracking()
            .Where(b => b.WarehouseId == p.WarehouseId && b.StockLotId == p.StockLotId)
            .Select(b => (decimal?)b.QuantityOnHand).FirstOrDefaultAsync(ct) ?? 0m;
        var detalleLote = lote is null or StockLot.UntrackedCode ? string.Empty : $" (lote {lote})";
        return $"No alcanza la existencia de {item?.Name ?? "el artículo"}{detalleLote} en {bodega ?? "la bodega"}: " +
               $"hay {StockOutPlanner.Fmt(hay)} {item?.BaseUnit} y se pidieron {StockOutPlanner.Fmt(p.Quantity)}. " +
               "Puede que otra persona la haya despachado hace un momento.";
    }
}
