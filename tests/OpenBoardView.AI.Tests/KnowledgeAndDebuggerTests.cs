using OpenBoardView.AI.Agent;
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
        Assert.Equal(5, kb.Applicable[0].Flows.Count);
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

    [Fact]
    public void NaturalLanguagePrompt_SelectsPp5vS3DecisionTreeInsteadOfPpbusFallback()
    {
        const string prompt =
            "Tengo una MacBook Pro 820-3115 con PP5V_S3 en 0V. Guíame con un árbol de decisión paso a paso: " +
            "¿qué debo medir primero con el multímetro para determinar si es un corto a tierra en el riel o " +
            "una falta de habilitación de Q7221 por señales del PCH (PM_SLP_S4_L)?";

        var trace = Sample().BuildTrace(new MazeTraceRequestDto { QueryOrSymptom = prompt });

        Assert.Equal("PP5V_S3", trace.TargetNet);
        Assert.Contains("PP5V_S3", trace.Title);
        Assert.DoesNotContain("PPBUS_G3H", trace.Title);
        Assert.Equal(6, trace.Steps.Count);

        var stepIds = trace.Steps.Select(s => s.ComponentId).ToList();
        Assert.Equal(new[] { "L7260", "C6213", "U7200", "Q7221", "Q7911", "U1800" }, stepIds);

        // Paso 1 debe ser medición en modo resistencia (óhmetro) para bifurcar corto vs habilitación
        Assert.Equal(MeasurementModes.Resistance, trace.Steps[0].MeasurementMode);
        Assert.NotNull(trace.Steps[0].Branch);
        Assert.NotNull(trace.Steps[0].ExpectedReading);
    }

    [Fact]
    public void PpVcoreS0CpuPrompt_SelectsCuratedCpuVcoreFlowAndExtractsStructuredIntent()
    {
        const string prompt =
            "Tengo la placa 820-3115-B con PPVCORE_S0_CPU en 0V, la placa enciende ventilador pero no da video. " +
            "Guíame paso a paso por las bobinas L7510/L7520, ALL_SYS_PWRGD y el regulador U7400.";

        var dbg = Sample();
        var intent = DiagnosticIntentParser.Parse(prompt, _fx.Graph, dbg.Knowledge, dbg);

        Assert.Equal("CPU_VCORE_ABSENT", intent.SymptomId);
        Assert.Equal("PPVCORE_S0_CPU", intent.PrimaryNet);
        Assert.Equal(new[] { "ALL_SYS_PWRGD", "CPU_VR_EN", "PP5V_S0" }, intent.SecondaryNets);
        Assert.Equal(new[] { "L7510", "L7520", "U7400", "Q7950", "R7402" }, intent.PrimaryComponents);
        Assert.Equal("DECISION_TREE", intent.DiagnosticMode);

        var trace = dbg.BuildTrace(new MazeTraceRequestDto { QueryOrSymptom = prompt });

        Assert.Equal("PPVCORE_S0_CPU", trace.TargetNet);
        Assert.Contains("PPVCORE_S0_CPU", trace.Title);
        Assert.Equal(5, trace.Steps.Count);

        var stepIds = trace.Steps.Select(s => s.ComponentId).ToList();
        Assert.Equal(new[] { "L7510", "R7402", "U7400", "U7400", "L7510" }, stepIds);
        Assert.DoesNotContain("R7510", stepIds);
        Assert.Contains(trace.Steps, s => s.OutputNet == "ALL_SYS_PWRGD");
        Assert.Equal(MeasurementModes.Resistance, trace.Steps[0].MeasurementMode);
    }

    [Fact]
    public void R7020CurrentSenseStep_PointsToPins17And18OfU7000()
    {
        var trace = Sample().BuildTrace(new MazeTraceRequestDto { QueryOrSymptom = "no carga" });
        var r7020Step = trace.Steps.FirstOrDefault(s => s.ComponentId == "R7020");

        Assert.NotNull(r7020Step);
        Assert.Contains("Pines 17 y 18 de U7000", r7020Step!.PinToMeasure);
    }

    [Fact]
    public void DynamicFallbackOnVcore_PrioritizesInductorsAndAvoidsSenseResistorLoop()
    {
        var dbg = _fx.DebuggerFor("OtherBoard.brd");
        var trace = dbg.BuildTrace(new MazeTraceRequestDto { QueryOrSymptom = "PPVCORE_S0_CPU en 0V" });

        Assert.Equal("PPVCORE_S0_CPU", trace.TargetNet);
        var stepIds = trace.Steps.Select(s => s.ComponentId).ToList();
        Assert.Contains("L7510", stepIds);
        Assert.Contains("L7520", stepIds);
        // R7510 no debe repetirse en bucle por sus pines Kelvin ISNS
        Assert.True(stepIds.Count(id => id == "R7510") <= 1);
    }

    [Theory]
    [InlineData("Tengo una placa con PP3V3_S5 en 0V, ¿qué mido?", "PP3V3_S5")]
    [InlineData("¿Cómo reviso la señal PM_SLP_S4_L paso a paso?", "PM_SLP_S4_L")]
    [InlineData("Revisar habilitación en Q7221", "P5VS3_EN_L")]
    public void ExtractTargetNetFromQuery_ExtractsExactRailOrComponentFromNaturalLanguage(string rawQuery, string expectedNet)
    {
        var extracted = Sample().ExtractTargetNetFromQuery(rawQuery);
        Assert.Equal(expectedNet, extracted);
    }

    [Theory]
    [InlineData("los puertos usb no funcionan")]
    [InlineData("el audio no suena por los parlantes")]
    [InlineData("tengo un corto en la linea de 3.3v")]
    [InlineData("falla general desconocida, el equipo no hace nada")]
    public void NaturalLanguageQueries_NeverReturnZeroStepsOrThrow(string naturalQuery)
    {
        var trace = Sample().BuildTrace(new MazeTraceRequestDto { QueryOrSymptom = naturalQuery });
        Assert.NotNull(trace);
        Assert.NotEmpty(trace.Steps);
        Assert.False(string.IsNullOrWhiteSpace(trace.TargetNet));
        Assert.NotNull(trace.OverallDiagnosis);
    }

    [Fact]
    public void BoardMasterPowerRail_IsDynamicallyIdentified()
    {
        var masterRail = _fx.Graph.GetBoardMasterPowerRail();
        Assert.Equal("PPBUS_G3H", masterRail);
    }
}

public class SchematicPdfExtractionTests : IClassFixture<SampleBoardFixture>
{
    private readonly SampleBoardFixture _fx;
    public SchematicPdfExtractionTests(SampleBoardFixture fx) => _fx = fx;

    [Theory]
    [InlineData("SYS_ONEWIRE")]
    [InlineData("ADAPTER_SENSE")]
    [InlineData("BKLT_PLT_RST_L")]
    [InlineData("LCD_BKLT_EN")]
    [InlineData("LED_RETURN_1")]
    [InlineData("PPBUS_S0_LCDBKLT_FUSED")]
    public void NonPowerPrefixNetsFromBrd_AreIndexedInSchematicContext(string netName)
    {
        var idx = _fx.Schematic;
        Assert.True(idx.NetContext.ContainsKey(netName), $"La red '{netName}' existe en el .brd y en el PDF pero no fue indexada.");
    }

    [Theory]
    [InlineData("PPBUS_S0_LCDBKLT_FUSED", 12.6)]
    [InlineData("PPBUS_SW_LCDBKLT_PWR", 12.6)]
    [InlineData("PP3V42_G3H", 3.42)]
    [InlineData("PP5V_S0", 5.0)]
    [InlineData("PP3V3_S5", 3.3)]
    [InlineData("PPVCORE_S0_CPU_PH1", 1.25)]
    public void CadVoltageProperties_AreExtractedDirectlyFromSchematicPdf(string netName, double expectedVolts)
    {
        var idx = _fx.Schematic;
        Assert.True(idx.NetVoltages.TryGetValue(netName, out var info), $"No se extrajo VOLTAGE= para '{netName}' desde el PDF.");
        Assert.Equal(expectedVolts, info.Voltage, precision: 2);
    }

    [Fact]
    public void SwitchNodeAttribute_IsExtractedFromPdf()
    {
        var idx = _fx.Schematic;
        Assert.Contains("PPBUS_SW_LCDBKLT_PWR_SW", idx.SwitchNodeNets);
    }

    [Theory]
    [InlineData("R5400", "0.003 Ω")]
    [InlineData("R7020", "0.02 Ω")]
    [InlineData("F7040", "8AMP-24V")]
    [InlineData("F9700", "3AMP-32V-467")]
    [InlineData("C7513", "82UF 16V")]
    public void ComponentValues_AreExtractedBy2DVoronoiNeighborhood(string compId, string expectedValue)
    {
        var idx = _fx.Schematic;
        Assert.True(idx.ComponentValues.TryGetValue(compId, out var actual), $"No se extrajo valor para {compId}");
        Assert.Equal(expectedValue, actual);
    }

    [Theory]
    [InlineData("C7540", true)]
    [InlineData("C7530", true)]
    [InlineData("R7803", true)]
    [InlineData("F7040", false)]
    [InlineData("U7000", false)]
    [InlineData("C7513", false)]
    [InlineData("R5400", false)]
    public void NoStuffComponents_AreCorrectlyClassifiedFromSchematic(string compId, bool expectedNoStuff)
    {
        var idx = _fx.Schematic;
        Assert.Equal(expectedNoStuff, idx.NoStuffComponents.Contains(compId));
    }
}

public class CircuitSimulatorTests : IClassFixture<SampleBoardFixture>
{
    private readonly SampleBoardFixture _fx;
    public CircuitSimulatorTests(SampleBoardFixture fx) => _fx = fx;

    [Theory]
    [InlineData("PPBUS_S0_LCDBKLT_FUSED", 12.56)]
    [InlineData("PPBUS_SW_LCDBKLT_PWR", 12.56)]
    [InlineData("PPVOUT_SW_LCDBKLT", 28.5)]
    [InlineData("PP15V_T29", 15.0)]
    [InlineData("PP3V42_G3H", 3.42)]
    [InlineData("PP1V05_S0", 1.05)]
    public void TryInferNominalVoltage_HandlesPreBoostAndIntegerRailsAccurately(string netName, double expectedV)
    {
        var v = CircuitSimulator.TryInferNominalVoltage(netName);
        Assert.NotNull(v);
        Assert.Equal(expectedV, v!.Value, precision: 2);
    }

    [Theory]
    [InlineData("I2C_BKL_1_SDA")]
    [InlineData("LPC_AD<0>")]
    [InlineData("AUD_I2C_INT_L")]
    [InlineData("RANDOM_UNKNOWN_SIGNAL")]
    public void NonPowerSignal_ReturnsNullVoltageInsteadOfInventing3V3(string signalNet)
    {
        Assert.Null(CircuitSimulator.TryInferNominalVoltage(signalNet));

        var sim = _fx.SimulatorFor("OtherBoard.brd", ensurePdfIndexed: false);
        var (v, src) = sim.ResolveNominalVoltage(signalNet);
        Assert.Null(v);
        Assert.Equal(DataSources.Unknown, src);
    }

    [Fact]
    public void PpbusShortSimulation_RoutesThroughRealSeriesShuntR5400AndNotRandomResistors()
    {
        var sim = _fx.SimulatorFor(SampleBoardFixture.SampleBrdName, ensurePdfIndexed: true);
        var res = sim.RunSimulation(new SimulationRequestDto
        {
            NetName = "PPBUS_G3H",
            FaultedComponentId = "C7513"
        });

        var faultEdges = res.FlowEdges.Where(e => e.Status == "HIGH_CURRENT_FAULT").ToList();
        Assert.Equal(2, faultEdges.Count);
        Assert.Equal("F7040", faultEdges[0].FromComponentId);
        Assert.Equal("R5400", faultEdges[0].ToComponentId);
        Assert.Equal("R5400", faultEdges[1].FromComponentId);
        Assert.Equal("C7513", faultEdges[1].ToComponentId);

        // Las ramas paralelas no deben transportar corriente de falla
        var parallelEdges = res.FlowEdges.Where(e => e.Status == "NORMAL").ToList();
        Assert.All(parallelEdges, e => Assert.Equal(0.0, e.CurrentAmps));
    }

    [Fact]
    public void LiftingSeriesShuntR5400_IsolatesShortInC7513()
    {
        var sim = _fx.SimulatorFor(SampleBoardFixture.SampleBrdName, ensurePdfIndexed: true);
        var res = sim.RunSimulation(new SimulationRequestDto
        {
            NetName = "PPBUS_G3H",
            FaultedComponentId = "C7513",
            LiftedComponentIds = new List<string> { "R5400" }
        });

        Assert.True(res.IsShortIsolatedByLiftedPart);
        Assert.Equal("BRANCH_ISOLATION_TEST", res.VerificationState);
    }

    [Fact]
    public void DynamicGraphSequence_ExcludesPeripheralPullupsAndHighImpedanceResistors()
    {
        _ = _fx.Schematic; // Asegura valores 2D de resistores (ej. R3880 = 470K Ω) cargados desde el PDF
        var dbg = _fx.DebuggerFor("OtherBoard.brd");
        var trace = dbg.BuildTrace(new MazeTraceRequestDto
        {
            QueryOrSymptom = "Tengo un problema en PPBUS_G3H, guíame paso a paso"
        });

        var stepIds = trace.Steps.Select(s => s.ComponentId).ToList();
        Assert.Contains("F7040", stepIds);

        // No debe incluir resistencias divisoras de alta impedancia ni periféricos Thunderbolt/FireWire
        Assert.DoesNotContain("R3880", stepIds); // 470K Ω
        Assert.DoesNotContain("R4260", stepIds); // 300K Ω FireWire
        Assert.DoesNotContain("R4262", stepIds); // 10K Ω FireWire
    }

    [Fact]
    public void PassiveBridgeR0960_InheritsUpstreamPpbusVoltageInsteadOf17V8MaxRating()
    {
        var sim = _fx.SimulatorFor(SampleBoardFixture.SampleBrdName, ensurePdfIndexed: true);
        var (volts, source) = sim.ResolveNominalVoltage("PP15V_T29");

        Assert.NotNull(volts);
        // PP15V_T29 nace de PPBUS_G3H a través del resistor puente R0960 (0 Ω), por lo que en reposo
        // su tensión real de operación es 12.56V (y nunca el rating máximo CAD 17.8V del PDF).
        Assert.Equal(12.56, volts!.Value, precision: 2);
        Assert.Equal(DataSources.Bridged, source);
    }
}
