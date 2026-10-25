using System.Globalization;
using System.Text.RegularExpressions;
using OpenBoardView.AI.Knowledge;

namespace OpenBoardView.AI.Graph;

/// <summary>Voltaje resuelto para una red, con su origen y (si aplica) el puente por el que se heredó.</summary>
public record ResolvedVoltage(double? Voltage, string Source, string? BridgedFrom = null, string? BridgeComponent = null);

/// <summary>
/// Resolución única de tensiones nominales, compartida por el simulador y el Debugger de laberinto.
/// Orden de prioridad:
///   1. Base de conocimiento curada de la placa.
///   2. Herencia eléctrica por puente de baja impedancia (0 Ω / SHORT / shunt ≤ 0.1 Ω / fusible / bobina):
///      dos redes unidas así son el mismo nodo DC, así que comparten tensión (ej. R0960 = 0 Ω une PPBUS_G3H con PP15V_T29).
///   3. Atributo CAD VOLTAGE= del PDF. Ojo: en Cadence es la tensión de DISEÑO usada para reglas de ruteo
///      (a menudo la máxima), no necesariamente la nominal en operación.
///   4. Inferencia estricta por nombre (PP5V_S3 → 5 V). Nunca inventa 3.3 V para señales.
/// </summary>
public class NetVoltageResolver
{
    /// <summary>Un resistor por debajo de este valor se considera puente (0 Ω, jumper o shunt de sensado).</summary>
    public const double BridgeMaxOhms = 0.1;

    private static readonly Regex OhmsRegex = new(
        @"^\s*([0-9]*\.?[0-9]+)\s*([KMG]?)\s*(?:OHM|Ω)?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private readonly CircuitGraphBuilder _graph;
    private readonly FlowKnowledgeBase? _knowledge;

    public NetVoltageResolver(CircuitGraphBuilder graph, FlowKnowledgeBase? knowledge = null)
    {
        _graph = graph ?? throw new ArgumentNullException(nameof(graph));
        _knowledge = knowledge;
    }

    public ResolvedVoltage Resolve(string? netName) =>
        string.IsNullOrWhiteSpace(netName)
            ? new ResolvedVoltage(null, DataSources.Unknown)
            : Resolve(netName.Trim(), new HashSet<string>(StringComparer.OrdinalIgnoreCase), allowBridge: true);

    private ResolvedVoltage Resolve(string net, HashSet<string> visited, bool allowBridge)
    {
        visited.Add(net);

        if (_knowledge != null && _knowledge.TryGetCuratedVoltage(net, out double curated))
            return new ResolvedVoltage(curated, DataSources.Curated);

        bool existsInBoard = _graph.NetExists(net);

        if (allowBridge && existsInBoard)
        {
            foreach (var (otherNet, bridgeId) in GetLowImpedanceNeighbors(net))
            {
                if (visited.Contains(otherNet)) continue;
                // Solo un salto de herencia para no propagar por cadenas largas de forma opaca.
                var inherited = Resolve(otherNet, visited, allowBridge: false);
                if (inherited.Voltage.HasValue && inherited.Voltage.Value > 0 &&
                    inherited.Source is DataSources.Curated or DataSources.Inferred or DataSources.SchematicPdf)
                {
                    return new ResolvedVoltage(inherited.Voltage, DataSources.Bridged, otherNet, bridgeId);
                }
            }
        }

        var details = existsInBoard ? _graph.GetNetExact(net) : null;
        if (details?.SchematicVoltage is double pdf && pdf > 0)
            return new ResolvedVoltage(pdf, DataSources.SchematicPdf);

        double? inferred = CircuitSimulator.TryInferNominalVoltage(net);
        if (inferred.HasValue)
            return new ResolvedVoltage(inferred.Value, DataSources.Inferred);

        return new ResolvedVoltage(null, DataSources.Unknown);
    }

    /// <summary>Redes unidas a <paramref name="net"/> por un elemento de impedancia despreciable (mismo nodo DC).</summary>
    public IEnumerable<(string Net, string ComponentId)> GetLowImpedanceNeighbors(string net)
    {
        foreach (var comp in _graph.GetComponentsOnNet(net))
        {
            if (comp.IsNoStuff || !IsLowImpedanceBridge(comp)) continue;
            foreach (var other in comp.ConnectedNets)
            {
                if (other.Equals(net, StringComparison.OrdinalIgnoreCase)) continue;
                if (other.StartsWith("ISNS_", StringComparison.OrdinalIgnoreCase)) continue; // pines Kelvin del shunt
                if (other.Contains("GND", StringComparison.OrdinalIgnoreCase)) continue;
                yield return (other, comp.ComponentId);
            }
        }
    }

    /// <summary>True para fusibles, bobinas de potencia y resistencias de 0 Ω / shunt (según el valor leído del PDF).</summary>
    public static bool IsLowImpedanceBridge(ComponentNodeSummary comp)
    {
        if (comp.Category is "Fuse") return true;
        if (comp.Category is "Inductor/Coil") return comp.PinCount <= 2;
        if (comp.Category is "Resistor")
        {
            double? ohms = ParseOhms(comp.SchematicValue);
            return ohms.HasValue && ohms.Value <= BridgeMaxOhms;
        }
        return false;
    }

    /// <summary>Convierte "0.003 Ω", "68K Ω", "0 Ω (SHORT)", "4.7" a ohmios. Null si no se puede leer.</summary>
    public static double? ParseOhms(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (value.Contains("SHORT", StringComparison.OrdinalIgnoreCase)) return 0.0;

        var m = OhmsRegex.Match(value);
        if (!m.Success) return null;
        if (!double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double n)) return null;

        return m.Groups[2].Value.ToUpperInvariant() switch
        {
            "K" => n * 1e3,
            "M" => n * 1e6,
            "G" => n * 1e9,
            _ => n
        };
    }
}
