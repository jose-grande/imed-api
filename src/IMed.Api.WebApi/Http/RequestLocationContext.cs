using IMed.Api.Application.Abstractions;
using Microsoft.AspNetCore.Http;

namespace IMed.Api.WebApi.Http;

/// <summary>
/// Lee la sede declarada por el cliente del header <c>X-IMed-Location</c> (ADR-008).
///
/// <para>Un header mal formado se trata como "no declaró sede" en vez de como error: el request
/// sigue con la sede inferida. La alternativa —rechazar— convertiría un detalle del cliente en
/// una caída de la operación, y la inferencia ya da una respuesta correcta.</para>
/// </summary>
public sealed class RequestLocationContext(IHttpContextAccessor accessor) : IRequestLocationContext
{
    public const string HeaderName = "X-IMed-Location";

    public Guid? RequestedLocationId
    {
        get
        {
            var raw = accessor.HttpContext?.Request.Headers[HeaderName].ToString();
            return Guid.TryParse(raw, out var id) && id != Guid.Empty ? id : null;
        }
    }
}
