using System.Text.Json;
using Microsoft.Extensions.AI;
using OpenBoardView.AI.Agent;
using OpenBoardView.AI.Graph;
using OpenBoardView.AI.Knowledge;
using OpenBoardView.AI.Models;
using OpenBoardView.AI.Parsers;
using OpenBoardView.AI.Persistence;
using OpenBoardView.AI.Server;
using OpenBoardView.AI.Services;

var builder = WebApplication.CreateBuilder(args);
builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: true);
builder.WebHost.UseUrls("http://127.0.0.1:5080");

builder.Services.AddControllers();
builder.Services.AddHttpClient();
builder.Services.AddSingleton<ObvWebSocketServer>();

// Registro del Servicio de Persistencia Híbrida SQLite (v4)
builder.Services.AddSingleton<IPersistenceService>(sp =>
{
    var env = sp.GetRequiredService<IHostEnvironment>();
    var dbPath = Path.Combine(env.ContentRootPath, "Data", "openboardview_ai.db");
    var service = new SqlitePersistenceService(dbPath);
    service.InitializeAsync().GetAwaiter().GetResult();
    return service;
});

// Administrador de Espacios de Trabajo Multi-Archivo (v4)
builder.Services.AddSingleton<IWorkspaceService, WorkspaceService>();

// Servicio Universal de Importación de Presets y Plantillas de Diagnóstico (v4)
builder.Services.AddSingleton<IPresetImporterService, PresetImporterService>();

// Motor Autónomo de Análisis de Esquemáticos con Optimización de Tokens (v4)
builder.Services.AddSingleton<AutomatedSchematicAnalyzer>();

// Servicio de Sincronización en Segundo Plano hacia la Nube (v4)
builder.Services.AddHttpClient<BackgroundSyncWorker>();
builder.Services.AddHostedService<BackgroundSyncWorker>();

// 1. Gestor Dinámico de Circuitos, Grafo y Espacios de Trabajo (v4)
builder.Services.AddSingleton<ActiveCircuitManager>();
builder.Services.AddTransient<CircuitGraphBuilder>(sp => sp.GetRequiredService<ActiveCircuitManager>().CurrentGraph);
builder.Services.AddTransient<CircuitSimulator>(sp => sp.GetRequiredService<ActiveCircuitManager>().CurrentSimulator);
builder.Services.AddTransient<CircuitMazeDebugger>(sp => sp.GetRequiredService<ActiveCircuitManager>().CurrentMazeDebugger);
builder.Services.AddTransient<FlowKnowledgeBase>(sp => sp.GetRequiredService<ActiveCircuitManager>().CurrentKnowledge);

builder.Services.AddScoped<DiagnosticsAgentContext>();
builder.Services.AddScoped<BoardDiagnosticsTools>();
builder.Services.AddScoped<IChatClient>(sp =>
{
    var httpClientFactory = sp.GetRequiredService<IHttpClientFactory>();
    var config = sp.GetRequiredService<IConfiguration>();
    var provider = config["AiProvider:Provider"] ?? "Gemini";

    IChatClient baseClient;
    if (provider.Equals("Ollama", StringComparison.OrdinalIgnoreCase) ||
        provider.Equals("OpenAI", StringComparison.OrdinalIgnoreCase))
    {
        var httpClient = httpClientFactory.CreateClient("OllamaChatClient");
        var endpoint = config["AiProvider:OllamaOrOpenAI:Endpoint"] ?? "http://localhost:11434/api/chat";
        var model = config["AiProvider:OllamaOrOpenAI:Model"] ?? "qwen2.5-coder:7b";
        var apiKey = config["AiProvider:OllamaOrOpenAI:ApiKey"];
        baseClient = new OllamaChatClient(httpClient, endpoint, model, apiKey);
    }
    else
    {
        var httpClient = httpClientFactory.CreateClient("GeminiChatClient");
        var apiKey = config["AiProvider:Gemini:ApiKey"];
        if (string.IsNullOrWhiteSpace(apiKey) || apiKey.StartsWith("TU_API_KEY") || apiKey.StartsWith("YOUR_"))
        {
            apiKey = Environment.GetEnvironmentVariable("GEMINI_API_KEY") ?? apiKey ?? string.Empty;
        }
        var model = config["AiProvider:Gemini:Model"] ?? "gemini-3.1-flash-lite";
        baseClient = new GeminiChatClient(httpClient, apiKey, model);
    }

    return baseClient
        .AsBuilder()
        .UseFunctionInvocation()
        .Build();
});
builder.Services.AddScoped<BoardDiagnosticsService>();

var app = builder.Build();

// Modo CLI para verificación rápida (`dotnet run -- --verify`, `dotnet run -- --ask "..."` o `dotnet run -- --test-persistence`)
if (args.Contains("--test-persistence"))
{
    using var scope = app.Services.CreateScope();
    var persistence = scope.ServiceProvider.GetRequiredService<IPersistenceService>();
    var wsService = scope.ServiceProvider.GetRequiredService<IWorkspaceService>();

    Console.WriteLine("\n=======================================================");
    Console.WriteLine("    VERIFICACIÓN DE PERSISTENCIA Y WORKSPACES (v4)     ");
    Console.WriteLine("=======================================================");

    var wsDto = await wsService.CreateWorkspaceAsync("Test Verification WS", "Espacio de prueba", new List<FileUploadRequest>
    {
        new() { FileName = "820-3115-B.brd", ContentBytes = new byte[] { 1, 2, 3, 4 }, Length = 4 },
        new() { FileName = "Apple_Schematics.pdf", ContentBytes = new byte[] { 5, 6, 7, 8 }, Length = 4 }
    });

    Console.WriteLine($"1) Workspace creado con éxito: ID={wsDto.WorkspaceId}");
    var detail = await wsService.GetWorkspaceByIdAsync(wsDto.WorkspaceId);
    Console.WriteLine($"2) Archivos en Workspace ({detail?.Files.Count ?? 0} detectados):");
    foreach (var f in detail?.Files ?? new())
    {
        Console.WriteLine($"   - {f.FileName} [{f.FileType}] | Hash: {f.FileHash[..8]}...");
    }

    Console.WriteLine($"3) Registrando placa y reparación estadística...");
    await persistence.SaveBoardCacheAsync("820-3115-B", wsDto.WorkspaceId, "MacBook Pro A1278", "mock_brd_hash", "mock_pdf_hash", new List<ComponentCacheDto>());
    await persistence.RecordRepairAsync(new RepairRecordDto
    {
        WorkspaceId = wsDto.WorkspaceId,
        BoardId = "820-3115-B",
        SymptomKey = "PPBUS_G3H_MISSING",
        FaultyComponent = "C7513",
        FaultType = "SHORT_TO_GND",
        MeasuredImpedance = 0.4
    });

    var stats = await persistence.GetTopFailingComponentsAsync("820-3115-B", "PPBUS_G3H_MISSING");
    Console.WriteLine($"4) Estadísticas de falla para 'PPBUS_G3H_MISSING':");
    foreach (var s in stats)
    {
        Console.WriteLine($"   - Componente: {s.ComponentId} | Casos: {s.FailureCount} | Probabilidad: {s.ProbabilityPercentage:F1}%");
    }

    await wsService.DeleteWorkspaceAsync(wsDto.WorkspaceId);
    Console.WriteLine("\n✅ Todas las pruebas de persistencia v4 finalizaron con ÉXITO (datos de prueba limpiados).\n");
    return;
}

// Inicializar gestor dinámico de circuitos y cargar workspace activo o predeterminado
var circuitManager = app.Services.GetRequiredService<ActiveCircuitManager>();
await circuitManager.InitializeDefaultAsync(args);

// Auto-seed de plantillas curadas de diagnóstico (Presets) en SQLite
var presetImporter = app.Services.GetRequiredService<IPresetImporterService>();
string presetsDir = Path.Combine(app.Environment.ContentRootPath, "Presets");
if (Directory.Exists(presetsDir))
{
    foreach (var presetFile in Directory.GetFiles(presetsDir, "*.json"))
    {
        await presetImporter.ImportPresetFromJsonAsync(presetFile);
    }
}

if (args.Contains("--verify") || args.Contains("--ask"))
{
    using var scope = app.Services.CreateScope();
    var graph = scope.ServiceProvider.GetRequiredService<CircuitGraphBuilder>();
    var tools = scope.ServiceProvider.GetRequiredService<BoardDiagnosticsTools>();

    Console.WriteLine("\n=======================================================");
    Console.WriteLine("   VERIFICACIÓN DE FASES 1, 2, 3, 4 y 5 (820-3115-B)   ");
    Console.WriteLine("=======================================================");

    var overview = tools.GetBoardOverview();
    Console.WriteLine("\n1) Resumen de Topología y Cuadrantes (GetBoardOverview):");
    Console.WriteLine(JsonSerializer.Serialize(overview, new JsonSerializerOptions { WriteIndented = true }));

    var topNets = graph.Topology.Nets.OrderByDescending(n => n.Pins.Count).Take(5).Select(n => $"{n.Name} ({n.Pins.Count} pines)");
    Console.WriteLine($"\n2.a) Redes con más pines (Tierra/Poder): [{string.Join(", ", topNets)}]");

    var f7040 = tools.GetComponentDetails("F7040");
    Console.WriteLine($"\n2.b) Redes en F7040 (Fusible principal): [{string.Join(", ", f7040?.ConnectedNets ?? new())}]");

    var ppbusQuadrants = tools.FindShortCircuitCandidatesByQuadrant("PPBUS_G3H");
    Console.WriteLine("\n2) Análisis de Densidad por Cuadrantes en 'PPBUS_G3H':");
    foreach (var q in ppbusQuadrants)
    {
        Console.WriteLine($"   - Cuadrante {q.Quadrant}: {q.TotalComponents} comps totales | {q.ShuntCapacitorsToGnd.Count} capacitores a GND ({string.Join(", ", q.ShuntCapacitorsToGnd.Take(8))})");
    }

    var u7000 = tools.GetComponentDetails("U7000");
    Console.WriteLine($"\n3) Detalles de U7000 (Charger ISL6259):");
    Console.WriteLine($"   - Cuadrante: {u7000?.Quadrant} | Lado: {u7000?.Side} | Pines: {u7000?.PinCount} | Págs PDF: [{string.Join(", ", u7000?.SchematicPages ?? new())}]");
    Console.WriteLine($"   - Contexto PDF: {u7000?.SchematicContext}");

    var upstream = tools.TraceUpstreamPath("PPBUS_G3H", "F7040");
    Console.WriteLine($"\n4) Rastreo de Camino en Serie (TraceUpstreamPath desde PPBUS_G3H):");
    foreach (var hop in upstream.Take(6))
    {
        Console.WriteLine($"   [Paso {hop.Step}] {hop.FromNet} --({hop.ThroughComponentId} [{hop.ComponentCategory}, {hop.Quadrant}])--> {hop.ToNet}");
    }

    int askIdx = Array.IndexOf(args, "--ask");
    if (askIdx >= 0 && askIdx + 1 < args.Length)
    {
        string prompt = args[askIdx + 1];
        Console.WriteLine($"\n5) Consultando al Agente IA (Microsoft.Extensions.AI -> Gemini API Key):");
        Console.WriteLine($"   Prompt: \"{prompt}\"\n");

        var agentService = scope.ServiceProvider.GetRequiredService<BoardDiagnosticsService>();
        var response = await agentService.ProcessQueryAsync(new AgentQueryRequest(prompt));

        Console.WriteLine($"   Herramientas invocadas automáticamente: [{string.Join(", ", response.ExecutedTools)}]");
        Console.WriteLine($"   Comandos WebSocket emitidos a OBV: {response.UiCommands.Count}");
        foreach (var cmd in response.UiCommands)
        {
            Console.WriteLine($"     -> WS JSON: {JsonSerializer.Serialize(cmd)}");
        }
        Console.WriteLine("\n--- RESPUESTA DEL AGENTE ---");
        Console.WriteLine(response.ResponseText);
    }

    return;
}

app.UseWebSockets();

// Fase 5: Endpoint WebSocket para OpenBoardView (/ws/obv)
app.Map("/ws/obv", async context =>
{
    if (context.WebSockets.IsWebSocketRequest)
    {
        using var webSocket = await context.WebSockets.AcceptWebSocketAsync();
        var wsServer = context.RequestServices.GetRequiredService<ObvWebSocketServer>();
        await wsServer.HandleClientAsync(webSocket, context.RequestAborted);
    }
    else
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
    }
});

// Endpoints REST y Streaming para el Agente IA y el Visor Interactivo
app.UseDefaultFiles();
app.UseStaticFiles();

app.MapGet("/api/board/overview", (BoardDiagnosticsTools tools) => Results.Ok(tools.GetBoardOverview()));

app.MapGet("/api/board/geometry", (CircuitGraphBuilder graph) =>
{
    var t = graph.Topology;
    return Results.Ok(new
    {
        t.FileName,
        t.WasEncrypted,
        BoundingBox = new
        {
            t.BoundingBox.MinX,
            t.BoundingBox.MaxX,
            t.BoundingBox.MinY,
            t.BoundingBox.MaxY,
            t.BoundingBox.Width,
            t.BoundingBox.Height,
            t.BoundingBox.MidX,
            t.BoundingBox.MidY
        },
        Outline = t.OutlinePoints.Select(p => new { x = p.X, y = p.Y }),
        Nets = t.Nets
            .Where(n => !n.IsGround && !n.IsUnconnected)
            .OrderByDescending(n => n.Name.StartsWith("PP", StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(n => n.Pins.Count)
            .Select(n => new
            {
                name = n.Name,
                pinCount = n.Pins.Count,
                nominalV = CircuitSimulator.InferNominalVoltage(n.Name)
            }),
        Components = t.Components.Where(c => c.Pins.Count > 0).Select(c => new
        {
            id = c.Id,
            x = c.X,
            y = c.Y,
            side = c.Side.ToString(),
            category = c.Category,
            quadrant = c.Quadrant,
            minX = c.Pins.Min(p => p.X),
            maxX = c.Pins.Max(p => p.X),
            minY = c.Pins.Min(p => p.Y),
            maxY = c.Pins.Max(p => p.Y),
            schematicPages = c.SchematicPages,
            schematicContext = c.SchematicContext,
            pins = c.Pins.Select(p => new
            {
                num = p.Number,
                x = p.X,
                y = p.Y,
                net = p.NetName
            })
        })
    });
});

app.MapGet("/api/board/presets", async (ActiveCircuitManager circuitManager, IPresetImporterService importer) =>
{
    var board = circuitManager.ActiveBoardName ?? "820-02757";
    var presets = await importer.GetPresetsForBoardAsync(board);
    return Results.Ok(presets);
});

app.MapGet("/api/board/net/{netName}/quadrants", (string netName, BoardDiagnosticsTools tools) =>
    Results.Ok(tools.FindShortCircuitCandidatesByQuadrant(netName)));

app.MapGet("/api/board/component/{componentId}", (string componentId, BoardDiagnosticsTools tools) =>
    Results.Ok(tools.GetComponentDetails(componentId)));

app.MapPost("/api/board/simulate", async (SimulationRequestDto request, CircuitSimulator simulator, ObvWebSocketServer wsServer, CancellationToken ct) =>
{
    var sim = simulator.RunSimulation(request);
    var wsPayload = new ObvUiCommandPayload(
        Action: "simulate_flow",
        Component: sim.FaultedComponentId,
        Net: sim.RootNet,
        Message: sim.DiagnosticSummary,
        Simulation: sim
    );
    await wsServer.BroadcastCommandAsync(wsPayload, ct);
    return Results.Ok(sim);
});

app.MapPost("/api/board/debugger/trace", async (
    MazeTraceRequestDto request, 
    CircuitMazeDebugger mazeDebugger, 
    IPersistenceService persistence,
    CircuitGraphBuilder graph,
    ObvWebSocketServer wsServer, 
    CancellationToken ct) =>
{
    var boardId = Path.GetFileNameWithoutExtension(graph.Topology.FileName);
    var failureStats = await persistence.GetTopFailingComponentsAsync(boardId, request.QueryOrSymptom);
    var preset = await persistence.GetDiagnosticPresetAsync(boardId, request.QueryOrSymptom);
    var trace = mazeDebugger.BuildTrace(request, failureStats, preset);
    var activeNode = trace.Steps.FirstOrDefault(s => s.StepNumber == trace.CurrentStepNumber) ?? trace.Steps.First();
    var wsPayload = new ObvUiCommandPayload(
        Action: "maze_debugger",
        Component: activeNode.ComponentId,
        Net: !string.IsNullOrWhiteSpace(trace.TargetNet) ? trace.TargetNet : activeNode.OutputNet,
        Quadrant: activeNode.Quadrant,
        X: activeNode.X,
        Y: activeNode.Y,
        Message: trace.OverallDiagnosis,
        MazeTrace: trace
    );
    await wsServer.BroadcastCommandAsync(wsPayload, ct);
    return Results.Ok(trace);
});

app.MapPost("/api/agent/intent", (AgentQueryRequest request, CircuitGraphBuilder graph, FlowKnowledgeBase kb, CircuitMazeDebugger mazeDebugger) =>
{
    var intent = DiagnosticIntentParser.Parse(request.Prompt, graph, kb, mazeDebugger);
    return Results.Ok(intent);
});

app.MapPost("/api/agent/query", async (AgentQueryRequest request, BoardDiagnosticsService agent, CancellationToken ct) =>
{
    var res = await agent.ProcessQueryAsync(request, ct);
    return Results.Ok(res);
});

app.MapPost("/api/agent/stream", async (HttpContext httpContext, AgentQueryRequest request, BoardDiagnosticsService agent, CancellationToken ct) =>
{
    httpContext.Response.ContentType = "text/event-stream";
    await foreach (var update in agent.ProcessStreamingQueryAsync(request, ct))
    {
        if (!string.IsNullOrEmpty(update.Text))
        {
            var payload = JsonSerializer.Serialize(new { text = update.Text });
            await httpContext.Response.WriteAsync($"data: {payload}\n\n", ct);
            await httpContext.Response.Body.FlushAsync(ct);
        }
    }
    await httpContext.Response.WriteAsync("data: [DONE]\n\n", ct);
});

// Registrar controladores de Workspaces, Reparaciones y Sesiones (v4)
app.MapControllers();

app.Run();
