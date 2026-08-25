using IMed.Api.Domain.Billing.Fiscal;

namespace IMed.Api.Application.Abstractions;

/// <summary>
/// Resultado de intentar reservar un correlativo. Es un resultado y no una excepción porque
/// quedarse sin números es un caso de negocio esperable —pasa un sábado con la clínica llena— y no
/// una falla del programa.
/// </summary>
/// <param name="Sequence">El correlativo reservado, si se pudo.</param>
/// <param name="Failure">Por qué no se pudo, si no se pudo.</param>
public sealed record FiscalReservation(long? Sequence, FiscalReservationFailure? Failure)
{
    public bool Reserved => Sequence is not null;

    public static FiscalReservation Ok(long sequence) => new(sequence, null);
    public static FiscalReservation Failed(FiscalReservationFailure failure) => new(null, failure);
}

public interface IFiscalRepository
{
    // ---- Régimen y catálogos del núcleo ----
    Task<FiscalRegime?> GetActiveRegimeAsync(Guid organizationId, DateOnly day, CancellationToken ct = default);
    Task AddRegimeAsync(FiscalRegime regime, CancellationToken ct = default);

    Task<IReadOnlyList<TaxCode>> ListTaxCodesAsync(Guid organizationId, CancellationToken ct = default);
    Task<TaxCode?> GetTaxCodeAsync(Guid organizationId, Guid id, CancellationToken ct = default);
    Task AddTaxCodeAsync(TaxCode taxCode, CancellationToken ct = default);

    Task<IReadOnlyList<FiscalDocumentType>> ListDocumentTypesAsync(Guid organizationId, CancellationToken ct = default);
    Task<FiscalDocumentType?> GetDocumentTypeAsync(Guid organizationId, Guid id, CancellationToken ct = default);
    Task AddDocumentTypeAsync(FiscalDocumentType type, CancellationToken ct = default);

    // ---- Autorizaciones ----
    Task<IReadOnlyList<FiscalAuthorization>> ListAuthorizationsAsync(Guid organizationId, CancellationToken ct = default);
    Task<FiscalAuthorization?> GetAuthorizationAsync(Guid organizationId, Guid id, CancellationToken ct = default);
    Task AddAuthorizationAsync(FiscalAuthorization authorization, CancellationToken ct = default);
    Task UpdateAuthorizationAsync(FiscalAuthorization authorization, CancellationToken ct = default);

    /// <summary>
    /// Reserva el próximo correlativo de una autorización, <b>atómicamente</b>.
    ///
    /// <para><b>Por qué está en el repositorio y no en la entidad.</b> Leer la fila, decidir en
    /// memoria y guardar deja una ventana entre la lectura y la escritura: dos cajas que cobran al
    /// mismo tiempo leen el mismo <c>next_sequence</c> y emiten dos documentos con el mismo número.
    /// Eso no es un bug cosmético, es incumplimiento fiscal. La única forma de cerrarlo es que la
    /// verificación y el incremento sean la misma sentencia, y eso solo lo puede garantizar la base.</para>
    ///
    /// <para>Es el mismo patrón ya probado en <c>lab_order_counter</c> y <c>lab_accession_counter</c>,
    /// con una diferencia: acá el contador tiene techo, así que el <c>UPDATE</c> lleva las
    /// condiciones adentro y no entrega número cuando no corresponde.</para>
    /// </summary>
    /// <param name="today">Día de la clínica, para contrastar contra la fecha límite.</param>
    Task<FiscalReservation> ReserveNextSequenceAsync(
        Guid organizationId, Guid authorizationId, DateOnly today, CancellationToken ct = default);

    // ---- Puntos de cobro ----
    Task<IReadOnlyList<CashPoint>> ListCashPointsAsync(Guid organizationId, CancellationToken ct = default);
    Task<CashPoint?> GetCashPointAsync(Guid organizationId, Guid id, CancellationToken ct = default);
    Task<bool> CashPointCodeExistsAsync(Guid organizationId, string code, CancellationToken ct = default);
    Task AddCashPointAsync(CashPoint cashPoint, CancellationToken ct = default);
    Task UpdateCashPointAsync(CashPoint cashPoint, CancellationToken ct = default);
}
