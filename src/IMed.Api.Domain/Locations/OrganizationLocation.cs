namespace IMed.Api.Domain.Locations;

/// <summary>
/// Sede física de una organización — sucursal, punto de toma, laboratorio de proceso (ADR-008).
///
/// <para><b>Por qué no es una Organization hija.</b> Si cada sede fuera su propia organización sería
/// su propia partición HAPI, y el expediente del paciente quedaría fragmentado por sucursal: quien
/// se atiende en dos sedes tendría dos historias sin relación. El valor de una red de clínicas es
/// justamente que el expediente siga al paciente. Por eso la sede es un <c>Location</c> FHIR dentro
/// de <b>una</b> organización.</para>
///
/// <para><b>Qué es de la organización y qué es de la sede.</b> Lo clínico —expediente, catálogo,
/// rangos, reglas, roles— es de la organización. Lo operativo —worklists, colas, correlativos,
/// impresoras, equipos, inventario, caja— es de la sede.</para>
///
/// <para><b>Espejo local, no copia autoritativa.</b> El recurso FHIR es la fuente de verdad de los
/// datos de contacto y dirección; esta fila existe para tener una clave estable a la que puedan
/// apuntar las tablas operativas y las asignaciones de usuario. <see cref="LocationFhirId"/> se
/// llena cuando el recurso se materializa en HAPI; que esté vacío no invalida la sede.</para>
/// </summary>
public sealed class OrganizationLocation
{
    public Guid Id { get; private set; }
    public Guid OrganizationId { get; private set; }

    /// <summary>Código corto y estable, único dentro de la organización. Es lo que se imprime.</summary>
    public string Code { get; private set; } = string.Empty;

    public string Name { get; private set; } = string.Empty;

    /// <summary>Id del <c>Location</c> FHIR, si ya se materializó.</summary>
    public string? LocationFhirId { get; private set; }

    /// <summary>
    /// Sede por defecto de la organización. Es la que se asume cuando el usuario no eligió
    /// ninguna y la que recibe el backfill de los datos anteriores al eje de sede.
    /// </summary>
    public bool IsDefault { get; private set; }

    /// <summary>
    /// Código de establecimiento con el que la autoridad fiscal identifica esta sede — en Honduras,
    /// los tres primeros dígitos del número de factura (FSF, bloque 0).
    ///
    /// <para>Vive acá y no en una tabla aparte porque la "sede física" del módulo de facturación y
    /// la sede de ADR-008 son la misma cosa: dos tablas de sedes obligarían a contestar en cada
    /// bodega, caja y punto de emisión cuál de las dos es la buena, y a mantenerlas sincronizadas
    /// a mano.</para>
    ///
    /// <para>Null mientras la sede no facture. El RTN y la razón social los aporta
    /// <see cref="LegalEntityId"/>; la sede aporta solo este código.</para>
    /// </summary>
    public string? FiscalEstablishmentCode { get; private set; }

    /// <summary>
    /// Razón social a la que pertenece la sede (FAC-9). Es la que emite sus documentos fiscales:
    /// dos sedes de la misma razón social comparten RTN; dos de razones sociales distintas son,
    /// para el SAR, dos contribuyentes. Reemplaza a FSF-DEC-07, que suponía un solo emisor.
    /// </summary>
    public Guid LegalEntityId { get; private set; }

    /// <summary>Dirección física de la sede. Es la del establecimiento, no el domicilio fiscal.</summary>
    public string? Address { get; private set; }

    public string? Phone { get; private set; }

    public bool Active { get; private set; } = true;
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    private OrganizationLocation() { }

    /// <summary>
    /// Id FHIR determinístico del <c>Location</c>. Se calcula y no se espera a que HAPI lo asigne
    /// para que la sincronización sea un <c>PUT</c> idempotente —reintentarla no crea duplicados— y
    /// para que el frontend pueda referenciar la sede apenas existe. El prefijo garantiza un id
    /// alfanumérico, que HAPI acepta en un PUT.
    /// </summary>
    public static string FhirIdFor(Guid locationId) => $"sede-{locationId:N}";

    public static OrganizationLocation Create(
        Guid organizationId,
        string code,
        string name,
        Guid legalEntityId,
        bool isDefault = false,
        string? address = null,
        string? phone = null,
        TimeProvider? timeProvider = null)
    {
        if (legalEntityId == Guid.Empty)
        {
            throw new ArgumentException("La sede necesita una razón social.", nameof(legalEntityId));
        }
        if (string.IsNullOrWhiteSpace(code))
        {
            throw new ArgumentException("El código de la sede es obligatorio.", nameof(code));
        }
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("El nombre de la sede es obligatorio.", nameof(name));
        }

        var now = (timeProvider ?? TimeProvider.System).GetUtcNow();
        return new OrganizationLocation
        {
            Id = Guid.CreateVersion7(now),
            OrganizationId = organizationId,
            Code = code.Trim().ToUpperInvariant(),
            Name = name.Trim(),
            LegalEntityId = legalEntityId,
            Address = Clean(address),
            Phone = Clean(phone),
            IsDefault = isDefault,
            Active = true,
            CreatedAt = now,
            UpdatedAt = now,
        };
    }

    /// <summary>Ancla el recurso FHIR una vez materializado.</summary>
    public void AttachFhirLocation(string locationFhirId, TimeProvider? timeProvider = null)
    {
        if (string.IsNullOrWhiteSpace(locationFhirId))
        {
            throw new ArgumentException("El id del Location FHIR es obligatorio.", nameof(locationFhirId));
        }
        LocationFhirId = locationFhirId;
        UpdatedAt = (timeProvider ?? TimeProvider.System).GetUtcNow();
    }

    /// <summary>
    /// Fija el código de establecimiento fiscal. Se valida acá y no en la pantalla porque un
    /// código mal formado no se descubre al guardarlo: se descubre cuando el primer documento
    /// sale con un número que la autoridad no reconoce.
    /// </summary>
    public void SetFiscalEstablishmentCode(string? code, TimeProvider? timeProvider = null)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            FiscalEstablishmentCode = null;
        }
        else
        {
            var trimmed = code.Trim();
            if (trimmed.Length != 3 || !trimmed.All(char.IsAsciiDigit))
            {
                throw new ArgumentException(
                    "El código de establecimiento son exactamente 3 dígitos.", nameof(code));
            }
            FiscalEstablishmentCode = trimmed;
        }
        UpdatedAt = (timeProvider ?? TimeProvider.System).GetUtcNow();
    }

    /// <summary>Actualiza la dirección y el teléfono del establecimiento.</summary>
    public void UpdateContact(string? address, string? phone, TimeProvider? timeProvider = null)
    {
        Address = Clean(address);
        Phone = Clean(phone);
        UpdatedAt = (timeProvider ?? TimeProvider.System).GetUtcNow();
    }

    /// <summary>
    /// Pasa la sede a otra razón social. Si se puede o no —una sede con CAI vigentes de la razón
    /// social anterior no puede cambiar de emisor sin darlos de baja— lo decide el handler, que es
    /// quien ve las autorizaciones.
    /// </summary>
    public void MoveToLegalEntity(Guid legalEntityId, TimeProvider? timeProvider = null)
    {
        if (legalEntityId == Guid.Empty)
        {
            throw new ArgumentException("La sede necesita una razón social.", nameof(legalEntityId));
        }
        LegalEntityId = legalEntityId;
        UpdatedAt = (timeProvider ?? TimeProvider.System).GetUtcNow();
    }

    private static string? Clean(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    public void Rename(string name, TimeProvider? timeProvider = null)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("El nombre de la sede es obligatorio.", nameof(name));
        }
        Name = name.Trim();
        UpdatedAt = (timeProvider ?? TimeProvider.System).GetUtcNow();
    }

    /// <summary>
    /// Desactiva la sede. No se borra: las órdenes, muestras y correlativos que la referencian
    /// son historia y tienen que seguir resolviendo su sede aunque ya no se opere ahí.
    /// </summary>
    public void Deactivate(TimeProvider? timeProvider = null)
    {
        if (IsDefault)
        {
            throw new InvalidOperationException(
                "La sede por defecto no se puede desactivar: designá otra como principal primero.");
        }
        Active = false;
        UpdatedAt = (timeProvider ?? TimeProvider.System).GetUtcNow();
    }

    public void Reactivate(TimeProvider? timeProvider = null)
    {
        Active = true;
        UpdatedAt = (timeProvider ?? TimeProvider.System).GetUtcNow();
    }

    /// <summary>Designa esta sede como la principal. El llamador debe quitarle la marca a la anterior.</summary>
    public void MarkDefault(TimeProvider? timeProvider = null)
    {
        if (!Active)
        {
            throw new InvalidOperationException("Una sede inactiva no puede ser la principal.");
        }
        IsDefault = true;
        UpdatedAt = (timeProvider ?? TimeProvider.System).GetUtcNow();
    }

    public void ClearDefault(TimeProvider? timeProvider = null)
    {
        IsDefault = false;
        UpdatedAt = (timeProvider ?? TimeProvider.System).GetUtcNow();
    }
}
