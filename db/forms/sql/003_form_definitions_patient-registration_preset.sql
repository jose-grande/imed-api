-- form_definitions · formulario base de patient-registration 1.0.0
--
-- Formulario base de la plataforma (organization_id NULL), publicado. Las organizaciones crean su
-- borrador copiándolo desde el studio; hasta que publican uno propio siguen con el formulario
-- estándar del cliente, así que cargarlo no cambia lo que ve nadie.
--
-- Está escrito con la estructura del formulario actual de pacientes (kind studio-v2): los campos de
-- identificación sueltos en la tarjeta y las pestañas con sus contenedores, cada uno con sus
-- columnas. Lleva todos los campos del catálogo (script 002) que muestra el formulario actual.
-- Quedan fuera tres campos del formulario actual que repiten datos del responsable (nombre,
-- parentesco y teléfono de la pestaña de contactos, que el registro de responsable ya recoge) y el
-- interruptor "¿Es menor de edad?", que no es un dato sino una ayuda de pantalla.
--
-- El mapeo (fhir_mapping_json) es el que genera la API a partir del catálogo y el documento.
--
-- Se puede volver a ejecutar: si el formulario base 1.0.0 ya existe, actualiza su documento y su
-- mapeo (no se toca nada de las organizaciones); si no existe, lo crea.
-- Requiere los scripts 001 y 002 (la API valida los formularios contra el catálogo).

WITH base AS (
  SELECT
    $json${
  "kind": "studio-v2",
  "title": "Registro de Paciente",
  "children": [
    {
      "type": "field",
      "field": "identifierValue",
      "span": 4
    },
    {
      "type": "field",
      "field": "medicalRecordNumber",
      "span": 3
    },
    {
      "type": "field",
      "field": "identifierType",
      "span": 3
    },
    {
      "type": "field",
      "field": "firstName",
      "span": 3
    },
    {
      "type": "field",
      "field": "middleName",
      "span": 3
    },
    {
      "type": "field",
      "field": "lastName",
      "span": 3
    },
    {
      "type": "field",
      "field": "secondLastName",
      "span": 3
    },
    {
      "type": "tabs",
      "id": "patient_tabs",
      "tabs": [
        {
          "id": "tab_general",
          "label": "Datos Generales",
          "children": [
            {
              "type": "container",
              "id": "gen_demographics",
              "layout": "grid",
              "columns": 3,
              "gap": 6,
              "children": [
                {
                  "type": "field",
                  "field": "registrationDate"
                },
                {
                  "type": "field",
                  "field": "gender"
                },
                {
                  "type": "field",
                  "field": "birthDate"
                }
              ]
            },
            {
              "type": "container",
              "id": "gen_origin",
              "layout": "grid",
              "columns": 3,
              "gap": 6,
              "children": [
                {
                  "type": "field",
                  "field": "nationality"
                },
                {
                  "type": "field",
                  "field": "origin"
                },
                {
                  "type": "field",
                  "field": "ethnicity"
                }
              ]
            },
            {
              "type": "container",
              "id": "gen_cultural",
              "layout": "grid",
              "columns": 3,
              "gap": 6,
              "children": [
                {
                  "type": "field",
                  "field": "communication"
                },
                {
                  "type": "field",
                  "field": "religion"
                },
                {
                  "type": "field",
                  "field": "patientCategory"
                }
              ]
            },
            {
              "type": "container",
              "id": "gen_contact",
              "layout": "grid",
              "columns": 3,
              "gap": 6,
              "children": [
                {
                  "type": "field",
                  "field": "phoneNumber"
                },
                {
                  "type": "field",
                  "field": "email"
                },
                {
                  "type": "field",
                  "field": "taxId"
                }
              ]
            },
            {
              "type": "container",
              "id": "gen_status",
              "layout": "grid",
              "columns": 3,
              "gap": 6,
              "children": [
                {
                  "type": "field",
                  "field": "employmentStatus"
                },
                {
                  "type": "field",
                  "field": "active"
                }
              ]
            },
            {
              "type": "container",
              "id": "gen_notes",
              "layout": "vertical",
              "columns": 1,
              "gap": 4,
              "children": [
                {
                  "type": "field",
                  "field": "generalNotes"
                }
              ]
            }
          ]
        },
        {
          "id": "tab_address",
          "label": "Ubicación / Domicilio",
          "children": [
            {
              "type": "container",
              "id": "addr_location",
              "layout": "grid",
              "columns": 3,
              "gap": 6,
              "children": [
                {
                  "type": "field",
                  "field": "addressState"
                },
                {
                  "type": "field",
                  "field": "addressCity"
                },
                {
                  "type": "field",
                  "field": "addressDistrict"
                }
              ]
            },
            {
              "type": "container",
              "id": "addr_detail",
              "layout": "vertical",
              "columns": 1,
              "gap": 4,
              "children": [
                {
                  "type": "field",
                  "field": "addressLine"
                }
              ]
            }
          ]
        },
        {
          "id": "tab_complementary",
          "label": "Datos Complementarios",
          "children": [
            {
              "type": "container",
              "id": "comp_personal",
              "layout": "grid",
              "columns": 3,
              "gap": 6,
              "children": [
                {
                  "type": "field",
                  "field": "maritalStatus"
                },
                {
                  "type": "field",
                  "field": "educationLevel"
                },
                {
                  "type": "field",
                  "field": "bloodType"
                }
              ]
            },
            {
              "type": "container",
              "id": "comp_conditions",
              "layout": "grid",
              "columns": 4,
              "gap": 6,
              "children": [
                {
                  "type": "field",
                  "field": "hasDisability"
                },
                {
                  "type": "field",
                  "field": "disabilityType"
                },
                {
                  "type": "field",
                  "field": "deceasedBoolean"
                },
                {
                  "type": "field",
                  "field": "deceasedDate"
                }
              ]
            },
            {
              "type": "container",
              "id": "comp_emergency",
              "layout": "grid",
              "columns": 2,
              "gap": 6,
              "children": [
                {
                  "type": "field",
                  "field": "emergencyContactName"
                },
                {
                  "type": "field",
                  "field": "emergencyContactPhone"
                }
              ]
            }
          ]
        },
        {
          "id": "tab_coverage",
          "label": "Cobertura y Facturación",
          "children": [
            {
              "type": "container",
              "id": "cov_insurance",
              "layout": "grid",
              "columns": 2,
              "gap": 6,
              "children": [
                {
                  "type": "field",
                  "field": "coverageType"
                },
                {
                  "type": "field",
                  "field": "policyNumber"
                }
              ]
            },
            {
              "type": "container",
              "id": "cov_billing",
              "layout": "grid",
              "columns": 3,
              "gap": 6,
              "children": [
                {
                  "type": "field",
                  "field": "insurerOrganization"
                },
                {
                  "type": "field",
                  "field": "paymentCondition"
                },
                {
                  "type": "field",
                  "field": "priceList"
                }
              ]
            }
          ]
        },
        {
          "id": "tab_referral",
          "label": "Información de Referencia",
          "children": [
            {
              "type": "container",
              "id": "ref_service",
              "layout": "grid",
              "columns": 2,
              "gap": 6,
              "children": [
                {
                  "type": "field",
                  "field": "referralSpecialty"
                },
                {
                  "type": "field",
                  "field": "referralFacility"
                }
              ]
            },
            {
              "type": "container",
              "id": "ref_practitioners",
              "layout": "grid",
              "columns": 3,
              "gap": 6,
              "children": [
                {
                  "type": "field",
                  "field": "referralFacilityOther"
                },
                {
                  "type": "field",
                  "field": "referringPractitioner"
                },
                {
                  "type": "field",
                  "field": "admissionUser"
                }
              ]
            }
          ]
        },
        {
          "id": "tab_contacts",
          "label": "Responsables / Contactos",
          "children": [
            {
              "type": "container",
              "id": "cont_extra",
              "layout": "grid",
              "columns": 2,
              "gap": 6,
              "children": [
                {
                  "type": "field",
                  "field": "responsibleCoverage"
                },
                {
                  "type": "field",
                  "field": "responsibleIsPrimary"
                }
              ]
            }
          ]
        },
        {
          "id": "tab_responsible_detail",
          "label": "Registro de Responsable",
          "children": [
            {
              "type": "container",
              "id": "resp_identity",
              "layout": "grid",
              "columns": 3,
              "gap": 6,
              "children": [
                {
                  "type": "field",
                  "field": "responsibleDni"
                },
                {
                  "type": "field",
                  "field": "responsibleDetailRelationship"
                },
                {
                  "type": "field",
                  "field": "responsibleFullName"
                }
              ]
            },
            {
              "type": "container",
              "id": "resp_personal",
              "layout": "grid",
              "columns": 3,
              "gap": 6,
              "children": [
                {
                  "type": "field",
                  "field": "responsibleOccupation"
                },
                {
                  "type": "field",
                  "field": "responsibleNationality"
                },
                {
                  "type": "field",
                  "field": "responsibleBirthDate"
                }
              ]
            },
            {
              "type": "container",
              "id": "resp_civil",
              "layout": "grid",
              "columns": 2,
              "gap": 6,
              "children": [
                {
                  "type": "field",
                  "field": "responsibleMaritalStatus"
                },
                {
                  "type": "field",
                  "field": "responsibleAddress"
                }
              ]
            },
            {
              "type": "container",
              "id": "resp_phones",
              "layout": "grid",
              "columns": 3,
              "gap": 6,
              "children": [
                {
                  "type": "field",
                  "field": "responsiblePhoneHome"
                },
                {
                  "type": "field",
                  "field": "responsiblePhoneMobile"
                },
                {
                  "type": "field",
                  "field": "responsibleInsuranceType"
                }
              ]
            }
          ]
        },
        {
          "id": "tab_employment",
          "label": "Laboral",
          "children": [
            {
              "type": "container",
              "id": "emp_company",
              "layout": "grid",
              "columns": 2,
              "gap": 6,
              "children": [
                {
                  "type": "field",
                  "field": "employerName"
                },
                {
                  "type": "field",
                  "field": "jobTitle"
                }
              ]
            },
            {
              "type": "container",
              "id": "emp_address",
              "layout": "vertical",
              "columns": 1,
              "gap": 4,
              "children": [
                {
                  "type": "field",
                  "field": "employerAddress"
                }
              ]
            },
            {
              "type": "container",
              "id": "emp_phones",
              "layout": "grid",
              "columns": 2,
              "gap": 6,
              "children": [
                {
                  "type": "field",
                  "field": "employerPhone1"
                },
                {
                  "type": "field",
                  "field": "employerPhone2"
                }
              ]
            }
          ]
        }
      ]
    }
  ]
}$json$::jsonb AS schema_json,
    $json${
  "version": "1.0",
  "items": [
    {
      "linkId": "identifierValue",
      "mapsTo": "Patient.identifier",
      "extractor": "identifier",
      "part": "value"
    },
    {
      "linkId": "medicalRecordNumber",
      "mapsTo": "Patient.identifier",
      "extractor": "secondary-identifier",
      "system": "https://imed.app/identifiers/secondary/medical-record",
      "fixedType": "MR"
    },
    {
      "linkId": "identifierType",
      "mapsTo": "Patient.identifier",
      "extractor": "identifier",
      "part": "type"
    },
    {
      "linkId": "firstName",
      "mapsTo": "Patient.name",
      "extractor": "human-name",
      "part": "given"
    },
    {
      "linkId": "middleName",
      "mapsTo": "Patient.name",
      "extractor": "human-name",
      "part": "given"
    },
    {
      "linkId": "lastName",
      "mapsTo": "Patient.name",
      "extractor": "human-name",
      "part": "family"
    },
    {
      "linkId": "secondLastName",
      "mapsTo": "Patient.name",
      "extractor": "human-name",
      "part": "family"
    },
    {
      "linkId": "registrationDate",
      "mapsTo": "Patient.extension",
      "extractor": "extension",
      "extensionUrl": "https://imed.app/fhir/StructureDefinition/patient-registration-date",
      "valueType": "date"
    },
    {
      "linkId": "gender",
      "mapsTo": "Patient.gender",
      "extractor": "primitive"
    },
    {
      "linkId": "birthDate",
      "mapsTo": "Patient.birthDate",
      "extractor": "primitive"
    },
    {
      "linkId": "nationality",
      "mapsTo": "Patient.extension",
      "extractor": "extension",
      "extensionUrl": "https://imed.app/fhir/StructureDefinition/patient-nationality",
      "valueType": "code"
    },
    {
      "linkId": "origin",
      "mapsTo": "Patient.extension",
      "extractor": "extension",
      "extensionUrl": "https://imed.app/fhir/StructureDefinition/patient-origin",
      "valueType": "code"
    },
    {
      "linkId": "ethnicity",
      "mapsTo": "Patient.extension",
      "extractor": "extension",
      "extensionUrl": "https://imed.app/fhir/StructureDefinition/patient-ethnicity",
      "valueType": "code"
    },
    {
      "linkId": "communication",
      "mapsTo": "Patient.communication.language",
      "extractor": "codeable-concept",
      "codingSystem": "urn:ietf:bcp:47",
      "fixed": {
        "preferred": true
      }
    },
    {
      "linkId": "religion",
      "mapsTo": "Patient.extension",
      "extractor": "extension",
      "extensionUrl": "https://imed.app/fhir/StructureDefinition/patient-religion",
      "valueType": "code"
    },
    {
      "linkId": "patientCategory",
      "mapsTo": "Patient.extension",
      "extractor": "extension",
      "extensionUrl": "https://imed.app/fhir/StructureDefinition/patient-category",
      "valueType": "code"
    },
    {
      "linkId": "phoneNumber",
      "mapsTo": "Patient.telecom",
      "extractor": "contact-point",
      "system": "phone"
    },
    {
      "linkId": "email",
      "mapsTo": "Patient.telecom",
      "extractor": "contact-point",
      "system": "email"
    },
    {
      "linkId": "taxId",
      "mapsTo": "Patient.identifier",
      "extractor": "secondary-identifier",
      "system": "https://imed.app/identifiers/secondary/tax",
      "fixedType": "TAX"
    },
    {
      "linkId": "employmentStatus",
      "mapsTo": "Patient.extension",
      "extractor": "extension",
      "extensionUrl": "https://imed.app/fhir/StructureDefinition/patient-employment-status",
      "valueType": "code"
    },
    {
      "linkId": "active",
      "mapsTo": "Patient.active",
      "extractor": "primitive",
      "default": true
    },
    {
      "linkId": "generalNotes",
      "mapsTo": "Patient.extension",
      "extractor": "extension",
      "extensionUrl": "https://imed.app/fhir/StructureDefinition/patient-general-notes",
      "valueType": "string"
    },
    {
      "linkId": "addressState",
      "mapsTo": "Patient.address",
      "extractor": "address",
      "part": "state"
    },
    {
      "linkId": "addressCity",
      "mapsTo": "Patient.address",
      "extractor": "address",
      "part": "city"
    },
    {
      "linkId": "addressDistrict",
      "mapsTo": "Patient.address",
      "extractor": "address",
      "part": "district"
    },
    {
      "linkId": "addressLine",
      "mapsTo": "Patient.address",
      "extractor": "address",
      "part": "line",
      "fixed": {
        "use": "home"
      }
    },
    {
      "linkId": "maritalStatus",
      "mapsTo": "Patient.maritalStatus",
      "extractor": "codeable-concept",
      "codingSystem": "http://terminology.hl7.org/CodeSystem/v3-MaritalStatus"
    },
    {
      "linkId": "educationLevel",
      "mapsTo": "Patient.extension",
      "extractor": "extension",
      "extensionUrl": "https://imed.app/fhir/StructureDefinition/patient-education-level",
      "valueType": "code"
    },
    {
      "linkId": "bloodType",
      "mapsTo": "Patient.extension",
      "extractor": "extension",
      "extensionUrl": "https://imed.app/fhir/StructureDefinition/patient-blood-type",
      "valueType": "code"
    },
    {
      "linkId": "hasDisability",
      "mapsTo": "Patient.extension",
      "extractor": "extension",
      "extensionUrl": "https://imed.app/fhir/StructureDefinition/patient-has-disability",
      "valueType": "boolean"
    },
    {
      "linkId": "disabilityType",
      "mapsTo": "Patient.extension",
      "extractor": "extension",
      "extensionUrl": "https://imed.app/fhir/StructureDefinition/patient-disability-type",
      "valueType": "code"
    },
    {
      "linkId": "deceasedBoolean",
      "mapsTo": "Patient.deceasedBoolean",
      "extractor": "primitive"
    },
    {
      "linkId": "deceasedDate",
      "mapsTo": "Patient.deceasedDateTime",
      "extractor": "primitive"
    },
    {
      "linkId": "emergencyContactName",
      "mapsTo": "Patient.contact",
      "extractor": "patient-contact",
      "part": "name",
      "fixedType": "C"
    },
    {
      "linkId": "emergencyContactPhone",
      "mapsTo": "Patient.contact",
      "extractor": "patient-contact",
      "part": "phone",
      "fixedType": "C"
    },
    {
      "linkId": "coverageType",
      "mapsTo": "Patient.extension",
      "extractor": "extension",
      "extensionUrl": "https://imed.app/fhir/StructureDefinition/patient-coverage-type",
      "valueType": "code"
    },
    {
      "linkId": "policyNumber",
      "mapsTo": "Patient.extension",
      "extractor": "extension",
      "extensionUrl": "https://imed.app/fhir/StructureDefinition/patient-policy-number",
      "valueType": "string"
    },
    {
      "linkId": "insurerOrganization",
      "mapsTo": "Patient.extension",
      "extractor": "extension",
      "extensionUrl": "https://imed.app/fhir/StructureDefinition/patient-insurer-organization",
      "valueType": "string"
    },
    {
      "linkId": "paymentCondition",
      "mapsTo": "Patient.extension",
      "extractor": "extension",
      "extensionUrl": "https://imed.app/fhir/StructureDefinition/patient-payment-condition",
      "valueType": "code"
    },
    {
      "linkId": "priceList",
      "mapsTo": "Patient.extension",
      "extractor": "extension",
      "extensionUrl": "https://imed.app/fhir/StructureDefinition/patient-price-list",
      "valueType": "code"
    },
    {
      "linkId": "referralSpecialty",
      "mapsTo": "Patient.extension",
      "extractor": "extension",
      "extensionUrl": "https://imed.app/fhir/StructureDefinition/patient-referral-specialty",
      "valueType": "code"
    },
    {
      "linkId": "referralFacility",
      "mapsTo": "Patient.extension",
      "extractor": "extension",
      "extensionUrl": "https://imed.app/fhir/StructureDefinition/patient-referral-facility",
      "valueType": "string"
    },
    {
      "linkId": "referralFacilityOther",
      "mapsTo": "Patient.extension",
      "extractor": "extension",
      "extensionUrl": "https://imed.app/fhir/StructureDefinition/patient-referral-facility-other",
      "valueType": "string"
    },
    {
      "linkId": "referringPractitioner",
      "mapsTo": "Patient.extension",
      "extractor": "extension",
      "extensionUrl": "https://imed.app/fhir/StructureDefinition/patient-referring-practitioner",
      "valueType": "string"
    },
    {
      "linkId": "admissionUser",
      "mapsTo": "Patient.extension",
      "extractor": "extension",
      "extensionUrl": "https://imed.app/fhir/StructureDefinition/patient-admission-user",
      "valueType": "string"
    },
    {
      "linkId": "responsibleCoverage",
      "mapsTo": "RelatedPerson.extension",
      "extractor": "extension",
      "extensionUrl": "https://imed.app/fhir/StructureDefinition/related-person-coverage",
      "valueType": "string"
    },
    {
      "linkId": "responsibleIsPrimary",
      "mapsTo": "RelatedPerson.extension",
      "extractor": "extension",
      "extensionUrl": "https://imed.app/fhir/StructureDefinition/related-person-is-primary",
      "valueType": "boolean"
    },
    {
      "linkId": "responsibleDni",
      "mapsTo": "RelatedPerson.identifier",
      "extractor": "identifier",
      "part": "value",
      "fixedType": "IdentidadNacional",
      "identifying": true
    },
    {
      "linkId": "responsibleDetailRelationship",
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
    },
    {
      "linkId": "responsibleFullName",
      "mapsTo": "RelatedPerson.name",
      "extractor": "human-name",
      "part": "full",
      "identifying": true
    },
    {
      "linkId": "responsibleOccupation",
      "mapsTo": "RelatedPerson.extension",
      "extractor": "extension",
      "extensionUrl": "https://imed.app/fhir/StructureDefinition/related-person-occupation",
      "valueType": "string"
    },
    {
      "linkId": "responsibleNationality",
      "mapsTo": "RelatedPerson.extension",
      "extractor": "extension",
      "extensionUrl": "https://imed.app/fhir/StructureDefinition/related-person-nationality",
      "valueType": "code"
    },
    {
      "linkId": "responsibleBirthDate",
      "mapsTo": "RelatedPerson.birthDate",
      "extractor": "primitive"
    },
    {
      "linkId": "responsibleMaritalStatus",
      "mapsTo": "RelatedPerson.extension",
      "extractor": "extension",
      "extensionUrl": "https://imed.app/fhir/StructureDefinition/related-person-marital-status",
      "valueType": "coding",
      "codingSystem": "http://terminology.hl7.org/CodeSystem/v3-MaritalStatus"
    },
    {
      "linkId": "responsibleAddress",
      "mapsTo": "RelatedPerson.address",
      "extractor": "address",
      "part": "text",
      "fixed": {
        "use": "home"
      }
    },
    {
      "linkId": "responsiblePhoneHome",
      "mapsTo": "RelatedPerson.telecom",
      "extractor": "contact-point",
      "system": "phone",
      "use": "home",
      "identifying": true
    },
    {
      "linkId": "responsiblePhoneMobile",
      "mapsTo": "RelatedPerson.telecom",
      "extractor": "contact-point",
      "system": "phone",
      "use": "mobile",
      "identifying": true
    },
    {
      "linkId": "responsibleInsuranceType",
      "mapsTo": "RelatedPerson.extension",
      "extractor": "extension",
      "extensionUrl": "https://imed.app/fhir/StructureDefinition/related-person-insurance-type",
      "valueType": "code"
    },
    {
      "linkId": "employerName",
      "mapsTo": "Patient.extension",
      "extractor": "extension",
      "extensionUrl": "https://imed.app/fhir/StructureDefinition/patient-employer-name",
      "valueType": "string"
    },
    {
      "linkId": "jobTitle",
      "mapsTo": "Patient.extension",
      "extractor": "extension",
      "extensionUrl": "https://imed.app/fhir/StructureDefinition/patient-job-title",
      "valueType": "string"
    },
    {
      "linkId": "employerAddress",
      "mapsTo": "Patient.extension",
      "extractor": "extension",
      "extensionUrl": "https://imed.app/fhir/StructureDefinition/patient-employer-address",
      "valueType": "string"
    },
    {
      "linkId": "employerPhone1",
      "mapsTo": "Patient.extension",
      "extractor": "extension",
      "extensionUrl": "https://imed.app/fhir/StructureDefinition/patient-employer-phone-1",
      "valueType": "string"
    },
    {
      "linkId": "employerPhone2",
      "mapsTo": "Patient.extension",
      "extractor": "extension",
      "extensionUrl": "https://imed.app/fhir/StructureDefinition/patient-employer-phone-2",
      "valueType": "string"
    }
  ]
}$json$::jsonb AS fhir_mapping_json
),
updated AS (
  UPDATE form_definitions d
  SET schema_json = base.schema_json,
      fhir_mapping_json = base.fhir_mapping_json
  FROM base
  WHERE d.organization_id IS NULL AND d.code = 'patient-registration' AND d.version = '1.0.0'
  RETURNING d.id
)
INSERT INTO form_definitions (
  id, code, specialty_code, version, status, title, description,
  schema_json, fhir_mapping_json, organization_id,
  created_at, created_by, published_at, published_by
)
SELECT
  gen_random_uuid(),
  'patient-registration',
  NULL,
  '1.0.0',
  'Published',
  'Registro de Paciente',
  'Formulario base del studio de formularios: Registro de Paciente.',
  base.schema_json,
  base.fhir_mapping_json,
  NULL,
  now(), '00000000-0000-0000-0000-000000000001', now(), '00000000-0000-0000-0000-000000000001'
FROM base
WHERE NOT EXISTS (SELECT 1 FROM updated);
