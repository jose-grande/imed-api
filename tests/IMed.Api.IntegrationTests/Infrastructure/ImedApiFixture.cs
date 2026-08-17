using IMed.Api.Application.Abstractions;
using IMed.Api.Application.Abstractions.Email;
using IMed.Api.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace IMed.Api.IntegrationTests.Infrastructure;

/// <summary>
/// Fixture compartido por toda la suite. Levanta:
/// <list type="bullet">
///   <item>WireMock de Keycloak (incluye JWKS, token endpoint, admin API).</item>
///   <item>WireMock de HAPI FHIR (partition + Organization).</item>
///   <item>WebApplicationFactory que arranca <c>imed-api</c> apuntando a esos mocks.</item>
/// </list>
///
/// <para><b>Postgres</b>: usa el Postgres local de desarrollo (<c>:5432</c>) con la
/// BD dedicada <c>imed_app_test</c>. La BD debe existir antes (ver
/// <c>imed-infra/postgres/02-create-test-database.sql</c>). Las migraciones EF
/// se aplican en cada run; el state se trunca entre tests.</para>
///
/// <para>El fixture es <c>ICollectionFixture</c> para reusarlo entre clases. Cada
/// test individual debe llamar a <see cref="ResetStateAsync"/> en su
/// <c>InitializeAsync</c> para tener BD/mocks limpios.</para>
/// </summary>
public sealed class ImedApiFixture : IAsyncLifetime
{
    /// <summary>
    /// Connection string del Postgres local. Sobreescribible vía variable de entorno
    /// <c>IMED_TEST_DB_CONNECTION</c> para CI.
    /// </summary>
    private static string TestConnectionString =>
        Environment.GetEnvironmentVariable("IMED_TEST_DB_CONNECTION")
        ?? "Host=localhost;Port=5432;Database=imed_app_test;Username=imed;Password=imed_dev_password;Include Error Detail=true";

    public TestSigningKey SigningKey { get; } = new();
    public KeycloakMockServer Keycloak { get; private set; } = null!;
    public HapiMockServer Hapi { get; } = new();
    public FakeEmailSender Email { get; } = new();
    public ImedApiFactory Factory { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        Keycloak = new KeycloakMockServer(SigningKey);

        // Migrar ANTES de construir el host. Resolver `Factory.Services` arranca los
        // IHostedService, y varios seeders consultan tablas al arrancar: si la migración
        // corriera después, un seeder que toca una tabla recién agregada fallaría contra un
        // esquema viejo. En producción el pipeline migra antes de levantar el proceso; acá se
        // reproduce ese orden en vez de depender de que ningún seeder mire el esquema nuevo.
        // Mismas opciones que el registro real: sin la tabla de historial correcta, EF creería
        // que no hay ninguna migración aplicada e intentaría crear el esquema desde cero.
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(TestConnectionString, npgsql =>
            {
                npgsql.MigrationsAssembly(typeof(AppDbContext).Assembly.GetName().Name);
                npgsql.MigrationsHistoryTable("__ef_migrations_history");
            })
            .Options;
        await using (var migrationDb = new AppDbContext(options))
        {
            await migrationDb.Database.MigrateAsync();
        }

        Factory = new ImedApiFactory(
            postgresConnectionString: TestConnectionString,
            keycloakAuthority: Keycloak.Authority,
            keycloakBaseUrl: Keycloak.BaseUrl,
            hapiBaseUrl: Hapi.BaseUrl,
            emailSender: Email);
    }

    public async Task DisposeAsync()
    {
        await Factory.DisposeAsync();
        Hapi.Dispose();
        Keycloak.Dispose();
        SigningKey.Dispose();
    }

    /// <summary>
    /// Limpia el state entre tests: trunca tablas de imed_app_test y resetea los mocks.
    /// </summary>
    public async Task ResetStateAsync()
    {
        Keycloak.Reset();
        Hapi.Reset();
        Email.Reset();

        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.Database.ExecuteSqlRawAsync("""
            TRUNCATE TABLE app_audit_log,
                           outbox_events,
                           invitations,
                           join_requests,
                           user_practitioner_assignments,
                           user_tier_assignments,
                           organization_subscriptions
            RESTART IDENTITY CASCADE;
            """);
    }

    /// <summary>
    /// Aprovisiona el catálogo de laboratorio de una org (copy-on-create, rev 53):
    /// clona las plantillas globales del seed a filas propias de la org. En prod lo
    /// hace el bootstrap; los tests que usan tokens con org ad-hoc lo llaman para que
    /// las lecturas estrictas por org vean el catálogo. Idempotente.
    /// </summary>
    public async Task ProvisionLabCatalogAsync(Guid organizationId)
    {
        using var scope = Factory.Services.CreateScope();
        var provisioning = scope.ServiceProvider.GetRequiredService<ILabCatalogProvisioning>();
        await provisioning.ProvisionOrganizationAsync(organizationId);
    }
}

[CollectionDefinition(Name)]
public sealed class ImedApiCollection : ICollectionFixture<ImedApiFixture>
{
    public const string Name = "imed-api integration";
}

/// <summary>
/// WebApplicationFactory que apunta el host de tests al Postgres local + mocks
/// levantados por <see cref="ImedApiFixture"/>.
///
/// <para><b>Por qué env vars</b>: <c>Program.cs</c> en minimal API lee
/// <c>builder.Configuration["..."]</c> al tope (antes de <c>builder.Build()</c>).
/// Las overrides via <c>ConfigureAppConfiguration</c> en
/// <see cref="WebApplicationFactory{TEntryPoint}"/> se aplican TARDE al
/// <c>ConfigurationManager</c> y no llegan a esos reads. Las env vars en cambio
/// son cargadas nativamente por el provider chain default <b>antes</b> de que
/// <c>Program.cs</c> empiece a leer.</para>
/// </summary>
public sealed class ImedApiFactory : WebApplicationFactory<Program>
{
    private readonly List<string> _ownedEnvVars = new();
    private readonly IEmailSender _emailSender;

    public ImedApiFactory(
        string postgresConnectionString,
        string keycloakAuthority,
        string keycloakBaseUrl,
        string hapiBaseUrl,
        IEmailSender emailSender)
    {
        _emailSender = emailSender;
        // Doble underscore = separador de sección en env vars
        // (https://learn.microsoft.com/en-us/aspnet/core/fundamentals/configuration/#environment-variables).
        SetEnv("ConnectionStrings__ImedApp", postgresConnectionString);
        SetEnv("Keycloak__Authority", keycloakAuthority);
        SetEnv("Keycloak__BaseUrl", keycloakBaseUrl);
        SetEnv("Keycloak__Realm", "imed-clinical-dev-test");
        SetEnv("Keycloak__ServiceAccountClientId", "imed-api-sa");
        SetEnv("Keycloak__ServiceAccountClientSecret", "test-secret");
        SetEnv("Keycloak__RequireHttpsMetadata", "false");
        SetEnv("HapiFhir__BaseUrl", hapiBaseUrl);
        SetEnv("Cors__AllowedOrigins__0", "http://localhost:4200");
        // Outbox: tick agresivo para que los tests no esperen 5s entre polls.
        SetEnv("Outbox__Enabled", "true");
        SetEnv("Outbox__IdlePollSeconds", "1");
        SetEnv("Outbox__BusyPollSeconds", "1");
        SetEnv("Outbox__BatchSize", "10");
        SetEnv("Outbox__BackoffBaseSeconds", "1");
        SetEnv("Outbox__BackoffMaxSeconds", "10");
        SetEnv("Outbox__MaxAttempts", "5");
        // El modo del proxy FHIR se fija acá y no se hereda del appsettings: cuando el entorno
        // pasó a `Enforce`, la prueba del modo sombra empezó a fallar sin que nadie tocara ni el
        // proxy ni la prueba. Un test que depende de una config ambiente no dice lo que dice.
        SetEnv("FhirGateway__Mode", "Shadow");
        SetEnv("FhirGateway__IntentMode", "Shadow");
        SetEnv("Logging__LogLevel__Default", "Warning");
        SetEnv("Logging__LogLevel__IMed.Api", "Debug");

        // Email infra: registramos smtp4dev como provider (no se conecta de
        // verdad en tests — los handlers que envían email lo hacen vía mocks).
        // El AddEmail() del DependencyInjection requiere Email:Provider — sin
        // esto el host no inicia.
        SetEnv("Email__Provider", "smtp4dev");
        SetEnv("Email__Host", "localhost");
        SetEnv("Email__Port", "2525");
        SetEnv("Email__UseSsl", "false");
        SetEnv("Email__FromAddress", "no-reply@imed.test");
        SetEnv("Email__FromDisplayName", "IMed (test)");

        // Fixture ICD-11 (~18 codes representativos, copy-to-output desde
        // Fixtures/icd11-test-tabulation.txt). Catálogo shared entre tests
        // de la collection — read-only data, NO se truncar en ResetStateAsync.
        var icd11FixturePath = Path.Combine(
            AppContext.BaseDirectory, "Fixtures", "icd11-test-tabulation.txt");
        SetEnv("Catalogs__Icd11SourcePath", icd11FixturePath);

        // Fixture LOINC (~18 ACTIVE codes con shape de release multi-archivo
        // — Fixtures/loinc-test/{LoincTable, AccessoryFiles/LinguisticVariants}).
        var loincBaseDir = Path.Combine(AppContext.BaseDirectory, "Fixtures", "loinc-test");
        SetEnv("Catalogs__Loinc__BaseDir", loincBaseDir);
        SetEnv("Catalogs__Loinc__PrimaryVariant", "esMX28LinguisticVariant.csv");
        SetEnv("Catalogs__Loinc__AdditionalVariants__0", "esES12LinguisticVariant.csv");
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Test");
        builder.ConfigureServices(services =>
        {
            // Reemplazar el IEmailSender registrado por la infra (smtp4dev /
            // generic) por nuestro fake — los tests aserran contra los mensajes
            // capturados en lugar de levantar un SMTP local.
            services.RemoveAll<IEmailSender>();
            services.AddSingleton(_emailSender);
        });
    }

    protected override void Dispose(bool disposing)
    {
        // Limpiar env vars para no contaminar otros runs en el mismo proceso.
        if (disposing)
        {
            foreach (var key in _ownedEnvVars)
            {
                Environment.SetEnvironmentVariable(key, null);
            }
        }
        base.Dispose(disposing);
    }

    private void SetEnv(string key, string value)
    {
        Environment.SetEnvironmentVariable(key, value);
        _ownedEnvVars.Add(key);
    }
}
