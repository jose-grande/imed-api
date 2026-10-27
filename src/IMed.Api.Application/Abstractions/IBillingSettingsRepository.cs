using IMed.Api.Domain.Billing;

namespace IMed.Api.Application.Abstractions;

/// <summary>Parámetros de facturación por organización (P1).</summary>
public interface IBillingSettingsRepository
{
    /// <summary>Los parámetros de la organización, o los valores por defecto si nunca se guardaron.</summary>
    Task<BillingSettings> GetAsync(Guid organizationId, CancellationToken ct = default);

    Task SaveAsync(BillingSettings settings, CancellationToken ct = default);
}
