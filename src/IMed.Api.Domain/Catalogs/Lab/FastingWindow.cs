namespace IMed.Api.Domain.Catalogs.Lab;

/// <summary>
/// Ventana de ayuno efectiva de un conjunto de pruebas: el rango de horas que satisface a
/// todas a la vez.
/// </summary>
/// <remarks>
/// <para>Antes el ayuno de un panel o de un contenedor se resolvía con el <b>máximo</b> de las
/// horas de sus pruebas, porque cada prueba tenía un solo número. Con rangos eso ya no alcanza:
/// hay que combinar <b>el mayor de los mínimos</b> con <b>el menor de los máximos</b>.</para>
///
/// <para>Y esa combinación puede quedar vacía. Si una prueba pide 8–10 h y otra 12–14 h, no
/// existe una hora de ayuno que sirva para las dos: la muestra va a estar fuera de rango para al
/// menos una. Con un solo número eso era <b>indetectable</b> —el máximo devolvía 14 y nadie se
/// enteraba de que la primera prueba quedaba fuera de su ventana—. Acá queda expuesto en
/// <see cref="IsImpossible"/>.</para>
/// </remarks>
/// <param name="MinHours">Mayor de los mínimos. <c>null</c> si ninguna prueba exige ayuno.</param>
/// <param name="MaxHours">Menor de los máximos. <c>null</c> si ninguna declara cota superior.</param>
public readonly record struct FastingWindow(int? MinHours, int? MaxHours)
{
    /// <summary>Ventana de un conjunto sin requisitos de ayuno.</summary>
    public static readonly FastingWindow None = new(null, null);

    /// <summary>¿Hay que preguntar por el ayuno al recibir la muestra?</summary>
    public bool Required => MinHours is > 0;

    /// <summary>
    /// <c>true</c> cuando ninguna hora de ayuno satisface a todas las pruebas.
    /// </summary>
    /// <remarks>
    /// No bloquea la recepción —es una decisión de negocio que se toma en la pantalla, no acá—,
    /// pero tiene que poder verse: hoy este caso pasa inadvertido.
    /// </remarks>
    public bool IsImpossible => MinHours is int min && MaxHours is int max && max < min;

    /// <summary>
    /// Combina las ventanas de varias pruebas en la que satisface a todas.
    /// </summary>
    /// <param name="ranges">Pares (mínimo, máximo) de cada prueba. Los nulos se ignoran.</param>
    public static FastingWindow Combine(IEnumerable<(int? Min, int? Max)> ranges)
    {
        ArgumentNullException.ThrowIfNull(ranges);

        int? min = null;
        int? max = null;

        foreach (var (candidateMin, candidateMax) in ranges)
        {
            // El ayuno más estricto manda: si una sola prueba del panel exige 9 h, el paciente
            // tiene que venir con 9 h.
            if (candidateMin is int m && (min is null || m > min))
            {
                min = m;
            }

            // Y la cota superior más estricta también: si una prueba admite hasta 12 h, pasadas
            // esas 12 h su resultado ya no es válido, aunque otra prueba tolere más.
            if (candidateMax is int x && (max is null || x < max))
            {
                max = x;
            }
        }

        return new FastingWindow(min, max);
    }

    /// <summary>Combina las ventanas de un conjunto de pruebas del catálogo.</summary>
    public static FastingWindow Combine(IEnumerable<LabTestDefinition> tests)
    {
        ArgumentNullException.ThrowIfNull(tests);
        return Combine(tests.Select(t => (t.FastingHoursMin, t.FastingHoursMax)));
    }

    /// <summary>
    /// Texto para mostrarle a quien recibe la muestra. Cadena vacía si no se exige ayuno.
    /// </summary>
    /// <remarks>
    /// Se redacta acá y no en la pantalla para que el mensaje de bloqueo y la etiqueta de
    /// recepción digan lo mismo: eran dos textos distintos construidos por separado.
    /// </remarks>
    public string Describe() => (MinHours, MaxHours) switch
    {
        (int min, int max) when max < min => $"rango inconsistente ({min} a {max} h)",
        (int min, int max) => $"{min} a {max} h",
        (int min, null) => $"al menos {min} h",
        (null, int max) => $"a lo sumo {max} h",
        _ => string.Empty,
    };
}
