using FluentAssertions;
using IMed.Api.Domain.Catalogs.Lab;
using Xunit;

namespace IMed.Api.IntegrationTests.LabReception;

/// <summary>
/// Tests de <see cref="FastingWindow"/>, la combinación del ayuno exigido por varias pruebas.
///
/// <para>Observación de QA (ronda 1, staging): «la expresión de horas de ayuno debe permitir un
/// rango». Lo confirmó un técnico con el caso del perfil lipídico — mínimo 9 h, máximo 12 h.</para>
/// </summary>
public sealed class FastingWindowTests
{
    [Fact]
    public void Una_prueba_con_rango_conserva_sus_dos_cotas()
    {
        var w = FastingWindow.Combine([(9, 12)]);

        w.MinHours.Should().Be(9);
        w.MaxHours.Should().Be(12);
        w.Required.Should().BeTrue();
        w.IsImpossible.Should().BeFalse();
        w.Describe().Should().Be("9 a 12 h");
    }

    [Fact]
    public void Sin_cota_superior_queda_abierta()
    {
        // Una glucosa pide «al menos 8 h» y no se arruina por ayunar más.
        var w = FastingWindow.Combine([(8, (int?)null)]);

        w.MaxHours.Should().BeNull();
        w.Describe().Should().Be("al menos 8 h");
    }

    [Fact]
    public void Sin_requisitos_no_exige_ayuno()
    {
        var w = FastingWindow.Combine([((int?)null, (int?)null), ((int?)null, (int?)null)]);

        w.Required.Should().BeFalse();
        w.IsImpossible.Should().BeFalse();
        w.Describe().Should().BeEmpty();
    }

    [Fact]
    public void Combina_el_mayor_de_los_minimos_con_el_menor_de_los_maximos()
    {
        // Un panel con una glucosa (8 h, sin tope) y un perfil lipídico (9 a 12 h):
        // la ventana que sirve para las dos es 9 a 12.
        var w = FastingWindow.Combine([(8, (int?)null), (9, 12)]);

        w.MinHours.Should().Be(9);
        w.MaxHours.Should().Be(12);
        w.IsImpossible.Should().BeFalse();
    }

    [Fact]
    public void Las_pruebas_sin_ayuno_no_arrastran_la_ventana()
    {
        // Agregar un hemograma a un perfil lipídico no relaja el ayuno del segundo.
        var w = FastingWindow.Combine([((int?)null, (int?)null), (9, 12)]);

        w.MinHours.Should().Be(9);
        w.MaxHours.Should().Be(12);
    }

    [Fact]
    public void Detecta_ventanas_que_no_se_cruzan()
    {
        // Esta es la capacidad nueva. Con un solo número por prueba, el `Max` devolvía 14 y
        // nadie se enteraba de que la primera prueba quedaba fuera de su ventana: ninguna hora
        // de ayuno sirve para las dos a la vez.
        var w = FastingWindow.Combine([(8, 10), (12, 14)]);

        w.IsImpossible.Should().BeTrue();
        w.MinHours.Should().Be(12);
        w.MaxHours.Should().Be(10);
        w.Describe().Should().Contain("inconsistente");
    }

    [Fact]
    public void Un_rango_de_un_solo_valor_es_valido()
    {
        // «exactamente 12 h» es un requisito legítimo, no un conflicto.
        var w = FastingWindow.Combine([(12, 12)]);

        w.IsImpossible.Should().BeFalse();
        w.Describe().Should().Be("12 a 12 h");
    }

    [Fact]
    public void Un_conjunto_vacio_no_exige_ayuno()
    {
        var w = FastingWindow.Combine(Array.Empty<(int?, int?)>());

        w.Should().Be(FastingWindow.None);
        w.Required.Should().BeFalse();
    }

    [Fact]
    public void El_catalogo_rechaza_un_rango_invertido()
    {
        // La invariante vive en el dominio: un máximo menor que el mínimo deja la prueba
        // inrecibible, y conviene que falle al cargarla y no al recibir la muestra.
        var crear = () => LabTestDefinition.Create(
            "X", "Prueba", fastingHoursMin: 12, fastingHoursMax: 9);

        crear.Should().Throw<ArgumentException>().WithMessage("*no puede ser menor que el mínimo*");
    }
}
