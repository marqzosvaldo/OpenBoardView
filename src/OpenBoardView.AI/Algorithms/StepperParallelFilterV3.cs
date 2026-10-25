using System.Text.RegularExpressions;
using OpenBoardView.AI.Graph;
using OpenBoardView.AI.Knowledge;
using OpenBoardView.AI.Models;

namespace OpenBoardView.AI.Algorithms;

/// <summary>
/// Algoritmo de optimización topológica (v3) encargado de consolidar mediciones redundantes
/// en capacitores de desacoplo conectados a tierra (GND) en rieles de alimentación.
/// Permite alternar en tiempo real entre el modo limpio/resumido (ON) y el modo expandido (OFF).
/// </summary>
public class StepperParallelFilterV3
{
    private static readonly Regex CapacitorRegex = new(@"^(C|CE|C_)\d+", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public List<StepperNodeDto> ProcessNodes(
        List<StepperNodeDto> rawNodes, 
        IBrdTopologyProvider brdProvider,
        bool isFilterEnabled)
    {
        if (rawNodes == null || rawNodes.Count == 0) return new List<StepperNodeDto>();

        // SI EL SWITCH ESTÁ EN OFF: Devolver la lista completa sin modificar
        if (!isFilterEnabled)
        {
            var expandedNodes = rawNodes.Select(n => n.Clone()).ToList();
            for (int i = 0; i < expandedNodes.Count; i++)
            {
                expandedNodes[i].StepNumber = i + 1;
                expandedNodes[i].Notes = $"{expandedNodes[i].SignalName} (Modo Expandido - Muestra completa)";
            }
            return expandedNodes;
        }

        // SI EL SWITCH ESTÁ EN ON: Aplicar filtrado inteligente con salvaguardas
        var filteredNodes = new List<StepperNodeDto>();
        HashSet<string> processedNets = new(StringComparer.OrdinalIgnoreCase);

        foreach (var node in rawNodes)
        {
            bool isCapacitor = CapacitorRegex.IsMatch(node.ComponentId);

            if (isCapacitor)
            {
                // Verificar que sea capacitor en paralelo (conectado a GND)
                bool isConnectedToGnd = brdProvider.IsComponentPinConnectedToGnd(node.ComponentId);

                if (isConnectedToGnd)
                {
                    if (processedNets.Contains(node.SignalName))
                    {
                        var existingNode = filteredNodes.FirstOrDefault(n => 
                            n.SignalName.Equals(node.SignalName, StringComparison.OrdinalIgnoreCase) &&
                            CapacitorRegex.IsMatch(n.ComponentId));
                        if (existingNode != null)
                        {
                            existingNode.OmittedParallelCount++;
                            existingNode.ParallelComponentIds ??= new List<string>();
                            if (!existingNode.ParallelComponentIds.Contains(node.ComponentId, StringComparer.OrdinalIgnoreCase))
                            {
                                existingNode.ParallelComponentIds.Add(node.ComponentId);
                            }
                            existingNode.Notes = $"{existingNode.SignalName} (+{existingNode.OmittedParallelCount} capacitores en paralelo omitidos)";
                        }
                        continue;
                    }
                    processedNets.Add(node.SignalName);
                }
            }

            filteredNodes.Add(node.Clone());
        }

        for (int i = 0; i < filteredNodes.Count; i++)
        {
            filteredNodes[i].StepNumber = i + 1;
        }

        return filteredNodes;
    }

    /// <summary>
    /// Procesa una lista de MazeStepDto del depurador interactivo, generando simultáneamente
    /// la versión filtrada (1 capacitor representativo) y la versión expandida (todos los capacitores individuales).
    /// </summary>
    public (List<MazeStepDto> Filtered, List<MazeStepDto> Expanded) ProcessMazeSteps(
        List<MazeStepDto> rawSteps,
        IBrdTopologyProvider brdProvider)
    {
        if (rawSteps == null || rawSteps.Count == 0)
        {
            return (new List<MazeStepDto>(), new List<MazeStepDto>());
        }

        var expandedList = new List<MazeStepDto>();
        var filteredList = new List<MazeStepDto>();
        var processedNets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 1. Construir lista expandida inicial
        for (int i = 0; i < rawSteps.Count; i++)
        {
            var s = rawSteps[i];
            bool isCap = CapacitorRegex.IsMatch(s.ComponentId) || s.Category.Equals("Capacitor", StringComparison.OrdinalIgnoreCase);
            bool toGnd = isCap && brdProvider.IsComponentPinConnectedToGnd(s.ComponentId);

            string? notes = toGnd
                ? $"{s.OutputNet} (Modo Expandido - Capacitor individual para prueba de corto)"
                : null;

            expandedList.Add(s with
            {
                StepNumber = i + 1,
                Notes = notes
            });
        }

        // Si alguna red tiene un capacitor en rawSteps, enriquecer la lista expandida con otros capacitores a GND
        var fullyExpandedList = new List<MazeStepDto>();
        var addedExpComps = new HashSet<string>(expandedList.Select(e => e.ComponentId), StringComparer.OrdinalIgnoreCase);
        
        foreach (var step in expandedList)
        {
            fullyExpandedList.Add(step);
            bool isCap = CapacitorRegex.IsMatch(step.ComponentId) || step.Category.Equals("Capacitor", StringComparison.OrdinalIgnoreCase);
            if (isCap && brdProvider.IsComponentPinConnectedToGnd(step.ComponentId))
            {
                var parallelOnBrd = brdProvider.GetParallelCapacitorIdsOnNet(step.OutputNet, step.ComponentId);
                foreach (var extraCapId in parallelOnBrd.Take(5))
                {
                    if (addedExpComps.Add(extraCapId))
                    {
                        var compDetails = brdProvider.GetComponentDetails(extraCapId);
                        fullyExpandedList.Add(new MazeStepDto(
                            StepNumber: fullyExpandedList.Count + 1,
                            StageTitle: $"Capacitor en Paralelo: {step.OutputNet} ({extraCapId})",
                            ComponentId: extraCapId,
                            Category: "Capacitor",
                            Quadrant: compDetails?.Quadrant ?? step.Quadrant,
                            Side: compDetails?.Side ?? step.Side,
                            X: compDetails?.X ?? step.X,
                            Y: compDetails?.Y ?? step.Y,
                            InputNet: step.OutputNet,
                            OutputNet: step.OutputNet,
                            ExpectedVoltage: step.ExpectedVoltage,
                            PinToMeasure: $"Pin 1 de {extraCapId}",
                            HowItWorks: $"Capacitor de desacoplo conectado en paralelo a GND en {step.OutputNet}.",
                            FailDiagnosis: $"Verificar cortocircuito directo a tierra en {extraCapId}.",
                            Status: "PENDING",
                            DataSource: DataSources.Inferred,
                            ComponentFound: compDetails != null,
                            MeasurementMode: step.MeasurementMode,
                            ExpectedReading: step.ExpectedReading,
                            Branch: step.Branch,
                            Notes: $"{step.OutputNet} (Modo Expandido - Capacitor en paralelo para prueba de corto)"
                        ));
                    }
                }
            }
        }

        for (int i = 0; i < fullyExpandedList.Count; i++)
        {
            fullyExpandedList[i] = fullyExpandedList[i] with { StepNumber = i + 1 };
        }

        // 2. Construir lista filtrada (ON): consolidar capacitores redundantes en la misma red
        foreach (var step in rawSteps)
        {
            bool isCap = CapacitorRegex.IsMatch(step.ComponentId) || step.Category.Equals("Capacitor", StringComparison.OrdinalIgnoreCase);

            if (isCap && brdProvider.IsComponentPinConnectedToGnd(step.ComponentId))
            {
                if (processedNets.Contains(step.OutputNet))
                {
                    var existingIdx = filteredList.FindIndex(s => 
                        s.OutputNet.Equals(step.OutputNet, StringComparison.OrdinalIgnoreCase) &&
                        (CapacitorRegex.IsMatch(s.ComponentId) || s.Category.Equals("Capacitor", StringComparison.OrdinalIgnoreCase)));
                    if (existingIdx >= 0)
                    {
                        var existing = filteredList[existingIdx];
                        int newCount = existing.OmittedParallelCount + 1;
                        var pList = existing.ParallelComponentIds != null ? new List<string>(existing.ParallelComponentIds) : new List<string>();
                        if (!pList.Contains(step.ComponentId, StringComparer.OrdinalIgnoreCase))
                        {
                            pList.Add(step.ComponentId);
                        }
                        filteredList[existingIdx] = existing with
                        {
                            OmittedParallelCount = newCount,
                            ParallelComponentIds = pList,
                            Notes = $"{existing.OutputNet} (+{newCount} capacitores en paralelo omitidos)"
                        };
                    }
                    continue;
                }
                
                processedNets.Add(step.OutputNet);

                // Descubrir capacitores adicionales en placa para enriquecer la etiqueta informativa
                var parallelIds = brdProvider.GetParallelCapacitorIdsOnNet(step.OutputNet, step.ComponentId);
                int brdCount = parallelIds.Count;

                filteredList.Add(step with
                {
                    OmittedParallelCount = brdCount,
                    ParallelComponentIds = parallelIds.Take(10).ToList(),
                    Notes = brdCount > 0 ? $"{step.OutputNet} (+{brdCount} capacitores en paralelo omitidos)" : null
                });
                continue;
            }

            filteredList.Add(step);
        }

        for (int i = 0; i < filteredList.Count; i++)
        {
            filteredList[i] = filteredList[i] with { StepNumber = i + 1 };
        }

        return (filteredList, fullyExpandedList);
    }
}
