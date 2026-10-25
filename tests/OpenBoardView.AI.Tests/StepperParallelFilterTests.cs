using OpenBoardView.AI.Algorithms;
using OpenBoardView.AI.Graph;
using OpenBoardView.AI.Models;
using Xunit;

namespace OpenBoardView.AI.Tests;

public class StepperParallelFilterTests
{
    private class MockBrdTopologyProvider : IBrdTopologyProvider
    {
        private readonly HashSet<string> _gndComps;
        private readonly Dictionary<string, List<string>> _netCaps;

        public MockBrdTopologyProvider(IEnumerable<string> gndComps, Dictionary<string, List<string>>? netCaps = null)
        {
            _gndComps = new HashSet<string>(gndComps, StringComparer.OrdinalIgnoreCase);
            _netCaps = netCaps ?? new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        }

        public bool IsComponentPinConnectedToGnd(string componentId) => _gndComps.Contains(componentId);

        public List<string> GetParallelCapacitorIdsOnNet(string netName, string? excludeCompId = null)
        {
            if (!_netCaps.TryGetValue(netName, out var list)) return new List<string>();
            return list.Where(c => !c.Equals(excludeCompId, StringComparison.OrdinalIgnoreCase)).ToList();
        }

        public ComponentNodeSummary? GetComponentDetails(string componentId)
        {
            return new ComponentNodeSummary(
                ComponentId: componentId,
                Category: "Capacitor",
                Quadrant: "Q1",
                Side: "Top",
                X: 1000,
                Y: 1000,
                PinCount: 2,
                ConnectedToGround: _gndComps.Contains(componentId),
                ConnectedNets: new List<string> { "PPBUS_G3H", "GND" }
            );
        }
    }

    [Fact]
    public void ProcessNodes_FilterEnabled_ConsolidatesParallelDecouplingCapacitors()
    {
        // Arrange
        var provider = new MockBrdTopologyProvider(new[] { "CE720", "CE721", "CE722", "CE723", "CE725" });
        var filter = new StepperParallelFilterV3();

        var rawNodes = new List<StepperNodeDto>
        {
            new(1, "F7040", "1", "PPBUS_G3H", "VOLTAGE_DC", "12.60 V", "0.00 V", 50, "Fusible de entrada"),
            new(2, "CE720", "1", "PPBUS_G3H", "VOLTAGE_DC", "12.60 V", "0.00 V", 52, "Capacitor de filtrado 1"),
            new(3, "CE721", "1", "PPBUS_G3H", "VOLTAGE_DC", "12.60 V", "0.00 V", 52, "Capacitor de filtrado 2"),
            new(4, "CE722", "1", "PPBUS_G3H", "VOLTAGE_DC", "12.60 V", "0.00 V", 52, "Capacitor de filtrado 3"),
            new(5, "CE723", "1", "PPBUS_G3H", "VOLTAGE_DC", "12.60 V", "0.00 V", 52, "Capacitor de filtrado 4"),
            new(6, "CE725", "1", "PPBUS_G3H", "VOLTAGE_DC", "12.60 V", "0.00 V", 52, "Capacitor de filtrado 5"),
            new(7, "Q7030", "4", "CHGR_GATE", "VOLTAGE_DC", "5.00 V", "0.00 V", 55, "Gate de conmutación")
        };

        // Act
        var filtered = filter.ProcessNodes(rawNodes, provider, isFilterEnabled: true);

        // Assert
        Assert.Equal(3, filtered.Count); // F7040, CE720 consolidado, Q7030
        Assert.Equal("F7040", filtered[0].ComponentId);
        Assert.Equal(1, filtered[0].StepNumber);

        var capNode = filtered[1];
        Assert.Equal("CE720", capNode.ComponentId);
        Assert.Equal(2, capNode.StepNumber);
        Assert.Equal(4, capNode.OmittedParallelCount);
        Assert.Contains("+4 capacitores en paralelo omitidos", capNode.Notes);

        Assert.Equal("Q7030", filtered[2].ComponentId);
        Assert.Equal(3, filtered[2].StepNumber);
    }

    [Fact]
    public void ProcessNodes_FilterDisabled_ExpandsAllIndividualCapacitors()
    {
        // Arrange
        var provider = new MockBrdTopologyProvider(new[] { "CE720", "CE721", "CE722", "CE723", "CE725" });
        var filter = new StepperParallelFilterV3();

        var rawNodes = new List<StepperNodeDto>
        {
            new(1, "CE720", "1", "PPBUS_G3H", "VOLTAGE_DC", "12.60 V", "0.00 V", 52, "Cap 1"),
            new(2, "CE721", "1", "PPBUS_G3H", "VOLTAGE_DC", "12.60 V", "0.00 V", 52, "Cap 2"),
            new(3, "CE722", "1", "PPBUS_G3H", "VOLTAGE_DC", "12.60 V", "0.00 V", 52, "Cap 3")
        };

        // Act
        var expanded = filter.ProcessNodes(rawNodes, provider, isFilterEnabled: false);

        // Assert
        Assert.Equal(3, expanded.Count);
        Assert.Equal(1, expanded[0].StepNumber);
        Assert.Equal(2, expanded[1].StepNumber);
        Assert.Equal(3, expanded[2].StepNumber);
        Assert.Contains("Modo Expandido", expanded[0].Notes);
        Assert.Contains("Modo Expandido", expanded[1].Notes);
        Assert.Contains("Modo Expandido", expanded[2].Notes);
    }

    [Fact]
    public void ProcessNodes_SeriesCapacitors_AreNeverOmitted()
    {
        // Arrange: C1000 y C1001 son capacitores en serie (NO conectados a tierra)
        var provider = new MockBrdTopologyProvider(gndComps: Array.Empty<string>());
        var filter = new StepperParallelFilterV3();

        var rawNodes = new List<StepperNodeDto>
        {
            new(1, "C1000", "1", "PCIE_TX_P", "VOLTAGE_DC", "0.80 V", "0.00 V", 10, "AC Coupling TX+"),
            new(2, "C1001", "1", "PCIE_TX_P", "VOLTAGE_DC", "0.80 V", "0.00 V", 10, "AC Coupling TX-")
        };

        // Act
        var result = filter.ProcessNodes(rawNodes, provider, isFilterEnabled: true);

        // Assert: Al no tocar tierra, ninguno se consolida ni se omite
        Assert.Equal(2, result.Count);
        Assert.Equal("C1000", result[0].ComponentId);
        Assert.Equal("C1001", result[1].ComponentId);
        Assert.Equal(0, result[0].OmittedParallelCount);
    }

    [Fact]
    public void ProcessMazeSteps_DualGeneration_ProducesFilteredAndExpandedLists()
    {
        // Arrange
        var provider = new MockBrdTopologyProvider(
            gndComps: new[] { "C7040", "C7041", "C7042" },
            netCaps: new Dictionary<string, List<string>>
            {
                ["PPBUS_G3H"] = new() { "C7040", "C7041", "C7042", "C7043", "C7044" }
            }
        );
        var filter = new StepperParallelFilterV3();

        var rawSteps = new List<MazeStepDto>
        {
            new(1, "Fusible Principal", "F7040", "Fuse", "Q1", "Top", 100, 200, "PPDCIN_G3H", "PPBUS_G3H", 12.6, "Pin 1", "Fusible", "Abierto", "PASSED"),
            new(2, "Capacitor 1", "C7040", "Capacitor", "Q1", "Top", 110, 200, "PPBUS_G3H", "PPBUS_G3H", 12.6, "Pin 1", "Filtro", "Corto", "PENDING"),
            new(3, "Capacitor 2", "C7041", "Capacitor", "Q1", "Top", 120, 200, "PPBUS_G3H", "PPBUS_G3H", 12.6, "Pin 1", "Filtro", "Corto", "PENDING"),
            new(4, "Bobina Buck", "L7030", "Inductor/Coil", "Q1", "Top", 150, 200, "PPBUS_G3H", "PP3V3_S5", 3.3, "Pin 2", "Bobina", "Abierta", "PENDING")
        };

        // Act
        var (filtered, expanded) = filter.ProcessMazeSteps(rawSteps, provider);

        // Assert
        // En filtrado: C7040 y C7041 se consolidan en 1 nodo
        Assert.Equal(3, filtered.Count);
        Assert.Equal("F7040", filtered[0].ComponentId);
        Assert.Equal("C7040", filtered[1].ComponentId);
        Assert.True(filtered[1].OmittedParallelCount > 0);
        Assert.Contains("+", filtered[1].Notes);
        Assert.Equal("L7030", filtered[2].ComponentId);

        // En expandido: Todos los capacitores están presentes y numerados correlativamente
        Assert.True(expanded.Count >= 4);
        for (int i = 0; i < expanded.Count; i++)
        {
            Assert.Equal(i + 1, expanded[i].StepNumber);
        }
    }
}
