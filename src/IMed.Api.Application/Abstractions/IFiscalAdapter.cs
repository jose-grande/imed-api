using IMed.Api.Domain.Billing.Fiscal;

namespace IMed.Api.Application.Abstractions;

/// <summary>Los cuatro segmentos de un número fiscal, ya resueltos.</summary>
/// <param name="EstablishmentCode">Sede que emite.</param>
/// <param name="EmissionPointCode">Caja o punto de emisión.</param>
/// <param name="DocumentTypeCode">Clase de documento.</param>
/// <param name="Sequence">Correlativo reservado.</param>
/// <param name="FullNumber">
/// El número armado como se imprime. Se devuelve junto a los segmentos —y se guarda
/// denormalizado— porque buscar "la factura 001-002-01-00004521" es lo que hace un cajero cuando
/// el paciente vuelve con el papel en la mano, y reconstruirlo en cada consulta para comparar
/// sería una búsqueda que no usa índice.
/// </param>
public sealed record FiscalNumber(
    string EstablishmentCode,
    string EmissionPointCode,
    string DocumentTypeCode,
    long Sequence,
    string FullNumber);

/// <summary>
/// Encuentra el adaptador de un régimen. Existe para que el resto del código pida "el adaptador de
/// este régimen" sin conocer la lista de implementaciones registradas.
/// </summary>
public interface IFiscalAdapterResolver
{
    /// <summary>El adaptador del régimen, o <c>null</c> si no hay ninguno.</summary>
    IFiscalAdapter? Find(string regimeCode);

    /// <summary>
    /// El adaptador del régimen, o excepción. Se usa donde no tener adaptador es un error de
    /// configuración y no un caso de negocio: emitir bajo un régimen que el sistema no sabe
    /// interpretar produciría un documento inválido, que es peor que no emitir.
    /// </summary>
    IFiscalAdapter Require(string regimeCode);
}

/// <summary>
/// Traduce entre el núcleo agnóstico y las reglas de un régimen fiscal concreto (FSF-DEC-02).
///
/// <para><b>El núcleo no compila contra ninguna implementación.</b> Se resuelve por
/// <see cref="RegimeCode"/> en tiempo de ejecución, así que agregar facturación electrónica u otro
/// país es escribir una clase nueva y registrarla — no tocar el motor.</para>
/// </summary>
public interface IFiscalAdapter
{
    /// <summary>Régimen que atiende este adaptador. Ej.: <c>HN_SAR_CAI</c>.</summary>
    string RegimeCode { get; }

    /// <summary>
    /// Arma el número tal como lo exige el régimen, a partir de los segmentos ya reservados.
    /// </summary>
    FiscalNumber ComposeNumber(
        string establishmentCode,
        string emissionPointCode,
        string documentTypeCode,
        long sequence);

    /// <summary>
    /// Reglas del régimen sobre el comprador. Devuelve el motivo del rechazo, o <c>null</c> si la
    /// venta puede emitirse.
    ///
    /// <para>Vive en el adaptador y no en el núcleo porque el umbral que obliga a pedir
    /// identificación tributaria es una regla local: cambia por país y por reforma.</para>
    /// </summary>
    string? ValidateBuyer(decimal grandTotal, string? buyerTaxId);

    /// <summary>
    /// El importe en letras, cuando el régimen lo exige. <c>null</c> si no aplica.
    /// </summary>
    string? AmountInWords(decimal amount);
}
