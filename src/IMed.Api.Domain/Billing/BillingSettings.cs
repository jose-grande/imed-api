namespace IMed.Api.Domain.Billing;

/// <summary>
/// Cuándo cobra la institución (UX-Cobro-Anticipado §4). Decide conductas por defecto —dónde abre
/// la Caja, si la puerta de prepago bloquea, si se reconoce la consulta precobrada—; el tiempo de
/// pago de cada cobrable (<c>PaymentTiming</c>) las afina.
/// </summary>
public enum BillingCollectionMode
{
    /// <summary>Se atiende y se cobra al salir. Es el comportamiento de siempre y el valor por defecto.</summary>
    AfterService = 0,

    /// <summary>Se cobra antes de prestar el servicio.</summary>
    BeforeService = 1,

    /// <summary>Según el paciente o el servicio.</summary>
    Mixed = 2,
}

/// <summary>
/// Parámetros de facturación de una organización (plan de salida a producción, P1). Una fila por
/// organización; si no existe, valen los valores por defecto.
/// </summary>
public sealed class BillingSettings
{
    /// <summary>Ventana de "control" por defecto: 30 días.</summary>
    public const int DefaultFollowUpWindowDays = 30;

    public Guid OrganizationId { get; private set; }

    /// <summary>
    /// Días dentro de los que una nueva consulta de la misma especialidad se propone como
    /// <b>control</b> y no como primera vez. Se propone: la persona lo puede cambiar.
    /// </summary>
    public int FollowUpWindowDays { get; private set; } = DefaultFollowUpWindowDays;

    /// <summary>
    /// Modo de cobro. <b>Por ahora solo se guarda y se lee</b> (fase 0): ninguna conducta depende de
    /// él todavía. "Después" es el valor por defecto para que las organizaciones existentes no vean
    /// ningún cambio hasta que alguien elija otro modo a propósito.
    /// </summary>
    public BillingCollectionMode CollectionMode { get; private set; } = BillingCollectionMode.AfterService;

    public DateTimeOffset UpdatedAt { get; private set; }

    private BillingSettings() { }

    public static BillingSettings Defaults(Guid organizationId) =>
        new() { OrganizationId = organizationId, FollowUpWindowDays = DefaultFollowUpWindowDays };

    public void SetCollectionMode(BillingCollectionMode mode, TimeProvider? timeProvider = null)
    {
        if (!Enum.IsDefined(mode))
        {
            throw new ArgumentOutOfRangeException(nameof(mode), "Modo de cobro desconocido.");
        }
        CollectionMode = mode;
        UpdatedAt = (timeProvider ?? TimeProvider.System).GetUtcNow();
    }

    public void SetFollowUpWindow(int days, TimeProvider? timeProvider = null)
    {
        if (days is < 0 or > 365)
        {
            throw new ArgumentOutOfRangeException(nameof(days), "La ventana de control va de 0 a 365 días.");
        }
        FollowUpWindowDays = days;
        UpdatedAt = (timeProvider ?? TimeProvider.System).GetUtcNow();
    }
}
