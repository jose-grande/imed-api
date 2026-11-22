using System.Text.Json.Nodes;
using IMed.Api.Application.Abstractions;
using IMed.Api.Domain.Billing.Cash;
using IMed.Api.Domain.Billing.Invoicing;

namespace IMed.Api.Application.Billing;

// ── Corte de caja del día ───────────────────────────────────────────────────

/// <summary>Lo cobrado por un método de pago.</summary>
public sealed record MethodTotalDto(string Method, int Count, decimal Amount);

/// <summary>Un turno dentro del corte, con su arqueo.</summary>
public sealed record ShiftCutDto(
    Guid SessionId,
    Guid CashPointId,
    string CashPointName,
    Guid CashierUserId,
    DateTimeOffset OpenedAt,
    DateTimeOffset? ClosedAt,
    string Status,
    decimal OpeningAmount,
    decimal? CountedAmount,
    decimal? ExpectedAmount,
    decimal? Difference,
    string? DifferenceNote,
    IReadOnlyList<MethodTotalDto> ByMethod,
    decimal Withdrawals,
    decimal Deposits,
    IReadOnlyList<TerminalTotalDto>? ByTerminal = null);

/// <summary>
/// Corte de caja del día (FAC-8).
///
/// <para><b>Los turnos abiertos aparecen igual</b>, marcados como tales y sin arqueo. Un corte que
/// solo muestra lo cerrado le esconde al dueño la caja que quedó abierta desde ayer — que es
/// justamente lo que quiere ver.</para>
/// </summary>
public sealed record DailyCashCutDto(
    DateOnly Day,
    IReadOnlyList<ShiftCutDto> Shifts,
    IReadOnlyList<MethodTotalDto> TotalsByMethod,
    decimal TotalCollected,
    decimal TotalDifference,
    int OpenShifts,
    Guid? LegalEntityId = null,
    string? LegalName = null,
    string? TaxId = null,
    IReadOnlyList<TerminalTotalDto>? TotalsByTerminal = null);

public sealed class GetDailyCashCutHandler(
    ICashRepository cash, IFiscalRepository fiscal, IClinicClock clock, LegalEntityScopeResolver scopes,
    IPaymentTerminalRepository terminals)
{
    /// <param name="legalEntityId">Razón social (FAC-9): solo sus cajas. Obligatoria si hay varias.</param>
    public async Task<DailyCashCutDto> HandleAsync(
        Guid orgId, DateOnly? day, Guid? cashPointId, Guid? legalEntityId = null, CancellationToken ct = default)
    {
        var dia = day ?? clock.Today();
        var alcance = await scopes.ResolveAsync(orgId, legalEntityId, ct);
        var todasLasCajas = await fiscal.ListCashPointsAsync(orgId, ct);
        var cajasDeLaRazon = todasLasCajas
            .Where(c => alcance.LocationIds.Contains(c.LocationId))
            .Select(c => c.Id)
            .ToHashSet();
        var turnos = (await cash.ListSessionsAsync(orgId, cashPointId, dia, ct))
            .Where(t => cajasDeLaRazon.Contains(t.CashPointId))
            .ToList();
        var cajas = todasLasCajas.ToDictionary(c => c.Id, c => c.Name);

        var nombresTerminal = await TerminalNames.LoadAsync(terminals, orgId, ct);
        var todosLosPagos = new List<Payment>();
        var cortes = new List<ShiftCutDto>(turnos.Count);
        var globalPorMetodo = new Dictionary<PaymentMethod, (int Count, decimal Amount)>();

        foreach (var t in turnos)
        {
            var pagos = (await cash.ListPaymentsAsync(orgId, t.Id, ct))
                .Where(p => p.Status == PaymentStatus.Applied)
                .ToList();
            var movimientos = await cash.ListMovementsAsync(orgId, t.Id, ct);
            todosLosPagos.AddRange(pagos);

            var porMetodo = pagos
                .GroupBy(p => p.Method)
                .Select(g => new MethodTotalDto(
                    g.Key.ToString(), g.Count(),
                    Math.Round(g.Sum(p => p.AllocatedAmount), 2, MidpointRounding.AwayFromZero)))
                .OrderBy(m => m.Method)
                .ToList();

            foreach (var g in pagos.GroupBy(p => p.Method))
            {
                var previo = globalPorMetodo.GetValueOrDefault(g.Key);
                globalPorMetodo[g.Key] =
                    (previo.Count + g.Count(), previo.Amount + g.Sum(p => p.AllocatedAmount));
            }

            cortes.Add(new ShiftCutDto(
                t.Id, t.CashPointId, cajas.GetValueOrDefault(t.CashPointId, "(caja eliminada)"),
                t.CashierUserId, t.OpenedAt, t.ClosedAt, t.Status.ToString(),
                t.OpeningAmount, t.CountedAmount, t.ExpectedAmount, t.Difference, t.DifferenceNote,
                porMetodo,
                Withdrawals: Sum(movimientos, CashMovementKind.Withdrawal),
                Deposits: Sum(movimientos, CashMovementKind.Deposit),
                ByTerminal: TerminalNames.Totals(pagos, nombresTerminal, p => p.AllocatedAmount)));
        }

        var totales = globalPorMetodo
            .Select(kv => new MethodTotalDto(
                kv.Key.ToString(), kv.Value.Count,
                Math.Round(kv.Value.Amount, 2, MidpointRounding.AwayFromZero)))
            .OrderBy(m => m.Method)
            .ToList();

        return new DailyCashCutDto(
            dia, cortes, totales,
            TotalCollected: Math.Round(totales.Sum(t => t.Amount), 2, MidpointRounding.AwayFromZero),
            // Solo los turnos cerrados tienen diferencia; los abiertos suman cero al descuadre.
            TotalDifference: Math.Round(
                cortes.Sum(c => c.Difference ?? 0m), 2, MidpointRounding.AwayFromZero),
            OpenShifts: cortes.Count(c => c.Status == nameof(CashSessionStatus.Open)),
            LegalEntityId: alcance.LegalEntity?.Id,
            LegalName: alcance.LegalEntity?.LegalName,
            TaxId: alcance.LegalEntity?.TaxId,
            TotalsByTerminal: TerminalNames.Totals(todosLosPagos, nombresTerminal, p => p.AllocatedAmount));
    }

    private static decimal Sum(IEnumerable<CashMovement> movements, CashMovementKind kind) =>
        Math.Round(
            movements.Where(m => m.Kind == kind).Sum(m => m.Amount), 2, MidpointRounding.AwayFromZero);
}

// ── Reporte de cajas por sede ───────────────────────────────────────────────

/// <summary>Un turno en el reporte de cajas, con su sede y el nombre de quien lo trabajó.</summary>
public sealed record CashReportShiftDto(
    Guid SessionId,
    DateOnly Day,
    Guid LocationId,
    string LocationName,
    Guid CashPointId,
    string CashPointName,
    Guid CashierUserId,
    string? CashierName,
    DateTimeOffset OpenedAt,
    DateTimeOffset? ClosedAt,
    string Status,
    decimal OpeningAmount,
    decimal? CountedAmount,
    decimal? ExpectedAmount,
    decimal? Difference,
    string? DifferenceNote,
    IReadOnlyList<MethodTotalDto> ByMethod,
    decimal TotalCollected,
    decimal Withdrawals,
    decimal Deposits,
    IReadOnlyList<TerminalTotalDto>? ByTerminal = null);

/// <summary>Totales de un grupo (una sede o un cajero).</summary>
public sealed record CashReportGroupDto(
    Guid Id,
    string Name,
    int Shifts,
    int OpenShifts,
    IReadOnlyList<MethodTotalDto> ByMethod,
    decimal TotalCollected,
    decimal TotalDifference);

/// <summary>Lo cobrado por día en el rango.</summary>
public sealed record CashReportDayDto(DateOnly Day, decimal TotalCollected, decimal TotalDifference, int Shifts);

public sealed record CashReportDto(
    DateOnly From,
    DateOnly To,
    Guid? LocationId,
    Guid? CashierUserId,
    IReadOnlyList<CashReportGroupDto> ByLocation,
    IReadOnlyList<CashReportGroupDto> ByCashier,
    IReadOnlyList<CashReportDayDto> ByDay,
    IReadOnlyList<CashReportShiftDto> Shifts,
    IReadOnlyList<MethodTotalDto> TotalsByMethod,
    decimal TotalCollected,
    decimal TotalDifference,
    int OpenShifts,
    IReadOnlyList<TerminalTotalDto>? TotalsByTerminal = null);

/// <summary>
/// Reporte de cajas por sede (UX-Cobro-Anticipado F1): turnos de un rango de días, con totales
/// por sede, por cajero, por día y por método.
///
/// <para><b>Es del jefe administrativo</b> (<c>billing.supervise</c>): muestra lo cobrado de los
/// turnos abiertos, efectivo incluido. El cajero no lo ve: para él el arqueo es a ciegas.</para>
///
/// <para>El día de un turno es el día de la clínica en que se abrió. El esperado, lo contado y la
/// diferencia solo existen en los turnos cerrados.</para>
/// </summary>
public sealed class GetCashReportHandler(
    ICashRepository cash,
    IFiscalRepository fiscal,
    ILocationRepository locations,
    IOrganizationMemberRepository members,
    IClinicClock clock,
    IPaymentTerminalRepository terminals)
{
    /// <summary>Tope del rango: un reporte de más de un trimestre se vuelve una exportación, no una pantalla.</summary>
    public const int MaxDays = 93;

    /// <exception cref="ArgumentException">Rango invertido o de más de <see cref="MaxDays"/> días.</exception>
    public async Task<CashReportDto> HandleAsync(
        Guid orgId, DateOnly? from, DateOnly? to, Guid? locationId, Guid? cashierUserId, CancellationToken ct = default)
    {
        var hasta = to ?? clock.Today();
        var desde = from ?? hasta;
        if (desde > hasta)
        {
            throw new ArgumentException("La fecha inicial es posterior a la final.", nameof(from));
        }
        if (hasta.DayNumber - desde.DayNumber + 1 > MaxDays)
        {
            throw new ArgumentException($"El rango máximo es de {MaxDays} días.", nameof(to));
        }

        var cajas = (await fiscal.ListCashPointsAsync(orgId, ct)).ToDictionary(c => c.Id);
        var sedes = (await locations.ListAsync(orgId, activeOnly: false, ct)).ToDictionary(l => l.Id, l => l.Name);
        var nombres = (await members.ListByOrganizationAsync(orgId, ct))
            .GroupBy(m => m.KeycloakUserId)
            .ToDictionary(g => g.Key, g => g.First().DisplayName ?? g.First().Email);

        var turnos = (await cash.ListSessionsOpenedBetweenAsync(
                orgId, clock.StartOf(desde), clock.StartOf(hasta.AddDays(1)), ct))
            .Where(t => cajas.ContainsKey(t.CashPointId))
            .Where(t => locationId is not { } sede || cajas[t.CashPointId].LocationId == sede)
            .Where(t => cashierUserId is not { } cajero || t.CashierUserId == cajero)
            .ToList();
        var ids = turnos.Select(t => t.Id).ToList();
        var pagos = (await cash.ListPaymentsForSessionsAsync(orgId, ids, ct))
            .Where(p => p.Status == PaymentStatus.Applied)
            .ToLookup(p => p.CashSessionId);
        var movimientos = (await cash.ListMovementsForSessionsAsync(orgId, ids, ct)).ToLookup(m => m.CashSessionId);
        var nombresTerminal = await TerminalNames.LoadAsync(terminals, orgId, ct);

        var filas = turnos.Select(t =>
        {
            var caja = cajas[t.CashPointId];
            var porMetodo = PorMetodo(pagos[t.Id]);
            return new CashReportShiftDto(
                t.Id, clock.DateOf(t.OpenedAt), caja.LocationId, sedes.GetValueOrDefault(caja.LocationId, "(sede eliminada)"),
                caja.Id, caja.Name, t.CashierUserId, nombres.GetValueOrDefault(t.CashierUserId),
                t.OpenedAt, t.ClosedAt, t.Status.ToString(),
                t.OpeningAmount, t.CountedAmount, t.ExpectedAmount, t.Difference, t.DifferenceNote,
                porMetodo, Redondear(porMetodo.Sum(m => m.Amount)),
                Withdrawals: Redondear(movimientos[t.Id].Where(m => m.Kind == CashMovementKind.Withdrawal).Sum(m => m.Amount)),
                Deposits: Redondear(movimientos[t.Id].Where(m => m.Kind == CashMovementKind.Deposit).Sum(m => m.Amount)),
                ByTerminal: TerminalNames.Totals(pagos[t.Id], nombresTerminal, p => p.AllocatedAmount));
        }).ToList();

        var totales = PorMetodo(turnos.SelectMany(t => pagos[t.Id]));
        return new CashReportDto(
            desde, hasta, locationId, cashierUserId,
            ByLocation: Agrupar(filas, f => f.LocationId, f => f.LocationName, pagos),
            ByCashier: Agrupar(filas, f => f.CashierUserId, f => f.CashierName ?? "Cajero sin nombre registrado", pagos),
            ByDay: filas.GroupBy(f => f.Day).OrderBy(g => g.Key)
                .Select(g => new CashReportDayDto(
                    g.Key, Redondear(g.Sum(f => f.TotalCollected)), Redondear(g.Sum(f => f.Difference ?? 0m)), g.Count()))
                .ToList(),
            Shifts: filas.OrderByDescending(f => f.OpenedAt).ToList(),
            TotalsByMethod: totales,
            TotalCollected: Redondear(totales.Sum(m => m.Amount)),
            // Solo los turnos cerrados tienen diferencia; los abiertos suman cero al descuadre.
            TotalDifference: Redondear(filas.Sum(f => f.Difference ?? 0m)),
            OpenShifts: filas.Count(f => f.Status == nameof(CashSessionStatus.Open)),
            TotalsByTerminal: TerminalNames.Totals(turnos.SelectMany(t => pagos[t.Id]), nombresTerminal, p => p.AllocatedAmount));
    }

    private static List<CashReportGroupDto> Agrupar(
        IReadOnlyList<CashReportShiftDto> filas,
        Func<CashReportShiftDto, Guid> clave,
        Func<CashReportShiftDto, string> nombre,
        ILookup<Guid, Payment> pagos) =>
        filas.GroupBy(clave)
            .Select(g =>
            {
                var porMetodo = PorMetodo(g.SelectMany(f => pagos[f.SessionId]));
                return new CashReportGroupDto(
                    g.Key, nombre(g.First()), g.Count(),
                    g.Count(f => f.Status == nameof(CashSessionStatus.Open)),
                    porMetodo, Redondear(porMetodo.Sum(m => m.Amount)),
                    Redondear(g.Sum(f => f.Difference ?? 0m)));
            })
            .OrderByDescending(g => g.TotalCollected)
            .ToList();

    /// <summary>Lo aplicado a facturas, por método: en efectivo, neto de vuelto.</summary>
    private static List<MethodTotalDto> PorMetodo(IEnumerable<Payment> pagos) =>
        pagos.GroupBy(p => p.Method)
            .Select(g => new MethodTotalDto(g.Key.ToString(), g.Count(), Redondear(g.Sum(p => p.AllocatedAmount))))
            .OrderBy(m => m.Method)
            .ToList();

    private static decimal Redondear(decimal v) => Math.Round(v, 2, MidpointRounding.AwayFromZero);
}

// ── Reporte de ISV ──────────────────────────────────────────────────────────

/// <summary>
/// Lo que el contador necesita para declarar (FAC-8).
///
/// <para><b>Los tratamientos van separados y no se suman entre sí:</b> exento, exonerado y cada
/// tasa gravada ocupan renglones distintos de la declaración. Un total único obliga al contador a
/// recalcular a mano, que es exactamente el trabajo que este reporte viene a evitar.</para>
///
/// <para><b>Lo anulado no cuenta.</b> Un documento anulado no generó obligación tributaria, y
/// sumarlo declararía de más.</para>
/// </summary>
public sealed record TaxReportDto(
    DateOnly From,
    DateOnly To,
    int DocumentCount,
    int VoidedCount,
    decimal SubtotalExempt,
    decimal SubtotalExonerated,
    decimal SubtotalTaxed15,
    decimal SubtotalTaxed18,
    decimal DiscountTotal,
    decimal TaxTotal,
    decimal GrandTotal,
    Guid? LegalEntityId = null,
    string? LegalName = null,
    string? TaxId = null,
    /// <summary>Notas de crédito del período. Sus montos ya están restados de los totales (A9).</summary>
    int CreditNoteCount = 0);

public sealed class GetTaxReportHandler(
    IFiscalReportRepository repo, IClinicClock clock, LegalEntityScopeResolver scopes, IFiscalRepository fiscal)
{
    /// <param name="legalEntityId">Razón social que declara (FAC-9). Obligatoria si hay varias.</param>
    public async Task<TaxReportDto> HandleAsync(
        Guid orgId, DateOnly? since, DateOnly? until, Guid? legalEntityId = null, CancellationToken ct = default)
    {
        var hoy = clock.Today();
        var desde = since ?? new DateOnly(hoy.Year, hoy.Month, 1);
        var hasta = until ?? hoy;
        var alcance = await scopes.ResolveAsync(orgId, legalEntityId, ct);

        var docs = (await repo.ListDocumentsAsync(orgId, desde, hasta, ct))
            .Where(d => alcance.Owns(d.LegalEntityId, d.LocationId))
            .ToList();
        var vigentes = docs.Where(d => d.Status != FiscalDocumentStatus.Voided).ToList();

        // Cada tipo de documento declara su signo: la nota de crédito (−1) resta lo que revirtió.
        // Sumarlas como si fueran facturas declararía de más exactamente lo que se devolvió.
        var signos = (await fiscal.ListDocumentTypesAsync(orgId, ct))
            .ToDictionary(t => t.Id, t => t.Sign == 0 ? (short)1 : t.Sign);
        decimal Neto(Func<FiscalDocument, decimal> monto) =>
            Round(vigentes.Sum(d => monto(d) * signos.GetValueOrDefault(d.DocumentTypeId, (short)1)));
        var notas = vigentes.Count(d => signos.GetValueOrDefault(d.DocumentTypeId, (short)1) < 0);

        return new TaxReportDto(
            desde, hasta,
            DocumentCount: vigentes.Count - notas,
            VoidedCount: docs.Count - vigentes.Count,
            SubtotalExempt: Neto(d => d.SubtotalExempt),
            SubtotalExonerated: Neto(d => d.SubtotalExonerated),
            SubtotalTaxed15: Neto(d => d.SubtotalTaxed15),
            SubtotalTaxed18: Neto(d => d.SubtotalTaxed18),
            DiscountTotal: Neto(d => d.DiscountTotal),
            TaxTotal: Neto(d => d.TaxTotal),
            GrandTotal: Neto(d => d.GrandTotal),
            LegalEntityId: alcance.LegalEntity?.Id,
            LegalName: alcance.LegalEntity?.LegalName,
            TaxId: alcance.LegalEntity?.TaxId,
            CreditNoteCount: notas);
    }

    private static decimal Round(decimal v) => Math.Round(v, 2, MidpointRounding.AwayFromZero);
}

// ── Libro de documentos emitidos ────────────────────────────────────────────

public sealed record IssuedDocumentDto(
    string FullNumber,
    long Sequence,
    DateTimeOffset IssuedAt,
    string BuyerName,
    string? BuyerTaxId,
    decimal GrandTotal,
    string Status,
    string? VoidReason);

/// <summary>
/// Un correlativo que la autorización tenía y que no aparece emitido.
///
/// <para><b>Una auditoría pregunta por los huecos.</b> No basta con no tenerlos: hay que poder
/// explicarlos. Un rango contiguo se reporta como un solo hueco —«del 45 al 78»— porque listar
/// treinta y cuatro filas idénticas esconde el dato en vez de mostrarlo.</para>
/// </summary>
/// <param name="Explanation">
/// Qué se sabe del hueco. Hoy solo hay una explicación posible —el número se reservó y la emisión
/// no llegó a completarse—; cuando existan más causas, esto las distingue.
/// </param>
public sealed record SequenceGapDto(long From, long To, int Count, string Explanation);

public sealed record DocumentLedgerDto(
    Guid FiscalAuthorizationId,
    string Cai,
    string EmissionPointCode,
    long RangeFrom,
    long RangeTo,
    long NextSequence,
    DateOnly LimitDate,
    string Status,
    IReadOnlyList<IssuedDocumentDto> Documents,
    IReadOnlyList<SequenceGapDto> Gaps,
    int VoidedCount);

public sealed class GetDocumentLedgerHandler(
    IFiscalReportRepository repo, IFiscalRepository fiscal)
{
    public async Task<DocumentLedgerDto?> HandleAsync(
        Guid orgId, Guid authorizationId, CancellationToken ct = default)
    {
        var auth = await fiscal.GetAuthorizationAsync(orgId, authorizationId, ct);
        if (auth is null)
        {
            return null;
        }

        var docs = (await repo.ListByAuthorizationAsync(orgId, authorizationId, ct))
            .OrderBy(d => d.Sequence)
            .ToList();

        return new DocumentLedgerDto(
            auth.Id, auth.Cai, auth.EmissionPointCode, auth.RangeFrom, auth.RangeTo,
            auth.NextSequence, auth.LimitDate, auth.Status.ToString(),
            docs.Select(d => new IssuedDocumentDto(
                d.FullNumber, d.Sequence, d.IssuedAt, d.BuyerName, d.BuyerTaxId,
                d.GrandTotal, d.Status.ToString(), d.VoidReason)).ToList(),
            // Los huecos se buscan hasta `NextSequence - 1`: lo que todavía no se entregó no es un
            // hueco, es rango disponible.
            DetectGaps(docs.Select(d => d.Sequence), auth.RangeFrom, auth.NextSequence - 1),
            docs.Count(d => d.Status == FiscalDocumentStatus.Voided));
    }

    /// <summary>
    /// Encuentra los correlativos entregados que no tienen documento.
    ///
    /// <para>Se expone y se prueba por su cuenta: es aritmética de rangos —fácil de equivocar en
    /// los bordes— y el resultado va a una auditoría. Probarla solo a través del handler exigiría
    /// sembrar una autorización y varios documentos por cada borde, y en la práctica eso significa
    /// que los bordes no se prueban.</para>
    /// </summary>
    public static IReadOnlyList<SequenceGapDto> DetectGaps(
        IEnumerable<long> issued, long rangeFrom, long lastDelivered)
    {
        const string Explanation =
            "El número se reservó y la emisión no llegó a completarse. No se reutiliza.";

        var presentes = issued.ToHashSet();
        var gaps = new List<SequenceGapDto>();

        long? inicio = null;
        for (var n = rangeFrom; n <= lastDelivered; n++)
        {
            if (presentes.Contains(n))
            {
                if (inicio is { } desde)
                {
                    gaps.Add(new SequenceGapDto(desde, n - 1, (int)(n - desde), Explanation));
                    inicio = null;
                }
            }
            else
            {
                inicio ??= n;
            }
        }

        if (inicio is { } ultimo)
        {
            gaps.Add(new SequenceGapDto(
                ultimo, lastDelivered, (int)(lastDelivered - ultimo + 1), Explanation));
        }

        return gaps;
    }
}

// ── Reporte de excepciones ──────────────────────────────────────────────────

public sealed record ExceptionRowDto(
    string Kind,
    DateTimeOffset OccurredAt,
    string Description,
    decimal? Amount,
    Guid? RequestedBy,
    Guid? AuthorizedBy,
    string? Reason);

/// <summary>
/// Todo lo que salió del camino normal (FAC-8): cargos tecleados a mano, descuentos que alguien
/// tuvo que autorizar, servicios dados sin cobrar, arqueos que no cuadraron y consultas cobradas
/// por adelantado cuya cita se canceló.
///
/// <para>Van juntos y no en cuatro reportes porque la pregunta del dueño es una sola: <i>"¿qué
/// pasó fuera de lo normal y quién lo autorizó?"</i>. Repartirlo en cuatro pantallas obliga a
/// cruzarlas a mano para contestarla.</para>
/// </summary>
public sealed record ExceptionReportDto(
    DateOnly From,
    DateOnly To,
    IReadOnlyList<ExceptionRowDto> Rows,
    int ManualCharges,
    int AuthorizedDiscounts,
    int WaivedServices,
    int CashDifferences,
    int CancelledPrepaidAppointments);

public sealed class GetExceptionReportHandler(
    IFiscalReportRepository repo, IServiceWaiverRepository waivers,
    ICashRepository cash, IClinicClock clock, IHapiFhirClient hapi)
{
    /// <summary>Estados de una cita que ya no se va a atender.</summary>
    private static readonly Dictionary<string, string> CitaMuerta = new(StringComparer.Ordinal)
    {
        ["cancelled"] = "se canceló",
        ["noshow"] = "el paciente no se presentó",
        ["entered-in-error"] = "se anuló por error",
    };

    public async Task<ExceptionReportDto> HandleAsync(
        Guid orgId, DateOnly? since, DateOnly? until, CancellationToken ct = default)
    {
        var hoy = clock.Today();
        var desde = since ?? new DateOnly(hoy.Year, hoy.Month, 1);
        var hasta = until ?? hoy;

        var filas = new List<ExceptionRowDto>();

        var manuales = await repo.ListManualChargesAsync(orgId, desde, hasta, ct);
        foreach (var c in manuales)
        {
            filas.Add(new ExceptionRowDto(
                "ManualCharge", c.CreatedAt, c.DescriptionSnapshot, c.Total,
                RequestedBy: null, AuthorizedBy: null, c.Reason));
        }

        var descuentos = await repo.ListAuthorizedDiscountsAsync(orgId, desde, hasta, ct);
        foreach (var c in descuentos)
        {
            filas.Add(new ExceptionRowDto(
                "AuthorizedDiscount", c.CreatedAt,
                $"Descuento sobre «{c.DescriptionSnapshot}»", c.DiscountAmount,
                RequestedBy: null, c.DiscountAuthorizedBy, c.Reason));
        }

        var exonerados = await waivers.ListAsync(orgId, desde, hasta, ct);
        foreach (var w in exonerados)
        {
            filas.Add(new ExceptionRowDto(
                "WaivedService", w.WaivedAt, w.SourceRef, Amount: null,
                RequestedBy: null, w.WaivedBy, w.Reason));
        }

        var turnos = await cash.ListSessionsAsync(orgId, null, null, ct);
        var descuadres = turnos
            .Where(t => t.Difference is not null && t.Difference != 0
                        && t.ClosedAt is { } cerrado
                        // Día de la clínica: un turno cerrado a las 20:00 es de hoy, no de mañana.
                        && clock.DateOf(cerrado) >= desde
                        && clock.DateOf(cerrado) <= hasta)
            .ToList();

        foreach (var t in descuadres)
        {
            filas.Add(new ExceptionRowDto(
                "CashDifference", t.ClosedAt!.Value,
                $"Arqueo con diferencia de L {t.Difference:N2}", t.Difference,
                t.CashierUserId, t.DifferenceAuthorizedBy, t.DifferenceNote));
        }

        var citasMuertas = await CitasPagadasCanceladasAsync(orgId, desde, hasta, ct);
        filas.AddRange(citasMuertas);

        return new ExceptionReportDto(
            desde, hasta,
            filas.OrderByDescending(f => f.OccurredAt).ToList(),
            manuales.Count, descuentos.Count, exonerados.Count, descuadres.Count, citasMuertas.Count);
    }

    /// <summary>
    /// Consultas pagadas en caja cuya cita ya no se va a atender. Es dinero cobrado por un servicio
    /// que no se va a dar: hay que reprogramar la cita (el pago la sigue) o devolverlo con nota de
    /// crédito. Mientras nadie lo resuelva, aparece en todos los reportes que cubran su fecha: es un
    /// pendiente, no un hecho del pasado.
    ///
    /// <para>Cancelar y agendar otra cita no mueve el pago: la cita nueva se cobraría de nuevo. Por
    /// eso esta fila existe.</para>
    /// </summary>
    private async Task<List<ExceptionRowDto>> CitasPagadasCanceladasAsync(
        Guid orgId, DateOnly desde, DateOnly hasta, CancellationToken ct)
    {
        var filas = new List<ExceptionRowDto>();
        var cargos = await repo.ListLivePrepaidConsultationsAsync(orgId, desde, hasta, ct);
        var particion = $"org-{orgId}";
        foreach (var cargo in cargos)
        {
            JsonObject? cita;
            try
            {
                cita = await hapi.ReadResourceAsync("Appointment", cargo.SourceId, particion, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Un HAPI caído no puede tumbar el reporte entero: las otras excepciones son de la base.
                continue;
            }
            string motivo;
            if (cita is null)
            {
                motivo = "la cita ya no existe";
            }
            else if (!CitaMuerta.TryGetValue(cita["status"]?.GetValue<string>() ?? string.Empty, out motivo!))
            {
                continue;
            }
            var cuando = DateTimeOffset.TryParse(cita?["meta"]?["lastUpdated"]?.GetValue<string>(), out var t)
                ? t
                : cargo.PerformedAt;
            filas.Add(new ExceptionRowDto(
                "CancelledPrepaidAppointment", cuando,
                $"«{cargo.DescriptionSnapshot}» pagada en caja, pero la cita del {clock.DateOf(cargo.PerformedAt):dd/MM/yyyy} {motivo}",
                cargo.Total, RequestedBy: null, AuthorizedBy: null,
                "Reprogramar la cita (el pago la sigue) o devolver con nota de crédito."));
        }
        return filas;
    }
}
