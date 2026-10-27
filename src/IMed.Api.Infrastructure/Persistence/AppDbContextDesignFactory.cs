using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace IMed.Api.Infrastructure.Persistence;

/// <summary>
/// Factoría que usan las herramientas de línea de comandos de EF Core para construir el
/// <see cref="AppDbContext"/> fuera del ciclo de vida de la aplicación.
/// </summary>
/// <remarks>
/// <para>Sin esto, <c>dotnet ef</c> tiene que compilar <c>IMed.Api.WebApi</c> para encontrar el
/// contexto, y eso falla con <c>MSB3021</c> cuando el servicio está corriendo en desarrollo: el
/// proceso mantiene tomados los <c>.dll</c> de su propio <c>bin</c>. Con la factoría, agregar o
/// aplicar una migración no requiere detener el API.</para>
///
/// <para><b>La cadena de conexión sale de la misma variable que usa el servicio</b>,
/// <c>ConnectionStrings__ImedApp</c>, y si falta se aborta. No hay ninguna por defecto a
/// propósito, por las dos razones de siempre: el comando que viene después modifica el esquema
/// de la base a la que apunte —elegirla por nuestra cuenta es elegir qué base se altera—, y una
/// credencial escrita en el repositorio no debe existir, ni siquiera de desarrollo.</para>
/// </remarks>
public sealed class AppDbContextDesignFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    /// <summary>Variable de entorno con la cadena de conexión. La misma que lee el servicio.</summary>
    public const string ConnectionVariable = "ConnectionStrings__ImedApp";

    /// <inheritdoc/>
    /// <exception cref="InvalidOperationException">No hay cadena de conexión.</exception>
    public AppDbContext CreateDbContext(string[] args)
    {
        var conn = Environment.GetEnvironmentVariable(ConnectionVariable);

        if (string.IsNullOrWhiteSpace(conn))
        {
            throw new InvalidOperationException(
                $"Falta {ConnectionVariable}. Las herramientas de EF no leen appsettings ni .env por "
                + "sí solas; hay que exportar la variable antes:\n"
                + "    $env:ConnectionStrings__ImedApp = \"Host=...;Database=imed_app;...\"\n"
                + "No se usa ninguna cadena por defecto a propósito: el comando que viene después "
                + "modifica el esquema de la base a la que apunte.");
        }

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(conn, npgsql =>
            {
                // Debe coincidir con el registro en DependencyInjection.
                npgsql.MigrationsHistoryTable("__ef_migrations_history");
                npgsql.MigrationsAssembly(typeof(AppDbContext).Assembly.GetName().Name);
            })
            .Options;

        return new AppDbContext(options);
    }
}
