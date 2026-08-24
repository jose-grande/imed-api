using System.Globalization;

namespace IMed.Api.Application.LabReception;

/// <summary>
/// Construcción del número de accession — el identificador que va impreso en la etiqueta y que
/// se escanea en cada estación.
///
/// <para><b>Por qué lleva la sede.</b> Desde ADR-008 el correlativo es por sede: cada sucursal
/// lleva su propia numeración contigua, porque una secuencia compartida hace que cada una vea
/// huecos en sus números, y en un laboratorio un hueco se lee como una muestra perdida. Pero con
/// secuencias independientes, dos sucursales generarían el mismo número el mismo día — así que el
/// segmento de sede no es decorativo: es lo que mantiene único al accession.</para>
///
/// <para>Formato: <c>{ORG4}-{SEDE4}-{yyyyMMdd}-{seq}</c>. Es además la práctica habitual en
/// laboratorios con varias sedes: el número dice de dónde salió la muestra sin consultar nada.</para>
/// </summary>
public static class LabAccession
{
    /// <summary>Sede a usar cuando la fila es anterior al eje y no hay código que poner.</summary>
    private const string UnknownLocationSegment = "GRAL";

    public static string Build(Guid organizationId, string? locationCode, DateOnly day, int seq)
    {
        var org = $"{organizationId:N}"[..4].ToUpperInvariant();
        var site = Normalize(locationCode);
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{org}-{site}-{day:yyyyMMdd}-{seq:D4}");
    }

    /// <summary>
    /// Recorta el código de sede a un segmento estable de 4 caracteres. Se quedan solo letras y
    /// dígitos: el separador del accession es el guion, y un código con guion partiría el número
    /// en más segmentos de los que el escáner espera.
    /// </summary>
    private static string Normalize(string? locationCode)
    {
        if (string.IsNullOrWhiteSpace(locationCode))
        {
            return UnknownLocationSegment;
        }

        var alnum = new string(locationCode.Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();
        if (alnum.Length == 0)
        {
            return UnknownLocationSegment;
        }
        return alnum.Length <= 4 ? alnum : alnum[..4];
    }
}
