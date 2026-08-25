using System.Globalization;
using System.Text;
using IMed.Api.Application.Abstractions;

namespace IMed.Api.Infrastructure.Billing;

/// <summary>
/// Adaptador del régimen de facturación en papel del SAR hondureño (<c>HN_SAR_CAI</c>).
///
/// <para>Es la primera implementación de <see cref="IFiscalAdapter"/> y la única que menciona
/// Honduras. Todo lo de acá es regla local: el formato de 16 dígitos, el umbral de RTN y el monto
/// en letras.</para>
/// </summary>
public sealed class HondurasSarAdapter : IFiscalAdapter
{
    public const string Code = "HN_SAR_CAI";

    /// <summary>
    /// Sobre este monto, la venta exige el RTN del comprador. Es umbral de ley, así que vive acá y
    /// no en configuración de la organización: una clínica no puede elegir su propio umbral.
    /// </summary>
    public const decimal BuyerTaxIdThreshold = 10_000m;

    public string RegimeCode => Code;

    /// <summary>
    /// <c>NNN-NNN-NN-NNNNNNNN</c>: establecimiento · punto de emisión · tipo · correlativo.
    /// </summary>
    public FiscalNumber ComposeNumber(
        string establishmentCode,
        string emissionPointCode,
        string documentTypeCode,
        long sequence)
    {
        var est = Segment(establishmentCode, 3, nameof(establishmentCode));
        var pto = Segment(emissionPointCode, 3, nameof(emissionPointCode));
        var tipo = Segment(documentTypeCode, 2, nameof(documentTypeCode));

        if (sequence < 1 || sequence > 99_999_999)
        {
            throw new ArgumentOutOfRangeException(
                nameof(sequence), "El correlativo del SAR es de 8 dígitos: va de 1 a 99999999.");
        }

        var corr = sequence.ToString("D8", CultureInfo.InvariantCulture);
        return new FiscalNumber(est, pto, tipo, sequence, $"{est}-{pto}-{tipo}-{corr}");
    }

    public string? ValidateBuyer(decimal grandTotal, string? buyerTaxId)
    {
        if (grandTotal <= BuyerTaxIdThreshold)
        {
            return null;
        }
        return string.IsNullOrWhiteSpace(buyerTaxId)
            ? $"Una venta sobre L {BuyerTaxIdThreshold:N2} exige el RTN del comprador."
            : null;
    }

    /// <summary>
    /// Importe en letras, como exige el documento impreso. Ej.: <c>1234.50</c> →
    /// <c>"MIL DOSCIENTOS TREINTA Y CUATRO LEMPIRAS CON 50/100"</c>.
    /// </summary>
    public string? AmountInWords(decimal amount)
    {
        if (amount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(amount), "Un importe negativo no se escribe en letras.");
        }

        var redondeado = Math.Round(amount, 2, MidpointRounding.AwayFromZero);
        var entero = (long)decimal.Truncate(redondeado);
        var centavos = (int)Math.Round((redondeado - entero) * 100m, 0, MidpointRounding.AwayFromZero);

        // El redondeo de los centavos puede empujar al entero: 0.999 → 1 lempira, 0 centavos.
        if (centavos == 100)
        {
            entero++;
            centavos = 0;
        }

        var moneda = entero == 1 ? "LEMPIRA" : "LEMPIRAS";

        // "Un millón DE lempiras", pero "un millón quinientos mil lempiras": el `de` va solo
        // cuando el millón no lleva nada detrás.
        if (entero >= 1_000_000 && entero % 1_000_000 == 0)
        {
            moneda = $"DE {moneda}";
        }

        return $"{Apocopar(EnLetras(entero))} {moneda} CON {centavos:D2}/100";
    }

    /// <summary>
    /// <c>uno</c> se apocopa en <c>un</c> delante de sustantivo masculino: "un lempira",
    /// "veintiún lempiras", "ciento un lempiras". Solo afecta al final del número, que es donde
    /// toca al sustantivo.
    /// </summary>
    private static string Apocopar(string letras)
    {
        if (letras.EndsWith("VEINTIUNO", StringComparison.Ordinal))
        {
            return string.Concat(letras.AsSpan(0, letras.Length - "VEINTIUNO".Length), "VEINTIÚN");
        }
        if (letras.EndsWith("UNO", StringComparison.Ordinal))
        {
            return string.Concat(letras.AsSpan(0, letras.Length - "UNO".Length), "UN");
        }
        return letras;
    }

    private static string Segment(string value, int length, string paramName)
    {
        var trimmed = (value ?? string.Empty).Trim();
        if (trimmed.Length != length || !trimmed.All(char.IsAsciiDigit))
        {
            throw new ArgumentException(
                $"El segmento son exactamente {length} dígitos: '{value}' no sirve.", paramName);
        }
        return trimmed;
    }

    // ── Número a letras (es-HN) ────────────────────────────────────────────
    // Se escribe acá en vez de traer una dependencia porque son las reglas del castellano para
    // menos de mil millones, el documento las exige exactas, y una biblioteca de propósito general
    // trae además las de otros idiomas que nadie va a revisar.

    private static readonly string[] Unidades =
    [
        "CERO", "UNO", "DOS", "TRES", "CUATRO", "CINCO", "SEIS", "SIETE", "OCHO", "NUEVE",
        "DIEZ", "ONCE", "DOCE", "TRECE", "CATORCE", "QUINCE", "DIECISÉIS", "DIECISIETE",
        "DIECIOCHO", "DIECINUEVE", "VEINTE", "VEINTIUNO", "VEINTIDÓS", "VEINTITRÉS",
        "VEINTICUATRO", "VEINTICINCO", "VEINTISÉIS", "VEINTISIETE", "VEINTIOCHO", "VEINTINUEVE",
    ];

    private static readonly string[] Decenas =
    [
        "", "", "", "TREINTA", "CUARENTA", "CINCUENTA", "SESENTA", "SETENTA", "OCHENTA", "NOVENTA",
    ];

    private static readonly string[] Centenas =
    [
        "", "CIENTO", "DOSCIENTOS", "TRESCIENTOS", "CUATROCIENTOS", "QUINIENTOS",
        "SEISCIENTOS", "SETECIENTOS", "OCHOCIENTOS", "NOVECIENTOS",
    ];

    private static string EnLetras(long n)
    {
        if (n == 0)
        {
            return Unidades[0];
        }
        if (n >= 1_000_000_000)
        {
            // Un documento de mil millones de lempiras no existe en este negocio. Fallar es mejor
            // que imprimir un importe en letras que no dice lo mismo que el número.
            throw new ArgumentOutOfRangeException(nameof(n), "Importe fuera del rango soportado.");
        }

        var sb = new StringBuilder();

        var millones = n / 1_000_000;
        if (millones > 0)
        {
            sb.Append(millones == 1 ? "UN MILLÓN" : $"{EnLetras(millones)} MILLONES");
            n %= 1_000_000;
            if (n > 0)
            {
                sb.Append(' ');
            }
        }

        var miles = n / 1_000;
        if (miles > 0)
        {
            sb.Append(miles == 1 ? "MIL" : $"{EnLetras(miles)} MIL");
            n %= 1_000;
            if (n > 0)
            {
                sb.Append(' ');
            }
        }

        if (n > 0)
        {
            sb.Append(MenorQueMil((int)n));
        }

        return sb.ToString();
    }

    private static string MenorQueMil(int n)
    {
        if (n == 100)
        {
            return "CIEN"; // "ciento" solo cuando le sigue algo
        }

        var sb = new StringBuilder();
        var c = n / 100;
        if (c > 0)
        {
            sb.Append(Centenas[c]);
            n %= 100;
            if (n > 0)
            {
                sb.Append(' ');
            }
        }

        if (n == 0)
        {
            return sb.ToString();
        }
        if (n < 30)
        {
            sb.Append(Unidades[n]);
            return sb.ToString();
        }

        sb.Append(Decenas[n / 10]);
        if (n % 10 > 0)
        {
            sb.Append(" Y ").Append(Unidades[n % 10]);
        }
        return sb.ToString();
    }
}
