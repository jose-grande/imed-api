# imed-api

Backend .NET Core 10 del Sistema EMD IMed. Expone la API REST del dominio de aplicación (suscripciones, formularios, auditoría app-level, agregador cross-tenant para el portal de pacientes, invitaciones, etc.).

**No contiene lógica clínica FHIR** — esa vive en `hapi-fhir-imed` (Spring Boot + HAPI FHIR). Ver PRD §5.3 para el criterio .NET vs Spring Boot.

## Estructura (Clean Architecture)

```
imed-api/
├── src/
│   ├── IMed.Api.Domain/          # Entities, value objects. Sin deps.
│   ├── IMed.Api.Application/     # Use cases, interfaces, DTOs. Dep: Domain.
│   ├── IMed.Api.Infrastructure/  # EF Core, Keycloak Admin, adapters. Dep: Application.
│   └── IMed.Api.WebApi/          # Endpoints, DI, middleware. Dep: all.
├── tests/                         # (Hito 2)
├── Directory.Build.props          # Settings globales (TFM, nullable, warnings)
├── Directory.Packages.props       # Central Package Management
├── global.json                    # Pin SDK .NET
└── IMed.Api.slnx                  # Solución (formato XML .NET 10)
```

## Prerequisitos

| Componente       | Versión / Target              |
| ---------------- | ----------------------------- |
| .NET SDK         | 10.0.107 (pin en `global.json`) |
| PostgreSQL       | 16 corriendo local, BD `imed_app` creada por `imed-infra/postgres/01-create-databases.sql` |
| Keycloak         | Realm `imed-clinical-dev` importado en `:8090` |

## Arrancar en dev

```bash
cd E:\Proyectos\IMed\repos\imed-api
dotnet restore
dotnet build
dotnet run --project src/IMed.Api.WebApi
```

El API queda escuchando en `http://localhost:5000`. Ejecuta con profile `http` por defecto.

### URLs útiles

| URL                                       | Propósito                                     | Auth |
| ----------------------------------------- | --------------------------------------------- | ---- |
| `http://localhost:5000/openapi/v1.json`   | Contrato OpenAPI 3.x generado                 | no   |
| `http://localhost:5000/scalar/v1`         | UI Scalar para explorar la API                | no   |
| `http://localhost:5000/healthz`           | Liveness probe                                 | no   |
| `http://localhost:5000/readyz`            | Readiness (incluye check de Postgres)         | no   |
| `http://localhost:5000/api/me`            | Devuelve los claims del JWT actual            | **sí** |

### Probar `/api/me` con un token de Keycloak

```bash
# Obtener token con admin.dev (usuario semilla)
TOKEN=$(curl -s -X POST http://localhost:8090/realms/imed-clinical-dev/protocol/openid-connect/token \
  -d "grant_type=password&client_id=imed-clinical-web&username=admin.dev&password=dev" \
  | jq -r .access_token)

# Llamar al endpoint
curl http://localhost:5000/api/me -H "Authorization: Bearer $TOKEN"
```

> El cliente `imed-clinical-web` es `publicClient: true` con `standardFlowEnabled: true` pero **NO** tiene `directAccessGrantsEnabled`, así que ese `grant_type=password` fallará. Para pruebas rápidas desde CLI, podés (a) habilitar "Direct Access Grants" temporalmente solo en dev, o (b) sacar el token desde DevTools del `fhir-client` ya logueado y copiarlo.

## Configuración

| Clave                          | Origen                        | Descripción                              |
| ------------------------------ | ----------------------------- | ---------------------------------------- |
| `ConnectionStrings:ImedApp`    | `appsettings.*.json`          | Postgres `imed_app`                      |
| `Keycloak:Authority`           | `appsettings.*.json`          | Issuer del realm (JWKS se descubre auto) |
| `Keycloak:RequireHttpsMetadata`| `appsettings.*.json`          | `false` en dev, `true` en staging/prod   |
| `Cors:AllowedOrigins`          | `appsettings.*.json`          | Lista de orígenes permitidos             |

**Producción:** los valores reales vienen de vault / env vars, nunca del `appsettings.Production.json`. Ver PLAN §8.2.2.

## Migraciones EF Core

```bash
# Crear una migración nueva
dotnet ef migrations add <NombreMigracion> \
  --project src/IMed.Api.Infrastructure \
  --startup-project src/IMed.Api.WebApi \
  --output-dir Persistence/Migrations

# Aplicar a la BD
dotnet ef database update \
  --project src/IMed.Api.Infrastructure \
  --startup-project src/IMed.Api.WebApi

# Generar SQL idempotente (para staging/prod)
dotnet ef migrations script --idempotent \
  --project src/IMed.Api.Infrastructure \
  --startup-project src/IMed.Api.WebApi \
  -o migrations.sql
```

Política dev/prod:
- **Dev:** `dotnet ef database update` manualmente. Hibernate/EF NO se actualiza on-startup (ver PLAN §8.6.4).
- **Staging/Prod:** solo scripts SQL idempotentes, aplicados por pipeline antes de arrancar el servicio.

## Estado actual (Hito 1 — Épica C cerrada)

- ✅ Scaffolding Clean Architecture
- ✅ Central Package Management
- ✅ JWT Bearer auth contra Keycloak
- ✅ CORS configurado para `fhir-client` (`:4200`) y `patient-portal` futuro (`:4300`)
- ✅ Endpoints `/healthz`, `/readyz`, `/api/me`
- ✅ OpenAPI + Scalar UI en dev
- ✅ `POST /api/organizations/bootstrap` (PLAN §2.3.4) con outbox híbrido
- ✅ Keycloak Admin API client + `KeycloakServiceAccountTokenProvider` compartido
- ✅ HAPI FHIR client con partition management
- ✅ **Outbox pattern híbrido + worker** (PLAN §2.3.7, ADR-006)
- ✅ **SSE `/api/me/stream`** (PLAN §2.3.6) con bus in-memory + heartbeat
- ✅ **Audit log app-level** (PLAN §2.3.3) — tabla `app_audit_log` con índices op/actor/categoría
- ✅ Tests de integración (xUnit + WireMock + Postgres local) — 8/8 verde

## Outbox híbrido (resumen)

Cada operación con side-effects externos (Keycloak, futuras integraciones) inserta un evento en `outbox_events` dentro de la transacción local. **Hot-path**: el handler intenta el side-effect inmediatamente; si OK, marca el evento dispatched in-line. Si falla, retorna OK al cliente con `pendingKeycloakSync=true`. **Worker**: `OutboxProcessor` (BackgroundService) usa `FOR UPDATE SKIP LOCKED` (multi-instance ready), procesa con backoff exponencial 5s→1h, MaxAttempts=24. Tras éxito, publica al `IUserNotificationBus` (SSE).

Ver:
- [`src/IMed.Api.Application/Outbox/IOutboxDispatcher.cs`](src/IMed.Api.Application/Outbox/IOutboxDispatcher.cs) — interfaz que cada side-effect implementa.
- [`src/IMed.Api.Infrastructure/Outbox/OutboxProcessor.cs`](src/IMed.Api.Infrastructure/Outbox/OutboxProcessor.cs) — worker.
- [`src/IMed.Api.Infrastructure/Outbox/OrganizationBootstrappedDispatcher.cs`](src/IMed.Api.Infrastructure/Outbox/OrganizationBootstrappedDispatcher.cs) — primer dispatcher (bootstrap).

### Configuración (sección `Outbox` en appsettings)

| Clave | Default | Descripción |
| ----- | ------- | ----------- |
| `Enabled` | `true` | Si `false`, el worker no consume (eventos siguen acumulándose). |
| `IdlePollSeconds` | `5` | Poll cuando NO hay eventos pendientes. |
| `BusyPollSeconds` | `1` | Poll cuando recién despachó algo. |
| `BatchSize` | `25` | Eventos por tick. |
| `BackoffBaseSeconds` | `5` | Base del backoff exponencial. |
| `BackoffMaxSeconds` | `3600` | Cap del backoff (1h). |
| `MaxAttempts` | `24` | Tras N fallos, `Freeze()` con `LastError` preservado. |

## SSE `/api/me/stream`

Endpoint para que el frontend reciba notificaciones tipadas en tiempo real. El evento crítico hoy es `refresh_token`: el backend cambió claims del usuario (organization_assigned, tier_changed) y el cliente debe llamar `AuthService.forceTokenRefresh()`.

**Limitación in-memory**: solo funciona dentro de un proceso. Para multi-instance, swap a Redis pub/sub (ADR-006). El binding está en `DependencyInjection.AddNotifications()`.

**Heartbeat**: comentario `: ping` cada 30s para evitar timeouts de proxies.

## Tests de integración

```bash
dotnet test
```

Requisitos:
- Postgres local con BD `imed_app_test` creada (ver `imed-infra/postgres/02-create-test-database.sql`).
- Override del connection string vía env var `IMED_TEST_DB_CONNECTION` para CI.

Mocks: Keycloak Admin + JWKS + token endpoint con WireMock; HAPI partition + Organization con WireMock. JWTs firmados localmente con RSA 2048 generada por suite.

## Troubleshooting

| Síntoma                                        | Probable causa                                           |
| ---------------------------------------------- | -------------------------------------------------------- |
| `Unable to resolve service for DbContext`      | Falta `AddInfrastructure(config)` en `Program.cs`        |
| `401 Unauthorized` en `/api/me`                | Token inválido / expirado / issuer no coincide           |
| `Readyz` devuelve `Unhealthy`                  | Postgres no corriendo, o credenciales mal                |
| CORS error desde Angular                       | Origen no está en `Cors:AllowedOrigins`                  |
