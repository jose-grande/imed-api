using FluentAssertions;
using IMed.Api.Domain.Billing.Catalog;
using Xunit;

namespace IMed.Api.IntegrationTests.Billing;

/// <summary>
/// Resolución de precio (FAC-3). Es una función pura, así que se prueba sin host: cada escalón de
/// la precedencia tiene su caso, que es justo lo que no se consigue cuando la regla vive en un
/// <c>ORDER BY</c> y probar un escalón exige sembrar cinco tablas.
/// </summary>
public sealed class PriceResolverTests
{
    private static readonly Guid Org = Guid.NewGuid();
    private static readonly Guid TaxId = Guid.NewGuid();
    private static readonly DateOnly Hoy = new(2026, 8, 18);

    private static BillableItem Item(decimal defaultPrice = 500m) =>
        BillableItem.Create(Org, "CONS_GEN", "Consulta general", BillableItemKind.Service, TaxId, defaultPrice);

    private static PriceCandidate Candidato(
        BillableItem item,
        PriceListKind kind,
        decimal price,
        DateOnly? from = null,
        DateOnly? to = null,
        int priority = 0,
        Guid? payerContractId = null,
        DateOnly? itemFrom = null,
        DateOnly? itemTo = null)
    {
        var lista = PriceList.Create(
            Org, $"L{kind}{price}{priority}", $"Lista {kind}", kind,
            from ?? new DateOnly(2026, 1, 1), to, priority, payerContractId: payerContractId);
        var precio = PriceListItem.Create(
            Org, lista.Id, item.Id, price,
            itemFrom ?? new DateOnly(2026, 1, 1), itemTo);
        return new PriceCandidate(lista, precio);
    }

    [Fact]
    public void SinTarifario_ganaElPrecioBaseDelItem()
    {
        var item = Item(500m);

        var r = PriceResolver.Resolve(item, [], Hoy);

        r.Price.Should().Be(500m);
        r.Source.Should().Be(PriceSource.ItemDefault);
        r.Explain().Should().Contain("ningún tarifario");
    }

    [Fact]
    public void ConDosTarifariosSolapados_ganaElCorrectoYSeSabeDeCual()
    {
        // 🎯 Criterio de aceptación de FAC-3: el precio correcto **y** la pantalla dice de dónde
        // salió. Sin lo segundo, "¿por qué se le cobró esto?" se responde llamando a administración.
        var item = Item(500m);
        var publica = Candidato(item, PriceListKind.Public, 450m);
        var promo = Candidato(item, PriceListKind.Promotional, 380m);

        var r = PriceResolver.Resolve(item, [publica, promo], Hoy);

        r.Price.Should().Be(380m, "la promoción gana sobre la lista pública");
        r.Source.Should().Be(PriceSource.PromotionalList);
        r.Explain().Should().Contain("Promoción vigente");
    }

    [Fact]
    public void ElConvenioGanaSobreTodo_peroSoloParaQuienLoTiene()
    {
        // Aplicar el precio de un convenio a quien no lo tiene es regalar el descuento negociado.
        var item = Item(500m);
        var contrato = Guid.NewGuid();
        var convenio = Candidato(item, PriceListKind.Agreement, 300m, payerContractId: contrato);
        var promo = Candidato(item, PriceListKind.Promotional, 380m);

        var conContrato = PriceResolver.Resolve(item, [convenio, promo], Hoy, payerContractId: contrato);
        conContrato.Price.Should().Be(300m);
        conContrato.Source.Should().Be(PriceSource.AgreementList);

        var sinContrato = PriceResolver.Resolve(item, [convenio, promo], Hoy, payerContractId: null);
        sinContrato.Price.Should().Be(380m, "sin contrato, el convenio no aplica");

        var otroContrato = PriceResolver.Resolve(item, [convenio, promo], Hoy, payerContractId: Guid.NewGuid());
        otroContrato.Price.Should().Be(380m, "el convenio de otro pagador tampoco aplica");
    }

    [Fact]
    public void UnPrecioConVigenciaVencida_noSeAplica()
    {
        // Criterio de aceptación 4. Una promoción que terminó ayer no puede seguir cobrándose.
        var item = Item(500m);
        var publica = Candidato(item, PriceListKind.Public, 450m);
        var promoVencida = Candidato(
            item, PriceListKind.Promotional, 380m,
            from: new DateOnly(2026, 1, 1), to: new DateOnly(2026, 8, 17));

        var r = PriceResolver.Resolve(item, [publica, promoVencida], Hoy);

        r.Price.Should().Be(450m);
        r.Source.Should().Be(PriceSource.PublicList);
    }

    [Fact]
    public void UnaListaVigentePeroConElPrecioVencido_tampocoAplica()
    {
        // La vigencia de la lista y la de la línea son distintas: se puede retirar un ítem de una
        // promoción sin cerrar la promoción entera.
        var item = Item(500m);
        var promo = Candidato(
            item, PriceListKind.Promotional, 380m,
            itemFrom: new DateOnly(2026, 1, 1), itemTo: new DateOnly(2026, 8, 1));

        var r = PriceResolver.Resolve(item, [promo], Hoy);

        r.Source.Should().Be(PriceSource.ItemDefault);
        r.Price.Should().Be(500m);
    }

    [Fact]
    public void UnaListaQueTodaviaNoEmpieza_noSeAdelanta()
    {
        var item = Item(500m);
        var futura = Candidato(item, PriceListKind.Promotional, 300m, from: new DateOnly(2026, 9, 1));

        PriceResolver.Resolve(item, [futura], Hoy).Source.Should().Be(PriceSource.ItemDefault);
    }

    [Fact]
    public void EntreDosPromocionesSolapadas_mandaLaPrioridadDeclarada()
    {
        // Dos promociones a la vez son normales —la de temporada y la del aniversario—. Sin un
        // criterio declarado, la que gana depende del orden en que la base devuelva las filas, y
        // eso es un precio que cambia sin que nadie lo haya cambiado.
        var item = Item(500m);
        var temporada = Candidato(item, PriceListKind.Promotional, 400m, priority: 1);
        var aniversario = Candidato(item, PriceListKind.Promotional, 350m, priority: 5);

        PriceResolver.Resolve(item, [temporada, aniversario], Hoy)
            .Price.Should().Be(350m, "mayor prioridad gana");

        // Y el orden en que llegan los candidatos no cambia el resultado.
        PriceResolver.Resolve(item, [aniversario, temporada], Hoy).Price.Should().Be(350m);
    }

    [Fact]
    public void ElOrdenCompleto_esConvenioPromocionPersonalPublica()
    {
        var item = Item(500m);
        var contrato = Guid.NewGuid();
        var todas = new[]
        {
            Candidato(item, PriceListKind.Public, 450m),
            Candidato(item, PriceListKind.Employee, 400m),
            Candidato(item, PriceListKind.Promotional, 380m),
            Candidato(item, PriceListKind.Agreement, 300m, payerContractId: contrato),
        };

        PriceResolver.Resolve(item, todas, Hoy, contrato).Source.Should().Be(PriceSource.AgreementList);
        PriceResolver.Resolve(item, todas.Take(3).ToArray(), Hoy).Source.Should().Be(PriceSource.PromotionalList);
        PriceResolver.Resolve(item, todas.Take(2).ToArray(), Hoy).Source.Should().Be(PriceSource.EmployeeList);
        PriceResolver.Resolve(item, todas.Take(1).ToArray(), Hoy).Source.Should().Be(PriceSource.PublicList);
    }

    [Fact]
    public void ElPrecioDeOtroItem_noSeMezcla()
    {
        var item = Item(500m);
        var otro = BillableItem.Create(Org, "LAB_CBC", "Hemograma", BillableItemKind.Service, TaxId, 200m);
        var precioDelOtro = Candidato(otro, PriceListKind.Promotional, 150m);

        PriceResolver.Resolve(item, [precioDelOtro], Hoy)
            .Source.Should().Be(PriceSource.ItemDefault);
    }

    [Fact]
    public void ElPisoYElTope_viajanConElPrecioResuelto()
    {
        // El piso importa tanto como el precio: sin él, un descuento autorizado puede dejar la
        // línea por debajo del costo y eso se descubre mirando márgenes a fin de mes.
        var item = Item(500m);
        var lista = PriceList.Create(Org, "PUB", "Pública", PriceListKind.Public, new DateOnly(2026, 1, 1));
        var precio = PriceListItem.Create(
            Org, lista.Id, item.Id, 450m, new DateOnly(2026, 1, 1),
            minPrice: 380m, discountPctMax: 15m);

        var r = PriceResolver.Resolve(item, [new PriceCandidate(lista, precio)], Hoy);

        r.MinPrice.Should().Be(380m);
        r.DiscountPctMax.Should().Be(15m);
    }

    [Fact]
    public void UnPisoMayorQueElPrecio_seRechazaAlCrearlo()
    {
        var item = Item();
        var lista = PriceList.Create(Org, "PUB", "Pública", PriceListKind.Public, new DateOnly(2026, 1, 1));

        var crear = () => PriceListItem.Create(
            Org, lista.Id, item.Id, 400m, new DateOnly(2026, 1, 1), minPrice: 500m);

        crear.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void UnCobrableSinTributo_noSePuedeCrear()
    {
        // El default silencioso —"asumamos exento"— es cómo se declara de menos sin darse cuenta.
        var crear = () => BillableItem.Create(
            Org, "X", "Algo", BillableItemKind.Service, Guid.Empty, 100m);

        crear.Should().Throw<ArgumentException>().WithMessage("*tributo*");
    }

    [Fact]
    public void UnCobrable_noPuedeEnlazarAPruebaYPanelALaVez()
    {
        // Si lo hiciera, capturar el cargo tendría dos orígenes posibles y la idempotencia de
        // FAC-4 dejaría de poder distinguirlos.
        var crear = () => BillableItem.Create(
            Org, "X", "Algo", BillableItemKind.Service, TaxId, 100m,
            labTestId: Guid.NewGuid(), labPanelId: Guid.NewGuid());

        crear.Should().Throw<ArgumentException>();
    }
}
