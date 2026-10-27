using IMed.Api.Application.Abstractions;
using IMed.Api.Domain.Inventory;

namespace IMed.Api.Application.Inventory;

/// <summary>Fuentes de los movimientos: dicen en el libro qué documento movió la existencia.</summary>
public static class StockSources
{
    public const string GoodsReceipt = "goods_receipt";
    public const string Transfer = "stock_transfer";
    public const string Consumption = "internal_consumption";
    public const string Adjustment = "stock_adjustment";
    public const string PharmacyDispense = "pharmacy_dispense";
    public const string CreditNote = "credit_note";
}

public sealed record GoodsReceiptLineInput(
    Guid StockItemId, string? LotCode, DateOnly? ExpiryDate, string? PresentationName, decimal Quantity, decimal UnitCost);

public sealed record GoodsReceiptInput(
    Guid? WarehouseId, Guid SupplierId, string SupplierInvoiceNumber, DateOnly SupplierInvoiceDate, string? Notes,
    IReadOnlyList<GoodsReceiptLineInput> Lines);

/// <summary>
/// Entradas de mercadería (D7) y su anulación.
///
/// <para>La entrada es el único lugar donde nacen lotes y vencimientos, así que aquí se valida en
/// serio: lote y vencimiento obligatorios en medicamentos y reactivos, ningún lote ya vencido, y el
/// mismo lote del mismo artículo siempre con el mismo vencimiento. Un vencimiento mal capturado
/// hace mentir al FEFO y a las alertas para siempre.</para>
/// </summary>
public sealed class GoodsReceiptHandlers(
    IInventoryRepository repo,
    IInventoryUnitOfWork uow,
    IStockLedger ledger,
    WarehouseResolver warehouses,
    IClinicClock clock)
{
    public async Task<Guid> CreateAsync(
        Guid organizationId, Guid? userId, GoodsReceiptInput input, CancellationToken ct = default)
    {
        if (input.Lines is not { Count: > 0 })
        {
            throw new ArgumentException("La entrada no tiene líneas.");
        }
        var bodega = await warehouses.ResolveAsync(organizationId, userId, input.WarehouseId, preferPharmacy: false, ct);
        var proveedor = await repo.GetSupplierAsync(organizationId, input.SupplierId, ct)
            ?? throw new ArgumentException("El proveedor no existe.");
        if (!proveedor.Active)
        {
            throw new ArgumentException($"El proveedor {proveedor.Name} está desactivado.");
        }
        if (await repo.ReceiptExistsAsync(organizationId, proveedor.Id, input.SupplierInvoiceNumber.Trim(), ct))
        {
            throw new InvalidOperationException(
                $"La factura {input.SupplierInvoiceNumber.Trim()} de {proveedor.Name} ya está registrada.");
        }
        var hoy = clock.Today();
        if (input.SupplierInvoiceDate > hoy)
        {
            throw new ArgumentException("La fecha de la factura del proveedor no puede ser futura.");
        }

        var items = (await repo.GetItemsAsync(organizationId, input.Lines.Select(l => l.StockItemId).Distinct().ToList(), ct))
            .ToDictionary(i => i.Id);

        return await uow.ExecuteAsync(async t =>
        {
            var entrada = GoodsReceipt.Create(
                organizationId, bodega.Id, proveedor.Id, input.SupplierInvoiceNumber, input.SupplierInvoiceDate,
                userId, input.Notes);

            var patas = new List<StockPosting>();
            foreach (var l in input.Lines)
            {
                if (!items.TryGetValue(l.StockItemId, out var item))
                {
                    throw new ArgumentException("Uno de los artículos no existe.");
                }
                if (!item.Active)
                {
                    throw new ArgumentException($"{item.Name} está desactivado.");
                }
                var lote = await LoteDeEntradaAsync(organizationId, item, l.LotCode, l.ExpiryDate, hoy, t);

                var factor = 1m;
                string? presentacion = null;
                if (!string.IsNullOrWhiteSpace(l.PresentationName))
                {
                    var p = item.Presentations.FirstOrDefault(x =>
                        string.Equals(x.Name, l.PresentationName.Trim(), StringComparison.OrdinalIgnoreCase))
                        ?? throw new ArgumentException($"{item.Name} no tiene la presentación \"{l.PresentationName}\".");
                    factor = p.FactorToBase;
                    presentacion = p.Name;
                }

                var linea = entrada.AddLine(item.Id, lote.Id, presentacion, l.Quantity, factor, l.UnitCost);
                patas.Add(new StockPosting(
                    StockMovementType.PurchaseReceipt, item.Id, lote.Id, bodega.Id, +1, linea.Quantity,
                    SourceLineId: linea.Id, UnitCost: linea.UnitCost));
            }

            await repo.AddReceiptAsync(entrada, t);
            await ledger.PostAsync(organizationId, entrada.Id, StockSources.GoodsReceipt, entrada.Id, userId, patas, t);
            return entrada.Id;
        }, ct);
    }

    /// <summary>
    /// Anula una entrada: sale lo que entró, del mismo lote y al mismo costo, y el promedio se
    /// recalcula como si la compra no hubiera existido. Si parte ya se despachó o se trasladó, no
    /// se puede: la existencia ya no está, y se corrige con un ajuste.
    /// </summary>
    public async Task<bool> VoidAsync(
        Guid organizationId, Guid? userId, Guid receiptId, string reason, CancellationToken ct = default)
    {
        var entrada = await repo.GetReceiptAsync(organizationId, receiptId, ct);
        if (entrada is null)
        {
            return false;
        }
        await warehouses.ResolveAsync(organizationId, userId, entrada.WarehouseId, preferPharmacy: false, ct);

        await uow.ExecuteAsync(async t =>
        {
            entrada.Void(userId, reason);
            var patas = entrada.Lines.Select(l => new StockPosting(
                StockMovementType.PurchaseReceiptVoid, l.StockItemId, l.StockLotId, entrada.WarehouseId, -1, l.Quantity,
                SourceLineId: l.Id, UnitCost: l.UnitCost, ReversesReceipt: true, Notes: reason)).ToList();
            try
            {
                await ledger.PostAsync(organizationId, Guid.CreateVersion7(), StockSources.GoodsReceipt, entrada.Id, userId, patas, t);
            }
            catch (InsufficientStockException)
            {
                throw new InsufficientStockException(
                    "Parte de lo que entró con esta factura ya salió de la bodega: la entrada no se puede anular. " +
                    "Corregí la diferencia con un ajuste.");
            }
            return true;
        }, ct);
        return true;
    }

    private async Task<StockLot> LoteDeEntradaAsync(
        Guid organizationId, StockItem item, string? lotCode, DateOnly? expiry, DateOnly hoy, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(lotCode))
        {
            if (item.RequiresLot)
            {
                throw new ArgumentException($"{item.Name} exige lote: capturalo como viene en la caja.");
            }
            return await repo.GetOrCreateUntrackedLotAsync(organizationId, item.Id, ct);
        }
        if (item.Kind is StockItemKind.Medication or StockItemKind.Reagent && expiry is null)
        {
            throw new ArgumentException($"El lote {lotCode.Trim()} de {item.Name} necesita su fecha de vencimiento.");
        }
        if (expiry is { } v && v < hoy)
        {
            throw new ArgumentException(
                $"El lote {lotCode.Trim()} de {item.Name} venció el {v:dd/MM/yyyy}: no se recibe mercadería vencida.");
        }
        var existente = await repo.FindLotAsync(item.Id, lotCode, ct);
        if (existente is not null && existente.ExpiryDate != expiry)
        {
            throw new ArgumentException(
                $"El lote {existente.LotCode} de {item.Name} ya está registrado con vencimiento " +
                $"{existente.ExpiryDate?.ToString("dd/MM/yyyy", System.Globalization.CultureInfo.InvariantCulture) ?? "sin fecha"}. Revisá la caja: un lote tiene un solo vencimiento.");
        }
        return existente ?? await repo.GetOrCreateLotAsync(organizationId, item.Id, lotCode, expiry, ct);
    }
}

public sealed record StockOutLineInput(Guid StockItemId, decimal Quantity, Guid? StockLotId);

public sealed record TransferInput(
    Guid FromWarehouseId, Guid ToWarehouseId, string? Notes, IReadOnlyList<StockOutLineInput> Lines);

public sealed record ConsumptionInput(
    Guid? WarehouseId, string Area, string? Notes, IReadOnlyList<StockOutLineInput> Lines);

/// <summary>Traslados y consumo interno (B1, D9).</summary>
public sealed class StockMovementHandlers(
    IInventoryRepository repo,
    IInventoryUnitOfWork uow,
    IStockLedger ledger,
    WarehouseResolver warehouses,
    StockOutPlanner planner,
    ILocationRepository locations)
{
    /// <summary>
    /// Traslado: sale de una bodega y entra a otra al mismo costo, en el mismo grupo del libro.
    /// Entre sedes de razones sociales distintas no hay traslado: para el SAR eso es una venta
    /// entre dos contribuyentes, con su factura.
    /// </summary>
    public async Task<Guid> TransferAsync(
        Guid organizationId, Guid? userId, TransferInput input, CancellationToken ct = default)
    {
        if (input.Lines is not { Count: > 0 })
        {
            throw new ArgumentException("El traslado no tiene líneas.");
        }
        var origen = await warehouses.ResolveAsync(organizationId, userId, input.FromWarehouseId, false, ct);
        var destino = await repo.GetWarehouseAsync(organizationId, input.ToWarehouseId, ct)
            ?? throw new ArgumentException("La bodega de destino no existe.");
        if (!destino.Active)
        {
            throw new ArgumentException($"La bodega {destino.Name} está desactivada.");
        }
        if (origen.LocationId != destino.LocationId)
        {
            var sedeOrigen = await locations.GetAsync(organizationId, origen.LocationId, ct);
            var sedeDestino = await locations.GetAsync(organizationId, destino.LocationId, ct);
            if (sedeOrigen?.LegalEntityId != sedeDestino?.LegalEntityId)
            {
                throw new InvalidOperationException(
                    "Las dos sedes facturan con razones sociales distintas: mover mercadería entre ellas es una venta, no un traslado.");
            }
        }

        var items = await ItemsAsync(organizationId, input.Lines, ct);
        return await uow.ExecuteAsync(async t =>
        {
            var traslado = StockTransfer.Create(organizationId, origen.Id, destino.Id, userId, input.Notes);
            var patas = new List<StockPosting>();
            foreach (var l in input.Lines)
            {
                var item = items[l.StockItemId];
                foreach (var a in await planner.PlanAsync(origen, item, l.Quantity, l.StockLotId, allowUnusable: true, t))
                {
                    var linea = traslado.AddLine(item.Id, a.StockLotId, a.Quantity);
                    patas.Add(new StockPosting(StockMovementType.TransferOut, item.Id, a.StockLotId, origen.Id, -1, a.Quantity, linea.Id));
                    patas.Add(new StockPosting(StockMovementType.TransferIn, item.Id, a.StockLotId, destino.Id, +1, a.Quantity, linea.Id,
                        CostFromWarehouseId: origen.Id));
                }
            }
            await repo.AddTransferAsync(traslado, t);
            var movs = await ledger.PostAsync(organizationId, traslado.Id, StockSources.Transfer, traslado.Id, userId, patas, t);
            foreach (var linea in traslado.Lines)
            {
                linea.SetUnitCost(movs.First(m => m.SourceLineId == linea.Id && m.Direction == -1).UnitCost);
            }
            return traslado.Id;
        }, ct);
    }

    /// <summary>Consumo interno (D9): sale por FEFO —o del lote elegido— al costo promedio.</summary>
    public async Task<Guid> ConsumeAsync(
        Guid organizationId, Guid? userId, ConsumptionInput input, CancellationToken ct = default)
    {
        if (input.Lines is not { Count: > 0 })
        {
            throw new ArgumentException("El consumo no tiene líneas.");
        }
        var bodega = await warehouses.ResolveAsync(organizationId, userId, input.WarehouseId, false, ct);
        var items = await ItemsAsync(organizationId, input.Lines, ct);
        return await uow.ExecuteAsync(async t =>
        {
            var consumo = InternalConsumption.Create(organizationId, bodega.Id, input.Area, input.Notes, userId);
            var patas = new List<StockPosting>();
            foreach (var l in input.Lines)
            {
                var item = items[l.StockItemId];
                foreach (var a in await planner.PlanAsync(bodega, item, l.Quantity, l.StockLotId, allowUnusable: false, t))
                {
                    var linea = consumo.AddLine(item.Id, a.StockLotId, a.Quantity);
                    patas.Add(new StockPosting(StockMovementType.InternalConsumption, item.Id, a.StockLotId, bodega.Id, -1, a.Quantity, linea.Id,
                        Notes: consumo.Area));
                }
            }
            await repo.AddConsumptionAsync(consumo, t);
            var movs = await ledger.PostAsync(organizationId, consumo.Id, StockSources.Consumption, consumo.Id, userId, patas, t);
            foreach (var linea in consumo.Lines)
            {
                linea.SetUnitCost(movs.First(m => m.SourceLineId == linea.Id).UnitCost);
            }
            return consumo.Id;
        }, ct);
    }

    private async Task<Dictionary<Guid, StockItem>> ItemsAsync(
        Guid organizationId, IReadOnlyList<StockOutLineInput> lines, CancellationToken ct)
    {
        var items = (await repo.GetItemsAsync(organizationId, lines.Select(l => l.StockItemId).Distinct().ToList(), ct))
            .ToDictionary(i => i.Id);
        foreach (var l in lines)
        {
            if (!items.ContainsKey(l.StockItemId))
            {
                throw new ArgumentException("Uno de los artículos no existe.");
            }
        }
        return items;
    }
}
