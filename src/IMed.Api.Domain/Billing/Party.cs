namespace IMed.Api.Domain.Billing;

/// <summary>Qué es la contraparte de una transacción.</summary>
public enum PartyKind
{
    /// <summary>Paciente del expediente. Exige <see cref="Party.PatientFhirId"/>.</summary>
    Patient = 0,

    /// <summary>Cliente de mostrador: compró algo sin ser paciente. Venta libre de farmacia.</summary>
    WalkInCustomer = 1,

    /// <summary>Aseguradora o empresa con convenio.</summary>
    Payer = 2,

    /// <summary>Proveedor de insumos o medicamentos.</summary>
    Supplier = 3,

    /// <summary>Empresa que paga por sus empleados sin ser aseguradora.</summary>
    Company = 4,
}

/// <summary>
/// El otro lado de la transacción (FSF, bloque 0).
///
/// <para><b>Por qué no se cuelga todo de <c>Patient</c>.</b> Quien compra no siempre es paciente:
/// puede ser alguien que pasó a comprar ibuprofeno, una aseguradora o un proveedor. Crear un
/// <c>Patient</c> para poder facturarle mete registros administrativos en el expediente clínico —
/// y el expediente deja de responder "quiénes son mis pacientes" sin una limpieza previa.</para>
///
/// <para><b>Espejo, no dueño.</b> Cuando la parte es un paciente, el <c>Patient</c> FHIR sigue
/// siendo la fuente de verdad de sus datos; esta fila existe para que las cuentas y los documentos
/// fiscales tengan una clave estable a la que apuntar, y para guardar lo que el expediente no
/// tiene: el RTN con el que se factura.</para>
/// </summary>
public sealed class Party
{
    public Guid Id { get; private set; }
    public Guid OrganizationId { get; private set; }
    public PartyKind Kind { get; private set; }

    /// <summary>
    /// Paciente del expediente, cuando <see cref="Kind"/> es <see cref="PartyKind.Patient"/>.
    /// Obligatorio en ese caso y único por organización — dos partes apuntando al mismo paciente
    /// significarían dos estados de cuenta para la misma persona.
    /// </summary>
    public string? PatientFhirId { get; private set; }

    /// <summary>Razón social o nombre completo. Es lo que se imprime en el documento fiscal.</summary>
    public string LegalName { get; private set; } = string.Empty;

    /// <summary>Nombre comercial, cuando difiere del legal.</summary>
    public string? TradeName { get; private set; }

    /// <summary>
    /// RTN en Honduras. Nullable a propósito: la venta de mostrador bajo el umbral no lo exige, y
    /// obligarlo convertiría cada venta chica en un trámite. El umbral se valida al emitir (FAC-5),
    /// que es donde la regla fiscal aplica.
    /// </summary>
    public string? TaxId { get; private set; }

    /// <summary>Qué clase de identificación tributaria es <see cref="TaxId"/> (RTN, DNI, pasaporte).</summary>
    public string? TaxIdKind { get; private set; }

    public string? Address { get; private set; }
    public string? Email { get; private set; }
    public string? Phone { get; private set; }

    public bool Active { get; private set; } = true;
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    private Party() { }

    public static Party Create(
        Guid organizationId,
        PartyKind kind,
        string legalName,
        string? patientFhirId = null,
        string? taxId = null,
        string? taxIdKind = null,
        string? tradeName = null,
        string? address = null,
        string? email = null,
        string? phone = null,
        TimeProvider? timeProvider = null)
    {
        if (string.IsNullOrWhiteSpace(legalName))
        {
            throw new ArgumentException("El nombre de la parte es obligatorio.", nameof(legalName));
        }

        // La invariante que hace utilizable el vínculo con el expediente: una parte de tipo
        // paciente sin paciente es una fila que no se puede reconciliar con nada.
        if (kind == PartyKind.Patient && string.IsNullOrWhiteSpace(patientFhirId))
        {
            throw new ArgumentException(
                "Una parte de tipo paciente exige el paciente del expediente.", nameof(patientFhirId));
        }
        if (kind != PartyKind.Patient && !string.IsNullOrWhiteSpace(patientFhirId))
        {
            throw new ArgumentException(
                $"Una parte de tipo '{kind}' no puede apuntar a un paciente del expediente.",
                nameof(patientFhirId));
        }

        var now = (timeProvider ?? TimeProvider.System).GetUtcNow();
        return new Party
        {
            Id = Guid.CreateVersion7(now),
            OrganizationId = organizationId,
            Kind = kind,
            PatientFhirId = Trim(patientFhirId),
            LegalName = legalName.Trim(),
            TradeName = Trim(tradeName),
            TaxId = Trim(taxId),
            TaxIdKind = Trim(taxIdKind),
            Address = Trim(address),
            Email = Trim(email),
            Phone = Trim(phone),
            Active = true,
            CreatedAt = now,
            UpdatedAt = now,
        };
    }

    /// <summary>
    /// Actualiza los datos con los que se factura. No toca <see cref="Kind"/> ni
    /// <see cref="PatientFhirId"/>: cambiar a quién representa una parte que ya tiene cuentas y
    /// documentos emitidos reescribiría la historia de esos documentos.
    /// </summary>
    public void UpdateDetails(
        string legalName,
        string? taxId = null,
        string? taxIdKind = null,
        string? tradeName = null,
        string? address = null,
        string? email = null,
        string? phone = null,
        TimeProvider? timeProvider = null)
    {
        if (string.IsNullOrWhiteSpace(legalName))
        {
            throw new ArgumentException("El nombre de la parte es obligatorio.", nameof(legalName));
        }

        LegalName = legalName.Trim();
        TaxId = Trim(taxId);
        TaxIdKind = Trim(taxIdKind);
        TradeName = Trim(tradeName);
        Address = Trim(address);
        Email = Trim(email);
        Phone = Trim(phone);
        UpdatedAt = (timeProvider ?? TimeProvider.System).GetUtcNow();
    }

    public void Deactivate(TimeProvider? timeProvider = null)
    {
        Active = false;
        UpdatedAt = (timeProvider ?? TimeProvider.System).GetUtcNow();
    }

    public void Reactivate(TimeProvider? timeProvider = null)
    {
        Active = true;
        UpdatedAt = (timeProvider ?? TimeProvider.System).GetUtcNow();
    }

    private static string? Trim(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
