using IMed.Api.Application.Abstractions;
using IMed.Api.Infrastructure.Time;
using Microsoft.Extensions.Options;

namespace IMed.Api.IntegrationTests.Infrastructure;

/// <summary>Relojes de la clínica para pruebas de renderers.</summary>
public static class TestClinicClocks
{
    /// <summary>El reloj real con la zona del piloto.</summary>
    public static IClinicClock Honduras { get; } = new ClinicClock(
        TimeProvider.System, Options.Create(new ClinicClockOptions { TimeZone = "America/Tegucigalpa" }));
}

/// <summary>
/// Reloj que anota qué instantes le pidieron pasar a la hora de la clínica. Sirve para probar que
/// un renderer no imprime ninguna fecha sin pasarla por el reloj: el texto del PDF va codificado y
/// no se puede buscar "14:30" en los bytes.
/// </summary>
public sealed class RecordingClinicClock : IClinicClock
{
    private readonly IClinicClock _inner = TestClinicClocks.Honduras;

    public List<DateTimeOffset> Converted { get; } = [];

    public DateOnly Today() => _inner.Today();

    public DateOnly DateOf(DateTimeOffset instant) => _inner.DateOf(instant);

    public DateTimeOffset StartOf(DateOnly day) => _inner.StartOf(day);

    public DateTimeOffset ToClinicTime(DateTimeOffset instant)
    {
        Converted.Add(instant);
        return _inner.ToClinicTime(instant);
    }
}
