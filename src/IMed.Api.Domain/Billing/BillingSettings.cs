namespace IMed.Api.Domain.Billing;

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

    public DateTimeOffset UpdatedAt { get; private set; }

    private BillingSettings() { }

    public static BillingSettings Defaults(Guid organizationId) =>
        new() { OrganizationId = organizationId, FollowUpWindowDays = DefaultFollowUpWindowDays };

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
