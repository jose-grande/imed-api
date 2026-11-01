using System.Text.Json;
using IMed.Api.Domain.Auditing;
using IMed.Api.Domain.Catalogs;
using IMed.Api.Domain.Diagrams;
using IMed.Api.Domain.Forms;
using IMed.Api.Domain.Invitations;
using IMed.Api.Domain.JoinRequests;
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
    public DbSet<FormCatalog> FormCatalogs => Set<FormCatalog>();
    public DbSet<FormCatalogField> FormCatalogFields => Set<FormCatalogField>();
    public DbSet<DiagramDefinition> DiagramDefinitions => Set<DiagramDefinition>();
    public DbSet<Icd11Code> Icd11Catalog => Set<Icd11Code>();
    public DbSet<LoincCode> LoincCatalog => Set<LoincCode>();
    public DbSet<SpecialtyCode> SpecialtyCatalog => Set<SpecialtyCode>();
    public DbSet<AllergyCatalogEntry> AllergyCatalog => Set<AllergyCatalogEntry>();
    public DbSet<MedicationCatalogEntry> MedicationCatalog => Set<MedicationCatalogEntry>();
    public DbSet<UserPractitionerAssignment> UserPractitionerAssignments => Set<UserPractitionerAssignment>();
    public DbSet<Invitation> Invitations => Set<Invitation>();
    public DbSet<JoinRequest> JoinRequests => Set<JoinRequest>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        ConfigureOrganizationSubscription(modelBuilder);
        ConfigureUserTierAssignment(modelBuilder);
        ConfigureOutboxEvent(modelBuilder);
        ConfigureAppAuditLog(modelBuilder);
        ConfigureFormDefinition(modelBuilder);
        ConfigureFormCatalog(modelBuilder);
        ConfigureDiagramDefinition(modelBuilder);
        ConfigureIcd11Code(modelBuilder);
        ConfigureLoincCode(modelBuilder);
        ConfigureSpecialtyCode(modelBuilder);
        ConfigureAllergyCatalogEntry(modelBuilder);
        ConfigureMedicationCatalogEntry(modelBuilder);
        ConfigureUserPractitionerAssignment(modelBuilder);
        ConfigureInvitation(modelBuilder);
        ConfigureJoinRequest(modelBuilder);

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

    private static void ConfigureFormCatalog(ModelBuilder b)
    {
        b.Entity<FormCatalog>(e =>
        {
            e.ToTable("form_catalogs");
            e.HasKey(x => x.Id);
            e.Property(x => x.FormCode).HasMaxLength(60).IsRequired();
            e.Property(x => x.Version).HasMaxLength(20).IsRequired();
            e.Property(x => x.Title).HasMaxLength(200).IsRequired();
            e.Property(x => x.ResourceTypes).HasColumnType("jsonb").IsRequired();
            e.Property(x => x.Active).IsRequired();
            e.Property(x => x.CreatedAt).IsRequired();
            e.Property(x => x.UpdatedAt).IsRequired();

            // Un catálogo por (tipo de formulario, versión); los scripts de db/forms hacen upsert sobre esta clave.
            e.HasIndex(x => new { x.FormCode, x.Version })
                .IsUnique()
                .HasDatabaseName("ux_form_catalogs_code_version");
        });

        b.Entity<FormCatalogField>(e =>
        {
            e.ToTable("form_catalog_fields");
            e.HasKey(x => x.Id);
            e.Property(x => x.CatalogId).IsRequired();
            e.Property(x => x.FieldKey).HasMaxLength(60).IsRequired();
            e.Property(x => x.SortOrder).IsRequired();
            e.Property(x => x.Widget).HasMaxLength(20).IsRequired();
            e.Property(x => x.Core).IsRequired();
            e.Property(x => x.Custom).IsRequired();
            e.Property(x => x.Email).IsRequired();
            e.Property(x => x.Active).IsRequired();
            e.Property(x => x.Editable).HasColumnType("jsonb").IsRequired();
            e.Property(x => x.Defaults).HasColumnType("jsonb").IsRequired();
            e.Property(x => x.Messages).HasColumnType("jsonb");
            e.Property(x => x.ListSource).HasColumnType("jsonb");
            e.Property(x => x.Binding).HasColumnType("jsonb").IsRequired();
            e.Property(x => x.CreatedAt).IsRequired();
            e.Property(x => x.UpdatedAt).IsRequired();

            // Los campos se borran con su catálogo.
            e.HasOne<FormCatalog>().WithMany().HasForeignKey(x => x.CatalogId).OnDelete(DeleteBehavior.Cascade);

            // Un campo por clave dentro de cada catálogo; los scripts hacen upsert sobre esta clave.
            e.HasIndex(x => new { x.CatalogId, x.FieldKey })
                .IsUnique()
                .HasDatabaseName("ux_form_catalog_fields_catalog_key");
        });
    }

    private static void ConfigureDiagramDefinition(ModelBuilder b)
    {
        b.Entity<DiagramDefinition>(e =>
        {
            e.ToTable("diagram_definitions");
            e.HasKey(x => x.Id);
            e.Property(x => x.Code).HasMaxLength(60).IsRequired();
            e.Property(x => x.SpecialtyCode).HasMaxLength(60);
            e.Property(x => x.Version).HasMaxLength(20).IsRequired();
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(20).IsRequired();
            e.Property(x => x.Title).HasMaxLength(200).IsRequired();
            e.Property(x => x.Description);
            e.Property(x => x.SvgContent).HasColumnType("text").IsRequired();
            e.Property(x => x.ZonesJson).HasColumnType("jsonb").IsRequired();
            e.Property(x => x.AnnotationStyleJson).HasColumnType("jsonb").IsRequired();
            e.Property(x => x.LinkedFormCodesJson).HasColumnType("jsonb").IsRequired();
            e.Property(x => x.ZoneBodySiteMapJson).HasColumnType("jsonb").IsRequired();
            e.Property(x => x.OrganizationId);
            e.Property(x => x.CreatedAt).IsRequired();
            e.Property(x => x.CreatedBy).IsRequired();
            e.Property(x => x.PublishedAt);
            e.Property(x => x.PublishedBy);

            // Búsqueda principal: por org + specialty + status (misma forma que Forms).
            e.HasIndex(x => new { x.OrganizationId, x.SpecialtyCode, x.Status })
                .HasDatabaseName("ix_diagram_definitions_org_specialty_status");
            // Unicidad de (code, version, organization_id) para evitar duplicados.
            e.HasIndex(x => new { x.OrganizationId, x.Code, x.Version })
                .IsUnique()
                .HasDatabaseName("ux_diagram_definitions_org_code_version");
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
