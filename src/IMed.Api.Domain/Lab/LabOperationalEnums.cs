namespace IMed.Api.Domain.Lab;

/// <summary>Destino de la orden tras el enrutamiento (LIS-DEC-13).</summary>
public enum OrderRouting
{
    /// <summary>Procesada en el laboratorio de esta sede → entra a la worklist.</summary>
    InHouse = 0,
    /// <summary>Referida a laboratorio externo (la sede no realiza la prueba).</summary>
    SendOut = 1,
}

/// <summary>Estado operativo de la recepción de una orden.</summary>
public enum OrderIntakeStatus
{
    Pending = 0,
    Received = 1,
    Blocked = 2,
    Completed = 3,
}

/// <summary>Tipo de evento del ciclo de vida de la muestra.</summary>
public enum SpecimenEventType
{
    Received = 0,
    InProcess = 1,
    Centrifuged = 2,
    Aliquoted = 3,
    Rejected = 4,
    Stored = 5,
}

/// <summary>Incidencia preanalítica registrada sobre una muestra.</summary>
public enum SpecimenIncident
{
    Hemolysis = 0,
    InsufficientVolume = 1,
    Clotted = 2,
    Mislabeled = 3,
    Other = 4,
}
