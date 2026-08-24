namespace IMed.Api.Application.Abstractions;

/// <summary>
/// Sede que el cliente declara para este request (ADR-008).
///
/// <para>Existe para que el selector de sede activa signifique algo: sin esto el backend siempre
/// inferiría la sede, y cambiarla en pantalla no cambiaría nada de lo que el usuario opera.</para>
///
/// <para>Es una <b>declaración, no una autorización</b>: <c>ResolveActiveLocationHandler</c>
/// verifica que el usuario tenga acceso a la sede pedida antes de usarla. Un header no puede
/// habilitar a nadie a operar donde no le corresponde.</para>
/// </summary>
public interface IRequestLocationContext
{
    /// <summary>Sede declarada en el request, o null si el cliente no indicó ninguna.</summary>
    Guid? RequestedLocationId { get; }
}
