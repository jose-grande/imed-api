using FluentAssertions;
using IMed.Api.Infrastructure.Time;
using Microsoft.Extensions.Options;
using Xunit;

namespace IMed.Api.IntegrationTests.Time;

/// <summary>
/// El calendario de la clínica.
///
/// <para>Los correlativos legibles —número de orden, número de accesión— llevan la fecha adentro y
/// reinician su contador cada día. Calculados con UTC, en Honduras el día cambia a las 18:00: una
/// orden de las 20:00 sale con la fecha de mañana y el contador arrancado de nuevo en plena
/// tarde.</para>
/// </summary>
public sealed class ClinicClockTests
{
    private sealed class RelojFijo(DateTimeOffset instante) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => instante;
    }

    private static ClinicClock Reloj(DateTimeOffset utc, string zona = "America/Tegucigalpa") =>
        new(new RelojFijo(utc), Options.Create(new ClinicClockOptions { TimeZone = zona }));

    [Fact]
    public void AlAnochecerEnHonduras_elDiaTodaviaEsElDeHoy()
    {
        // 2026-08-17T02:00Z son las 20:00 del 16 en Tegucigalpa. Este es el caso que se vio en
        // producción: órdenes de la tarde numeradas `ORD-20260817-…`.
        var hoy = Reloj(new DateTimeOffset(2026, 8, 17, 2, 0, 0, TimeSpan.Zero)).Today();

        hoy.Should().Be(new DateOnly(2026, 8, 16));
    }

    [Fact]
    public void ElDiaCambiaAMedianocheLocal_noALas18()
    {
        // 06:00Z = medianoche local. Un minuto antes todavía es el día anterior.
        Reloj(new DateTimeOffset(2026, 8, 17, 5, 59, 0, TimeSpan.Zero)).Today()
            .Should().Be(new DateOnly(2026, 8, 16));

        Reloj(new DateTimeOffset(2026, 8, 17, 6, 0, 0, TimeSpan.Zero)).Today()
            .Should().Be(new DateOnly(2026, 8, 17));
    }

    [Fact]
    public void DuranteElDia_coincideConUtc()
    {
        // A media mañana las dos fechas son la misma: el bug solo se manifiesta de tarde, que es
        // por lo que sobrevivió tanto tiempo.
        Reloj(new DateTimeOffset(2026, 8, 16, 15, 0, 0, TimeSpan.Zero)).Today()
            .Should().Be(new DateOnly(2026, 8, 16));
    }

    [Fact]
    public void UnaZonaInexistente_falla_enVezDeCaerASilencioUtc()
    {
        // Caer a UTC ante una configuración mala reintroduce el bug sin que nada avise, y un
        // correlativo con la fecha corrida no se nota hasta que alguien audita.
        var acto = () => Reloj(DateTimeOffset.UtcNow, "Marte/Olympus");

        acto.Should().Throw<TimeZoneNotFoundException>();
    }
}
