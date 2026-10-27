using IMed.Api.Application.Abstractions;
using Microsoft.Extensions.Options;

namespace IMed.Api.Infrastructure.Time;

/// <summary>Zona horaria de la clínica. Sección <c>Clinic</c>.</summary>
public sealed class ClinicClockOptions
{
    public const string SectionName = "Clinic";

    /// <summary>
    /// Identificador IANA. El default es el del piloto; una instalación en otro país lo cambia por
    /// configuración y nada más.
    ///
    /// <para>Se usa el nombre IANA y no el de Windows porque .NET los resuelve en ambos sistemas
    /// desde .NET 6, y es el que va a seguir siendo válido cuando esto corra en un contenedor.</para>
    /// </summary>
    public string TimeZone { get; set; } = "America/Tegucigalpa";
}

/// <inheritdoc cref="IClinicClock"/>
public sealed class ClinicClock : IClinicClock
{
    private readonly TimeProvider _timeProvider;
    private readonly TimeZoneInfo _zone;

    public ClinicClock(TimeProvider timeProvider, IOptions<ClinicClockOptions> options)
    {
        _timeProvider = timeProvider;

        // Se resuelve una sola vez y se deja fallar si el identificador no existe. Caer a UTC
        // ante una configuración mala sería reintroducir en silencio el mismo bug que esto vino a
        // arreglar, y un correlativo con la fecha corrida no se nota hasta que alguien audita.
        _zone = TimeZoneInfo.FindSystemTimeZoneById(options.Value.TimeZone);
    }

    public DateOnly Today() => DateOf(_timeProvider.GetUtcNow());

    public DateOnly DateOf(DateTimeOffset instant) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(instant, _zone).DateTime);
}
