using IMed.Api.Application.Abstractions;
using IMed.Api.Application.Locations;
using IMed.Api.Domain.Inventory;
using Microsoft.Extensions.Logging;

namespace IMed.Api.Application.Inventory;

/// <summary>
/// La bodega general de cada sede (P7).
///
/// <para><b>Perezosa e idempotente, no un gancho en el alta de sedes.</b> La creación de sedes es
/// de otro módulo; en vez de tocarla, el inventario asegura la bodega general cada vez que la
/// necesita (al listar bodegas, al operar en una sede) y un backfill de arranque la crea para todas
/// las sedes existentes. Una sede nueva tiene su bodega general la primera vez que alguien mira el
/// inventario, sin que nadie se acuerde de crearla.</para>
/// </summary>
public sealed class WarehouseProvisioner(
    IInventoryRepository repo,
    ILocationRepository locations,
    ILogger<WarehouseProvisioner> logger)
{
    public async Task<int> EnsureGeneralWarehousesAsync(Guid organizationId, CancellationToken ct = default)
    {
        var sedes = await locations.ListAsync(organizationId, activeOnly: false, ct);
        if (sedes.Count == 0)
        {
            return 0;
        }
        var generales = (await repo.ListWarehousesAsync(organizationId, null, activeOnly: false, ct))
            .Where(w => w.IsGeneral)
            .Select(w => w.LocationId)
            .ToHashSet();

        var creadas = 0;
        foreach (var sede in sedes.Where(s => !generales.Contains(s.Id)))
        {
            for (var intento = 0; intento < 3; intento++)
            {
                var codigo = intento == 0 ? sede.Code : $"{sede.Code}-{intento + 1}";
                var bodega = Warehouse.CreateGeneral(organizationId, sede.Id, codigo, sede.Name);
                if (await repo.TryAddGeneralWarehouseAsync(bodega, ct))
                {
                    creadas++;
                    break;
                }
                // Chocó: o la creó otro proceso (listo) o su código ya lo usa una bodega manual.
                var ahora = await repo.ListWarehousesAsync(organizationId, sede.Id, activeOnly: false, ct);
                if (ahora.Any(w => w.IsGeneral))
                {
                    break;
                }
            }
        }
        if (creadas > 0)
        {
            logger.LogInformation("Org {OrgId}: {Count} bodega(s) general(es) creada(s).", organizationId, creadas);
        }
        return creadas;
    }
}

/// <summary>
/// Decide en qué bodega opera un acto y verifica que el usuario pueda operar ahí.
///
/// <para>Una bodega es de una sede, y el usuario solo opera en sus sedes (ADR-008): pedir una
/// bodega de otra sede pasa por <see cref="ResolveActiveLocationHandler"/>, que la rechaza si no la
/// tiene asignada. Sin bodega explícita se toma la de la sede activa: la de farmacia para despachar
/// y vender, la general para lo demás.</para>
/// </summary>
public sealed class WarehouseResolver(
    IInventoryRepository repo,
    WarehouseProvisioner provisioner,
    ResolveActiveLocationHandler activeLocation)
{
    public async Task<Warehouse> ResolveAsync(
        Guid organizationId, Guid? userId, Guid? warehouseId, bool preferPharmacy, CancellationToken ct = default)
    {
        if (warehouseId is { } id)
        {
            var bodega = await repo.GetWarehouseAsync(organizationId, id, ct)
                ?? throw new ArgumentException("La bodega no existe.", nameof(warehouseId));
            if (!bodega.Active)
            {
                throw new ArgumentException($"La bodega {bodega.Name} está desactivada.", nameof(warehouseId));
            }
            // Lanza si el usuario no tiene asignada la sede de la bodega.
            await activeLocation.HandleAsync(organizationId, userId, bodega.LocationId, ct);
            return bodega;
        }

        var sede = await activeLocation.HandleAsync(organizationId, userId, null, ct);
        await provisioner.EnsureGeneralWarehousesAsync(organizationId, ct);
        var bodegas = await repo.ListWarehousesAsync(organizationId, sede, activeOnly: true, ct);
        var elegida = (preferPharmacy ? bodegas.FirstOrDefault(w => w.Kind == WarehouseKind.Pharmacy) : null)
            ?? bodegas.FirstOrDefault(w => w.IsGeneral)
            ?? throw new InvalidOperationException("La sede activa no tiene bodega general.");
        return elegida;
    }
}

public sealed record WarehouseInput(Guid? LocationId, string? Code, string Name, string Kind, bool Active);

public sealed class WarehouseHandlers(
    IInventoryRepository repo,
    IInventoryUnitOfWork uow,
    WarehouseProvisioner provisioner,
    ILocationRepository locations,
    ResolveActiveLocationHandler activeLocation)
{
    public async Task<IReadOnlyList<WarehouseDto>> ListAsync(
        Guid organizationId, Guid? locationId, bool activeOnly, CancellationToken ct = default)
    {
        await provisioner.EnsureGeneralWarehousesAsync(organizationId, ct);
        var sedes = (await locations.ListAsync(organizationId, activeOnly: false, ct)).ToDictionary(s => s.Id, s => s.Name);
        var bodegas = await repo.ListWarehousesAsync(organizationId, locationId, activeOnly, ct);
        return bodegas
            .OrderBy(w => sedes.GetValueOrDefault(w.LocationId))
            .ThenByDescending(w => w.IsGeneral)
            .ThenBy(w => w.Name)
            .Select(w => ToDto(w, sedes.GetValueOrDefault(w.LocationId)))
            .ToList();
    }

    public async Task<WarehouseDto> CreateAsync(
        Guid organizationId, Guid? userId, WarehouseInput input, CancellationToken ct = default)
    {
        if (!Enum.TryParse<WarehouseKind>(input.Kind, ignoreCase: true, out var tipo))
        {
            throw new ArgumentException("Tipo de bodega desconocido.", nameof(input));
        }
        var sede = await activeLocation.HandleAsync(organizationId, userId, input.LocationId, ct);
        var codigo = (input.Code ?? string.Empty).Trim().ToUpperInvariant();
        if (await repo.WarehouseCodeExistsAsync(organizationId, codigo, ct))
        {
            throw new ArgumentException($"Ya existe una bodega con el código {codigo}.", nameof(input));
        }
        var bodega = Warehouse.Create(organizationId, sede, codigo, input.Name, tipo);
        await repo.AddWarehouseAsync(bodega, ct);
        await uow.SaveChangesAsync(ct);
        var nombreSede = (await locations.GetAsync(organizationId, sede, ct))?.Name;
        return ToDto(bodega, nombreSede);
    }

    public async Task<WarehouseDto?> UpdateAsync(
        Guid organizationId, Guid warehouseId, WarehouseInput input, CancellationToken ct = default)
    {
        if (!Enum.TryParse<WarehouseKind>(input.Kind, ignoreCase: true, out var tipo))
        {
            throw new ArgumentException("Tipo de bodega desconocido.", nameof(input));
        }
        var bodega = await repo.GetWarehouseAsync(organizationId, warehouseId, ct);
        if (bodega is null)
        {
            return null;
        }
        bodega.Update(input.Name, tipo, input.Active);
        await repo.UpdateWarehouseAsync(bodega, ct);
        await uow.SaveChangesAsync(ct);
        var nombreSede = (await locations.GetAsync(organizationId, bodega.LocationId, ct))?.Name;
        return ToDto(bodega, nombreSede);
    }

    public static WarehouseDto ToDto(Warehouse w, string? locationName) =>
        new(w.Id, w.LocationId, locationName, w.Code, w.Name, w.Kind.ToString(), w.IsGeneral, w.Active);
}

public sealed record SupplierInput(string Name, string? TaxId, string? ContactName, string? Phone, string? Email, bool Active);

public sealed class SupplierHandlers(IInventoryRepository repo, IInventoryUnitOfWork uow)
{
    public async Task<IReadOnlyList<SupplierDto>> ListAsync(
        Guid organizationId, string? term, bool activeOnly, CancellationToken ct = default) =>
        (await repo.ListSuppliersAsync(organizationId, term, activeOnly, ct)).Select(ToDto).ToList();

    public async Task<SupplierDto> CreateAsync(Guid organizationId, SupplierInput input, CancellationToken ct = default)
    {
        await ValidarRtnAsync(organizationId, input.TaxId, null, ct);
        var proveedor = Supplier.Create(organizationId, input.Name, input.TaxId, input.ContactName, input.Phone, input.Email);
        await repo.AddSupplierAsync(proveedor, ct);
        await uow.SaveChangesAsync(ct);
        return ToDto(proveedor);
    }

    public async Task<SupplierDto?> UpdateAsync(
        Guid organizationId, Guid supplierId, SupplierInput input, CancellationToken ct = default)
    {
        var proveedor = await repo.GetSupplierAsync(organizationId, supplierId, ct);
        if (proveedor is null)
        {
            return null;
        }
        await ValidarRtnAsync(organizationId, input.TaxId, supplierId, ct);
        proveedor.Update(input.Name, input.TaxId, input.ContactName, input.Phone, input.Email, input.Active);
        await repo.UpdateSupplierAsync(proveedor, ct);
        await uow.SaveChangesAsync(ct);
        return ToDto(proveedor);
    }

    private async Task ValidarRtnAsync(Guid organizationId, string? taxId, Guid? exceptId, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(taxId) && await repo.SupplierTaxIdExistsAsync(organizationId, taxId.Trim(), exceptId, ct))
        {
            throw new ArgumentException($"Ya hay un proveedor con el RTN {taxId.Trim()}.", nameof(taxId));
        }
    }

    public static SupplierDto ToDto(Supplier s) =>
        new(s.Id, s.Name, s.TaxId, s.ContactName, s.Phone, s.Email, s.Active);
}

public sealed class InventorySettingsHandlers(IInventoryRepository repo, IInventoryUnitOfWork uow)
{
    public async Task<InventorySettingsDto> GetAsync(Guid organizationId, CancellationToken ct = default)
    {
        var s = await repo.GetSettingsAsync(organizationId, ct);
        return new InventorySettingsDto(s.AdjustmentApprovalThreshold, s.ExpiryAlertDays);
    }

    public async Task<InventorySettingsDto> UpdateAsync(
        Guid organizationId, InventorySettingsDto input, CancellationToken ct = default)
    {
        var s = await repo.GetSettingsAsync(organizationId, ct);
        s.Update(input.AdjustmentApprovalThreshold, input.ExpiryAlertDays);
        await repo.SaveSettingsAsync(s, ct);
        await uow.SaveChangesAsync(ct);
        return new InventorySettingsDto(s.AdjustmentApprovalThreshold, s.ExpiryAlertDays);
    }
}

public sealed record InventoryUnitInput(string Name, bool Active = true);

/// <summary>
/// La lista de unidades base de la organización.
///
/// <para><b>Se siembra a pedido</b>, como la bodega general: la primera vez que alguien la mira
/// arranca con <see cref="InventoryUnit.Defaults"/>, y siempre incluye las unidades que ya usan los
/// artículos creados cuando el campo era texto libre, para que ninguno quede fuera de la lista.</para>
/// </summary>
public sealed class InventoryUnitHandlers(IInventoryRepository repo, IInventoryUnitOfWork uow)
{
    public async Task<IReadOnlyList<InventoryUnitDto>> ListAsync(
        Guid organizationId, bool activeOnly, CancellationToken ct = default)
    {
        await EnsureAsync(organizationId, ct);
        return (await repo.ListUnitsAsync(organizationId, ct))
            .Where(u => !activeOnly || u.Active)
            .Select(ToDto)
            .ToList();
    }

    public async Task<InventoryUnitDto> CreateAsync(Guid organizationId, InventoryUnitInput input, CancellationToken ct = default)
    {
        await EnsureAsync(organizationId, ct);
        var unidad = InventoryUnit.Create(organizationId, input.Name);
        if (!input.Active)
        {
            unidad.Update(unidad.Name, active: false);
        }
        if (!await repo.TryAddUnitAsync(unidad, ct))
        {
            throw new ArgumentException($"La unidad \"{unidad.Name}\" ya está en la lista.", nameof(input));
        }
        return ToDto(unidad);
    }

    /// <summary>
    /// Renombra o activa/desactiva. Un renombre cambia la unidad de los artículos que la usan en la
    /// misma transacción: es la misma unidad mal escrita, no otra.
    /// </summary>
    public async Task<InventoryUnitDto?> UpdateAsync(
        Guid organizationId, Guid unitId, InventoryUnitInput input, CancellationToken ct = default)
    {
        var unidad = await repo.GetUnitAsync(organizationId, unitId, ct);
        if (unidad is null)
        {
            return null;
        }
        var anterior = unidad.Name;
        var nuevo = InventoryUnit.Normalize(input.Name);
        if (nuevo != anterior && await repo.FindUnitByNameAsync(organizationId, nuevo, ct) is not null)
        {
            throw new ArgumentException(
                $"La unidad \"{nuevo}\" ya está en la lista. Desactivá esta y usá aquella en los artículos.", nameof(input));
        }

        await uow.ExecuteAsync(async t =>
        {
            unidad.Update(nuevo, input.Active);
            await uow.SaveChangesAsync(t);
            if (nuevo != anterior)
            {
                await repo.RenameItemsBaseUnitAsync(organizationId, anterior, nuevo, t);
            }
            return true;
        }, ct);
        return ToDto(unidad);
    }

    /// <summary>
    /// La unidad con la que se guarda un artículo: tiene que estar en la lista y activa. La que el
    /// artículo ya tenía se acepta aunque se haya desactivado después, para que editar otro dato
    /// no obligue a cambiarla.
    /// </summary>
    public async Task<string> ValidateForItemAsync(
        Guid organizationId, string? baseUnit, string? current, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(baseUnit))
        {
            throw new ArgumentException(
                "La unidad base es obligatoria: es la unidad en la que se cuenta todo (tableta, frasco, unidad).",
                nameof(baseUnit));
        }
        var nombre = InventoryUnit.Normalize(baseUnit);
        if (nombre == current)
        {
            return nombre;
        }
        await EnsureAsync(organizationId, ct);
        var unidad = await repo.FindUnitByNameAsync(organizationId, nombre, ct)
            ?? throw new ArgumentException(
                $"La unidad \"{nombre}\" no está en la lista de unidades. Agregala en Inventario → Configuración.",
                nameof(baseUnit));
        if (!unidad.Active)
        {
            throw new ArgumentException($"La unidad \"{nombre}\" está desactivada.", nameof(baseUnit));
        }
        return nombre;
    }

    private async Task EnsureAsync(Guid organizationId, CancellationToken ct)
    {
        var existentes = (await repo.ListUnitsAsync(organizationId, ct)).Select(u => u.Name).ToHashSet();
        var usadas = await repo.ListItemBaseUnitsAsync(organizationId, ct);
        var semilla = existentes.Count == 0 ? InventoryUnit.Defaults : [];
        foreach (var nombre in semilla.Concat(usadas).Distinct())
        {
            // Una unidad vieja que no cabe en la lista (más larga que el tope) se deja como está.
            if (existentes.Contains(nombre) || nombre.Length is 0 or > InventoryUnit.NameMaxLength)
            {
                continue;
            }
            await repo.TryAddUnitAsync(InventoryUnit.Create(organizationId, nombre), ct);
            existentes.Add(nombre);
        }
    }

    public static InventoryUnitDto ToDto(InventoryUnit u) => new(u.Id, u.Name, u.Active);
}
