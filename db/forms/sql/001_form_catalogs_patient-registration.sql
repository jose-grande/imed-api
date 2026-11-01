-- form_catalogs · patient-registration 1.0.0
--
-- Cabecera del catálogo de campos del formulario "Registro de Paciente". Los campos van en el script 002.
--
-- Se edita a mano.
--
-- Es idempotente: si el catálogo (form_code + version) ya existe, lo actualiza.
-- La API lo ve como máximo un minuto después (caché).
-- Requiere las migraciones AddFormCatalogs y SplitFormCatalogFields.

INSERT INTO form_catalogs (id, form_code, version, title, resource_types, active, created_at, updated_at)
VALUES (
  gen_random_uuid(),
  'patient-registration',
  '1.0.0',
  'Registro de Paciente',
  $json$["Patient","RelatedPerson"]$json$::jsonb,
  TRUE,
  now(),
  now()
)
ON CONFLICT (form_code, version) DO UPDATE
  SET title          = EXCLUDED.title,
      resource_types = EXCLUDED.resource_types,
      active         = EXCLUDED.active,
      updated_at     = now();
