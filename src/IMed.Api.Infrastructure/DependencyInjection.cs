using IMed.Api.Application.Abstractions;
using IMed.Api.Application.Abstractions.Email;
using IMed.Api.Application.Abstractions.Terminology;
using IMed.Api.Application.Allergies;
using IMed.Api.Application.Catalogs;
using IMed.Api.Application.Diagnoses;
using IMed.Api.Application.Diagrams;
using IMed.Api.Application.Forms;
using IMed.Api.Application.Forms.Studio;
using IMed.Api.Application.Identifiers;
using IMed.Api.Application.Invitations;
using IMed.Api.Application.JoinRequests;
using IMed.Api.Application.LabOrders;
using IMed.Api.Application.Labs;
using IMed.Api.Application.Medications;
using IMed.Api.Application.Notifications;
using IMed.Api.Application.Organizations;
using IMed.Api.Application.Outbox;
using IMed.Api.Application.Patients;
using IMed.Api.Application.Practitioners;
using IMed.Api.Application.Prescriptions;
using IMed.Api.Application.Profile;
using IMed.Api.Application.Team;
using IMed.Api.Infrastructure.Email;
using IMed.Api.Infrastructure.Forms.Studio;
using IMed.Api.Infrastructure.Hapi;
using IMed.Api.Infrastructure.Identifiers;
using IMed.Api.Infrastructure.Keycloak;
using IMed.Api.Infrastructure.Notifications;
using IMed.Api.Infrastructure.Outbox;
using IMed.Api.Infrastructure.Pdf;
using IMed.Api.Infrastructure.Persistence;
using IMed.Api.Infrastructure.Persistence.Seeding;
using IMed.Api.Infrastructure.Terminology;
using IMed.Api.Infrastructure.Terminology.Importers;
using IMed.Api.Infrastructure.Terminology.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace IMed.Api.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        // TimeProvider.System es la implementación default en .NET 8+; registrarla
        // explícitamente permite inyectarla y sustituirla por fakes en tests.
        services.TryAddSingleton<TimeProvider>(_ => TimeProvider.System);

        services.AddDatabase(configuration);
        services.AddKeycloakAdmin(configuration);
        services.AddHapiFhir(configuration);
        services.AddEmail(configuration);
        services.AddInvitations(configuration);
        services.AddIdentifiers(configuration);
        services.AddFormStudio(configuration);
        services.AddTerminology();
        services.AddPdf();
        services.AddUseCases();
        services.AddNotifications();
        services.AddOutbox(configuration);
        services.AddSeeders();

        return services;
    }

    private static IServiceCollection AddFormStudio(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton<IFieldCatalogProvider, DbFieldCatalogProvider>();
        services.AddSingleton(configuration.GetSection(FormStudioOptions.SectionName).Get<FormStudioOptions>()
            ?? new FormStudioOptions());
        services.AddScoped<StudioFormProcessor>();
        return services;
    }

    private static IServiceCollection AddEmail(this IServiceCollection services, IConfiguration configuration)
    {
        // Provider seleccionado por config Email:Provider — "smtp4dev" en dev,
        // "smtp" genérico en staging/prod (Épica O elige el relay concreto).
        // El binding es Singleton: el sender mantiene state liviano (options) y
        // crea SmtpClient por request — seguro para concurrencia.
        services.Configure<EmailOptions>(configuration.GetSection(EmailOptions.SectionName));

        var provider = configuration[$"{EmailOptions.SectionName}:Provider"]?.Trim().ToLowerInvariant();
        switch (provider)
        {
            case "smtp4dev":
                services.AddSingleton<IEmailSender, Smtp4DevEmailSender>();
                break;
            case "smtp":
                services.AddSingleton<IEmailSender, GenericSmtpEmailSender>();
                break;
            case null or "":
                throw new InvalidOperationException(
                    "Falta config Email:Provider. Valores válidos: 'smtp4dev' (dev) o 'smtp' (staging/prod).");
            default:
                throw new InvalidOperationException(
                    $"Email:Provider='{provider}' no soportado. Valores válidos: 'smtp4dev' o 'smtp'.");
        }
        return services;
    }

    private static IServiceCollection AddInvitations(this IServiceCollection services, IConfiguration configuration)
    {
        // Bind a un singleton concreto (no IOptions<>) — el handler no necesita
        // recargar la config en runtime y el código queda más limpio.
        var opts = configuration.GetSection(InvitationKeycloakOptions.SectionName)
            .Get<InvitationKeycloakOptions>() ?? new InvitationKeycloakOptions();
        services.AddSingleton(opts);

        var joinReqOpts = configuration.GetSection(JoinRequestEmailOptions.SectionName)
            .Get<JoinRequestEmailOptions>() ?? new JoinRequestEmailOptions();
        services.AddSingleton(joinReqOpts);
        return services;
    }

    private static IServiceCollection AddIdentifiers(this IServiceCollection services, IConfiguration configuration)
    {
        // Registry config-driven (Épica H1). Singleton porque la config es estática.
        services.Configure<IdentifierTypesOptions>(configuration.GetSection(IdentifierTypesOptions.SectionName));
        services.AddSingleton<IIdentifierTypeRegistry, ConfigBasedIdentifierTypeRegistry>();
        services.AddSingleton<IIdentifierValidator, IdentifierValidator>();
        return services;
    }

    private static IServiceCollection AddPdf(this IServiceCollection services)
    {
        // Renderers stateless (Épicas K5 + L5). El font resolver de PdfSharpCore
        // se inicializa lazy en el ctor — seguro como singleton.
        services.AddSingleton<IPrescriptionPdfRenderer, PrescriptionPdfRenderer>();
        services.AddSingleton<ILabOrderPdfRenderer, LabOrderPdfRenderer>();
        return services;
    }

    private static IServiceCollection AddTerminology(this IServiceCollection services)
    {
        // Port + adapter Postgres del servicio de terminología (Épica N rev 1.1).
        // El adapter rutea por system URI a readers per-CodeSystem. Cuando aparezcan
        // adapters alternativos (HAPI Terminology Server, Snowstorm, ElasticSearch),
        // se reemplaza el binding de ITerminologyService o se introduce un router.
        services.AddScoped<ITerminologyService, PostgresTerminologyService>();

        // Readers per-CodeSystem. PostgresTerminologyService los descubre vía
        // IEnumerable<INamedTerminologyCatalogReader> en su constructor.
        services.AddScoped<INamedTerminologyCatalogReader, Icd11CatalogReader>();
        services.AddScoped<INamedTerminologyCatalogReader, LoincCatalogReader>();

        // Importers (uso interno del adapter Postgres). Múltiples implementaciones
        // pueden coexistir para el mismo CodeSystem — el seeder elige por SourceFormat.
        services.AddScoped<ITerminologyCatalogImporter, Icd11WhoSimpleTabulationImporter>();
        services.AddScoped<ITerminologyCatalogImporter, LoincStandardImporter>();

        return services;
    }

    private static IServiceCollection AddSeeders(this IServiceCollection services)
    {
        // Seeders idempotentes que corren al startup. Si la BD ya tiene los datos,
        // no-op (lo verifican antes de insertar).
        services.AddHostedService<SpecialtySeeder>();
        services.AddHostedService<AllergySeeder>();
        services.AddHostedService<MedicationSeeder>();
        services.AddHostedService<FormDefinitionsSeeder>();
        services.AddHostedService<DiagramDefinitionsSeeder>();
        services.AddHostedService<Icd11Seeder>();
        services.AddHostedService<LoincSeeder>();
        return services;
    }

    private static IServiceCollection AddNotifications(this IServiceCollection services)
    {
        // Bus in-memory: state vive en el singleton. Multi-instance requiere
        // reemplazar por una implementación pub/sub (Redis) — ADR-006.
        services.AddSingleton<IUserNotificationBus, InMemoryUserNotificationBus>();
        return services;
    }

    private static IServiceCollection AddOutbox(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<OutboxProcessorOptions>(configuration.GetSection(OutboxProcessorOptions.SectionName));

        // Dispatchers: uno por OutboxEvent.Type. Registrar nuevos acá.
        services.AddScoped<IOutboxDispatcher, OrganizationBootstrappedDispatcher>();
        services.AddScoped<IOutboxDispatcher, PractitionerProfileUpdatedDispatcher>();
        services.AddScoped<IOutboxDispatcher, InvitationEmailPendingDispatcher>();
        services.AddScoped<IOutboxDispatcher, InvitationLazyProvisionPendingDispatcher>();
        services.AddScoped<IOutboxDispatcher, TeamRoleChangedDispatcher>();

        services.AddHostedService<OutboxProcessor>();
        return services;
    }

    private static IServiceCollection AddDatabase(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("ImedApp")
            ?? throw new InvalidOperationException(
                "Falta ConnectionStrings:ImedApp. Ver appsettings o env vars.");

        services.AddDbContext<AppDbContext>(options =>
        {
            options.UseNpgsql(connectionString, npgsql =>
            {
                npgsql.MigrationsAssembly(typeof(AppDbContext).Assembly.GetName().Name);
                npgsql.MigrationsHistoryTable("__ef_migrations_history");
            });
        });

        services.AddScoped<IBootstrapOrganizationPersistence, BootstrapOrganizationPersistence>();
        services.AddScoped<IOrganizationProfileRepository, OrganizationProfileRepository>();
        services.AddScoped<IAppAuditLog, AppAuditLog>();
        services.AddScoped<IFormDefinitionRepository, FormDefinitionRepository>();
        services.AddScoped<IDiagramDefinitionRepository, DiagramDefinitionRepository>();
        services.AddScoped<ISpecialtyRepository, SpecialtyRepository>();
        services.AddScoped<IAllergyCatalogRepository, AllergyCatalogRepository>();
        services.AddScoped<IMedicationCatalogRepository, MedicationCatalogRepository>();
        services.AddScoped<IUserPractitionerAssignmentRepository, UserPractitionerAssignmentRepository>();
        services.AddScoped<IUpsertPractitionerProfilePersistence, UpsertPractitionerProfilePersistence>();
        services.AddScoped<IInvitationsRepository, InvitationsRepository>();
        services.AddScoped<IInvitationsPersistence, InvitationsPersistence>();
        services.AddScoped<IInvitationLazyProvisionPersistence, InvitationLazyProvisionPersistence>();
        services.AddScoped<IJoinRequestsRepository, JoinRequestsRepository>();
        services.AddScoped<IChangeMemberRolePersistence, ChangeMemberRolePersistence>();

        return services;
    }

    private static IServiceCollection AddKeycloakAdmin(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<KeycloakOptions>(configuration.GetSection(KeycloakOptions.SectionName));

        // Singleton compartido por KeycloakAdminClient y HapiFhirClient para no
        // multiplicar llamadas al endpoint /token de Keycloak.
        services.AddSingleton<KeycloakServiceAccountTokenProvider>();
        services.AddHttpClient(KeycloakServiceAccountTokenProvider.TokenHttpClientName, client =>
        {
            client.Timeout = TimeSpan.FromSeconds(10);
        });

        services.AddHttpClient<IKeycloakAdminClient, KeycloakAdminClient>((sp, client) =>
        {
            var opts = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<KeycloakOptions>>().Value;
            if (string.IsNullOrWhiteSpace(opts.BaseUrl))
            {
                throw new InvalidOperationException("Falta Keycloak:BaseUrl en la configuracion.");
            }

            client.BaseAddress = new Uri(opts.BaseUrl.TrimEnd('/') + "/");
            client.Timeout = TimeSpan.FromSeconds(10);
        });

        return services;
    }

    private static IServiceCollection AddHapiFhir(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<HapiFhirOptions>(configuration.GetSection(HapiFhirOptions.SectionName));

        services.AddHttpClient<IHapiFhirClient, HapiFhirClient>((sp, client) =>
        {
            var opts = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<HapiFhirOptions>>().Value;
            if (string.IsNullOrWhiteSpace(opts.BaseUrl))
            {
                throw new InvalidOperationException("Falta HapiFhir:BaseUrl en la configuración.");
            }

            client.BaseAddress = new Uri(opts.BaseUrl.TrimEnd('/') + "/");
            client.Timeout = TimeSpan.FromSeconds(15);
            client.DefaultRequestHeaders.Add("Accept", "application/fhir+json");
        });

        return services;
    }

    private static IServiceCollection AddUseCases(this IServiceCollection services)
    {
        services.AddScoped<BootstrapOrganizationHandler>();
        services.AddScoped<GetMyProfileHandler>();

        // Forms
        services.AddScoped<GetFormByIdHandler>();
        services.AddScoped<ListFormsHandler>();
        services.AddScoped<GetFormBySpecialtyHandler>();
        services.AddScoped<GetFormByCodeHandler>();
        services.AddScoped<ResolveFormByCodeHandler>();
        services.AddScoped<CreateFormHandler>();
        services.AddScoped<UpdateFormHandler>();
        services.AddScoped<PublishFormHandler>();
        services.AddScoped<RetireFormHandler>();
        services.AddScoped<DeleteFormHandler>();

        // Diagrams
        services.AddScoped<GetDiagramByIdHandler>();
        services.AddScoped<ListDiagramsHandler>();
        services.AddScoped<GetDiagramsBySpecialtyHandler>();
        services.AddScoped<GetDiagramByCodeHandler>();
        services.AddScoped<CreateDiagramHandler>();
        services.AddScoped<UpdateDiagramHandler>();
        services.AddScoped<PublishDiagramHandler>();
        services.AddScoped<RetireDiagramHandler>();
        services.AddScoped<DeleteDiagramHandler>();

        // Catalogs
        services.AddScoped<SearchSpecialtyHandler>();
        services.AddScoped<SearchAllergiesHandler>();
        services.AddScoped<SearchMedicationsHandler>();

        // Diagnoses (consume ITerminologyService — agnóstico del provider)
        services.AddScoped<SearchDiagnosesHandler>();
        services.AddScoped<SearchLabsHandler>();

        // Practitioners
        services.AddScoped<GetMyPractitionerProfileHandler>();
        services.AddScoped<UpsertMyPractitionerProfileHandler>();

        // Team (Épica F2 + F5)
        services.AddScoped<ListTeamHandler>();
        services.AddScoped<ChangeMemberRoleHandler>();

        // Invitations (Épica F3)
        services.AddScoped<CreateInvitationHandler>();
        services.AddScoped<RevokeInvitationHandler>();
        services.AddScoped<ListPendingInvitationsHandler>();
        services.AddScoped<MarkInvitationsExpiredHandler>();
        services.AddScoped<InvitationLazyProvisionService>();

        // Join requests (Épica F4)
        services.AddScoped<SubmitJoinRequestHandler>();
        services.AddScoped<ApproveJoinRequestHandler>();
        services.AddScoped<RejectJoinRequestHandler>();
        services.AddScoped<ListPendingJoinRequestsHandler>();

        // Identifiers (Épica H1)
        services.AddScoped<ListIdentifierTypesHandler>();
        services.AddScoped<ValidateIdentifierHandler>();

        // Patients (Épica H2 — dedupe; create/edit sigue hablando con HAPI desde el frontend)
        services.AddScoped<CheckDuplicatePatientHandler>();

        // Prescriptions PDF (Épica K5)
        services.AddScoped<GetPrescriptionPdfHandler>();

        // Lab orders PDF (Épica L5)
        services.AddScoped<GetLabOrderPdfHandler>();

        return services;
    }
}
