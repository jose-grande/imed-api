using FluentAssertions;
using IMed.Api.Domain.Billing.Fiscal;
using IMed.Api.Infrastructure.Billing;
using Xunit;

namespace IMed.Api.IntegrationTests.Billing;

/// <summary>
/// Adaptador del régimen HN_SAR_CAI (FAC-2). Es el único lugar del motor que menciona Honduras,
/// así que acá se fijan las reglas locales: el formato de 16 dígitos, el umbral de RTN y el
/// importe en letras.
/// </summary>
public sealed class HondurasSarAdapterTests
{
    private readonly HondurasSarAdapter _sar = new();

    [Fact]
    public void ElNumero_seArmaEnCuatroSegmentos()
    {
        var numero = _sar.ComposeNumber("001", "002", "01", 4521);

        numero.FullNumber.Should().Be("001-002-01-00004521");
        numero.EstablishmentCode.Should().Be("001");
        numero.EmissionPointCode.Should().Be("002");
        numero.DocumentTypeCode.Should().Be("01");
        numero.Sequence.Should().Be(4521);
    }

    [Theory]
    [InlineData("1", "002", "01")]     // establecimiento corto
    [InlineData("001", "2", "01")]     // punto de emisión corto
    [InlineData("001", "002", "1")]    // tipo corto
    [InlineData("00A", "002", "01")]   // con letra
    public void UnSegmentoMalFormado_seRechaza(string est, string punto, string tipo)
    {
        // Se valida acá y no en la pantalla: un número mal formado no se descubre al guardarlo,
        // se descubre cuando la autoridad rechaza el documento.
        var armar = () => _sar.ComposeNumber(est, punto, tipo, 1);

        armar.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void ElCorrelativo_esDeOchoDigitos()
    {
        _sar.ComposeNumber("001", "002", "01", 99_999_999)
            .FullNumber.Should().Be("001-002-01-99999999");

        var pasado = () => _sar.ComposeNumber("001", "002", "01", 100_000_000);
        pasado.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void SobreElUmbral_laVentaExigeRtnDelComprador()
    {
        // Regla de ley, no configuración de la clínica: una organización no elige su propio umbral.
        _sar.ValidateBuyer(10_000m, null).Should().BeNull("justo en el umbral todavía no se exige");
        _sar.ValidateBuyer(9_999.99m, null).Should().BeNull();

        _sar.ValidateBuyer(10_000.01m, null).Should().NotBeNull("pasado el umbral hace falta RTN");
        _sar.ValidateBuyer(25_000m, "08011990123456").Should().BeNull("con RTN pasa");
    }

    [Theory]
    [InlineData(0, "CERO LEMPIRAS CON 00/100")]
    [InlineData(15.5, "QUINCE LEMPIRAS CON 50/100")]
    [InlineData(100, "CIEN LEMPIRAS CON 00/100")]
    [InlineData(1234.5, "MIL DOSCIENTOS TREINTA Y CUATRO LEMPIRAS CON 50/100")]
    [InlineData(2000, "DOS MIL LEMPIRAS CON 00/100")]
    [InlineData(1_500_000, "UN MILLÓN QUINIENTOS MIL LEMPIRAS CON 00/100")]
    public void ElImporteEnLetras_sigueLasReglasDelCastellano(decimal monto, string esperado)
    {
        // El documento impreso lo exige. "Cien" y "ciento", "un millón" y "dos millones", y la
        // "y" solo entre decena y unidad: son las que se equivocan al escribirlas a mano.
        _sar.AmountInWords(monto).Should().Be(esperado);
    }

    [Theory]
    [InlineData(1, "UN LEMPIRA CON 00/100")]
    [InlineData(21, "VEINTIÚN LEMPIRAS CON 00/100")]
    [InlineData(31, "TREINTA Y UN LEMPIRAS CON 00/100")]
    [InlineData(101, "CIENTO UN LEMPIRAS CON 00/100")]
    [InlineData(1001, "MIL UN LEMPIRAS CON 00/100")]
    public void ElUnoFinal_seApocopaAntesDeLempira(decimal monto, string esperado)
    {
        // "Un lempira", no "uno lempira". La apócope aplica al `uno` que toca al sustantivo, así
        // que también en veintiuno, treinta y uno, ciento uno y mil uno.
        _sar.AmountInWords(monto).Should().Be(esperado);
    }

    [Theory]
    [InlineData(1_000_000, "UN MILLÓN DE LEMPIRAS CON 00/100")]
    [InlineData(2_000_000, "DOS MILLONES DE LEMPIRAS CON 00/100")]
    public void ElMillonRedondo_llevaDeAntesDelSustantivo(decimal monto, string esperado)
    {
        // "Un millón DE lempiras", pero "un millón quinientos mil lempiras": el `de` va solo
        // cuando el millón no lleva nada detrás.
        _sar.AmountInWords(monto).Should().Be(esperado);
    }

    [Fact]
    public void LosCentavosQueRedondeanHaciaArriba_empujanElEntero()
    {
        // 0.999 no es "CERO LEMPIRAS CON 100/100".
        _sar.AmountInWords(0.999m).Should().Be("UN LEMPIRA CON 00/100");
    }

    [Fact]
    public void ElResolverDeAdaptadores_encuentraElRegimenYFallaClaroSiNoExiste()
    {
        var resolver = new FiscalAdapterResolver([_sar]);

        resolver.Find(HondurasSarAdapter.Code).Should().BeSameAs(_sar);
        resolver.Find("HN_SAR_ELECTRONICA").Should().BeNull();

        var exigir = () => resolver.Require("HN_SAR_ELECTRONICA");
        exigir.Should().Throw<InvalidOperationException>()
            .WithMessage("*HN_SAR_CAI*", "el error tiene que decir qué regímenes sí están registrados");
    }

    [Fact]
    public void DosAdaptadoresParaElMismoRegimen_fallanAlArrancar()
    {
        // En silencio se convertiría en "a veces factura distinto", según el orden del contenedor.
        var duplicado = () => new FiscalAdapterResolver([_sar, new HondurasSarAdapter()]);

        duplicado.Should().Throw<InvalidOperationException>().WithMessage("*más de un adaptador*");
    }

    [Fact]
    public void UnTributoQueNoGrava_noPuedeLlevarTasa()
    {
        // Un "exento al 15%" produce impuesto cobrado sobre algo que la ley no grava, y eso se
        // descubre en la declaración.
        var crear = () => TaxCode.Create(Guid.NewGuid(), "EXENTO", "Exento de ISV", 15m, TaxTreatment.Exempt);

        crear.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void ElImpuesto_saleDeLaTasaDelItemYNoDeUnaConstante()
    {
        var org = Guid.NewGuid();
        var gravado = TaxCode.Create(org, "ISV15", "ISV 15%", 15m, TaxTreatment.Taxed);
        var exento = TaxCode.Create(org, "EXENTO", "Exento", 0m, TaxTreatment.Exempt);

        gravado.TaxOn(1000m).Should().Be(150m);
        exento.TaxOn(1000m).Should().Be(0m, "la consulta médica no paga ISV en Honduras");
    }
}
