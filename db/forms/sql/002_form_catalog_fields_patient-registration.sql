-- form_catalog_fields · patient-registration 1.0.0
--
-- Los 69 campos del catálogo, una sentencia por campo: widget, lo que una organización
-- puede cambiar, valores por defecto, lista de opciones y mapeo a FHIR. Para ajustar un campo basta
-- editar su sentencia y volver a ejecutar el script.
--
-- Se edita a mano.
--
-- Es idempotente: actualiza los campos que ya existen (clave dentro del catálogo) y, al final,
-- desactiva los que ya no están en esta lista (no los borra).
-- Requiere el script 001 (la cabecera del catálogo).

-- 01 · identifierType (select, núcleo)
INSERT INTO form_catalog_fields (
  id, catalog_id, field_key, sort_order, widget, core, custom, email, active,
  editable, defaults, messages, list_source, binding, created_at, updated_at
)
SELECT
  gen_random_uuid(), c.id, 'identifierType', 1, 'select',
  TRUE, FALSE, FALSE, TRUE,
  $json$["label","placeholder","helpText"]$json$::jsonb,
  $json${
  "label": "Tipo de Identificación",
  "placeholder": "Seleccione...",
  "required": true
}$json$::jsonb,
  $json${
  "required": "El tipo de identificación es obligatorio"
}$json$::jsonb,
  $json${
  "source": "api",
  "base": "imed-api",
  "path": "/api/identifiers/types",
  "params": {
    "country": "{organizationCountry}"
  },
  "map": {
    "value": "code",
    "label": "label"
  }
}$json$::jsonb,
  $json${
  "mapsTo": "Patient.identifier",
  "extractor": "identifier",
  "part": "type"
}$json$::jsonb,
  now(), now()
FROM form_catalogs c
WHERE c.form_code = 'patient-registration' AND c.version = '1.0.0'
ON CONFLICT (catalog_id, field_key) DO UPDATE
  SET sort_order  = EXCLUDED.sort_order,
      widget      = EXCLUDED.widget,
      core        = EXCLUDED.core,
      custom      = EXCLUDED.custom,
      email       = EXCLUDED.email,
      active      = TRUE,
      editable    = EXCLUDED.editable,
      defaults    = EXCLUDED.defaults,
      messages    = EXCLUDED.messages,
      list_source = EXCLUDED.list_source,
      binding     = EXCLUDED.binding,
      updated_at  = now();

-- 02 · identifierValue (text, núcleo)
INSERT INTO form_catalog_fields (
  id, catalog_id, field_key, sort_order, widget, core, custom, email, active,
  editable, defaults, messages, list_source, binding, created_at, updated_at
)
SELECT
  gen_random_uuid(), c.id, 'identifierValue', 2, 'text',
  TRUE, FALSE, FALSE, TRUE,
  $json$["label","placeholder","helpText"]$json$::jsonb,
  $json${
  "label": "Número de Identificación",
  "placeholder": "Según tipo seleccionado",
  "required": true
}$json$::jsonb,
  $json${
  "required": "El número de identificación es obligatorio"
}$json$::jsonb,
  NULL,
  $json${
  "mapsTo": "Patient.identifier",
  "extractor": "identifier",
  "part": "value"
}$json$::jsonb,
  now(), now()
FROM form_catalogs c
WHERE c.form_code = 'patient-registration' AND c.version = '1.0.0'
ON CONFLICT (catalog_id, field_key) DO UPDATE
  SET sort_order  = EXCLUDED.sort_order,
      widget      = EXCLUDED.widget,
      core        = EXCLUDED.core,
      custom      = EXCLUDED.custom,
      email       = EXCLUDED.email,
      active      = TRUE,
      editable    = EXCLUDED.editable,
      defaults    = EXCLUDED.defaults,
      messages    = EXCLUDED.messages,
      list_source = EXCLUDED.list_source,
      binding     = EXCLUDED.binding,
      updated_at  = now();

-- 03 · prefix (text)
INSERT INTO form_catalog_fields (
  id, catalog_id, field_key, sort_order, widget, core, custom, email, active,
  editable, defaults, messages, list_source, binding, created_at, updated_at
)
SELECT
  gen_random_uuid(), c.id, 'prefix', 3, 'text',
  FALSE, FALSE, FALSE, TRUE,
  $json$["label","placeholder","helpText","required"]$json$::jsonb,
  $json${
  "label": "Prefijo",
  "placeholder": "Ej: Dr.",
  "maxLength": 20
}$json$::jsonb,
  NULL,
  NULL,
  $json${
  "mapsTo": "Patient.name",
  "extractor": "human-name",
  "part": "prefix"
}$json$::jsonb,
  now(), now()
FROM form_catalogs c
WHERE c.form_code = 'patient-registration' AND c.version = '1.0.0'
ON CONFLICT (catalog_id, field_key) DO UPDATE
  SET sort_order  = EXCLUDED.sort_order,
      widget      = EXCLUDED.widget,
      core        = EXCLUDED.core,
      custom      = EXCLUDED.custom,
      email       = EXCLUDED.email,
      active      = TRUE,
      editable    = EXCLUDED.editable,
      defaults    = EXCLUDED.defaults,
      messages    = EXCLUDED.messages,
      list_source = EXCLUDED.list_source,
      binding     = EXCLUDED.binding,
      updated_at  = now();

-- 04 · firstName (text, núcleo)
INSERT INTO form_catalog_fields (
  id, catalog_id, field_key, sort_order, widget, core, custom, email, active,
  editable, defaults, messages, list_source, binding, created_at, updated_at
)
SELECT
  gen_random_uuid(), c.id, 'firstName', 4, 'text',
  TRUE, FALSE, FALSE, TRUE,
  $json$["label","placeholder","helpText"]$json$::jsonb,
  $json${
  "label": "Primer Nombre",
  "placeholder": "Ej: Juan",
  "maxLength": 50,
  "minLength": 2,
  "required": true
}$json$::jsonb,
  $json${
  "required": "El primer nombre es obligatorio",
  "minLength": "Mínimo 2 caracteres"
}$json$::jsonb,
  NULL,
  $json${
  "mapsTo": "Patient.name",
  "extractor": "human-name",
  "part": "given"
}$json$::jsonb,
  now(), now()
FROM form_catalogs c
WHERE c.form_code = 'patient-registration' AND c.version = '1.0.0'
ON CONFLICT (catalog_id, field_key) DO UPDATE
  SET sort_order  = EXCLUDED.sort_order,
      widget      = EXCLUDED.widget,
      core        = EXCLUDED.core,
      custom      = EXCLUDED.custom,
      email       = EXCLUDED.email,
      active      = TRUE,
      editable    = EXCLUDED.editable,
      defaults    = EXCLUDED.defaults,
      messages    = EXCLUDED.messages,
      list_source = EXCLUDED.list_source,
      binding     = EXCLUDED.binding,
      updated_at  = now();

-- 05 · middleName (text)
INSERT INTO form_catalog_fields (
  id, catalog_id, field_key, sort_order, widget, core, custom, email, active,
  editable, defaults, messages, list_source, binding, created_at, updated_at
)
SELECT
  gen_random_uuid(), c.id, 'middleName', 5, 'text',
  FALSE, FALSE, FALSE, TRUE,
  $json$["label","placeholder","helpText","required"]$json$::jsonb,
  $json${
  "label": "Segundo Nombre",
  "placeholder": "Ej: Carlos",
  "maxLength": 50
}$json$::jsonb,
  NULL,
  NULL,
  $json${
  "mapsTo": "Patient.name",
  "extractor": "human-name",
  "part": "given"
}$json$::jsonb,
  now(), now()
FROM form_catalogs c
WHERE c.form_code = 'patient-registration' AND c.version = '1.0.0'
ON CONFLICT (catalog_id, field_key) DO UPDATE
  SET sort_order  = EXCLUDED.sort_order,
      widget      = EXCLUDED.widget,
      core        = EXCLUDED.core,
      custom      = EXCLUDED.custom,
      email       = EXCLUDED.email,
      active      = TRUE,
      editable    = EXCLUDED.editable,
      defaults    = EXCLUDED.defaults,
      messages    = EXCLUDED.messages,
      list_source = EXCLUDED.list_source,
      binding     = EXCLUDED.binding,
      updated_at  = now();

-- 06 · lastName (text, núcleo)
INSERT INTO form_catalog_fields (
  id, catalog_id, field_key, sort_order, widget, core, custom, email, active,
  editable, defaults, messages, list_source, binding, created_at, updated_at
)
SELECT
  gen_random_uuid(), c.id, 'lastName', 6, 'text',
  TRUE, FALSE, FALSE, TRUE,
  $json$["label","placeholder","helpText"]$json$::jsonb,
  $json${
  "label": "Primer Apellido",
  "placeholder": "Ej: Pérez",
  "maxLength": 50,
  "minLength": 2,
  "required": true
}$json$::jsonb,
  $json${
  "required": "El primer apellido es obligatorio",
  "minLength": "Mínimo 2 caracteres"
}$json$::jsonb,
  NULL,
  $json${
  "mapsTo": "Patient.name",
  "extractor": "human-name",
  "part": "family"
}$json$::jsonb,
  now(), now()
FROM form_catalogs c
WHERE c.form_code = 'patient-registration' AND c.version = '1.0.0'
ON CONFLICT (catalog_id, field_key) DO UPDATE
  SET sort_order  = EXCLUDED.sort_order,
      widget      = EXCLUDED.widget,
      core        = EXCLUDED.core,
      custom      = EXCLUDED.custom,
      email       = EXCLUDED.email,
      active      = TRUE,
      editable    = EXCLUDED.editable,
      defaults    = EXCLUDED.defaults,
      messages    = EXCLUDED.messages,
      list_source = EXCLUDED.list_source,
      binding     = EXCLUDED.binding,
      updated_at  = now();

-- 07 · secondLastName (text)
INSERT INTO form_catalog_fields (
  id, catalog_id, field_key, sort_order, widget, core, custom, email, active,
  editable, defaults, messages, list_source, binding, created_at, updated_at
)
SELECT
  gen_random_uuid(), c.id, 'secondLastName', 7, 'text',
  FALSE, FALSE, FALSE, TRUE,
  $json$["label","placeholder","helpText","required"]$json$::jsonb,
  $json${
  "label": "Segundo Apellido",
  "placeholder": "Ej: García",
  "maxLength": 50
}$json$::jsonb,
  NULL,
  NULL,
  $json${
  "mapsTo": "Patient.name",
  "extractor": "human-name",
  "part": "family"
}$json$::jsonb,
  now(), now()
FROM form_catalogs c
WHERE c.form_code = 'patient-registration' AND c.version = '1.0.0'
ON CONFLICT (catalog_id, field_key) DO UPDATE
  SET sort_order  = EXCLUDED.sort_order,
      widget      = EXCLUDED.widget,
      core        = EXCLUDED.core,
      custom      = EXCLUDED.custom,
      email       = EXCLUDED.email,
      active      = TRUE,
      editable    = EXCLUDED.editable,
      defaults    = EXCLUDED.defaults,
      messages    = EXCLUDED.messages,
      list_source = EXCLUDED.list_source,
      binding     = EXCLUDED.binding,
      updated_at  = now();

-- 08 · gender (select, núcleo)
INSERT INTO form_catalog_fields (
  id, catalog_id, field_key, sort_order, widget, core, custom, email, active,
  editable, defaults, messages, list_source, binding, created_at, updated_at
)
SELECT
  gen_random_uuid(), c.id, 'gender', 8, 'select',
  TRUE, FALSE, FALSE, TRUE,
  $json$["label","placeholder","helpText"]$json$::jsonb,
  $json${
  "label": "Sexo Biológico",
  "placeholder": "Seleccione...",
  "required": true
}$json$::jsonb,
  $json${
  "required": "El sexo es obligatorio"
}$json$::jsonb,
  $json${
  "source": "valueset",
  "url": "http://hl7.org/fhir/ValueSet/administrative-gender",
  "translated": true
}$json$::jsonb,
  $json${
  "mapsTo": "Patient.gender",
  "extractor": "primitive"
}$json$::jsonb,
  now(), now()
FROM form_catalogs c
WHERE c.form_code = 'patient-registration' AND c.version = '1.0.0'
ON CONFLICT (catalog_id, field_key) DO UPDATE
  SET sort_order  = EXCLUDED.sort_order,
      widget      = EXCLUDED.widget,
      core        = EXCLUDED.core,
      custom      = EXCLUDED.custom,
      email       = EXCLUDED.email,
      active      = TRUE,
      editable    = EXCLUDED.editable,
      defaults    = EXCLUDED.defaults,
      messages    = EXCLUDED.messages,
      list_source = EXCLUDED.list_source,
      binding     = EXCLUDED.binding,
      updated_at  = now();

-- 09 · birthDate (date, núcleo)
INSERT INTO form_catalog_fields (
  id, catalog_id, field_key, sort_order, widget, core, custom, email, active,
  editable, defaults, messages, list_source, binding, created_at, updated_at
)
SELECT
  gen_random_uuid(), c.id, 'birthDate', 9, 'date',
  TRUE, FALSE, FALSE, TRUE,
  $json$["label","helpText"]$json$::jsonb,
  $json${
  "label": "Fecha de Nacimiento",
  "required": true
}$json$::jsonb,
  $json${
  "required": "La fecha de nacimiento es obligatoria"
}$json$::jsonb,
  NULL,
  $json${
  "mapsTo": "Patient.birthDate",
  "extractor": "primitive"
}$json$::jsonb,
  now(), now()
FROM form_catalogs c
WHERE c.form_code = 'patient-registration' AND c.version = '1.0.0'
ON CONFLICT (catalog_id, field_key) DO UPDATE
  SET sort_order  = EXCLUDED.sort_order,
      widget      = EXCLUDED.widget,
      core        = EXCLUDED.core,
      custom      = EXCLUDED.custom,
      email       = EXCLUDED.email,
      active      = TRUE,
      editable    = EXCLUDED.editable,
      defaults    = EXCLUDED.defaults,
      messages    = EXCLUDED.messages,
      list_source = EXCLUDED.list_source,
      binding     = EXCLUDED.binding,
      updated_at  = now();

-- 10 · communication (select)
INSERT INTO form_catalog_fields (
  id, catalog_id, field_key, sort_order, widget, core, custom, email, active,
  editable, defaults, messages, list_source, binding, created_at, updated_at
)
SELECT
  gen_random_uuid(), c.id, 'communication', 10, 'select',
  FALSE, FALSE, FALSE, TRUE,
  $json$["label","placeholder","helpText","required"]$json$::jsonb,
  $json${
  "label": "Idioma Preferido",
  "placeholder": "Seleccione..."
}$json$::jsonb,
  NULL,
  $json${
  "source": "valueset",
  "url": "http://hl7.org/fhir/ValueSet/languages"
}$json$::jsonb,
  $json${
  "mapsTo": "Patient.communication.language",
  "extractor": "codeable-concept",
  "codingSystem": "urn:ietf:bcp:47",
  "fixed": {
    "preferred": true
  }
}$json$::jsonb,
  now(), now()
FROM form_catalogs c
WHERE c.form_code = 'patient-registration' AND c.version = '1.0.0'
ON CONFLICT (catalog_id, field_key) DO UPDATE
  SET sort_order  = EXCLUDED.sort_order,
      widget      = EXCLUDED.widget,
      core        = EXCLUDED.core,
      custom      = EXCLUDED.custom,
      email       = EXCLUDED.email,
      active      = TRUE,
      editable    = EXCLUDED.editable,
      defaults    = EXCLUDED.defaults,
      messages    = EXCLUDED.messages,
      list_source = EXCLUDED.list_source,
      binding     = EXCLUDED.binding,
      updated_at  = now();

-- 11 · phoneNumber (text)
INSERT INTO form_catalog_fields (
  id, catalog_id, field_key, sort_order, widget, core, custom, email, active,
  editable, defaults, messages, list_source, binding, created_at, updated_at
)
SELECT
  gen_random_uuid(), c.id, 'phoneNumber', 11, 'text',
  FALSE, FALSE, FALSE, TRUE,
  $json$["label","placeholder","helpText","required"]$json$::jsonb,
  $json${
  "label": "Teléfono Principal",
  "placeholder": "+504 9999-9999",
  "inputMode": "tel",
  "required": true
}$json$::jsonb,
  $json${
  "required": "El teléfono es obligatorio"
}$json$::jsonb,
  NULL,
  $json${
  "mapsTo": "Patient.telecom",
  "extractor": "contact-point",
  "system": "phone"
}$json$::jsonb,
  now(), now()
FROM form_catalogs c
WHERE c.form_code = 'patient-registration' AND c.version = '1.0.0'
ON CONFLICT (catalog_id, field_key) DO UPDATE
  SET sort_order  = EXCLUDED.sort_order,
      widget      = EXCLUDED.widget,
      core        = EXCLUDED.core,
      custom      = EXCLUDED.custom,
      email       = EXCLUDED.email,
      active      = TRUE,
      editable    = EXCLUDED.editable,
      defaults    = EXCLUDED.defaults,
      messages    = EXCLUDED.messages,
      list_source = EXCLUDED.list_source,
      binding     = EXCLUDED.binding,
      updated_at  = now();

-- 12 · email (text)
INSERT INTO form_catalog_fields (
  id, catalog_id, field_key, sort_order, widget, core, custom, email, active,
  editable, defaults, messages, list_source, binding, created_at, updated_at
)
SELECT
  gen_random_uuid(), c.id, 'email', 12, 'text',
  FALSE, FALSE, TRUE, TRUE,
  $json$["label","placeholder","helpText","required"]$json$::jsonb,
  $json${
  "label": "Correo Electrónico",
  "placeholder": "ejemplo@correo.com",
  "inputMode": "email"
}$json$::jsonb,
  $json${
  "email": "Formato de correo inválido"
}$json$::jsonb,
  NULL,
  $json${
  "mapsTo": "Patient.telecom",
  "extractor": "contact-point",
  "system": "email"
}$json$::jsonb,
  now(), now()
FROM form_catalogs c
WHERE c.form_code = 'patient-registration' AND c.version = '1.0.0'
ON CONFLICT (catalog_id, field_key) DO UPDATE
  SET sort_order  = EXCLUDED.sort_order,
      widget      = EXCLUDED.widget,
      core        = EXCLUDED.core,
      custom      = EXCLUDED.custom,
      email       = EXCLUDED.email,
      active      = TRUE,
      editable    = EXCLUDED.editable,
      defaults    = EXCLUDED.defaults,
      messages    = EXCLUDED.messages,
      list_source = EXCLUDED.list_source,
      binding     = EXCLUDED.binding,
      updated_at  = now();

-- 13 · active (toggle)
INSERT INTO form_catalog_fields (
  id, catalog_id, field_key, sort_order, widget, core, custom, email, active,
  editable, defaults, messages, list_source, binding, created_at, updated_at
)
SELECT
  gen_random_uuid(), c.id, 'active', 13, 'toggle',
  FALSE, FALSE, FALSE, TRUE,
  $json$["label","description","helpText","onLabel","offLabel","required"]$json$::jsonb,
  $json${
  "label": "Paciente Activo",
  "description": "Indica si el registro del paciente está vigente en el sistema",
  "helpText": "Desactivar para dar de baja al paciente",
  "defaultValue": true
}$json$::jsonb,
  NULL,
  NULL,
  $json${
  "mapsTo": "Patient.active",
  "extractor": "primitive",
  "default": true
}$json$::jsonb,
  now(), now()
FROM form_catalogs c
WHERE c.form_code = 'patient-registration' AND c.version = '1.0.0'
ON CONFLICT (catalog_id, field_key) DO UPDATE
  SET sort_order  = EXCLUDED.sort_order,
      widget      = EXCLUDED.widget,
      core        = EXCLUDED.core,
      custom      = EXCLUDED.custom,
      email       = EXCLUDED.email,
      active      = TRUE,
      editable    = EXCLUDED.editable,
      defaults    = EXCLUDED.defaults,
      messages    = EXCLUDED.messages,
      list_source = EXCLUDED.list_source,
      binding     = EXCLUDED.binding,
      updated_at  = now();

-- 14 · addressState (select)
INSERT INTO form_catalog_fields (
  id, catalog_id, field_key, sort_order, widget, core, custom, email, active,
  editable, defaults, messages, list_source, binding, created_at, updated_at
)
SELECT
  gen_random_uuid(), c.id, 'addressState', 14, 'select',
  FALSE, FALSE, FALSE, TRUE,
  $json$["label","placeholder","helpText","required"]$json$::jsonb,
  $json${
  "label": "Departamento",
  "placeholder": "Seleccione..."
}$json$::jsonb,
  NULL,
  $json${
  "source": "static",
  "options": [
    {
      "value": "AT",
      "label": "Atlántida"
    },
    {
      "value": "CH",
      "label": "Choluteca"
    },
    {
      "value": "CL",
      "label": "Colón"
    },
    {
      "value": "CM",
      "label": "Comayagua"
    },
    {
      "value": "CP",
      "label": "Copán"
    },
    {
      "value": "CR",
      "label": "Cortés"
    },
    {
      "value": "EP",
      "label": "El Paraíso"
    },
    {
      "value": "FM",
      "label": "Francisco Morazán"
    },
    {
      "value": "GD",
      "label": "Gracias a Dios"
    },
    {
      "value": "IN",
      "label": "Intibucá"
    },
    {
      "value": "IB",
      "label": "Islas de la Bahía"
    },
    {
      "value": "LP",
      "label": "La Paz"
    },
    {
      "value": "LE",
      "label": "Lempira"
    },
    {
      "value": "OC",
      "label": "Ocotepeque"
    },
    {
      "value": "OL",
      "label": "Olancho"
    },
    {
      "value": "SB",
      "label": "Santa Bárbara"
    },
    {
      "value": "VA",
      "label": "Valle"
    },
    {
      "value": "YO",
      "label": "Yoro"
    }
  ]
}$json$::jsonb,
  $json${
  "mapsTo": "Patient.address",
  "extractor": "address",
  "part": "state"
}$json$::jsonb,
  now(), now()
FROM form_catalogs c
WHERE c.form_code = 'patient-registration' AND c.version = '1.0.0'
ON CONFLICT (catalog_id, field_key) DO UPDATE
  SET sort_order  = EXCLUDED.sort_order,
      widget      = EXCLUDED.widget,
      core        = EXCLUDED.core,
      custom      = EXCLUDED.custom,
      email       = EXCLUDED.email,
      active      = TRUE,
      editable    = EXCLUDED.editable,
      defaults    = EXCLUDED.defaults,
      messages    = EXCLUDED.messages,
      list_source = EXCLUDED.list_source,
      binding     = EXCLUDED.binding,
      updated_at  = now();

-- 15 · addressCity (text)
INSERT INTO form_catalog_fields (
  id, catalog_id, field_key, sort_order, widget, core, custom, email, active,
  editable, defaults, messages, list_source, binding, created_at, updated_at
)
SELECT
  gen_random_uuid(), c.id, 'addressCity', 15, 'text',
  FALSE, FALSE, FALSE, TRUE,
  $json$["label","placeholder","helpText","required"]$json$::jsonb,
  $json${
  "label": "Municipio / Ciudad",
  "placeholder": "Ej: Tegucigalpa"
}$json$::jsonb,
  NULL,
  NULL,
  $json${
  "mapsTo": "Patient.address",
  "extractor": "address",
  "part": "city"
}$json$::jsonb,
  now(), now()
FROM form_catalogs c
WHERE c.form_code = 'patient-registration' AND c.version = '1.0.0'
ON CONFLICT (catalog_id, field_key) DO UPDATE
  SET sort_order  = EXCLUDED.sort_order,
      widget      = EXCLUDED.widget,
      core        = EXCLUDED.core,
      custom      = EXCLUDED.custom,
      email       = EXCLUDED.email,
      active      = TRUE,
      editable    = EXCLUDED.editable,
      defaults    = EXCLUDED.defaults,
      messages    = EXCLUDED.messages,
      list_source = EXCLUDED.list_source,
      binding     = EXCLUDED.binding,
      updated_at  = now();

-- 16 · addressLine (textarea)
INSERT INTO form_catalog_fields (
  id, catalog_id, field_key, sort_order, widget, core, custom, email, active,
  editable, defaults, messages, list_source, binding, created_at, updated_at
)
SELECT
  gen_random_uuid(), c.id, 'addressLine', 16, 'textarea',
  FALSE, FALSE, FALSE, TRUE,
  $json$["label","placeholder","helpText","required"]$json$::jsonb,
  $json${
  "label": "Dirección Domiciliar Completa",
  "placeholder": "Calle, avenida, número de casa, referencias...",
  "maxLength": 300,
  "rows": 3
}$json$::jsonb,
  NULL,
  NULL,
  $json${
  "mapsTo": "Patient.address",
  "extractor": "address",
  "part": "line",
  "fixed": {
    "use": "home"
  }
}$json$::jsonb,
  now(), now()
FROM form_catalogs c
WHERE c.form_code = 'patient-registration' AND c.version = '1.0.0'
ON CONFLICT (catalog_id, field_key) DO UPDATE
  SET sort_order  = EXCLUDED.sort_order,
      widget      = EXCLUDED.widget,
      core        = EXCLUDED.core,
      custom      = EXCLUDED.custom,
      email       = EXCLUDED.email,
      active      = TRUE,
      editable    = EXCLUDED.editable,
      defaults    = EXCLUDED.defaults,
      messages    = EXCLUDED.messages,
      list_source = EXCLUDED.list_source,
      binding     = EXCLUDED.binding,
      updated_at  = now();

-- 17 · addressPostalCode (text)
INSERT INTO form_catalog_fields (
  id, catalog_id, field_key, sort_order, widget, core, custom, email, active,
  editable, defaults, messages, list_source, binding, created_at, updated_at
)
SELECT
  gen_random_uuid(), c.id, 'addressPostalCode', 17, 'text',
  FALSE, FALSE, FALSE, TRUE,
  $json$["label","placeholder","helpText","required"]$json$::jsonb,
  $json${
  "label": "Código Postal",
  "placeholder": "Ej: 11101",
  "maxLength": 10
}$json$::jsonb,
  NULL,
  NULL,
  $json${
  "mapsTo": "Patient.address",
  "extractor": "address",
  "part": "postalCode"
}$json$::jsonb,
  now(), now()
FROM form_catalogs c
WHERE c.form_code = 'patient-registration' AND c.version = '1.0.0'
ON CONFLICT (catalog_id, field_key) DO UPDATE
  SET sort_order  = EXCLUDED.sort_order,
      widget      = EXCLUDED.widget,
      core        = EXCLUDED.core,
      custom      = EXCLUDED.custom,
      email       = EXCLUDED.email,
      active      = TRUE,
      editable    = EXCLUDED.editable,
      defaults    = EXCLUDED.defaults,
      messages    = EXCLUDED.messages,
      list_source = EXCLUDED.list_source,
      binding     = EXCLUDED.binding,
      updated_at  = now();

-- 18 · maritalStatus (select)
INSERT INTO form_catalog_fields (
  id, catalog_id, field_key, sort_order, widget, core, custom, email, active,
  editable, defaults, messages, list_source, binding, created_at, updated_at
)
SELECT
  gen_random_uuid(), c.id, 'maritalStatus', 18, 'select',
  FALSE, FALSE, FALSE, TRUE,
  $json$["label","placeholder","helpText","required"]$json$::jsonb,
  $json${
  "label": "Estado Civil",
  "placeholder": "Seleccione..."
}$json$::jsonb,
  NULL,
  $json${
  "source": "valueset",
  "url": "http://hl7.org/fhir/ValueSet/marital-status"
}$json$::jsonb,
  $json${
  "mapsTo": "Patient.maritalStatus",
  "extractor": "codeable-concept",
  "codingSystem": "http://terminology.hl7.org/CodeSystem/v3-MaritalStatus"
}$json$::jsonb,
  now(), now()
FROM form_catalogs c
WHERE c.form_code = 'patient-registration' AND c.version = '1.0.0'
ON CONFLICT (catalog_id, field_key) DO UPDATE
  SET sort_order  = EXCLUDED.sort_order,
      widget      = EXCLUDED.widget,
      core        = EXCLUDED.core,
      custom      = EXCLUDED.custom,
      email       = EXCLUDED.email,
      active      = TRUE,
      editable    = EXCLUDED.editable,
      defaults    = EXCLUDED.defaults,
      messages    = EXCLUDED.messages,
      list_source = EXCLUDED.list_source,
      binding     = EXCLUDED.binding,
      updated_at  = now();

-- 19 · responsibleFullName (text)
INSERT INTO form_catalog_fields (
  id, catalog_id, field_key, sort_order, widget, core, custom, email, active,
  editable, defaults, messages, list_source, binding, created_at, updated_at
)
SELECT
  gen_random_uuid(), c.id, 'responsibleFullName', 19, 'text',
  FALSE, FALSE, FALSE, TRUE,
  $json$["label","placeholder","helpText","required"]$json$::jsonb,
  $json${
  "label": "Nombre Completo del Contacto",
  "placeholder": "Nombre y apellidos"
}$json$::jsonb,
  NULL,
  NULL,
  $json${
  "mapsTo": "RelatedPerson.name",
  "extractor": "human-name",
  "part": "full",
  "identifying": true
}$json$::jsonb,
  now(), now()
FROM form_catalogs c
WHERE c.form_code = 'patient-registration' AND c.version = '1.0.0'
ON CONFLICT (catalog_id, field_key) DO UPDATE
  SET sort_order  = EXCLUDED.sort_order,
      widget      = EXCLUDED.widget,
      core        = EXCLUDED.core,
      custom      = EXCLUDED.custom,
      email       = EXCLUDED.email,
      active      = TRUE,
      editable    = EXCLUDED.editable,
      defaults    = EXCLUDED.defaults,
      messages    = EXCLUDED.messages,
      list_source = EXCLUDED.list_source,
      binding     = EXCLUDED.binding,
      updated_at  = now();

-- 20 · responsibleDetailRelationship (select)
INSERT INTO form_catalog_fields (
  id, catalog_id, field_key, sort_order, widget, core, custom, email, active,
  editable, defaults, messages, list_source, binding, created_at, updated_at
)
SELECT
  gen_random_uuid(), c.id, 'responsibleDetailRelationship', 20, 'select',
  FALSE, FALSE, FALSE, TRUE,
  $json$["label","placeholder","helpText","required"]$json$::jsonb,
  $json${
  "label": "Parentesco / Relación",
  "placeholder": "Seleccione..."
}$json$::jsonb,
  NULL,
  $json${
  "source": "static",
  "options": [
    {
      "value": "parent",
      "label": "Padre/Madre"
    },
    {
      "value": "spouse",
      "label": "Cónyuge"
    },
    {
      "value": "child",
      "label": "Hijo/a"
    },
    {
      "value": "sibling",
      "label": "Hermano/a"
    },
    {
      "value": "guardian",
      "label": "Tutor Legal"
    },
    {
      "value": "other",
      "label": "Otro"
    }
  ]
}$json$::jsonb,
  $json${
  "mapsTo": "RelatedPerson.relationship",
  "extractor": "codeable-concept",
  "codingSystem": "http://terminology.hl7.org/CodeSystem/v3-RoleCode",
  "codeMap": {
    "parent": {
      "code": "PRN",
      "display": "Padre/Madre"
    },
    "spouse": {
      "code": "SPS",
      "display": "Cónyuge"
    },
    "child": {
      "code": "CHILD",
      "display": "Hijo/a"
    },
    "sibling": {
      "code": "SIB",
      "display": "Hermano/a"
    },
    "guardian": {
      "code": "GUARD",
      "display": "Tutor Legal"
    },
    "grandparent": {
      "code": "GRPRN",
      "display": "Abuelo/a"
    },
    "uncle": {
      "code": "AUNT",
      "display": "Tío/a"
    },
    "friend": {
      "code": "FRND",
      "display": "Amigo/a"
    },
    "emergency": {
      "code": "EMC",
      "display": "Contacto de emergencia"
    },
    "other": {
      "code": "O",
      "display": "Otro"
    }
  }
}$json$::jsonb,
  now(), now()
FROM form_catalogs c
WHERE c.form_code = 'patient-registration' AND c.version = '1.0.0'
ON CONFLICT (catalog_id, field_key) DO UPDATE
  SET sort_order  = EXCLUDED.sort_order,
      widget      = EXCLUDED.widget,
      core        = EXCLUDED.core,
      custom      = EXCLUDED.custom,
      email       = EXCLUDED.email,
      active      = TRUE,
      editable    = EXCLUDED.editable,
      defaults    = EXCLUDED.defaults,
      messages    = EXCLUDED.messages,
      list_source = EXCLUDED.list_source,
      binding     = EXCLUDED.binding,
      updated_at  = now();

-- 21 · responsibleDni (text)
INSERT INTO form_catalog_fields (
  id, catalog_id, field_key, sort_order, widget, core, custom, email, active,
  editable, defaults, messages, list_source, binding, created_at, updated_at
)
SELECT
  gen_random_uuid(), c.id, 'responsibleDni', 21, 'text',
  FALSE, FALSE, FALSE, TRUE,
  $json$["label","placeholder","helpText","required"]$json$::jsonb,
  $json${
  "label": "Identidad del Responsable (DNI)",
  "placeholder": "0801-1990-12345",
  "inputMode": "numeric"
}$json$::jsonb,
  NULL,
  NULL,
  $json${
  "mapsTo": "RelatedPerson.identifier",
  "extractor": "identifier",
  "part": "value",
  "fixedType": "IdentidadNacional",
  "identifying": true
}$json$::jsonb,
  now(), now()
FROM form_catalogs c
WHERE c.form_code = 'patient-registration' AND c.version = '1.0.0'
ON CONFLICT (catalog_id, field_key) DO UPDATE
  SET sort_order  = EXCLUDED.sort_order,
      widget      = EXCLUDED.widget,
      core        = EXCLUDED.core,
      custom      = EXCLUDED.custom,
      email       = EXCLUDED.email,
      active      = TRUE,
      editable    = EXCLUDED.editable,
      defaults    = EXCLUDED.defaults,
      messages    = EXCLUDED.messages,
      list_source = EXCLUDED.list_source,
      binding     = EXCLUDED.binding,
      updated_at  = now();

-- 22 · responsiblePhoneMobile (text)
INSERT INTO form_catalog_fields (
  id, catalog_id, field_key, sort_order, widget, core, custom, email, active,
  editable, defaults, messages, list_source, binding, created_at, updated_at
)
SELECT
  gen_random_uuid(), c.id, 'responsiblePhoneMobile', 22, 'text',
  FALSE, FALSE, FALSE, TRUE,
  $json$["label","placeholder","helpText","required"]$json$::jsonb,
  $json${
  "label": "Teléfono Celular",
  "placeholder": "+504 9999-9999",
  "inputMode": "tel"
}$json$::jsonb,
  NULL,
  NULL,
  $json${
  "mapsTo": "RelatedPerson.telecom",
  "extractor": "contact-point",
  "system": "phone",
  "use": "mobile",
  "identifying": true
}$json$::jsonb,
  now(), now()
FROM form_catalogs c
WHERE c.form_code = 'patient-registration' AND c.version = '1.0.0'
ON CONFLICT (catalog_id, field_key) DO UPDATE
  SET sort_order  = EXCLUDED.sort_order,
      widget      = EXCLUDED.widget,
      core        = EXCLUDED.core,
      custom      = EXCLUDED.custom,
      email       = EXCLUDED.email,
      active      = TRUE,
      editable    = EXCLUDED.editable,
      defaults    = EXCLUDED.defaults,
      messages    = EXCLUDED.messages,
      list_source = EXCLUDED.list_source,
      binding     = EXCLUDED.binding,
      updated_at  = now();

-- 23 · responsiblePhoneHome (text)
INSERT INTO form_catalog_fields (
  id, catalog_id, field_key, sort_order, widget, core, custom, email, active,
  editable, defaults, messages, list_source, binding, created_at, updated_at
)
SELECT
  gen_random_uuid(), c.id, 'responsiblePhoneHome', 23, 'text',
  FALSE, FALSE, FALSE, TRUE,
  $json$["label","placeholder","helpText","required"]$json$::jsonb,
  $json${
  "label": "Teléfono Fijo",
  "placeholder": "+504 2222-2222",
  "inputMode": "tel"
}$json$::jsonb,
  NULL,
  NULL,
  $json${
  "mapsTo": "RelatedPerson.telecom",
  "extractor": "contact-point",
  "system": "phone",
  "use": "home",
  "identifying": true
}$json$::jsonb,
  now(), now()
FROM form_catalogs c
WHERE c.form_code = 'patient-registration' AND c.version = '1.0.0'
ON CONFLICT (catalog_id, field_key) DO UPDATE
  SET sort_order  = EXCLUDED.sort_order,
      widget      = EXCLUDED.widget,
      core        = EXCLUDED.core,
      custom      = EXCLUDED.custom,
      email       = EXCLUDED.email,
      active      = TRUE,
      editable    = EXCLUDED.editable,
      defaults    = EXCLUDED.defaults,
      messages    = EXCLUDED.messages,
      list_source = EXCLUDED.list_source,
      binding     = EXCLUDED.binding,
      updated_at  = now();

-- 24 · responsibleBirthDate (date)
INSERT INTO form_catalog_fields (
  id, catalog_id, field_key, sort_order, widget, core, custom, email, active,
  editable, defaults, messages, list_source, binding, created_at, updated_at
)
SELECT
  gen_random_uuid(), c.id, 'responsibleBirthDate', 24, 'date',
  FALSE, FALSE, FALSE, TRUE,
  $json$["label","helpText","required"]$json$::jsonb,
  $json${
  "label": "Fecha de Nacimiento"
}$json$::jsonb,
  NULL,
  NULL,
  $json${
  "mapsTo": "RelatedPerson.birthDate",
  "extractor": "primitive"
}$json$::jsonb,
  now(), now()
FROM form_catalogs c
WHERE c.form_code = 'patient-registration' AND c.version = '1.0.0'
ON CONFLICT (catalog_id, field_key) DO UPDATE
  SET sort_order  = EXCLUDED.sort_order,
      widget      = EXCLUDED.widget,
      core        = EXCLUDED.core,
      custom      = EXCLUDED.custom,
      email       = EXCLUDED.email,
      active      = TRUE,
      editable    = EXCLUDED.editable,
      defaults    = EXCLUDED.defaults,
      messages    = EXCLUDED.messages,
      list_source = EXCLUDED.list_source,
      binding     = EXCLUDED.binding,
      updated_at  = now();

-- 25 · responsibleAddress (text)
INSERT INTO form_catalog_fields (
  id, catalog_id, field_key, sort_order, widget, core, custom, email, active,
  editable, defaults, messages, list_source, binding, created_at, updated_at
)
SELECT
  gen_random_uuid(), c.id, 'responsibleAddress', 25, 'text',
  FALSE, FALSE, FALSE, TRUE,
  $json$["label","placeholder","helpText","required"]$json$::jsonb,
  $json${
  "label": "Dirección del Contacto",
  "placeholder": "Dirección completa"
}$json$::jsonb,
  NULL,
  NULL,
  $json${
  "mapsTo": "RelatedPerson.address",
  "extractor": "address",
  "part": "text",
  "fixed": {
    "use": "home"
  }
}$json$::jsonb,
  now(), now()
FROM form_catalogs c
WHERE c.form_code = 'patient-registration' AND c.version = '1.0.0'
ON CONFLICT (catalog_id, field_key) DO UPDATE
  SET sort_order  = EXCLUDED.sort_order,
      widget      = EXCLUDED.widget,
      core        = EXCLUDED.core,
      custom      = EXCLUDED.custom,
      email       = EXCLUDED.email,
      active      = TRUE,
      editable    = EXCLUDED.editable,
      defaults    = EXCLUDED.defaults,
      messages    = EXCLUDED.messages,
      list_source = EXCLUDED.list_source,
      binding     = EXCLUDED.binding,
      updated_at  = now();

-- 26 · custom_text (text, personalizado)
INSERT INTO form_catalog_fields (
  id, catalog_id, field_key, sort_order, widget, core, custom, email, active,
  editable, defaults, messages, list_source, binding, created_at, updated_at
)
SELECT
  gen_random_uuid(), c.id, 'custom_text', 26, 'text',
  FALSE, TRUE, FALSE, TRUE,
  $json$["label","placeholder","helpText","required"]$json$::jsonb,
  $json${
  "label": "Campo de texto",
  "maxLength": 100
}$json$::jsonb,
  NULL,
  NULL,
  $json${
  "mapsTo": "Patient.extension",
  "extractor": "extension",
  "valueType": "string"
}$json$::jsonb,
  now(), now()
FROM form_catalogs c
WHERE c.form_code = 'patient-registration' AND c.version = '1.0.0'
ON CONFLICT (catalog_id, field_key) DO UPDATE
  SET sort_order  = EXCLUDED.sort_order,
      widget      = EXCLUDED.widget,
      core        = EXCLUDED.core,
      custom      = EXCLUDED.custom,
      email       = EXCLUDED.email,
      active      = TRUE,
      editable    = EXCLUDED.editable,
      defaults    = EXCLUDED.defaults,
      messages    = EXCLUDED.messages,
      list_source = EXCLUDED.list_source,
      binding     = EXCLUDED.binding,
      updated_at  = now();

-- 27 · custom_textarea (textarea, personalizado)
INSERT INTO form_catalog_fields (
  id, catalog_id, field_key, sort_order, widget, core, custom, email, active,
  editable, defaults, messages, list_source, binding, created_at, updated_at
)
SELECT
  gen_random_uuid(), c.id, 'custom_textarea', 27, 'textarea',
  FALSE, TRUE, FALSE, TRUE,
  $json$["label","placeholder","helpText","required"]$json$::jsonb,
  $json${
  "label": "Campo de texto largo",
  "maxLength": 500,
  "rows": 3
}$json$::jsonb,
  NULL,
  NULL,
  $json${
  "mapsTo": "Patient.extension",
  "extractor": "extension",
  "valueType": "string"
}$json$::jsonb,
  now(), now()
FROM form_catalogs c
WHERE c.form_code = 'patient-registration' AND c.version = '1.0.0'
ON CONFLICT (catalog_id, field_key) DO UPDATE
  SET sort_order  = EXCLUDED.sort_order,
      widget      = EXCLUDED.widget,
      core        = EXCLUDED.core,
      custom      = EXCLUDED.custom,
      email       = EXCLUDED.email,
      active      = TRUE,
      editable    = EXCLUDED.editable,
      defaults    = EXCLUDED.defaults,
      messages    = EXCLUDED.messages,
      list_source = EXCLUDED.list_source,
      binding     = EXCLUDED.binding,
      updated_at  = now();

-- 28 · custom_date (date, personalizado)
INSERT INTO form_catalog_fields (
  id, catalog_id, field_key, sort_order, widget, core, custom, email, active,
  editable, defaults, messages, list_source, binding, created_at, updated_at
)
SELECT
  gen_random_uuid(), c.id, 'custom_date', 28, 'date',
  FALSE, TRUE, FALSE, TRUE,
  $json$["label","helpText","required"]$json$::jsonb,
  $json${
  "label": "Campo de fecha"
}$json$::jsonb,
  NULL,
  NULL,
  $json${
  "mapsTo": "Patient.extension",
  "extractor": "extension",
  "valueType": "date"
}$json$::jsonb,
  now(), now()
FROM form_catalogs c
WHERE c.form_code = 'patient-registration' AND c.version = '1.0.0'
ON CONFLICT (catalog_id, field_key) DO UPDATE
  SET sort_order  = EXCLUDED.sort_order,
      widget      = EXCLUDED.widget,
      core        = EXCLUDED.core,
      custom      = EXCLUDED.custom,
      email       = EXCLUDED.email,
      active      = TRUE,
      editable    = EXCLUDED.editable,
      defaults    = EXCLUDED.defaults,
      messages    = EXCLUDED.messages,
      list_source = EXCLUDED.list_source,
      binding     = EXCLUDED.binding,
      updated_at  = now();

-- 29 · custom_toggle (toggle, personalizado)
INSERT INTO form_catalog_fields (
  id, catalog_id, field_key, sort_order, widget, core, custom, email, active,
  editable, defaults, messages, list_source, binding, created_at, updated_at
)
SELECT
  gen_random_uuid(), c.id, 'custom_toggle', 29, 'toggle',
  FALSE, TRUE, FALSE, TRUE,
  $json$["label","description","helpText","onLabel","offLabel"]$json$::jsonb,
  $json${
  "label": "Campo sí/no",
  "defaultValue": false
}$json$::jsonb,
  NULL,
  NULL,
  $json${
  "mapsTo": "Patient.extension",
  "extractor": "extension",
  "valueType": "boolean"
}$json$::jsonb,
  now(), now()
FROM form_catalogs c
WHERE c.form_code = 'patient-registration' AND c.version = '1.0.0'
ON CONFLICT (catalog_id, field_key) DO UPDATE
  SET sort_order  = EXCLUDED.sort_order,
      widget      = EXCLUDED.widget,
      core        = EXCLUDED.core,
      custom      = EXCLUDED.custom,
      email       = EXCLUDED.email,
      active      = TRUE,
      editable    = EXCLUDED.editable,
      defaults    = EXCLUDED.defaults,
      messages    = EXCLUDED.messages,
      list_source = EXCLUDED.list_source,
      binding     = EXCLUDED.binding,
      updated_at  = now();

-- 30 · medicalRecordNumber (text)
INSERT INTO form_catalog_fields (
  id, catalog_id, field_key, sort_order, widget, core, custom, email, active,
  editable, defaults, messages, list_source, binding, created_at, updated_at
)
SELECT
  gen_random_uuid(), c.id, 'medicalRecordNumber', 30, 'text',
  FALSE, FALSE, FALSE, TRUE,
  $json$[
  "label",
  "placeholder",
  "helpText",
  "required"
]$json$::jsonb,
  $json${
  "label": "Número de Expediente Clínico",
  "placeholder": "EXP-000001",
  "required": true
}$json$::jsonb,
  $json${
  "required": "El número de expediente es obligatorio"
}$json$::jsonb,
  NULL,
  $json${
  "mapsTo": "Patient.identifier",
  "extractor": "secondary-identifier",
  "system": "https://imed.app/identifiers/secondary/medical-record",
  "fixedType": "MR"
}$json$::jsonb,
  now(), now()
FROM form_catalogs c
WHERE c.form_code = 'patient-registration' AND c.version = '1.0.0'
ON CONFLICT (catalog_id, field_key) DO UPDATE
  SET sort_order  = EXCLUDED.sort_order,
      widget      = EXCLUDED.widget,
      core        = EXCLUDED.core,
      custom      = EXCLUDED.custom,
      email       = EXCLUDED.email,
      active      = TRUE,
      editable    = EXCLUDED.editable,
      defaults    = EXCLUDED.defaults,
      messages    = EXCLUDED.messages,
      list_source = EXCLUDED.list_source,
      binding     = EXCLUDED.binding,
      updated_at  = now();

-- 31 · registrationDate (date)
INSERT INTO form_catalog_fields (
  id, catalog_id, field_key, sort_order, widget, core, custom, email, active,
  editable, defaults, messages, list_source, binding, created_at, updated_at
)
SELECT
  gen_random_uuid(), c.id, 'registrationDate', 31, 'date',
  FALSE, FALSE, FALSE, TRUE,
  $json$[
  "label",
  "helpText",
  "required"
]$json$::jsonb,
  $json${
  "label": "Fecha de Registro",
  "required": true
}$json$::jsonb,
  $json${
  "required": "La fecha de registro es obligatoria"
}$json$::jsonb,
  NULL,
  $json${
  "mapsTo": "Patient.extension",
  "extractor": "extension",
  "extensionUrl": "https://imed.app/fhir/StructureDefinition/patient-registration-date",
  "valueType": "date"
}$json$::jsonb,
  now(), now()
FROM form_catalogs c
WHERE c.form_code = 'patient-registration' AND c.version = '1.0.0'
ON CONFLICT (catalog_id, field_key) DO UPDATE
  SET sort_order  = EXCLUDED.sort_order,
      widget      = EXCLUDED.widget,
      core        = EXCLUDED.core,
      custom      = EXCLUDED.custom,
      email       = EXCLUDED.email,
      active      = TRUE,
      editable    = EXCLUDED.editable,
      defaults    = EXCLUDED.defaults,
      messages    = EXCLUDED.messages,
      list_source = EXCLUDED.list_source,
      binding     = EXCLUDED.binding,
      updated_at  = now();

-- 32 · nationality (select)
INSERT INTO form_catalog_fields (
  id, catalog_id, field_key, sort_order, widget, core, custom, email, active,
  editable, defaults, messages, list_source, binding, created_at, updated_at
)
SELECT
  gen_random_uuid(), c.id, 'nationality', 32, 'select',
  FALSE, FALSE, FALSE, TRUE,
  $json$[
  "label",
  "placeholder",
  "helpText",
  "required"
]$json$::jsonb,
  $json${
  "label": "Nacionalidad del Paciente",
  "placeholder": "Seleccione..."
}$json$::jsonb,
  NULL,
  $json${
  "source": "static",
  "options": [
    {
      "value": "HN",
      "label": "Hondureña"
    },
    {
      "value": "GT",
      "label": "Guatemalteca"
    },
    {
      "value": "SV",
      "label": "Salvadoreña"
    },
    {
      "value": "NI",
      "label": "Nicaragüense"
    },
    {
      "value": "CR",
      "label": "Costarricense"
    },
    {
      "value": "PA",
      "label": "Panameña"
    },
    {
      "value": "MX",
      "label": "Mexicana"
    },
    {
      "value": "US",
      "label": "Estadounidense"
    },
    {
      "value": "OTHER",
      "label": "Otra"
    }
  ]
}$json$::jsonb,
  $json${
  "mapsTo": "Patient.extension",
  "extractor": "extension",
  "extensionUrl": "https://imed.app/fhir/StructureDefinition/patient-nationality",
  "valueType": "code"
}$json$::jsonb,
  now(), now()
FROM form_catalogs c
WHERE c.form_code = 'patient-registration' AND c.version = '1.0.0'
ON CONFLICT (catalog_id, field_key) DO UPDATE
  SET sort_order  = EXCLUDED.sort_order,
      widget      = EXCLUDED.widget,
      core        = EXCLUDED.core,
      custom      = EXCLUDED.custom,
      email       = EXCLUDED.email,
      active      = TRUE,
      editable    = EXCLUDED.editable,
      defaults    = EXCLUDED.defaults,
      messages    = EXCLUDED.messages,
      list_source = EXCLUDED.list_source,
      binding     = EXCLUDED.binding,
      updated_at  = now();

-- 33 · origin (select)
INSERT INTO form_catalog_fields (
  id, catalog_id, field_key, sort_order, widget, core, custom, email, active,
  editable, defaults, messages, list_source, binding, created_at, updated_at
)
SELECT
  gen_random_uuid(), c.id, 'origin', 33, 'select',
  FALSE, FALSE, FALSE, TRUE,
  $json$[
  "label",
  "placeholder",
  "helpText",
  "required"
]$json$::jsonb,
  $json${
  "label": "Procedencia",
  "placeholder": "Seleccione..."
}$json$::jsonb,
  NULL,
  $json${
  "source": "static",
  "options": [
    {
      "value": "national",
      "label": "Nacional"
    },
    {
      "value": "foreign",
      "label": "Extranjero"
    }
  ]
}$json$::jsonb,
  $json${
  "mapsTo": "Patient.extension",
  "extractor": "extension",
  "extensionUrl": "https://imed.app/fhir/StructureDefinition/patient-origin",
  "valueType": "code"
}$json$::jsonb,
  now(), now()
FROM form_catalogs c
WHERE c.form_code = 'patient-registration' AND c.version = '1.0.0'
ON CONFLICT (catalog_id, field_key) DO UPDATE
  SET sort_order  = EXCLUDED.sort_order,
      widget      = EXCLUDED.widget,
      core        = EXCLUDED.core,
      custom      = EXCLUDED.custom,
      email       = EXCLUDED.email,
      active      = TRUE,
      editable    = EXCLUDED.editable,
      defaults    = EXCLUDED.defaults,
      messages    = EXCLUDED.messages,
      list_source = EXCLUDED.list_source,
      binding     = EXCLUDED.binding,
      updated_at  = now();

-- 34 · ethnicity (select)
INSERT INTO form_catalog_fields (
  id, catalog_id, field_key, sort_order, widget, core, custom, email, active,
  editable, defaults, messages, list_source, binding, created_at, updated_at
)
SELECT
  gen_random_uuid(), c.id, 'ethnicity', 34, 'select',
  FALSE, FALSE, FALSE, TRUE,
  $json$[
  "label",
  "placeholder",
  "helpText",
  "required"
]$json$::jsonb,
  $json${
  "label": "Etnia / Pueblo Indígena",
  "placeholder": "Seleccione..."
}$json$::jsonb,
  NULL,
  $json${
  "source": "static",
  "options": [
    {
      "value": "none",
      "label": "Ninguno"
    },
    {
      "value": "lenca",
      "label": "Lenca"
    },
    {
      "value": "misquito",
      "label": "Misquito"
    },
    {
      "value": "garifuna",
      "label": "Garífuna"
    },
    {
      "value": "tolupan",
      "label": "Tolupán"
    },
    {
      "value": "chorti",
      "label": "Chortí"
    },
    {
      "value": "pech",
      "label": "Pech"
    },
    {
      "value": "tawahka",
      "label": "Tawahka"
    },
    {
      "value": "nahua",
      "label": "Nahua"
    },
    {
      "value": "negro_english",
      "label": "Negro de Habla Inglesa"
    }
  ]
}$json$::jsonb,
  $json${
  "mapsTo": "Patient.extension",
  "extractor": "extension",
  "extensionUrl": "https://imed.app/fhir/StructureDefinition/patient-ethnicity",
  "valueType": "code"
}$json$::jsonb,
  now(), now()
FROM form_catalogs c
WHERE c.form_code = 'patient-registration' AND c.version = '1.0.0'
ON CONFLICT (catalog_id, field_key) DO UPDATE
  SET sort_order  = EXCLUDED.sort_order,
      widget      = EXCLUDED.widget,
      core        = EXCLUDED.core,
      custom      = EXCLUDED.custom,
      email       = EXCLUDED.email,
      active      = TRUE,
      editable    = EXCLUDED.editable,
      defaults    = EXCLUDED.defaults,
      messages    = EXCLUDED.messages,
      list_source = EXCLUDED.list_source,
      binding     = EXCLUDED.binding,
      updated_at  = now();

-- 35 · religion (select)
INSERT INTO form_catalog_fields (
  id, catalog_id, field_key, sort_order, widget, core, custom, email, active,
  editable, defaults, messages, list_source, binding, created_at, updated_at
)
SELECT
  gen_random_uuid(), c.id, 'religion', 35, 'select',
  FALSE, FALSE, FALSE, TRUE,
  $json$[
  "label",
  "placeholder",
  "helpText",
  "required"
]$json$::jsonb,
  $json${
  "label": "Religión",
  "placeholder": "Seleccione..."
}$json$::jsonb,
  NULL,
  $json${
  "source": "static",
  "options": [
    {
      "value": "1007",
      "label": "Católica"
    },
    {
      "value": "1077",
      "label": "Evangélica / Protestante"
    },
    {
      "value": "1025",
      "label": "Testigo de Jehová"
    },
    {
      "value": "1041",
      "label": "Adventista"
    },
    {
      "value": "1023",
      "label": "Mormón"
    },
    {
      "value": "1059",
      "label": "Musulmana"
    },
    {
      "value": "1019",
      "label": "Judía"
    },
    {
      "value": "1071",
      "label": "Ninguna"
    },
    {
      "value": "OTHER",
      "label": "Otra"
    }
  ]
}$json$::jsonb,
  $json${
  "mapsTo": "Patient.extension",
  "extractor": "extension",
  "extensionUrl": "https://imed.app/fhir/StructureDefinition/patient-religion",
  "valueType": "code"
}$json$::jsonb,
  now(), now()
FROM form_catalogs c
WHERE c.form_code = 'patient-registration' AND c.version = '1.0.0'
ON CONFLICT (catalog_id, field_key) DO UPDATE
  SET sort_order  = EXCLUDED.sort_order,
      widget      = EXCLUDED.widget,
      core        = EXCLUDED.core,
      custom      = EXCLUDED.custom,
      email       = EXCLUDED.email,
      active      = TRUE,
      editable    = EXCLUDED.editable,
      defaults    = EXCLUDED.defaults,
      messages    = EXCLUDED.messages,
      list_source = EXCLUDED.list_source,
      binding     = EXCLUDED.binding,
      updated_at  = now();

-- 36 · patientCategory (select)
INSERT INTO form_catalog_fields (
  id, catalog_id, field_key, sort_order, widget, core, custom, email, active,
  editable, defaults, messages, list_source, binding, created_at, updated_at
)
SELECT
  gen_random_uuid(), c.id, 'patientCategory', 36, 'select',
  FALSE, FALSE, FALSE, TRUE,
  $json$[
  "label",
  "placeholder",
  "helpText",
  "required"
]$json$::jsonb,
  $json${
  "label": "Categoría de Paciente",
  "placeholder": "Seleccione..."
}$json$::jsonb,
  NULL,
  $json${
  "source": "static",
  "options": [
    {
      "value": "ambulatory",
      "label": "Ambulatorio"
    },
    {
      "value": "hospitalized",
      "label": "Hospitalizado"
    },
    {
      "value": "emergency",
      "label": "Emergencia"
    },
    {
      "value": "observation",
      "label": "Observación"
    }
  ]
}$json$::jsonb,
  $json${
  "mapsTo": "Patient.extension",
  "extractor": "extension",
  "extensionUrl": "https://imed.app/fhir/StructureDefinition/patient-category",
  "valueType": "code"
}$json$::jsonb,
  now(), now()
FROM form_catalogs c
WHERE c.form_code = 'patient-registration' AND c.version = '1.0.0'
ON CONFLICT (catalog_id, field_key) DO UPDATE
  SET sort_order  = EXCLUDED.sort_order,
      widget      = EXCLUDED.widget,
      core        = EXCLUDED.core,
      custom      = EXCLUDED.custom,
      email       = EXCLUDED.email,
      active      = TRUE,
      editable    = EXCLUDED.editable,
      defaults    = EXCLUDED.defaults,
      messages    = EXCLUDED.messages,
      list_source = EXCLUDED.list_source,
      binding     = EXCLUDED.binding,
      updated_at  = now();

-- 37 · taxId (text)
INSERT INTO form_catalog_fields (
  id, catalog_id, field_key, sort_order, widget, core, custom, email, active,
  editable, defaults, messages, list_source, binding, created_at, updated_at
)
SELECT
  gen_random_uuid(), c.id, 'taxId', 37, 'text',
  FALSE, FALSE, FALSE, TRUE,
  $json$[
  "label",
  "placeholder",
  "helpText",
  "required"
]$json$::jsonb,
  $json${
  "label": "RTN / Registro Tributario",
  "placeholder": "0801-1990-12345-0"
}$json$::jsonb,
  NULL,
  NULL,
  $json${
  "mapsTo": "Patient.identifier",
  "extractor": "secondary-identifier",
  "system": "https://imed.app/identifiers/secondary/tax",
  "fixedType": "TAX"
}$json$::jsonb,
  now(), now()
FROM form_catalogs c
WHERE c.form_code = 'patient-registration' AND c.version = '1.0.0'
ON CONFLICT (catalog_id, field_key) DO UPDATE
  SET sort_order  = EXCLUDED.sort_order,
      widget      = EXCLUDED.widget,
      core        = EXCLUDED.core,
      custom      = EXCLUDED.custom,
      email       = EXCLUDED.email,
      active      = TRUE,
      editable    = EXCLUDED.editable,
      defaults    = EXCLUDED.defaults,
      messages    = EXCLUDED.messages,
      list_source = EXCLUDED.list_source,
      binding     = EXCLUDED.binding,
      updated_at  = now();

-- 38 · employmentStatus (select)
INSERT INTO form_catalog_fields (
  id, catalog_id, field_key, sort_order, widget, core, custom, email, active,
  editable, defaults, messages, list_source, binding, created_at, updated_at
)
SELECT
  gen_random_uuid(), c.id, 'employmentStatus', 38, 'select',
  FALSE, FALSE, FALSE, TRUE,
  $json$[
  "label",
  "placeholder",
  "helpText",
  "required"
]$json$::jsonb,
  $json${
  "label": "Situación Laboral",
  "placeholder": "Seleccione..."
}$json$::jsonb,
  NULL,
  $json${
  "source": "static",
  "options": [
    {
      "value": "employed",
      "label": "Empleado"
    },
    {
      "value": "unemployed",
      "label": "Desempleado"
    },
    {
      "value": "retired",
      "label": "Jubilado"
    },
    {
      "value": "student",
      "label": "Estudiante"
    },
    {
      "value": "self_employed",
      "label": "Trabajador Independiente"
    },
    {
      "value": "homemaker",
      "label": "Ama de Casa"
    }
  ]
}$json$::jsonb,
  $json${
  "mapsTo": "Patient.extension",
  "extractor": "extension",
  "extensionUrl": "https://imed.app/fhir/StructureDefinition/patient-employment-status",
  "valueType": "code"
}$json$::jsonb,
  now(), now()
FROM form_catalogs c
WHERE c.form_code = 'patient-registration' AND c.version = '1.0.0'
ON CONFLICT (catalog_id, field_key) DO UPDATE
  SET sort_order  = EXCLUDED.sort_order,
      widget      = EXCLUDED.widget,
      core        = EXCLUDED.core,
      custom      = EXCLUDED.custom,
      email       = EXCLUDED.email,
      active      = TRUE,
      editable    = EXCLUDED.editable,
      defaults    = EXCLUDED.defaults,
      messages    = EXCLUDED.messages,
      list_source = EXCLUDED.list_source,
      binding     = EXCLUDED.binding,
      updated_at  = now();

-- 39 · generalNotes (textarea)
INSERT INTO form_catalog_fields (
  id, catalog_id, field_key, sort_order, widget, core, custom, email, active,
  editable, defaults, messages, list_source, binding, created_at, updated_at
)
SELECT
  gen_random_uuid(), c.id, 'generalNotes', 39, 'textarea',
  FALSE, FALSE, FALSE, TRUE,
  $json$[
  "label",
  "placeholder",
  "helpText",
  "required"
]$json$::jsonb,
  $json${
  "label": "Observaciones Generales",
  "placeholder": "Ingrese observaciones relevantes del paciente...",
  "maxLength": 1000,
  "rows": 3
}$json$::jsonb,
  NULL,
  NULL,
  $json${
  "mapsTo": "Patient.extension",
  "extractor": "extension",
  "extensionUrl": "https://imed.app/fhir/StructureDefinition/patient-general-notes",
  "valueType": "string"
}$json$::jsonb,
  now(), now()
FROM form_catalogs c
WHERE c.form_code = 'patient-registration' AND c.version = '1.0.0'
ON CONFLICT (catalog_id, field_key) DO UPDATE
  SET sort_order  = EXCLUDED.sort_order,
      widget      = EXCLUDED.widget,
      core        = EXCLUDED.core,
      custom      = EXCLUDED.custom,
      email       = EXCLUDED.email,
      active      = TRUE,
      editable    = EXCLUDED.editable,
      defaults    = EXCLUDED.defaults,
      messages    = EXCLUDED.messages,
      list_source = EXCLUDED.list_source,
      binding     = EXCLUDED.binding,
      updated_at  = now();

-- 40 · addressDistrict (text)
INSERT INTO form_catalog_fields (
  id, catalog_id, field_key, sort_order, widget, core, custom, email, active,
  editable, defaults, messages, list_source, binding, created_at, updated_at
)
SELECT
  gen_random_uuid(), c.id, 'addressDistrict', 40, 'text',
  FALSE, FALSE, FALSE, TRUE,
  $json$[
  "label",
  "placeholder",
  "helpText",
  "required"
]$json$::jsonb,
  $json${
  "label": "Aldea / Caserío / Barrio",
  "placeholder": "Ej: Col. Kennedy"
}$json$::jsonb,
  NULL,
  NULL,
  $json${
  "mapsTo": "Patient.address",
  "extractor": "address",
  "part": "district"
}$json$::jsonb,
  now(), now()
FROM form_catalogs c
WHERE c.form_code = 'patient-registration' AND c.version = '1.0.0'
ON CONFLICT (catalog_id, field_key) DO UPDATE
  SET sort_order  = EXCLUDED.sort_order,
      widget      = EXCLUDED.widget,
      core        = EXCLUDED.core,
      custom      = EXCLUDED.custom,
      email       = EXCLUDED.email,
      active      = TRUE,
      editable    = EXCLUDED.editable,
      defaults    = EXCLUDED.defaults,
      messages    = EXCLUDED.messages,
      list_source = EXCLUDED.list_source,
      binding     = EXCLUDED.binding,
      updated_at  = now();

-- 41 · educationLevel (select)
INSERT INTO form_catalog_fields (
  id, catalog_id, field_key, sort_order, widget, core, custom, email, active,
  editable, defaults, messages, list_source, binding, created_at, updated_at
)
SELECT
  gen_random_uuid(), c.id, 'educationLevel', 41, 'select',
  FALSE, FALSE, FALSE, TRUE,
  $json$[
  "label",
  "placeholder",
  "helpText",
  "required"
]$json$::jsonb,
  $json${
  "label": "Nivel de Escolaridad",
  "placeholder": "Seleccione..."
}$json$::jsonb,
  NULL,
  $json${
  "source": "static",
  "options": [
    {
      "value": "none",
      "label": "Ninguno"
    },
    {
      "value": "preschool",
      "label": "Preescolar"
    },
    {
      "value": "primary",
      "label": "Primaria"
    },
    {
      "value": "secondary",
      "label": "Secundaria"
    },
    {
      "value": "technical",
      "label": "Técnico"
    },
    {
      "value": "university",
      "label": "Universitario"
    },
    {
      "value": "postgraduate",
      "label": "Postgrado"
    }
  ]
}$json$::jsonb,
  $json${
  "mapsTo": "Patient.extension",
  "extractor": "extension",
  "extensionUrl": "https://imed.app/fhir/StructureDefinition/patient-education-level",
  "valueType": "code"
}$json$::jsonb,
  now(), now()
FROM form_catalogs c
WHERE c.form_code = 'patient-registration' AND c.version = '1.0.0'
ON CONFLICT (catalog_id, field_key) DO UPDATE
  SET sort_order  = EXCLUDED.sort_order,
      widget      = EXCLUDED.widget,
      core        = EXCLUDED.core,
      custom      = EXCLUDED.custom,
      email       = EXCLUDED.email,
      active      = TRUE,
      editable    = EXCLUDED.editable,
      defaults    = EXCLUDED.defaults,
      messages    = EXCLUDED.messages,
      list_source = EXCLUDED.list_source,
      binding     = EXCLUDED.binding,
      updated_at  = now();

-- 42 · bloodType (select)
INSERT INTO form_catalog_fields (
  id, catalog_id, field_key, sort_order, widget, core, custom, email, active,
  editable, defaults, messages, list_source, binding, created_at, updated_at
)
SELECT
  gen_random_uuid(), c.id, 'bloodType', 42, 'select',
  FALSE, FALSE, FALSE, TRUE,
  $json$[
  "label",
  "placeholder",
  "helpText",
  "required"
]$json$::jsonb,
  $json${
  "label": "Grupo Sanguíneo y Rh",
  "placeholder": "Seleccione..."
}$json$::jsonb,
  NULL,
  $json${
  "source": "static",
  "options": [
    {
      "value": "A+",
      "label": "A Rh+"
    },
    {
      "value": "A-",
      "label": "A Rh-"
    },
    {
      "value": "B+",
      "label": "B Rh+"
    },
    {
      "value": "B-",
      "label": "B Rh-"
    },
    {
      "value": "AB+",
      "label": "AB Rh+"
    },
    {
      "value": "AB-",
      "label": "AB Rh-"
    },
    {
      "value": "O+",
      "label": "O Rh+"
    },
    {
      "value": "O-",
      "label": "O Rh-"
    }
  ]
}$json$::jsonb,
  $json${
  "mapsTo": "Patient.extension",
  "extractor": "extension",
  "extensionUrl": "https://imed.app/fhir/StructureDefinition/patient-blood-type",
  "valueType": "code"
}$json$::jsonb,
  now(), now()
FROM form_catalogs c
WHERE c.form_code = 'patient-registration' AND c.version = '1.0.0'
ON CONFLICT (catalog_id, field_key) DO UPDATE
  SET sort_order  = EXCLUDED.sort_order,
      widget      = EXCLUDED.widget,
      core        = EXCLUDED.core,
      custom      = EXCLUDED.custom,
      email       = EXCLUDED.email,
      active      = TRUE,
      editable    = EXCLUDED.editable,
      defaults    = EXCLUDED.defaults,
      messages    = EXCLUDED.messages,
      list_source = EXCLUDED.list_source,
      binding     = EXCLUDED.binding,
      updated_at  = now();

-- 43 · hasDisability (toggle)
INSERT INTO form_catalog_fields (
  id, catalog_id, field_key, sort_order, widget, core, custom, email, active,
  editable, defaults, messages, list_source, binding, created_at, updated_at
)
SELECT
  gen_random_uuid(), c.id, 'hasDisability', 43, 'toggle',
  FALSE, FALSE, FALSE, TRUE,
  $json$["label","description","helpText","onLabel","offLabel","required"]$json$::jsonb,
  $json${
  "label": "¿Tiene Discapacidad?",
  "description": "Indicar si el paciente presenta algún tipo de discapacidad reconocida",
  "defaultValue": false
}$json$::jsonb,
  NULL,
  NULL,
  $json${
  "mapsTo": "Patient.extension",
  "extractor": "extension",
  "extensionUrl": "https://imed.app/fhir/StructureDefinition/patient-has-disability",
  "valueType": "boolean"
}$json$::jsonb,
  now(), now()
FROM form_catalogs c
WHERE c.form_code = 'patient-registration' AND c.version = '1.0.0'
ON CONFLICT (catalog_id, field_key) DO UPDATE
  SET sort_order  = EXCLUDED.sort_order,
      widget      = EXCLUDED.widget,
      core        = EXCLUDED.core,
      custom      = EXCLUDED.custom,
      email       = EXCLUDED.email,
      active      = TRUE,
      editable    = EXCLUDED.editable,
      defaults    = EXCLUDED.defaults,
      messages    = EXCLUDED.messages,
      list_source = EXCLUDED.list_source,
      binding     = EXCLUDED.binding,
      updated_at  = now();

-- 44 · disabilityType (select)
INSERT INTO form_catalog_fields (
  id, catalog_id, field_key, sort_order, widget, core, custom, email, active,
  editable, defaults, messages, list_source, binding, created_at, updated_at
)
SELECT
  gen_random_uuid(), c.id, 'disabilityType', 44, 'select',
  FALSE, FALSE, FALSE, TRUE,
  $json$[
  "label",
  "placeholder",
  "helpText",
  "required"
]$json$::jsonb,
  $json${
  "label": "Tipo de Discapacidad",
  "placeholder": "Seleccione..."
}$json$::jsonb,
  NULL,
  $json${
  "source": "static",
  "options": [
    {
      "value": "physical",
      "label": "Física"
    },
    {
      "value": "visual",
      "label": "Visual"
    },
    {
      "value": "hearing",
      "label": "Auditiva"
    },
    {
      "value": "intellectual",
      "label": "Intelectual"
    },
    {
      "value": "psychosocial",
      "label": "Psicosocial"
    },
    {
      "value": "multiple",
      "label": "Múltiple"
    }
  ]
}$json$::jsonb,
  $json${
  "mapsTo": "Patient.extension",
  "extractor": "extension",
  "extensionUrl": "https://imed.app/fhir/StructureDefinition/patient-disability-type",
  "valueType": "code"
}$json$::jsonb,
  now(), now()
FROM form_catalogs c
WHERE c.form_code = 'patient-registration' AND c.version = '1.0.0'
ON CONFLICT (catalog_id, field_key) DO UPDATE
  SET sort_order  = EXCLUDED.sort_order,
      widget      = EXCLUDED.widget,
      core        = EXCLUDED.core,
      custom      = EXCLUDED.custom,
      email       = EXCLUDED.email,
      active      = TRUE,
      editable    = EXCLUDED.editable,
      defaults    = EXCLUDED.defaults,
      messages    = EXCLUDED.messages,
      list_source = EXCLUDED.list_source,
      binding     = EXCLUDED.binding,
      updated_at  = now();

-- 45 · deceasedBoolean (toggle)
INSERT INTO form_catalog_fields (
  id, catalog_id, field_key, sort_order, widget, core, custom, email, active,
  editable, defaults, messages, list_source, binding, created_at, updated_at
)
SELECT
  gen_random_uuid(), c.id, 'deceasedBoolean', 45, 'toggle',
  FALSE, FALSE, FALSE, TRUE,
  $json$["label","description","helpText","onLabel","offLabel","required"]$json$::jsonb,
  $json${
  "label": "¿Paciente Fallecido?",
  "description": "Marcar si el paciente ha fallecido para actualizar su estado en el sistema",
  "defaultValue": false
}$json$::jsonb,
  NULL,
  NULL,
  $json${
  "mapsTo": "Patient.deceasedBoolean",
  "extractor": "primitive"
}$json$::jsonb,
  now(), now()
FROM form_catalogs c
WHERE c.form_code = 'patient-registration' AND c.version = '1.0.0'
ON CONFLICT (catalog_id, field_key) DO UPDATE
  SET sort_order  = EXCLUDED.sort_order,
      widget      = EXCLUDED.widget,
      core        = EXCLUDED.core,
      custom      = EXCLUDED.custom,
      email       = EXCLUDED.email,
      active      = TRUE,
      editable    = EXCLUDED.editable,
      defaults    = EXCLUDED.defaults,
      messages    = EXCLUDED.messages,
      list_source = EXCLUDED.list_source,
      binding     = EXCLUDED.binding,
      updated_at  = now();

-- 46 · deceasedDate (date)
INSERT INTO form_catalog_fields (
  id, catalog_id, field_key, sort_order, widget, core, custom, email, active,
  editable, defaults, messages, list_source, binding, created_at, updated_at
)
SELECT
  gen_random_uuid(), c.id, 'deceasedDate', 46, 'date',
  FALSE, FALSE, FALSE, TRUE,
  $json$[
  "label",
  "helpText",
  "required"
]$json$::jsonb,
  $json${
  "label": "Fecha de Fallecimiento"
}$json$::jsonb,
  NULL,
  NULL,
  $json${
  "mapsTo": "Patient.deceasedDateTime",
  "extractor": "primitive"
}$json$::jsonb,
  now(), now()
FROM form_catalogs c
WHERE c.form_code = 'patient-registration' AND c.version = '1.0.0'
ON CONFLICT (catalog_id, field_key) DO UPDATE
  SET sort_order  = EXCLUDED.sort_order,
      widget      = EXCLUDED.widget,
      core        = EXCLUDED.core,
      custom      = EXCLUDED.custom,
      email       = EXCLUDED.email,
      active      = TRUE,
      editable    = EXCLUDED.editable,
      defaults    = EXCLUDED.defaults,
      messages    = EXCLUDED.messages,
      list_source = EXCLUDED.list_source,
      binding     = EXCLUDED.binding,
      updated_at  = now();

-- 47 · emergencyContactName (text)
INSERT INTO form_catalog_fields (
  id, catalog_id, field_key, sort_order, widget, core, custom, email, active,
  editable, defaults, messages, list_source, binding, created_at, updated_at
)
SELECT
  gen_random_uuid(), c.id, 'emergencyContactName', 47, 'text',
  FALSE, FALSE, FALSE, TRUE,
  $json$[
  "label",
  "placeholder",
  "helpText",
  "required"
]$json$::jsonb,
  $json${
  "label": "Contacto de Emergencia",
  "placeholder": "Nombre del contacto"
}$json$::jsonb,
  NULL,
  NULL,
  $json${
  "mapsTo": "Patient.contact",
  "extractor": "patient-contact",
  "part": "name",
  "fixedType": "C"
}$json$::jsonb,
  now(), now()
FROM form_catalogs c
WHERE c.form_code = 'patient-registration' AND c.version = '1.0.0'
ON CONFLICT (catalog_id, field_key) DO UPDATE
  SET sort_order  = EXCLUDED.sort_order,
      widget      = EXCLUDED.widget,
      core        = EXCLUDED.core,
      custom      = EXCLUDED.custom,
      email       = EXCLUDED.email,
      active      = TRUE,
      editable    = EXCLUDED.editable,
      defaults    = EXCLUDED.defaults,
      messages    = EXCLUDED.messages,
      list_source = EXCLUDED.list_source,
      binding     = EXCLUDED.binding,
      updated_at  = now();

-- 48 · emergencyContactPhone (text)
INSERT INTO form_catalog_fields (
  id, catalog_id, field_key, sort_order, widget, core, custom, email, active,
  editable, defaults, messages, list_source, binding, created_at, updated_at
)
SELECT
  gen_random_uuid(), c.id, 'emergencyContactPhone', 48, 'text',
  FALSE, FALSE, FALSE, TRUE,
  $json$[
  "label",
  "placeholder",
  "helpText",
  "required"
]$json$::jsonb,
  $json${
  "label": "Teléfono de Emergencia",
  "placeholder": "+504 9999-9999",
  "inputMode": "tel"
}$json$::jsonb,
  NULL,
  NULL,
  $json${
  "mapsTo": "Patient.contact",
  "extractor": "patient-contact",
  "part": "phone",
  "fixedType": "C"
}$json$::jsonb,
  now(), now()
FROM form_catalogs c
WHERE c.form_code = 'patient-registration' AND c.version = '1.0.0'
ON CONFLICT (catalog_id, field_key) DO UPDATE
  SET sort_order  = EXCLUDED.sort_order,
      widget      = EXCLUDED.widget,
      core        = EXCLUDED.core,
      custom      = EXCLUDED.custom,
      email       = EXCLUDED.email,
      active      = TRUE,
      editable    = EXCLUDED.editable,
      defaults    = EXCLUDED.defaults,
      messages    = EXCLUDED.messages,
      list_source = EXCLUDED.list_source,
      binding     = EXCLUDED.binding,
      updated_at  = now();

-- 49 · coverageType (select)
INSERT INTO form_catalog_fields (
  id, catalog_id, field_key, sort_order, widget, core, custom, email, active,
  editable, defaults, messages, list_source, binding, created_at, updated_at
)
SELECT
  gen_random_uuid(), c.id, 'coverageType', 49, 'select',
  FALSE, FALSE, FALSE, TRUE,
  $json$[
  "label",
  "placeholder",
  "helpText",
  "required"
]$json$::jsonb,
  $json${
  "label": "Cobertura / Seguro del Paciente",
  "placeholder": "Seleccione..."
}$json$::jsonb,
  NULL,
  $json${
  "source": "static",
  "options": [
    {
      "value": "none",
      "label": "Sin Cobertura"
    },
    {
      "value": "ihss",
      "label": "IHSS"
    },
    {
      "value": "private",
      "label": "Seguro Privado"
    },
    {
      "value": "military",
      "label": "Seguro Militar"
    },
    {
      "value": "government",
      "label": "Programa Gubernamental"
    }
  ]
}$json$::jsonb,
  $json${
  "mapsTo": "Patient.extension",
  "extractor": "extension",
  "extensionUrl": "https://imed.app/fhir/StructureDefinition/patient-coverage-type",
  "valueType": "code"
}$json$::jsonb,
  now(), now()
FROM form_catalogs c
WHERE c.form_code = 'patient-registration' AND c.version = '1.0.0'
ON CONFLICT (catalog_id, field_key) DO UPDATE
  SET sort_order  = EXCLUDED.sort_order,
      widget      = EXCLUDED.widget,
      core        = EXCLUDED.core,
      custom      = EXCLUDED.custom,
      email       = EXCLUDED.email,
      active      = TRUE,
      editable    = EXCLUDED.editable,
      defaults    = EXCLUDED.defaults,
      messages    = EXCLUDED.messages,
      list_source = EXCLUDED.list_source,
      binding     = EXCLUDED.binding,
      updated_at  = now();

-- 50 · policyNumber (text)
INSERT INTO form_catalog_fields (
  id, catalog_id, field_key, sort_order, widget, core, custom, email, active,
  editable, defaults, messages, list_source, binding, created_at, updated_at
)
SELECT
  gen_random_uuid(), c.id, 'policyNumber', 50, 'text',
  FALSE, FALSE, FALSE, TRUE,
  $json$[
  "label",
  "placeholder",
  "helpText",
  "required"
]$json$::jsonb,
  $json${
  "label": "Número de Póliza / Afiliación",
  "placeholder": "Ej: POL-123456"
}$json$::jsonb,
  NULL,
  NULL,
  $json${
  "mapsTo": "Patient.extension",
  "extractor": "extension",
  "extensionUrl": "https://imed.app/fhir/StructureDefinition/patient-policy-number",
  "valueType": "string"
}$json$::jsonb,
  now(), now()
FROM form_catalogs c
WHERE c.form_code = 'patient-registration' AND c.version = '1.0.0'
ON CONFLICT (catalog_id, field_key) DO UPDATE
  SET sort_order  = EXCLUDED.sort_order,
      widget      = EXCLUDED.widget,
      core        = EXCLUDED.core,
      custom      = EXCLUDED.custom,
      email       = EXCLUDED.email,
      active      = TRUE,
      editable    = EXCLUDED.editable,
      defaults    = EXCLUDED.defaults,
      messages    = EXCLUDED.messages,
      list_source = EXCLUDED.list_source,
      binding     = EXCLUDED.binding,
      updated_at  = now();

-- 51 · insurerOrganization (select)
INSERT INTO form_catalog_fields (
  id, catalog_id, field_key, sort_order, widget, core, custom, email, active,
  editable, defaults, messages, list_source, binding, created_at, updated_at
)
SELECT
  gen_random_uuid(), c.id, 'insurerOrganization', 51, 'select',
  FALSE, FALSE, FALSE, TRUE,
  $json$[
  "label",
  "placeholder",
  "helpText",
  "required"
]$json$::jsonb,
  $json${
  "label": "Aseguradora / Pagador",
  "placeholder": "Seleccione o busque..."
}$json$::jsonb,
  NULL,
  $json${
  "source": "api",
  "base": "fhir",
  "path": "/Organization?type=ins&_count=100&_sort=name",
  "dataPath": "entry",
  "map": {
    "value": "resource.id",
    "label": "resource.name"
  },
  "cacheDuration": 60000
}$json$::jsonb,
  $json${
  "mapsTo": "Patient.extension",
  "extractor": "extension",
  "extensionUrl": "https://imed.app/fhir/StructureDefinition/patient-insurer-organization",
  "valueType": "string"
}$json$::jsonb,
  now(), now()
FROM form_catalogs c
WHERE c.form_code = 'patient-registration' AND c.version = '1.0.0'
ON CONFLICT (catalog_id, field_key) DO UPDATE
  SET sort_order  = EXCLUDED.sort_order,
      widget      = EXCLUDED.widget,
      core        = EXCLUDED.core,
      custom      = EXCLUDED.custom,
      email       = EXCLUDED.email,
      active      = TRUE,
      editable    = EXCLUDED.editable,
      defaults    = EXCLUDED.defaults,
      messages    = EXCLUDED.messages,
      list_source = EXCLUDED.list_source,
      binding     = EXCLUDED.binding,
      updated_at  = now();

-- 52 · paymentCondition (select)
INSERT INTO form_catalog_fields (
  id, catalog_id, field_key, sort_order, widget, core, custom, email, active,
  editable, defaults, messages, list_source, binding, created_at, updated_at
)
SELECT
  gen_random_uuid(), c.id, 'paymentCondition', 52, 'select',
  FALSE, FALSE, FALSE, TRUE,
  $json$[
  "label",
  "placeholder",
  "helpText",
  "required"
]$json$::jsonb,
  $json${
  "label": "Condición de Pago",
  "placeholder": "Seleccione..."
}$json$::jsonb,
  NULL,
  $json${
  "source": "static",
  "options": [
    {
      "value": "cash",
      "label": "Contado"
    },
    {
      "value": "credit",
      "label": "Crédito"
    },
    {
      "value": "insurance",
      "label": "Seguro"
    },
    {
      "value": "government",
      "label": "Gobierno"
    },
    {
      "value": "free",
      "label": "Gratuito"
    }
  ]
}$json$::jsonb,
  $json${
  "mapsTo": "Patient.extension",
  "extractor": "extension",
  "extensionUrl": "https://imed.app/fhir/StructureDefinition/patient-payment-condition",
  "valueType": "code"
}$json$::jsonb,
  now(), now()
FROM form_catalogs c
WHERE c.form_code = 'patient-registration' AND c.version = '1.0.0'
ON CONFLICT (catalog_id, field_key) DO UPDATE
  SET sort_order  = EXCLUDED.sort_order,
      widget      = EXCLUDED.widget,
      core        = EXCLUDED.core,
      custom      = EXCLUDED.custom,
      email       = EXCLUDED.email,
      active      = TRUE,
      editable    = EXCLUDED.editable,
      defaults    = EXCLUDED.defaults,
      messages    = EXCLUDED.messages,
      list_source = EXCLUDED.list_source,
      binding     = EXCLUDED.binding,
      updated_at  = now();

-- 53 · priceList (select)
INSERT INTO form_catalog_fields (
  id, catalog_id, field_key, sort_order, widget, core, custom, email, active,
  editable, defaults, messages, list_source, binding, created_at, updated_at
)
SELECT
  gen_random_uuid(), c.id, 'priceList', 53, 'select',
  FALSE, FALSE, FALSE, TRUE,
  $json$[
  "label",
  "placeholder",
  "helpText",
  "required"
]$json$::jsonb,
  $json${
  "label": "Lista de Precios",
  "placeholder": "Seleccione..."
}$json$::jsonb,
  NULL,
  $json${
  "source": "static",
  "options": [
    {
      "value": "standard",
      "label": "Estándar"
    },
    {
      "value": "social",
      "label": "Social / Reducida"
    },
    {
      "value": "insurance",
      "label": "Tarifario Aseguradora"
    },
    {
      "value": "vip",
      "label": "VIP / Premium"
    }
  ]
}$json$::jsonb,
  $json${
  "mapsTo": "Patient.extension",
  "extractor": "extension",
  "extensionUrl": "https://imed.app/fhir/StructureDefinition/patient-price-list",
  "valueType": "code"
}$json$::jsonb,
  now(), now()
FROM form_catalogs c
WHERE c.form_code = 'patient-registration' AND c.version = '1.0.0'
ON CONFLICT (catalog_id, field_key) DO UPDATE
  SET sort_order  = EXCLUDED.sort_order,
      widget      = EXCLUDED.widget,
      core        = EXCLUDED.core,
      custom      = EXCLUDED.custom,
      email       = EXCLUDED.email,
      active      = TRUE,
      editable    = EXCLUDED.editable,
      defaults    = EXCLUDED.defaults,
      messages    = EXCLUDED.messages,
      list_source = EXCLUDED.list_source,
      binding     = EXCLUDED.binding,
      updated_at  = now();

-- 54 · referralSpecialty (select)
INSERT INTO form_catalog_fields (
  id, catalog_id, field_key, sort_order, widget, core, custom, email, active,
  editable, defaults, messages, list_source, binding, created_at, updated_at
)
SELECT
  gen_random_uuid(), c.id, 'referralSpecialty', 54, 'select',
  FALSE, FALSE, FALSE, TRUE,
  $json$[
  "label",
  "placeholder",
  "helpText",
  "required"
]$json$::jsonb,
  $json${
  "label": "Servicio / Especialidad",
  "placeholder": "Seleccione..."
}$json$::jsonb,
  NULL,
  $json${
  "source": "static",
  "options": [
    {
      "value": "general",
      "label": "Medicina General"
    },
    {
      "value": "internal",
      "label": "Medicina Interna"
    },
    {
      "value": "pediatrics",
      "label": "Pediatría"
    },
    {
      "value": "gynecology",
      "label": "Ginecología"
    },
    {
      "value": "surgery",
      "label": "Cirugía General"
    },
    {
      "value": "orthopedics",
      "label": "Ortopedia"
    },
    {
      "value": "cardiology",
      "label": "Cardiología"
    },
    {
      "value": "neurology",
      "label": "Neurología"
    },
    {
      "value": "dermatology",
      "label": "Dermatología"
    },
    {
      "value": "ophthalmology",
      "label": "Oftalmología"
    },
    {
      "value": "ent",
      "label": "Otorrinolaringología"
    },
    {
      "value": "psychiatry",
      "label": "Psiquiatría"
    },
    {
      "value": "emergency",
      "label": "Emergencia"
    }
  ]
}$json$::jsonb,
  $json${
  "mapsTo": "Patient.extension",
  "extractor": "extension",
  "extensionUrl": "https://imed.app/fhir/StructureDefinition/patient-referral-specialty",
  "valueType": "code"
}$json$::jsonb,
  now(), now()
FROM form_catalogs c
WHERE c.form_code = 'patient-registration' AND c.version = '1.0.0'
ON CONFLICT (catalog_id, field_key) DO UPDATE
  SET sort_order  = EXCLUDED.sort_order,
      widget      = EXCLUDED.widget,
      core        = EXCLUDED.core,
      custom      = EXCLUDED.custom,
      email       = EXCLUDED.email,
      active      = TRUE,
      editable    = EXCLUDED.editable,
      defaults    = EXCLUDED.defaults,
      messages    = EXCLUDED.messages,
      list_source = EXCLUDED.list_source,
      binding     = EXCLUDED.binding,
      updated_at  = now();

-- 55 · referralFacility (select)
INSERT INTO form_catalog_fields (
  id, catalog_id, field_key, sort_order, widget, core, custom, email, active,
  editable, defaults, messages, list_source, binding, created_at, updated_at
)
SELECT
  gen_random_uuid(), c.id, 'referralFacility', 55, 'select',
  FALSE, FALSE, FALSE, TRUE,
  $json$[
  "label",
  "placeholder",
  "helpText",
  "required"
]$json$::jsonb,
  $json${
  "label": "Establecimiento de Referencia",
  "placeholder": "Seleccione..."
}$json$::jsonb,
  NULL,
  $json${
  "source": "api",
  "base": "fhir",
  "path": "/Location?_count=100&_sort=name",
  "dataPath": "entry",
  "map": {
    "value": "resource.id",
    "label": "resource.name"
  }
}$json$::jsonb,
  $json${
  "mapsTo": "Patient.extension",
  "extractor": "extension",
  "extensionUrl": "https://imed.app/fhir/StructureDefinition/patient-referral-facility",
  "valueType": "string"
}$json$::jsonb,
  now(), now()
FROM form_catalogs c
WHERE c.form_code = 'patient-registration' AND c.version = '1.0.0'
ON CONFLICT (catalog_id, field_key) DO UPDATE
  SET sort_order  = EXCLUDED.sort_order,
      widget      = EXCLUDED.widget,
      core        = EXCLUDED.core,
      custom      = EXCLUDED.custom,
      email       = EXCLUDED.email,
      active      = TRUE,
      editable    = EXCLUDED.editable,
      defaults    = EXCLUDED.defaults,
      messages    = EXCLUDED.messages,
      list_source = EXCLUDED.list_source,
      binding     = EXCLUDED.binding,
      updated_at  = now();

-- 56 · referralFacilityOther (text)
INSERT INTO form_catalog_fields (
  id, catalog_id, field_key, sort_order, widget, core, custom, email, active,
  editable, defaults, messages, list_source, binding, created_at, updated_at
)
SELECT
  gen_random_uuid(), c.id, 'referralFacilityOther', 56, 'text',
  FALSE, FALSE, FALSE, TRUE,
  $json$[
  "label",
  "placeholder",
  "helpText",
  "required"
]$json$::jsonb,
  $json${
  "label": "Establecimiento No Catalogado",
  "placeholder": "Nombre del establecimiento si no aparece en la lista"
}$json$::jsonb,
  NULL,
  NULL,
  $json${
  "mapsTo": "Patient.extension",
  "extractor": "extension",
  "extensionUrl": "https://imed.app/fhir/StructureDefinition/patient-referral-facility-other",
  "valueType": "string"
}$json$::jsonb,
  now(), now()
FROM form_catalogs c
WHERE c.form_code = 'patient-registration' AND c.version = '1.0.0'
ON CONFLICT (catalog_id, field_key) DO UPDATE
  SET sort_order  = EXCLUDED.sort_order,
      widget      = EXCLUDED.widget,
      core        = EXCLUDED.core,
      custom      = EXCLUDED.custom,
      email       = EXCLUDED.email,
      active      = TRUE,
      editable    = EXCLUDED.editable,
      defaults    = EXCLUDED.defaults,
      messages    = EXCLUDED.messages,
      list_source = EXCLUDED.list_source,
      binding     = EXCLUDED.binding,
      updated_at  = now();

-- 57 · referringPractitioner (text)
INSERT INTO form_catalog_fields (
  id, catalog_id, field_key, sort_order, widget, core, custom, email, active,
  editable, defaults, messages, list_source, binding, created_at, updated_at
)
SELECT
  gen_random_uuid(), c.id, 'referringPractitioner', 57, 'text',
  FALSE, FALSE, FALSE, TRUE,
  $json$[
  "label",
  "placeholder",
  "helpText",
  "required"
]$json$::jsonb,
  $json${
  "label": "Médico que Refiere",
  "placeholder": "Nombre del médico"
}$json$::jsonb,
  NULL,
  NULL,
  $json${
  "mapsTo": "Patient.extension",
  "extractor": "extension",
  "extensionUrl": "https://imed.app/fhir/StructureDefinition/patient-referring-practitioner",
  "valueType": "string"
}$json$::jsonb,
  now(), now()
FROM form_catalogs c
WHERE c.form_code = 'patient-registration' AND c.version = '1.0.0'
ON CONFLICT (catalog_id, field_key) DO UPDATE
  SET sort_order  = EXCLUDED.sort_order,
      widget      = EXCLUDED.widget,
      core        = EXCLUDED.core,
      custom      = EXCLUDED.custom,
      email       = EXCLUDED.email,
      active      = TRUE,
      editable    = EXCLUDED.editable,
      defaults    = EXCLUDED.defaults,
      messages    = EXCLUDED.messages,
      list_source = EXCLUDED.list_source,
      binding     = EXCLUDED.binding,
      updated_at  = now();

-- 58 · admissionUser (text)
INSERT INTO form_catalog_fields (
  id, catalog_id, field_key, sort_order, widget, core, custom, email, active,
  editable, defaults, messages, list_source, binding, created_at, updated_at
)
SELECT
  gen_random_uuid(), c.id, 'admissionUser', 58, 'text',
  FALSE, FALSE, FALSE, TRUE,
  $json$[
  "label",
  "helpText"
]$json$::jsonb,
  $json${
  "label": "Usuario de Admisión",
  "helpText": "Campo llenado automáticamente por el sistema",
  "disabled": true
}$json$::jsonb,
  NULL,
  NULL,
  $json${
  "mapsTo": "Patient.extension",
  "extractor": "extension",
  "extensionUrl": "https://imed.app/fhir/StructureDefinition/patient-admission-user",
  "valueType": "string"
}$json$::jsonb,
  now(), now()
FROM form_catalogs c
WHERE c.form_code = 'patient-registration' AND c.version = '1.0.0'
ON CONFLICT (catalog_id, field_key) DO UPDATE
  SET sort_order  = EXCLUDED.sort_order,
      widget      = EXCLUDED.widget,
      core        = EXCLUDED.core,
      custom      = EXCLUDED.custom,
      email       = EXCLUDED.email,
      active      = TRUE,
      editable    = EXCLUDED.editable,
      defaults    = EXCLUDED.defaults,
      messages    = EXCLUDED.messages,
      list_source = EXCLUDED.list_source,
      binding     = EXCLUDED.binding,
      updated_at  = now();

-- 59 · responsibleCoverage (text)
INSERT INTO form_catalog_fields (
  id, catalog_id, field_key, sort_order, widget, core, custom, email, active,
  editable, defaults, messages, list_source, binding, created_at, updated_at
)
SELECT
  gen_random_uuid(), c.id, 'responsibleCoverage', 59, 'text',
  FALSE, FALSE, FALSE, TRUE,
  $json$[
  "label",
  "placeholder",
  "helpText",
  "required"
]$json$::jsonb,
  $json${
  "label": "Cobertura de Seguro",
  "placeholder": "Tipo de cobertura"
}$json$::jsonb,
  NULL,
  NULL,
  $json${
  "mapsTo": "RelatedPerson.extension",
  "extractor": "extension",
  "extensionUrl": "https://imed.app/fhir/StructureDefinition/related-person-coverage",
  "valueType": "string"
}$json$::jsonb,
  now(), now()
FROM form_catalogs c
WHERE c.form_code = 'patient-registration' AND c.version = '1.0.0'
ON CONFLICT (catalog_id, field_key) DO UPDATE
  SET sort_order  = EXCLUDED.sort_order,
      widget      = EXCLUDED.widget,
      core        = EXCLUDED.core,
      custom      = EXCLUDED.custom,
      email       = EXCLUDED.email,
      active      = TRUE,
      editable    = EXCLUDED.editable,
      defaults    = EXCLUDED.defaults,
      messages    = EXCLUDED.messages,
      list_source = EXCLUDED.list_source,
      binding     = EXCLUDED.binding,
      updated_at  = now();

-- 60 · responsibleIsPrimary (toggle)
INSERT INTO form_catalog_fields (
  id, catalog_id, field_key, sort_order, widget, core, custom, email, active,
  editable, defaults, messages, list_source, binding, created_at, updated_at
)
SELECT
  gen_random_uuid(), c.id, 'responsibleIsPrimary', 60, 'toggle',
  FALSE, FALSE, FALSE, TRUE,
  $json$["label","description","helpText","onLabel","offLabel","required"]$json$::jsonb,
  $json${
  "label": "Contacto Principal",
  "description": "Indica si este contacto es el responsable principal del paciente",
  "defaultValue": true
}$json$::jsonb,
  NULL,
  NULL,
  $json${
  "mapsTo": "RelatedPerson.extension",
  "extractor": "extension",
  "extensionUrl": "https://imed.app/fhir/StructureDefinition/related-person-is-primary",
  "valueType": "boolean"
}$json$::jsonb,
  now(), now()
FROM form_catalogs c
WHERE c.form_code = 'patient-registration' AND c.version = '1.0.0'
ON CONFLICT (catalog_id, field_key) DO UPDATE
  SET sort_order  = EXCLUDED.sort_order,
      widget      = EXCLUDED.widget,
      core        = EXCLUDED.core,
      custom      = EXCLUDED.custom,
      email       = EXCLUDED.email,
      active      = TRUE,
      editable    = EXCLUDED.editable,
      defaults    = EXCLUDED.defaults,
      messages    = EXCLUDED.messages,
      list_source = EXCLUDED.list_source,
      binding     = EXCLUDED.binding,
      updated_at  = now();

-- 61 · responsibleOccupation (text)
INSERT INTO form_catalog_fields (
  id, catalog_id, field_key, sort_order, widget, core, custom, email, active,
  editable, defaults, messages, list_source, binding, created_at, updated_at
)
SELECT
  gen_random_uuid(), c.id, 'responsibleOccupation', 61, 'text',
  FALSE, FALSE, FALSE, TRUE,
  $json$[
  "label",
  "placeholder",
  "helpText",
  "required"
]$json$::jsonb,
  $json${
  "label": "Profesión u Ocupación",
  "placeholder": "Ej: Ingeniero"
}$json$::jsonb,
  NULL,
  NULL,
  $json${
  "mapsTo": "RelatedPerson.extension",
  "extractor": "extension",
  "extensionUrl": "https://imed.app/fhir/StructureDefinition/related-person-occupation",
  "valueType": "string"
}$json$::jsonb,
  now(), now()
FROM form_catalogs c
WHERE c.form_code = 'patient-registration' AND c.version = '1.0.0'
ON CONFLICT (catalog_id, field_key) DO UPDATE
  SET sort_order  = EXCLUDED.sort_order,
      widget      = EXCLUDED.widget,
      core        = EXCLUDED.core,
      custom      = EXCLUDED.custom,
      email       = EXCLUDED.email,
      active      = TRUE,
      editable    = EXCLUDED.editable,
      defaults    = EXCLUDED.defaults,
      messages    = EXCLUDED.messages,
      list_source = EXCLUDED.list_source,
      binding     = EXCLUDED.binding,
      updated_at  = now();

-- 62 · responsibleNationality (select)
INSERT INTO form_catalog_fields (
  id, catalog_id, field_key, sort_order, widget, core, custom, email, active,
  editable, defaults, messages, list_source, binding, created_at, updated_at
)
SELECT
  gen_random_uuid(), c.id, 'responsibleNationality', 62, 'select',
  FALSE, FALSE, FALSE, TRUE,
  $json$[
  "label",
  "placeholder",
  "helpText",
  "required"
]$json$::jsonb,
  $json${
  "label": "Nacionalidad",
  "placeholder": "Seleccione..."
}$json$::jsonb,
  NULL,
  $json${
  "source": "static",
  "options": [
    {
      "value": "HN",
      "label": "Hondureña"
    },
    {
      "value": "GT",
      "label": "Guatemalteca"
    },
    {
      "value": "SV",
      "label": "Salvadoreña"
    },
    {
      "value": "NI",
      "label": "Nicaragüense"
    },
    {
      "value": "OTHER",
      "label": "Otra"
    }
  ]
}$json$::jsonb,
  $json${
  "mapsTo": "RelatedPerson.extension",
  "extractor": "extension",
  "extensionUrl": "https://imed.app/fhir/StructureDefinition/related-person-nationality",
  "valueType": "code"
}$json$::jsonb,
  now(), now()
FROM form_catalogs c
WHERE c.form_code = 'patient-registration' AND c.version = '1.0.0'
ON CONFLICT (catalog_id, field_key) DO UPDATE
  SET sort_order  = EXCLUDED.sort_order,
      widget      = EXCLUDED.widget,
      core        = EXCLUDED.core,
      custom      = EXCLUDED.custom,
      email       = EXCLUDED.email,
      active      = TRUE,
      editable    = EXCLUDED.editable,
      defaults    = EXCLUDED.defaults,
      messages    = EXCLUDED.messages,
      list_source = EXCLUDED.list_source,
      binding     = EXCLUDED.binding,
      updated_at  = now();

-- 63 · responsibleMaritalStatus (select)
INSERT INTO form_catalog_fields (
  id, catalog_id, field_key, sort_order, widget, core, custom, email, active,
  editable, defaults, messages, list_source, binding, created_at, updated_at
)
SELECT
  gen_random_uuid(), c.id, 'responsibleMaritalStatus', 63, 'select',
  FALSE, FALSE, FALSE, TRUE,
  $json$[
  "label",
  "placeholder",
  "helpText",
  "required"
]$json$::jsonb,
  $json${
  "label": "Estado Civil",
  "placeholder": "Seleccione..."
}$json$::jsonb,
  NULL,
  $json${
  "source": "valueset",
  "url": "http://hl7.org/fhir/ValueSet/marital-status"
}$json$::jsonb,
  $json${
  "mapsTo": "RelatedPerson.extension",
  "extractor": "extension",
  "extensionUrl": "https://imed.app/fhir/StructureDefinition/related-person-marital-status",
  "valueType": "coding",
  "codingSystem": "http://terminology.hl7.org/CodeSystem/v3-MaritalStatus"
}$json$::jsonb,
  now(), now()
FROM form_catalogs c
WHERE c.form_code = 'patient-registration' AND c.version = '1.0.0'
ON CONFLICT (catalog_id, field_key) DO UPDATE
  SET sort_order  = EXCLUDED.sort_order,
      widget      = EXCLUDED.widget,
      core        = EXCLUDED.core,
      custom      = EXCLUDED.custom,
      email       = EXCLUDED.email,
      active      = TRUE,
      editable    = EXCLUDED.editable,
      defaults    = EXCLUDED.defaults,
      messages    = EXCLUDED.messages,
      list_source = EXCLUDED.list_source,
      binding     = EXCLUDED.binding,
      updated_at  = now();

-- 64 · responsibleInsuranceType (select)
INSERT INTO form_catalog_fields (
  id, catalog_id, field_key, sort_order, widget, core, custom, email, active,
  editable, defaults, messages, list_source, binding, created_at, updated_at
)
SELECT
  gen_random_uuid(), c.id, 'responsibleInsuranceType', 64, 'select',
  FALSE, FALSE, FALSE, TRUE,
  $json$[
  "label",
  "placeholder",
  "helpText",
  "required"
]$json$::jsonb,
  $json${
  "label": "Tipo de Seguro / Cobertura",
  "placeholder": "Seleccione..."
}$json$::jsonb,
  NULL,
  $json${
  "source": "static",
  "options": [
    {
      "value": "none",
      "label": "Sin Cobertura"
    },
    {
      "value": "ihss",
      "label": "IHSS"
    },
    {
      "value": "private",
      "label": "Seguro Privado"
    },
    {
      "value": "military",
      "label": "Seguro Militar"
    }
  ]
}$json$::jsonb,
  $json${
  "mapsTo": "RelatedPerson.extension",
  "extractor": "extension",
  "extensionUrl": "https://imed.app/fhir/StructureDefinition/related-person-insurance-type",
  "valueType": "code"
}$json$::jsonb,
  now(), now()
FROM form_catalogs c
WHERE c.form_code = 'patient-registration' AND c.version = '1.0.0'
ON CONFLICT (catalog_id, field_key) DO UPDATE
  SET sort_order  = EXCLUDED.sort_order,
      widget      = EXCLUDED.widget,
      core        = EXCLUDED.core,
      custom      = EXCLUDED.custom,
      email       = EXCLUDED.email,
      active      = TRUE,
      editable    = EXCLUDED.editable,
      defaults    = EXCLUDED.defaults,
      messages    = EXCLUDED.messages,
      list_source = EXCLUDED.list_source,
      binding     = EXCLUDED.binding,
      updated_at  = now();

-- 65 · employerName (text)
INSERT INTO form_catalog_fields (
  id, catalog_id, field_key, sort_order, widget, core, custom, email, active,
  editable, defaults, messages, list_source, binding, created_at, updated_at
)
SELECT
  gen_random_uuid(), c.id, 'employerName', 65, 'text',
  FALSE, FALSE, FALSE, TRUE,
  $json$[
  "label",
  "placeholder",
  "helpText",
  "required"
]$json$::jsonb,
  $json${
  "label": "Nombre de Empresa",
  "placeholder": "Ej: Hospital Escuela Universitario"
}$json$::jsonb,
  NULL,
  NULL,
  $json${
  "mapsTo": "Patient.extension",
  "extractor": "extension",
  "extensionUrl": "https://imed.app/fhir/StructureDefinition/patient-employer-name",
  "valueType": "string"
}$json$::jsonb,
  now(), now()
FROM form_catalogs c
WHERE c.form_code = 'patient-registration' AND c.version = '1.0.0'
ON CONFLICT (catalog_id, field_key) DO UPDATE
  SET sort_order  = EXCLUDED.sort_order,
      widget      = EXCLUDED.widget,
      core        = EXCLUDED.core,
      custom      = EXCLUDED.custom,
      email       = EXCLUDED.email,
      active      = TRUE,
      editable    = EXCLUDED.editable,
      defaults    = EXCLUDED.defaults,
      messages    = EXCLUDED.messages,
      list_source = EXCLUDED.list_source,
      binding     = EXCLUDED.binding,
      updated_at  = now();

-- 66 · jobTitle (text)
INSERT INTO form_catalog_fields (
  id, catalog_id, field_key, sort_order, widget, core, custom, email, active,
  editable, defaults, messages, list_source, binding, created_at, updated_at
)
SELECT
  gen_random_uuid(), c.id, 'jobTitle', 66, 'text',
  FALSE, FALSE, FALSE, TRUE,
  $json$[
  "label",
  "placeholder",
  "helpText",
  "required"
]$json$::jsonb,
  $json${
  "label": "Cargo / Puesto",
  "placeholder": "Ej: Contador"
}$json$::jsonb,
  NULL,
  NULL,
  $json${
  "mapsTo": "Patient.extension",
  "extractor": "extension",
  "extensionUrl": "https://imed.app/fhir/StructureDefinition/patient-job-title",
  "valueType": "string"
}$json$::jsonb,
  now(), now()
FROM form_catalogs c
WHERE c.form_code = 'patient-registration' AND c.version = '1.0.0'
ON CONFLICT (catalog_id, field_key) DO UPDATE
  SET sort_order  = EXCLUDED.sort_order,
      widget      = EXCLUDED.widget,
      core        = EXCLUDED.core,
      custom      = EXCLUDED.custom,
      email       = EXCLUDED.email,
      active      = TRUE,
      editable    = EXCLUDED.editable,
      defaults    = EXCLUDED.defaults,
      messages    = EXCLUDED.messages,
      list_source = EXCLUDED.list_source,
      binding     = EXCLUDED.binding,
      updated_at  = now();

-- 67 · employerAddress (text)
INSERT INTO form_catalog_fields (
  id, catalog_id, field_key, sort_order, widget, core, custom, email, active,
  editable, defaults, messages, list_source, binding, created_at, updated_at
)
SELECT
  gen_random_uuid(), c.id, 'employerAddress', 67, 'text',
  FALSE, FALSE, FALSE, TRUE,
  $json$[
  "label",
  "placeholder",
  "helpText",
  "required"
]$json$::jsonb,
  $json${
  "label": "Dirección de la Empresa",
  "placeholder": "Dirección completa"
}$json$::jsonb,
  NULL,
  NULL,
  $json${
  "mapsTo": "Patient.extension",
  "extractor": "extension",
  "extensionUrl": "https://imed.app/fhir/StructureDefinition/patient-employer-address",
  "valueType": "string"
}$json$::jsonb,
  now(), now()
FROM form_catalogs c
WHERE c.form_code = 'patient-registration' AND c.version = '1.0.0'
ON CONFLICT (catalog_id, field_key) DO UPDATE
  SET sort_order  = EXCLUDED.sort_order,
      widget      = EXCLUDED.widget,
      core        = EXCLUDED.core,
      custom      = EXCLUDED.custom,
      email       = EXCLUDED.email,
      active      = TRUE,
      editable    = EXCLUDED.editable,
      defaults    = EXCLUDED.defaults,
      messages    = EXCLUDED.messages,
      list_source = EXCLUDED.list_source,
      binding     = EXCLUDED.binding,
      updated_at  = now();

-- 68 · employerPhone1 (text)
INSERT INTO form_catalog_fields (
  id, catalog_id, field_key, sort_order, widget, core, custom, email, active,
  editable, defaults, messages, list_source, binding, created_at, updated_at
)
SELECT
  gen_random_uuid(), c.id, 'employerPhone1', 68, 'text',
  FALSE, FALSE, FALSE, TRUE,
  $json$[
  "label",
  "placeholder",
  "helpText",
  "required"
]$json$::jsonb,
  $json${
  "label": "Teléfono Empresa 1",
  "placeholder": "+504 2222-2222",
  "inputMode": "tel"
}$json$::jsonb,
  NULL,
  NULL,
  $json${
  "mapsTo": "Patient.extension",
  "extractor": "extension",
  "extensionUrl": "https://imed.app/fhir/StructureDefinition/patient-employer-phone-1",
  "valueType": "string"
}$json$::jsonb,
  now(), now()
FROM form_catalogs c
WHERE c.form_code = 'patient-registration' AND c.version = '1.0.0'
ON CONFLICT (catalog_id, field_key) DO UPDATE
  SET sort_order  = EXCLUDED.sort_order,
      widget      = EXCLUDED.widget,
      core        = EXCLUDED.core,
      custom      = EXCLUDED.custom,
      email       = EXCLUDED.email,
      active      = TRUE,
      editable    = EXCLUDED.editable,
      defaults    = EXCLUDED.defaults,
      messages    = EXCLUDED.messages,
      list_source = EXCLUDED.list_source,
      binding     = EXCLUDED.binding,
      updated_at  = now();

-- 69 · employerPhone2 (text)
INSERT INTO form_catalog_fields (
  id, catalog_id, field_key, sort_order, widget, core, custom, email, active,
  editable, defaults, messages, list_source, binding, created_at, updated_at
)
SELECT
  gen_random_uuid(), c.id, 'employerPhone2', 69, 'text',
  FALSE, FALSE, FALSE, TRUE,
  $json$[
  "label",
  "placeholder",
  "helpText",
  "required"
]$json$::jsonb,
  $json${
  "label": "Teléfono Empresa 2",
  "placeholder": "+504 2222-2222",
  "inputMode": "tel"
}$json$::jsonb,
  NULL,
  NULL,
  $json${
  "mapsTo": "Patient.extension",
  "extractor": "extension",
  "extensionUrl": "https://imed.app/fhir/StructureDefinition/patient-employer-phone-2",
  "valueType": "string"
}$json$::jsonb,
  now(), now()
FROM form_catalogs c
WHERE c.form_code = 'patient-registration' AND c.version = '1.0.0'
ON CONFLICT (catalog_id, field_key) DO UPDATE
  SET sort_order  = EXCLUDED.sort_order,
      widget      = EXCLUDED.widget,
      core        = EXCLUDED.core,
      custom      = EXCLUDED.custom,
      email       = EXCLUDED.email,
      active      = TRUE,
      editable    = EXCLUDED.editable,
      defaults    = EXCLUDED.defaults,
      messages    = EXCLUDED.messages,
      list_source = EXCLUDED.list_source,
      binding     = EXCLUDED.binding,
      updated_at  = now();

-- Campos que ya no están en el catálogo: se desactivan.
UPDATE form_catalog_fields f
SET active = FALSE, updated_at = now()
FROM form_catalogs c
WHERE f.catalog_id = c.id
  AND c.form_code = 'patient-registration' AND c.version = '1.0.0'
  AND f.active
  AND f.field_key NOT IN ('identifierType', 'identifierValue', 'prefix', 'firstName', 'middleName', 'lastName', 'secondLastName', 'gender', 'birthDate', 'communication', 'phoneNumber', 'email', 'active', 'addressState', 'addressCity', 'addressLine', 'addressPostalCode', 'maritalStatus', 'responsibleFullName', 'responsibleDetailRelationship', 'responsibleDni', 'responsiblePhoneMobile', 'responsiblePhoneHome', 'responsibleBirthDate', 'responsibleAddress', 'custom_text', 'custom_textarea', 'custom_date', 'custom_toggle', 'medicalRecordNumber', 'registrationDate', 'nationality', 'origin', 'ethnicity', 'religion', 'patientCategory', 'taxId', 'employmentStatus', 'generalNotes', 'addressDistrict', 'educationLevel', 'bloodType', 'hasDisability', 'disabilityType', 'deceasedBoolean', 'deceasedDate', 'emergencyContactName', 'emergencyContactPhone', 'coverageType', 'policyNumber', 'insurerOrganization', 'paymentCondition', 'priceList', 'referralSpecialty', 'referralFacility', 'referralFacilityOther', 'referringPractitioner', 'admissionUser', 'responsibleCoverage', 'responsibleIsPrimary', 'responsibleOccupation', 'responsibleNationality', 'responsibleMaritalStatus', 'responsibleInsuranceType', 'employerName', 'jobTitle', 'employerAddress', 'employerPhone1', 'employerPhone2');
