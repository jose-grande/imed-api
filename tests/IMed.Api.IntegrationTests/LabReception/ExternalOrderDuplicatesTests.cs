using FluentAssertions;
using IMed.Api.Application.LabReception;
using Xunit;

namespace IMed.Api.IntegrationTests.LabReception;

/// <summary>
/// Una orden externa no admite el mismo examen o panel dos veces: se procesaría y cobraría dos
/// veces (dos tubos, dos cargos, dos resultados). La pantalla lo impide; el servidor también, por si
/// la solicitud llega de otro lado.
/// </summary>
public sealed class ExternalOrderDuplicatesTests
{
    private const string Panel = "http://imed.local/panel";
    private const string Prueba = "http://imed.local/lab-test";

    private static ExternalOrderExam Examen(string? sistema, string? codigo, string texto, string? loinc = null) =>
        new(loinc, codigo, sistema, texto, texto);

    [Fact]
    public void ElMismoPanelDosVeces_seRechaza_yDiceCual()
    {
        var repetir = () => CreateExternalOrderMultiHandler.RejectDuplicates(
            [Examen(Panel, "CBC", "Hemograma completo"), Examen(Panel, "CBC", "Hemograma completo")]);

        repetir.Should().Throw<ArgumentException>().WithMessage("Hemograma completo está repetido*");
    }

    [Fact]
    public void LaMismaPruebaDosVeces_seRechaza()
    {
        var repetir = () => CreateExternalOrderMultiHandler.RejectDuplicates(
            [Examen(Prueba, "TSH", "TSH"), Examen(Prueba, "tsh", "TSH")]);

        repetir.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void ExamenesDistintos_pasan()
    {
        var distintos = () => CreateExternalOrderMultiHandler.RejectDuplicates(
            [Examen(Panel, "CBC", "Hemograma completo"), Examen(Prueba, "TSH", "TSH"), Examen(Prueba, "GLU", "Glucosa")]);

        distintos.Should().NotThrow();
    }

    [Fact]
    public void UnPanelYUnaPruebaConElMismoCodigo_noSonElMismoExamen()
    {
        // El código solo identifica dentro de su sistema: el panel "GLU" y la prueba "GLU" son cosas distintas.
        var distintos = () => CreateExternalOrderMultiHandler.RejectDuplicates(
            [Examen(Panel, "GLU", "Curva de glucosa"), Examen(Prueba, "GLU", "Glucosa")]);

        distintos.Should().NotThrow();
    }
}
