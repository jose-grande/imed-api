using IMed.Api.Application.Abstractions;
using IMed.Api.Domain.Billing;
using IMed.Api.Domain.Billing.Accounts;
using IMed.Api.Domain.Billing.Catalog;
using IMed.Api.Application.Locations;

namespace IMed.Api.Application.Billing;

public sealed record ChargeDto(
    Guid Id,
    Guid AccountId,
    Guid BillableItemId,
    string Description,
    decimal Quantity,
    decimal UnitPrice,
    string PriceSource,
    string? PriceListName,
    decimal DiscountAmount,
    Guid? DiscountAuthorizedBy,
    decimal TaxRate,
    decimal NetAmount,
    decimal TaxAmount,
    decimal Total,
    string Status,
    string SourceKind,
    string SourceId,
    string? SourceFhirRef,
    string? Reason,
    DateTimeOffset PerformedAt,
    Guid LocationId,
    bool PricePending = false);

/// <summary>
/// Qué pasó al intentar capturar un cargo.
/// </summary>
/// <param name="Charge">El cargo, nuevo o el que ya existía.</param>
/// <param name="AlreadyCharged">
/// <c>true</c> si el acto ya estaba cobrado. <b>No es un error</b>: es el resultado correcto de
/// reprocesar algo, y el llamador —el laboratorio admitiendo una orden, el cierre de un
/// encuentro— tiene que poder seguir adelante sin ruido.
/// </param>
public sealed record ChargeCaptureResult(ChargeDto? Charge, bool AlreadyCharged, string? Rejected)
{
    public bool Succeeded => Charge is not null;

    public static ChargeCaptureResult Captured(ChargeDto c) => new(c, false, null);
    public static ChargeCaptureResult Duplicate(ChargeDto c) => new(c, true, null);
    public static ChargeCaptureResult Reject(string reason) => new(null, false, reason);
}

public static class ChargeMapper
{
    public static ChargeDto ToDto(BillingCharge c, string? priceListName = null) =>
        new(c.Id, c.AccountId, c.BillableItemId, c.DescriptionSnapshot, c.Quantity, c.UnitPrice,
            c.PriceSource.ToString(), priceListName, c.DiscountAmount, c.DiscountAuthorizedBy,
            c.TaxRate, c.NetAmount, c.TaxAmount, c.Total, c.Status.ToString(),
            c.SourceKind.ToString(), c.SourceId, c.SourceFhirRef, c.Reason, c.PerformedAt, c.LocationId,
            c.PricePending);
}

public sealed record CaptureChargeCommand(
    Guid OrganizationId,
    Guid AccountId,
    Guid BillableItemId,
    ChargeSourceKind SourceKind,
    string SourceId,
    decimal Quantity = 1m,
    decimal DiscountAmount = 0m,
    Guid? RequestedByUserId = null,
    Guid? DiscountAuthorizedBy = null,
    DateTimeOffset? PerformedAt = null,
    string? PerformerPractitionerId = null,
    string? SourceFhirRef = null,
    string? Reason = null,
    Guid? PayerContractId = null,
    Guid? LocationId = null);

/// <summary>
/// Captura un cargo en una cuenta (FAC-4).
///
/// <para><b>El precio se resuelve una vez y se congela.</b> A partir de acá el cargo no vuelve a
/// mirar el catálogo: subir el precio en el tarifario no puede cambiar lo que ya se le cobró a
/// alguien.</para>
///
/// <para><b>La idempotencia se resuelve en dos capas.</b> Primero una lectura —barata y suficiente
/// para el 99% de los casos— y después el índice único de la base, que es el que decide cuando dos
/// caminos llegan a la vez. Solo la primera capa sería una condición de carrera; solo la segunda
/// haría que un reproceso normal se viera como un error.</para>
/// </summary>
public sealed class CaptureChargeHandler(
    IBillingAccountRepository accounts,
    IBillingCatalogRepository catalog,
    IFiscalRepository fiscal,
    IAuthorizationLimitRepository limits,
    IClinicClock clock,
    ResolveActiveLocationHandler activeLocation)
{
    public async Task<ChargeCaptureResult> HandleAsync(
        CaptureChargeCommand cmd, CancellationToken ct = default)
    {
        var cuenta = await accounts.GetAccountAsync(cmd.OrganizationId, cmd.AccountId, ct);
        if (cuenta is null)
        {
            return ChargeCaptureResult.Reject("La cuenta no existe.");
        }
        if (!cuenta.AcceptsCharges)
        {
            return ChargeCaptureResult.Reject(
                $"La cuenta está en estado '{cuenta.Status}' y no acepta cargos.");
        }

        var item = await catalog.GetItemAsync(cmd.OrganizationId, cmd.BillableItemId, ct);
        if (item is null)
        {
            return ChargeCaptureResult.Reject("El cobrable no existe.");
        }

        // Primera capa: ¿ya se cobró este acto?
        var existente = await accounts.FindLiveChargeBySourceAsync(
            cmd.OrganizationId, cmd.SourceKind, cmd.SourceId, cmd.BillableItemId, ct);
        if (existente is not null)
        {
            return ChargeCaptureResult.Duplicate(ChargeMapper.ToDto(existente));
        }

        var today = clock.Today();
        var candidatos = await catalog.GetPriceCandidatesAsync(cmd.OrganizationId, item.Id, ct);
        var precio = PriceResolver.Resolve(item, candidatos, today, cmd.PayerContractId);

        var tributo = await fiscal.GetTaxCodeAsync(cmd.OrganizationId, item.TaxCodeId, ct);
        if (tributo is null)
        {
            return ChargeCaptureResult.Reject(
                "El tributo del cobrable no existe: no se puede saber si la línea paga impuesto.");
        }

        // Sin precio (D1): el cargo entra —el servicio se prestó— y espera a que alguien fije el
        // precio. No se resuelve contra tarifarios: el precio base es la decisión que falta.
        if (item.PricePending)
        {
            if (cmd.DiscountAmount > 0)
            {
                return ChargeCaptureResult.Reject(
                    $"{item.Name} todavía no tiene precio: no se le puede aplicar descuento.");
            }
            precio = new ResolvedPrice(0m, PriceSource.ItemDefault, null, null, null, null);
        }

        // ── Descuento: tope del usuario y piso del tarifario ──
        if (cmd.DiscountAmount > 0)
        {
            var rechazo = await ValidarDescuentoAsync(cmd, precio, today, ct);
            if (rechazo is not null)
            {
                return ChargeCaptureResult.Reject(rechazo);
            }
        }

        // Sede del servicio (FAC-9): la que diga el origen —la orden de laboratorio sabe dónde se
        // admitió— o la sede activa de quien registra el cargo, que es donde está atendiendo.
        var sede = cmd.LocationId
            ?? await activeLocation.HandleAsync(cmd.OrganizationId, cmd.RequestedByUserId, null, ct);

        var cargo = BillingCharge.Capture(
            cmd.OrganizationId, cuenta.Id, item.Id, item.Name,
            cmd.Quantity, precio.Price, precio.Source, tributo.Id, tributo.Rate,
            cmd.SourceKind, cmd.SourceId,
            priceListId: precio.PriceListId,
            discountAmount: cmd.DiscountAmount,
            discountAuthorizedBy: cmd.DiscountAuthorizedBy,
            component: item.Component,
            performedAt: cmd.PerformedAt,
            performerPractitionerId: cmd.PerformerPractitionerId,
            sourceFhirRef: cmd.SourceFhirRef,
            reason: cmd.Reason,
            locationId: sede,
            pricePending: item.PricePending);

        // Segunda capa: la base decide si dos caminos llegaron a la vez.
        if (await accounts.TryAddChargeAsync(cargo, ct))
        {
            return ChargeCaptureResult.Captured(ChargeMapper.ToDto(cargo, precio.PriceListName));
        }

        var ganador = await accounts.FindLiveChargeBySourceAsync(
            cmd.OrganizationId, cmd.SourceKind, cmd.SourceId, cmd.BillableItemId, ct);
        return ganador is not null
            ? ChargeCaptureResult.Duplicate(ChargeMapper.ToDto(ganador))
            : ChargeCaptureResult.Reject("No se pudo capturar el cargo.");
    }

    /// <summary>
    /// El descuento se valida contra dos cosas distintas: el <b>piso</b> del tarifario —que
    /// protege el margen— y el <b>tope del usuario</b> —que protege la caja—. Superar el tope no
    /// bloquea si alguien lo autorizó; superar el piso sí, porque el piso no es negociable por
    /// nivel jerárquico.
    /// </summary>
    private async Task<string?> ValidarDescuentoAsync(
        CaptureChargeCommand cmd, ResolvedPrice precio, DateOnly today, CancellationToken ct)
    {
        var bruto = cmd.Quantity * precio.Price;
        var neto = bruto - cmd.DiscountAmount;

        if (precio.MinPrice is { } piso && neto < piso * cmd.Quantity)
        {
            return $"El descuento deja la línea por debajo del piso del tarifario (L {piso:N2} por unidad).";
        }

        if (precio.DiscountPctMax is { } topeLinea)
        {
            var pct = bruto == 0 ? 0 : cmd.DiscountAmount / bruto * 100m;
            if (pct > topeLinea)
            {
                return $"El descuento supera el máximo de este ítem ({topeLinea:N2}%).";
            }
        }

        if (cmd.DiscountAuthorizedBy is not null || cmd.RequestedByUserId is null)
        {
            // Ya viene autorizado, o no sabemos quién pide (camino automático, sin descuento
            // manual de por medio): no hay tope de usuario que aplicar.
            return null;
        }

        var tope = await limits.FindEffectiveAsync(
            cmd.OrganizationId, cmd.RequestedByUserId.Value,
            AuthorizationLimitKind.DiscountAmount, today, ct);

        if (tope is null || cmd.DiscountAmount <= tope.Value)
        {
            return null;
        }

        return tope.RequiresApprovalAbove
            ? $"El descuento de L {cmd.DiscountAmount:N2} supera tu tope de L {tope.Value:N2}: requiere autorización de un supervisor."
            : $"El descuento de L {cmd.DiscountAmount:N2} supera tu tope de L {tope.Value:N2}.";
    }
}

public sealed class ReverseChargeHandler(IBillingAccountRepository accounts)
{
    public async Task<ChargeDto?> HandleAsync(
        Guid orgId, Guid chargeId, string reason, CancellationToken ct = default)
    {
        var cargo = await accounts.GetChargeForUpdateAsync(orgId, chargeId, ct);
        if (cargo is null)
        {
            return null;
        }

        cargo.Reverse(reason);
        await accounts.UpdateChargeAsync(cargo, ct);
        return ChargeMapper.ToDto(cargo);
    }
}
