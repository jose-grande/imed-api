using FluentAssertions;
using IMed.Api.Application.Abstractions;
using IMed.Api.Application.Billing;
using IMed.Api.Domain.Billing;
using IMed.Api.Domain.Billing.Accounts;
using IMed.Api.Domain.Billing.Catalog;
using IMed.Api.IntegrationTests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace IMed.Api.IntegrationTests.Billing;

/// <summary>
/// Captura de cargos (FAC-4). Corre contra Postgres porque la invariante que define la épica —el
/// índice único de idempotencia— la garantiza la base, no el código: un doble que devuelva
/// "ya existe" pasaría con cualquier implementación, incluida la que sí cobra dos veces.
/// </summary>
[Collection(ImedApiCollection.Name)]
public sealed class ChargeCaptureTests(ImedApiFixture fixture) : IAsyncLifetime
{
    public Task InitializeAsync() => fixture.ResetStateAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private sealed record Escenario(Guid OrgId, Guid AccountId, Guid ItemId, Guid ExentoId);

    /// <summary>Organización con régimen, un cobrable y una cuenta abierta.</summary>
    private async Task<Escenario> ArrangeAsync(decimal precioBase = 500m)
    {
        var orgId = Guid.NewGuid();
        using var scope = fixture.Factory.Services.CreateScope();
        var sp = scope.ServiceProvider;

        var setup = await sp.GetRequiredService<ProvisionFiscalRegimeHandler>()
            .HandleAsync(orgId, "HN_SAR_CAI");
        var exento = setup.TaxCodes.First(t => t.Code == "EXENTO").Id;

        var item = await sp.GetRequiredService<CreateBillableItemHandler>().HandleAsync(
            new CreateBillableItemCommand(
                orgId, "CONS_GEN", "Consulta general", "Service", exento, precioBase,
                "Prepaid", "Global", null, null, null));

        var cuenta = await sp.GetRequiredService<OpenAccountHandler>().HandleAsync(
            new OpenAccountCommand(orgId, null, "pat-1", "Juan Pérez", $"enc-{Guid.NewGuid():N}", "Episode"));

        return new Escenario(orgId, cuenta.Id, item.Id, exento);
    }

    private CaptureChargeHandler NuevoHandler(out IServiceScope scope)
    {
        scope = fixture.Factory.Services.CreateScope();
        return scope.ServiceProvider.GetRequiredService<CaptureChargeHandler>();
    }

    [Fact]
    public async Task AdmitirDosVecesLaMismaOrden_generaUnSoloCargo()
    {
        // 🔴 Criterio de aceptación 1 y la razón de ser de la épica. Sin esto, cualquier
        // reproceso —un job que reintenta, un doble clic— le cobra dos veces al paciente.
        var e = await ArrangeAsync();
        var handler = NuevoHandler(out var scope);
        using (scope)
        {
            var cmd = new CaptureChargeCommand(
                e.OrgId, e.AccountId, e.ItemId, ChargeSourceKind.LabOrder, "sr-2405");

            var primera = await handler.HandleAsync(cmd);
            var segunda = await handler.HandleAsync(cmd);

            primera.AlreadyCharged.Should().BeFalse();
            segunda.AlreadyCharged.Should().BeTrue("el acto ya estaba cobrado");
            segunda.Charge!.Id.Should().Be(primera.Charge!.Id, "es el mismo cargo, no uno nuevo");
        }
    }

    [Fact]
    public async Task NAdmisionesSimultaneas_deLaMismaOrden_dejanUnSoloCargo()
    {
        // La comprobación previa no alcanza: dos hilos pueden pasarla a la vez. Lo que decide es
        // el índice único, y esto lo verifica con concurrencia real.
        const int Intentos = 20;
        var e = await ArrangeAsync();

        var arranque = new TaskCompletionSource();
        var tareas = Enumerable.Range(0, Intentos).Select(async _ =>
        {
            using var scope = fixture.Factory.Services.CreateScope();
            var handler = scope.ServiceProvider.GetRequiredService<CaptureChargeHandler>();
            await arranque.Task;
            return await handler.HandleAsync(new CaptureChargeCommand(
                e.OrgId, e.AccountId, e.ItemId, ChargeSourceKind.LabOrder, "sr-9001"));
        }).ToList();

        arranque.SetResult();
        var resultados = await Task.WhenAll(tareas);

        resultados.Count(r => r.Succeeded && !r.AlreadyCharged)
            .Should().Be(1, "solo una captura crea el cargo");
        resultados.Where(r => r.Succeeded).Select(r => r.Charge!.Id).Distinct()
            .Should().HaveCount(1, "todas resuelven al mismo cargo");
    }

    [Fact]
    public async Task LiberarUnResultadoYaLiberado_noDuplicaElCargo()
    {
        // Criterio de aceptación 2. Es el mismo mecanismo con otro origen; se prueba aparte
        // porque conectar un origen nuevo sin pasarle la clave correcta es el error probable.
        var e = await ArrangeAsync();
        var handler = NuevoHandler(out var scope);
        using (scope)
        {
            var cmd = new CaptureChargeCommand(
                e.OrgId, e.AccountId, e.ItemId, ChargeSourceKind.LabResult, "rep-77");

            await handler.HandleAsync(cmd);
            var repetida = await handler.HandleAsync(cmd);

            repetida.AlreadyCharged.Should().BeTrue();
        }
    }

    [Fact]
    public async Task DosOrigenesDistintos_delMismoItem_sonDosCargos()
    {
        // La idempotencia es por acto, no por ítem: dos consultas en el mismo día son dos cargos.
        var e = await ArrangeAsync();
        var handler = NuevoHandler(out var scope);
        using (scope)
        {
            var a = await handler.HandleAsync(new CaptureChargeCommand(
                e.OrgId, e.AccountId, e.ItemId, ChargeSourceKind.LabOrder, "sr-1"));
            var b = await handler.HandleAsync(new CaptureChargeCommand(
                e.OrgId, e.AccountId, e.ItemId, ChargeSourceKind.LabOrder, "sr-2"));

            a.Charge!.Id.Should().NotBe(b.Charge!.Id);
            b.AlreadyCharged.Should().BeFalse();
        }
    }

    [Fact]
    public async Task SubirElPrecioEnElTarifario_noCambiaUnCargoYaCapturado()
    {
        // Criterio de aceptación 3. La factura de ayer tiene que poder reimprimirse igual dentro
        // de cinco años, aunque el ítem haya cambiado de precio o ya no exista.
        var e = await ArrangeAsync(precioBase: 500m);

        using var scope = fixture.Factory.Services.CreateScope();
        var sp = scope.ServiceProvider;
        var capture = sp.GetRequiredService<CaptureChargeHandler>();

        var cargo = await capture.HandleAsync(new CaptureChargeCommand(
            e.OrgId, e.AccountId, e.ItemId, ChargeSourceKind.Encounter, "enc-viejo"));
        cargo.Charge!.UnitPrice.Should().Be(500m);

        // Sube el precio: nace un tarifario que cobra el doble.
        var lista = await sp.GetRequiredService<CreatePriceListHandler>().HandleAsync(
            new CreatePriceListCommand(
                e.OrgId, "PUB", "Lista pública", "Public", new DateOnly(2026, 1, 1), null, 0, "HNL"));
        await sp.GetRequiredService<SetPriceHandler>().HandleAsync(new SetPriceCommand(
            e.OrgId, lista.Id, e.ItemId, 1000m, new DateOnly(2026, 1, 1), null, null, null));

        var estado = await sp.GetRequiredService<GetAccountStatementHandler>()
            .HandleAsync(e.OrgId, e.AccountId);

        estado!.Charges.Single().UnitPrice.Should().Be(500m,
            "el cargo congeló su precio: el tarifario nuevo rige de acá en adelante");

        // Y un cargo nuevo sí toma el precio nuevo.
        var nuevo = await capture.HandleAsync(new CaptureChargeCommand(
            e.OrgId, e.AccountId, e.ItemId, ChargeSourceKind.Encounter, "enc-nuevo"));
        nuevo.Charge!.UnitPrice.Should().Be(1000m);
    }

    [Fact]
    public async Task UnDescuentoSobreElTope_noSeAplicaSinAutorizacion()
    {
        // Criterio de aceptación 5.
        var e = await ArrangeAsync(precioBase: 1000m);
        var userId = Guid.NewGuid();

        using var scope = fixture.Factory.Services.CreateScope();
        var sp = scope.ServiceProvider;
        await sp.GetRequiredService<IAuthorizationLimitRepository>().AddAsync(
            UserAuthorizationLimit.Create(
                e.OrgId, userId, AuthorizationLimitKind.DiscountAmount, 100m,
                new DateOnly(2026, 1, 1)));

        var capture = sp.GetRequiredService<CaptureChargeHandler>();

        var sobreTope = await capture.HandleAsync(new CaptureChargeCommand(
            e.OrgId, e.AccountId, e.ItemId, ChargeSourceKind.Manual, "m-1",
            DiscountAmount: 300m, RequestedByUserId: userId, Reason: "Cortesía"));

        sobreTope.Succeeded.Should().BeFalse();
        sobreTope.Rejected.Should().Contain("autorización",
            "superar el tope abre el flujo de autorización, no rechaza sin salida");

        // Con supervisor que lo autoriza, pasa — y queda registrado quién fue.
        var supervisor = Guid.NewGuid();
        var autorizado = await capture.HandleAsync(new CaptureChargeCommand(
            e.OrgId, e.AccountId, e.ItemId, ChargeSourceKind.Manual, "m-2",
            DiscountAmount: 300m, RequestedByUserId: userId,
            DiscountAuthorizedBy: supervisor, Reason: "Cortesía autorizada"));

        autorizado.Succeeded.Should().BeTrue();
        autorizado.Charge!.DiscountAuthorizedBy.Should().Be(supervisor);
    }

    [Fact]
    public async Task UnDescuentoDentroDelTope_pasaSinAutorizacion()
    {
        var e = await ArrangeAsync(precioBase: 1000m);
        var userId = Guid.NewGuid();

        using var scope = fixture.Factory.Services.CreateScope();
        var sp = scope.ServiceProvider;
        await sp.GetRequiredService<IAuthorizationLimitRepository>().AddAsync(
            UserAuthorizationLimit.Create(
                e.OrgId, userId, AuthorizationLimitKind.DiscountAmount, 100m, new DateOnly(2026, 1, 1)));

        var r = await sp.GetRequiredService<CaptureChargeHandler>().HandleAsync(
            new CaptureChargeCommand(
                e.OrgId, e.AccountId, e.ItemId, ChargeSourceKind.Manual, "m-3",
                DiscountAmount: 80m, RequestedByUserId: userId, Reason: "Cortesía"));

        r.Succeeded.Should().BeTrue();
        r.Charge!.DiscountAuthorizedBy.Should().BeNull(
            "dentro del tope no hace falta autorización, y null no significa 'nadie lo autorizó'");
    }

    [Fact]
    public async Task UnDescuentoQuePasaElPisoDelTarifario_seRechazaAunqueEsteDentroDelTope()
    {
        // El piso protege el margen y el tope protege la caja: son cosas distintas. El piso no es
        // negociable por nivel jerárquico, así que ni autorizado pasa.
        var e = await ArrangeAsync(precioBase: 1000m);

        using var scope = fixture.Factory.Services.CreateScope();
        var sp = scope.ServiceProvider;
        var lista = await sp.GetRequiredService<CreatePriceListHandler>().HandleAsync(
            new CreatePriceListCommand(
                e.OrgId, "PUB", "Lista pública", "Public", new DateOnly(2026, 1, 1), null, 0, "HNL"));
        await sp.GetRequiredService<SetPriceHandler>().HandleAsync(new SetPriceCommand(
            e.OrgId, lista.Id, e.ItemId, 1000m, new DateOnly(2026, 1, 1), null,
            MinPrice: 900m, DiscountPctMax: null));

        var r = await sp.GetRequiredService<CaptureChargeHandler>().HandleAsync(
            new CaptureChargeCommand(
                e.OrgId, e.AccountId, e.ItemId, ChargeSourceKind.Manual, "m-4",
                DiscountAmount: 200m, DiscountAuthorizedBy: Guid.NewGuid(), Reason: "Cortesía"));

        r.Succeeded.Should().BeFalse();
        r.Rejected.Should().Contain("piso");
    }

    [Fact]
    public async Task ReversarUnCargo_permiteVolverACobrarElMismoActo()
    {
        // El índice excluye los reversados a propósito: si un cargo se reversó por error de
        // captura, el acto tiene que poder volver a cobrarse. Si no, un error de dedo dejaría el
        // servicio sin poder facturarse nunca.
        var e = await ArrangeAsync();

        using var scope = fixture.Factory.Services.CreateScope();
        var sp = scope.ServiceProvider;
        var capture = sp.GetRequiredService<CaptureChargeHandler>();

        var primera = await capture.HandleAsync(new CaptureChargeCommand(
            e.OrgId, e.AccountId, e.ItemId, ChargeSourceKind.LabOrder, "sr-3030"));

        await sp.GetRequiredService<ReverseChargeHandler>()
            .HandleAsync(e.OrgId, primera.Charge!.Id, "Se capturó sobre la cuenta equivocada.");

        var segunda = await capture.HandleAsync(new CaptureChargeCommand(
            e.OrgId, e.AccountId, e.ItemId, ChargeSourceKind.LabOrder, "sr-3030"));

        segunda.Succeeded.Should().BeTrue();
        segunda.AlreadyCharged.Should().BeFalse("el cargo anterior está reversado");
        segunda.Charge!.Id.Should().NotBe(primera.Charge.Id);
    }

    [Fact]
    public async Task UnCargoReversado_noSumaAlTotalPeroSigueVisible()
    {
        // Un cargo que desaparece sin rastro es indistinguible de uno que nunca se capturó.
        var e = await ArrangeAsync(precioBase: 500m);

        using var scope = fixture.Factory.Services.CreateScope();
        var sp = scope.ServiceProvider;
        var capture = sp.GetRequiredService<CaptureChargeHandler>();

        var a = await capture.HandleAsync(new CaptureChargeCommand(
            e.OrgId, e.AccountId, e.ItemId, ChargeSourceKind.LabOrder, "sr-a"));
        await capture.HandleAsync(new CaptureChargeCommand(
            e.OrgId, e.AccountId, e.ItemId, ChargeSourceKind.LabOrder, "sr-b"));

        await sp.GetRequiredService<ReverseChargeHandler>()
            .HandleAsync(e.OrgId, a.Charge!.Id, "Duplicado por error.");

        var estado = await sp.GetRequiredService<GetAccountStatementHandler>()
            .HandleAsync(e.OrgId, e.AccountId);

        estado!.Charges.Should().HaveCount(2, "el reversado sigue en la lista");
        estado.Charges.Should().Contain(c => c.Status == "Reversed");
        estado.Total.Should().Be(500m, "pero no suma");
    }

    [Fact]
    public async Task UnCargoManualSinMotivo_seRechaza()
    {
        // Es el que nadie puede explicar después, y el que sale en el reporte de excepciones.
        var e = await ArrangeAsync();
        var handler = NuevoHandler(out var scope);
        using (scope)
        {
            var acto = async () => await handler.HandleAsync(new CaptureChargeCommand(
                e.OrgId, e.AccountId, e.ItemId, ChargeSourceKind.Manual, "m-9"));

            await acto.Should().ThrowAsync<ArgumentException>();
        }
    }

    [Fact]
    public async Task UnaCuentaCerrada_noAceptaCargos()
    {
        var e = await ArrangeAsync();

        using var scope = fixture.Factory.Services.CreateScope();
        var sp = scope.ServiceProvider;
        await sp.GetRequiredService<CloseAccountHandler>().HandleAsync(e.OrgId, e.AccountId);

        var r = await sp.GetRequiredService<CaptureChargeHandler>().HandleAsync(
            new CaptureChargeCommand(
                e.OrgId, e.AccountId, e.ItemId, ChargeSourceKind.LabOrder, "sr-tarde"));

        r.Succeeded.Should().BeFalse();
        r.Rejected.Should().Contain("Closed");
    }

    [Fact]
    public async Task AbrirDosVecesLaCuentaDeUnEncuentro_devuelveLaMisma()
    {
        // El cierre de una consulta puede reintentarse. Dos cuentas del mismo episodio parten el
        // estado del paciente en dos sin que nada lo señale.
        var orgId = Guid.NewGuid();
        var enc = $"enc-{Guid.NewGuid():N}";

        using var scope = fixture.Factory.Services.CreateScope();
        var handler = scope.ServiceProvider.GetRequiredService<OpenAccountHandler>();

        var a = await handler.HandleAsync(new OpenAccountCommand(orgId, null, "pat-9", "Ana", enc, "Episode"));
        var b = await handler.HandleAsync(new OpenAccountCommand(orgId, null, "pat-9", "Ana", enc, "Episode"));

        b.Id.Should().Be(a.Id);
    }

    [Fact]
    public async Task ElImpuesto_saleDeLaTasaCongeladaDelCargo()
    {
        var orgId = Guid.NewGuid();
        using var scope = fixture.Factory.Services.CreateScope();
        var sp = scope.ServiceProvider;

        var setup = await sp.GetRequiredService<ProvisionFiscalRegimeHandler>()
            .HandleAsync(orgId, "HN_SAR_CAI");
        var isv15 = setup.TaxCodes.First(t => t.Code == "ISV15").Id;

        var item = await sp.GetRequiredService<CreateBillableItemHandler>().HandleAsync(
            new CreateBillableItemCommand(
                orgId, "EST_BOTOX", "Toxina botulínica", "Service", isv15, 4000m,
                "Prepaid", "Global", null, null, null));

        var cuenta = await sp.GetRequiredService<OpenAccountHandler>().HandleAsync(
            new OpenAccountCommand(orgId, null, "pat-est", "Estética", $"enc-{Guid.NewGuid():N}", "Episode"));

        var r = await sp.GetRequiredService<CaptureChargeHandler>().HandleAsync(
            new CaptureChargeCommand(orgId, cuenta.Id, item.Id, ChargeSourceKind.Encounter, "enc-est"));

        r.Charge!.TaxRate.Should().Be(15m);
        r.Charge.NetAmount.Should().Be(4000m);
        r.Charge.TaxAmount.Should().Be(600m);
        r.Charge.Total.Should().Be(4600m, "la estética sí paga ISV en Honduras");
    }
}
