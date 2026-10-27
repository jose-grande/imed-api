using System.Text.RegularExpressions;

namespace IMed.Api.Domain.Inventory;

/// <summary>
/// Un artículo de inventario: el maestro único de todo lo que se guarda en una bodega (FSF §8,
/// FSF-DEC-04). Medicamentos, reactivos e insumos viven en la misma tabla y el mismo libro.
///
/// <para><b>Un solo maestro, no uno por módulo.</b> Los sistemas que tienen un maestro de farmacia
/// y otro de almacén pasan la vida reconciliándolos. Lo que distingue a un medicamento de una gasa
/// es su <b>perfil</b> (<see cref="Medication"/>, <see cref="Reagent"/>, <see cref="Supply"/>), no
/// una tabla aparte.</para>
///
/// <para><b>Todo se guarda en unidad base.</b> <see cref="BaseUnit"/> es la unidad más chica que se
/// cuenta (tableta, frasco, unidad); las presentaciones (caja de 30, blíster de 10) son solo una
/// forma cómoda de capturar una compra y se convierten con su factor. El libro y las existencias
/// nunca guardan cajas.</para>
///
/// <para><b>Vender es un enlace, no una copia.</b> Un artículo vendible tiene su cobrable en el
/// catálogo de facturación (<see cref="BillableItemId"/>, D12): el precio vive allá, una sola vez,
/// y aquí solo se guarda el enlace.</para>
/// </summary>
public sealed partial class StockItem
{
    public const int CodeMaxLength = 30;

    public Guid Id { get; private set; }
    public Guid OrganizationId { get; private set; }

    /// <summary>Código interno, único en la organización. Es lo que se teclea en el mostrador.</summary>
    public string Code { get; private set; } = string.Empty;

    public string Name { get; private set; } = string.Empty;

    /// <summary>Código de barras del fabricante (GTIN/EAN), si tiene. El lector actúa como teclado.</summary>
    public string? Barcode { get; private set; }

    public StockItemKind Kind { get; private set; }

    /// <summary>Unidad en la que se cuenta y se guarda todo: "tableta", "frasco", "unidad".</summary>
    public string BaseUnit { get; private set; } = string.Empty;

    /// <summary>
    /// ¿Cada entrada y salida tiene que decir de qué lote? Siempre en medicamentos y reactivos (D8):
    /// sin lote no hay retiro de producto posible ni salida por vencimiento.
    /// </summary>
    public bool RequiresLot { get; private set; }

    /// <summary>¿Se cobra al paciente? Si es así, tiene su cobrable en el catálogo.</summary>
    public bool Sellable { get; private set; }

    /// <summary>Se puede vender en mostrador sin receta (P11). Solo tiene sentido si es vendible.</summary>
    public bool OverTheCounter { get; private set; }

    /// <summary>Tributo con el que se vende (un medicamento y un cosmético no pagan lo mismo).</summary>
    public Guid? TaxCodeId { get; private set; }

    /// <summary>Cobrable enlazado en el catálogo de facturación (D12). Null si no es vendible o si
    /// la organización todavía no tiene régimen fiscal.</summary>
    public Guid? BillableItemId { get; private set; }

    /// <summary>Existencia mínima por sede (P9). Por debajo, el artículo aparece en las alertas.</summary>
    public decimal? MinimumStock { get; private set; }

    public bool Active { get; private set; } = true;

    public MedicationProfile? Medication { get; private set; }
    public ReagentProfile? Reagent { get; private set; }
    public SupplyProfile? Supply { get; private set; }

    private readonly List<StockItemPresentation> _presentations = [];
    public IReadOnlyList<StockItemPresentation> Presentations => _presentations;

    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    private StockItem() { }

    public static StockItem Create(
        Guid organizationId,
        string code,
        string name,
        StockItemKind kind,
        string baseUnit,
        bool requiresLot = false,
        string? barcode = null,
        TimeProvider? timeProvider = null)
    {
        var now = (timeProvider ?? TimeProvider.System).GetUtcNow();
        var item = new StockItem
        {
            Id = Guid.CreateVersion7(now),
            OrganizationId = organizationId,
            Kind = kind,
            CreatedAt = now,
        };
        item.SetCodeInternal(code);
        item.Rename(name, baseUnit, barcode, requiresLot, now);
        return item;
    }

    /// <summary>Datos generales. El código no cambia: lo usan el kardex impreso y el mostrador.</summary>
    public void Rename(string name, string baseUnit, string? barcode, bool requiresLot, TimeProvider? timeProvider = null) =>
        Rename(name, baseUnit, barcode, requiresLot, (timeProvider ?? TimeProvider.System).GetUtcNow());

    private void Rename(string name, string baseUnit, string? barcode, bool requiresLot, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("El nombre del artículo es obligatorio.", nameof(name));
        }
        if (string.IsNullOrWhiteSpace(baseUnit))
        {
            throw new ArgumentException(
                "La unidad base es obligatoria: es la unidad en la que se cuenta todo (tableta, frasco, unidad).",
                nameof(baseUnit));
        }
        Name = name.Trim();
        BaseUnit = baseUnit.Trim().ToLowerInvariant();
        Barcode = string.IsNullOrWhiteSpace(barcode) ? null : barcode.Trim();
        // D8: medicamentos y reactivos llevan lote siempre, digan lo que digan.
        RequiresLot = Kind is StockItemKind.Medication or StockItemKind.Reagent || requiresLot;
        UpdatedAt = now;
    }

    private void SetCodeInternal(string code)
    {
        var c = (code ?? string.Empty).Trim().ToUpperInvariant();
        if (c.Length == 0 || c.Length > CodeMaxLength || !CodePattern().IsMatch(c))
        {
            throw new ArgumentException(
                $"El código del artículo lleva de 1 a {CodeMaxLength} letras, números o guiones.", nameof(code));
        }
        Code = c;
    }

    /// <summary>
    /// Perfil de medicamento. El código ATC es lo que une la receta (que se escribe con el genérico)
    /// con el artículo comercial que se despacha (P10).
    /// </summary>
    public void SetMedicationProfile(MedicationProfile profile, TimeProvider? timeProvider = null)
    {
        if (Kind != StockItemKind.Medication)
        {
            throw new InvalidOperationException("Solo un medicamento lleva perfil de medicamento.");
        }
        ArgumentNullException.ThrowIfNull(profile);
        Medication = profile;
        Touch(timeProvider);
    }

    public void SetReagentProfile(ReagentProfile profile, TimeProvider? timeProvider = null)
    {
        if (Kind != StockItemKind.Reagent)
        {
            throw new InvalidOperationException("Solo un reactivo lleva perfil de reactivo.");
        }
        Reagent = profile;
        Touch(timeProvider);
    }

    public void SetSupplyProfile(SupplyProfile profile, TimeProvider? timeProvider = null)
    {
        if (Kind != StockItemKind.Supply)
        {
            throw new InvalidOperationException("Solo un insumo lleva perfil de insumo.");
        }
        Supply = profile;
        Touch(timeProvider);
    }

    /// <summary>
    /// Venta: si es vendible, con qué tributo y si va al mostrador sin receta. Un artículo de venta
    /// libre tiene que ser vendible (P11): "sin receta pero no se cobra" no es un estado real.
    /// </summary>
    public void SetSale(bool sellable, bool overTheCounter, Guid? taxCodeId, TimeProvider? timeProvider = null)
    {
        if (sellable && taxCodeId is null)
        {
            throw new ArgumentException(
                "Un artículo vendible necesita su tributo: sin él la factura no sabe si paga impuesto.",
                nameof(taxCodeId));
        }
        if (overTheCounter && !sellable)
        {
            throw new ArgumentException("Un artículo de venta libre tiene que ser vendible.", nameof(overTheCounter));
        }
        Sellable = sellable;
        OverTheCounter = sellable && overTheCounter;
        TaxCodeId = sellable ? taxCodeId : null;
        Touch(timeProvider);
    }

    /// <summary>Guarda el cobrable que el alta automática le dio (B2).</summary>
    public void LinkBillableItem(Guid billableItemId, TimeProvider? timeProvider = null)
    {
        BillableItemId = billableItemId;
        Touch(timeProvider);
    }

    public void SetMinimumStock(decimal? minimum, TimeProvider? timeProvider = null)
    {
        if (minimum is < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(minimum), "El mínimo no puede ser negativo.");
        }
        MinimumStock = minimum is 0 ? null : minimum;
        Touch(timeProvider);
    }

    public void SetActive(bool active, TimeProvider? timeProvider = null)
    {
        Active = active;
        Touch(timeProvider);
    }

    /// <summary>
    /// Reemplaza las presentaciones. Se identifican por nombre: renombrar una es quitarla y crear
    /// otra, lo que está bien porque ningún movimiento guarda la presentación (todo va en base).
    /// </summary>
    public void ReplacePresentations(
        IEnumerable<(string Name, decimal FactorToBase)> presentations, TimeProvider? timeProvider = null)
    {
        var nuevas = presentations
            .Select(p => StockItemPresentation.Create(Id, p.Name, p.FactorToBase))
            .ToList();
        if (nuevas.GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase).Any(g => g.Count() > 1))
        {
            throw new ArgumentException("Dos presentaciones no pueden llamarse igual.", nameof(presentations));
        }
        _presentations.RemoveAll(p => !nuevas.Any(n => string.Equals(n.Name, p.Name, StringComparison.OrdinalIgnoreCase)));
        foreach (var n in nuevas)
        {
            var actual = _presentations.FirstOrDefault(p => string.Equals(p.Name, n.Name, StringComparison.OrdinalIgnoreCase));
            if (actual is null)
            {
                _presentations.Add(n);
            }
            else
            {
                actual.SetFactor(n.FactorToBase);
            }
        }
        Touch(timeProvider);
    }

    private void Touch(TimeProvider? timeProvider) =>
        UpdatedAt = (timeProvider ?? TimeProvider.System).GetUtcNow();

    [GeneratedRegex("^[A-Z0-9][A-Z0-9-]*$")]
    private static partial Regex CodePattern();
}

/// <summary>
/// Una forma de comprar el artículo: "Caja x 30" equivale a 30 tabletas. La conversión es del
/// artículo y no global (FSF §4.2): una caja de este producto no es una caja de otro.
/// </summary>
public sealed class StockItemPresentation
{
    public Guid Id { get; private set; }
    public Guid StockItemId { get; private set; }
    public string Name { get; private set; } = string.Empty;

    /// <summary>Cuántas unidades base trae. Una caja de 30 tabletas: 30.</summary>
    public decimal FactorToBase { get; private set; }

    private StockItemPresentation() { }

    public static StockItemPresentation Create(Guid stockItemId, string name, decimal factorToBase)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("La presentación necesita un nombre.", nameof(name));
        }
        var p = new StockItemPresentation
        {
            Id = Guid.CreateVersion7(),
            StockItemId = stockItemId,
            Name = name.Trim(),
        };
        p.SetFactor(factorToBase);
        return p;
    }

    internal void SetFactor(decimal factorToBase)
    {
        if (factorToBase <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(factorToBase), "La presentación tiene que traer al menos una fracción de unidad.");
        }
        FactorToBase = factorToBase;
    }
}

/// <summary>Perfil de medicamento (FSF §8, <c>stock_item_medication</c>).</summary>
public sealed partial class MedicationProfile
{
    /// <summary>Código ATC del genérico. Es la llave con la receta (P10).</summary>
    public string AtcCode { get; private set; } = string.Empty;

    /// <summary>Principio activo, como lo dice la caja.</summary>
    public string? ActiveIngredient { get; private set; }

    /// <summary>Concentración: "500 mg", "250 mg/5 mL".</summary>
    public string? Concentration { get; private set; }

    /// <summary>Forma farmacéutica: tableta, cápsula, suspensión, inyectable.</summary>
    public string? DoseForm { get; private set; }

    /// <summary>Vía de administración.</summary>
    public string? Route { get; private set; }

    /// <summary>Registro sanitario (ARSA).</summary>
    public string? SanitaryRegistration { get; private set; }

    private MedicationProfile() { }

    public static MedicationProfile Create(
        string atcCode,
        string? activeIngredient = null,
        string? concentration = null,
        string? doseForm = null,
        string? route = null,
        string? sanitaryRegistration = null)
    {
        var atc = (atcCode ?? string.Empty).Trim().ToUpperInvariant();
        if (!AtcPattern().IsMatch(atc))
        {
            throw new ArgumentException(
                "El código ATC es obligatorio en un medicamento (letra, dos dígitos y hasta cuatro caracteres más; ej. N02BE01): " +
                "es lo que une la receta con el artículo que se despacha.",
                nameof(atcCode));
        }
        return new MedicationProfile
        {
            AtcCode = atc,
            ActiveIngredient = Clean(activeIngredient),
            Concentration = Clean(concentration),
            DoseForm = Clean(doseForm),
            Route = Clean(route),
            SanitaryRegistration = Clean(sanitaryRegistration),
        };
    }

    private static string? Clean(string? v) => string.IsNullOrWhiteSpace(v) ? null : v.Trim();

    [GeneratedRegex("^[A-Z][0-9]{2}([A-Z]([A-Z]([0-9]{2})?)?)?$")]
    private static partial Regex AtcPattern();
}

/// <summary>Perfil de reactivo de laboratorio (FSF §8, <c>stock_item_reagent</c>).</summary>
public sealed class ReagentProfile
{
    /// <summary>Condiciones de almacenamiento ("2–8 °C").</summary>
    public string? StorageConditions { get; private set; }

    /// <summary>Días que dura abierto. Informativo: el consumo por examen está fuera de alcance (D9).</summary>
    public int? OpenStabilityDays { get; private set; }

    private ReagentProfile() { }

    public static ReagentProfile Create(string? storageConditions, int? openStabilityDays)
    {
        if (openStabilityDays is < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(openStabilityDays), "Los días no pueden ser negativos.");
        }
        return new ReagentProfile
        {
            StorageConditions = string.IsNullOrWhiteSpace(storageConditions) ? null : storageConditions.Trim(),
            OpenStabilityDays = openStabilityDays,
        };
    }
}

/// <summary>Perfil de insumo (FSF §8, <c>stock_item_supply</c>).</summary>
public sealed class SupplyProfile
{
    public bool Sterile { get; private set; }
    public bool SingleUse { get; private set; }

    private SupplyProfile() { }

    public static SupplyProfile Create(bool sterile, bool singleUse) =>
        new() { Sterile = sterile, SingleUse = singleUse };
}
