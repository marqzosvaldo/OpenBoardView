using OpenBoardView.AI.Graph;
using OpenBoardView.AI.Knowledge;

namespace OpenBoardView.AI.Tests;

public class ParserTests : IClassFixture<SampleBoardFixture>
{
    private readonly SampleBoardFixture _fx;
    public ParserTests(SampleBoardFixture fx) => _fx = fx;

    [Fact]
    public void SampleBoard_IsDecryptedAndParsed()
    {
        Assert.True(_fx.Topology.WasEncrypted);
        Assert.Equal(3952, _fx.Topology.Components.Count);
        Assert.Equal(2361, _fx.Topology.Nets.Count);
    }

    [Theory]
    [InlineData("F7040")]
    [InlineData("U7000")]
    [InlineData("C7513")]
    public void KnownComponents_Exist(string id)
    {
        Assert.NotNull(_fx.Graph.GetComponentDetails(id));
    }

    [Fact]
    public void UnknownComponent_ReturnsNull()
    {
        Assert.Null(_fx.Graph.GetComponentDetails("ZZ99999"));
    }
}

public class KnowledgeTests : IClassFixture<SampleBoardFixture>
{
    private readonly SampleBoardFixture _fx;
    public KnowledgeTests(SampleBoardFixture fx) => _fx = fx;

    [Fact]
    public void CuratedFlows_AreApplicableToTheSampleBoard()
    {
        var kb = _fx.KnowledgeFor(SampleBoardFixture.SampleBrdName);
        Assert.Empty(kb.LoadErrors);
        Assert.Single(kb.Applicable);
        Assert.Equal(3, kb.Applicable[0].Flows.Count);
    }

    [Fact]
    public void CuratedFlows_AreNotApplicableToADifferentBoard()
    {
        var kb = _fx.KnowledgeFor("Dell Latitude LA-D751P.brd");
        Assert.NotEmpty(kb.Loaded);
        Assert.Empty(kb.Applicable);
        Assert.Null(kb.FindFlow("no carga"));
    }

    [Fact]
    public void CuratedFlows_OnlyReferenceComponentsAndNetsThatExistInTheBoard()
    {
        var issues = _fx.DebuggerFor(SampleBoardFixture.SampleBrdName).ValidateKnowledge();

        Assert.True(issues.Count == 0,
            "Los flujos curados citan elementos inexistentes en la placa:\n" +
            string.Join("\n", issues.Select(i => $"  [{i.FlowId} paso {i.StepNumber}] {i.Kind}: {i.Detail}")));
    }

    [Fact]
    public void CuratedSteps_AlwaysDeclareAVoltageSource()
    {
        var kb = _fx.KnowledgeFor(SampleBoardFixture.SampleBrdName);
        var steps = kb.Applicable.SelectMany(k => k.Flows).SelectMany(f => f.Steps);
        Assert.All(steps, s => Assert.Contains(s.VoltageSource, new[] { DataSources.Curated, DataSources.Inferred }));
    }

    [Fact]
    public void CuratedSteps_AreGroundedInTheSchematicPdf()
    {
        var pages = _fx.Schematic.PageTexts;
        bool InPdf(string token) => pages.Values.Any(t =>
            System.Text.RegularExpressions.Regex.IsMatch(t, $@"\b{System.Text.RegularExpressions.Regex.Escape(token)}\b",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase));

        var problems = new List<string>();
        foreach (var flow in _fx.KnowledgeFor(SampleBoardFixture.SampleBrdName).Applicable.SelectMany(k => k.Flows))
        {
            int n = 1;
            foreach (var s in flow.Steps)
            {
                if (!InPdf(s.Component)) problems.Add($"[{flow.Id} paso {n}] componente {s.Component} no aparece en el PDF");
                if (!InPdf(s.InputNet) && !InPdf(s.OutputNet))
                    problems.Add($"[{flow.Id} paso {n}] ni '{s.InputNet}' ni '{s.OutputNet}' aparecen en el PDF");
                n++;
            }
        }

        Assert.True(problems.Count == 0, "Pasos curados sin respaldo en el esquemático:\n  " + string.Join("\n  ", problems));
    }

    [Fact]
    public void MalformedKnowledgeFile_IsReportedNotThrown()
    {
        string dir = Path.Combine(Path.GetTempPath(), "obv-kb-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "broken.json"), "{ esto no es json");
            var kb = FlowKnowledgeBase.LoadFromDirectories(new[] { dir }, "any.brd");

            Assert.Empty(kb.Loaded);
            Assert.Single(kb.LoadErrors);
            Assert.Contains("broken.json", kb.LoadErrors[0]);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}

public class MazeDebuggerTests : IClassFixture<SampleBoardFixture>
{
    private readonly SampleBoardFixture _fx;
    public MazeDebuggerTests(SampleBoardFixture fx) => _fx = fx;

    private CircuitMazeDebugger Sample() => _fx.DebuggerFor(SampleBoardFixture.SampleBrdName);

    [Theory]
    [InlineData("mi laptop no carga", 9)]
    [InlineData("no tengo luz verde en el magsafe", 6)]
    [InlineData("pantalla oscura sin backlight", 6)]
    public void CuratedSymptoms_ProduceTheExpectedFlow(string symptom, int expectedSteps)
    {
        var trace = Sample().BuildTrace(new MazeTraceRequestDto { QueryOrSymptom = symptom });

        Assert.Equal(expectedSteps, trace.Steps.Count);
        Assert.All(trace.Steps, s =>
        {
            Assert.Equal(DataSources.Curated, s.DataSource);
            Assert.True(s.ComponentFound, $"{s.ComponentId} no existe en la placa");
        });
        Assert.Null(trace.Warnings);
    }

    [Fact]
    public void ChargingFlow_StartsAtMagSafeAndPassesThroughMainFuse()
    {
        var trace = Sample().BuildTrace(new MazeTraceRequestDto { QueryOrSymptom = "no carga" });
        var ids = trace.Steps.Select(s => s.ComponentId).ToList();

        Assert.Equal("J6900", ids.First());
        Assert.Contains("F7040", ids);
    }

    [Fact]
    public void UncuratedQuery_FallsBackToTopologyAndIsMarkedAsInferred()
    {
        var trace = Sample().BuildTrace(new MazeTraceRequestDto { QueryOrSymptom = "PP3V3_S5" });

        Assert.NotEmpty(trace.Steps);
        Assert.All(trace.Steps, s => Assert.Equal(DataSources.Inferred, s.DataSource));
        Assert.NotNull(trace.Warnings);
        Assert.Contains(trace.Warnings!, w => w.Contains("estimación", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void DifferentBoard_NeverShowsComponentsFromTheCuratedFlow()
    {
        // Misma topología cargada, pero simulando que el archivo pertenece a otra familia de placa.
        var dbg = _fx.DebuggerFor("Dell Latitude LA-D751P.brd");
        var trace = dbg.BuildTrace(new MazeTraceRequestDto { QueryOrSymptom = "no carga" });

        Assert.All(trace.Steps, s => Assert.Equal(DataSources.Inferred, s.DataSource));
        Assert.NotNull(trace.Warnings);
        Assert.Contains(trace.Warnings!, w => w.Contains("No hay un flujo curado"));
    }

    [Fact]
    public void NullQuery_DoesNotThrow()
    {
        var request = new MazeTraceRequestDto { QueryOrSymptom = null! };
        var ex = Record.Exception(() => Sample().BuildTrace(request));
        Assert.Null(ex);
    }

    [Fact]
    public void NoMatchingNetOrFlow_ReturnsExplicitMessageInsteadOfInventing()
    {
        var trace = Sample().BuildTrace(new MazeTraceRequestDto { QueryOrSymptom = "zzzz_no_existe_9999" });

        // Si no se encontró nada, no debe fabricar pasos con componentes inventados.
        Assert.All(trace.Steps, s => Assert.True(s.ComponentFound));
    }

    [Fact]
    public void BlockedStep_MarksPreviousAsPassedAndNextAsPending()
    {
        var trace = Sample().BuildTrace(new MazeTraceRequestDto { QueryOrSymptom = "no carga", BlockedAtStepNumber = 3 });

        Assert.Equal(3, trace.BlockedAtStepNumber);
        Assert.Equal("PASSED", trace.Steps[0].Status);
        Assert.Equal("PASSED", trace.Steps[1].Status);
        Assert.Equal("BLOCKED_FAULT", trace.Steps[2].Status);
        Assert.Equal("PENDING", trace.Steps[3].Status);
    }
}
