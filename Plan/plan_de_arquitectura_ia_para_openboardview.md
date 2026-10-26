# Plan de Arquitectura: Agente IA de Diagnóstico para OpenBoardView (.NET 10 + Microsoft.Extensions.AI)

## 🎯 Objetivo del Proyecto
Crear un agente de IA multi-proveedor (Google Gemini con API Key, OpenAI o modelos locales vía Ollama) capaz de diagnosticar fallas en placas madre cruzando la topología exacta (Netlist) de los archivos de OpenBoardView (`.brd`/`.fz`/`.cad`) con la metadata semántica de los esquemáticos en PDF. El agente aísla fallas por cuadrantes espaciales, simula inyecciones de tensión DC, ofrece un depurador guiado por pasos (Circuit Step Debugger) y se comunica bidireccionalmente en tiempo real con la interfaz web y el cliente de escritorio C++ de OpenBoardView.

## 🛠 Stack Tecnológico Completo
*   **Backend & Orquestación:** C# (**.NET 10** / `net10.0` ASP.NET Core Web API + Console CLI).
*   **Framework IA Estándar:** **`Microsoft.Extensions.AI` (v10.11.0)** usando la interfaz unificada `IChatClient` con middleware `.AsBuilder().UseFunctionInvocation().Build()`.
*   **Proveedores LLM Soportados:**
    *   **Google Gemini (Cloud con API Key):** `GeminiChatClient` con cascada de modelos (`gemini-3.5-flash`, `gemini-flash-lite-latest`, etc.), timeout preventivo de 15s por intento, preservación de `thoughtSignature` y asignación estricta de `functionResponse.name`.
    *   **Ollama (Local Offline):** `OllamaChatClient` (`qwen2.5-coder:7b`, `llama3`).
    *   **OpenAI Compatible.**
    *   **Modo Autónomo Determinista:** Reglas de inferencia C# sobre el grafo .brd y presets SQLite ante falta de conexión.
*   **Persistencia Local-First:** SQLite relacional en modo WAL (`openboardview_ai.db`) con soporte opcional para extensiones vectoriales (`sqlite-vec`), caché de inicio instantáneo (**< 0.10s**) mediante validación de hash SHA-256 y gestión de espacios de trabajo (*Workspaces*) multi-archivo.
*   **Herramientas (Function Calling):** `AIFunctionFactory.Create` con atributos `[Description]` en `BoardDiagnosticsTools.cs` y estado reactivo en `DiagnosticsAgentContext.cs`.
*   **Parser PDF:** `UglyToad.PdfPig` (para extracción semántica de esquemáticos en C# y filtrado léxico de páginas de potencia).
*   **Comunicación UI Bidireccional:** Servidor WebSockets (`ObvWebSocketServer.cs`) en `/ws/obv` y endpoints HTTP/SSE Streaming.
*   **Frontend Interactivo:** SPA HTML5 / Canvas 2D con visualización de PCB, simulador de partículas de corriente DC, cámara térmica infrarroja simulada (FLIR), Stepper con filtro de componentes en paralelo (v3) y chip de estado interactivo de IA con selector de modelos y reintentos.

---

## 🚀 Fases de Implementación Completadas

### Fase 1: Extractor de Topología (BoardView API) ✅
* Lectura directa de archivos `.brd` TestLink y desencriptación bit-shift de cabeceras propietarias de Apple (`0x23 0xE2 0x63 0x28`).
* Cálculo del Bounding Box y particionado espacial en 4 cuadrantes (`Q1`, `Q2`, `Q3`, `Q4`).
* Estructuras de datos: `Component`, `Pin`, `Net`, `BoundingBox`.

### Fase 2: Extractor de Semántica (PDF RAG) ✅
* Extracción léxica y de coordenadas página a página con `PdfPig`.
* Construcción del índice invertido componente/red $\longleftrightarrow$ páginas y contexto esquemático.

### Fase 3: Motor del Grafo Bipartito y Algoritmos de Diagnóstico ✅
* Grafo de adyacencia Componentes $\longleftrightarrow$ Redes.
* Métodos: `GetComponentsOnNet`, `GetComponentDetails`, `GetUpstreamPath`, `FindShortCircuitCandidatesByQuadrant`.

### Fase 4: Agente de Diagnóstico con `Microsoft.Extensions.AI` ✅
* Adaptador `IChatClient` con `.UseFunctionInvocation()`.
* Exposición de herramientas diagnósticas con `BoardDiagnosticsTools`.

### Fase 5: Comunicación Bidireccional WebSocket y Streaming SSE ✅
* Servidor `/ws/obv` para sincronización de coordenadas, cámara y selección de componentes.
* Streaming Server-Sent Events (`/api/agent/stream`) para respuestas token por token.

### Fase 6: Persistencia Híbrida Local-First y Espacios de Trabajo (v4) ✅
* Esquema relacional SQLite con tablas `Workspaces`, `WorkspaceFiles`, `Boards`, `Components`, `RepairHistory`, `DiagnosticSessions`, `DiagnosticPresets`.
* Modal de subida multi-archivo con clasificación automática de roles (`FileRoleClassifier`).
* Reducción del tiempo de inicio de **60 segundos a < 0.10 segundos** mediante caché SHA-256.

### Fase 7: Conmutación Dinámica de Circuitos en Tiempo de Ejecución ✅
* `ActiveCircuitManager` permite cambiar de placa o espacio de trabajo al vuelo sin reiniciar el proceso del servidor.
* Emisión automática de eventos WebSocket para refrescar el canvas web y la ventana C++.

### Fase 8: Memoria Predictiva y Depurador Interactivo (Circuit Step Debugger) ✅
* Recorrido guiado de pasos con multímetro (tensión DC y ohmios en frío).
* Árboles de decisión con bifurcación lógica (ej. `PP5V_S3`, `PPVCORE_S0_CPU`).
* Cálculo estadístico de probabilidad de fallo y atajos predictivos en el Stepper.

### Fase 9: Filtro Topológico de Desacoplo en Paralelo (v3) ✅
* Detección algorítmica de capacitores de desacoplo conectados a GND (`StepperParallelFilterV3`).
* Switch reactivo en la UI (Modo Resumido vs Modo Expandido) para colapsar decenas de pasos redundantes en un único nodo representativo.

### Fase 10: Motor Autónomo de Análisis de Esquemáticos (v4) ✅
* Ingesta de esquemáticos complejos (150+ páginas).
* Filtrado local con `PdfPig` reduciendo tokens de 250k a ~8k (ahorro del 95%).
* Validación cruzada C# contra el CAD para eliminar alucinaciones y generación de presets en SQLite.

### Fase 11: Sondeo Adaptativo de Hardware y Resiliencia Multi-Modelo ✅
* `InferenceCapabilityProbe`: sondeo adaptativo en 3 niveles (Ollama $\rightarrow$ Gemini $\rightarrow$ Determinista).
* Cascada de modelos en `GeminiChatClient` con timeout preventivo de 15s por intento.
* Cumplimiento estricto del protocolo de Gemini: preservación de `thoughtSignature`, mapeo exacto de `functionResponse.name` y sanitización de esquemas JSON.

### Fase 12: Chip Interactivo de Estado y Conmutador de Modelos en Chat ✅
* Indicador visual en tiempo real (Verde Online / Rojo Offline).
* Menú desplegable interactivo `<select id="aiModelSelect">` para alternar entre modelos detectados.
* Botón interactivo de reintento (`🔄`) y popover para actualizar el API Key al vuelo.

### Fase 13: Sincronización en Segundo Plano hacia la Nube ✅
* `BackgroundSyncWorker` (`IHostedService`) con control de estado delta (`SyncStatus = 0, 1, 2`) para sincronización asíncrona no bloqueante.

### Fase 14: Suite Integral de Pruebas Unitarias (80 Tests) ✅
* 80 pruebas automatizadas con xUnit cubriendo persistencia SQLite, grafos topológicos, bases de conocimiento, árboles de decisión y filtro de desacoplo.
