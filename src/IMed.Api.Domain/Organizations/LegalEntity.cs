namespace IMed.Api.Domain.Organizations;

/// <summary>
/// Razón social: la persona jurídica que emite los documentos fiscales de una o más sedes
/// (épica FAC-9, FAC9-DEC-01).
///
/// <para><b>Por qué existe entre la organización y la sede.</b> Una cadena de clínicas es un solo
/// tenant —el expediente sigue al paciente en todas sus sedes (ADR-008)—, pero sus sedes no
/// siempre son la misma persona jurídica. Hay cadenas con un solo RTN y varias sucursales, y hay
/// cadenas donde cada sede es una sociedad distinta. Para el SAR la diferencia es total: cada RTN
/// tiene sus propios CAI, sus propios libros y su propia declaración de ISV. La razón social es lo
/// que permite decir de quién es cada documento sin partir el expediente.</para>
///
/// <para><b>Siempre hay una por defecto.</b> La organización nace con una sola razón social y todas
/// sus sedes cuelgan de ella; una clínica de un solo local no se entera de que el concepto existe.
/// Las adicionales se crean al dar de alta una sede que es otra persona jurídica.</para>
///
/// <para><b>El RTN no se reescribe sobre documentos emitidos.</b> Si la sociedad cambia de RTN es,
/// a efectos fiscales, otra persona jurídica: se crea una nueva y se le pasan las sedes. Así cada
/// documento ya emitido sigue explicándose con el RTN que llevaba impreso. La regla la aplica el
/// handler, que es quien puede consultar si ya hubo emisiones.</para>
/// </summary>
public sealed class LegalEntity
{
    /// <summary>Largo del RTN hondureño, sin guiones.</summary>
    public const int TaxIdLength = 14;

    public Guid Id { get; private set; }
    public Guid OrganizationId { get; private set; }

    /// <summary>Razón social tal como debe salir impresa en la factura.</summary>
    public string LegalName { get; private set; } = string.Empty;

    /// <summary>Nombre comercial, si difiere de la razón social. Va en el encabezado.</summary>
    public string? TradeName { get; private set; }

    /// <summary>
    /// RTN, normalizado a solo dígitos. Nullable únicamente para la razón social por defecto de las
    /// organizaciones que se crearon sin él: la emisión lo exige y se rechaza sin él.
    /// </summary>
    public string? TaxId { get; private set; }

    /// <summary>Domicilio fiscal registrado ante la autoridad.</summary>
    public string? FiscalAddress { get; private set; }

    public string? Phone { get; private set; }
    public string? Email { get; private set; }

    /// <summary>La que reciben las sedes que no declaran otra. Una por organización.</summary>
    public bool IsDefault { get; private set; }

    public bool Active { get; private set; } = true;

    /// <summary>
    /// Id del <c>Organization</c> FHIR que la representa, <c>partOf</c> la organización tenant y en
    /// su misma partición. Es determinístico (ver <see cref="FhirIdFor"/>) para que la sincronización
    /// sea un <c>PUT</c> idempotente; queda en null hasta que HAPI lo confirma.
    /// </summary>
    public string? OrganizationFhirId { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    private LegalEntity() { }

    /// <summary>Id FHIR determinístico. El prefijo garantiza un id alfanumérico, que HAPI acepta en un PUT.</summary>
    public static string FhirIdFor(Guid legalEntityId) => $"rs-{legalEntityId:N}";

    /// <summary>
    /// Razón social adicional, registrada al crear una sede que es otra persona jurídica. Acá el
    /// RTN, la razón social y el domicilio fiscal son obligatorios: es justamente la información
    /// que la distingue de la principal.
    /// </summary>
    public static LegalEntity Create(
        Guid organizationId,
        string legalName,
        string taxId,
        string fiscalAddress,
        string? tradeName = null,
        string? phone = null,
        string? email = null,
        TimeProvider? timeProvider = null)
    {
        if (string.IsNullOrWhiteSpace(fiscalAddress))
        {
            throw new ArgumentException("El domicilio fiscal es obligatorio.", nameof(fiscalAddress));
        }

        var entity = New(organizationId, legalName, isDefault: false, timeProvider);
        entity.TaxId = NormalizeTaxId(taxId);
        entity.FiscalAddress = fiscalAddress.Trim();
        entity.TradeName = Clean(tradeName);
        entity.Phone = Clean(phone);
        entity.Email = Clean(email);
        return entity;
    }

    /// <summary>
    /// Razón social por defecto de una organización. El RTN es opcional y no se valida: viene del
    /// alta de la organización, donde es un campo libre, y rechazarlo acá dejaría a la organización
    /// sin razón social —y por lo tanto sin sede—. La emisión es la que lo exige.
    /// </summary>
    public static LegalEntity CreateDefault(
        Guid organizationId,
        string legalName,
        string? taxId = null,
        TimeProvider? timeProvider = null)
    {
        var entity = New(organizationId, legalName, isDefault: true, timeProvider);
        entity.TaxId = Clean(taxId)?.Replace("-", string.Empty).Replace(" ", string.Empty);
        return entity;
    }

    private static LegalEntity New(Guid organizationId, string legalName, bool isDefault, TimeProvider? timeProvider)
    {
        if (organizationId == Guid.Empty)
        {
            throw new ArgumentException("OrganizationId es obligatorio.", nameof(organizationId));
        }
        if (string.IsNullOrWhiteSpace(legalName))
        {
            throw new ArgumentException("La razón social es obligatoria.", nameof(legalName));
        }

        var now = (timeProvider ?? TimeProvider.System).GetUtcNow();
        return new LegalEntity
        {
            Id = Guid.CreateVersion7(now),
            OrganizationId = organizationId,
            LegalName = legalName.Trim(),
            IsDefault = isDefault,
            Active = true,
            CreatedAt = now,
            UpdatedAt = now,
        };
    }

    /// <summary>
    /// Actualiza los datos fiscales. El RTN solo se valida si cambia, para que editar el teléfono
    /// de una razón social por defecto con un RTN viejo mal cargado no obligue a corregirlo primero.
    /// Si el RTN puede cambiar o no lo decide el handler.
    /// </summary>
    public void Update(
        string legalName,
        string? taxId,
        string? fiscalAddress,
        string? tradeName,
        string? phone,
        string? email,
        TimeProvider? timeProvider = null)
    {
        if (string.IsNullOrWhiteSpace(legalName))
        {
            throw new ArgumentException("La razón social es obligatoria.", nameof(legalName));
        }

        var normalized = Clean(taxId)?.Replace("-", string.Empty).Replace(" ", string.Empty);
        if (normalized != TaxId)
        {
            TaxId = normalized is null && IsDefault ? null : NormalizeTaxId(taxId);
        }

        if (!IsDefault && string.IsNullOrWhiteSpace(fiscalAddress))
        {
            throw new ArgumentException("El domicilio fiscal es obligatorio.", nameof(fiscalAddress));
        }

        LegalName = legalName.Trim();
        FiscalAddress = Clean(fiscalAddress);
        TradeName = Clean(tradeName);
        Phone = Clean(phone);
        Email = Clean(email);
        UpdatedAt = (timeProvider ?? TimeProvider.System).GetUtcNow();
    }

    /// <summary>¿Cambiaría el RTN con este valor? Lo usa el handler para decidir si consulta emisiones.</summary>
    public bool WouldChangeTaxId(string? taxId) =>
        Clean(taxId)?.Replace("-", string.Empty).Replace(" ", string.Empty) != TaxId;

    /// <summary>Ancla el <c>Organization</c> FHIR una vez que HAPI confirmó la escritura.</summary>
    public void AttachFhirOrganization(string organizationFhirId, TimeProvider? timeProvider = null)
    {
        if (string.IsNullOrWhiteSpace(organizationFhirId))
        {
            throw new ArgumentException("El id del Organization FHIR es obligatorio.", nameof(organizationFhirId));
        }
        OrganizationFhirId = organizationFhirId;
        UpdatedAt = (timeProvider ?? TimeProvider.System).GetUtcNow();
    }

    /// <summary>
    /// RTN normalizado: sin guiones ni espacios, exactamente <see cref="TaxIdLength"/> dígitos. Se
    /// valida al registrarlo y no al emitir: un RTN mal formado se descubre recién cuando la
    /// primera factura sale con un emisor que la autoridad no reconoce.
    /// </summary>
    public static string NormalizeTaxId(string? taxId)
    {
        var digits = (taxId ?? string.Empty).Replace("-", string.Empty).Replace(" ", string.Empty).Trim();
        if (digits.Length != TaxIdLength || !digits.All(char.IsAsciiDigit))
        {
            throw new ArgumentException(
                $"El RTN son {TaxIdLength} dígitos (se aceptan guiones, que se descartan).", nameof(taxId));
        }
        return digits;
    }

    private static string? Clean(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
