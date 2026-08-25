using FluentAssertions;
using IMed.Api.Application.Abstractions;
using IMed.Api.Application.Billing;
using IMed.Api.Domain.Billing.Accounts;
using IMed.Api.Domain.Locations;
using IMed.Api.Infrastructure.Persistence;
using IMed.Api.IntegrationTests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace IMed.Api.IntegrationTests.Billing;

/// <summary>
/// Endurecimiento de la ola U1 (FAC-8).
///
/// <para>Es la diferencia entre "lo vimos funcionar" y "lo pueden usar el lunes". Cada prueba de
/// acá corresponde a un escenario que el plan nombra como riesgo, no a una función nueva.</para>
/// </summary>
[Collection(ImedApiCollection.Name)]
public sealed class BillingHardeningTests(ImedApiFixture fixture) : IAsyncLifetime
{
    public Task InitializeAsync() => fixture.ResetStateAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private sealed record Escenario(
        Guid OrgId, Guid AccountId, Guid LocationId, Guid FacturaTypeId, Guid AuthId, Guid ItemId);

    private async Task<Escenario> ArrangeAsync(long rangeTo = 500)
    {
        var orgId = Guid.NewGuid();
        using var scope = fixture.Factory.Services.CreateScope();
        var sp = scope.ServiceProvider;
        var db = sp.GetRequiredService<AppDbContext>();

        var sede = OrganizationLocation.Create(orgId, "CENTRAL", "Sede central", isDefault: true);
        sede.SetFiscalEstablishmentCode("001");
        db.OrganizationLocations.Add(sede);
        await db.SaveChangesAsync();

        var setup = await sp.GetRequiredService<ProvisionFiscalRegimeHandler>()
            .HandleAsync(orgId, "HN_SAR_CAI", "08019999123456", "Clínica de Prueba");
        var exento = setup.TaxCodes.First(t => t.Code == "EXENTO").Id;
        var factura = setup.DocumentTypes.First(t => t.Code == "factura").Id;

        var auth = await sp.GetRequiredService<RegisterFiscalAuthorizationHandler>().HandleAsync(
            new RegisterFiscalAuthorizationCommand(
                orgId, sede.Id, factura, "CAI-QA", "002", 1, rangeTo, new DateOnly(2027, 12, 31)));

        var item = await sp.GetRequiredService<CreateBillableItemHandler>().HandleAsync(
            new CreateBillableItemCommand(
                orgId, "CONS", "Consulta general", "Service", exento, 500m,
                "Prepaid", "Global", null, null, null));

        var cuenta = await sp.GetRequiredService<OpenAccountHandler>().HandleAsync(
            new OpenAccountCommand(orgId, null, "pat-h-1", "Juan", $"enc-{Guid.NewGuid():N}", "Episode"));

        return new Escenario(orgId, cuenta.Id, sede.Id, factura, auth.Id, item.Id);
    }

    [Fact]
    public async Task ElCorrelativo_aguantaCargaDeVerdad()
    {
        // El plan pide concurrencia **con carga**, no con dos hilos: un duplicado que aparece una
        // vez cada doscientas no se ve con dos.
        //
        // Volumen alto (200 reservas) con concurrencia acotada (32 a la vez). Soltar las 200
        // juntas agota el pool de conexiones de Postgres y la prueba falla por timeout sin decir
        // nada sobre el correlativo — que es lo que se quiere medir. 32 simultáneas ya serializan
        // de verdad sobre la misma fila.
        const int Intentos = 200;
        const int Simultaneas = 32;

        var e = await ArrangeAsync(rangeTo: Intentos);
        var hoy = new DateOnly(2026, 8, 25);

        using var puerta = new SemaphoreSlim(Simultaneas);
        var arranque = new TaskCompletionSource();

        var tareas = Enumerable.Range(0, Intentos).Select(async _ =>
        {
            await arranque.Task;
            await puerta.WaitAsync();
            try
            {
                using var scope = fixture.Factory.Services.CreateScope();
                var repo = scope.ServiceProvider.GetRequiredService<IFiscalRepository>();
                return await repo.ReserveNextSequenceAsync(e.OrgId, e.AuthId, hoy);
            }
            finally
            {
                puerta.Release();
            }
        }).ToList();

        arranque.SetResult();
        var resultados = await Task.WhenAll(tareas);

        var numeros = resultados.Where(r => r.Reserved).Select(r => r.Sequence!.Value).ToList();

        numeros.Should().HaveCount(Intentos);
        numeros.Should().OnlyHaveUniqueItems("200 reservas bajo carga, 200 números distintos");
        numeros.Order().Should().BeEquivalentTo(
            Enumerable.Range(1, Intentos).Select(i => (long)i),
            "contiguos y sin huecos");
    }

    [Fact]
    public async Task ElCaiSeAgotaAMitadDelDia_yElMensajeDiceQueHacer()
    {
        // 🔴 El escenario que más duele y el que nadie prueba: la clínica está operando, el CAI se
        // termina, y hay pacientes esperando. Lo que no puede pasar es que el sistema devuelva
        // "error" sin decir qué hacer.
        var e = await ArrangeAsync(rangeTo: 2);
        using var scope = fixture.Factory.Services.CreateScope();
        var sp = scope.ServiceProvider;
        var capturar = sp.GetRequiredService<CaptureChargeHandler>();
        var emitir = sp.GetRequiredService<IssueInvoiceHandler>();

        // Dos ventas normales.
        for (var i = 1; i <= 2; i++)
        {
            await capturar.HandleAsync(new CaptureChargeCommand(
                e.OrgId, e.AccountId, e.ItemId, ChargeSourceKind.LabOrder, $"sr-{i}"));
            var f = await emitir.HandleAsync(new IssueInvoiceCommand(
                e.OrgId, e.AccountId, e.LocationId, e.FacturaTypeId));
            f.Succeeded.Should().BeTrue($"la venta {i} entra en el rango");
        }

        // La tercera: se acabó.
        await capturar.HandleAsync(new CaptureChargeCommand(
            e.OrgId, e.AccountId, e.ItemId, ChargeSourceKind.LabOrder, "sr-3"));
        var tercera = await emitir.HandleAsync(new IssueInvoiceCommand(
            e.OrgId, e.AccountId, e.LocationId, e.FacturaTypeId));

        tercera.Succeeded.Should().BeFalse();
        tercera.Rejected!.Code.Should().Be("autorizacion_bloqueada");
        tercera.Rejected.Message.Should().Contain("rango nuevo",
            "el mensaje tiene que mandar al usuario a la solución, no decir 'error'");

        // Y el cargo sigue vivo: la venta no se perdió, quedó esperando el CAI nuevo.
        var estado = await sp.GetRequiredService<GetAccountStatementHandler>()
            .HandleAsync(e.OrgId, e.AccountId);
        estado!.Charges.Should().Contain(c => c.SourceId == "sr-3" && c.Status == "Captured");

        // Registrar un rango nuevo destraba la caja sin reiniciar nada.
        await sp.GetRequiredService<RegisterFiscalAuthorizationHandler>().HandleAsync(
            new RegisterFiscalAuthorizationCommand(
                e.OrgId, e.LocationId, e.FacturaTypeId, "CAI-NUEVO", "002",
                1001, 2000, new DateOnly(2027, 12, 31)));

        var reintento = await emitir.HandleAsync(new IssueInvoiceCommand(
            e.OrgId, e.AccountId, e.LocationId, e.FacturaTypeId));

        reintento.Succeeded.Should().BeTrue(reintento.Rejected?.Message);
        reintento.Invoice!.FiscalDocument!.Sequence.Should().Be(1001);
        reintento.Invoice.FiscalDocument.CaiSnapshot.Should().Be("CAI-NUEVO");
    }

    [Theory]
    [InlineData("Encounter")]
    [InlineData("LabOrder")]
    [InlineData("LabResult")]
    [InlineData("PharmacyDispense")]
    public async Task LaIdempotencia_valeParaCadaOrigen(string origen)
    {
        // El plan pide probarla desde cada origen conectado. Conectar uno nuevo sin pasarle la
        // clave correcta es el error probable, y no se ve hasta que alguien paga dos veces.
        var e = await ArrangeAsync();
        using var scope = fixture.Factory.Services.CreateScope();
        var capturar = scope.ServiceProvider.GetRequiredService<CaptureChargeHandler>();
        var kind = Enum.Parse<ChargeSourceKind>(origen);

        var cmd = new CaptureChargeCommand(
            e.OrgId, e.AccountId, e.ItemId, kind, "acto-1");

        var primera = await capturar.HandleAsync(cmd);
        var segunda = await capturar.HandleAsync(cmd);

        primera.AlreadyCharged.Should().BeFalse();
        segunda.AlreadyCharged.Should().BeTrue($"el origen '{origen}' también es idempotente");
        segunda.Charge!.Id.Should().Be(primera.Charge!.Id);
    }

    [Fact]
    public async Task ReimprimirDosVeces_daElMismoDocumento()
    {
        var e = await ArrangeAsync();
        using var scope = fixture.Factory.Services.CreateScope();
        var sp = scope.ServiceProvider;

        await sp.GetRequiredService<CaptureChargeHandler>().HandleAsync(
            new CaptureChargeCommand(
                e.OrgId, e.AccountId, e.ItemId, ChargeSourceKind.LabOrder, "sr-1"));
        var f = await sp.GetRequiredService<IssueInvoiceHandler>().HandleAsync(
            new IssueInvoiceCommand(e.OrgId, e.AccountId, e.LocationId, e.FacturaTypeId));

        var pdf = sp.GetRequiredService<GetInvoicePdfHandler>();
        var a = await pdf.HandleAsync(e.OrgId, f.Invoice!.Id);
        var b = await pdf.HandleAsync(e.OrgId, f.Invoice.Id);

        a!.FileName.Should().Be(b!.FileName);
        a.Bytes.Length.Should().Be(b.Bytes.Length,
            "el mismo documento congelado produce el mismo papel");
    }

    [Fact]
    public async Task UnBorrador_noSeImprime()
    {
        // Imprimir algo sin número fiscal produce un papel que parece una factura y no lo es.
        var e = await ArrangeAsync();
        using var scope = fixture.Factory.Services.CreateScope();

        var pdf = await scope.ServiceProvider.GetRequiredService<GetInvoicePdfHandler>()
            .HandleAsync(e.OrgId, Guid.NewGuid());

        pdf.Should().BeNull();
    }

    [Fact]
    public async Task NingunaLecturaDelModulo_cruzaOrganizaciones()
    {
        // Barrido explícito: cada consulta del módulo, con el id correcto pero la organización
        // equivocada. Es el error que no se ve en pruebas de una sola org.
        var e = await ArrangeAsync();
        using var scope = fixture.Factory.Services.CreateScope();
        var sp = scope.ServiceProvider;

        await sp.GetRequiredService<CaptureChargeHandler>().HandleAsync(
            new CaptureChargeCommand(
                e.OrgId, e.AccountId, e.ItemId, ChargeSourceKind.LabOrder, "sr-1"));
        var f = await sp.GetRequiredService<IssueInvoiceHandler>().HandleAsync(
            new IssueInvoiceCommand(e.OrgId, e.AccountId, e.LocationId, e.FacturaTypeId));

        var otra = Guid.NewGuid();

        (await sp.GetRequiredService<GetAccountStatementHandler>()
            .HandleAsync(otra, e.AccountId)).Should().BeNull("cuenta");
        (await sp.GetRequiredService<GetInvoiceHandler>()
            .HandleAsync(otra, f.Invoice!.Id)).Should().BeNull("factura");
        (await sp.GetRequiredService<GetInvoicePdfHandler>()
            .HandleAsync(otra, f.Invoice.Id)).Should().BeNull("PDF");
        (await sp.GetRequiredService<GetDocumentLedgerHandler>()
            .HandleAsync(otra, e.AuthId)).Should().BeNull("libro fiscal");
        (await sp.GetRequiredService<ListAccountInvoicesHandler>()
            .HandleAsync(otra, e.AccountId)).Should().BeEmpty("facturas de la cuenta");
        (await sp.GetRequiredService<SearchBillableItemsHandler>()
            .HandleAsync(otra, null, true, 50)).Should().BeEmpty("catálogo");
        (await sp.GetRequiredService<ListFiscalAuthorizationsHandler>()
            .HandleAsync(otra)).Should().BeEmpty("autorizaciones");
        (await sp.GetRequiredService<ListCashPointsHandler>()
            .HandleAsync(otra)).Should().BeEmpty("cajas");
        (await sp.GetRequiredService<SearchAccountsHandler>()
            .HandleAsync(otra, null, null, 50)).Should().BeEmpty("cuentas");
    }

    [Fact]
    public async Task ElCorrelativoDeOtraOrganizacion_noSeConsume()
    {
        // Conocer un id no puede alcanzar para gastarle los números a otra clínica.
        var e = await ArrangeAsync();
        using var scope = fixture.Factory.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IFiscalRepository>();

        var r = await repo.ReserveNextSequenceAsync(
            Guid.NewGuid(), e.AuthId, new DateOnly(2026, 8, 25));

        r.Reserved.Should().BeFalse();

        // Y el contador de la organización real no se movió.
        var auth = await repo.GetAuthorizationAsync(e.OrgId, e.AuthId);
        auth!.NextSequence.Should().Be(1);
    }
}
