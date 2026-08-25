namespace IMed.Api.Domain.Billing.Accounts;

/// <summary>
/// La cuenta donde se acumulan los cargos de un paciente (FSF bloque 3).
///
/// <para><b>Por qué la cuenta y no la factura.</b> Entre el primer cargo y la factura pasa tiempo:
/// el paciente entra, se le hacen cosas, y recién al final se cobra. Sin una cuenta, cada acto
/// clínico tendría que decidir por su cuenta a qué documento pertenece — y los actos clínicos no
/// saben nada de facturación, ni deberían.</para>
///
/// <para><b>El garante va aparte del paciente</b> porque no siempre son la misma persona: un menor
/// lo paga su madre, un empleado lo paga su empresa. Modelarlo después obliga a migrar cuentas ya
/// facturadas para saber a quién se le cobró.</para>
/// </summary>
public sealed class BillingAccount
{
    public Guid Id { get; private set; }
    public Guid OrganizationId { get; private set; }
    public BillingAccountKind Kind { get; private set; }

    /// <summary>A quién se le cobra. Es una <c>party</c> de FAC-1.</summary>
    public Guid PartyId { get; private set; }

    /// <summary>Paciente del expediente, cuando la cuenta es de uno.</summary>
    public string? PatientFhirId { get; private set; }

    /// <summary>Responsable de pago, si no es el mismo que <see cref="PartyId"/>.</summary>
    public Guid? GuarantorPartyId { get; private set; }

    /// <summary>Encuentro que originó la cuenta, cuando es de episodio.</summary>
    public string? EncounterFhirId { get; private set; }

    public BillingAccountStatus Status { get; private set; }
    public DateTimeOffset OpenedAt { get; private set; }
    public DateTimeOffset? ClosedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    private BillingAccount() { }

    public static BillingAccount Open(
        Guid organizationId,
        Guid partyId,
        BillingAccountKind kind = BillingAccountKind.Episode,
        string? patientFhirId = null,
        string? encounterFhirId = null,
        Guid? guarantorPartyId = null,
        TimeProvider? timeProvider = null)
    {
        if (partyId == Guid.Empty)
        {
            throw new ArgumentException("La cuenta necesita saber a quién se le cobra.", nameof(partyId));
        }

        var now = (timeProvider ?? TimeProvider.System).GetUtcNow();
        return new BillingAccount
        {
            Id = Guid.CreateVersion7(now),
            OrganizationId = organizationId,
            Kind = kind,
            PartyId = partyId,
            PatientFhirId = string.IsNullOrWhiteSpace(patientFhirId) ? null : patientFhirId.Trim(),
            EncounterFhirId = string.IsNullOrWhiteSpace(encounterFhirId) ? null : encounterFhirId.Trim(),
            GuarantorPartyId = guarantorPartyId,
            Status = BillingAccountStatus.Open,
            OpenedAt = now,
            UpdatedAt = now,
        };
    }

    /// <summary>¿Se le pueden seguir agregando cargos?</summary>
    public bool AcceptsCharges => Status == BillingAccountStatus.Open;

    public void Close(TimeProvider? timeProvider = null)
    {
        if (Status is BillingAccountStatus.Invoiced or BillingAccountStatus.Voided)
        {
            throw new InvalidOperationException(
                $"Una cuenta en estado '{Status}' ya no se cierra.");
        }
        var now = (timeProvider ?? TimeProvider.System).GetUtcNow();
        Status = BillingAccountStatus.Closed;
        ClosedAt = now;
        UpdatedAt = now;
    }

    /// <summary>
    /// Reabre una cuenta cerrada. Existe porque el caso real —apareció un cargo tardío, un
    /// resultado que se liberó después— es frecuente, y sin esto se resuelve abriendo una segunda
    /// cuenta del mismo episodio, que parte el estado de cuenta del paciente en dos.
    /// </summary>
    public void Reopen(TimeProvider? timeProvider = null)
    {
        if (Status != BillingAccountStatus.Closed)
        {
            throw new InvalidOperationException(
                $"Solo se reabre una cuenta cerrada; esta está en '{Status}'.");
        }
        Status = BillingAccountStatus.Open;
        ClosedAt = null;
        UpdatedAt = (timeProvider ?? TimeProvider.System).GetUtcNow();
    }

    public void MarkInvoiced(TimeProvider? timeProvider = null)
    {
        Status = BillingAccountStatus.Invoiced;
        UpdatedAt = (timeProvider ?? TimeProvider.System).GetUtcNow();
    }

    public void AssignGuarantor(Guid? guarantorPartyId, TimeProvider? timeProvider = null)
    {
        GuarantorPartyId = guarantorPartyId;
        UpdatedAt = (timeProvider ?? TimeProvider.System).GetUtcNow();
    }
}
