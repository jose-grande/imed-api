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
    decimal Deposits);

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
    string? TaxId = null);

public sealed class GetDailyCashCutHandler(
    ICashRepository cash, IFiscalRepository fiscal, IClinicClock clock, LegalEntityScopeResolver scopes)
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

        var cortes = new List<ShiftCutDto>(turnos.Count);
        var globalPorMetodo = new Dictionary<PaymentMethod, (int Count, decimal Amount)>();

        foreach (var t in turnos)
        {
            var pagos = (await cash.ListPaymentsAsync(orgId, t.Id, ct))
                .Where(p => p.Status == PaymentStatus.Applied)
                .ToList();
            var movimientos = await cash.ListMovementsAsync(orgId, t.Id, ct);

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
                Deposits: Sum(movimientos, CashMovementKind.Deposit)));
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
            TaxId: alcance.LegalEntity?.TaxId);
    }

    private static decimal Sum(IEnumerable<CashMovement> movements, CashMovementKind kind) =>
        Math.Round(
            movements.Where(m => m.Kind == kind).Sum(m => m.Amount), 2, MidpointRounding.AwayFromZero);
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
    string? TaxId = null);

public sealed class GetTaxReportHandler(IFiscalReportRepository repo, IClinicClock clock, LegalEntityScopeResolver scopes)
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

        return new TaxReportDto(
            desde, hasta,
            DocumentCount: vigentes.Count,
            VoidedCount: docs.Count - vigentes.Count,
            SubtotalExempt: Round(vigentes.Sum(d => d.SubtotalExempt)),
            SubtotalExonerated: Round(vigentes.Sum(d => d.SubtotalExonerated)),
            SubtotalTaxed15: Round(vigentes.Sum(d => d.SubtotalTaxed15)),
            SubtotalTaxed18: Round(vigentes.Sum(d => d.SubtotalTaxed18)),
            DiscountTotal: Round(vigentes.Sum(d => d.DiscountTotal)),
            TaxTotal: Round(vigentes.Sum(d => d.TaxTotal)),
            GrandTotal: Round(vigentes.Sum(d => d.GrandTotal)),
            LegalEntityId: alcance.LegalEntity?.Id,
            LegalName: alcance.LegalEntity?.LegalName,
            TaxId: alcance.LegalEntity?.TaxId);
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
/// tuvo que autorizar, servicios dados sin cobrar y arqueos que no cuadraron.
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
    int CashDifferences);

public sealed class GetExceptionReportHandler(
    IFiscalReportRepository repo, IServiceWaiverRepository waivers,
    ICashRepository cash, IClinicClock clock)
{
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
                        && DateOnly.FromDateTime(cerrado.UtcDateTime) >= desde
                        && DateOnly.FromDateTime(cerrado.UtcDateTime) <= hasta)
            .ToList();

        foreach (var t in descuadres)
        {
            filas.Add(new ExceptionRowDto(
                "CashDifference", t.ClosedAt!.Value,
                $"Arqueo con diferencia de L {t.Difference:N2}", t.Difference,
                t.CashierUserId, t.DifferenceAuthorizedBy, t.DifferenceNote));
        }

        return new ExceptionReportDto(
            desde, hasta,
            filas.OrderByDescending(f => f.OccurredAt).ToList(),
            manuales.Count, descuentos.Count, exonerados.Count, descuadres.Count);
    }
}
