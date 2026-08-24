namespace IMed.Api.Application.Abstractions;

/// <summary>
/// El calendario de la clínica: qué día es <b>ahí</b>, no en UTC.
///
/// <para><b>Por qué existe.</b> Los correlativos legibles —número de orden, número de accesión—
/// llevan la fecha adentro y se reinician cada día. Calcularlos con <c>UtcNow</c> significa que en
/// Honduras (UTC-6) el día cambia a las 18:00: una orden pedida el 16 a las 20:00 sale numerada
/// <c>ORD-20260817-0001</c>, con la fecha de mañana y el contador reiniciado en plena tarde. Eso
/// se ve en el papel que el paciente se lleva y en cualquier auditoría que cruce números con
/// fechas.</para>
///
/// <para>No reemplaza a <see cref="TimeProvider"/>: las marcas de tiempo de los registros siguen
/// siendo UTC, que es lo correcto para almacenar. Esto es para cuando la pregunta es "¿qué día es
/// hoy para la gente que trabaja acá?".</para>
/// </summary>
public interface IClinicClock
{
    /// <summary>El día en curso en la zona de la clínica.</summary>
    DateOnly Today();
}
