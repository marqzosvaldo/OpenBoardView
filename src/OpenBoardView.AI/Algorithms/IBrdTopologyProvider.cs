using OpenBoardView.AI.Graph;
using OpenBoardView.AI.Models;

namespace OpenBoardView.AI.Algorithms;

/// <summary>
/// Proveedor de consultas topológicas CAD sobre la placa física (.brd).
/// Permite al filtro de capacitores en paralelo verificar conexiones a GND y descubrir
/// componentes de desacoplo redundantes en un riel de voltaje.
/// </summary>
public interface IBrdTopologyProvider
{
    /// <summary>
    /// Determina si al menos un pin del componente está conectado directamente a tierra (GND/VSS).
    /// </summary>
    bool IsComponentPinConnectedToGnd(string componentId);

    /// <summary>
    /// Obtiene la lista de IDs de capacitores de desacoplo (conectados a GND) en una red dada.
    /// </summary>
    List<string> GetParallelCapacitorIdsOnNet(string netName, string? excludeCompId = null);

    /// <summary>
    /// Obtiene los detalles de ubicación (coordenadas, cuadrante, capa) de un componente.
    /// </summary>
    ComponentNodeSummary? GetComponentDetails(string componentId);
}
