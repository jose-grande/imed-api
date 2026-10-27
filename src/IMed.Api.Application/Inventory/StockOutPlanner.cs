using IMed.Api.Application.Abstractions;
using IMed.Api.Domain.Inventory;

namespace IMed.Api.Application.Inventory;

/// <summary>
/// Decide de qué lotes sale una cantidad: del lote que el usuario eligió, o por FEFO si no eligió
/// ninguno (D8). Lo comparten el despacho, la venta, el consumo, el traslado y el ajuste.
///
/// <para>Valida antes de tocar el libro para poder dar un mensaje útil ("hay 4 tabletas en la
/// bodega de farmacia"); la garantía real la da el libro, que vuelve a comprobar con la fila
/// bloqueada.</para>
/// </summary>
public sealed class StockOutPlanner(IInventoryRepository repo, IClinicClock clock)
{
    /// <param name="allowUnusable">
    /// Acepta un lote vencido o bloqueado elegido a mano. Sirve para dar de baja un vencido (ajuste)
    /// o llevarlo a otra bodega (traslado); nunca para despachar, vender ni consumir.
    /// </param>
    public async Task<IReadOnlyList<LotAllocation>> PlanAsync(
        Warehouse warehouse, StockItem item, decimal quantity, Guid? stockLotId, bool allowUnusable,
        CancellationToken ct = default)
    {
        if (quantity <= 0)
        {
            throw new ArgumentException($"La cantidad de {item.Name} tiene que ser positiva.");
        }
        var hoy = clock.Today();
        var disponibles = await repo.GetAvailabilityAsync(warehouse.Id, item.Id, ct);

        if (stockLotId is { } loteId)
        {
            var lote = disponibles.FirstOrDefault(l => l.StockLotId == loteId);
            if (lote is null)
            {
                var existe = await repo.GetLotAsync(item.OrganizationId, loteId, ct);
                if (existe is null || existe.StockItemId != item.Id)
                {
                    throw new ArgumentException($"El lote indicado no es de {item.Name}.");
                }
                throw new InsufficientStockException(
                    $"El lote {existe.LotCode} de {item.Name} no tiene existencia en {warehouse.Name}.");
            }
            if (!allowUnusable && !FefoAllocator.IsUsable(lote, hoy))
            {
                throw new InvalidOperationException(lote.Status == StockLotStatus.Blocked
                    ? $"El lote {lote.LotCode} de {item.Name} está bloqueado."
                    : $"El lote {lote.LotCode} de {item.Name} venció el {lote.ExpiryDate:dd/MM/yyyy}: se da de baja con un ajuste.");
            }
            if (lote.Available < quantity)
            {
                throw new InsufficientStockException(
                    $"El lote {lote.LotCode} de {item.Name} tiene {Fmt(lote.Available)} {item.BaseUnit} en {warehouse.Name}; se pidieron {Fmt(quantity)}.");
            }
            return [new LotAllocation(lote.StockLotId, quantity)];
        }

        var reparto = FefoAllocator.Allocate(disponibles, quantity, hoy);
        if (reparto is null)
        {
            var usable = FefoAllocator.Usable(disponibles, hoy);
            var vencido = disponibles.Where(l => !FefoAllocator.IsUsable(l, hoy)).Sum(l => l.Available);
            var extra = vencido > 0 ? $" (además hay {Fmt(vencido)} vencidas o bloqueadas que no se pueden usar)" : string.Empty;
            throw new InsufficientStockException(
                $"No alcanza la existencia de {item.Name} en {warehouse.Name}: hay {Fmt(usable)} {item.BaseUnit} disponibles y se pidieron {Fmt(quantity)}{extra}.");
        }
        return reparto;
    }

    public static string Fmt(decimal v) => v.ToString("0.######", System.Globalization.CultureInfo.InvariantCulture);
}
