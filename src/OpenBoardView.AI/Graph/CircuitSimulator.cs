using System.Globalization;
using System.Text.RegularExpressions;
using OpenBoardView.AI.Knowledge;
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
    string Status, // "HEALTHY", "SHORTED_TO_GND", "OPEN_UNPOWERED", "INJECTED"
    string VoltageSource = DataSources.Inferred);

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
    List<ThermalHotspotDto> ThermalHotspots,
    string VoltageSource = DataSources.Inferred,
    string VerificationState = "THEORETICAL_SIMULATION",
    int ParallelCandidateCount = 0,
    List<string>? RecommendedIsolationBridges = null);

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
/// Resuelve tensiones jerárquicamente (1. JSON curado -> 2. Propiedad CAD VOLTAGE= del PDF -> 3. Prefijo PP explícito),
/// modela buses en paralelo reales con puentes en serie demostrables (F, R shunt, L) y excluye componentes NO STUFF.
/// </summary>
public class CircuitSimulator
{
    private readonly CircuitGraphBuilder _graph;
    private readonly FlowKnowledgeBase? _knowledge;
    private readonly NetVoltageResolver _voltages;

    public CircuitSimulator(CircuitGraphBuilder graph, FlowKnowledgeBase? knowledge = null)
    {
        _graph = graph ?? throw new ArgumentNullException(nameof(graph));
        _knowledge = knowledge;
        _voltages = new NetVoltageResolver(graph, knowledge);
    }

    public (double? Voltage, string Source) ResolveNominalVoltage(string netName)
    {
        var r = _voltages.Resolve(netName);
        return (r.Voltage, r.Source);
    }

    public SimulationResultDto RunSimulation(SimulationRequestDto req)
    {
        string rawNet = string.IsNullOrWhiteSpace(req.NetName) ? _graph.GetBoardMasterPowerRail() : req.NetName.Trim().ToUpperInvariant();
        string targetNet = _graph.NetExists(rawNet) ? rawNet : _graph.GetBoardMasterPowerRail();
        var liftedSet = new HashSet<string>(req.LiftedComponentIds ?? new List<string>(), StringComparer.OrdinalIgnoreCase);

        var (resolvedNominalV, voltageSource) = ResolveNominalVoltage(targetNet);
        double nominalV = resolvedNominalV ?? 0.0;
        double sourceV = req.InjectedVoltage.HasValue && req.InjectedVoltage.Value > 0
            ? req.InjectedVoltage.Value
            : (nominalV > 0 ? nominalV : 1.0);
        double maxCurrent = req.CurrentLimitAmps > 0 ? req.CurrentLimitAmps : 2.5;
        double rFault = Math.Max(0.05, req.FaultResistanceOhms);

        // 1. Obtener componentes en la red principal y sus sub-ramas a 1-2 saltos (a través de F, L, R)
        var mainNetComponents = _graph.GetComponentsOnNet(targetNet);
        var hops = _graph.GetUpstreamPath(null, targetNet, maxDepth: 2);
        var quadrants = _graph.GetHighDensityQuadrants(targetNet);
        int parallelCandidateCount = quadrants.Sum(q => q.ShuntCapacitorsToGnd.Count);

        bool userSpecifiedFaultComp = !string.IsNullOrWhiteSpace(req.FaultedComponentId);
        string? faultCompId = req.FaultedComponentId?.Trim().ToUpperInvariant();
        ComponentNodeSummary? faultComp = null;

        if (userSpecifiedFaultComp && faultCompId != null)
        {
            faultComp = _graph.GetComponentDetails(faultCompId);
        }

        if (faultComp == null)
        {
            // Elegir un candidato poblado (!IsNoStuff) conectado a GND para ejemplificar el escenario hipotético
            var topCapId = quadrants.SelectMany(q => q.ShuntCapacitorsToGnd).FirstOrDefault()
                           ?? mainNetComponents.FirstOrDefault(c => !c.IsNoStuff && c.Category == "Capacitor" && c.ConnectedToGround)?.ComponentId
                           ?? mainNetComponents.FirstOrDefault(c => !c.IsNoStuff && c.Category == "Capacitor")?.ComponentId
                           ?? mainNetComponents.FirstOrDefault(c => !c.IsNoStuff)?.ComponentId;
            if (topCapId != null)
            {
                faultCompId = topCapId;
                faultComp = _graph.GetComponentDetails(topCapId);
            }
        }

        // 2. Identificar la fuente / punto de entrada de la línea (ej. F7040, L7030, U7000, L6995)
        var sourceComp = FindEntrySourceComponent(targetNet, mainNetComponents, hops, req.InjectionPointComponentId);

        // Identificar si el componente en falla está en una sub-rama separada por un puente en serie real (ej. R5400 -> PPBUS_S5_HS_COMPUTING_ISNS)
        UpstreamHopDto? seriesBranchHop = null;
        ComponentNodeSummary? seriesBranchBridge = null;
        if (faultComp != null && !faultComp.ConnectedNets.Contains(targetNet, StringComparer.OrdinalIgnoreCase))
        {
            seriesBranchHop = hops.FirstOrDefault(h =>
                h.FromNet.Equals(targetNet, StringComparison.OrdinalIgnoreCase) &&
                faultComp.ConnectedNets.Contains(h.ToNet, StringComparer.OrdinalIgnoreCase));

            if (seriesBranchHop != null)
            {
                seriesBranchBridge = _graph.GetComponentDetails(seriesBranchHop.ThroughComponentId);
            }
        }

        // Puentes recomendados para aislar ramas en la práctica de taller
        var isolationBridges = hops
            .Where(h => h.Step == 1 && h.ComponentCategory is "Fuse" or "Resistor" or "Inductor/Coil")
            .Select(h => h.ThroughComponentId)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(6)
            .ToList();
        if (sourceComp != null && sourceComp.Category is "Fuse" or "Inductor/Coil" or "Resistor" &&
            !isolationBridges.Contains(sourceComp.ComponentId, StringComparer.OrdinalIgnoreCase))
        {
            isolationBridges.Insert(0, sourceComp.ComponentId);
        }

        // 3. Evaluar si algún componente levantado (ej. F7040 o R5400) aísla la fuente del componente en corto
        bool shortIsolated = false;
        string? isolatingBridgeId = null;

        foreach (var liftedId in liftedSet)
        {
            var liftedDetails = _graph.GetComponentDetails(liftedId);
            if (liftedDetails != null && liftedDetails.Category is "Fuse" or "Inductor/Coil" or "Resistor")
            {
                if (liftedDetails.ConnectedNets.Any(n => n.Equals(targetNet, StringComparison.OrdinalIgnoreCase)) &&
                    string.IsNullOrWhiteSpace(req.InjectionPointComponentId) &&
                    (sourceComp == null || liftedDetails.ComponentId.Equals(sourceComp.ComponentId, StringComparison.OrdinalIgnoreCase)))
                {
                    shortIsolated = true;
                    isolatingBridgeId = liftedDetails.ComponentId;
                    break;
                }

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
        double rSeriesPath = 0.12;
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
            var (upV, upSrc) = ResolveNominalVoltage(upstreamNet);

            netStates.Add(new NetVoltageStateDto(
                NetName: upstreamNet,
                NominalVoltage: upV ?? nominalV,
                SimulatedVoltage: upV ?? nominalV,
                ResistanceToGndOhms: 45000,
                Status: "HEALTHY",
                VoltageSource: upSrc));

            netStates.Add(new NetVoltageStateDto(
                NetName: targetNet,
                NominalVoltage: nominalV,
                SimulatedVoltage: 0.0,
                ResistanceToGndOhms: rFault,
                Status: "OPEN_UNPOWERED",
                VoltageSource: voltageSource));
        }
        else
        {
            netStates.Add(new NetVoltageStateDto(
                NetName: targetNet,
                NominalVoltage: nominalV,
                SimulatedVoltage: collapsedVoltage,
                ResistanceToGndOhms: rFault,
                Status: req.InjectedVoltage.HasValue ? "INJECTED" : "SHORTED_TO_GND",
                VoltageSource: voltageSource));
        }

        foreach (var hop in hops.Take(6))
        {
            bool hopBridgeLifted = liftedSet.Contains(hop.ThroughComponentId);
            var (hopV, hopSrc) = ResolveNominalVoltage(hop.ToNet);
            netStates.Add(new NetVoltageStateDto(
                NetName: hop.ToNet,
                NominalVoltage: hopV ?? 0.0,
                SimulatedVoltage: hopBridgeLifted || shortIsolated ? 0.0 : collapsedVoltage,
                ResistanceToGndOhms: hopBridgeLifted ? 99999 : rFault * 2,
                Status: hopBridgeLifted ? "OPEN_UNPOWERED" : "SHORTED_TO_GND",
                VoltageSource: hopSrc));
        }

        // 6. Construir Aristas de Flujo de Corriente Realista (Topología en Estrella / Paralelo + Puentes en Serie Reales)
        var flowEdges = new List<FlowEdgeDto>();
        if (sourceComp != null && faultComp != null)
        {
            var waypoints = new List<(ComponentNodeSummary Node, string Net)> { (sourceComp, targetNet) };

            // Solo insertar un puente intermedio si DEMOSTRABLEMENTE conecta targetNet con la sub-red de faultComp (ej. R5400)
            if (seriesBranchBridge != null && seriesBranchHop != null &&
                seriesBranchBridge.ComponentId != sourceComp.ComponentId &&
                seriesBranchBridge.ComponentId != faultComp.ComponentId)
            {
                waypoints.Add((seriesBranchBridge, seriesBranchHop.ToNet));
            }

            string finalNet = seriesBranchHop?.ToNet ?? targetNet;
            waypoints.Add((faultComp, finalNet));

            for (int i = 0; i < waypoints.Count - 1; i++)
            {
                var fromNode = waypoints[i].Node;
                var toNode = waypoints[i + 1].Node;
                string edgeNet = waypoints[i].Net;
                bool isBlocked = shortIsolated || liftedSet.Contains(fromNode.ComponentId) || liftedSet.Contains(toNode.ComponentId);

                flowEdges.Add(new FlowEdgeDto(
                    Id: $"FLOW_{i + 1}",
                    FromComponentId: fromNode.ComponentId,
                    FromX: fromNode.X,
                    FromY: fromNode.Y,
                    ToComponentId: toNode.ComponentId,
                    ToX: toNode.X,
                    ToY: toNode.Y,
                    NetName: edgeNet,
                    ThroughSeriesComponentId: fromNode.ComponentId,
                    CurrentAmps: isBlocked ? 0.0 : actualCurrent,
                    Status: isBlocked ? "OPEN_ISOLATED" : "HIGH_CURRENT_FAULT"
                ));
            }

            // Ramas en paralelo sobre el mismo plano de cobre: sufren la caída de tensión, pero NO transportan la corriente del corto (0.0A)
            foreach (var branchComp in mainNetComponents
                         .Where(c => !c.IsNoStuff &&
                                     c.ComponentId != faultComp.ComponentId &&
                                     c.ComponentId != sourceComp.ComponentId &&
                                     c.ComponentId != seriesBranchBridge?.ComponentId)
                         .Take(8))
            {
                bool isBlocked = shortIsolated || liftedSet.Contains(branchComp.ComponentId);
                flowEdges.Add(new FlowEdgeDto(
                    Id: $"PARALLEL_{branchComp.ComponentId}",
                    FromComponentId: sourceComp.ComponentId,
                    FromX: sourceComp.X,
                    FromY: sourceComp.Y,
                    ToComponentId: branchComp.ComponentId,
                    ToX: branchComp.X,
                    ToY: branchComp.Y,
                    NetName: targetNet,
                    ThroughSeriesComponentId: sourceComp.ComponentId,
                    CurrentAmps: 0.0,
                    Status: isBlocked ? "OPEN_ISOLATED" : "NORMAL"
                ));
            }
        }

        // 7. Estimación Térmica Heurística (P = I^2 * R)
        var hotspots = new List<ThermalHotspotDto>();
        double ambientTempC = 25.0;

        if (!shortIsolated && faultComp != null)
        {
            double faultPowerW = Math.Round(actualCurrent * actualCurrent * rFault, 2);
            double faultTempC = Math.Min(135.0, Math.Round(ambientTempC + (faultPowerW * 38.0), 1));
            string valInfo = !string.IsNullOrWhiteSpace(faultComp.SchematicValue) ? $" [{faultComp.SchematicValue}]" : string.Empty;

            hotspots.Add(new ThermalHotspotDto(
                ComponentId: faultComp.ComponentId,
                Category: faultComp.Category,
                Quadrant: faultComp.Quadrant,
                Side: faultComp.Side,
                X: faultComp.X,
                Y: faultComp.Y,
                NetName: seriesBranchHop?.ToNet ?? targetNet,
                PowerWatts: faultPowerW,
                EstimatedTempCelsius: faultTempC,
                IntensityNormalized: Math.Clamp((faultTempC - ambientTempC) / 85.0, 0.25, 1.0),
                Reason: $"Hipótesis de derivación a GND ({rFault:F2} Ω){valInfo}. Disipación estimada: {faultPowerW:F2}W a {actualCurrent:F2}A (modelo empírico ~38°C/W)."
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
                    Reason: $"Calentamiento secundario en elemento de entrada en serie ({actualCurrent:F2}A)."
                ));
            }
        }

        string verificationState = shortIsolated
            ? "BRANCH_ISOLATION_TEST"
            : (userSpecifiedFaultComp ? "HYPOTHETICAL_COMPONENT_FAULT" : "THEORETICAL_SIMULATION");

        string noStuffWarning = faultComp is { IsNoStuff: true }
            ? $" ⚠️ ATENCIÓN: El esquemático PDF marca a {faultComp.ComponentId} como NO STUFF (no viene soldado de fábrica)."
            : string.Empty;

        string branchExplanation = seriesBranchHop != null
            ? $" a través del puente en serie {seriesBranchHop.ThroughComponentId} hacia la sub-rama '{seriesBranchHop.ToNet}'"
            : string.Empty;

        string candidateContext = userSpecifiedFaultComp
            ? $"evaluando falla puntual en {faultComp?.ComponentId}"
            : $"ejemplificado en {faultComp?.ComponentId} (1 de {Math.Max(1, parallelCandidateCount)} capacitores en paralelo a GND en esta red)";

        string bridgesHint = isolationBridges.Count > 0
            ? $" Para aislar la rama físicamente sin desoldar capacitores al azar, levanta o mide caída de tensión en: [{string.Join(", ", isolationBridges)}]."
            : string.Empty;

        string summary = shortIsolated
            ? $"🔍 AISLAMIENTO SIMULADO (Hipótesis de bisección): Al levantar [{string.Join(", ", liftedSet)}], el nodo previo recupera sus {nominalV:F2}V (fuente: {voltageSource}) y cesa el drenaje hacia '{targetNet}'. Esto es consistente con una derivación a GND ({rFault:F2} Ω) aguas abajo{branchExplanation} ({candidateContext}).{noStuffWarning}"
            : $"⚠️ HIPÓTESIS DE CORTO EN '{targetNet}' ({candidateContext}): Tensión nominal {nominalV:F2}V (fuente: {voltageSource}) colapsaría a {collapsedVoltage:F2}V drenando {actualCurrent:F2}A{branchExplanation}. Estimación térmica (~38°C/W): ~{hotspots.FirstOrDefault()?.EstimatedTempCelsius ?? 25.0:F1} °C en {faultComp?.ComponentId} ({faultComp?.Quadrant}, cara {faultComp?.Side}). Requiere medición física con multímetro/cámara IR.{bridgesHint}{noStuffWarning}";

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
            ThermalHotspots: hotspots,
            VoltageSource: voltageSource,
            VerificationState: verificationState,
            ParallelCandidateCount: parallelCandidateCount,
            RecommendedIsolationBridges: isolationBridges
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

        var fuse = netComponents.FirstOrDefault(c => !c.IsNoStuff && c.Category == "Fuse");
        if (fuse != null) return fuse;

        var hopFuseId = hops.FirstOrDefault(h => h.ComponentCategory == "Fuse")?.ThroughComponentId;
        if (hopFuseId != null) return _graph.GetComponentDetails(hopFuseId);

        var inductor = netComponents.FirstOrDefault(c => !c.IsNoStuff && c.Category == "Inductor/Coil");
        if (inductor != null) return inductor;

        var ic = netComponents.FirstOrDefault(c => !c.IsNoStuff && c.Category == "IC/Chip");
        if (ic != null) return ic;

        return netComponents.FirstOrDefault(c => !c.IsNoStuff) ?? netComponents.FirstOrDefault();
    }

    /// <summary>
    /// Infiere tensión nominal SOLO si el nombre de la red codifica explícitamente un riel de potencia.
    /// Si es una señal lógica o nodo sin tensión explícita en su nombre, devuelve null (nunca inventa 3.3V).
    /// </summary>
    public static double? TryInferNominalVoltage(string? netName)
    {
        if (string.IsNullOrWhiteSpace(netName)) return null;
        string u = netName.Trim().ToUpperInvariant().TrimStart('=');

        if (u.Equals("GND") || u.StartsWith("GND_") || u.EndsWith("_GND") || u.Contains("VSS"))
            return 0.0;

        // 1. Etapas alimentadas por PPBUS / PPVBAT antes de cualquier etapa elevadora (p. ej. PPBUS_S0_LCDBKLT_FUSED = 12.56V)
        if (u.StartsWith("PPBUS") || u.StartsWith("PPVBAT") || u.StartsWith("PPVIN_S5_HS_") || u.StartsWith("PPVIN_S0_") || u.StartsWith("PPVIN_S3_"))
            return 12.56;

        // 2. Salida ya elevada (Boost) de Backlight (solo después del diodo rectificador D9701: PPVOUT_SW_LCDBKLT)
        if (u.StartsWith("PPVOUT") && u.Contains("LCDBKLT"))
            return 28.5;

        // 3. Entrada del adaptador MagSafe / DC-In
        if (u.Contains("DCIN") || u.Contains("18V5"))
            return 16.5;

        // 4. Rieles explícitos de VCore / System Agent / VTT
        if (u.StartsWith("PPVCORE") || u.StartsWith("PPCPUVCORE"))
            return 1.15;
        if (u.StartsWith("PPVCCSA"))
            return 0.9;
        if (u.StartsWith("PPVTT"))
            return 0.75;
        if (u.StartsWith("PPDDR"))
            return 1.5;
        if (u.StartsWith("PPCPUVCCIO") || u.StartsWith("PPVCCIO"))
            return 1.05;
        if (u.StartsWith("PPVRTC"))
            return 3.3;

        // 5. Expresión regular general para rieles codificados como PP<entero>V<decimal opcional> (ej. PP3V42, PP15V_T29, PP5V_S0, PP1V05_S0, PP0V75)
        var m = Regex.Match(u, @"PP(\d+)V(\d+)?");
        if (m.Success)
        {
            string intPart = m.Groups[1].Value;
            string decPart = m.Groups[2].Success ? m.Groups[2].Value : string.Empty;
            string numeric = string.IsNullOrEmpty(decPart) ? intPart : $"{intPart}.{decPart}";

            if (double.TryParse(numeric, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed))
            {
                return parsed;
            }
        }

        // Si no es un riel PP con tensión codificada, devolver null en lugar de inventar 3.3V
        return null;
    }

    public static double InferNominalVoltage(string netName) =>
        TryInferNominalVoltage(netName) ?? 0.0;
}
