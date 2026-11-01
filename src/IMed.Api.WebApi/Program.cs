using IMed.Api.Infrastructure;
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
    });

builder.Services.AddAuthorization();

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

app.UseCors(CorsPolicyName);

app.UseAuthentication();
app.UseAuthorization();

// Endpoints
app.MapHealthEndpoints();
app.MapMeEndpoints();
app.MapMeStreamEndpoint();
app.MapMePractitionerProfileEndpoints();
app.MapOrganizationsEndpoints();
app.MapFormsEndpoints();
app.MapFormCatalogEndpoints();
app.MapDiagramsEndpoints();
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

app.Run();

// Expone el Program como tipo público para que WebApplicationFactory<Program>
// pueda materializarlo en la suite de integración (Microsoft.AspNetCore.Mvc.Testing).
public partial class Program;
