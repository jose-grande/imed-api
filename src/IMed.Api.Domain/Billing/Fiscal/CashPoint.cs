namespace IMed.Api.Domain.Billing.Fiscal;

/// <summary>
/// Punto de cobro — una caja física (FSF, hueco G1).
///
/// <para><b>Se puede crear en caliente.</b> Una clínica abre una caja extra el día que hay fila,
/// no la semana anterior. Si dar de alta una caja exigiera reiniciar el sistema o un trámite
/// previo, la caja extra se abre igual pero por fuera del sistema — con recibos a mano.</para>
///
/// <para><b>El punto de emisión es configuración, no invariante.</b> Varias cajas pueden compartir
/// <see cref="EmissionPointCode"/> o cada una tener el suyo; el código soporta las dos y la
/// autoridad decide cuál corresponde. Por eso no hay índice único sobre esta columna: imponerlo
/// cerraría la mitad de las configuraciones legales antes de saber cuál aplica.</para>
/// </summary>
public sealed class CashPoint
{
    public Guid Id { get; private set; }
    public Guid OrganizationId { get; private set; }

    /// <summary>Sede donde está la caja. Es <c>organization_location</c> (ADR-008).</summary>
    public Guid LocationId { get; private set; }

    /// <summary>Código corto y estable, único dentro de la organización. Ej.: <c>CAJA1</c>.</summary>
    public string Code { get; private set; } = string.Empty;

    public string Name { get; private set; } = string.Empty;
    public CashPointProfile Profile { get; private set; }

    /// <summary>Punto de emisión con el que numera esta caja (3 dígitos).</summary>
    public string EmissionPointCode { get; private set; } = string.Empty;

    /// <summary>
    /// Caja abierta para una jornada puntual. Se marca para que el corte del día la distinga de
    /// las permanentes y para poder darla de baja sin que parezca un error de configuración.
    /// </summary>
    public bool IsTemporary { get; private set; }

    /// <summary>Plantilla de impresión: ticket de 80 mm o carta. La resuelve el renderer (FAC-5).</summary>
    public string? PrinterProfile { get; private set; }

    public bool Active { get; private set; } = true;
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    private CashPoint() { }

    public static CashPoint Create(
        Guid organizationId,
        Guid locationId,
        string code,
        string name,
        string emissionPointCode,
        CashPointProfile profile = CashPointProfile.Services,
        bool isTemporary = false,
        string? printerProfile = null,
        TimeProvider? timeProvider = null)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            throw new ArgumentException("El código de la caja es obligatorio.", nameof(code));
        }
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("El nombre de la caja es obligatorio.", nameof(name));
        }

        var point = (emissionPointCode ?? string.Empty).Trim();
        if (point.Length != 3 || !point.All(char.IsAsciiDigit))
        {
            throw new ArgumentException(
                "El punto de emisión son exactamente 3 dígitos.", nameof(emissionPointCode));
        }

        var now = (timeProvider ?? TimeProvider.System).GetUtcNow();
        return new CashPoint
        {
            Id = Guid.CreateVersion7(now),
            OrganizationId = organizationId,
            LocationId = locationId,
            Code = code.Trim().ToUpperInvariant(),
            Name = name.Trim(),
            Profile = profile,
            EmissionPointCode = point,
            IsTemporary = isTemporary,
            PrinterProfile = string.IsNullOrWhiteSpace(printerProfile) ? null : printerProfile.Trim(),
            Active = true,
            CreatedAt = now,
            UpdatedAt = now,
        };
    }

    /// <summary>
    /// Cierra la caja. No se borra: los turnos, pagos y documentos que la referencian son historia
    /// y tienen que seguir resolviendo de qué caja salieron.
    /// </summary>
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
}
