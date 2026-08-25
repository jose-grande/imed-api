namespace IMed.Api.Domain.Billing.Gate;

/// <summary>Si un servicio puede ejecutarse, y por qué.</summary>
public enum GateVerdict
{
    /// <summary>Puede ejecutarse: está pagado, es postpago, o es una emergencia.</summary>
    Authorized = 0,

    /// <summary>Es prepago y todavía no se cobró.</summary>
    PendingPayment = 1,

    /// <summary>Alguien con permiso lo liberó sin cobrar, con motivo registrado.</summary>
    Waived = 2,
}

/// <summary>
/// Por qué el veredicto es el que es. Se devuelve aparte del veredicto porque la pantalla tiene
/// que poder decirle al técnico <b>qué pasó</b>, no solo si puede seguir.
/// </summary>
public enum GateReason
{
    /// <summary>El cobrable está marcado postpago o indiferente: no bloquea nunca.</summary>
    Postpaid = 0,

    /// <summary>La factura que lo cubre está pagada.</summary>
    Paid = 1,

    /// <summary>Prioridad clínica: <c>stat</c> o <c>urgent</c>. Criterio clínico, no configuración.</summary>
    ClinicalUrgency = 2,

    /// <summary>Hay cargo capturado pero todavía sin cobrar.</summary>
    NotPaidYet = 3,

    /// <summary>Ni siquiera se capturó el cargo: el servicio no está en el catálogo cobrable.</summary>
    NotCharged = 4,

    /// <summary>Liberado por alguien con permiso.</summary>
    ExplicitWaiver = 5,
}

/// <summary>
/// El resultado de preguntarle a la puerta de prepago (FSF hueco G2, épica FAC-7).
///
/// <para><b>🔴 El estado se muestra, no se impone.</b> Ninguna de las tres respuestas es una
/// orden: son información para que el módulo clínico decida cómo mostrarla. Un sistema que bloquea
/// sin salida se evade —el técnico anota en papel y el registro se pierde—, y eso es peor que no
/// tener el control. Lo que sí hace la puerta es dejar constancia de quién siguió adelante sin
/// cobrar.</para>
/// </summary>
/// <param name="Verdict">Qué se puede hacer.</param>
/// <param name="Reason">Por qué.</param>
/// <param name="Message">Texto listo para mostrar, en el idioma del usuario.</param>
/// <param name="WaivedBy">Quién liberó, cuando corresponde.</param>
/// <param name="WaiverReason">Con qué motivo.</param>
/// <param name="AmountDue">Cuánto falta cobrar, si falta algo.</param>
public sealed record GateDecision(
    GateVerdict Verdict,
    GateReason Reason,
    string Message,
    Guid? WaivedBy = null,
    string? WaiverReason = null,
    decimal? AmountDue = null)
{
    /// <summary>¿Puede ejecutarse sin que nadie tenga que decidir nada?</summary>
    public bool CanProceedFreely => Verdict is GateVerdict.Authorized or GateVerdict.Waived;

    public static GateDecision Postpaid() => new(
        GateVerdict.Authorized, GateReason.Postpaid,
        "Se cobra después de ejecutarlo.");

    public static GateDecision Paid() => new(
        GateVerdict.Authorized, GateReason.Paid,
        "Pagado.");

    public static GateDecision Urgent() => new(
        GateVerdict.Authorized, GateReason.ClinicalUrgency,
        "Prioridad clínica: se ejecuta sin esperar el cobro.");

    public static GateDecision Pending(decimal amountDue) => new(
        GateVerdict.PendingPayment, GateReason.NotPaidYet,
        $"Pendiente de pago: faltan L {amountDue:N2}.",
        AmountDue: amountDue);

    public static GateDecision NotCharged() => new(
        GateVerdict.PendingPayment, GateReason.NotCharged,
        "Sin cargo capturado: este servicio no está enlazado a nada cobrable.");

    public static GateDecision Waived(Guid by, string reason) => new(
        GateVerdict.Waived, GateReason.ExplicitWaiver,
        $"Liberado sin cobrar: {reason}",
        WaivedBy: by, WaiverReason: reason);
}

/// <summary>
/// Constancia de que alguien dejó pasar un servicio sin cobrarlo.
///
/// <para><b>Convierte la excepción en un dato medible.</b> "Cuántos servicios se dieron sin cobrar
/// y quién los autorizó" es un reporte que el dueño va a pedir el primer mes; sin esta tabla la
/// respuesta es "no sabemos".</para>
/// </summary>
public sealed class ServiceWaiver
{
    public Guid Id { get; private set; }
    public Guid OrganizationId { get; private set; }

    /// <summary>Qué se liberó: <c>ServiceRequest/2405</c>, por ejemplo.</summary>
    public string SourceRef { get; private set; } = string.Empty;

    public Guid? AccountId { get; private set; }
    public Guid WaivedBy { get; private set; }
    public string Reason { get; private set; } = string.Empty;
    public DateTimeOffset WaivedAt { get; private set; }

    private ServiceWaiver() { }

    public static ServiceWaiver Create(
        Guid organizationId,
        string sourceRef,
        Guid waivedBy,
        string reason,
        Guid? accountId = null,
        TimeProvider? timeProvider = null)
    {
        if (string.IsNullOrWhiteSpace(sourceRef))
        {
            throw new ArgumentException("Hay que decir qué se libera.", nameof(sourceRef));
        }

        // Sin motivo, la constancia no sirve para el reporte: "se liberó" sin más es lo mismo que
        // no haber registrado nada.
        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new ArgumentException(
                "Liberar un servicio sin cobrar exige motivo.", nameof(reason));
        }

        var now = (timeProvider ?? TimeProvider.System).GetUtcNow();
        return new ServiceWaiver
        {
            Id = Guid.CreateVersion7(now),
            OrganizationId = organizationId,
            SourceRef = sourceRef.Trim(),
            AccountId = accountId,
            WaivedBy = waivedBy,
            Reason = reason.Trim(),
            WaivedAt = now,
        };
    }
}

/// <summary>Prioridades clínicas que la puerta nunca detiene.</summary>
public static class ClinicalUrgency
{
    private static readonly HashSet<string> Urgent =
        new(StringComparer.OrdinalIgnoreCase) { "stat", "urgent", "asap" };

    /// <summary>
    /// ¿Esta prioridad exige ejecutar sin esperar el cobro?
    ///
    /// <para>Es criterio clínico y no configuración: no hay una pantalla donde la clínica pueda
    /// decidir que un <c>stat</c> espere a pagar. Incluye <c>asap</c> además de los dos que nombra
    /// el plan, porque FHIR lo define entre <c>urgent</c> y <c>routine</c> y dejarlo afuera haría
    /// que una orden más urgente que <c>routine</c> se trate como rutina.</para>
    /// </summary>
    public static bool BypassesPayment(string? priority) =>
        !string.IsNullOrWhiteSpace(priority) && Urgent.Contains(priority.Trim());
}
