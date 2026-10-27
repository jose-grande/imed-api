using IMed.Api.Application.Abstractions;
using IMed.Api.Application.Billing;
using IMed.Api.Application.Inventory;
using IMed.Api.Application.Locations;
using IMed.Api.Domain.Billing.Accounts;
using IMed.Api.Domain.Inventory;
using IMed.Api.Domain.Locations;
using IMed.Api.Domain.Pharmacy;

namespace IMed.Api.Application.Pharmacy;

/// <summary>Arma la vista de una entrega con sus nombres, lotes y precios.</summary>
public sealed class DispenseReader(
    IInventoryRepository repo,
    IInventoryQueries queries,
    IBillingAccountRepository accounts)
{
    public async Task<DispenseDto?> GetAsync(Guid organizationId, Guid dispenseId, CancellationToken ct = default)
    {
        var d = await repo.GetDispenseAsync(organizationId, dispenseId, ct);
        return d is null ? null : await ToDtoAsync(d, ct);
    }

    public async Task<DispenseDto> ToDtoAsync(PharmacyDispense d, CancellationToken ct = default)
    {
        var lk = await queries.GetLookupAsync(
            d.OrganizationId,
            d.Lines.Select(l => l.StockItemId).ToList(),
            d.Lines.SelectMany(l => l.Lots).Select(x => x.StockLotId).ToList(),
            [d.WarehouseId], ct);
        var cargos = d.AccountId == Guid.Empty
            ? new Dictionary<Guid, BillingCharge>()
            : (await accounts.ListChargesAsync(d.OrganizationId, d.AccountId, ct)).ToDictionary(c => c.Id);

        var lineas = d.Lines.OrderBy(l => l.LineNumber).Select(l =>
        {
            var cargo = l.ChargeId is { } cid ? cargos.GetValueOrDefault(cid) : null;
            var item = lk.Items.TryGetValue(l.StockItemId, out var it) ? it : (Code: "?", Name: l.Description, BaseUnit: "");
            return new DispenseLineDto(
                l.Id, l.StockItemId, l.Description, l.Quantity, item.BaseUnit, l.MedicationRequestFhirId, l.PrescribedText,
                l.PrescribedQuantity, l.PrescribedUnit, l.CompletesPrescription, l.SubstitutionReason, l.ChargeId,
                cargo?.UnitPrice ?? 0m, cargo?.Total ?? 0m, cargo?.PricePending ?? false,
                l.Lots.Select(x =>
                {
                    var lote = lk.Lots.TryGetValue(x.StockLotId, out var lo) ? lo : (LotCode: "?", Expiry: (DateOnly?)null);
                    return new DispenseLotDto(x.StockLotId, lote.LotCode, lote.Expiry, x.Quantity);
                }).ToList());
        }).ToList();

        return new DispenseDto(
            d.Id, d.Kind.ToString(), d.Status.ToString(), d.LocationId, d.WarehouseId, lk.Warehouse(d.WarehouseId),
            d.AccountId, d.PatientFhirId, d.PatientName, d.EncounterFhirId, d.BuyerName, d.BuyerTaxId, d.DispensedAt,
            d.DispensedBy, d.Notes, d.InvoiceId, d.CancellationReason,
            Math.Round(lineas.Sum(l => l.Total), 2, MidpointRounding.AwayFromZero), lineas);
    }
}

/// <summary>
/// La cola de recetas de la farmacia (B4): lo que los médicos recetaron en las consultas de la sede
/// activa y todavía no se despachó por completo.
///
/// <para><b>Por sede.</b> La consulta declara su sede (FAC-9); la farmacia de la sede norte no ve las
/// recetas de la sede sur. Las consultas anteriores a FAC-9 no tienen sede y se muestran en la sede
/// principal, que es la que recibió su backfill.</para>
///
/// <para><b>Propone, no decide.</b> Para cada medicamento recetado lista los artículos comerciales
/// con su código ATC (P10) y propone el primero que alcanza; el lote lo propone el FEFO al
/// despachar.</para>
/// </summary>
public sealed class PrescriptionQueueHandler(
    IPrescriptionSource source,
    IInventoryRepository repo,
    IBillingCatalogRepository catalog,
    ILocationRepository locations,
    WarehouseResolver warehouses,
    IClinicClock clock)
{
    public async Task<PrescriptionQueueDto> HandleAsync(
        Guid organizationId, Guid? userId, Guid? warehouseId, int days, CancellationToken ct = default)
    {
        var bodega = await warehouses.ResolveAsync(organizationId, userId, warehouseId, preferPharmacy: true, ct);
        var sede = bodega.LocationId;
        var desde = clock.StartOf(clock.Today().AddDays(-Math.Clamp(days, 1, 90)));

        var recetas = await source.ListActiveAsync(organizationId, desde, ct);
        var conConsulta = recetas.Where(r => r.EncounterFhirId is not null).ToList();
        var sedes = await source.GetEncounterLocationsAsync(
            organizationId, conConsulta.Select(r => r.EncounterFhirId!).Distinct().ToList(), ct);
        var principal = (await locations.GetDefaultAsync(organizationId, ct))?.Id;

        var deLaSede = conConsulta
            .Where(r => sedes.GetValueOrDefault(r.EncounterFhirId!) is { } s ? s == sede : sede == principal)
            .ToList();

        var despachado = await repo.GetPrescriptionFillAsync(organizationId, deLaSede.Select(r => r.Id).ToList(), ct);
        var pendientes = deLaSede
            .Where(r => !(despachado.TryGetValue(r.Id, out var f) && f.Completed))
            .ToList();

        var nombres = await source.GetPatientNamesAsync(
            organizationId, pendientes.Select(r => r.PatientFhirId).Distinct().ToList(), ct);

        var hoy = clock.Today();
        var candidatosPorAtc = new Dictionary<string, List<DispenseCandidateDto>>(StringComparer.Ordinal);
        async Task<List<DispenseCandidateDto>> CandidatosAsync(string? atc)
        {
            if (string.IsNullOrWhiteSpace(atc))
            {
                return [];
            }
            if (candidatosPorAtc.TryGetValue(atc, out var ya))
            {
                return ya;
            }
            var lista = new List<DispenseCandidateDto>();
            foreach (var item in await repo.FindMedicationsByAtcAsync(organizationId, atc, ct))
            {
                var usable = FefoAllocator.Usable(await repo.GetAvailabilityAsync(bodega.Id, item.Id, ct), hoy);
                var cobrable = item.BillableItemId is { } bid ? await catalog.GetItemAsync(organizationId, bid, ct) : null;
                lista.Add(new DispenseCandidateDto(
                    item.Id, item.Code, item.Name, item.BaseUnit, item.Medication?.AtcCode, usable,
                    cobrable?.DefaultPrice, cobrable?.PricePending ?? true, item.Sellable));
            }
            candidatosPorAtc[atc] = lista = lista.OrderByDescending(c => c.Usable).ThenBy(c => c.Name).ToList();
            return lista;
        }

        var resultado = new List<QueuePrescriptionDto>();
        foreach (var grupo in pendientes
                     .GroupBy(r => (r.EncounterFhirId!, r.PatientFhirId, r.GroupId))
                     .OrderBy(g => g.Min(r => r.AuthoredOn)))
        {
            var meds = new List<QueueMedicationDto>();
            foreach (var r in grupo.OrderBy(r => r.Label))
            {
                var candidatos = await CandidatosAsync(r.AtcCode);
                var ya = despachado.TryGetValue(r.Id, out var f) ? f.Dispensed : 0m;
                var falta = r.Quantity is { } q ? Math.Max(q - ya, 0) : 0m;
                var propuesto = candidatos.FirstOrDefault(c => c.Sellable && c.Usable > 0 && c.Usable >= falta)
                    ?? candidatos.FirstOrDefault(c => c.Sellable && c.Usable > 0);
                meds.Add(new QueueMedicationDto(
                    r.Id, r.Label, r.AtcCode, r.DosageText, r.Quantity, r.Unit, ya, candidatos, propuesto?.StockItemId));
            }
            resultado.Add(new QueuePrescriptionDto(
                grupo.Key.Item1, grupo.Key.PatientFhirId, nombres.GetValueOrDefault(grupo.Key.PatientFhirId),
                grupo.Key.GroupId, grupo.Min(r => r.AuthoredOn), meds));
        }
        return new PrescriptionQueueDto(sede, bodega.Id, bodega.Name, resultado);
    }

    /// <summary>
    /// Artículos para una receta escrita en texto libre (sin código ATC), para sustituir o para el
    /// mostrador: búsqueda por nombre, código o código de barras entre los vendibles, con su
    /// existencia en la bodega. <paramref name="overTheCounterOnly"/> limita a venta libre (P11).
    /// </summary>
    public async Task<IReadOnlyList<DispenseCandidateDto>> SearchCandidatesAsync(
        Guid organizationId, Guid? userId, Guid? warehouseId, string term, bool overTheCounterOnly = false,
        CancellationToken ct = default)
    {
        var bodega = await warehouses.ResolveAsync(organizationId, userId, warehouseId, preferPharmacy: true, ct);
        var hoy = clock.Today();
        var lista = new List<DispenseCandidateDto>();
        foreach (var item in await repo.SearchItemsAsync(
                     organizationId, term, null, overTheCounterOnly ? true : null, activeOnly: true, 30, ct))
        {
            if (!item.Sellable)
            {
                continue;
            }
            var usable = FefoAllocator.Usable(await repo.GetAvailabilityAsync(bodega.Id, item.Id, ct), hoy);
            var cobrable = item.BillableItemId is { } bid ? await catalog.GetItemAsync(organizationId, bid, ct) : null;
            lista.Add(new DispenseCandidateDto(
                item.Id, item.Code, item.Name, item.BaseUnit, item.Medication?.AtcCode, usable,
                cobrable?.DefaultPrice, cobrable?.PricePending ?? true, item.Sellable));
        }
        return lista;
    }
}

public sealed record DispenseLineInput(
    string MedicationRequestId,
    Guid StockItemId,
    decimal Quantity,
    Guid? StockLotId,
    bool CompletesPrescription,
    string? SubstitutionReason);

public sealed record DispensePrescriptionInput(
    string IdempotencyKey,
    Guid? WarehouseId,
    string? Notes,
    IReadOnlyList<DispenseLineInput> Lines);

/// <summary>
/// 🔴 Despacho de receta (B4, D6, P10). En <b>una sola transacción</b>: la salida del lote, el saldo
/// y el cargo <c>PharmacyDispense</c> en la cuenta de la consulta (FSF §10.1, costura 1).
///
/// <para><b>Idempotente por la llave de la pantalla.</b> La cabecera del despacho es lo primero que
/// se escribe: si otra petición con la misma llave llega a la vez, la base la hace esperar en el
/// índice único y, cuando la primera confirma, la segunda falla limpio, se deshace y devuelve el
/// despacho que ganó. Un doble clic no despacha dos veces.</para>
///
/// <para><b>Sin existencia, nada.</b> Si el último lote se lo llevó otro despacho un instante antes,
/// el libro lanza <see cref="InsufficientStockException"/> y se deshace todo: ni salida, ni cargo,
/// ni cuenta abierta.</para>
///
/// <para><b>La receta manda.</b> Se vuelve a leer cada <c>MedicationRequest</c> del servidor FHIR: tiene
/// que estar activa, ser de la consulta y del paciente que se despacha, y no estar ya atendida. Si
/// se entrega otro principio activo que el recetado, la constancia de sustitución es obligatoria.</para>
/// </summary>
public sealed class DispensePrescriptionHandler(
    IPrescriptionSource source,
    IInventoryRepository repo,
    IInventoryUnitOfWork uow,
    IStockLedger ledger,
    StockOutPlanner planner,
    WarehouseResolver warehouses,
    StockItemCommandHandler items,
    IEncounterAccountResolver accountResolver,
    CaptureChargeHandler captureCharge,
    DispenseReader reader)
{
    public async Task<DispenseResult> HandleAsync(
        Guid organizationId, Guid? userId, DispensePrescriptionInput input, CancellationToken ct = default)
    {
        var llave = input.IdempotencyKey?.Trim() ?? string.Empty;
        if (await repo.FindDispenseByKeyAsync(organizationId, llave, ct) is { } previo)
        {
            return new DispenseResult(await reader.ToDtoAsync(previo, ct), AlreadyProcessed: true);
        }
        if (input.Lines is not { Count: > 0 })
        {
            throw new ArgumentException("El despacho no tiene líneas.");
        }

        var bodega = await warehouses.ResolveAsync(organizationId, userId, input.WarehouseId, preferPharmacy: true, ct);

        // ── La receta, leída del servidor y no de lo que mandó la pantalla ──
        var recetas = new Dictionary<string, PrescribedMedication>(StringComparer.Ordinal);
        foreach (var id in input.Lines.Select(l => l.MedicationRequestId).Distinct())
        {
            var r = await source.GetAsync(organizationId, id, ct)
                ?? throw new ArgumentException("Una de las recetas ya no existe.");
            if (r.Status != "active")
            {
                throw new InvalidOperationException($"La receta de {r.Label} no está activa (estado: {r.Status}).");
            }
            if (r.EncounterFhirId is null)
            {
                throw new InvalidOperationException($"La receta de {r.Label} no pertenece a una consulta.");
            }
            recetas[id] = r;
        }
        var consultas = recetas.Values.Select(r => (r.EncounterFhirId, r.PatientFhirId)).Distinct().ToList();
        if (consultas.Count != 1)
        {
            throw new ArgumentException("Un despacho atiende las recetas de una sola consulta.");
        }
        var (encuentro, paciente) = (consultas[0].EncounterFhirId!, consultas[0].PatientFhirId);

        var llenado = await repo.GetPrescriptionFillAsync(organizationId, recetas.Keys.ToList(), ct);
        foreach (var r in recetas.Values)
        {
            if (llenado.TryGetValue(r.Id, out var f) && f.Completed)
            {
                throw new InvalidOperationException($"La receta de {r.Label} ya se despachó por completo.");
            }
        }

        // ── Artículos: vendibles, con cobrable, y la constancia si se sustituye ──
        var articulos = new Dictionary<Guid, StockItem>();
        var cobrables = new Dictionary<Guid, Guid>();
        foreach (var l in input.Lines)
        {
            if (articulos.ContainsKey(l.StockItemId))
            {
                continue;
            }
            var item = await repo.GetItemAsync(organizationId, l.StockItemId, ct)
                ?? throw new ArgumentException("Uno de los artículos no existe.");
            if (!item.Active)
            {
                throw new InvalidOperationException($"{item.Name} está desactivado.");
            }
            if (!item.Sellable)
            {
                throw new InvalidOperationException(
                    $"{item.Name} no está marcado como vendible: no se puede cargar al paciente.");
            }
            cobrables[item.Id] = await items.AsegurarCobrableAsync(organizationId, item, ct)
                ?? throw new InvalidOperationException(
                    $"{item.Name} todavía no tiene su cobrable: la organización necesita su régimen fiscal configurado.");
            articulos[item.Id] = item;
        }
        foreach (var l in input.Lines)
        {
            var r = recetas[l.MedicationRequestId];
            var item = articulos[l.StockItemId];
            if (r.AtcCode is { } atc && !string.Equals(item.Medication?.AtcCode, atc, StringComparison.OrdinalIgnoreCase)
                && string.IsNullOrWhiteSpace(l.SubstitutionReason))
            {
                throw new ArgumentException(
                    $"Se recetó {r.Label} ({atc}) y se va a entregar {item.Name}: anotá el motivo de la sustitución.");
            }
        }
        if (input.Lines.GroupBy(l => l.MedicationRequestId).Any(g => g.Count() > 1))
        {
            throw new ArgumentException("Cada medicamento recetado va en una sola línea del despacho.");
        }

        var nombre = (await source.GetPatientNamesAsync(organizationId, [paciente], ct)).GetValueOrDefault(paciente);

        try
        {
            var id = await uow.ExecuteAsync(async t =>
            {
                // 1) La cabecera primero: reclama la llave de idempotencia.
                var despacho = PharmacyDispense.Start(
                    organizationId, PharmacyDispenseKind.Prescription, llave, bodega.LocationId, bodega.Id,
                    paciente, nombre, encuentro, userId, input.Notes);
                await repo.AddDispenseAsync(despacho, t);
                await uow.SaveChangesAsync(t);

                // 2) La cuenta de la consulta (D2). Si ya se facturó o se cerró, el despacho abre la suya.
                var cuenta = await accountResolver.GetOrOpenForEncounterAsync(organizationId, encuentro, paciente, nombre, t)
                    ?? await accountResolver.GetOrOpenStandaloneAsync(
                        organizationId, BillingAccountOrigin.PharmacySale, despacho.Id.ToString(), paciente, nombre, t)
                    ?? throw new InvalidOperationException("No se pudo abrir la cuenta del despacho.");
                despacho.AssignAccount(cuenta);

                // 3) Salida FEFO (o del lote elegido) y 4) el cargo de cada línea.
                var patas = new List<StockPosting>();
                foreach (var l in input.Lines)
                {
                    var r = recetas[l.MedicationRequestId];
                    var item = articulos[l.StockItemId];
                    var linea = despacho.AddLine(
                        item.Id, item.Name, l.Quantity, r.Id, r.AtcCode, r.Label, r.Quantity, r.Unit,
                        l.CompletesPrescription,
                        r.AtcCode is not null && !string.Equals(item.Medication?.AtcCode, r.AtcCode, StringComparison.OrdinalIgnoreCase)
                            ? l.SubstitutionReason : null);
                    foreach (var a in await planner.PlanAsync(bodega, item, l.Quantity, l.StockLotId, allowUnusable: false, t))
                    {
                        patas.Add(new StockPosting(StockMovementType.Dispense, item.Id, a.StockLotId, bodega.Id, -1, a.Quantity, linea.Id));
                    }
                }
                var movs = await ledger.PostAsync(organizationId, despacho.Id, StockSources.PharmacyDispense, despacho.Id, userId, patas, t);
                foreach (var linea in despacho.Lines)
                {
                    foreach (var m in movs.Where(m => m.SourceLineId == linea.Id))
                    {
                        linea.AddLot(m.StockLotId, m.Quantity, m.UnitCost);
                    }
                }
                await uow.SaveChangesAsync(t);

                foreach (var linea in despacho.Lines)
                {
                    var cargo = await captureCharge.HandleAsync(new CaptureChargeCommand(
                        organizationId, cuenta, cobrables[linea.StockItemId], ChargeSourceKind.PharmacyDispense,
                        linea.Id.ToString(), Quantity: linea.Quantity, RequestedByUserId: userId,
                        SourceFhirRef: linea.MedicationRequestFhirId is { } mr ? $"MedicationRequest/{mr}" : null,
                        Reason: linea.SubstitutionReason is { } s ? $"Sustitución: {s}" : null,
                        LocationId: bodega.LocationId), t);
                    if (!cargo.Succeeded)
                    {
                        throw new InvalidOperationException($"No se pudo cargar {linea.Description}: {cargo.Rejected}");
                    }
                    linea.LinkCharge(cargo.Charge!.Id);
                }
                return despacho.Id;
            }, ct);

            var hecho = await repo.GetDispenseAsync(organizationId, id, ct);
            return new DispenseResult(await reader.ToDtoAsync(hecho!, ct), AlreadyProcessed: false);
        }
        catch (Exception ex) when (uow.IsUniqueViolation(ex))
        {
            // Otra petición con la misma llave ganó la carrera: se devuelve lo que ella hizo.
            var ganador = await repo.FindDispenseByKeyAsync(organizationId, llave, ct);
            if (ganador is null)
            {
                throw;
            }
            return new DispenseResult(await reader.ToDtoAsync(ganador, ct), AlreadyProcessed: true);
        }
    }
}

/// <summary>Historial de entregas de la sede (despachos y ventas).</summary>
public sealed class ListDispensesHandler(
    IInventoryRepository repo,
    DispenseReader reader,
    ResolveActiveLocationHandler activeLocation,
    IClinicClock clock)
{
    public async Task<IReadOnlyList<DispenseDto>> HandleAsync(
        Guid organizationId, Guid? userId, string? kind, DateOnly? from, DateOnly? to, CancellationToken ct = default)
    {
        var sede = await activeLocation.HandleAsync(organizationId, userId, null, ct);
        var hoy = clock.Today();
        PharmacyDispenseKind? tipo = Enum.TryParse<PharmacyDispenseKind>(kind, ignoreCase: true, out var k) ? k : null;
        var lista = await repo.ListDispensesAsync(
            organizationId, sede, tipo, clock.StartOf(from ?? hoy), clock.StartOf((to ?? hoy).AddDays(1)), 200, ct);
        var resultado = new List<DispenseDto>(lista.Count);
        foreach (var d in lista)
        {
            resultado.Add(await reader.ToDtoAsync(d, ct));
        }
        return resultado;
    }
}

public static class PharmacyLocationRefs
{
    /// <summary>Id de la sede a partir de la referencia FHIR <c>Location/sede-{guid}</c>.</summary>
    public static Guid? Parse(string? reference)
    {
        if (string.IsNullOrWhiteSpace(reference))
        {
            return null;
        }
        var id = reference.StartsWith("Location/", StringComparison.Ordinal) ? reference["Location/".Length..] : reference;
        const string prefijo = "sede-";
        return id.StartsWith(prefijo, StringComparison.Ordinal) && Guid.TryParse(id[prefijo.Length..], out var g) ? g : null;
    }

    public static string For(Guid locationId) => $"Location/{OrganizationLocation.FhirIdFor(locationId)}";
}
