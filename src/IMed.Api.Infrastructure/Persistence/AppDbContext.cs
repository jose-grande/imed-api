using System.Text.Json;
using IMed.Api.Domain.Auditing;
using IMed.Api.Domain.Authorization;
using IMed.Api.Domain.Catalogs;
using IMed.Api.Domain.Catalogs.Lab;
using IMed.Api.Domain.Forms;
using IMed.Api.Domain.Lab;
using IMed.Api.Domain.Invitations;
using IMed.Api.Domain.JoinRequests;
using IMed.Api.Domain.Locations;
using IMed.Api.Domain.Outbox;
using IMed.Api.Domain.Practitioners;
using IMed.Api.Domain.Subscriptions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using NpgsqlTypes;

namespace IMed.Api.Infrastructure.Persistence;

public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<OrganizationSubscription> OrganizationSubscriptions => Set<OrganizationSubscription>();
    public DbSet<UserTierAssignment> UserTierAssignments => Set<UserTierAssignment>();
    public DbSet<OutboxEvent> OutboxEvents => Set<OutboxEvent>();
    public DbSet<AppAuditLogEntry> AppAuditLog => Set<AppAuditLogEntry>();
    public DbSet<FormDefinition> FormDefinitions => Set<FormDefinition>();
    public DbSet<Icd11Code> Icd11Catalog => Set<Icd11Code>();
    public DbSet<LoincCode> LoincCatalog => Set<LoincCode>();
    public DbSet<SpecialtyCode> SpecialtyCatalog => Set<SpecialtyCode>();
    public DbSet<AllergyCatalogEntry> AllergyCatalog => Set<AllergyCatalogEntry>();
    public DbSet<MedicationCatalogEntry> MedicationCatalog => Set<MedicationCatalogEntry>();
    public DbSet<UserPractitionerAssignment> UserPractitionerAssignments => Set<UserPractitionerAssignment>();
    public DbSet<Invitation> Invitations => Set<Invitation>();
    public DbSet<JoinRequest> JoinRequests => Set<JoinRequest>();

    // Catálogo de laboratorio (Épica LIS-1)
    public DbSet<LabTestDefinition> LabTests => Set<LabTestDefinition>();
    public DbSet<LabTestOffering> LabTestOfferings => Set<LabTestOffering>();
    public DbSet<LabPanel> LabPanels => Set<LabPanel>();
    public DbSet<LabPanelVersion> LabPanelVersions => Set<LabPanelVersion>();
    public DbSet<LabPanelVersionItem> LabPanelVersionItems => Set<LabPanelVersionItem>();
    public DbSet<ReferenceRange> ReferenceRanges => Set<ReferenceRange>();
    public DbSet<DerivedCalculation> DerivedCalculations => Set<DerivedCalculation>();
    public DbSet<TatTarget> TatTargets => Set<TatTarget>();
    public DbSet<SpecimenType> SpecimenTypes => Set<SpecimenType>();
    public DbSet<TubeType> TubeTypes => Set<TubeType>();
    public DbSet<SpecimenRejectionReason> SpecimenRejectionReasons => Set<SpecimenRejectionReason>();
    public DbSet<ValidationRejectionReason> ValidationRejectionReasons => Set<ValidationRejectionReason>();
    public DbSet<ResultValidationRejection> ResultValidationRejections => Set<ResultValidationRejection>();
    public DbSet<LabLabelFormat> LabLabelFormats => Set<LabLabelFormat>();
    public DbSet<LabLabelFormatAssignment> LabLabelFormatAssignments => Set<LabLabelFormatAssignment>();

    // Operación del laboratorio (Épica LIS-3)
    public DbSet<OrderIntake> OrderIntakes => Set<OrderIntake>();
    public DbSet<SpecimenEvent> SpecimenEvents => Set<SpecimenEvent>();
    public DbSet<LabAccessionCounter> LabAccessionCounters => Set<LabAccessionCounter>();
    public DbSet<LabOrderCounter> LabOrderCounters => Set<LabOrderCounter>();
    public DbSet<LabOrderCheckIn> LabOrderCheckIns => Set<LabOrderCheckIn>();
    public DbSet<LabRule> LabRules => Set<LabRule>();
    public DbSet<LabRuleVersion> LabRuleVersions => Set<LabRuleVersion>();
    public DbSet<RuleExecution> RuleExecutions => Set<RuleExecution>();

    // Plan de colección (Épica R6-F2)
    public DbSet<CollectionPlan> CollectionPlans => Set<CollectionPlan>();
    public DbSet<CollectionPlanContainer> CollectionPlanContainers => Set<CollectionPlanContainer>();
    public DbSet<CollectionPlanItem> CollectionPlanItems => Set<CollectionPlanItem>();
    public DbSet<CollectionPlanUnavailableItem> CollectionPlanUnavailableItems => Set<CollectionPlanUnavailableItem>();
    public DbSet<CollectionEvent> CollectionEvents => Set<CollectionEvent>();

    // Matriz de permisos (ADR-007)
    public DbSet<Permission> Permissions => Set<Permission>();
    public DbSet<RolePermission> RolePermissions => Set<RolePermission>();
    public DbSet<PolicyVersion> PolicyVersions => Set<PolicyVersion>();
    public DbSet<FhirAccessRule> FhirAccessRules => Set<FhirAccessRule>();

    // Eje de sede (ADR-008)
    public DbSet<OrganizationLocation> OrganizationLocations => Set<OrganizationLocation>();
    public DbSet<UserLocationAssignment> UserLocations => Set<UserLocationAssignment>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        ConfigureAuthorization(modelBuilder);
        ConfigureLocations(modelBuilder);
        ConfigureOrganizationSubscription(modelBuilder);
        ConfigureUserTierAssignment(modelBuilder);
        ConfigureOutboxEvent(modelBuilder);
        ConfigureAppAuditLog(modelBuilder);
        ConfigureFormDefinition(modelBuilder);
        ConfigureIcd11Code(modelBuilder);
        ConfigureLoincCode(modelBuilder);
        ConfigureSpecialtyCode(modelBuilder);
        ConfigureAllergyCatalogEntry(modelBuilder);
        ConfigureMedicationCatalogEntry(modelBuilder);
        ConfigureUserPractitionerAssignment(modelBuilder);
        ConfigureInvitation(modelBuilder);
        ConfigureJoinRequest(modelBuilder);

        ConfigureLabTestDefinition(modelBuilder);
        ConfigureLabTestOffering(modelBuilder);
        ConfigureLabPanel(modelBuilder);
        ConfigureLabPanelVersion(modelBuilder);
        ConfigureLabPanelVersionItem(modelBuilder);
        ConfigureReferenceRange(modelBuilder);
        ConfigureDerivedCalculation(modelBuilder);
        ConfigureTatTarget(modelBuilder);
        ConfigureSpecimenType(modelBuilder);
        ConfigureTubeType(modelBuilder);
        ConfigureSpecimenRejectionReason(modelBuilder);
        ConfigureValidationRejection(modelBuilder);
        ConfigureLabLabelFormat(modelBuilder);
        ConfigureLabLabelFormatAssignment(modelBuilder);

        ConfigureOrderIntake(modelBuilder);
        ConfigureSpecimenEvent(modelBuilder);
        ConfigureLabAccessionCounter(modelBuilder);
        ConfigureLabOrderCounter(modelBuilder);
        ConfigureLabOrderCheckIn(modelBuilder);
        ConfigureLabRule(modelBuilder);
        ConfigureLabRuleVersion(modelBuilder);
        ConfigureRuleExecution(modelBuilder);

        ConfigureCollectionPlan(modelBuilder);
        ConfigureCollectionPlanContainer(modelBuilder);
        ConfigureCollectionPlanItem(modelBuilder);
        ConfigureCollectionPlanUnavailableItem(modelBuilder);
        ConfigureCollectionEvent(modelBuilder);

        ApplySnakeCaseConvention(modelBuilder);
    }

    private static void ConfigureOrganizationSubscription(ModelBuilder b)
    {
        b.Entity<OrganizationSubscription>(e =>
        {
            e.ToTable("organization_subscriptions");
            e.HasKey(x => x.OrganizationId);
            e.Property(x => x.Name).HasMaxLength(255).IsRequired();
            e.Property(x => x.FhirId).HasMaxLength(64).IsRequired();
            e.Property(x => x.ContractStatus).HasConversion<string>().HasMaxLength(20).IsRequired();
            e.Property(x => x.EffectiveFrom).IsRequired();
            e.Property(x => x.TrialEndsAt);
            e.Property(x => x.SeatsBasic).IsRequired();
            e.Property(x => x.SeatsPremium).IsRequired();
            e.Property(x => x.SeatsPlatinum).IsRequired();
        });
    }

    private static void ConfigureUserTierAssignment(ModelBuilder b)
    {
        b.Entity<UserTierAssignment>(e =>
        {
            e.ToTable("user_tier_assignments");
            e.HasKey(x => new { x.KeycloakUserId, x.AssignedAt });
            e.Property(x => x.Tier).HasConversion<string>().HasMaxLength(20).IsRequired();
            e.Property(x => x.AssignedBy).IsRequired();
            e.Property(x => x.AssignedAt).IsRequired();
            e.Property(x => x.EffectiveUntil);
            e.Property(x => x.OrganizationId).IsRequired();

            // Índice sobre las asignaciones vigentes (EffectiveUntil IS NULL)
            // optimiza la query "dame el tier actual del usuario X".
            e.HasIndex(x => x.KeycloakUserId)
                .HasDatabaseName("ix_user_tier_assignments_user_active")
                .HasFilter("effective_until IS NULL");

            e.HasIndex(x => x.OrganizationId)
                .HasDatabaseName("ix_user_tier_assignments_org");
        });
    }

    private static void ConfigureOutboxEvent(ModelBuilder b)
    {
        b.Entity<OutboxEvent>(e =>
        {
            e.ToTable("outbox_events");
            e.HasKey(x => x.Id);
            e.Property(x => x.Aggregate).HasMaxLength(100).IsRequired();
            e.Property(x => x.Type).HasMaxLength(100).IsRequired();
            e.Property(x => x.Payload).HasColumnType("jsonb").IsRequired();
            e.Property(x => x.CreatedAt).IsRequired();
            e.Property(x => x.DispatchedAt);
            e.Property(x => x.AttemptCount).IsRequired();
            e.Property(x => x.NextAttemptAt);
            e.Property(x => x.LastError);

            // Index para el worker: "dame los pendientes que ya deberían reintentarse"
            e.HasIndex(x => x.NextAttemptAt)
                .HasDatabaseName("ix_outbox_events_next_attempt")
                .HasFilter("dispatched_at IS NULL");
        });
    }

    /// <summary>
    /// Eje de sede (ADR-008): la sede es un <c>Location</c> dentro de una organización, no una
    /// organización hija. El índice único de <c>IsDefault</c> es filtrado — garantiza <b>una</b>
    /// sede principal por organización sin impedir que las demás existan.
    /// </summary>
    private static void ConfigureLocations(ModelBuilder b)
    {
        b.Entity<OrganizationLocation>(e =>
        {
            e.ToTable("organization_location");
            e.HasKey(x => x.Id);
            e.Property(x => x.OrganizationId).IsRequired();
            e.Property(x => x.Code).HasMaxLength(32).IsRequired();
            e.Property(x => x.Name).HasMaxLength(200).IsRequired();
            e.Property(x => x.LocationFhirId).HasMaxLength(64);
            e.Property(x => x.IsDefault).IsRequired();
            e.Property(x => x.Active).IsRequired();
            e.Property(x => x.CreatedAt).IsRequired();
            e.Property(x => x.UpdatedAt).IsRequired();

            // (org, code) cubre además el listado por organización, que siempre arranca por org.
            e.HasIndex(x => new { x.OrganizationId, x.Code })
                .IsUnique()
                .HasDatabaseName("ux_organization_location_org_code");
            // Único filtrado: garantiza UNA sede principal por organización sin impedir el resto.
            // Ojo: EF identifica los índices por conjunto de propiedades, así que no puede haber
            // otro HasIndex sobre OrganizationId solo — el segundo reconfigura al primero.
            e.HasIndex(x => x.OrganizationId)
                .IsUnique()
                .HasFilter("is_default")
                .HasDatabaseName("ux_organization_location_org_default");
        });

        b.Entity<UserLocationAssignment>(e =>
        {
            e.ToTable("user_location");
            e.HasKey(x => new { x.OrganizationId, x.UserId, x.LocationId });
            e.Property(x => x.IsPrimary).IsRequired();
            e.Property(x => x.AssignedAt).IsRequired();
            e.Property(x => x.AssignedBy);

            e.HasIndex(x => new { x.OrganizationId, x.UserId })
                .HasDatabaseName("ix_user_location_org_user");
            e.HasIndex(x => x.LocationId)
                .HasDatabaseName("ix_user_location_location");
        });
    }

    /// <summary>
    /// Matriz de permisos (ADR-007). <c>organization_id</c> nulo = plantilla global del producto;
    /// con valor = variante de ese tenant, que gana sobre la global al resolver.
    /// </summary>
    private static void ConfigureAuthorization(ModelBuilder b)
    {
        b.Entity<Permission>(e =>
        {
            e.ToTable("permission");
            e.HasKey(x => x.Code);
            e.Property(x => x.Code).HasMaxLength(80);
            e.Property(x => x.Module).HasMaxLength(40).IsRequired();
            e.Property(x => x.Description).HasMaxLength(300).IsRequired();
        });

        b.Entity<RolePermission>(e =>
        {
            e.ToTable("role_permission");
            e.HasKey(x => x.Id);
            e.Property(x => x.OrganizationId);
            e.Property(x => x.RoleCode).HasMaxLength(60).IsRequired();
            e.Property(x => x.PermissionCode).HasMaxLength(80).IsRequired();
            e.Property(x => x.Scope).HasConversion<string>().HasMaxLength(20).IsRequired();
            e.Property(x => x.Denied).IsRequired();
            e.Property(x => x.CreatedAt).IsRequired();

            // Una sola fila por (org, rol, permiso): la variante del tenant reemplaza a la
            // global, no se acumula con ella.
            e.HasIndex(x => new { x.OrganizationId, x.RoleCode, x.PermissionCode })
                .IsUnique()
                .HasDatabaseName("ux_role_permission_org_role_perm");
            e.HasIndex(x => x.RoleCode).HasDatabaseName("ix_role_permission_role");
        });

        b.Entity<FhirAccessRule>(e =>
        {
            e.ToTable("fhir_access_rule");
            e.HasKey(x => x.Id);
            e.Property(x => x.OrganizationId);
            e.Property(x => x.ResourceType).HasMaxLength(64).IsRequired();
            e.Property(x => x.Verb).HasConversion<string>().HasMaxLength(20).IsRequired();
            e.Property(x => x.PermissionCode).HasMaxLength(80).IsRequired();
            e.Property(x => x.CreatedAt).IsRequired();

            e.HasIndex(x => new { x.OrganizationId, x.ResourceType, x.Verb, x.PermissionCode })
                .IsUnique()
                .HasDatabaseName("ux_fhir_access_rule");
            e.HasIndex(x => new { x.ResourceType, x.Verb })
                .HasDatabaseName("ix_fhir_access_rule_resource_verb");
        });

        b.Entity<PolicyVersion>(e =>
        {
            e.ToTable("policy_version");
            // La plantilla global usa el guid cero como clave: Postgres no considera iguales
            // dos NULL en una PK, y necesitamos exactamente una fila global.
            e.HasKey(x => x.OrganizationId);
            e.Property(x => x.OrganizationId).ValueGeneratedNever();
            e.Property(x => x.Version).IsRequired();
            e.Property(x => x.UpdatedAt).IsRequired();
            e.Property(x => x.UpdatedBy);
        });
    }

    private static void ConfigureAppAuditLog(ModelBuilder b)
    {
        b.Entity<AppAuditLogEntry>(e =>
        {
            e.ToTable("app_audit_log");
            e.HasKey(x => x.Id);
            e.Property(x => x.OccurredAt).IsRequired();
            e.Property(x => x.Category).HasMaxLength(50).IsRequired();
            e.Property(x => x.Action).HasMaxLength(80).IsRequired();
            e.Property(x => x.Severity).HasConversion<string>().HasMaxLength(20).IsRequired();
            e.Property(x => x.ActorUserId);
            e.Property(x => x.OrganizationId);
            e.Property(x => x.Target).HasMaxLength(200);
            e.Property(x => x.CorrelationId).HasMaxLength(100);
            e.Property(x => x.RemoteIp).HasMaxLength(45); // IPv6 max length
            e.Property(x => x.Payload).HasColumnType("jsonb").IsRequired();

            // Filtros operativos comunes: por org, por actor, por categoría/acción.
            e.HasIndex(x => new { x.OrganizationId, x.OccurredAt })
                .HasDatabaseName("ix_app_audit_log_org_time");
            e.HasIndex(x => new { x.ActorUserId, x.OccurredAt })
                .HasDatabaseName("ix_app_audit_log_actor_time");
            e.HasIndex(x => new { x.Category, x.Action, x.OccurredAt })
                .HasDatabaseName("ix_app_audit_log_category_time");
        });
    }

    private static void ConfigureFormDefinition(ModelBuilder b)
    {
        b.Entity<FormDefinition>(e =>
        {
            e.ToTable("form_definitions");
            e.HasKey(x => x.Id);
            e.Property(x => x.Code).HasMaxLength(60).IsRequired();
            e.Property(x => x.SpecialtyCode).HasMaxLength(60);
            e.Property(x => x.Version).HasMaxLength(20).IsRequired();
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(20).IsRequired();
            e.Property(x => x.Title).HasMaxLength(200).IsRequired();
            e.Property(x => x.Description);
            e.Property(x => x.SchemaJson).HasColumnType("jsonb").IsRequired();
            e.Property(x => x.FhirMappingJson).HasColumnType("jsonb").IsRequired();
            e.Property(x => x.OrganizationId);
            e.Property(x => x.CreatedAt).IsRequired();
            e.Property(x => x.CreatedBy).IsRequired();
            e.Property(x => x.PublishedAt);
            e.Property(x => x.PublishedBy);

            // Búsqueda principal del médico: por org + specialty + status.
            e.HasIndex(x => new { x.OrganizationId, x.SpecialtyCode, x.Status })
                .HasDatabaseName("ix_form_definitions_org_specialty_status");
            // Unicidad de (code, version, organization_id) para evitar duplicados.
            e.HasIndex(x => new { x.OrganizationId, x.Code, x.Version })
                .IsUnique()
                .HasDatabaseName("ux_form_definitions_org_code_version");
        });
    }

    private static void ConfigureIcd11Code(ModelBuilder b)
    {
        b.Entity<Icd11Code>(e =>
        {
            e.ToTable("icd11_catalog");
            e.HasKey(x => x.Code);
            e.Property(x => x.Code).HasMaxLength(20).IsRequired();
            e.Property(x => x.DisplayEs).HasMaxLength(500).IsRequired();
            e.Property(x => x.DisplayEn).HasMaxLength(500);
            e.Property(x => x.Active).IsRequired();

            // Properties como JSONB. Conversión Dictionary<string,string> ↔ JSON string.
            // El shape del diccionario es responsabilidad del importer (chapter,
            // parent-code, is-leaf, foundation-uri para ICD-11).
            // ValueComparer explícito porque Dictionary es reference type — sin
            // él EF advierte (warning 10620) que no puede detectar cambios in-place.
            // Para nuestro uso (catálogo bulk-load, nunca update), basta deep-equals.
            e.Property(x => x.Properties)
                .HasColumnType("jsonb")
                .HasConversion(
                    v => JsonSerializer.Serialize(v ?? new Dictionary<string, string>(), (JsonSerializerOptions?)null),
                    v => string.IsNullOrEmpty(v)
                        ? new Dictionary<string, string>()
                        : JsonSerializer.Deserialize<Dictionary<string, string>>(v, (JsonSerializerOptions?)null)
                          ?? new Dictionary<string, string>(),
                    new ValueComparer<Dictionary<string, string>>(
                        (a, b) => DictionariesEqual(a, b),
                        v => v == null ? 0 : v.Count,
                        v => v == null ? new Dictionary<string, string>() : new Dictionary<string, string>(v)))
                .IsRequired();

            // tsvector GENERATED — Postgres lo recomputa automáticamente en cada
            // INSERT/UPDATE de display_es/display_en. Indexado con GIN para FTS.
            // Shadow property: el dominio no conoce NpgsqlTsVector, las queries
            // lo acceden con EF.Property<NpgsqlTsVector>(x, "SearchVector").
            //
            // Usa el wrapper IMMUTABLE `imed_search_normalize_es(text,text)` —
            // creado en la migración AddIcd11Catalog. Las funciones core
            // `to_tsvector(text,text)` y `unaccent(text)` son STABLE y Postgres
            // rechaza GENERATED columns que dependan de funciones no-immutable.
            e.Property<NpgsqlTsVector>("SearchVector")
                .HasColumnName("tsv")
                .HasColumnType("tsvector")
                .HasComputedColumnSql(
                    "imed_search_normalize_es(coalesce(display_es,''), coalesce(display_en,''))",
                    stored: true);

            e.HasIndex("SearchVector")
                .HasMethod("gin")
                .HasDatabaseName("ix_icd11_search_vector");

            // pg_trgm sobre code para tolerar typos en typeahead por código.
            e.HasIndex(x => x.Code)
                .HasDatabaseName("ix_icd11_code_trgm")
                .HasMethod("gin")
                .HasOperators("gin_trgm_ops");

            e.HasIndex(x => x.Active).HasDatabaseName("ix_icd11_active");
        });
    }

    private static void ConfigureLoincCode(ModelBuilder b)
    {
        b.Entity<LoincCode>(e =>
        {
            e.ToTable("loinc_catalog");
            e.HasKey(x => x.Code);
            e.Property(x => x.Code).HasMaxLength(20).IsRequired();
            // LOINC LONG_COMMON_NAME puede llegar a 200+ chars — más holgado que ICD-11.
            e.Property(x => x.DisplayEs).HasMaxLength(800).IsRequired();
            e.Property(x => x.DisplayEn).HasMaxLength(800);
            e.Property(x => x.Active).IsRequired();

            // Properties JSONB — mismo patrón que Icd11. Reusa el ValueComparer
            // y conversión de Dictionary<string,string>.
            e.Property(x => x.Properties)
                .HasColumnType("jsonb")
                .HasConversion(
                    v => JsonSerializer.Serialize(v ?? new Dictionary<string, string>(), (JsonSerializerOptions?)null),
                    v => string.IsNullOrEmpty(v)
                        ? new Dictionary<string, string>()
                        : JsonSerializer.Deserialize<Dictionary<string, string>>(v, (JsonSerializerOptions?)null)
                          ?? new Dictionary<string, string>(),
                    new ValueComparer<Dictionary<string, string>>(
                        (a, b) => DictionariesEqual(a, b),
                        v => v == null ? 0 : v.Count,
                        v => v == null ? new Dictionary<string, string>() : new Dictionary<string, string>(v)))
                .IsRequired();

            // Reusa el wrapper IMMUTABLE creado en AddIcd11Catalog.
            // Mismo patrón que ICD-11: shadow property, GIN sobre tsv.
            e.Property<NpgsqlTsVector>("SearchVector")
                .HasColumnName("tsv")
                .HasColumnType("tsvector")
                .HasComputedColumnSql(
                    "imed_search_normalize_es(coalesce(display_es,''), coalesce(display_en,''))",
                    stored: true);

            e.HasIndex("SearchVector")
                .HasMethod("gin")
                .HasDatabaseName("ix_loinc_search_vector");

            // pg_trgm sobre code para tolerar typos en typeahead por código LOINC.
            e.HasIndex(x => x.Code)
                .HasDatabaseName("ix_loinc_code_trgm")
                .HasMethod("gin")
                .HasOperators("gin_trgm_ops");

            e.HasIndex(x => x.Active).HasDatabaseName("ix_loinc_active");
        });
    }

    private static void ConfigureSpecialtyCode(ModelBuilder b)
    {
        b.Entity<SpecialtyCode>(e =>
        {
            e.ToTable("specialty_catalog");
            e.HasKey(x => x.Code);
            e.Property(x => x.Code).HasMaxLength(60).IsRequired();
            e.Property(x => x.DisplayEs).HasMaxLength(200).IsRequired();
            e.Property(x => x.System).HasMaxLength(120).IsRequired();
            e.Property(x => x.Active).IsRequired();

            // Typeahead por display: pg_trgm GIN igual que icd10.
            e.HasIndex(x => x.DisplayEs)
                .HasDatabaseName("ix_specialty_display_trgm")
                .HasMethod("gin")
                .HasOperators("gin_trgm_ops");
            e.HasIndex(x => x.Active).HasDatabaseName("ix_specialty_active");
        });
    }

    private static void ConfigureAllergyCatalogEntry(ModelBuilder b)
    {
        b.Entity<AllergyCatalogEntry>(e =>
        {
            e.ToTable("allergy_catalog");
            e.HasKey(x => x.Code);
            e.Property(x => x.Code).HasMaxLength(60).IsRequired();
            e.Property(x => x.DisplayEs).HasMaxLength(300).IsRequired();
            e.Property(x => x.Category).HasMaxLength(30).IsRequired();
            e.Property(x => x.Active).IsRequired();

            // Typeahead por display: pg_trgm GIN. Volumen del slice (~25-50) hace
            // que un index trgm sea suficiente; si crece a cientos pasamos a tsv
            // como ICD-11. Filtro por categoría es exact match.
            e.HasIndex(x => x.DisplayEs)
                .HasDatabaseName("ix_allergy_display_trgm")
                .HasMethod("gin")
                .HasOperators("gin_trgm_ops");
            e.HasIndex(x => x.Active).HasDatabaseName("ix_allergy_active");
            e.HasIndex(x => x.Category).HasDatabaseName("ix_allergy_category");
        });
    }

    private static void ConfigureMedicationCatalogEntry(ModelBuilder b)
    {
        b.Entity<MedicationCatalogEntry>(e =>
        {
            e.ToTable("medication_catalog");
            e.HasKey(x => x.AtcCode);
            e.Property(x => x.AtcCode).HasMaxLength(20).IsRequired();
            e.Property(x => x.NameGeneric).HasMaxLength(200).IsRequired();
            e.Property(x => x.NameCommercial).HasMaxLength(200);
            e.Property(x => x.Presentation).HasMaxLength(120);
            e.Property(x => x.Category).HasMaxLength(40).IsRequired();
            e.Property(x => x.Active).IsRequired();

            // Volumen del slice (~50 entries iniciales) hace que un index trgm
            // sobre name_generic alcance para typeahead. Cuando crezca a miles,
            // mover a tsv como ICD-11/LOINC.
            e.HasIndex(x => x.NameGeneric)
                .HasDatabaseName("ix_medication_name_generic_trgm")
                .HasMethod("gin")
                .HasOperators("gin_trgm_ops");
            e.HasIndex(x => x.Active).HasDatabaseName("ix_medication_active");
            e.HasIndex(x => x.Category).HasDatabaseName("ix_medication_category");
        });
    }

    private static void ConfigureUserPractitionerAssignment(ModelBuilder b)
    {
        b.Entity<UserPractitionerAssignment>(e =>
        {
            e.ToTable("user_practitioner_assignments");
            // Un user puede tener Practitioner distinto por org → PK compuesta.
            e.HasKey(x => new { x.KeycloakUserId, x.OrganizationId });
            e.Property(x => x.PractitionerFhirId).HasMaxLength(64).IsRequired();
            e.Property(x => x.PractitionerRoleFhirId).HasMaxLength(64);
            e.Property(x => x.CreatedAt).IsRequired();
            e.Property(x => x.UpdatedAt).IsRequired();

            // Lookup auxiliar: dado un PractitionerFhirId, ¿de qué user es?
            // (no es búsqueda crítica pero ayuda en debugging y reverse-mapping).
            e.HasIndex(x => x.PractitionerFhirId)
                .HasDatabaseName("ix_user_practitioner_assignments_fhir_id");
        });
    }

    private static void ConfigureInvitation(ModelBuilder b)
    {
        b.Entity<Invitation>(e =>
        {
            e.ToTable("invitations");
            e.HasKey(x => x.Id);
            e.Property(x => x.Email).HasMaxLength(320).IsRequired(); // RFC 5321 max
            e.Property(x => x.OrganizationId).IsRequired();
            e.Property(x => x.OrganizationFhirId).HasMaxLength(64).IsRequired();
            e.Property(x => x.Role).HasConversion<string>().HasMaxLength(20).IsRequired();
            e.Property(x => x.SpecialtyHint).HasMaxLength(60);
            e.Property(x => x.KeycloakUserId);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(20).IsRequired();
            e.Property(x => x.CreatedAt).IsRequired();
            e.Property(x => x.CreatedBy).IsRequired();
            e.Property(x => x.AcceptedAt);
            e.Property(x => x.RevokedAt);
            e.Property(x => x.RevokedBy);

            // Lookup principal del lazy provision: dado el email del JWT y el
            // organization_id, ¿hay invitación pending?
            e.HasIndex(x => new { x.OrganizationId, x.Email })
                .HasDatabaseName("ix_invitations_org_email");

            // Listado de pendings por org (admin UI). Filtro por status pending
            // optimiza la query más común.
            e.HasIndex(x => new { x.OrganizationId, x.Status, x.CreatedAt })
                .HasDatabaseName("ix_invitations_org_status_created");

            // Un solo invite pending por (email, org). Estados terminales no
            // ocupan el slot — permite re-invitar tras revoke/expire.
            e.HasIndex(x => new { x.Email, x.OrganizationId })
                .IsUnique()
                .HasFilter("status = 'Pending'")
                .HasDatabaseName("ux_invitations_email_org_pending");
        });
    }

    private static void ConfigureJoinRequest(ModelBuilder b)
    {
        b.Entity<JoinRequest>(e =>
        {
            e.ToTable("join_requests");
            e.HasKey(x => x.Id);
            e.Property(x => x.Email).HasMaxLength(320).IsRequired();
            e.Property(x => x.FullName).HasMaxLength(200).IsRequired();
            e.Property(x => x.OrganizationId).IsRequired();
            e.Property(x => x.OrganizationFhirId).HasMaxLength(64).IsRequired();
            e.Property(x => x.Message).HasMaxLength(2000);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(20).IsRequired();
            e.Property(x => x.CreatedAt).IsRequired();
            e.Property(x => x.ResolvedAt);
            e.Property(x => x.ResolvedBy);
            e.Property(x => x.RejectionReason).HasMaxLength(1000);
            e.Property(x => x.LinkedInvitationId);

            // Listado para el admin: pendings de la org ordenadas por fecha.
            e.HasIndex(x => new { x.OrganizationId, x.Status, x.CreatedAt })
                .HasDatabaseName("ix_join_requests_org_status_created");

            // Una sola request pending por (email, org). Estados terminales no
            // ocupan el slot — permite re-solicitar tras rechazo.
            e.HasIndex(x => new { x.Email, x.OrganizationId })
                .IsUnique()
                .HasFilter("status = 'Pending'")
                .HasDatabaseName("ux_join_requests_email_org_pending");
        });
    }

    private static void ConfigureLabTestDefinition(ModelBuilder b)
    {
        b.Entity<LabTestDefinition>(e =>
        {
            e.ToTable("lab_test");
            e.HasKey(x => x.Id);
            e.Property(x => x.OrganizationId);
            e.Property(x => x.Code).HasMaxLength(60).IsRequired();
            e.Property(x => x.LoincCode).HasMaxLength(20);
            e.Property(x => x.Name).HasMaxLength(300).IsRequired();
            e.Property(x => x.Datatype).HasConversion<string>().HasMaxLength(20).IsRequired();
            e.Property(x => x.Unit).HasMaxLength(40);
            e.Property(x => x.Department).HasConversion<string>().HasMaxLength(20).IsRequired();
            e.Property(x => x.FastingHours);
            e.Property(x => x.CollectionType).HasMaxLength(40);
            e.Property(x => x.PatientInstruction).HasMaxLength(1000);
            e.Property(x => x.SpecimenTypeId);
            e.Property(x => x.TubeTypeId);
            e.Property(x => x.CollectionMode).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.MinVolumeMl).HasPrecision(6, 2);
            e.Property(x => x.PreferredVolumeMl).HasPrecision(6, 2);
            e.Property(x => x.AnalyzerMinVolumeMl).HasPrecision(6, 2);
            e.Property(x => x.StabilityMinutes);
            e.Property(x => x.Active).IsRequired();
            e.Property(x => x.CreatedAt).IsRequired();
            e.Property(x => x.UpdatedAt).IsRequired();

            // (org, code) único — null-org = plantilla global. Postgres trata NULLs
            // como distintos, así que el unique no colisiona entre orgs ni con el global.
            e.HasIndex(x => new { x.OrganizationId, x.Code })
                .IsUnique()
                .HasDatabaseName("ux_lab_test_org_code");
            // Typeahead/filtro por nombre — pg_trgm (volumen chico, como medication).
            e.HasIndex(x => x.Name)
                .HasDatabaseName("ix_lab_test_name_trgm")
                .HasMethod("gin")
                .HasOperators("gin_trgm_ops");
            e.HasIndex(x => new { x.OrganizationId, x.Active })
                .HasDatabaseName("ix_lab_test_org_active");
        });
    }

    private static void ConfigureLabTestOffering(ModelBuilder b)
    {
        b.Entity<LabTestOffering>(e =>
        {
            e.ToTable("lab_test_offering");
            e.HasKey(x => new { x.OrganizationId, x.LabTestId });
            e.Property(x => x.Offered).IsRequired();
            e.Property(x => x.UpdatedAt).IsRequired();
        });
    }

    private static void ConfigureLabPanel(ModelBuilder b)
    {
        b.Entity<LabPanel>(e =>
        {
            e.ToTable("lab_panel");
            e.HasKey(x => x.Id);
            e.Property(x => x.OrganizationId);
            e.Property(x => x.Code).HasMaxLength(60).IsRequired();
            e.Property(x => x.Name).HasMaxLength(300).IsRequired();
            e.Property(x => x.Description).HasMaxLength(1000);
            e.Property(x => x.LoincCode).HasMaxLength(20);
            e.Property(x => x.Active).IsRequired();
            e.Property(x => x.CreatedAt).IsRequired();
            e.Property(x => x.UpdatedAt).IsRequired();

            e.HasIndex(x => new { x.OrganizationId, x.Code })
                .IsUnique()
                .HasDatabaseName("ux_lab_panel_org_code");
            e.HasIndex(x => x.Name)
                .HasDatabaseName("ix_lab_panel_name_trgm")
                .HasMethod("gin")
                .HasOperators("gin_trgm_ops");
        });
    }

    private static void ConfigureLabPanelVersion(ModelBuilder b)
    {
        b.Entity<LabPanelVersion>(e =>
        {
            e.ToTable("lab_panel_version");
            e.HasKey(x => x.Id);
            e.Property(x => x.LabPanelId).IsRequired();
            e.Property(x => x.Version).IsRequired();
            e.Property(x => x.EffectiveDate).IsRequired();
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(20).IsRequired();
            e.Property(x => x.CreatedAt).IsRequired();

            e.HasIndex(x => new { x.LabPanelId, x.Version })
                .IsUnique()
                .HasDatabaseName("ux_lab_panel_version_panel_version");
        });
    }

    private static void ConfigureLabPanelVersionItem(ModelBuilder b)
    {
        b.Entity<LabPanelVersionItem>(e =>
        {
            e.ToTable("lab_panel_version_item");
            e.HasKey(x => new { x.LabPanelVersionId, x.LabTestId });
            e.Property(x => x.DisplayOrder).IsRequired();

            e.HasIndex(x => x.LabPanelVersionId)
                .HasDatabaseName("ix_lab_panel_version_item_version");
        });
    }

    private static void ConfigureReferenceRange(ModelBuilder b)
    {
        b.Entity<ReferenceRange>(e =>
        {
            e.ToTable("reference_range");
            e.HasKey(x => x.Id);
            e.Property(x => x.OrganizationId);
            e.Property(x => x.LabTestId).IsRequired();
            e.Property(x => x.Sex).HasConversion<string>().HasMaxLength(10).IsRequired();
            e.Property(x => x.AgeMinYears);
            e.Property(x => x.AgeMaxYears);
            e.Property(x => x.Pregnancy);
            e.Property(x => x.Method).HasMaxLength(120);
            e.Property(x => x.Low).HasPrecision(12, 4);
            e.Property(x => x.High).HasPrecision(12, 4);
            e.Property(x => x.CriticalLow).HasPrecision(12, 4);
            e.Property(x => x.CriticalHigh).HasPrecision(12, 4);
            e.Property(x => x.Unit).HasMaxLength(40);
            e.Property(x => x.CreatedAt).IsRequired();
            e.Property(x => x.UpdatedAt).IsRequired();

            e.HasIndex(x => x.LabTestId)
                .HasDatabaseName("ix_reference_range_test");
        });
    }

    private static void ConfigureDerivedCalculation(ModelBuilder b)
    {
        b.Entity<DerivedCalculation>(e =>
        {
            e.ToTable("derived_calculation");
            e.HasKey(x => x.Id);
            e.Property(x => x.OrganizationId);
            e.Property(x => x.OutputTestCode).HasMaxLength(60).IsRequired();
            e.Property(x => x.Name).HasMaxLength(200).IsRequired();
            e.Property(x => x.FormulaJson).HasColumnType("jsonb").IsRequired();
            e.Property(x => x.InputsJson).HasColumnType("jsonb").IsRequired();
            e.Property(x => x.Active).IsRequired();
            e.Property(x => x.CreatedAt).IsRequired();

            e.HasIndex(x => x.OutputTestCode)
                .HasDatabaseName("ix_derived_calculation_output");
        });
    }

    private static void ConfigureTatTarget(ModelBuilder b)
    {
        b.Entity<TatTarget>(e =>
        {
            e.ToTable("tat_target");
            e.HasKey(x => x.Id);
            e.Property(x => x.OrganizationId);
            e.Property(x => x.Scope).HasConversion<string>().HasMaxLength(10).IsRequired();
            e.Property(x => x.TargetCode).HasMaxLength(60).IsRequired();
            e.Property(x => x.Priority).HasConversion<string>().HasMaxLength(20).IsRequired();
            e.Property(x => x.Minutes).IsRequired();
            e.Property(x => x.CreatedAt).IsRequired();

            e.HasIndex(x => new { x.Scope, x.TargetCode, x.Priority })
                .HasDatabaseName("ix_tat_target_scope_code_priority");
        });
    }

    private static void ConfigureSpecimenType(ModelBuilder b)
    {
        b.Entity<SpecimenType>(e =>
        {
            e.ToTable("specimen_type");
            e.HasKey(x => x.Id);
            e.Property(x => x.OrganizationId);
            e.Property(x => x.Code).HasMaxLength(40).IsRequired();
            e.Property(x => x.Name).HasMaxLength(200).IsRequired();
            e.Property(x => x.SnomedCode).HasMaxLength(40);
            e.Property(x => x.CollectionMode).HasConversion<string>().HasMaxLength(20).IsRequired();
            e.Property(x => x.DefaultMethod).HasMaxLength(120);
            e.Property(x => x.DefaultBodySite).HasMaxLength(120);
            e.Property(x => x.ContainerKind).HasConversion<string>().HasMaxLength(20).IsRequired();
            e.Property(x => x.StabilityMinutes);
            e.Property(x => x.Discipline).HasConversion<string>().HasMaxLength(20).IsRequired();
            e.Property(x => x.Active).IsRequired();
            e.Property(x => x.CreatedAt).IsRequired();
            e.Property(x => x.UpdatedAt).IsRequired();

            e.HasIndex(x => new { x.OrganizationId, x.Code })
                .IsUnique()
                .HasDatabaseName("ux_specimen_type_org_code");
        });
    }

    private static void ConfigureTubeType(ModelBuilder b)
    {
        b.Entity<TubeType>(e =>
        {
            e.ToTable("tube_type");
            e.HasKey(x => x.Id);
            e.Property(x => x.OrganizationId);
            e.Property(x => x.Code).HasMaxLength(40).IsRequired();
            e.Property(x => x.Name).HasMaxLength(200).IsRequired();
            e.Property(x => x.Color).HasMaxLength(40);
            e.Property(x => x.Additive).HasMaxLength(120);
            e.Property(x => x.VolumeMinMl).HasPrecision(6, 2);
            e.Property(x => x.CapacityMl).HasPrecision(6, 2);
            e.Property(x => x.DeadVolumeMl).HasPrecision(6, 2);
            e.Property(x => x.Active).IsRequired();
            e.Property(x => x.CreatedAt).IsRequired();
            e.Property(x => x.UpdatedAt).IsRequired();

            e.HasIndex(x => new { x.OrganizationId, x.Code })
                .IsUnique()
                .HasDatabaseName("ux_tube_type_org_code");
        });
    }

    private static void ConfigureSpecimenRejectionReason(ModelBuilder b)
    {
        b.Entity<SpecimenRejectionReason>(e =>
        {
            e.ToTable("specimen_rejection_reason");
            e.HasKey(x => x.Id);
            e.Property(x => x.OrganizationId);
            e.Property(x => x.Code).HasMaxLength(40).IsRequired();
            e.Property(x => x.Name).HasMaxLength(200).IsRequired();
            e.Property(x => x.Description).HasMaxLength(500);
            e.Property(x => x.RequiresNewSample).IsRequired();
            e.Property(x => x.SortOrder).IsRequired();
            e.Property(x => x.Active).IsRequired();
            e.Property(x => x.CreatedAt).IsRequired();
            e.Property(x => x.UpdatedAt).IsRequired();

            e.HasIndex(x => new { x.OrganizationId, x.Code })
                .IsUnique()
                .HasDatabaseName("ux_specimen_rejection_reason_org_code");
        });
    }

    private static void ConfigureValidationRejection(ModelBuilder b)
    {
        b.Entity<ValidationRejectionReason>(e =>
        {
            e.ToTable("validation_rejection_reason");
            e.HasKey(x => x.Id);
            e.Property(x => x.OrganizationId);
            e.Property(x => x.Code).HasMaxLength(40).IsRequired();
            e.Property(x => x.Name).HasMaxLength(200).IsRequired();
            e.Property(x => x.Description).HasMaxLength(500);
            e.Property(x => x.RequiresNewSample).IsRequired();
            e.Property(x => x.SortOrder).IsRequired();
            e.Property(x => x.Active).IsRequired();
            e.Property(x => x.CreatedAt).IsRequired();
            e.Property(x => x.UpdatedAt).IsRequired();

            e.HasIndex(x => new { x.OrganizationId, x.Code })
                .IsUnique()
                .HasDatabaseName("ux_validation_rejection_reason_org_code");
        });

        b.Entity<ResultValidationRejection>(e =>
        {
            e.ToTable("result_validation_rejection");
            e.HasKey(x => x.Id);
            e.Property(x => x.OrganizationId).IsRequired();
            e.Property(x => x.ReportFhirId).HasMaxLength(64).IsRequired();
            e.Property(x => x.ServiceRequestFhirId).HasMaxLength(64);
            e.Property(x => x.ReasonId).IsRequired();
            e.Property(x => x.Note).HasMaxLength(1000);
            e.Property(x => x.RejectedBy).IsRequired();
            e.Property(x => x.RejectedAt).IsRequired();
            e.Property(x => x.ResolvedAt);

            // La cola de validación pregunta "¿este informe tiene una devolución abierta?"
            // en cada carga: se indexa por informe y por resolución.
            e.HasIndex(x => new { x.OrganizationId, x.ReportFhirId, x.ResolvedAt })
                .HasDatabaseName("ix_result_validation_rejection_report");
        });
    }

    private static void ConfigureLabLabelFormat(ModelBuilder b)
    {
        b.Entity<LabLabelFormat>(e =>
        {
            e.ToTable("lab_label_format");
            e.HasKey(x => x.Id);
            e.Property(x => x.OrganizationId);
            e.Property(x => x.Code).HasMaxLength(40).IsRequired();
            e.Property(x => x.Name).HasMaxLength(200).IsRequired();
            e.Property(x => x.WidthMm).HasPrecision(6, 2).IsRequired();
            e.Property(x => x.HeightMm).HasPrecision(6, 2).IsRequired();
            e.Property(x => x.MarginMm).HasPrecision(6, 2).IsRequired();
            e.Property(x => x.OffsetXMm).HasPrecision(6, 2).IsRequired();
            e.Property(x => x.OffsetYMm).HasPrecision(6, 2).IsRequired();
            e.Property(x => x.IsDefault).IsRequired();
            e.Property(x => x.SortOrder).IsRequired();
            e.Property(x => x.Active).IsRequired();
            e.Property(x => x.CreatedAt).IsRequired();
            e.Property(x => x.UpdatedAt).IsRequired();

            e.HasIndex(x => new { x.OrganizationId, x.Code })
                .IsUnique()
                .HasDatabaseName("ux_lab_label_format_org_code");
        });
    }

    private static void ConfigureLabLabelFormatAssignment(ModelBuilder b)
    {
        b.Entity<LabLabelFormatAssignment>(e =>
        {
            e.ToTable("lab_label_format_assignment");
            e.HasKey(x => x.Id);
            e.Property(x => x.OrganizationId);
            e.Property(x => x.TubeTypeCode).HasMaxLength(40);
            e.Property(x => x.SpecimenTypeCode).HasMaxLength(40);
            e.Property(x => x.LabLabelFormatId).IsRequired();
            e.Property(x => x.CreatedAt).IsRequired();
            e.Property(x => x.UpdatedAt).IsRequired();

            e.HasOne<LabLabelFormat>()
                .WithMany()
                .HasForeignKey(x => x.LabLabelFormatId)
                .OnDelete(DeleteBehavior.Cascade);

            // Una sola asignación por (org, tubo) y por (org, tipo de muestra). Los índices
            // son filtrados porque solo una de las dos columnas está poblada por fila.
            e.HasIndex(x => new { x.OrganizationId, x.TubeTypeCode })
                .IsUnique()
                .HasFilter("tube_type_code IS NOT NULL")
                .HasDatabaseName("ux_lab_label_assignment_org_tube");

            e.HasIndex(x => new { x.OrganizationId, x.SpecimenTypeCode })
                .IsUnique()
                .HasFilter("specimen_type_code IS NOT NULL")
                .HasDatabaseName("ux_lab_label_assignment_org_specimen");
        });
    }

    private static void ConfigureCollectionPlan(ModelBuilder b)
    {
        b.Entity<CollectionPlan>(e =>
        {
            e.ToTable("collection_plan");
            e.HasKey(x => x.Id);
            e.Property(x => x.OrganizationId).IsRequired();
            e.Property(x => x.LocationId);
            e.Property(x => x.Requisition).HasMaxLength(64).IsRequired();
            e.Property(x => x.EncounterFhirId).HasMaxLength(64);
            e.Property(x => x.PatientFhirId).HasMaxLength(64).IsRequired();
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(30).IsRequired();
            e.Property(x => x.ComputedBy);
            e.Property(x => x.CreatedAt).IsRequired();
            e.Property(x => x.UpdatedAt).IsRequired();

            e.HasMany(x => x.Containers)
                .WithOne()
                .HasForeignKey(c => c.CollectionPlanId)
                .OnDelete(DeleteBehavior.Cascade);

            e.HasMany(x => x.UnavailableItems)
                .WithOne()
                .HasForeignKey(i => i.CollectionPlanId)
                .OnDelete(DeleteBehavior.Cascade);

            // Un plan por (org, requisición) — recalcular reemplaza el anterior.
            e.HasIndex(x => new { x.OrganizationId, x.Requisition })
                .IsUnique()
                .HasDatabaseName("ux_collection_plan_org_requisition");
        });
    }

    private static void ConfigureCollectionPlanUnavailableItem(ModelBuilder b)
    {
        b.Entity<CollectionPlanUnavailableItem>(e =>
        {
            e.ToTable("collection_plan_unavailable_item");
            e.HasKey(x => x.Id);
            e.Property(x => x.CollectionPlanId).IsRequired();
            e.Property(x => x.ServiceRequestFhirId).HasMaxLength(64).IsRequired();
            e.Property(x => x.LoincCode).HasMaxLength(20);
            e.Property(x => x.Display).HasMaxLength(300).IsRequired();
            e.Property(x => x.Reason).HasMaxLength(40).IsRequired();

            e.HasIndex(x => x.CollectionPlanId)
                .HasDatabaseName("ix_collection_plan_unavailable_plan");
        });
    }

    private static void ConfigureCollectionPlanContainer(ModelBuilder b)
    {
        b.Entity<CollectionPlanContainer>(e =>
        {
            e.ToTable("collection_plan_container");
            e.HasKey(x => x.Id);
            e.Property(x => x.CollectionPlanId).IsRequired();
            e.Property(x => x.Sequence).IsRequired();
            e.Property(x => x.SpecimenTypeCode).HasMaxLength(40).IsRequired();
            e.Property(x => x.SpecimenTypeName).HasMaxLength(200);
            e.Property(x => x.TubeTypeCode).HasMaxLength(40);
            e.Property(x => x.TubeTypeName).HasMaxLength(200);
            e.Property(x => x.Additive).HasMaxLength(120);
            e.Property(x => x.RequiredVolumeMl).HasPrecision(7, 2);
            e.Property(x => x.AccessionNumber).HasMaxLength(40);
            e.Property(x => x.Barcode).HasMaxLength(64);
            e.Property(x => x.SpecimenFhirId).HasMaxLength(64);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(30).IsRequired();
            e.Property(x => x.CollectedAt);
            e.Property(x => x.ReceivedAt);
            e.Property(x => x.StorageLocation).HasMaxLength(120);
            e.Property(x => x.RejectionReasonCode).HasMaxLength(40);

            e.HasMany(x => x.Items)
                .WithOne()
                .HasForeignKey(i => i.CollectionPlanContainerId)
                .OnDelete(DeleteBehavior.Cascade);

            e.HasIndex(x => x.CollectionPlanId)
                .HasDatabaseName("ix_collection_plan_container_plan");
        });
    }

    private static void ConfigureCollectionEvent(ModelBuilder b)
    {
        b.Entity<CollectionEvent>(e =>
        {
            e.ToTable("collection_event");
            e.HasKey(x => x.Id);
            e.Property(x => x.CollectionPlanContainerId).IsRequired();
            e.Property(x => x.Type).HasConversion<string>().HasMaxLength(30).IsRequired();
            e.Property(x => x.At).IsRequired();
            e.Property(x => x.By);
            e.Property(x => x.Detail).HasMaxLength(500);

            // Cascade desde el contenedor: recalcular el plan limpia su timeline.
            e.HasOne<CollectionPlanContainer>()
                .WithMany()
                .HasForeignKey(x => x.CollectionPlanContainerId)
                .OnDelete(DeleteBehavior.Cascade);

            e.HasIndex(x => x.CollectionPlanContainerId)
                .HasDatabaseName("ix_collection_event_container");
        });
    }

    private static void ConfigureCollectionPlanItem(ModelBuilder b)
    {
        b.Entity<CollectionPlanItem>(e =>
        {
            e.ToTable("collection_plan_item");
            e.HasKey(x => x.Id);
            e.Property(x => x.CollectionPlanContainerId).IsRequired();
            e.Property(x => x.ServiceRequestFhirId).HasMaxLength(64).IsRequired();
            e.Property(x => x.LabTestCode).HasMaxLength(60).IsRequired();
            e.Property(x => x.LabTestName).HasMaxLength(300);

            e.HasIndex(x => x.CollectionPlanContainerId)
                .HasDatabaseName("ix_collection_plan_item_container");
        });
    }

    private static void ConfigureOrderIntake(ModelBuilder b)
    {
        b.Entity<OrderIntake>(e =>
        {
            e.ToTable("order_intake");
            e.HasKey(x => x.Id);
            e.Property(x => x.OrganizationId).IsRequired();
            e.Property(x => x.LocationId);
            e.Property(x => x.ServiceRequestFhirId).HasMaxLength(64).IsRequired();
            e.Property(x => x.SpecimenFhirId).HasMaxLength(64);
            e.Property(x => x.PatientFhirId).HasMaxLength(64).IsRequired();
            e.Property(x => x.Requisition).HasMaxLength(64);
            e.Property(x => x.SpecimenTypeCode).HasMaxLength(40);
            e.Property(x => x.TubeCode).HasMaxLength(40);
            e.Property(x => x.ExamLoincCode).HasMaxLength(40);
            e.Property(x => x.ExamText).HasMaxLength(300);
            e.Property(x => x.Routing).HasConversion<string>().HasMaxLength(20).IsRequired();
            e.Property(x => x.Priority).HasConversion<string>().HasMaxLength(20).IsRequired();
            e.Property(x => x.ScheduledFor);
            e.Property(x => x.IsWalkIn).IsRequired();
            e.Property(x => x.FastingRequired).IsRequired();
            e.Property(x => x.FastingSatisfied);
            e.Property(x => x.AccessionNumber).HasMaxLength(40);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(20).IsRequired();
            e.Property(x => x.BlockReason).HasMaxLength(500);
            e.Property(x => x.ReportFhirId).HasMaxLength(64);
            e.Property(x => x.ValidatedByPractitionerFhirId).HasMaxLength(64);
            e.Property(x => x.ValidatedAt);
            e.Property(x => x.ReceivedBy);
            e.Property(x => x.ReceivedAt);
            e.Property(x => x.CreatedAt).IsRequired();
            e.Property(x => x.UpdatedAt).IsRequired();

            // Worklist: por org + estado + prioridad.
            e.HasIndex(x => new { x.OrganizationId, x.Status })
                .HasDatabaseName("ix_order_intake_org_status");
            e.HasIndex(x => x.ServiceRequestFhirId)
                .HasDatabaseName("ix_order_intake_service_request");
            e.HasIndex(x => x.PatientFhirId)
                .HasDatabaseName("ix_order_intake_patient");
            e.HasIndex(x => x.AccessionNumber)
                .HasDatabaseName("ix_order_intake_accession");
        });
    }

    private static void ConfigureSpecimenEvent(ModelBuilder b)
    {
        b.Entity<SpecimenEvent>(e =>
        {
            e.ToTable("specimen_event");
            e.HasKey(x => x.Id);
            e.Property(x => x.OrderIntakeId).IsRequired();
            e.Property(x => x.EventType).HasConversion<string>().HasMaxLength(20).IsRequired();
            e.Property(x => x.OccurredAt).IsRequired();
            e.Property(x => x.PerformedBy);
            e.Property(x => x.Incident).HasConversion<string>().HasMaxLength(30);
            e.Property(x => x.Notes).HasMaxLength(1000);

            e.HasIndex(x => x.OrderIntakeId)
                .HasDatabaseName("ix_specimen_event_intake");
        });
    }

    private static void ConfigureLabAccessionCounter(ModelBuilder b)
    {
        b.Entity<LabAccessionCounter>(e =>
        {
            e.ToTable("lab_accession_counter");
            e.HasKey(x => new { x.OrganizationId, x.LocationId, x.Day });
            e.Property(x => x.Seq).IsRequired();
        });
    }

    private static void ConfigureLabOrderCounter(ModelBuilder b)
    {
        b.Entity<LabOrderCounter>(e =>
        {
            e.ToTable("lab_order_counter");
            e.HasKey(x => new { x.OrganizationId, x.LocationId, x.Day });
            e.Property(x => x.Seq).IsRequired();
        });
    }

    private static void ConfigureLabOrderCheckIn(ModelBuilder b)
    {
        b.Entity<LabOrderCheckIn>(e =>
        {
            e.ToTable("lab_order_checkin");
            e.HasKey(x => new { x.OrganizationId, x.Requisition });
            e.Property(x => x.Requisition).HasMaxLength(64).IsRequired();
            e.Property(x => x.LocationId);
            e.Property(x => x.CheckedInAt).IsRequired();
            e.Property(x => x.CheckedInBy).IsRequired();
        });
    }

    private static void ConfigureLabRule(ModelBuilder b)
    {
        b.Entity<LabRule>(e =>
        {
            e.ToTable("lab_rule");
            e.HasKey(x => x.Id);
            e.Property(x => x.OrganizationId);
            e.Property(x => x.Code).HasMaxLength(60).IsRequired();
            e.Property(x => x.Name).HasMaxLength(200).IsRequired();
            e.Property(x => x.Scope).HasConversion<string>().HasMaxLength(10).IsRequired();
            e.Property(x => x.TargetLoinc).HasMaxLength(20);
            e.Property(x => x.Action).HasConversion<string>().HasMaxLength(20).IsRequired();
            e.Property(x => x.Severity).HasConversion<string>().HasMaxLength(20).IsRequired();
            e.Property(x => x.Message).HasMaxLength(500);
            e.Property(x => x.Active).IsRequired();
            e.Property(x => x.CreatedAt).IsRequired();
            e.Property(x => x.UpdatedAt).IsRequired();

            e.HasIndex(x => new { x.OrganizationId, x.Code })
                .IsUnique().HasDatabaseName("ux_lab_rule_org_code");
            e.HasIndex(x => new { x.OrganizationId, x.Scope, x.TargetLoinc, x.Active })
                .HasDatabaseName("ix_lab_rule_lookup");
        });
    }

    private static void ConfigureLabRuleVersion(ModelBuilder b)
    {
        b.Entity<LabRuleVersion>(e =>
        {
            e.ToTable("lab_rule_version");
            e.HasKey(x => x.Id);
            e.Property(x => x.LabRuleId).IsRequired();
            e.Property(x => x.Version).IsRequired();
            e.Property(x => x.LogicJson).HasColumnType("jsonb").IsRequired();
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(20).IsRequired();
            e.Property(x => x.EffectiveDate).IsRequired();
            e.Property(x => x.CreatedAt).IsRequired();

            e.HasIndex(x => new { x.LabRuleId, x.Version })
                .IsUnique().HasDatabaseName("ux_lab_rule_version_rule_version");
        });
    }

    private static void ConfigureRuleExecution(ModelBuilder b)
    {
        b.Entity<RuleExecution>(e =>
        {
            e.ToTable("rule_execution");
            e.HasKey(x => x.Id);
            e.Property(x => x.OrganizationId).IsRequired();
            e.Property(x => x.ObservationFhirId).HasMaxLength(64).IsRequired();
            e.Property(x => x.LabRuleId).IsRequired();
            e.Property(x => x.RuleVersion).IsRequired();
            e.Property(x => x.Fired).IsRequired();
            e.Property(x => x.Verdict).HasConversion<string>().HasMaxLength(20).IsRequired();
            e.Property(x => x.EvaluatedAt).IsRequired();

            e.HasIndex(x => x.ObservationFhirId).HasDatabaseName("ix_rule_execution_observation");
        });
    }

    private static bool DictionariesEqual(Dictionary<string, string>? a, Dictionary<string, string>? b)
    {
        if (ReferenceEquals(a, b))
        {
            return true;
        }
        if (a is null || b is null)
        {
            return false;
        }
        if (a.Count != b.Count)
        {
            return false;
        }
        foreach (var kv in a)
        {
            if (!b.TryGetValue(kv.Key, out var bValue) || bValue != kv.Value)
            {
                return false;
            }
        }
        return true;
    }

    private static void ApplySnakeCaseConvention(ModelBuilder modelBuilder)
    {
        foreach (var entity in modelBuilder.Model.GetEntityTypes())
        {
            if (entity.GetTableName() is { } tableName)
            {
                entity.SetTableName(ToSnakeCase(tableName));
            }

            foreach (var property in entity.GetProperties())
            {
                property.SetColumnName(ToSnakeCase(property.GetColumnName()));
            }
        }
    }

    private static string ToSnakeCase(string input)
    {
        if (string.IsNullOrEmpty(input))
        {
            return input;
        }

        var sb = new System.Text.StringBuilder(input.Length + 8);
        for (var i = 0; i < input.Length; i++)
        {
            var c = input[i];
            if (char.IsUpper(c) && i > 0 && !char.IsUpper(input[i - 1]))
            {
                sb.Append('_');
            }
            sb.Append(char.ToLowerInvariant(c));
        }

        return sb.ToString();
    }
}
