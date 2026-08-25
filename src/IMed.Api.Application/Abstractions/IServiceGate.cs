namespace IMed.Api.Application.Abstractions;

/// <summary>Lo que un módulo clínico necesita saber del cobro. Nada más.</summary>
/// <param name="Verdict"><c>Authorized</c> / <c>PendingPayment</c> / <c>Waived</c>.</param>
/// <param name="Message">Texto listo para mostrar.</param>
/// <param name="AmountDue">Cuánto falta, si falta algo.</param>
public sealed record ServiceGateStatus(string Verdict, string Message, decimal? AmountDue);

/// <summary>
/// Puerta de prepago vista desde un módulo clínico (FAC-7).
///
/// <para><b>Existe para que el LIS no dependa de facturación.</b> El laboratorio pregunta a este
/// puerto y recibe un veredicto con su texto; quién lo calcula, con qué tablas y bajo qué reglas
/// es asunto del módulo de facturación. Sin esta indirección, el LIS quedaría compilando contra
/// cuentas, cargos y facturas — y el día que se quiera vender el LIS sin facturación, no se
/// puede.</para>
///
/// <para><b>Devuelve información, no permisos.</b> Ninguna respuesta obliga al llamador a hacer
/// nada: un sistema que impide recibir la muestra se evade —el técnico anota en papel y el
/// registro se pierde— y eso es peor que no tener el control.</para>
/// </summary>
public interface IServiceGate
{
    /// <summary>Estado de cobro de una orden de laboratorio.</summary>
    /// <param name="serviceRequestId">Id del <c>ServiceRequest</c>.</param>
    /// <param name="priority">Prioridad FHIR. <c>stat</c> y <c>urgent</c> nunca se detienen.</param>
    Task<ServiceGateStatus> EvaluateAsync(
        Guid organizationId, string serviceRequestId, string? priority, CancellationToken ct = default);
}
