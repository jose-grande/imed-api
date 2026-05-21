namespace IMed.Api.Domain.Subscriptions;

/// <summary>
/// Contrato de billing de una Organization. Define cuántos asientos de cada tier
/// puede asignar el Org Admin. Ver ADR-006 rev 4.
///
/// Las columnas <see cref="Name"/> y <see cref="FhirId"/> son denormalizaciones
/// de HAPI FHIR para evitar un round-trip por cada lectura de perfil. Si el
/// nombre cambia en HAPI (caso raro), un sync job o el endpoint de update lo
/// refleja acá.
/// </summary>
public sealed class OrganizationSubscription
{
    public Guid OrganizationId { get; private set; }
    public string Name { get; private set; } = string.Empty;
    public string FhirId { get; private set; } = string.Empty;
    public ContractStatus ContractStatus { get; private set; }
    public int SeatsBasic { get; private set; }
    public int SeatsPremium { get; private set; }
    public int SeatsPlatinum { get; private set; }
    public DateTimeOffset EffectiveFrom { get; private set; }
    public DateTimeOffset? TrialEndsAt { get; private set; }

    private OrganizationSubscription() { }

    public static OrganizationSubscription CreateBootstrapTrial(
        Guid organizationId,
        string name,
        string fhirId,
        int trialDurationDays = 30,
        TimeProvider? timeProvider = null)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Name es obligatorio.", nameof(name));
        }

        if (string.IsNullOrWhiteSpace(fhirId))
        {
            throw new ArgumentException("FhirId es obligatorio.", nameof(fhirId));
        }

        var now = (timeProvider ?? TimeProvider.System).GetUtcNow();
        return new OrganizationSubscription
        {
            OrganizationId = organizationId,
            Name = name,
            FhirId = fhirId,
            ContractStatus = ContractStatus.Trial,
            SeatsBasic = 1,
            SeatsPremium = 0,
            SeatsPlatinum = 0,
            EffectiveFrom = now,
            TrialEndsAt = now.AddDays(trialDurationDays)
        };
    }

    public bool HasAvailableSeat(SubscriptionTier tier, int currentlyAssigned) => tier switch
    {
        SubscriptionTier.Basic => currentlyAssigned < SeatsBasic,
        SubscriptionTier.Premium => currentlyAssigned < SeatsPremium,
        SubscriptionTier.Platinum => currentlyAssigned < SeatsPlatinum,
        _ => false
    };
}

public enum ContractStatus
{
    Trial = 0,
    Active = 1,
    PastDue = 2,
    Canceled = 3,
}
