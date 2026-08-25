namespace IMed.Api.Domain.Billing.Catalog;

/// <summary>
/// Un tarifario: qué precios rigen, para quién y desde cuándo (FSF bloque 2).
///
/// <para><b>La vigencia no se edita, se sucede.</b> Cambiar el precio de una lista vigente
/// reescribe lo que se cobró ayer; lo correcto es cerrar la lista y abrir otra. Por eso los
/// precios viven en <see cref="PriceListItem"/> con su propia vigencia.</para>
/// </summary>
public sealed class PriceList
{
    public Guid Id { get; private set; }
    public Guid OrganizationId { get; private set; }
    public string Code { get; private set; } = string.Empty;
    public string Name { get; private set; } = string.Empty;
    public PriceListKind Kind { get; private set; }
    public string Currency { get; private set; } = "HNL";

    /// <summary>
    /// Convenio del pagador al que pertenece esta lista, cuando <see cref="Kind"/> es
    /// <see cref="PriceListKind.Agreement"/>. Sin uso hasta U6; se declara ahora para que las
    /// listas de convenio ya tengan dónde apuntar.
    /// </summary>
    public Guid? PayerContractId { get; private set; }

    /// <summary>
    /// Desempate entre listas del mismo tipo, mayor gana. Existe porque dos promociones
    /// solapadas son normales —la de temporada y la del aniversario— y sin un criterio declarado
    /// la que se aplica depende del orden en que la base devuelva las filas.
    /// </summary>
    public int Priority { get; private set; }

    public DateOnly ValidFrom { get; private set; }
    public DateOnly? ValidTo { get; private set; }
    public bool Active { get; private set; } = true;
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    private PriceList() { }

    public static PriceList Create(
        Guid organizationId,
        string code,
        string name,
        PriceListKind kind,
        DateOnly validFrom,
        DateOnly? validTo = null,
        int priority = 0,
        string currency = "HNL",
        Guid? payerContractId = null,
        TimeProvider? timeProvider = null)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            throw new ArgumentException("El código del tarifario es obligatorio.", nameof(code));
        }
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("El nombre del tarifario es obligatorio.", nameof(name));
        }
        if (validTo is { } end && end < validFrom)
        {
            throw new ArgumentException("La vigencia termina antes de empezar.", nameof(validTo));
        }

        var now = (timeProvider ?? TimeProvider.System).GetUtcNow();
        return new PriceList
        {
            Id = Guid.CreateVersion7(now),
            OrganizationId = organizationId,
            Code = code.Trim().ToUpperInvariant(),
            Name = name.Trim(),
            Kind = kind,
            Currency = string.IsNullOrWhiteSpace(currency) ? "HNL" : currency.Trim().ToUpperInvariant(),
            PayerContractId = payerContractId,
            Priority = priority,
            ValidFrom = validFrom,
            ValidTo = validTo,
            Active = true,
            CreatedAt = now,
            UpdatedAt = now,
        };
    }

    public bool IsInEffectOn(DateOnly day) =>
        Active && day >= ValidFrom && (ValidTo is not { } end || day <= end);

    public void Close(DateOnly lastDay, TimeProvider? timeProvider = null)
    {
        if (lastDay < ValidFrom)
        {
            throw new ArgumentException(
                "El cierre no puede ser anterior al inicio de la vigencia.", nameof(lastDay));
        }
        ValidTo = lastDay;
        UpdatedAt = (timeProvider ?? TimeProvider.System).GetUtcNow();
    }

    public void Deactivate(TimeProvider? timeProvider = null)
    {
        Active = false;
        UpdatedAt = (timeProvider ?? TimeProvider.System).GetUtcNow();
    }
}

/// <summary>
/// El precio de un cobrable dentro de un tarifario, con su propio piso de descuento.
///
/// <para><b>El piso importa tanto como el precio.</b> Sin <see cref="MinPrice"/>, un descuento
/// autorizado puede dejar la línea por debajo del costo sin que nada lo señale, y eso solo se
/// descubre cuando alguien mira los márgenes a fin de mes.</para>
/// </summary>
public sealed class PriceListItem
{
    public Guid Id { get; private set; }
    public Guid OrganizationId { get; private set; }
    public Guid PriceListId { get; private set; }
    public Guid BillableItemId { get; private set; }

    public decimal Price { get; private set; }

    /// <summary>Piso de descuento. Null = sin piso declarado.</summary>
    public decimal? MinPrice { get; private set; }

    /// <summary>Descuento máximo en porcentaje. Null = sin tope propio de la línea.</summary>
    public decimal? DiscountPctMax { get; private set; }

    public DateOnly ValidFrom { get; private set; }
    public DateOnly? ValidTo { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    private PriceListItem() { }

    public static PriceListItem Create(
        Guid organizationId,
        Guid priceListId,
        Guid billableItemId,
        decimal price,
        DateOnly validFrom,
        DateOnly? validTo = null,
        decimal? minPrice = null,
        decimal? discountPctMax = null,
        TimeProvider? timeProvider = null)
    {
        if (price < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(price), "El precio no puede ser negativo.");
        }
        if (minPrice is { } piso)
        {
            if (piso < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(minPrice), "El piso no puede ser negativo.");
            }
            if (piso > price)
            {
                throw new ArgumentException(
                    "El piso de descuento no puede ser mayor que el precio.", nameof(minPrice));
            }
        }
        if (discountPctMax is { } pct && (pct < 0 || pct > 100))
        {
            throw new ArgumentOutOfRangeException(nameof(discountPctMax), "El tope va de 0 a 100.");
        }
        if (validTo is { } end && end < validFrom)
        {
            throw new ArgumentException("La vigencia termina antes de empezar.", nameof(validTo));
        }

        var now = (timeProvider ?? TimeProvider.System).GetUtcNow();
        return new PriceListItem
        {
            Id = Guid.CreateVersion7(now),
            OrganizationId = organizationId,
            PriceListId = priceListId,
            BillableItemId = billableItemId,
            Price = price,
            MinPrice = minPrice,
            DiscountPctMax = discountPctMax,
            ValidFrom = validFrom,
            ValidTo = validTo,
            CreatedAt = now,
            UpdatedAt = now,
        };
    }

    public bool IsInEffectOn(DateOnly day) =>
        day >= ValidFrom && (ValidTo is not { } end || day <= end);

    public void Close(DateOnly lastDay, TimeProvider? timeProvider = null)
    {
        ValidTo = lastDay;
        UpdatedAt = (timeProvider ?? TimeProvider.System).GetUtcNow();
    }
}
