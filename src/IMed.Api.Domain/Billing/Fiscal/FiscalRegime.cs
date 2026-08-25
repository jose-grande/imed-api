namespace IMed.Api.Domain.Billing.Fiscal;

/// <summary>
/// Régimen fiscal bajo el que emite una organización (FSF bloque 1, núcleo agnóstico).
///
/// <para><b>Este archivo no menciona Honduras a propósito.</b> Lo específico del país —formato del
/// número, longitudes, si exige monto en letras, umbral de RTN, reglas de redondeo— vive en
/// <see cref="ConfigJson"/> y lo interpreta un adaptador registrado por <see cref="RegimeCode"/>.
/// El día que el SAR pase a facturación electrónica, o que el producto se venda en otro país, se
/// escribe otro adaptador: el núcleo no se toca.</para>
///
/// <para><b>Vigencia en vez de edición.</b> Un régimen no se corrige, se sustituye: los documentos
/// emitidos bajo el anterior tienen que poder explicarse con las reglas que regían ese día.</para>
/// </summary>
public sealed class FiscalRegime
{
    public Guid Id { get; private set; }
    public Guid OrganizationId { get; private set; }

    /// <summary>ISO 3166-1 alfa-2. <c>HN</c> para Honduras.</summary>
    public string CountryCode { get; private set; } = string.Empty;

    /// <summary>Clave con la que se resuelve el adaptador. Ej.: <c>HN_SAR_CAI</c>.</summary>
    public string RegimeCode { get; private set; } = string.Empty;

    /// <summary>Configuración que solo el adaptador entiende. JSON opaco para el núcleo.</summary>
    public string? ConfigJson { get; private set; }

    /// <summary>
    /// Identificación tributaria del emisor — el RTN de la clínica en Honduras.
    ///
    /// <para><b>Vive acá y no en la organización</b> porque es la identidad con la que se emite
    /// <em>bajo este régimen</em>, y comparte su vigencia: si la clínica cambia de RTN, lo
    /// correcto es cerrar el régimen y abrir otro, para que los documentos ya emitidos sigan
    /// explicándose con el RTN que llevaban impreso. Colgarlo de la organización lo convertiría en
    /// un dato mutable que reescribe la historia de cada factura.</para>
    ///
    /// <para>Nullable porque el régimen se puede configurar antes de tener el dato; la emisión sí
    /// lo exige (FSF-DEC-07: varias sedes, un solo emisor fiscal).</para>
    /// </summary>
    public string? IssuerTaxId { get; private set; }

    /// <summary>Razón social del emisor, como debe salir impresa.</summary>
    public string? IssuerLegalName { get; private set; }

    public DateOnly ValidFrom { get; private set; }
    public DateOnly? ValidTo { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    private FiscalRegime() { }

    public static FiscalRegime Create(
        Guid organizationId,
        string countryCode,
        string regimeCode,
        DateOnly validFrom,
        string? configJson = null,
        DateOnly? validTo = null,
        string? issuerTaxId = null,
        string? issuerLegalName = null,
        TimeProvider? timeProvider = null)
    {
        if (string.IsNullOrWhiteSpace(countryCode))
        {
            throw new ArgumentException("El país del régimen es obligatorio.", nameof(countryCode));
        }
        if (string.IsNullOrWhiteSpace(regimeCode))
        {
            throw new ArgumentException("El código de régimen es obligatorio.", nameof(regimeCode));
        }
        if (validTo is { } end && end < validFrom)
        {
            throw new ArgumentException("La vigencia termina antes de empezar.", nameof(validTo));
        }

        var now = (timeProvider ?? TimeProvider.System).GetUtcNow();
        return new FiscalRegime
        {
            Id = Guid.CreateVersion7(now),
            OrganizationId = organizationId,
            CountryCode = countryCode.Trim().ToUpperInvariant(),
            RegimeCode = regimeCode.Trim().ToUpperInvariant(),
            ConfigJson = string.IsNullOrWhiteSpace(configJson) ? null : configJson,
            IssuerTaxId = Trim(issuerTaxId),
            IssuerLegalName = Trim(issuerLegalName),
            ValidFrom = validFrom,
            ValidTo = validTo,
            CreatedAt = now,
            UpdatedAt = now,
        };
    }

    /// <summary>
    /// Fija la identidad fiscal del emisor. Se puede completar después de configurar el régimen,
    /// pero antes de la primera emisión.
    /// </summary>
    public void SetIssuer(string? taxId, string? legalName, TimeProvider? timeProvider = null)
    {
        IssuerTaxId = Trim(taxId);
        IssuerLegalName = Trim(legalName);
        UpdatedAt = (timeProvider ?? TimeProvider.System).GetUtcNow();
    }

    private static string? Trim(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    public bool IsInEffectOn(DateOnly day) =>
        day >= ValidFrom && (ValidTo is not { } end || day <= end);

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
}
