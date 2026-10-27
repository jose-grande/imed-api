using IMed.Api.Application.Abstractions;
using IMed.Api.Domain.Billing.Fiscal;
using IMed.Api.Domain.Billing.Catalog;
using Microsoft.Extensions.Logging;

namespace IMed.Api.Application.Billing;

/// <summary>
/// Alta automática de cobrables (plan de salida a producción, D1 y F0.3).
///
/// <para><b>Todo lo que se puede cobrar nace con su renglón en el catálogo.</b> Una especialidad
/// nueva, un examen o panel nuevo y un artículo vendible crean su cobrable en el momento, sin que
/// nadie tenga que acordarse. Nacen <b>sin precio</b>: el acto clínico no espera, pero la factura
/// no sale hasta que el jefe administrativo fije el precio.</para>
///
/// <para><b>Es idempotente y nunca lanza por un duplicado.</b> Lo llaman caminos que pueden
/// repetirse —reintentos del outbox, backfills, dos médicos de la misma especialidad que entran a
/// la vez— y el índice único del enlace decide quién gana.</para>
/// </summary>
public interface IBillableItemProvisioner
{
    /// <summary>
    /// Asegura los tres cobrables de consulta de una especialidad (primera vez, control y
    /// emergencia). Devuelve los tres, existentes o nuevos; vacío si la organización todavía no
    /// tiene régimen fiscal (sin tributos no hay cobrable posible).
    /// </summary>
    Task<IReadOnlyList<BillableItemDto>> EnsureForSpecialtyAsync(
        Guid organizationId, string specialtyCode, CancellationToken ct = default);

    /// <summary>Asegura el cobrable de una prueba de laboratorio de la organización.</summary>
    Task<BillableItemDto?> EnsureForLabTestAsync(
        Guid organizationId, Guid labTestId, CancellationToken ct = default);

    /// <summary>Asegura el cobrable de un panel de laboratorio de la organización.</summary>
    Task<BillableItemDto?> EnsureForLabPanelAsync(
        Guid organizationId, Guid labPanelId, CancellationToken ct = default);

    /// <summary>
    /// Asegura el cobrable de un artículo de inventario vendible (Stream B). El tributo lo decide
    /// el artículo: un medicamento y un insumo no pagan lo mismo.
    /// </summary>
    Task<BillableItemDto?> EnsureForStockItemAsync(
        Guid organizationId, Guid stockItemId, string code, string name, Guid taxCodeId,
        CancellationToken ct = default);

    /// <summary>
    /// Asegura los cobrables de todas las pruebas y paneles activos de la organización. Lo usan
    /// la configuración del régimen fiscal (antes no había tributos) y el backfill de arranque.
    /// </summary>
    Task<int> ReconcileLabAsync(Guid organizationId, CancellationToken ct = default);
}

public sealed class BillableItemProvisioner(
    IBillingCatalogRepository catalog,
    IFiscalRepository fiscal,
    ILabCatalogRepository lab,
    ISpecialtyRepository specialties,
    ILogger<BillableItemProvisioner> logger) : IBillableItemProvisioner
{
    /// <summary>
    /// Tributo por defecto de lo que el sistema da de alta solo: los servicios de salud están
    /// exentos de ISV. Se puede cambiar por ítem en el catálogo.
    /// </summary>
    public const string DefaultServiceTaxCode = "EXENTO";

    private static readonly (ConsultationType Type, string Suffix, string Label)[] TiposConsulta =
    [
        (ConsultationType.FirstVisit, "1V", "primera vez"),
        (ConsultationType.FollowUp, "CTRL", "control"),
        (ConsultationType.Emergency, "EMER", "emergencia"),
    ];

    public async Task<IReadOnlyList<BillableItemDto>> EnsureForSpecialtyAsync(
        Guid organizationId, string specialtyCode, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(specialtyCode))
        {
            throw new ArgumentException("La especialidad es obligatoria.", nameof(specialtyCode));
        }
        var codigo = specialtyCode.Trim();
        var especialidad = await specialties.GetByCodeAsync(codigo, ct)
            ?? throw new ArgumentException($"La especialidad '{codigo}' no existe en el catálogo.", nameof(specialtyCode));

        var tributo = await TributoServicioAsync(organizationId, ct);
        if (tributo is null)
        {
            return [];
        }

        var resultado = new List<BillableItemDto>(TiposConsulta.Length);
        foreach (var (tipo, sufijo, etiqueta) in TiposConsulta)
        {
            var item = await AsegurarAsync(
                () => catalog.FindBySpecialtyAsync(organizationId, codigo, tipo, ct),
                intento => BillableItem.Create(
                    organizationId,
                    CodigoUnico($"CONS-{codigo}-{sufijo}", intento),
                    $"Consulta {especialidad.DisplayEs} — {etiqueta}",
                    BillableItemKind.Service,
                    tributo.Value,
                    defaultPrice: 0m,
                    PaymentTiming.Either,
                    serviceCode: codigo,
                    specialtyCode: codigo,
                    consultationType: tipo,
                    pricePending: true),
                ct);
            if (item is not null)
            {
                resultado.Add(BillableItemMapper.ToDto(item));
            }
        }
        return resultado;
    }

    public async Task<BillableItemDto?> EnsureForLabTestAsync(
        Guid organizationId, Guid labTestId, CancellationToken ct = default)
    {
        var prueba = await lab.GetTestByIdAsync(labTestId, ct);
        if (prueba is null || prueba.OrganizationId != organizationId)
        {
            // Las plantillas globales no se cobran: cada organización cobra su copia.
            return null;
        }
        var tributo = await TributoServicioAsync(organizationId, ct);
        if (tributo is null)
        {
            return null;
        }

        var item = await AsegurarAsync(
            () => catalog.FindAnyByLabAsync(organizationId, labTestId, null, ct),
            intento => BillableItem.Create(
                organizationId, CodigoUnico($"LAB-{prueba.Code}", intento), prueba.Name,
                BillableItemKind.Service, tributo.Value, defaultPrice: 0m,
                // El laboratorio ambulatorio se paga antes (FAC-7): la recepción lo avisa sin bloquear.
                PaymentTiming.Prepaid,
                labTestId: labTestId, serviceCode: prueba.LoincCode, pricePending: true),
            ct);
        return item is null ? null : BillableItemMapper.ToDto(item);
    }

    public async Task<BillableItemDto?> EnsureForLabPanelAsync(
        Guid organizationId, Guid labPanelId, CancellationToken ct = default)
    {
        var panel = await lab.GetPanelByIdAsync(labPanelId, ct);
        if (panel is null || panel.OrganizationId != organizationId)
        {
            return null;
        }
        var tributo = await TributoServicioAsync(organizationId, ct);
        if (tributo is null)
        {
            return null;
        }

        var item = await AsegurarAsync(
            () => catalog.FindAnyByLabAsync(organizationId, null, labPanelId, ct),
            intento => BillableItem.Create(
                organizationId, CodigoUnico($"PAN-{panel.Code}", intento), panel.Name,
                BillableItemKind.Service, tributo.Value, defaultPrice: 0m,
                PaymentTiming.Prepaid,
                labPanelId: labPanelId, serviceCode: panel.LoincCode, pricePending: true),
            ct);
        return item is null ? null : BillableItemMapper.ToDto(item);
    }

    public async Task<BillableItemDto?> EnsureForStockItemAsync(
        Guid organizationId, Guid stockItemId, string code, string name, Guid taxCodeId,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("El artículo necesita código y nombre.", nameof(code));
        }
        _ = await fiscal.GetTaxCodeAsync(organizationId, taxCodeId, ct)
            ?? throw new ArgumentException("El tributo no existe en esta organización.", nameof(taxCodeId));

        var item = await AsegurarAsync(
            () => catalog.FindAnyByStockItemAsync(organizationId, stockItemId, ct),
            intento => BillableItem.Create(
                organizationId, CodigoUnico($"ART-{code.Trim()}", intento), name,
                BillableItemKind.Product, taxCodeId, defaultPrice: 0m,
                PaymentTiming.Either,
                stockItemId: stockItemId, pricePending: true),
            ct);
        return item is null ? null : BillableItemMapper.ToDto(item);
    }

    public async Task<int> ReconcileLabAsync(Guid organizationId, CancellationToken ct = default)
    {
        if (await TributoServicioAsync(organizationId, ct) is null)
        {
            return 0;
        }

        var creados = 0;
        var pruebas = await lab.SearchTestsAsync(organizationId, null, null, activeOnly: true, limit: 5000, ct);
        foreach (var p in pruebas)
        {
            if (await catalog.FindAnyByLabAsync(organizationId, p.Id, null, ct) is null
                && await EnsureForLabTestAsync(organizationId, p.Id, ct) is not null)
            {
                creados++;
            }
        }
        foreach (var panel in (await lab.ListPanelsAsync(organizationId, ct)).Where(x => x.Active))
        {
            if (await catalog.FindAnyByLabAsync(organizationId, null, panel.Id, ct) is null
                && await EnsureForLabPanelAsync(organizationId, panel.Id, ct) is not null)
            {
                creados++;
            }
        }
        return creados;
    }

    /// <summary>
    /// Busca por el enlace; si no hay, crea. Si el alta choca, vuelve a buscar por el enlace: si
    /// ahora existe, otro camino ganó la carrera; si no, chocó el código con un cobrable cargado a
    /// mano y se reintenta con un sufijo.
    /// </summary>
    private async Task<BillableItem?> AsegurarAsync(
        Func<Task<BillableItem?>> buscar, Func<int, BillableItem> crear, CancellationToken ct)
    {
        const int maxIntentos = 5;
        for (var intento = 0; intento < maxIntentos; intento++)
        {
            if (await buscar() is { } existente)
            {
                return existente;
            }
            var nuevo = crear(intento);
            if (await catalog.TryAddLinkedItemAsync(nuevo, ct))
            {
                return nuevo;
            }
        }
        logger.LogWarning("No se pudo dar de alta un cobrable automático tras {Intentos} intentos.", maxIntentos);
        return await buscar();
    }

    private async Task<Guid?> TributoServicioAsync(Guid organizationId, CancellationToken ct)
    {
        var tributos = await fiscal.ListTaxCodesAsync(organizationId, ct);
        var exento = tributos.FirstOrDefault(t => t.Code == DefaultServiceTaxCode && t.Active)
                     ?? tributos.FirstOrDefault(t => t.Treatment == TaxTreatment.Exempt && t.Active);
        if (exento is null)
        {
            logger.LogInformation(
                "Org {OrgId} sin régimen fiscal: el alta automática de cobrables espera a que se configure.",
                organizationId);
        }
        return exento?.Id;
    }

    /// <summary>El código del cobrable cabe en 40 caracteres y lleva sufijo si chocó.</summary>
    private static string CodigoUnico(string baseCode, int intento)
    {
        var sufijo = intento == 0 ? string.Empty : $"-{intento + 1}";
        var cuerpo = baseCode.Length + sufijo.Length > 40 ? baseCode[..(40 - sufijo.Length)] : baseCode;
        return cuerpo + sufijo;
    }
}
