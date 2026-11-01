# Studio de formularios — datos de base

Scripts para cargar el catálogo de campos y el formulario base de cada tipo de formulario del studio.

| Script | Tabla | Qué carga |
| --- | --- | --- |
| `sql/001_form_catalogs_patient-registration.sql` | `form_catalogs` | La cabecera del catálogo (tipo de formulario, versión, título, recursos FHIR) |
| `sql/002_form_catalog_fields_patient-registration.sql` | `form_catalog_fields` | Los campos del catálogo: **una sentencia por campo** |
| `sql/003_form_definitions_patient-registration_preset.sql` | `form_definitions` | El formulario base (sin organización), publicado |

## Cómo cargarlos

1. Aplicar las migraciones (`AddFormCatalogs` y `SplitFormCatalogFields`). `dotnet ef` no logra compilar mientras el
   proyecto trate como error los avisos de vulnerabilidad de paquetes, así que se compila antes con la auditoría
   desactivada y se corre `dotnet ef` sin recompilar:

   ```bash
   dotnet build src/IMed.Api.WebApi/IMed.Api.WebApi.csproj -p:NuGetAudit=false

   ASPNETCORE_ENVIRONMENT=Development dotnet ef database update --no-build \
     --project src/IMed.Api.Infrastructure \
     --startup-project src/IMed.Api.WebApi
   ```

   Si la base ya tenía la primera migración con un catálogo cargado, `SplitFormCatalogFields` copia cada campo a
   su propia fila antes de quitar la columna anterior.

2. Ejecutar los scripts **en orden** (cada uno asume los anteriores):

   ```bash
   psql -h localhost -p 5434 -U imed -d imed_app -f db/forms/sql/001_form_catalogs_patient-registration.sql
   psql -h localhost -p 5434 -U imed -d imed_app -f db/forms/sql/002_form_catalog_fields_patient-registration.sql
   psql -h localhost -p 5434 -U imed -d imed_app -f db/forms/sql/003_form_definitions_patient-registration_preset.sql
   ```

   Con Postgres en Docker: `docker exec -i postgres-db psql -U imed -d imed_app < db/forms/sql/001_...sql`.

Los tres son idempotentes. El 002 actualiza los campos que ya existen y **desactiva** (no borra) los que ya no
están en el script. La API ve un catálogo nuevo o cambiado como máximo un minuto después (caché).
Sin estos datos nada se rompe: el cliente usa el formulario que trae y el catálogo que trae.

## Ajustar un campo

Cada campo es una sentencia de `002`, con su clave, widget, banderas (`core`, `custom`, `email`, `active`), lo que
se puede editar, valores por defecto, lista de opciones y mapeo. Se edita esa sentencia y se vuelve a ejecutar el
script. Para apagar un campo sin tocar el script: `UPDATE form_catalog_fields SET active = FALSE WHERE field_key = '...'`.

## De dónde salen

Los scripts son la fuente del catálogo: se editan a mano y se vuelven a ejecutar. El cliente no trae una copia;
lee el catálogo de la API (`GET /api/form-catalog/{code}`).

## Agregar otro tipo de formulario

Un tipo nuevo es una fila en `form_catalogs` (su `form_code` es el `code` de sus formularios), sus filas en
`form_catalog_fields` y su formulario base en `form_definitions` sin organización: un script por cada uno, con el
mismo formato.
