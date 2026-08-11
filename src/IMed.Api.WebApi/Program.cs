using System.Security.Claims;
using System.Text.Json;
using IMed.Api.Application.Abstractions;
using IMed.Api.Infrastructure;
using IMed.Api.WebApi.Http;
using IMed.Api.Infrastructure.Persistence;
using IMed.Api.WebApi.Endpoints;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using Scalar.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

// --- Configuration bindings ------------------------------------------------
var keycloakSection = builder.Configuration.GetSection("Keycloak");
var keycloakAuthority = keycloakSection["Authority"]
    ?? throw new InvalidOperationException("Falta Keycloak:Authority en la configuración.");
var requireHttpsMetadata = keycloakSection.GetValue<bool>("RequireHttpsMetadata");

var corsOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>()
    ?? [];

// --- Services --------------------------------------------------------------
builder.Services.AddInfrastructure(builder.Configuration);

// Sede declarada por el cliente (ADR-008). Vive acá y no en Infrastructure porque leer un
// header es una preocupación de transporte: la capa de datos no debe saber que existe HTTP.
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<IRequestLocationContext, RequestLocationContext>();

// Un argumento inválido es un 400, no un 500. Sin esto, la validación que hacen los handlers
// con ArgumentException llega como error del servidor en los endpoints que no la atrapan a mano.
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<ArgumentExceptionHandler>();

// CORS para los portales Angular
const string CorsPolicyName = "ImedPortals";
builder.Services.AddCors(options =>
{
    options.AddPolicy(CorsPolicyName, policy =>
    {
        policy.WithOrigins(corsOrigins)
              .AllowAnyHeader()
              .AllowAnyMethod()
              .AllowCredentials();
    });
});

// JWT Bearer authentication contra Keycloak
builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(JwtBearerDefaults.AuthenticationScheme, options =>
    {
        options.Authority = keycloakAuthority;
        options.RequireHttpsMetadata = requireHttpsMetadata;
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            // Keycloak por default NO emite `aud` con el clientId del SPA público.
            // Si en el futuro configuramos un audience mapper, activar esto con
            // ValidateAudience=true + ValidAudience="imed-clinical-api".
            ValidateAudience = false,
            ValidateIssuerSigningKey = true,
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromSeconds(30),
            NameClaimType = "preferred_username",
            RoleClaimType = "roles"
        };

        // Keycloak emite los realm roles en `realm_access.roles` (JSON anidado);
        // .NET no los aplana a RoleClaimType, así que las políticas RequireRole
        // no los verían (bug #15). Los aplanamos a claims "roles" tras validar.
        options.Events = new JwtBearerEvents
        {
            OnTokenValidated = context =>
            {
                if (context.Principal?.Identity is ClaimsIdentity identity)
                {
                    var realmAccess = identity.FindFirst("realm_access")?.Value;
                    if (!string.IsNullOrEmpty(realmAccess))
                    {
                        try
                        {
                            using var doc = JsonDocument.Parse(realmAccess);
                            if (doc.RootElement.TryGetProperty("roles", out var roles)
                                && roles.ValueKind == JsonValueKind.Array)
                            {
                                foreach (var role in roles.EnumerateArray())
                                {
                                    var value = role.GetString();
                                    if (!string.IsNullOrEmpty(value) && !identity.HasClaim("roles", value))
                                    {
                                        identity.AddClaim(new Claim("roles", value));
                                    }
                                }
                            }
                        }
                        catch (JsonException)
                        {
                            // realm_access mal formado → el usuario queda sin roles de realm.
                        }
                    }
                }
                return Task.CompletedTask;
            }
        };
    });

// Políticas de autorización del módulo LIS (Épica LIS-2). Las épicas siguientes
// aplican estas políticas a sus endpoints en vez de re-listar roles.
builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("LabRead", p => p.RequireRole(
        "lab_tech", "bioanalyst", "lab_supervisor", "pathologist", "practitioner", "org_admin"));
    options.AddPolicy("LabTechnician", p => p.RequireRole(
        "lab_tech", "lab_supervisor", "org_admin"));
    options.AddPolicy("LabValidator", p => p.RequireRole(
        "bioanalyst", "pathologist", "lab_supervisor", "org_admin"));
    options.AddPolicy("LabSupervisor", p => p.RequireRole(
        "lab_supervisor", "org_admin"));
});

// Health checks
builder.Services.AddHealthChecks()
    .AddDbContextCheck<AppDbContext>(name: "postgres-imed_app");

// OpenAPI (nativo en .NET 10) + Scalar UI
builder.Services.AddOpenApi();

var app = builder.Build();

// --- Pipeline --------------------------------------------------------------
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.MapScalarApiReference(options =>
    {
        options.Title = "IMed API (dev)";
    });
}

app.UseExceptionHandler();

app.UseCors(CorsPolicyName);

app.UseAuthentication();
app.UseAuthorization();

// Endpoints
app.MapHealthEndpoints();
app.MapMeEndpoints();
app.MapMeStreamEndpoint();
app.MapMePractitionerProfileEndpoints();
app.MapOrganizationsEndpoints();
app.MapLocationsEndpoints();
app.MapFormsEndpoints();
app.MapDiagnosesEndpoints();
app.MapLabsEndpoints();
app.MapSpecialtiesEndpoints();
app.MapTeamEndpoints();
app.MapInvitationsEndpoints();
app.MapJoinRequestsEndpoints();
app.MapIdentifiersEndpoints();
app.MapPatientsEndpoints();
app.MapAllergiesEndpoints();
app.MapMedicationsEndpoints();
app.MapPrescriptionsEndpoints();
app.MapLabOrdersEndpoints();
app.MapLabCatalogEndpoints();
app.MapLabReceptionEndpoints();
app.MapLabSpecimenEndpoints();
app.MapLabWorklistEndpoints();
app.MapLabResultsEndpoints();
app.MapLabRulesEndpoints();
app.MapLabDashboardEndpoints();

app.Run();

// Expone el Program como tipo público para que WebApplicationFactory<Program>
// pueda materializarlo en la suite de integración (Microsoft.AspNetCore.Mvc.Testing).
public partial class Program;
