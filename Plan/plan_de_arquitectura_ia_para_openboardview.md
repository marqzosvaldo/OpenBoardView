# Plan de Arquitectura: Agente IA de Diagnóstico para OpenBoardView (.NET 10 + Microsoft.Extensions.AI)

## 🎯 Objetivo del Proyecto
Crear un agente de IA multi-proveedor (Google Gemini con API Key, OpenAI, Anthropic o modelos locales vía Ollama) capaz de diagnosticar fallas en placas madre cruzando la topología exacta (Netlist) de los archivos de OpenBoardView (`.brd`/`.fz`) con la metadata semántica de los esquemáticos en PDF. El agente aísla fallas por cuadrantes espaciales y se comunica bidireccionalmente en tiempo real con la interfaz de OpenBoardView.

## 🛠 Stack Tecnológico (Inspirado en `ClinicaEsencial`)
*   **Backend & Orquestación:** C# (**.NET 10** / `net10.0` ASP.NET Core Web API + Console CLI).
*   **Framework IA Estándar:** **`Microsoft.Extensions.AI` (v10.11.0)** usando la interfaz unificada `IChatClient` con middleware `.AsBuilder().UseFunctionInvocation().Build()`.
*   **Proveedores LLM Soportados:**
    *   **Cloud con API Key (Por defecto):** `GeminiChatClient` (Google Gemini `gemini-3.1-flash-lite` / `gemini-2.5-pro`) u OpenAI compatible.
    *   **Local (Opcional):** `OllamaChatClient` (Llama 3.3, Qwen 2.5 Coder, DeepSeek, Gemma).
*   **Herramientas (Function Calling):** `AIFunctionFactory.Create` con atributos `[Description]` en `BoardDiagnosticsTools.cs` y estado reactivo en `DiagnosticsAgentContext.cs`.
*   **Parser PDF:** `UglyToad.PdfPig` (para extracción semántica de esquemáticos en C#).
*   **Comunicación UI Bidireccional:** WebSockets (`ObvWebSocketServer.cs`) y endpoints HTTP/SSE Streaming.

---

## 🚀 Fases de Implementación

### Fase 1: Extractor de Topología (BoardView API)
**Objetivo:** Obtener la "carretera" (nodos y aristas) sin usar IA visual, leyendo directamente los archivos `.brd` (incluyendo soporte nativo para desencriptación de cabeceras `0x23 0xE2 0x63 0x28` usadas en placas Apple MacBook como `820-3115-B`).
*   **Módulo:** `/Parsers/BoardViewParser.cs`
*   **Estructuras de Datos:** 
    *   `Component` (Id, X, Y, Side, PartType, PinCount, Quadrant)
    *   `Net` (Name, List<Pin>)
    *   `Pin` (Number, Probe, X, Y, Side, NetName, ComponentId)
*   **Tareas:**
    1. Escribir un parser en C# que desencripte (si aplica) y lea el formato TestLink `.brd`, extrayendo `Parts`, `Pins`, `Nails` y `Format`.
    2. Calcular el bounding box (ancho y alto total de la placa) en base a las coordenadas min/max de los componentes y el outline (`Format`).
    3. Asignar cada componente a un "Cuadrante" (`Q1`, `Q2`, `Q3`, `Q4`) dividiendo el bounding box en una cuadrícula espacial 2x2.

### Fase 2: Extractor de Semántica (PDF RAG)
**Objetivo:** Extraer roles, valores y voltajes (ej. `R7040` = 10K, `U7000` = ISL6259 / Charger / PMIC) del texto del PDF para dar contexto eléctrico al agente.
*   **Módulo:** `/Parsers/SchematicPdfReader.cs`
*   **Tareas:**
    1. Integrar `UglyToad.PdfPig`.
    2. Extraer el texto página por página del esquemático (`820-3115-B Schematics.pdf`), agrupando bloques de texto cercanos.
    3. Construir un índice inverso en memoria donde la clave es el ID del componente (ej. `"U7000"`) o nombre de la red (ej. `"PPBUS_G3H"`) y el valor es el contexto semántico y páginas donde aparece en el PDF.

### Fase 3: Motor del Grafo y Dependencias
**Objetivo:** Unir la Fase 1 y Fase 2 en una estructura consultable ultrarrápida para la IA.
*   **Módulo:** `/Graph/CircuitGraphBuilder.cs`
*   **Tareas:**
    1. Construir un grafo bipartito en memoria (Listas de Adyacencia): Componentes ↔ Redes (Nets) + enriquecimiento semántico del PDF.
    2. Implementar consultas de diagnóstico electrónico:
        *   `GetComponentsOnNet(string netName)` -> Devuelve lista de componentes conectados a esa pista con su cuadrante, lado (Top/Bottom) y metadata del PDF.
        *   `GetComponentDetails(string componentId)` -> Devuelve todos sus pines, redes conectadas, ubicación física y contexto del esquemático.
        *   `GetUpstreamPath(string componentId, string netName)` -> Rastrea caminos a través de resistencias/inductores/fusibles en serie (componentes de 2 pines) para hallar de dónde proviene una línea de tensión o señal.
        *   `GetHighDensityQuadrants(string netName)` -> Identifica en qué cuadrante (`Q1`-`Q4`) y cara (`Top`/`Bottom`) se concentran más capacitores/componentes conectados a una línea en corto.

### Fase 4: Agente de Diagnóstico con `Microsoft.Extensions.AI`
**Objetivo:** Exponer las funciones del grafo y esquemático como `AITool` al `IChatClient` (Gemini API Key / OpenAI / Ollama).
*   **Módulos:** `/Agent/GeminiChatClient.cs`, `/Agent/BoardDiagnosticsTools.cs`, `/Agent/DiagnosticsAgentContext.cs`, `/Agent/BoardDiagnosticsService.cs`
*   **Tareas:**
    1. Configurar `IChatClient` en `.NET 10` con `.UseFunctionInvocation()`, soportando configuración por `appsettings.json` (API Key de Gemini incluida por defecto + soporte para cambiar proveedor o modelo).
    2. Decorar los métodos de `BoardDiagnosticsTools` con `[Description]` y registrarlos mediante `AIFunctionFactory.Create(...)`.
    3. Definir el *System Prompt* especializado en microelectrónica y reparación de placas lógicas (análisis de líneas de poder como `PPBUS_G3H`, `PP3V42_G3H`, aislamiento de cortos por cuadrantes e inyección de tensión térmica).

### Fase 5: UI Bidireccional (WebSocket a OpenBoardView + API REST/SSE)
**Objetivo:** Que el agente ilumine físicamente los componentes o redes en la pantalla del usuario y exponga endpoints de consulta/streaming.
*   **Módulo:** `/Server/ObvWebSocketServer.cs` y controladores en ASP.NET Core.
*   **Tareas:**
    1. Integrar un servidor WebSocket (`/ws/obv`) y endpoints REST/SSE (`/api/agent/query` y `/api/agent/stream`).
    2. Incluir la herramienta `HighlightInBoardView` en `BoardDiagnosticsTools` que envía payloads JSON en vivo: `{ "action": "highlight", "component": "U7000", "net": "PPBUS_G3H", "quadrant": "Q2" }`.

---

## 📋 Estructura del Proyecto (.NET 10)
Ubicación: `/src/OpenBoardView.AI`
*   `/Models`: `Component.cs`, `Pin.cs`, `Net.cs`, `BoundingBox.cs`, `AgentDtos.cs`
*   `/Parsers`: `BoardViewParser.cs`, `SchematicPdfReader.cs`
*   `/Graph`: `CircuitGraphBuilder.cs`
*   `/Agent`: `GeminiChatClient.cs`, `OllamaChatClient.cs`, `BoardDiagnosticsTools.cs`, `DiagnosticsAgentContext.cs`, `BoardDiagnosticsService.cs`
*   `/Server`: `ObvWebSocketServer.cs`
