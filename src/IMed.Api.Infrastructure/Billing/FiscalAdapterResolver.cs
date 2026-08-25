using IMed.Api.Application.Abstractions;

namespace IMed.Api.Infrastructure.Billing;

/// <summary>
/// Resuelve el adaptador de un régimen (FSF-DEC-02). Es lo que mantiene al núcleo sin compilar
/// contra ninguna implementación concreta.
/// </summary>
public sealed class FiscalAdapterResolver : IFiscalAdapterResolver
{
    private readonly Dictionary<string, IFiscalAdapter> _porRegimen;

    public FiscalAdapterResolver(IEnumerable<IFiscalAdapter> adapters)
    {
        _porRegimen = new Dictionary<string, IFiscalAdapter>(StringComparer.OrdinalIgnoreCase);
        foreach (var a in adapters)
        {
            // Dos adaptadores para el mismo régimen es un error de registro, y en silencio se
            // convierte en "a veces factura distinto" según el orden del contenedor.
            if (!_porRegimen.TryAdd(a.RegimeCode, a))
            {
                throw new InvalidOperationException(
                    $"Hay más de un adaptador fiscal registrado para '{a.RegimeCode}'.");
            }
        }
    }

    public IFiscalAdapter? Find(string regimeCode) =>
        string.IsNullOrWhiteSpace(regimeCode)
            ? null
            : _porRegimen.GetValueOrDefault(regimeCode.Trim());

    public IFiscalAdapter Require(string regimeCode) =>
        Find(regimeCode)
        ?? throw new InvalidOperationException(
            $"No hay adaptador fiscal para el régimen '{regimeCode}'. " +
            $"Registrados: {string.Join(", ", _porRegimen.Keys.Order())}.");
}
