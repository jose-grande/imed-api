using IMed.Api.Infrastructure.Inventory;
using IMed.Api.Application.Abstractions;
using IMed.Api.Application.Abstractions.Email;
using IMed.Api.Application.Abstractions.Terminology;
using IMed.Api.Application.Allergies;
using IMed.Api.Application.Preclinic;
using IMed.Api.Application.Authorization;
using IMed.Api.Application.Billing;
using IMed.Api.Application.Catalogs;
using IMed.Api.Application.Diagnoses;
using IMed.Api.Application.FhirGateway;
using IMed.Api.Application.Forms;
using IMed.Api.Application.Identifiers;
using IMed.Api.Application.Invitations;
using IMed.Api.Application.JoinRequests;
using IMed.Api.Application.LabCatalog;
using IMed.Api.Application.LabDashboard;
using IMed.Api.Application.LabOrders;
using IMed.Api.Application.LabReception;
using IMed.Api.Application.LabResults;
using IMed.Api.Application.LabSpecimens;
using IMed.Api.Application.LabRules;
using IMed.Api.Application.Labs;
using IMed.Api.Application.Locations;
using IMed.Api.Application.Medications;
using IMed.Api.Application.Notifications;
using IMed.Api.Application.Organizations;
using IMed.Api.Application.Outbox;
using IMed.Api.Application.Patients;
using IMed.Api.Application.Practitioners;
using IMed.Api.Application.Prescriptions;
using IMed.Api.Application.Profile;
using IMed.Api.Application.Team;
using IMed.Api.Infrastructure.Billing;
using IMed.Api.Infrastructure.Email;
using IMed.Api.Infrastructure.Hapi;
using IMed.Api.Infrastructure.Identifiers;
using IMed.Api.Infrastructure.Imaging;
using IMed.Api.Infrastructure.Keycloak;
using IMed.Api.Infrastructure.Notifications;
using IMed.Api.Infrastructure.Lab;
using IMed.Api.Infrastructure.Outbox;
using IMed.Api.Infrastructure.Pdf;
using IMed.Api.Infrastructure.Persistence;
using IMed.Api.Infrastructure.Persistence.Seeding;
using IMed.Api.Infrastructure.Terminology;
using IMed.Api.Infrastructure.Terminology.Importers;
using IMed.Api.Infrastructure.Terminology.Repositories;
using IMed.Api.Infrastructure.Time;
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

        // Calendario de la clínica: los correlativos legibles llevan la fecha adentro y se
        // reinician cada día, así que tienen que usar el día local y no el de UTC.
        services.Configure<ClinicClockOptions>(configuration.GetSection(ClinicClockOptions.SectionName));
        services.TryAddSingleton<IClinicClock, ClinicClock>();

        // Umbrales de aviso del motor fiscal (FAC-2). Se inyecta la instancia y no IOptions<>
        // porque los handlers la usan en un mapeo puro y no necesitan recarga en caliente.
        var fiscalAlerts = new FiscalAlertOptions();
        configuration.GetSection(FiscalAlertOptions.SectionName).Bind(fiscalAlerts);
        services.AddSingleton(fiscalAlerts);

        services.AddDatabase(configuration);
        services.AddKeycloakAdmin(configuration);
        services.AddHapiFhir(configuration);
        services.AddEmail(configuration);
        services.AddInvitations(configuration);
        services.AddIdentifiers(configuration);
        services.AddTerminology();
        services.AddPdf();
        services.AddUseCases();
        // Inventario y farmacia (Stream B): su registro vive en Inventory/InventoryModule.cs.
        services.AddInventoryModule();
        services.AddNotifications();
        services.AddOutbox(configuration);
        services.AddSeeders();

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
        services.AddSingleton<IInvoicePdfRenderer, InvoicePdfRenderer>();
        services.AddSingleton<ILabReportPdfRenderer, LabReportPdfRenderer>();
        services.AddSingleton<ISpecimenLabelPdfRenderer, SpecimenLabelPdfRenderer>();

        // Identifica el logo que sube la organización por su contenido. Sin estado y sin
        // dependencias: singleton.
        services.AddSingleton<IImageInspector, ImageSharpImageInspector>();
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
        // La matriz de permisos va primero: sin ella no hay autorización que evaluar.
        services.AddHostedService<PermissionSeeder>();
        services.AddHostedService<SpecialtySeeder>();
        services.AddHostedService<AllergySeeder>();
        services.AddHostedService<MedicationSeeder>();
        services.AddHostedService<FormDefinitionsSeeder>();
        services.AddHostedService<Icd11Seeder>();
        services.AddHostedService<LoincSeeder>();
        services.AddHostedService<LabCatalogSeeder>();
        services.AddHostedService<LabRuleSeeder>();
        // DESPUÉS de los seeders de globales: clona el catálogo a las orgs preexistentes
        // (copy-on-create, rev 53). Depende del orden de registro (seeders son IHostedService secuenciales).
        services.AddHostedService<LabCatalogProvisioningBackfill>();
        services.AddHostedService<BillableCatalogBackfill>();
        // Recupera las muestras que ya pasaron por QC antes de existir la convergencia con
        // `order_intake`: sin esto no aparecen en Resultados/dashboard/TAT.
        services.AddHostedService<CollectionIntakeBackfill>();
        // Eje de sede (ADR-008): garantiza la sede principal de cada org existente y ancla a ella
        // los datos operativos anteriores al eje. Va al final: ancla lo que los backfills de
        // arriba pudieron haber creado en este mismo arranque.
        services.AddHostedService<LocationBackfill>();
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
        services.Configure<LabOrderReconciliationOptions>(
            configuration.GetSection(LabOrderReconciliationOptions.SectionName));

        // Dispatchers: uno por OutboxEvent.Type. Registrar nuevos acá.
        services.AddScoped<IOutboxDispatcher, OrganizationBootstrappedDispatcher>();
        services.AddScoped<IOutboxDispatcher, PractitionerProfileUpdatedDispatcher>();
        services.AddScoped<IOutboxDispatcher, InvitationEmailPendingDispatcher>();
        services.AddScoped<IOutboxDispatcher, InvitationLazyProvisionPendingDispatcher>();
        services.AddScoped<IOutboxDispatcher, TeamRoleChangedDispatcher>();
        services.AddScoped<IOutboxDispatcher, TeamFunctionsChangedDispatcher>();
        services.AddScoped<IOutboxDispatcher, EncounterBillingSyncDispatcher>();
        services.AddScoped<IOutboxDispatcher, LabOrderBillingSyncDispatcher>();
        services.AddScoped<IOutboxDispatcher, LocationFhirSyncDispatcher>();
        services.AddScoped<IOutboxDispatcher, PractitionerLocationsSyncDispatcher>();
        services.AddScoped<LocationFhirProjector>();

        services.AddHostedService<OutboxProcessor>();

        // Red de seguridad de las órdenes de laboratorio. La promoción `draft → active` la hace el
        // navegador después del cierre, fuera de la transacción: si se pierde, la orden nunca llega
        // al laboratorio y nadie se entera. Esto la repara sin importar la causa.
        services.AddHostedService<LabOrderReconciliation>();

        // Encuentros de preclínica que nadie atendió (Épica P, P-D2).
        services.Configure<IMed.Api.Infrastructure.Encounters.PlannedEncounterSweepOptions>(
            configuration.GetSection(IMed.Api.Infrastructure.Encounters.PlannedEncounterSweepOptions.SectionName));
        services.AddHostedService<IMed.Api.Infrastructure.Encounters.PlannedEncounterSweep>();
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
        services.AddScoped<IOrganizationBrandingRepository, OrganizationBrandingRepository>();
        services.AddScoped<IAppAuditLog, AppAuditLog>();
        services.AddScoped<IFormDefinitionRepository, FormDefinitionRepository>();
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
        services.AddScoped<ILabCatalogRepository, LabCatalogRepository>();
        services.AddMemoryCache();
        services.AddScoped<IPermissionResolver, PermissionResolver>();
        services.AddScoped<IPermissionMatrixRepository, PermissionMatrixRepository>();
        services.AddScoped<IFhirAccessRuleRepository, FhirAccessRuleRepository>();
        services.AddScoped<FhirAccessEvaluator>();
        services.AddScoped<FhirLocationGuard>();
        services.AddScoped<GetPermissionMatrixHandler>();
        services.AddScoped<SetPermissionGrantHandler>();
        services.AddScoped<ILocationRepository, LocationRepository>();
        services.AddScoped<ILocationProvisioning, LocationProvisioning>();
        services.AddScoped<ILegalEntityRepository, LegalEntityRepository>();

        // Motor fiscal (FAC-2). El adaptador se registra en la colección y se resuelve por
        // `regime_code`: agregar facturación electrónica u otro país es sumar una línea acá, no
        // tocar el motor.
        services.AddScoped<IFiscalRepository, FiscalRepository>();
        services.AddScoped<IBillingCatalogRepository, BillingCatalogRepository>();
        services.AddScoped<IBillingAccountRepository, BillingAccountRepository>();
        services.AddScoped<IPartyRepository, PartyRepository>();
        services.AddScoped<IAuthorizationLimitRepository, AuthorizationLimitRepository>();
        services.AddScoped<IInvoiceRepository, InvoiceRepository>();
        services.AddScoped<ICashRepository, CashRepository>();
        services.AddScoped<IServiceWaiverRepository, ServiceWaiverRepository>();
        services.AddScoped<IFiscalReportRepository, FiscalReportRepository>();
        services.AddSingleton<IFiscalAdapter, HondurasSarAdapter>();
        services.AddSingleton<IFiscalAdapterResolver, FiscalAdapterResolver>();
        services.AddScoped<ILabCatalogProvisioning, LabCatalogProvisioning>();
        services.AddScoped<ILabOperationsRepository, LabOperationsRepository>();
        services.AddScoped<ILabRuleRepository, LabRuleRepository>();

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
        services.AddScoped<SetOrganizationLogoHandler>();
        services.AddScoped<GetOrganizationLogoHandler>();
        services.AddScoped<RemoveOrganizationLogoHandler>();
        services.AddScoped<GetMyProfileHandler>();

        // Forms
        services.AddScoped<GetFormByIdHandler>();
        services.AddScoped<ListFormsHandler>();
        services.AddScoped<GetFormBySpecialtyHandler>();
        services.AddScoped<GetFormByCodeHandler>();
        services.AddScoped<CreateFormHandler>();
        services.AddScoped<UpdateFormHandler>();
        services.AddScoped<PublishFormHandler>();
        services.AddScoped<RetireFormHandler>();
        services.AddScoped<DeleteFormHandler>();

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
        services.AddScoped<SetMemberFunctionsHandler>();

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

        // Preclínica (Épica P-1): contexto acotado a la cita + envío en una transacción
        services.AddScoped<GetPreclinicContextHandler>();
        services.AddScoped<SubmitPreclinicHandler>();
        services.AddScoped<AllergyDedupeHandler>();

        // Lab orders gateway del médico (LIS-RD-09): create/update/get/list por imed-api
        services.AddScoped<UpsertLabOrderHandler>();
        services.AddScoped<AllocateOrderNumberHandler>();
        services.AddScoped<GetLabOrderHandler>();
        services.AddScoped<ListLabOrdersByEncounterHandler>();
        services.AddScoped<ListLabOrdersByPatientHandler>();

        // Lab catalog (Épica LIS-1)
        services.AddScoped<SearchLabTestsHandler>();
        services.AddScoped<GetLabTestByIdHandler>();
        services.AddScoped<CreateLabTestHandler>();
        services.AddScoped<UpdateLabTestHandler>();
        services.AddScoped<SetLabTestActiveHandler>();
        services.AddScoped<ListLabPanelsHandler>();
        services.AddScoped<GetLabPanelDetailHandler>();
        services.AddScoped<CreateLabPanelHandler>();
        services.AddScoped<CreatePanelVersionHandler>();
        services.AddScoped<ListReferenceRangesHandler>();
        services.AddScoped<CreateReferenceRangeHandler>();
        services.AddScoped<UpdateReferenceRangeHandler>();
        services.AddScoped<DeleteReferenceRangeHandler>();
        services.AddScoped<SetOfferingHandler>();
        services.AddScoped<ListLabelFormatsHandler>();
        services.AddScoped<CreateLabelFormatHandler>();
        services.AddScoped<UpdateLabelFormatHandler>();
        services.AddScoped<ListLabelFormatAssignmentsHandler>();
        services.AddScoped<SetLabelFormatAssignmentHandler>();
        services.AddScoped<ListDerivedCalculationsHandler>();
        services.AddScoped<ListTatTargetsHandler>();
        services.AddScoped<ListSpecimenTypesHandler>();
        services.AddScoped<ListTubeTypesHandler>();
        services.AddScoped<ListSpecimenRejectionReasonsHandler>();

        // Lab reception (Épica LIS-3)
        services.AddScoped<ResolveReceptionOrderHandler>();
        services.AddScoped<CreateExternalOrderHandler>();
        services.AddScoped<GetAwaitingAdmissionHandler>();
        services.AddScoped<CreateExternalOrderMultiHandler>();
        services.AddScoped<GetTechnicalReceptionHandler>();
        services.AddScoped<ReceiveSpecimenHandler>();
        services.AddScoped<ResolveRequisitionHandler>();
        services.AddScoped<ResolveActiveLocationHandler>();
        services.AddScoped<ListLocationsHandler>();
        services.AddScoped<ListMyLocationsHandler>();
        services.AddScoped<CreateLocationHandler>();
        services.AddScoped<LegalEntityChoice>();
        services.AddScoped<ListLegalEntitiesHandler>();
        services.AddScoped<UpdateLegalEntityHandler>();
        services.AddScoped<UpdateLocationHandler>();
        services.AddScoped<SetEstablishmentCodeHandler>();
        services.AddScoped<AssignUserToLocationHandler>();
        services.AddScoped<UnassignUserFromLocationHandler>();
        services.AddScoped<SetUserLocationsHandler>();
        services.AddScoped<ListLocationPractitionersHandler>();
        services.AddScoped<LegalEntityScopeResolver>();

        // Motor fiscal (FAC-2).
        services.AddScoped<GetFiscalSetupHandler>();
        services.AddScoped<ProvisionFiscalRegimeHandler>();
        services.AddScoped<ListFiscalAuthorizationsHandler>();
        services.AddScoped<RegisterFiscalAuthorizationHandler>();
        services.AddScoped<RevokeFiscalAuthorizationHandler>();
        services.AddScoped<ListCashPointsHandler>();
        services.AddScoped<CreateCashPointHandler>();
        services.AddScoped<SetCashPointActiveHandler>();

        // Catálogo de cobrables y tarifarios (FAC-3).
        services.AddScoped<SearchBillableItemsHandler>();
        services.AddScoped<CreateBillableItemHandler>();
        services.AddScoped<ResolvePriceHandler>();
        services.AddScoped<ListPricePendingItemsHandler>();
        services.AddScoped<UpdateBillableItemHandler>();
        // Alta automática de cobrables "sin precio" (plan de salida a producción, F0.3).
        services.AddScoped<IBillableItemProvisioner, BillableItemProvisioner>();
        // Cuenta por consulta / por acto suelto (F0.4).
        services.AddScoped<IEncounterAccountResolver, EncounterAccountResolver>();
        // Devolución al inventario por nota de crédito (F0.5). Vacía hasta que el Stream B la reemplace.
        services.AddScoped<IStockReturnPort, NoStockReturnPort>();
        // Cobro de consultas (A3/A4).
        services.AddScoped<IBillingSettingsRepository, BillingSettingsRepository>();
        services.AddScoped<SuggestConsultationTypeHandler>();
        services.AddScoped<EncounterChargeReconciler>();
        services.AddScoped<PractitionerSpecialtyLookup>();
        services.AddScoped<OrganizationCatalogReconciler>();
        services.AddScoped<LabOrderChargeReconciler>();
        services.AddScoped<IOutboxWriter, OutboxWriter>();
        services.AddScoped<ListPriceListsHandler>();
        services.AddScoped<CreatePriceListHandler>();
        services.AddScoped<ListPriceListItemsHandler>();
        services.AddScoped<SetPriceHandler>();

        // Cuentas y captura de cargos (FAC-4).
        services.AddScoped<OpenAccountHandler>();
        services.AddScoped<CloseAccountHandler>();
        services.AddScoped<SearchAccountsHandler>();
        services.AddScoped<GetAccountStatementHandler>();
        services.AddScoped<CaptureChargeHandler>();
        services.AddScoped<ReverseChargeHandler>();
        services.AddScoped<CaptureLabOrderChargesHandler>();

        // Factura y timbrado (FAC-5).
        services.AddScoped<IssueInvoiceHandler>();
        services.AddScoped<GetInvoiceHandler>();
        services.AddScoped<ListAccountInvoicesHandler>();
        services.AddScoped<VoidInvoiceHandler>();
        services.AddScoped<GetInvoicePdfHandler>();

        // Caja: turno, cobro y arqueo (FAC-6).
        services.AddScoped<CashExpectationCalculator>();
        services.AddScoped<OpenCashSessionHandler>();
        services.AddScoped<CloseCashSessionHandler>();
        services.AddScoped<HandoverCashSessionHandler>();
        services.AddScoped<RegisterCashMovementHandler>();
        services.AddScoped<GetOpenSessionHandler>();
        services.AddScoped<ReceivePaymentHandler>();
        services.AddScoped<ListSessionPaymentsHandler>();

        // Puerta de prepago y orden de servicio (FAC-7).
        services.AddScoped<EvaluateServiceGateHandler>();
        services.AddScoped<IServiceGate, ServiceGateAdapter>();
        services.AddScoped<WaiveServiceHandler>();
        services.AddScoped<ListServiceWaiversHandler>();

        // Corte fiscal y reportes (FAC-8).
        services.AddScoped<GetDailyCashCutHandler>();
        services.AddScoped<GetTaxReportHandler>();
        // Nota de crédito (A9).
        services.AddScoped<ICreditNoteRepository, CreditNoteRepository>();
        services.AddScoped<IssueCreditNoteHandler>();
        services.AddScoped<ListCreditNotesHandler>();
        services.AddScoped<RefundCreditNoteHandler>();
        services.AddScoped<GetDocumentLedgerHandler>();
        services.AddScoped<GetExceptionReportHandler>();
        services.AddScoped<CheckInOrderHandler>();
        services.AddScoped<ReferOrderExternalHandler>();
        services.AddScoped<ReceiveRequisitionHandler>();
        services.AddScoped<GetPendingOrdersHandler>();
        services.AddScoped<GetSpecimenLabelHandler>();
        services.AddScoped<GetCollectionQueueHandler>();
        services.AddScoped<GetWorklistHandler>();

        // Plan de colección (Épica R6-F2)
        // Puente ciclo de muestra (R6) ↔ flujo operativo (`order_intake`): lo usan el QC y el backfill.
        services.AddScoped<CollectionIntakeConvergence>();
        services.AddScoped<ComputeCollectionPlanHandler>();
        services.AddScoped<GetCollectionPlanHandler>();

        // Ciclo de vida de muestra: etiquetas + estados + timeline (Épica R6-F3/F4)
        services.AddScoped<GenerateLabelsHandler>();
        services.AddScoped<PrintSpecimenLabelsHandler>();
        services.AddScoped<AdvanceContainerHandler>();
        services.AddScoped<GetContainerTimelineHandler>();

        // Identificación positiva + QC preanalítico (Épica R6-F6)
        services.AddScoped<VerifyIdentityHandler>();
        services.AddScoped<QualityControlHandler>();

        // Lab specimen ops (R5: alícuotas / almacenamiento / estabilidad)
        services.AddScoped<CreateAliquotHandler>();
        services.AddScoped<RecordStorageHandler>();
        services.AddScoped<CheckStabilityHandler>();

        // Lab results (Épica LIS-4)
        services.AddScoped<EnterResultsHandler>();
        services.AddScoped<GetResultEntrySheetHandler>();
        services.AddScoped<CorrectResultHandler>();
        services.AddScoped<GetResultsByPatientHandler>();
        services.AddScoped<GetResultDetailHandler>();
        services.AddScoped<GetPendingValidationHandler>();
        services.AddScoped<GetReportHistoryHandler>();
        services.AddScoped<ReleaseReportHandler>();
        services.AddScoped<RejectReportHandler>();
        services.AddScoped<GetLabReportPdfHandler>();
        services.AddScoped<GetValidationRejectionReasonsHandler>();

        // Lab rules engine (Épica LIS-5)
        services.AddScoped<IResultEvaluationService, ResultEvaluationService>();
        services.AddScoped<ListLabRulesHandler>();
        services.AddScoped<CreateLabRuleHandler>();

        // Lab dashboard (Épica LIS-8)
        services.AddScoped<GetLabDashboardHandler>();

        return services;
    }
}
