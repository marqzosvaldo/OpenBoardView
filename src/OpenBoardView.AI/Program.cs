using System.Text.Json;
using Microsoft.Extensions.AI;
using OpenBoardView.AI.Agent;
using OpenBoardView.AI.Graph;
using OpenBoardView.AI.Knowledge;
using OpenBoardView.AI.Models;
using OpenBoardView.AI.Parsers;
using OpenBoardView.AI.Server;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls("http://127.0.0.1:5080");

builder.Services.AddHttpClient();
builder.Services.AddSingleton<ObvWebSocketServer>();

// 1. Cargar y parsear topología BRD (Fase 1) + Esquemático PDF (Fase 2) + Grafo en Memoria (Fase 3)
builder.Services.AddSingleton<CircuitGraphBuilder>(sp =>
{
    var config = sp.GetRequiredService<IConfiguration>();
    var env = sp.GetRequiredService<IHostEnvironment>();

    // Resolución flexible de archivos BRD y PDF:
    // 1. Argumentos CLI (--brd / --pdf)
    // 2. Variables de entorno (BOARD_BRD_PATH / BOARD_PDF_PATH)
    // 3. appsettings.json
    // 4. Auto-detección en carpeta Boards/
    // 5. Fallback predeterminado a Plan/
    string? brdArg = null;
    string? pdfArg = null;
    for (int i = 0; i < args.Length - 1; i++)
    {
        if (args[i].Equals("--brd", StringComparison.OrdinalIgnoreCase)) brdArg = args[i + 1];
        if (args[i].Equals("--pdf", StringComparison.OrdinalIgnoreCase)) pdfArg = args[i + 1];
    }

    string boardsDir = Path.GetFullPath(Path.Combine(env.ContentRootPath, "../../Boards"));
    string? autoBrd = Directory.Exists(boardsDir) ? Directory.GetFiles(boardsDir, "*.brd").FirstOrDefault() : null;
    string? autoPdf = Directory.Exists(boardsDir) ? Directory.GetFiles(boardsDir, "*.pdf").FirstOrDefault() : null;

    string brdConfigPath = brdArg
        ?? Environment.GetEnvironmentVariable("BOARD_BRD_PATH")
        ?? config["BoardFiles:BrdPath"]
        ?? autoBrd
        ?? "../../Plan/Apple MacBook Pro 13 A1278 i5 2.5GHz 820-3115-B Boardview.brd";

    string pdfConfigPath = pdfArg
        ?? Environment.GetEnvironmentVariable("BOARD_PDF_PATH")
        ?? config["BoardFiles:PdfPath"]
        ?? autoPdf
        ?? "../../Plan/Apple MacBook Pro A1278 13 MLB,J30 i5 2.5GHZ  051-9058  820-3115-B (03-12-2012) Schematics.pdf";

    string brdFullPath = Path.IsPathRooted(brdConfigPath) ? brdConfigPath : Path.GetFullPath(Path.Combine(env.ContentRootPath, brdConfigPath));
    string pdfFullPath = Path.IsPathRooted(pdfConfigPath) ? pdfConfigPath : Path.GetFullPath(Path.Combine(env.ContentRootPath, pdfConfigPath));

    var brdParser = new BoardViewParser();
    Console.WriteLine($"[Fase 1] Parseando archivo BRD: {Path.GetFileName(brdFullPath)}...");
    BoardTopology topology = brdParser.ParseFile(brdFullPath);
    Console.WriteLine($"[Fase 1] OK -> Componentes: {topology.Components.Count} | Redes: {topology.Nets.Count} | Desencriptado: {topology.WasEncrypted}");

    SchematicIndex? schematicIndex = null;
    if (File.Exists(pdfFullPath))
    {
        Console.WriteLine($"[Fase 2] Indexando esquemático PDF: {Path.GetFileName(pdfFullPath)}...");
        var pdfReader = new SchematicPdfReader();
        schematicIndex = pdfReader.ReadAndIndexPdf(pdfFullPath, topology);
        Console.WriteLine($"[Fase 2] OK -> Páginas: {schematicIndex.TotalPages} | Componentes con contexto PDF: {schematicIndex.ComponentContext.Count}");
    }

    Console.WriteLine("[Fase 3] Construyendo grafo de circuito en memoria...");
    return new CircuitGraphBuilder(topology, schematicIndex);
});

// 2. Registrar Simulador, Maze Debugger, Contexto, Herramientas y Proveedor IChatClient con Microsoft.Extensions.AI (Fase 4-6)
builder.Services.AddSingleton<CircuitSimulator>();

// Base de conocimiento de flujos de diagnóstico (JSON). Solo se aplican los flujos de las familias que coinciden con la placa cargada.
builder.Services.AddSingleton<FlowKnowledgeBase>(sp =>
{
    var graph = sp.GetRequiredService<CircuitGraphBuilder>();
    var env = sp.GetRequiredService<IHostEnvironment>();

    var dirs = new[]
    {
        Path.Combine(AppContext.BaseDirectory, "Knowledge"),
        Path.GetFullPath(Path.Combine(env.ContentRootPath, "../../Boards/knowledge"))
    };
    var kb = FlowKnowledgeBase.LoadFromDirectories(dirs, graph.Topology.FileName);

    Console.WriteLine($"[Conocimiento] Familias cargadas: {kb.Loaded.Count} | Aplicables a '{graph.Topology.FileName}': {kb.Applicable.Count}");
    foreach (var err in kb.LoadErrors)
        Console.WriteLine($"[Conocimiento] ⚠ No se pudo cargar {err}");
    return kb;
});
builder.Services.AddSingleton<CircuitMazeDebugger>();
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

// Modo CLI para verificación rápida (`dotnet run -- --verify` o `dotnet run -- --ask "..."`)
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

app.MapPost("/api/board/debugger/trace", async (MazeTraceRequestDto request, CircuitMazeDebugger mazeDebugger, ObvWebSocketServer wsServer, CancellationToken ct) =>
{
    var trace = mazeDebugger.BuildTrace(request);
    var activeNode = trace.Steps.FirstOrDefault(s => s.StepNumber == trace.CurrentStepNumber) ?? trace.Steps.First();
    var wsPayload = new ObvUiCommandPayload(
        Action: "maze_debugger",
        Component: activeNode.ComponentId,
        Net: activeNode.OutputNet,
        Quadrant: activeNode.Quadrant,
        X: activeNode.X,
        Y: activeNode.Y,
        Message: trace.OverallDiagnosis,
        MazeTrace: trace
    );
    await wsServer.BroadcastCommandAsync(wsPayload, ct);
    return Results.Ok(trace);
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

app.Run();
