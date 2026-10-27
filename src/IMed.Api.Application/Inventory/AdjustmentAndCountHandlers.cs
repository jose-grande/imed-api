using IMed.Api.Domain.Inventory;

namespace IMed.Api.Application.Inventory;

/// <param name="Direction">+1 suma (sobrante, donación), −1 resta (daño, vencido, pérdida).</param>
/// <param name="StockLotId">Lote existente. En una salida de un artículo con lote es obligatorio.</param>
/// <param name="LotCode">Lote nuevo de una entrada (con su <paramref name="ExpiryDate"/>).</param>
/// <param name="UnitCost">Costo de una entrada. Si no se da, entra al promedio vigente de la bodega.</param>
public sealed record AdjustmentLineInput(
    Guid StockItemId, int Direction, decimal Quantity, Guid? StockLotId, string? LotCode, DateOnly? ExpiryDate,
    decimal? UnitCost);

public sealed record AdjustmentInput(Guid? WarehouseId, string Reason, string? Notes, IReadOnlyList<AdjustmentLineInput> Lines);

/// <summary>Resultado de pedir un ajuste: aplicado ya, o esperando al gerente (P8).</summary>
public sealed record AdjustmentResult(Guid Id, string Status, decimal Value, decimal Threshold);

/// <summary>
/// Ajustes con motivo y aprobación por monto (P8).
///
/// <para>El valor que se compara con el umbral es el <b>absoluto a costo</b> de todas las líneas:
/// un faltante de L 3,000 compensado con un sobrante de L 2,500 no es un ajuste de L 500, son
/// L 5,500 de inventario que cambiaron de lugar en los libros, y eso lo firma el gerente.</para>
/// </summary>
public sealed class AdjustmentHandlers(
    IInventoryRepository repo,
    IInventoryUnitOfWork uow,
    IStockLedger ledger,
    WarehouseResolver warehouses,
    StockOutPlanner planner)
{
    public async Task<AdjustmentResult> CreateAsync(
        Guid organizationId, Guid? userId, bool canApprove, AdjustmentInput input, CancellationToken ct = default)
    {
        if (!Enum.TryParse<AdjustmentReason>(input.Reason, ignoreCase: true, out var motivo))
        {
            throw new ArgumentException("El motivo del ajuste es obligatorio y tiene que ser uno de la lista.");
        }
        if (input.Lines is not { Count: > 0 })
        {
            throw new ArgumentException("El ajuste no tiene líneas.");
        }
        var bodega = await warehouses.ResolveAsync(organizationId, userId, input.WarehouseId, false, ct);
        var ajuste = StockAdjustment.Create(organizationId, bodega.Id, motivo, input.Notes, userId);
        await ArmarLineasAsync(organizationId, bodega, ajuste, input.Lines, ct);
        return await EnviarAsync(organizationId, userId, canApprove, ajuste, ct);
    }

    public async Task<AdjustmentResult?> ApproveAsync(
        Guid organizationId, Guid? userId, Guid adjustmentId, CancellationToken ct = default)
    {
        var ajuste = await repo.GetAdjustmentAsync(organizationId, adjustmentId, ct);
        if (ajuste is null)
        {
            return null;
        }
        await warehouses.ResolveAsync(organizationId, userId, ajuste.WarehouseId, false, ct);
        var umbral = (await repo.GetSettingsAsync(organizationId, ct)).AdjustmentApprovalThreshold;
        await uow.ExecuteAsync(async t =>
        {
            ajuste.Approve(userId);
            await AplicarAsync(organizationId, userId, ajuste, t);
            return true;
        }, ct);
        return new AdjustmentResult(ajuste.Id, ajuste.Status.ToString(), ajuste.Value, umbral);
    }

    public async Task<AdjustmentResult?> RejectAsync(
        Guid organizationId, Guid? userId, Guid adjustmentId, string reason, CancellationToken ct = default)
    {
        var ajuste = await repo.GetAdjustmentAsync(organizationId, adjustmentId, ct);
        if (ajuste is null)
        {
            return null;
        }
        await warehouses.ResolveAsync(organizationId, userId, ajuste.WarehouseId, false, ct);
        ajuste.Reject(userId, reason);
        await uow.SaveChangesAsync(ct);
        var umbral = (await repo.GetSettingsAsync(organizationId, ct)).AdjustmentApprovalThreshold;
        return new AdjustmentResult(ajuste.Id, ajuste.Status.ToString(), ajuste.Value, umbral);
    }

    /// <summary>Envía un ajuste armado (lo usan la pantalla de ajustes y el cierre de un conteo).</summary>
    internal async Task<AdjustmentResult> EnviarAsync(
        Guid organizationId, Guid? userId, bool canApprove, StockAdjustment ajuste, CancellationToken ct)
    {
        var umbral = (await repo.GetSettingsAsync(organizationId, ct)).AdjustmentApprovalThreshold;
        await uow.ExecuteAsync(async t =>
        {
            var aplicar = ajuste.Submit(umbral, canApprove);
            await repo.AddAdjustmentAsync(ajuste, t);
            if (aplicar)
            {
                await AplicarAsync(organizationId, userId, ajuste, t);
            }
            return true;
        }, ct);
        return new AdjustmentResult(ajuste.Id, ajuste.Status.ToString(), ajuste.Value, umbral);
    }

    internal async Task ArmarLineasAsync(
        Guid organizationId, Warehouse bodega, StockAdjustment ajuste, IReadOnlyList<AdjustmentLineInput> lines,
        CancellationToken ct)
    {
        var items = (await repo.GetItemsAsync(organizationId, lines.Select(l => l.StockItemId).Distinct().ToList(), ct))
            .ToDictionary(i => i.Id);
        foreach (var l in lines)
        {
            if (!items.TryGetValue(l.StockItemId, out var item))
            {
                throw new ArgumentException("Uno de los artículos no existe.");
            }
            var promedio = await ledger.GetAverageCostAsync(bodega.Id, item.Id, ct);
            if (l.Direction == -1)
            {
                if (item.RequiresLot && l.StockLotId is null)
                {
                    throw new ArgumentException($"Indicá de qué lote de {item.Name} es el ajuste.");
                }
                var loteId = l.StockLotId ?? (await repo.GetOrCreateUntrackedLotAsync(organizationId, item.Id, ct)).Id;
                foreach (var a in await planner.PlanAsync(bodega, item, l.Quantity, loteId, allowUnusable: true, ct))
                {
                    ajuste.AddLine(item.Id, a.StockLotId, -1, a.Quantity, promedio);
                }
            }
            else if (l.Direction == 1)
            {
                StockLot lote;
                if (l.StockLotId is { } id)
                {
                    lote = await repo.GetLotAsync(organizationId, id, ct) is { } x && x.StockItemId == item.Id
                        ? x : throw new ArgumentException($"El lote indicado no es de {item.Name}.");
                }
                else if (!string.IsNullOrWhiteSpace(l.LotCode))
                {
                    if (item.Kind is StockItemKind.Medication or StockItemKind.Reagent && l.ExpiryDate is null)
                    {
                        throw new ArgumentException($"El lote {l.LotCode.Trim()} de {item.Name} necesita su vencimiento.");
                    }
                    var existente = await repo.FindLotAsync(item.Id, l.LotCode, ct);
                    if (existente is not null && l.ExpiryDate is not null && existente.ExpiryDate != l.ExpiryDate)
                    {
                        throw new ArgumentException(
                            $"El lote {existente.LotCode} ya está registrado con otro vencimiento.");
                    }
                    lote = existente ?? await repo.GetOrCreateLotAsync(organizationId, item.Id, l.LotCode, l.ExpiryDate, ct);
                }
                else if (!item.RequiresLot)
                {
                    lote = await repo.GetOrCreateUntrackedLotAsync(organizationId, item.Id, ct);
                }
                else
                {
                    throw new ArgumentException($"{item.Name} exige lote: indicá el lote que se encontró.");
                }

                var costo = l.UnitCost ?? promedio;
                if (l.UnitCost is null && promedio == 0)
                {
                    throw new ArgumentException(
                        $"{item.Name} nunca tuvo costo en {bodega.Name}: indicá el costo unitario del sobrante.");
                }
                ajuste.AddLine(item.Id, lote.Id, +1, l.Quantity, costo);
            }
            else
            {
                throw new ArgumentException("Cada línea del ajuste suma (+1) o resta (−1).");
            }
        }
    }

    private async Task AplicarAsync(Guid organizationId, Guid? userId, StockAdjustment ajuste, CancellationToken ct)
    {
        var patas = ajuste.Lines.Select(l => new StockPosting(
            l.Direction == 1 ? StockMovementType.AdjustmentIn : StockMovementType.AdjustmentOut,
            l.StockItemId, l.StockLotId, ajuste.WarehouseId, l.Direction, l.Quantity, l.Id,
            UnitCost: l.Direction == 1 ? l.UnitCost : null,
            Notes: ajuste.Reason.ToString())).ToList();
        var movs = await ledger.PostAsync(organizationId, ajuste.Id, StockSources.Adjustment, ajuste.Id, userId, patas, ct);
        // La salida se registra al costo real del momento de aplicarse (pudo cambiar mientras esperaba).
        foreach (var l in ajuste.Lines.Where(x => x.Direction == -1))
        {
            l.SetUnitCost(movs.First(m => m.SourceLineId == l.Id).UnitCost);
        }
    }
}

public sealed record CountRecordInput(IReadOnlyList<CountRecordLine> Lines);

public sealed record CountRecordLine(Guid LineId, decimal? CountedQuantity);

public sealed record CountAddLineInput(Guid StockItemId, Guid? StockLotId, string? LotCode, DateOnly? ExpiryDate);

/// <summary>Resultado de cerrar un conteo: el ajuste que generó, si hubo diferencias.</summary>
public sealed record CountCloseResult(Guid CountId, AdjustmentResult? Adjustment);

/// <summary>Conteo físico (B1): abrir, capturar, cerrar con ajuste ligado.</summary>
public sealed class StockCountHandlers(
    IInventoryRepository repo,
    IInventoryUnitOfWork uow,
    IStockLedger ledger,
    WarehouseResolver warehouses,
    AdjustmentHandlers adjustments)
{
    public async Task<Guid> StartAsync(
        Guid organizationId, Guid? userId, Guid? warehouseId, string? notes, CancellationToken ct = default)
    {
        var bodega = await warehouses.ResolveAsync(organizationId, userId, warehouseId, false, ct);
        var conteo = StockCount.Start(organizationId, bodega.Id, notes, userId);
        foreach (var (item, lote, cantidad) in await repo.GetWarehouseBalancesAsync(bodega.Id, ct))
        {
            if (cantidad > 0)
            {
                conteo.AddLine(item, lote, cantidad);
            }
        }
        await repo.AddCountAsync(conteo, ct);
        try
        {
            await uow.SaveChangesAsync(ct);
        }
        catch (Exception ex) when (uow.IsUniqueViolation(ex))
        {
            uow.DiscardChanges();
            throw new InvalidOperationException($"{bodega.Name} ya tiene un conteo abierto: terminalo o cancelalo primero.");
        }
        return conteo.Id;
    }

    /// <summary>Agrega al conteo un lote que no estaba en el sistema (se encontró en el estante).</summary>
    public async Task<bool> AddLineAsync(
        Guid organizationId, Guid? userId, Guid countId, CountAddLineInput input, CancellationToken ct = default)
    {
        var conteo = await repo.GetCountAsync(organizationId, countId, ct);
        if (conteo is null)
        {
            return false;
        }
        await warehouses.ResolveAsync(organizationId, userId, conteo.WarehouseId, false, ct);
        var item = await repo.GetItemAsync(organizationId, input.StockItemId, ct)
            ?? throw new ArgumentException("El artículo no existe.");
        StockLot lote;
        if (input.StockLotId is { } id)
        {
            lote = await repo.GetLotAsync(organizationId, id, ct) is { } x && x.StockItemId == item.Id
                ? x : throw new ArgumentException("El lote no es de ese artículo.");
        }
        else if (!string.IsNullOrWhiteSpace(input.LotCode))
        {
            lote = await repo.FindLotAsync(item.Id, input.LotCode, ct)
                ?? await repo.GetOrCreateLotAsync(organizationId, item.Id, input.LotCode, input.ExpiryDate, ct);
        }
        else if (!item.RequiresLot)
        {
            lote = await repo.GetOrCreateUntrackedLotAsync(organizationId, item.Id, ct);
        }
        else
        {
            throw new ArgumentException($"{item.Name} exige lote.");
        }
        if (conteo.Lines.Any(l => l.StockLotId == lote.Id))
        {
            throw new InvalidOperationException("Ese lote ya está en el conteo.");
        }
        conteo.AddLine(item.Id, lote.Id, await repo.GetLotBalanceAsync(conteo.WarehouseId, lote.Id, ct));
        await uow.SaveChangesAsync(ct);
        return true;
    }

    public async Task<bool> RecordAsync(
        Guid organizationId, Guid? userId, Guid countId, CountRecordInput input, CancellationToken ct = default)
    {
        var conteo = await repo.GetCountAsync(organizationId, countId, ct);
        if (conteo is null)
        {
            return false;
        }
        await warehouses.ResolveAsync(organizationId, userId, conteo.WarehouseId, false, ct);
        foreach (var l in input.Lines)
        {
            conteo.Record(l.LineId, l.CountedQuantity);
        }
        await uow.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>
    /// Cierra el conteo. La diferencia es contra la existencia de este momento; si hay, genera un
    /// ajuste "diferencia de conteo" que pasa por la regla de aprobación (P8) como cualquier otro.
    /// </summary>
    public async Task<CountCloseResult?> CloseAsync(
        Guid organizationId, Guid? userId, bool canApprove, Guid countId, CancellationToken ct = default)
    {
        var conteo = await repo.GetCountAsync(organizationId, countId, ct);
        if (conteo is null)
        {
            return null;
        }
        var bodega = await warehouses.ResolveAsync(organizationId, userId, conteo.WarehouseId, false, ct);
        if (conteo.Lines.Any(l => l.CountedQuantity is null))
        {
            throw new InvalidOperationException("Faltan líneas por contar. Si un lote no está en el estante, contalo en cero.");
        }

        var lineas = new List<AdjustmentLineInput>();
        foreach (var l in conteo.Lines)
        {
            var actual = await repo.GetLotBalanceAsync(bodega.Id, l.StockLotId, ct);
            var diferencia = l.CountedQuantity!.Value - actual;
            if (diferencia == 0)
            {
                continue;
            }
            decimal? costo = diferencia > 0 && await ledger.GetAverageCostAsync(bodega.Id, l.StockItemId, ct) == 0 ? 0m : null;
            lineas.Add(new AdjustmentLineInput(
                l.StockItemId, diferencia > 0 ? 1 : -1, Math.Abs(diferencia), l.StockLotId, null, null, costo));
        }

        AdjustmentResult? resultado = null;
        if (lineas.Count > 0)
        {
            var ajuste = StockAdjustment.Create(
                organizationId, bodega.Id, AdjustmentReason.CountDifference,
                $"Conteo físico del {conteo.StartedAt:dd/MM/yyyy}", userId, conteo.Id);
            await adjustments.ArmarLineasAsync(organizationId, bodega, ajuste, lineas, ct);
            conteo.Close(userId, ajuste.Id);
            resultado = await adjustments.EnviarAsync(organizationId, userId, canApprove, ajuste, ct);
        }
        else
        {
            conteo.Close(userId, null);
            await uow.SaveChangesAsync(ct);
        }
        return new CountCloseResult(conteo.Id, resultado);
    }

    public async Task<bool> CancelAsync(Guid organizationId, Guid? userId, Guid countId, CancellationToken ct = default)
    {
        var conteo = await repo.GetCountAsync(organizationId, countId, ct);
        if (conteo is null)
        {
            return false;
        }
        await warehouses.ResolveAsync(organizationId, userId, conteo.WarehouseId, false, ct);
        conteo.Cancel(userId);
        await uow.SaveChangesAsync(ct);
        return true;
    }
}
