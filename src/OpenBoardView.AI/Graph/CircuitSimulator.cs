using System.Text.RegularExpressions;
using OpenBoardView.AI.Models;

namespace OpenBoardView.AI.Graph;

public record FlowEdgeDto(
    string Id,
    string FromComponentId,
    int FromX,
    int FromY,
    string ToComponentId,
    int ToX,
    int ToY,
    string NetName,
    string ThroughSeriesComponentId,
    double CurrentAmps,
    string Status); // "NORMAL", "HIGH_CURRENT_FAULT", "OPEN_ISOLATED"

public record ThermalHotspotDto(
    string ComponentId,
    string Category,
    string Quadrant,
    string Side,
    int X,
    int Y,
    string NetName,
    double PowerWatts,
    double EstimatedTempCelsius,
    double IntensityNormalized, // 0.0 to 1.0
    string Reason);

public record NetVoltageStateDto(
    string NetName,
    double NominalVoltage,
    double SimulatedVoltage,
    double ResistanceToGndOhms,
    string Status); // "HEALTHY", "SHORTED_TO_GND", "OPEN_UNPOWERED", "INJECTED"

public record SimulationResultDto(
    string RootNet,
    string? FaultedComponentId,
    double FaultResistanceOhms,
    List<string> LiftedComponentIds,
    double InjectedVoltage,
    double CurrentLimitAmps,
    double TotalCurrentDrawnAmps,
    bool IsShortIsolatedByLiftedPart,
    string DiagnosticSummary,
    List<NetVoltageStateDto> NetStates,
    List<FlowEdgeDto> FlowEdges,
    List<ThermalHotspotDto> ThermalHotspots);

public class SimulationRequestDto
{
    public string NetName { get; set; } = "PPBUS_G3H";
    public string? FaultedComponentId { get; set; }
    public double FaultResistanceOhms { get; set; } = 0.4;
    public List<string>? LiftedComponentIds { get; set; }
    public double? InjectedVoltage { get; set; }
    public double CurrentLimitAmps { get; set; } = 2.5;
    public string? InjectionPointComponentId { get; set; }
}

/// <summary>
/// Motor de Simulación Eléctrica DC, Continuidad al Levantar Componentes, Flujo de Corriente y Cámara Térmica (IR).
/// Calcula propagación nodal sobre el grafo real .brd y modela disipación térmica P = I^2 * R.
/// </summary>
public class CircuitSimulator
{
    private readonly CircuitGraphBuilder _graph;

    public CircuitSimulator(CircuitGraphBuilder graph)
    {
        _graph = graph ?? throw new ArgumentNullException(nameof(graph));
    }

    public SimulationResultDto RunSimulation(SimulationRequestDto req)
    {
        string targetNet = string.IsNullOrWhiteSpace(req.NetName) ? "PPBUS_G3H" : req.NetName.Trim().ToUpperInvariant();
        var liftedSet = new HashSet<string>(req.LiftedComponentIds ?? new List<string>(), StringComparer.OrdinalIgnoreCase);

        double nominalV = InferNominalVoltage(targetNet);
        double sourceV = req.InjectedVoltage.HasValue && req.InjectedVoltage.Value > 0
            ? req.InjectedVoltage.Value
            : nominalV;
        double maxCurrent = req.CurrentLimitAmps > 0 ? req.CurrentLimitAmps : 2.5;
        double rFault = Math.Max(0.05, req.FaultResistanceOhms);

        // 1. Obtener componentes en la red principal y sus sub-ramas (a través de F, L, R)
        var mainNetComponents = _graph.GetComponentsOnNet(targetNet);
        var hops = _graph.GetUpstreamPath(null, targetNet, maxDepth: 2);

        // Seleccionar automáticamente un componente en corto realista si no se especificó uno
        string? faultCompId = req.FaultedComponentId?.Trim().ToUpperInvariant();
        ComponentNodeSummary? faultComp = null;

        if (!string.IsNullOrWhiteSpace(faultCompId))
        {
            faultComp = _graph.GetComponentDetails(faultCompId);
        }

        if (faultComp == null)
        {
            // Elegir el primer capacitor a GND en el cuadrante de mayor densidad
            var quadrants = _graph.GetHighDensityQuadrants(targetNet);
            var topCapId = quadrants.SelectMany(q => q.ShuntCapacitorsToGnd).FirstOrDefault()
                           ?? mainNetComponents.FirstOrDefault(c => c.Category == "Capacitor")?.ComponentId
                           ?? mainNetComponents.FirstOrDefault()?.ComponentId;
            if (topCapId != null)
            {
                faultCompId = topCapId;
                faultComp = _graph.GetComponentDetails(topCapId);
            }
        }

        // 2. Identificar la fuente / punto de entrada de la línea (ej. F7040, L7030, U7000, L6995)
        var sourceComp = FindEntrySourceComponent(targetNet, mainNetComponents, hops, req.InjectionPointComponentId);

        // 3. Evaluar si algún componente levantado (ej. F7040) aísla la fuente del componente en corto
        bool shortIsolated = false;
        string? isolatingBridgeId = null;

        foreach (var liftedId in liftedSet)
        {
            var liftedDetails = _graph.GetComponentDetails(liftedId);
            if (liftedDetails != null && liftedDetails.Category is "Fuse" or "Inductor/Coil" or "Resistor")
            {
                // Si se levanta el fusible de entrada (ej. F7040 en PPBUS_G3H) y no se está inyectando directamente después del fusible:
                if (liftedDetails.ConnectedNets.Any(n => n.Equals(targetNet, StringComparison.OrdinalIgnoreCase)) &&
                    string.IsNullOrWhiteSpace(req.InjectionPointComponentId))
                {
                    shortIsolated = true;
                    isolatingBridgeId = liftedDetails.ComponentId;
                    break;
                }

                // O si el componente en corto está en una sub-red alimentada por este puente levantado
                if (faultComp != null && hops.Any(h =>
                        h.ThroughComponentId.Equals(liftedId, StringComparison.OrdinalIgnoreCase) &&
                        faultComp.ConnectedNets.Contains(h.ToNet, StringComparer.OrdinalIgnoreCase)))
                {
                    shortIsolated = true;
                    isolatingBridgeId = liftedDetails.ComponentId;
                    break;
                }
            }
        }

        // 4. Cálculo eléctrico DC (Ley de Ohm + Límite de Corriente CC/CV)
        double rSeriesPath = 0.12; // Resistencia de pistas + puente (fusible/shunt)
        double totalResistance = rSeriesPath + rFault;
        double rawCurrent = shortIsolated ? 0.02 : (sourceV / totalResistance);
        double actualCurrent = shortIsolated ? 0.02 : Math.Min(maxCurrent, Math.Round(rawCurrent, 2));
        double collapsedVoltage = shortIsolated
            ? nominalV
            : Math.Round(actualCurrent * rFault, 2);

        // 5. Estados de las Redes (NetStates)
        var netStates = new List<NetVoltageStateDto>();

        if (shortIsolated && isolatingBridgeId != null)
        {
            var bridge = _graph.GetComponentDetails(isolatingBridgeId);
            var upstreamNet = bridge?.ConnectedNets.FirstOrDefault(n => !n.Equals(targetNet, StringComparison.OrdinalIgnoreCase))
                              ?? $"UPSTREAM_{isolatingBridgeId}";

            netStates.Add(new NetVoltageStateDto(
                NetName: upstreamNet,
                NominalVoltage: nominalV,
                SimulatedVoltage: nominalV,
                ResistanceToGndOhms: 45000,
                Status: "HEALTHY"));

            netStates.Add(new NetVoltageStateDto(
                NetName: targetNet,
                NominalVoltage: nominalV,
                SimulatedVoltage: 0.0,
                ResistanceToGndOhms: rFault,
                Status: "OPEN_UNPOWERED"));
        }
        else
        {
            netStates.Add(new NetVoltageStateDto(
                NetName: targetNet,
                NominalVoltage: nominalV,
                SimulatedVoltage: collapsedVoltage,
                ResistanceToGndOhms: rFault,
                Status: req.InjectedVoltage.HasValue ? "INJECTED" : "SHORTED_TO_GND"));
        }

        // Agregar sub-redes conectadas por puentes en serie
        foreach (var hop in hops.Take(6))
        {
            bool hopBridgeLifted = liftedSet.Contains(hop.ThroughComponentId);
            netStates.Add(new NetVoltageStateDto(
                NetName: hop.ToNet,
                NominalVoltage: InferNominalVoltage(hop.ToNet),
                SimulatedVoltage: hopBridgeLifted || shortIsolated ? 0.0 : collapsedVoltage,
                ResistanceToGndOhms: hopBridgeLifted ? 99999 : rFault * 2,
                Status: hopBridgeLifted ? "OPEN_UNPOWERED" : "SHORTED_TO_GND"));
        }

        // 6. Construir Aristas de Flujo de Corriente Animado (FlowEdges)
        var flowEdges = new List<FlowEdgeDto>();
        if (sourceComp != null && faultComp != null)
        {
            // buscar nodos intermedios (puentes F, R, L) para que la ruta siga la geometría real de la placa
            var waypoints = new List<ComponentNodeSummary> { sourceComp };

            var seriesBridges = mainNetComponents
                .Where(c => c.Category is "Fuse" or "Resistor" or "Inductor/Coil" && c.ComponentId != sourceComp.ComponentId && c.ComponentId != faultComp.ComponentId)
                .OrderBy(c => Distance(sourceComp.X, sourceComp.Y, c.X, c.Y))
                .Take(2)
                .ToList();

            waypoints.AddRange(seriesBridges);
            waypoints.Add(faultComp);

            for (int i = 0; i < waypoints.Count - 1; i++)
            {
                var fromNode = waypoints[i];
                var toNode = waypoints[i + 1];
                bool isBlocked = shortIsolated || liftedSet.Contains(fromNode.ComponentId) || liftedSet.Contains(toNode.ComponentId);

                flowEdges.Add(new FlowEdgeDto(
                    Id: $"FLOW_{i + 1}",
                    FromComponentId: fromNode.ComponentId,
                    FromX: fromNode.X,
                    FromY: fromNode.Y,
                    ToComponentId: toNode.ComponentId,
                    ToX: toNode.X,
                    ToY: toNode.Y,
                    NetName: targetNet,
                    ThroughSeriesComponentId: fromNode.ComponentId,
                    CurrentAmps: isBlocked ? 0.0 : actualCurrent,
                    Status: isBlocked ? "OPEN_ISOLATED" : "HIGH_CURRENT_FAULT"
                ));
            }

            // Agregar ramas secundarias para visualizar distribución en la red
            foreach (var branchComp in mainNetComponents.Where(c => c.ComponentId != faultComp.ComponentId && c.ComponentId != sourceComp.ComponentId).Take(8))
            {
                bool isBlocked = shortIsolated || liftedSet.Contains(branchComp.ComponentId);
                flowEdges.Add(new FlowEdgeDto(
                    Id: $"BRANCH_{branchComp.ComponentId}",
                    FromComponentId: sourceComp.ComponentId,
                    FromX: sourceComp.X,
                    FromY: sourceComp.Y,
                    ToComponentId: branchComp.ComponentId,
                    ToX: branchComp.X,
                    ToY: branchComp.Y,
                    NetName: targetNet,
                    ThroughSeriesComponentId: sourceComp.ComponentId,
                    CurrentAmps: isBlocked ? 0.0 : Math.Round(actualCurrent * 0.12, 2),
                    Status: isBlocked ? "OPEN_ISOLATED" : "NORMAL"
                ));
            }
        }

        // 7. Calcular Mapa de Calor / Cámara Térmica Infrarroja (P = I^2 * R)
        var hotspots = new List<ThermalHotspotDto>();
        double ambientTempC = 25.0;

        if (!shortIsolated && faultComp != null)
        {
            double faultPowerW = Math.Round(actualCurrent * actualCurrent * rFault, 2);
            double faultTempC = Math.Min(135.0, Math.Round(ambientTempC + (faultPowerW * 38.0), 1));

            hotspots.Add(new ThermalHotspotDto(
                ComponentId: faultComp.ComponentId,
                Category: faultComp.Category,
                Quadrant: faultComp.Quadrant,
                Side: faultComp.Side,
                X: faultComp.X,
                Y: faultComp.Y,
                NetName: targetNet,
                PowerWatts: faultPowerW,
                EstimatedTempCelsius: faultTempC,
                IntensityNormalized: Math.Clamp((faultTempC - ambientTempC) / 85.0, 0.25, 1.0),
                Reason: $"Cortocircuito directo a GND ({rFault:F2} Ω). Disipa {faultPowerW:F2}W a {actualCurrent:F2}A."
            ));

            if (sourceComp != null && sourceComp.ComponentId != faultComp.ComponentId)
            {
                double bridgePowerW = Math.Round(actualCurrent * actualCurrent * rSeriesPath, 2);
                double bridgeTempC = Math.Min(95.0, Math.Round(ambientTempC + (bridgePowerW * 28.0), 1));

                hotspots.Add(new ThermalHotspotDto(
                    ComponentId: sourceComp.ComponentId,
                    Category: sourceComp.Category,
                    Quadrant: sourceComp.Quadrant,
                    Side: sourceComp.Side,
                    X: sourceComp.X,
                    Y: sourceComp.Y,
                    NetName: targetNet,
                    PowerWatts: bridgePowerW,
                    EstimatedTempCelsius: bridgeTempC,
                    IntensityNormalized: Math.Clamp((bridgeTempC - ambientTempC) / 85.0, 0.15, 0.75),
                    Reason: $"Calentamiento secundario por paso de corriente de falla ({actualCurrent:F2}A) en serie."
                ));
            }
        }

        string summary = shortIsolated
            ? $"✅ CORTO AISLADO: Al levantar [{string.Join(", ", liftedSet)}], el lado de generación recupera sus {nominalV:F2}V nominales y cesa el consumo hacia '{targetNet}'. El corto ({rFault:F2} Ω) queda confirmado del lado del sistema en {faultComp?.ComponentId} (Cuadrante {faultComp?.Quadrant})."
            : $"🔥 CORTO ACTIVO EN '{targetNet}': La tensión cae de {nominalV:F2}V a {collapsedVoltage:F2}V drenando {actualCurrent:F2}A hacia tierra. Cámara térmica detecta punto caliente crítico en {faultComp?.ComponentId} ({faultComp?.Quadrant}, cara {faultComp?.Side}) alcanzando ~{hotspots.FirstOrDefault()?.EstimatedTempCelsius ?? 25.0:F1} °C.";

        return new SimulationResultDto(
            RootNet: targetNet,
            FaultedComponentId: faultComp?.ComponentId,
            FaultResistanceOhms: rFault,
            LiftedComponentIds: liftedSet.ToList(),
            InjectedVoltage: sourceV,
            CurrentLimitAmps: maxCurrent,
            TotalCurrentDrawnAmps: actualCurrent,
            IsShortIsolatedByLiftedPart: shortIsolated,
            DiagnosticSummary: summary,
            NetStates: netStates,
            FlowEdges: flowEdges,
            ThermalHotspots: hotspots
        );
    }

    private ComponentNodeSummary? FindEntrySourceComponent(
        string netName,
        List<ComponentNodeSummary> netComponents,
        List<UpstreamHopDto> hops,
        string? explicitInjectionCompId)
    {
        if (!string.IsNullOrWhiteSpace(explicitInjectionCompId))
        {
            var exp = _graph.GetComponentDetails(explicitInjectionCompId);
            if (exp != null) return exp;
        }

        // Preferir fusibles (ej. F7040, F9700), luego bobinas (L7030, L6995), luego resistencias shunt (R5400, R7020) o ICs
        var fuse = netComponents.FirstOrDefault(c => c.Category == "Fuse");
        if (fuse != null) return fuse;

        var hopFuseId = hops.FirstOrDefault(h => h.ComponentCategory == "Fuse")?.ThroughComponentId;
        if (hopFuseId != null) return _graph.GetComponentDetails(hopFuseId);

        var inductor = netComponents.FirstOrDefault(c => c.Category == "Inductor/Coil");
        if (inductor != null) return inductor;

        var ic = netComponents.FirstOrDefault(c => c.Category == "IC/Chip");
        if (ic != null) return ic;

        return netComponents.FirstOrDefault();
    }

    private static double Distance(int x1, int y1, int x2, int y2)
    {
        double dx = x2 - x1;
        double dy = y2 - y1;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    public static double InferNominalVoltage(string netName)
    {
        if (string.IsNullOrWhiteSpace(netName)) return 3.3;
        string u = netName.ToUpperInvariant();

        if (u.Contains("PPBUS_G3H") || u.Contains("PPVBAT_G3H")) return 12.56;
        if (u.Contains("DCIN") || u.Contains("18V5")) return 16.5;
        if (u.Contains("PP3V42")) return 3.42;
        if (u.Contains("PP5V")) return 5.0;
        if (u.Contains("PP3V3")) return 3.3;
        if (u.Contains("PP1V8")) return 1.8;
        if (u.Contains("PP1V5")) return 1.5;
        if (u.Contains("PP1V05")) return 1.05;
        if (u.Contains("VCORE") || u.Contains("CPU")) return 1.15;
        if (u.Contains("LCDBKLT")) return 28.0;

        var m = Regex.Match(u, @"PP(\d+)V(\d+)");
        if (m.Success && double.TryParse($"{m.Groups[1].Value}.{m.Groups[2].Value}",
                System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture,
                out double parsed))
        {
            return parsed;
        }

        return 3.3;
    }
}
