using IMed.Api.Application.Abstractions;
using IMed.Api.Application.LabCatalog;
using IMed.Api.Application.Inventory;
using IMed.Api.Application.Pharmacy;
using IMed.Api.Domain.Billing;
using IMed.Api.Domain.Billing.Fiscal;
using IMed.Api.Application.LabReception;
using IMed.Api.Domain.Billing.Accounts;
using IMed.Api.Domain.Billing.Catalog;

namespace IMed.Api.Application.Billing;

/// <summary>Un procedimiento de mostrador en la venta.</summary>
public sealed record CounterSaleProcedure(Guid BillableItemId, decimal Quantity = 1m);

/// <summary>Un producto de venta libre en la venta (F4).</summary>
public sealed record CounterSaleProduct(Guid StockItemId, decimal Quantity = 1m);

/// <param name="Exams">Exámenes y paneles del catálogo del laboratorio, como en la orden externa.</param>
/// <param name="Procedures">Procedimientos de mostrador (curaciones, inyecciones…).</param>
/// <param name="RequesterName">Médico solicitante, si el paciente trae orden. Opcional.</param>
/// <param name="Priority">Prioridad de la orden de laboratorio (<c>routine</c> por defecto).</param>
/// <param name="Appointments">Citas cuya consulta se cobra por adelantado (F3). Solo con modo "antes" o "mixto".</param>
public sealed record CounterSaleInput(
    string PatientFhirId,
    string? PatientName,
    IReadOnlyList<ExternalOrderExam>? Exams,
    IReadOnlyList<CounterSaleProcedure>? Procedures,
    string? RequesterName = null,
    string? Priority = null,
    IReadOnlyList<string>? Appointments = null,
    IReadOnlyList<CounterSaleProduct>? Products = null);

/// <summary>Las consultas agendadas de un paciente que se pueden cobrar por adelantado.</summary>
/// <param name="Enabled">El modo de cobro lo permite ("antes" o "mixto").</param>
/// <param name="Reason">Por qué no se puede, si no se puede.</param>
public sealed record SaleAppointmentsDto(bool Enabled, string? Reason, IReadOnlyList<SellableItemDto> Items);

/// <summary>Una línea cotizada.</summary>
/// <param name="Kind"><c>exam</c>, <c>panel</c> o <c>procedure</c>.</param>
/// <param name="UnitPrice">Precio vigente; null si el cobrable todavía no existe o no tiene precio.</param>
/// <param name="Problem">Por qué no se puede vender esta línea; null si se puede.</param>
public sealed record CounterSaleQuoteLine(
    string Kind,
    string Name,
    decimal Quantity,
    decimal? UnitPrice,
    decimal? Total,
    bool PricePending,
    string? Problem);

/// <param name="CanSell">Ninguna línea tiene problema. Una línea sin precio no impide crear la orden.</param>
public sealed record CounterSaleQuote(IReadOnlyList<CounterSaleQuoteLine> Lines, decimal Total, bool AnyPricePending, bool CanSell);

/// <summary>Algo que se puede vender en la Caja, con su precio.</summary>
/// <param name="Kind"><c>exam</c>, <c>panel</c> o <c>procedure</c>.</param>
/// <param name="Exam">Para exámenes y paneles: cómo va en la orden de laboratorio.</param>
/// <param name="BillableItemId">Para procedimientos: el cobrable.</param>
/// <param name="UnitPrice">Precio vigente; null si todavía no tiene.</param>
/// <param name="AppointmentId">Para consultas: la cita que se cobra.</param>
/// <param name="Start">Para consultas: cuándo es la cita.</param>
/// <param name="StockItemId">Para productos: el artículo de inventario.</param>
public sealed record SellableItemDto(
    string Kind,
    string Code,
    string Name,
    ExternalOrderExam? Exam,
    Guid? BillableItemId,
    decimal? UnitPrice,
    bool PricePending,
    string? AppointmentId = null,
    DateTimeOffset? Start = null,
    Guid? StockItemId = null);

/// <summary>Lo que dejó la venta: la cuenta a cobrar y la orden de laboratorio, si hubo exámenes.</summary>
public sealed record CounterSaleResult(Guid AccountId, string? Requisition, IReadOnlyList<string> ServiceRequestIds);

/// <summary>
/// Vender en la Caja (UX-Cobro-Anticipado F2): exámenes y procedimientos para un paciente, antes de
/// prestarlos.
///
/// <para><b>No es una forma nueva de cobrar: es otra puerta al flujo de siempre.</b> Los exámenes
/// crean la orden externa con <see cref="CreateExternalOrderMultiHandler"/> y se admiten con
/// <see cref="CheckInOrderHandler"/>: esa admisión abre la cuenta de la orden y captura sus cargos,
/// igual que en la recepción del laboratorio. Los procedimientos se cargan a esa misma cuenta, como
/// en la Orden de servicio. El cobro lo hace después <see cref="AccountCheckoutHandler"/>. Así el
/// laboratorio recibe la orden en su cola y nada se cobra dos veces.</para>
///
/// <para><b>Si el cobro falla, nada se pierde.</b> La orden y la cuenta ya existen: quedan pendientes
/// en la cola de la Caja, como cualquier orden externa sin cobrar.</para>
/// </summary>
public sealed class CounterSaleHandler(
    CreateExternalOrderMultiHandler createOrder,
    CheckInOrderHandler checkIn,
    IBillingAccountRepository accounts,
    IBillingCatalogRepository billingCatalog,
    ILabCatalogRepository labCatalog,
    OpenAccountHandler openAccount,
    CaptureChargeHandler capture,
    ResolvePriceHandler prices,
    SearchLabTestsHandler labTests,
    IBillingSettingsRepository settings,
    AppointmentConsultationResolver citas,
    IHapiFhirClient hapi,
    IInventoryRepository inventory,
    OverTheCounterSaleHandler otc,
    ICashRepository cash,
    IFiscalRepository fiscal)
{
    private const string SinCajaMixta =
        "Los productos se venden en la Caja solo con una caja mixta: una caja de servicios no cobra " +
        "productos, y una de farmacia no cobra servicios. Abrí tu turno en una caja mixta, o vendelos en Farmacia.";

    /// <summary>
    /// ¿El usuario puede vender productos acá? Con un turno abierto en una caja mixta. Sin turno no
    /// se sabe dónde va a cobrar: se rechaza también, porque el cobro sí lo va a exigir.
    /// </summary>
    private async Task<bool> CajaMixtaAsync(Guid org, Guid userId, CancellationToken ct)
    {
        var turno = await cash.FindOpenSessionByCashierAsync(org, userId, ct);
        if (turno is null)
        {
            return false;
        }
        var caja = await fiscal.GetCashPointAsync(org, turno.CashPointId, ct);
        return caja?.Profile == CashPointProfile.Mixed;
    }
    private const string SinModo =
        "La consulta se cobra por adelantado solo si la organización cobra antes del servicio " +
        "(modo de cobro \"antes\" o \"mixto\"). Con \"después\", la consulta se cobra al atenderse.";

    /// <summary>
    /// Las consultas agendadas del paciente que se pueden cobrar ahora. Solo con modo "antes" o
    /// "mixto": con "después" el cobro de la consulta al iniciarla no reconoce lo precobrado, y
    /// venderla acá la cobraría dos veces.
    /// </summary>
    /// <summary>
    /// Si la consulta de cada cita ya se pagó en caja, para la Agenda. Con modo "después" no aplica:
    /// la consulta se cobra al iniciarla y la Agenda no muestra nada.
    /// </summary>
    public async Task<AppointmentPaymentStatusDto> AppointmentPaymentStatusAsync(
        Guid organizationId, IReadOnlyCollection<string> appointmentIds, CancellationToken ct = default)
    {
        if ((await settings.GetAsync(organizationId, ct)).CollectionMode == BillingCollectionMode.AfterService)
        {
            return new AppointmentPaymentStatusDto(false, []);
        }
        var ids = appointmentIds.Where(a => !string.IsNullOrWhiteSpace(a)).Select(a => a.Trim()).Distinct().Take(200).ToList();
        var saldos = await accounts.PrepaidAppointmentBalancesAsync(organizationId, ids, ct);
        return new AppointmentPaymentStatusDto(true, ids.Select(id => new AppointmentPaymentDto(id,
            !saldos.TryGetValue(id, out var saldo) ? AppointmentPaymentDto.Unpaid
            : saldo is 0m ? AppointmentPaymentDto.Paid
            : AppointmentPaymentDto.Pending)).ToList());
    }

    public async Task<SaleAppointmentsDto> ListAppointmentsAsync(
        Guid organizationId, string patientFhirId, CancellationToken ct = default)
    {
        if ((await settings.GetAsync(organizationId, ct)).CollectionMode == BillingCollectionMode.AfterService)
        {
            return new SaleAppointmentsDto(false, SinModo, []);
        }
        var recursos = await hapi.SearchResourcesAsync(
            "Appointment", $"patient=Patient/{patientFhirId.Trim()}&status={string.Join(',', AppointmentConsultationResolver.Cobrables)}",
            $"org-{organizationId}", ct);
        var items = new List<SellableItemDto>();
        foreach (var r in recursos)
        {
            var c = await citas.ResolveAsync(organizationId, r, ct);
            if (c is null || await BloqueoDeCitaAsync(organizationId, c, patientFhirId, ct) is not null)
            {
                continue;
            }
            var precio = c.Item.PricePending == true ? null : await prices.HandleAsync(organizationId, c.Item.Id, null, ct);
            items.Add(new SellableItemDto(
                "consultation", c.AppointmentId, c.Item.Name, null, c.Item.Id, precio?.Price,
                precio is null, c.AppointmentId, c.Start));
        }
        return new SaleAppointmentsDto(true, null, items.OrderBy(i => i.Start).ToList());
    }

    /// <summary>
    /// Por qué no se puede cobrar por adelantado la consulta de esta cita; null si se puede.
    /// </summary>
    private async Task<string?> BloqueoDeCitaAsync(
        Guid org, AppointmentConsultation c, string patientFhirId, CancellationToken ct)
    {
        if (!string.Equals(c.PatientFhirId, patientFhirId.Trim(), StringComparison.Ordinal))
        {
            return "La cita es de otro paciente.";
        }
        if (!AppointmentConsultationResolver.Cobrables.Contains(c.Status))
        {
            return "La cita ya no está vigente (atendida, cancelada o no se presentó).";
        }
        if (await accounts.FindAnyLiveChargeBySourceAsync(org, ChargeSourceKind.Appointment, c.AppointmentId, ct) is not null)
        {
            return "La consulta de esa cita ya está cobrada.";
        }
        // La consulta ya empezó: su cargo nació con el encuentro y se cobra desde la cola.
        var encuentros = await hapi.SearchResourcesAsync(
            "Encounter", $"appointment=Appointment/{c.AppointmentId}", $"org-{org}", ct);
        foreach (var e in encuentros)
        {
            var encId = e["id"]?.GetValue<string>();
            if (encId is not null
                && await accounts.FindAnyLiveChargeBySourceAsync(org, ChargeSourceKind.Encounter, encId, ct) is not null)
            {
                return "La consulta ya empezó y tiene su cargo: cobrala desde la cola.";
            }
        }
        return null;
    }
    /// <summary>
    /// Buscar qué vender: exámenes que el laboratorio hace, paneles activos y procedimientos de
    /// mostrador, cada uno con su precio. Es un buscador propio de la venta porque el catálogo del
    /// laboratorio pide permisos de laboratorio que el cajero no tiene ni necesita.
    /// </summary>
    public async Task<IReadOnlyList<SellableItemDto>> SearchAsync(
        Guid organizationId, string? term, int limit = 20, CancellationToken ct = default) =>
        await SearchAsync(organizationId, null, term, limit, ct);

    /// <param name="userId">
    /// Quien vende: los productos solo se ofrecen si tiene un turno abierto en una caja mixta.
    /// </param>
    public async Task<IReadOnlyList<SellableItemDto>> SearchAsync(
        Guid organizationId, Guid? userId, string? term, int limit = 20, CancellationToken ct = default)
    {
        var t = term?.Trim() ?? string.Empty;
        if (t.Length < 2)
        {
            return [];
        }
        limit = Math.Clamp(limit, 1, 50);
        var resultado = new List<SellableItemDto>();

        var paneles = (await labCatalog.ListPanelsAsync(organizationId, ct))
            .Where(p => p.Active
                        && (p.Name.Contains(t, StringComparison.OrdinalIgnoreCase)
                            || p.Code.Contains(t, StringComparison.OrdinalIgnoreCase)))
            .Take(limit);
        foreach (var p in paneles)
        {
            var examen = new ExternalOrderExam(p.LoincCode, p.Code, LabOrderCodeSystems.Panel, p.Name, p.Name);
            var linea = await CotizarExamenAsync(organizationId, examen, ct);
            resultado.Add(new SellableItemDto("panel", p.Code, p.Name, examen, null, linea.UnitPrice, linea.PricePending));
        }

        var pruebas = await labTests.HandleAsync(
            new SearchLabTestsQuery(organizationId, t, null, ActiveOnly: true, limit, OrderableOnly: true), ct);
        foreach (var x in pruebas)
        {
            var examen = new ExternalOrderExam(x.LoincCode, x.Code, LabOrderCodeSystems.Test, x.Name, x.Name);
            var linea = await CotizarExamenAsync(organizationId, examen, ct);
            resultado.Add(new SellableItemDto("exam", x.Code, x.Name, examen, null, linea.UnitPrice, linea.PricePending));
        }

        var cobrables = (await billingCatalog.SearchItemsAsync(organizationId, t, activeOnly: true, limit * 2, ct))
            .Where(EsDeMostrador)
            .Take(limit);
        foreach (var i in cobrables)
        {
            var linea = await CotizarProcedimientoAsync(organizationId, new CounterSaleProcedure(i.Id), ct);
            resultado.Add(new SellableItemDto("procedure", i.Code, i.Name, null, i.Id, linea.UnitPrice, linea.PricePending));
        }

        // Productos de venta libre (F4): solo con caja mixta, y solo los que se pueden vender ya.
        if (userId is { } usuario && await CajaMixtaAsync(organizationId, usuario, ct))
        {
            var articulos = await inventory.SearchItemsAsync(organizationId, t, null, overTheCounter: true, activeOnly: true, limit, ct);
            foreach (var a in articulos.Where(a => a.Sellable))
            {
                var cobrable = a.BillableItemId is { } bid ? await billingCatalog.GetItemAsync(organizationId, bid, ct) : null;
                var precio = cobrable is null || cobrable.PricePending ? null : await prices.HandleAsync(organizationId, cobrable.Id, null, ct);
                resultado.Add(new SellableItemDto(
                    "product", a.Code, a.Name, null, cobrable?.Id, precio?.Price, precio is null, StockItemId: a.Id));
            }
        }

        return resultado.Take(limit * 3).ToList();
    }

    /// <summary>
    /// Lo que se vende como procedimiento: no es examen (va por la orden), ni consulta (va por la
    /// cita), ni producto o paquete (todavía no se venden en caja).
    /// </summary>
    private static bool EsDeMostrador(BillableItem i) =>
        i.LabTestId is null && i.LabPanelId is null && i.SpecialtyCode is null
        && i.Kind is not (BillableItemKind.Product or BillableItemKind.Package);

    /// <summary>
    /// Cotiza la venta sin crear nada: el paciente pregunta cuánto sale antes de decidir. Un examen
    /// que todavía no tiene cobrable o precio se puede vender (la orden se crea), pero no cobrar hasta
    /// que el jefe administrativo fije el precio.
    /// </summary>
    public async Task<CounterSaleQuote> QuoteAsync(
        Guid organizationId, IReadOnlyList<ExternalOrderExam>? exams, IReadOnlyList<CounterSaleProcedure>? procedures,
        CancellationToken ct = default)
    {
        var lineas = new List<CounterSaleQuoteLine>();
        foreach (var e in exams ?? [])
        {
            lineas.Add(await CotizarExamenAsync(organizationId, e, ct));
        }
        foreach (var p in procedures ?? [])
        {
            lineas.Add(await CotizarProcedimientoAsync(organizationId, p, ct));
        }
        return new CounterSaleQuote(
            lineas,
            Math.Round(lineas.Sum(l => l.Total ?? 0m), 2, MidpointRounding.AwayFromZero),
            lineas.Any(l => l.PricePending),
            lineas.Count > 0 && lineas.All(l => l.Problem is null));
    }

    /// <exception cref="ArgumentException">Venta vacía, examen fuera del catálogo o procedimiento que no es de mostrador.</exception>
    public async Task<CounterSaleResult> CreateAsync(
        Guid organizationId, Guid userId, CounterSaleInput input, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(input.PatientFhirId))
        {
            throw new ArgumentException("Elegí al paciente: lo clínico no se vende sin paciente.", nameof(input));
        }
        var examenes = input.Exams ?? [];
        var procedimientos = input.Procedures ?? [];
        var citasIds = (input.Appointments ?? []).Where(a => !string.IsNullOrWhiteSpace(a)).Distinct().ToList();
        var productos = input.Products ?? [];
        if (examenes.Count == 0 && procedimientos.Count == 0 && citasIds.Count == 0 && productos.Count == 0)
        {
            throw new ArgumentException("Agregá al menos un examen, un procedimiento, una consulta o un producto.", nameof(input));
        }

        // Los productos se validan antes de crear nada: de venta libre, con precio, en caja mixta.
        // La existencia la vuelve a exigir la venta al descontar: si falta, la orden ya creada
        // queda pendiente en la cola, como cualquier orden sin cobrar.
        if (productos.Count > 0)
        {
            if (!await CajaMixtaAsync(organizationId, userId, ct))
            {
                throw new ArgumentException(SinCajaMixta, nameof(input));
            }
            if (productos.GroupBy(p => p.StockItemId).Any(g => g.Count() > 1))
            {
                throw new ArgumentException("Un producto va una sola vez en la venta: sumá la cantidad en su línea.", nameof(input));
            }
            foreach (var p in productos)
            {
                if (p.Quantity <= 0)
                {
                    throw new ArgumentException("La cantidad de un producto tiene que ser mayor que cero.", nameof(input));
                }
                try
                {
                    await otc.ArticuloVendibleAsync(organizationId, p.StockItemId, ct);
                }
                catch (InvalidOperationException ex)
                {
                    throw new ArgumentException(ex.Message, nameof(input), ex);
                }
            }
        }

        // Las consultas se validan antes de crear nada, igual que los procedimientos.
        var consultas = new List<AppointmentConsultation>();
        if (citasIds.Count > 0
            && (await settings.GetAsync(organizationId, ct)).CollectionMode == BillingCollectionMode.AfterService)
        {
            throw new ArgumentException(SinModo, nameof(input));
        }
        foreach (var id in citasIds)
        {
            var c = await citas.ResolveAsync(organizationId, id, ct)
                ?? throw new ArgumentException("La cita no existe o no se puede cobrar.", nameof(input));
            if (await BloqueoDeCitaAsync(organizationId, c, input.PatientFhirId, ct) is { } bloqueo)
            {
                throw new ArgumentException(bloqueo, nameof(input));
            }
            if (c.Item.PricePending == true)
            {
                throw new ArgumentException(
                    $"«{c.Item.Name}» todavía no tiene precio: el jefe administrativo lo fija en Catálogo de cobrables → Sin precio.",
                    nameof(input));
            }
            consultas.Add(c);
        }

        // Los procedimientos se validan antes de crear nada: una orden creada para una venta que
        // después se rechaza quedaría suelta en el laboratorio.
        foreach (var p in procedimientos)
        {
            if ((await CotizarProcedimientoAsync(organizationId, p, ct)).Problem is { } problema)
            {
                throw new ArgumentException(problema, nameof(input));
            }
        }

        Guid cuentaId;
        string? requisicion = null;
        IReadOnlyList<string> ordenes = [];
        if (examenes.Count > 0)
        {
            // El mismo camino que la recepción: crear la orden externa y admitirla.
            var orden = await createOrder.HandleAsync(new CreateExternalOrderMultiCommand(
                organizationId, input.PatientFhirId.Trim(), input.RequesterName, null, null,
                input.Priority, null, null, "Vendida en caja", examenes, userId), ct);
            await checkIn.HandleAsync(organizationId, orden.Requisition, userId, ct);
            var cuenta = await accounts.FindByOriginAsync(organizationId, BillingAccountOrigin.LabOrder, orden.Requisition, ct)
                ?? throw new InvalidOperationException("La orden se creó pero no abrió su cuenta. Revisá la recepción del laboratorio.");
            cuentaId = cuenta.Id;
            requisicion = orden.Requisition;
            ordenes = orden.ServiceRequestIds;
        }
        else
        {
            // Solo procedimientos: la cuenta corriente del paciente, como la Orden de servicio.
            var cuenta = await openAccount.HandleAsync(new OpenAccountCommand(
                organizationId, null, input.PatientFhirId.Trim(), input.PatientName, null, "Running"), ct);
            cuentaId = cuenta.Id;
        }

        var lote = Guid.NewGuid().ToString("N")[..12];
        for (var i = 0; i < procedimientos.Count; i++)
        {
            var p = procedimientos[i];
            var r = await capture.HandleAsync(new CaptureChargeCommand(
                organizationId, cuentaId, p.BillableItemId, ChargeSourceKind.Manual, $"venta-{lote}-{i}",
                Quantity: p.Quantity, RequestedByUserId: userId, Reason: "Venta en caja"), ct);
            if (!r.Succeeded && !r.AlreadyCharged)
            {
                throw new InvalidOperationException(r.Rejected ?? "No se pudo cargar el procedimiento.");
            }
        }

        // Productos: por la venta libre de siempre (descuenta inventario FEFO), cargados a esta
        // cuenta y sin factura propia: los factura el cobro de la cuenta, junto con lo demás.
        if (productos.Count > 0)
        {
            await otc.SellAsync(organizationId, userId, new OverTheCounterSaleInput(
                $"caja-{lote}", null, input.PatientFhirId.Trim(), input.PatientName, null, null, "Venta en caja",
                productos.Select(p => new SaleLineInput(p.StockItemId, p.Quantity, null)).ToList(),
                Tenders: null, IntoAccountId: cuentaId, DeferInvoice: true), ct);
        }

        // La consulta queda atada a su cita: al iniciarla, el cobro de la consulta la reconoce y no
        // genera otro cargo (EncounterChargeReconciler, modo "antes" o "mixto").
        foreach (var c in consultas)
        {
            var r = await capture.HandleAsync(new CaptureChargeCommand(
                organizationId, cuentaId, c.Item.Id, ChargeSourceKind.Appointment, c.AppointmentId,
                RequestedByUserId: userId, PerformedAt: c.Start, PerformerPractitionerId: c.PractitionerId,
                SourceFhirRef: $"Appointment/{c.AppointmentId}", Reason: "Consulta cobrada en caja",
                LocationId: c.LocationId), ct);
            if (!r.Succeeded && !r.AlreadyCharged)
            {
                throw new InvalidOperationException(r.Rejected ?? "No se pudo cargar la consulta.");
            }
        }

        return new CounterSaleResult(cuentaId, requisicion, ordenes);
    }

    private async Task<CounterSaleQuoteLine> CotizarExamenAsync(Guid org, ExternalOrderExam e, CancellationToken ct)
    {
        var nombre = e.CodeDisplay ?? e.CodeText;
        var codigo = !string.IsNullOrWhiteSpace(e.LoincCode) ? e.LoincCode.Trim() : e.LocalCode?.Trim();
        var esPanel = string.Equals(e.LocalSystem, LabOrderCodeSystems.Panel, StringComparison.Ordinal);
        if (string.IsNullOrWhiteSpace(codigo))
        {
            return new CounterSaleQuoteLine(esPanel ? "panel" : "exam", nombre, 1m, null, null, false,
                $"«{nombre}» no está en el catálogo del laboratorio.");
        }

        Guid? testId = null, panelId = null;
        if (esPanel)
        {
            panelId = (await labCatalog.GetPanelByCodeOrLoincAsync(org, codigo, ct))?.Id;
        }
        else
        {
            testId = (await labCatalog.GetTestByCodeOrLoincAsync(org, codigo, ct))?.Id;
        }
        if (testId is null && panelId is null)
        {
            return new CounterSaleQuoteLine(esPanel ? "panel" : "exam", nombre, 1m, null, null, false,
                $"«{nombre}» no está en el catálogo del laboratorio.");
        }

        // El cobrable del examen se da de alta solo al admitir la primera orden; hasta entonces no hay precio.
        var item = await billingCatalog.FindAnyByLabAsync(org, testId, panelId, ct);
        if (item is null || item.PricePending)
        {
            return new CounterSaleQuoteLine(esPanel ? "panel" : "exam", nombre, 1m, null, null, true, null);
        }
        var precio = await prices.HandleAsync(org, item.Id, null, ct);
        return new CounterSaleQuoteLine(esPanel ? "panel" : "exam", nombre, 1m, precio?.Price, precio?.Price, false, null);
    }

    private async Task<CounterSaleQuoteLine> CotizarProcedimientoAsync(Guid org, CounterSaleProcedure p, CancellationToken ct)
    {
        var item = await billingCatalog.GetItemAsync(org, p.BillableItemId, ct);
        if (item is null || !item.Active)
        {
            return new CounterSaleQuoteLine("procedure", "(no existe)", p.Quantity, null, null, false,
                "El procedimiento no existe o está inactivo.");
        }
        if (p.Quantity <= 0)
        {
            return new CounterSaleQuoteLine("procedure", item.Name, p.Quantity, null, null, false, "La cantidad tiene que ser mayor que cero.");
        }
        // Lo mismo que la Orden de servicio: los exámenes van por la orden, las consultas por la cita y
        // los productos por la venta libre. Cargarlos acá los cobraría dos veces o sin descontar inventario.
        if (item.LabTestId is not null || item.LabPanelId is not null)
        {
            return Rechazo(item, p, $"«{item.Name}» es un examen: agregalo como examen para que llegue al laboratorio.");
        }
        if (item.SpecialtyCode is not null)
        {
            return Rechazo(item, p, $"«{item.Name}» es una consulta: todavía no se vende en caja.");
        }
        if (item.Kind is BillableItemKind.Product or BillableItemKind.Package)
        {
            return Rechazo(item, p, $"«{item.Name}» todavía no se vende en caja.");
        }
        if (item.PricePending)
        {
            return new CounterSaleQuoteLine("procedure", item.Name, p.Quantity, null, null, true, null);
        }
        var precio = await prices.HandleAsync(org, item.Id, null, ct);
        var total = precio is null ? (decimal?)null : Math.Round(precio.Price * p.Quantity, 2, MidpointRounding.AwayFromZero);
        return new CounterSaleQuoteLine("procedure", item.Name, p.Quantity, precio?.Price, total, false, null);
    }

    private static CounterSaleQuoteLine Rechazo(BillableItem item, CounterSaleProcedure p, string problema) =>
        new("procedure", item.Name, p.Quantity, null, null, false, problema);
}

/// <summary>Si la consulta de cada cita ya se cobró por adelantado.</summary>
/// <param name="Enabled">Falso con modo "después": no hay cobro anticipado que mostrar.</param>
public sealed record AppointmentPaymentStatusDto(bool Enabled, IReadOnlyList<AppointmentPaymentDto> Items);

/// <param name="Status">
/// <c>Paid</c>: cobrada y saldada. <c>Pending</c>: está en una venta, pero falta cobrarla (o
/// facturarla). <c>Unpaid</c>: nadie la cobró todavía; se cobra en caja o al iniciarla.
/// </param>
public sealed record AppointmentPaymentDto(string AppointmentId, string Status)
{
    public const string Paid = "Paid";
    public const string Pending = "Pending";
    public const string Unpaid = "Unpaid";
}
