using FluentAssertions;
using IMed.Api.Application.Abstractions;
using IMed.Api.Domain.Billing.Fiscal;
using IMed.Api.Domain.Locations;
using IMed.Api.Infrastructure.Persistence;
using IMed.Api.IntegrationTests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace IMed.Api.IntegrationTests.Billing;

/// <summary>
/// Asignación de correlativo fiscal (FAC-2). <b>Es la prueba de la épica.</b>
///
/// <para>Corre contra Postgres de verdad y no contra un doble, porque lo que se está probando es
/// precisamente la atomicidad que aporta la base: un mock devolvería números distintos con
/// cualquier implementación, incluida la que lee, decide en memoria y guarda — que es justamente
/// la que produce dos facturas con el mismo número cuando hay dos cajas cobrando a la vez.</para>
/// </summary>
[Collection(ImedApiCollection.Name)]
public sealed class FiscalNumberingTests(ImedApiFixture fixture) : IAsyncLifetime
{
    public Task InitializeAsync() => fixture.ResetStateAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private static readonly DateOnly Hoy = new(2026, 8, 18);

    /// <summary>
    /// Siembra una autorización lista para emitir y devuelve su id. Cada prueba usa su propia
    /// organización para que puedan correr sin pisarse.
    /// </summary>
    private async Task<(Guid OrgId, Guid AuthId)> SembrarAsync(
        long rangeFrom = 1,
        long rangeTo = 100,
        DateOnly? limitDate = null,
        FiscalAuthorizationStatus? estado = null)
    {
        var orgId = Guid.NewGuid();
        using var scope = fixture.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var sede = TestSedes.Create(db, orgId, "CENTRAL", "Sede central", isDefault: true);
        sede.SetFiscalEstablishmentCode("001");
        db.OrganizationLocations.Add(sede);

        var tipo = FiscalDocumentType.Create(orgId, "factura", "Factura", "01", affectsReceivable: true, sign: 1);
        db.FiscalDocumentTypes.Add(tipo);

        var auth = FiscalAuthorization.Create(
            orgId, sede.Id, tipo.Id,
            cai: "A1B2C3-D4E5F6-A7B8C9-D0E1F2-A3B4C5-D6",
            emissionPointCode: "002",
            rangeFrom: rangeFrom,
            rangeTo: rangeTo,
            limitDate: limitDate ?? new DateOnly(2026, 12, 31));

        if (estado == FiscalAuthorizationStatus.Revoked)
        {
            auth.Revoke();
        }

        db.FiscalAuthorizations.Add(auth);
        await db.SaveChangesAsync();
        return (orgId, auth.Id);
    }

    private IFiscalRepository NuevoRepo(out IServiceScope scope)
    {
        scope = fixture.Factory.Services.CreateScope();
        return scope.ServiceProvider.GetRequiredService<IFiscalRepository>();
    }

    [Fact]
    public async Task NSolicitudesSimultaneas_producenNCorrelativosDistintosYSinHuecos()
    {
        // 🔴 La prueba de la épica. Dos cajas cobrando a la vez es el caso normal, no el borde, y
        // un correlativo duplicado es incumplimiento fiscal — no un bug cosmético.
        //
        // Cada tarea abre su propio scope: compartir el DbContext las serializaría en el cliente y
        // la prueba pasaría incluso con una implementación que lee-decide-guarda, que es exactamente
        // la que hay que descartar.
        const int Cajas = 40;
        var (orgId, authId) = await SembrarAsync(rangeFrom: 1, rangeTo: Cajas);

        var arranque = new TaskCompletionSource();
        var tareas = Enumerable.Range(0, Cajas).Select(async _ =>
        {
            using var scope = fixture.Factory.Services.CreateScope();
            var repo = scope.ServiceProvider.GetRequiredService<IFiscalRepository>();
            await arranque.Task; // largar todas juntas maximiza el solapamiento
            return await repo.ReserveNextSequenceAsync(orgId, authId, Hoy);
        }).ToList();

        arranque.SetResult();
        var resultados = await Task.WhenAll(tareas);

        var numeros = resultados.Where(r => r.Reserved).Select(r => r.Sequence!.Value).ToList();

        numeros.Should().HaveCount(Cajas, "el rango alcanzaba para todas");
        numeros.Should().OnlyHaveUniqueItems("dos documentos con el mismo número es incumplimiento fiscal");
        numeros.Order().Should().BeEquivalentTo(
            Enumerable.Range(1, Cajas).Select(i => (long)i),
            "los correlativos son contiguos: sin repetidos y sin huecos");
    }

    [Fact]
    public async Task AgotarElRango_bloqueaEnVezDePasarseDelTecho()
    {
        // Invariante 1. Con más solicitudes que números, las que sobran tienen que fallar — no
        // entregar range_to + 1, que sería un número que la autoridad no autorizó.
        var (orgId, authId) = await SembrarAsync(rangeFrom: 1, rangeTo: 3);

        var repo = NuevoRepo(out var scope);
        using (scope)
        {
            var resultados = new List<FiscalReservation>();
            for (var i = 0; i < 5; i++)
            {
                resultados.Add(await repo.ReserveNextSequenceAsync(orgId, authId, Hoy));
            }

            resultados.Take(3).Should().OnlyContain(r => r.Reserved);
            resultados.Take(3).Select(r => r.Sequence!.Value).Should().BeEquivalentTo([1L, 2L, 3L]);

            resultados.Skip(3).Should().OnlyContain(
                r => !r.Reserved && r.Failure == FiscalReservationFailure.Exhausted);
        }
    }

    [Fact]
    public async Task AlConsumirElUltimo_laAutorizacionQuedaMarcadaComoAgotada()
    {
        // Sin esto la fila seguiría diciendo "activa" hasta que alguien intentara emitir, y la
        // pantalla que avisa "quedan N documentos" mentiría justo cuando importa: el sábado con
        // la clínica llena.
        var (orgId, authId) = await SembrarAsync(rangeFrom: 1, rangeTo: 2);

        var repo = NuevoRepo(out var scope);
        using (scope)
        {
            await repo.ReserveNextSequenceAsync(orgId, authId, Hoy);
            (await repo.GetAuthorizationAsync(orgId, authId))!
                .Status.Should().Be(FiscalAuthorizationStatus.Active, "todavía queda uno");

            await repo.ReserveNextSequenceAsync(orgId, authId, Hoy);
            var agotada = (await repo.GetAuthorizationAsync(orgId, authId))!;

            agotada.Status.Should().Be(FiscalAuthorizationStatus.Exhausted);
            agotada.Remaining.Should().Be(0);
        }
    }

    [Fact]
    public async Task PasadaLaFechaLimite_noSeEmiteAunqueSobrenNumeros()
    {
        // Invariante 2. El rango entero sin usar no sirve de nada si venció la autorización.
        var (orgId, authId) = await SembrarAsync(
            rangeFrom: 1, rangeTo: 1000, limitDate: new DateOnly(2026, 8, 17));

        var repo = NuevoRepo(out var scope);
        using (scope)
        {
            var ayer = await repo.ReserveNextSequenceAsync(orgId, authId, new DateOnly(2026, 8, 17));
            ayer.Reserved.Should().BeTrue("el día límite todavía se puede emitir");

            var hoy = await repo.ReserveNextSequenceAsync(orgId, authId, Hoy);
            hoy.Reserved.Should().BeFalse();
            hoy.Failure.Should().Be(FiscalReservationFailure.Expired);
        }
    }

    [Fact]
    public async Task UnaAutorizacionDadaDeBaja_noEntregaNumeros()
    {
        var (orgId, authId) = await SembrarAsync(estado: FiscalAuthorizationStatus.Revoked);

        var repo = NuevoRepo(out var scope);
        using (scope)
        {
            var r = await repo.ReserveNextSequenceAsync(orgId, authId, Hoy);

            r.Reserved.Should().BeFalse();
            r.Failure.Should().Be(FiscalReservationFailure.Revoked);
        }
    }

    [Fact]
    public async Task UnCorrelativoEntregado_noSeReutilizaAunqueLaVentaSeCaiga()
    {
        // Invariante 4, y la que sorprende a quien no facturó nunca: si la impresión falla, el
        // documento ya existe y se reimprime — no se re-emite. El contador solo sube, así que el
        // hueco queda. Los huecos son legítimos y explicables; un número repetido no.
        var (orgId, authId) = await SembrarAsync(rangeFrom: 1, rangeTo: 10);

        var repo = NuevoRepo(out var scope);
        using (scope)
        {
            var primero = await repo.ReserveNextSequenceAsync(orgId, authId, Hoy);
            // Acá "se cae la venta": nadie devuelve el número.
            var siguiente = await repo.ReserveNextSequenceAsync(orgId, authId, Hoy);

            primero.Sequence.Should().Be(1);
            siguiente.Sequence.Should().Be(2, "el 1 se consumió aunque no se haya usado");
        }
    }

    [Fact]
    public async Task LaAutorizacionDeOtraOrganizacion_noSeAlcanza()
    {
        // El UPDATE lleva organization_id adentro: sin eso, conocer un id bastaría para consumir
        // los correlativos de otra clínica.
        var (_, authId) = await SembrarAsync();
        var otraOrg = Guid.NewGuid();

        var repo = NuevoRepo(out var scope);
        using (scope)
        {
            var r = await repo.ReserveNextSequenceAsync(otraOrg, authId, Hoy);

            r.Reserved.Should().BeFalse();
            r.Failure.Should().Be(FiscalReservationFailure.NotFound);
        }
    }

    [Fact]
    public async Task ElRangoQueNoArrancaEnUno_seRespeta()
    {
        // El SAR autoriza rangos que continúan de una autorización anterior; arrancar en 1 sería
        // reusar números ya emitidos.
        var (orgId, authId) = await SembrarAsync(rangeFrom: 4521, rangeTo: 4523);

        var repo = NuevoRepo(out var scope);
        using (scope)
        {
            (await repo.ReserveNextSequenceAsync(orgId, authId, Hoy)).Sequence.Should().Be(4521);
            (await repo.ReserveNextSequenceAsync(orgId, authId, Hoy)).Sequence.Should().Be(4522);
            (await repo.ReserveNextSequenceAsync(orgId, authId, Hoy)).Sequence.Should().Be(4523);
            (await repo.ReserveNextSequenceAsync(orgId, authId, Hoy)).Failure
                .Should().Be(FiscalReservationFailure.Exhausted);
        }
    }
}
