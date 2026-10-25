using OpenBoardView.AI.Algorithms;
using OpenBoardView.AI.Models;
using OpenBoardView.AI.Parsers;

namespace OpenBoardView.AI.Graph;

public record ComponentNodeSummary(
    string ComponentId,
    string Category,
    string Quadrant,
    string Side,
    int X,
    int Y,
    int PinCount,
    bool ConnectedToGround,
    List<string> ConnectedNets,
    string? SchematicContext = null,
    List<int>? SchematicPages = null,
    string? SchematicValue = null,
    bool IsNoStuff = false);

public record QuadrantDensitySummary(
    string Quadrant,
    int TotalComponents,
    int CapacitorCount,
    int IcCount,
    int ResistorInductorCount,
    int TopSideCount,
    int BottomSideCount,
    List<string> ShuntCapacitorsToGnd,
    List<string> AllComponentIds);

public record UpstreamHopDto(
    int Step,
    string FromNet,
    string ThroughComponentId,
    string ComponentCategory,
    string Quadrant,
    string Side,
    string ToNet,
    string? SchematicContext);

/// <summary>
/// Fase 3: Motor del Grafo y Dependencias.
/// Une la topología física (.brd) y la semántica del esquemático (.pdf) en listas de adyacencia en memoria.
/// </summary>
public class CircuitGraphBuilder : IBrdTopologyProvider
{
    private readonly BoardTopology _topology;
    private readonly SchematicIndex? _schematicIndex;
    private readonly Dictionary<string, Component> _componentsById;
    private readonly Dictionary<string, Net> _netsByName;

    public CircuitGraphBuilder(BoardTopology topology, SchematicIndex? schematicIndex = null)
    {
        _topology = topology ?? throw new ArgumentNullException(nameof(topology));
        _schematicIndex = schematicIndex;

        if (_schematicIndex != null)
        {
            OpenBoardView.AI.Parsers.SchematicPdfReader.EnrichTopologyWithIndex(_topology, _schematicIndex);
        }

        _componentsById = _topology.Components
            .GroupBy(c => c.Id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        _netsByName = _topology.Nets
            .GroupBy(n => n.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
    }

    public BoardTopology Topology => _topology;
    public SchematicIndex? SchematicIndex => _schematicIndex;

    public Net? GetNetDetails(string netName) => ResolveNet(netName);

    /// <summary>Solo coincidencia exacta (sin búsqueda por subcadena). Usar cuando un falso positivo daría un dato erróneo.</summary>
    public Net? GetNetExact(string netName) =>
        !string.IsNullOrWhiteSpace(netName) && _netsByName.TryGetValue(netName.Trim(), out var n) ? n : null;

    public bool NetExists(string netName) => GetNetExact(netName) != null;

    public List<string> SearchNetNames(string query, int limit = 25)
    {
        if (string.IsNullOrWhiteSpace(query))
            return _topology.Nets.Where(n => !n.IsUnconnected).Take(limit).Select(n => n.Name).ToList();

        return _topology.Nets
            .Where(n => !n.IsUnconnected && n.Name.Contains(query.Trim(), StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(n => n.Name.Equals(query.Trim(), StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(n => n.Pins.Count)
            .Take(limit)
            .Select(n => n.Name)
            .ToList();
    }

    public ComponentNodeSummary? GetComponentDetails(string componentId)
    {
        if (string.IsNullOrWhiteSpace(componentId)) return null;
        if (!_componentsById.TryGetValue(componentId.Trim(), out var comp)) return null;

        return MapComponentSummary(comp);
    }

    public bool IsComponentPinConnectedToGnd(string componentId)
    {
        var comp = GetComponentDetails(componentId);
        return comp != null && comp.ConnectedToGround;
    }

    public List<string> GetParallelCapacitorIdsOnNet(string netName, string? excludeCompId = null)
    {
        var comps = GetComponentsOnNet(netName);
        return comps
            .Where(c => !c.IsNoStuff && c.Category == "Capacitor" && c.ConnectedToGround)
            .Where(c => excludeCompId == null || !c.ComponentId.Equals(excludeCompId, StringComparison.OrdinalIgnoreCase))
            .Select(c => c.ComponentId)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Identifica dinámicamente el riel principal de alimentación (línea maestra de standby / sistema)
    /// de la placa actualmente cargada, inspeccionando las redes del .brd sin asumir nombres fijos
    /// (ej. PP3V8_AON o PPBUS_AON en Apple Silicon M-series, PPBUS_G3H en Intel, etc.).
    /// </summary>
    public string GetBoardMasterPowerRail()
    {
        var powerNets = _topology.Nets
            .Where(n => !n.IsGround && !n.IsUnconnected && n.Pins.Count > 0)
            .Where(n => n.Name.StartsWith("PP", StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (powerNets.Count == 0)
        {
            var anyNet = _topology.Nets
                .Where(n => !n.IsGround && !n.IsUnconnected && n.Pins.Count > 0)
                .OrderByDescending(n => n.Pins.Count)
                .FirstOrDefault();
            return anyNet?.Name ?? "PPBUS_G3H";
        }

        var scored = powerNets.Select(n =>
        {
            int score = n.Pins.Count;
            string upper = n.Name.ToUpperInvariant();

            if (upper.Contains("PPBUS_G3H") || upper == "PPBUS_AON" || upper == "PP3V8_AON") score += 10000;
            else if (upper.StartsWith("PPBUS_") || upper.StartsWith("PP3V8_")) score += 5000;
            else if (upper.Contains("DCIN") || upper.Contains("VBUS")) score += 3000;
            else if (upper.Contains("AON") || upper.Contains("G3H") || upper.Contains("MAIN")) score += 2000;
            else if (upper.Contains("3V42") || upper.Contains("3V3_S5") || upper.Contains("5V_S5")) score += 1000;

            if (upper.Contains("_ISNS") || upper.Contains("SPKR") || upper.Contains("BKLT")) score -= 3000;

            return (Net: n, Score: score);
        })
        .OrderByDescending(x => x.Score)
        .First();

        return scored.Net.Name;
    }

    /// <summary>
    /// Devuelve todos los componentes conectados a una red específica, priorizando capacitores a tierra (candidatos a corto) e ICs.
    /// </summary>
    public List<ComponentNodeSummary> GetComponentsOnNet(string netName)
    {
        var net = ResolveNet(netName);
        if (net == null) return new List<ComponentNodeSummary>();

        var compIds = net.Pins
            .Select(p => p.ComponentId)
            .Distinct(StringComparer.OrdinalIgnoreCase);

        var result = new List<ComponentNodeSummary>();
        foreach (var id in compIds)
        {
            if (_componentsById.TryGetValue(id, out var comp))
            {
                result.Add(MapComponentSummary(comp));
            }
        }

        return result
            .OrderBy(c => c.IsNoStuff)
            .ThenByDescending(c => c.Category == "Capacitor" && c.ConnectedToGround)
            .ThenByDescending(c => c.Category == "IC/Chip")
            .ThenBy(c => c.ComponentId)
            .ToList();
    }

    /// <summary>
    /// Identifica en qué cuadrante (Q1-Q4) hay mayor densidad de componentes y capacitores de desacoplo (a GND) en la red afectada,
    /// incluyendo sub-ramas directas (ej. PPBUS_G3H -> PPBUS_S5_HS_COMPUTING_ISNS) separadas por resistencias de sensado o fusibles.
    /// </summary>
    public List<QuadrantDensitySummary> GetHighDensityQuadrants(string netName)
    {
        var componentsOnNet = GetComponentsOnNet(netName).ToDictionary(c => c.ComponentId, StringComparer.OrdinalIgnoreCase);

        // Si la red tiene pocas conexiones directas o se ramifica por resistencias de sensado (ISNS) / fusibles, incluir sub-redes a 1 salto
        var hops = GetUpstreamPath(null, netName, maxDepth: 1);
        foreach (var hop in hops.Where(h => h.ComponentCategory is "Fuse" or "Resistor" or "Inductor/Coil"))
        {
            if (hop.ToNet.StartsWith("PPBUS", StringComparison.OrdinalIgnoreCase) ||
                hop.ToNet.Contains(netName, StringComparison.OrdinalIgnoreCase))
            {
                foreach (var subComp in GetComponentsOnNet(hop.ToNet))
                {
                    componentsOnNet.TryAdd(subComp.ComponentId, subComp);
                }
            }
        }

        if (componentsOnNet.Count == 0) return new List<QuadrantDensitySummary>();

        return componentsOnNet.Values
            .GroupBy(c => c.Quadrant)
            .Select(g => new QuadrantDensitySummary(
                Quadrant: g.Key,
                TotalComponents: g.Count(),
                CapacitorCount: g.Count(c => c.Category == "Capacitor"),
                IcCount: g.Count(c => c.Category == "IC/Chip"),
                ResistorInductorCount: g.Count(c => c.Category is "Resistor" or "Inductor/Coil" or "Fuse"),
                TopSideCount: g.Count(c => c.Side == nameof(MountingSide.Top)),
                BottomSideCount: g.Count(c => c.Side == nameof(MountingSide.Bottom)),
                ShuntCapacitorsToGnd: g.Where(c => !c.IsNoStuff && c.Category == "Capacitor" && c.ConnectedToGround).Select(c => c.ComponentId).ToList(),
                AllComponentIds: g.Select(c => c.ComponentId).ToList()
            ))
            .OrderByDescending(q => q.ShuntCapacitorsToGnd.Count)
            .ThenByDescending(q => q.TotalComponents)
            .ToList();
    }

    /// <summary>
    /// BFS en el grafo para rastrear de dónde viene el voltaje o señal hacia atrás (a través de resistencias, bobinas, fusibles o diodos en serie).
    /// </summary>
    public List<UpstreamHopDto> GetUpstreamPath(string? startComponentId, string netName, int maxDepth = 4)
    {
        var startNet = ResolveNet(netName);
        if (startNet == null) return new List<UpstreamHopDto>();

        var visitedNets = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { startNet.Name };
        var visitedComponents = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(startComponentId))
        {
            visitedComponents.Add(startComponentId.Trim());
        }

        var queue = new Queue<(string CurrentNet, int Depth)>();
        queue.Enqueue((startNet.Name, 1));

        var hops = new List<UpstreamHopDto>();

        while (queue.Count > 0)
        {
            var (currNetName, depth) = queue.Dequeue();
            if (depth > maxDepth) continue;

            if (!_netsByName.TryGetValue(currNetName, out var currNet))
                continue;

            var compIds = currNet.Pins
                .Select(p => p.ComponentId)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(id => id.StartsWith("F", StringComparison.OrdinalIgnoreCase))
                .ThenByDescending(id => id.StartsWith("L", StringComparison.OrdinalIgnoreCase))
                .ThenByDescending(id => id.StartsWith("R", StringComparison.OrdinalIgnoreCase));

            foreach (var compId in compIds)
            {
                if (visitedComponents.Contains(compId)) continue;
                if (!_componentsById.TryGetValue(compId, out var comp)) continue;

                // Para rastrear caminos en serie (upstream/downstream), atravesamos componentes puente (R, L, F, D, Q) o de 2-3 pines
                bool isSeriesBridge = comp.Category is "Fuse" or "Inductor/Coil" or "Resistor" or "Diode" or "Transistor/MOSFET"
                                      || (comp.PinCount >= 2 && comp.PinCount <= 4 && comp.Category != "Capacitor");

                if (!isSeriesBridge) continue;

                var otherNets = comp.Pins
                    .Select(p => p.NetName)
                    .Where(n => !string.Equals(n, currNetName, StringComparison.OrdinalIgnoreCase))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                foreach (var nextNetName in otherNets)
                {
                    if (!_netsByName.TryGetValue(nextNetName, out var nextNet)) continue;
                    if (nextNet.IsGround || nextNet.IsUnconnected) continue;

                    visitedComponents.Add(compId);
                    hops.Add(new UpstreamHopDto(
                        Step: depth,
                        FromNet: currNetName,
                        ThroughComponentId: comp.Id,
                        ComponentCategory: comp.Category,
                        Quadrant: comp.Quadrant,
                        Side: comp.Side.ToString(),
                        ToNet: nextNet.Name,
                        SchematicContext: comp.SchematicContext
                    ));

                    if (!visitedNets.Contains(nextNet.Name) && hops.Count < 30)
                    {
                        visitedNets.Add(nextNet.Name);
                        queue.Enqueue((nextNet.Name, depth + 1));
                    }
                }
            }
        }

        return hops;
    }

    public string SearchSchematicText(string keyword)
    {
        if (_schematicIndex == null || string.IsNullOrWhiteSpace(keyword))
            return "Índice de esquemático PDF no disponible o consulta vacía.";

        var kw = keyword.Trim();

        if (_schematicIndex.ComponentContext.TryGetValue(kw, out var compHits))
        {
            return string.Join("\n", compHits.Select(h => $"[Página {h.PageNumber}] {h.Snippet}"));
        }

        if (_schematicIndex.NetContext.TryGetValue(kw, out var netHits))
        {
            return string.Join("\n", netHits.Select(h => $"[Página {h.PageNumber}] {h.Snippet}"));
        }

        var generalHits = new List<string>();
        foreach (var kvp in _schematicIndex.PageTexts)
        {
            if (kvp.Value.Contains(kw, StringComparison.OrdinalIgnoreCase))
            {
                var matchingLines = kvp.Value
                    .Split('\n')
                    .Where(l => l.Contains(kw, StringComparison.OrdinalIgnoreCase))
                    .Take(3);
                foreach (var line in matchingLines)
                {
                    generalHits.Add($"[Página {kvp.Key}] {line.Trim()}");
                }
                if (generalHits.Count >= 10) break;
            }
        }

        return generalHits.Count > 0
            ? string.Join("\n", generalHits)
            : $"No se encontraron coincidencias para '{kw}' en el esquemático PDF.";
    }

    private Net? ResolveNet(string netName)
    {
        if (string.IsNullOrWhiteSpace(netName)) return null;
        string clean = netName.Trim();

        if (_netsByName.TryGetValue(clean, out var exactNet))
            return exactNet;

        return _topology.Nets
            .FirstOrDefault(n => !n.IsUnconnected && n.Name.Contains(clean, StringComparison.OrdinalIgnoreCase));
    }

    private ComponentNodeSummary MapComponentSummary(Component comp)
    {
        var nets = comp.Pins
            .Select(p => p.NetName)
            .Where(n => !string.IsNullOrWhiteSpace(n) && !n.Equals("UNCONNECTED", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        bool hasGround = nets.Any(n =>
            n.Equals("GND", StringComparison.OrdinalIgnoreCase) ||
            n.StartsWith("GND_", StringComparison.OrdinalIgnoreCase) ||
            n.EndsWith("_GND", StringComparison.OrdinalIgnoreCase) ||
            n.Contains("GND", StringComparison.OrdinalIgnoreCase) ||
            n.Contains("VSS", StringComparison.OrdinalIgnoreCase));

        return new ComponentNodeSummary(
            ComponentId: comp.Id,
            Category: comp.Category,
            Quadrant: comp.Quadrant,
            Side: comp.Side.ToString(),
            X: comp.X,
            Y: comp.Y,
            PinCount: comp.PinCount,
            ConnectedToGround: hasGround,
            ConnectedNets: nets,
            SchematicContext: comp.SchematicContext,
            SchematicPages: comp.SchematicPages,
            SchematicValue: comp.SchematicValue,
            IsNoStuff: comp.IsNoStuff
        );
    }
}
