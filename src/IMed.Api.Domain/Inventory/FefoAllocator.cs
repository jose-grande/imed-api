namespace IMed.Api.Domain.Inventory;

/// <summary>Un lote con existencia en una bodega, tal como lo ve la salida.</summary>
public sealed record LotAvailability(
    Guid StockLotId, string LotCode, DateOnly? ExpiryDate, decimal Available, StockLotStatus Status, bool IsUntracked);

/// <summary>Cuánto sale de qué lote.</summary>
public sealed record LotAllocation(Guid StockLotId, decimal Quantity);

/// <summary>
/// 🔴 Salida FEFO (<i>first expired, first out</i>): propone primero el lote que vence antes (D8,
/// FSF §8).
///
/// <para><b>No confundir con el costeo.</b> El costo de la salida es el promedio ponderado (D13);
/// el lote físico que sale es el que vence primero. Son dos algoritmos independientes y los
/// sistemas que los mezclan terminan despachando el lote nuevo y venciendo el viejo en el estante.</para>
///
/// <para>Ni un lote vencido ni uno bloqueado se proponen ni se aceptan para despachar, vender o
/// consumir: un vencido se da de baja con un ajuste, y un bloqueado está retenido por algo.</para>
/// </summary>
public static class FefoAllocator
{
    /// <summary>Lotes que pueden salir hoy, en el orden en que deben salir.</summary>
    public static IReadOnlyList<LotAvailability> Order(IEnumerable<LotAvailability> lots, DateOnly today) =>
        lots.Where(l => l.Available > 0 && IsUsable(l, today))
            // Los que no vencen (lote técnico, insumos sin vencimiento) van al final.
            .OrderBy(l => l.ExpiryDate ?? DateOnly.MaxValue)
            .ThenBy(l => l.LotCode, StringComparer.Ordinal)
            .ToList();

    /// <summary>
    /// Reparte <paramref name="quantity"/> entre los lotes en orden FEFO. Devuelve null si no
    /// alcanza: la salida no se hace a medias (P6, sin existencias negativas).
    /// </summary>
    public static IReadOnlyList<LotAllocation>? Allocate(
        IEnumerable<LotAvailability> lots, decimal quantity, DateOnly today)
    {
        if (quantity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(quantity), "La cantidad tiene que ser positiva.");
        }
        var resultado = new List<LotAllocation>();
        var falta = quantity;
        foreach (var lote in Order(lots, today))
        {
            if (falta <= 0)
            {
                break;
            }
            var toma = Math.Min(falta, lote.Available);
            resultado.Add(new LotAllocation(lote.StockLotId, toma));
            falta -= toma;
        }
        return falta > 0 ? null : resultado;
    }

    /// <summary>Total que puede salir hoy.</summary>
    public static decimal Usable(IEnumerable<LotAvailability> lots, DateOnly today) =>
        lots.Where(l => l.Available > 0 && IsUsable(l, today)).Sum(l => l.Available);

    /// <summary>¿Este lote puede despacharse, venderse o consumirse hoy?</summary>
    public static bool IsUsable(LotAvailability lot, DateOnly today) =>
        lot.Status == StockLotStatus.Available && !(lot.ExpiryDate is { } v && v < today);
}

/// <summary>
/// No hay existencia suficiente para una salida (P6). Es un resultado esperado —dos personas
/// vendiendo la última caja— y no un fallo del sistema: lleva el mensaje para el usuario.
/// </summary>
public sealed class InsufficientStockException(string message) : Exception(message);
