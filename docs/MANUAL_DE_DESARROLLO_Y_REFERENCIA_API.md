# 💻 Manual de Desarrollo y Referencia Completa de la API

Este manual técnico contiene la especificación exhaustiva de la API REST, Server-Sent Events (SSE), WebSockets, el modelo de datos (DTOs), las herramientas CLI y la suite de pruebas unitarias de **OpenBoardView AI (v4)** en **.NET 10**.

---

## 1. Referencia de Endpoints de la API

El servidor Kestrel de .NET 10 escucha por defecto en `http://127.0.0.1:5080`.

### 1.1. Módulo de Tablero Físico y Topología (`/api/board/*`)

#### `GET /api/board/overview`
Devuelve el resumen global de la placa activa en memoria: total de componentes, redes, cuadrantes espaciales y estado de desencriptación.
* **Respuesta 200 OK:**
  ```json
  {
    "boardName": "820-02757-06 BoardView .brd",
    "totalComponents": 5298,
    "totalNets": 2528,
    "boundingBox": { "minX": 0, "maxX": 14200, "minY": 0, "maxY": 12400 },
    "quadrants": {
      "Q1": { "components": 1420, "dominantSide": "Bottom" },
      "Q2": { "components": 1102, "dominantSide": "Top" },
      "Q3": { "components": 1390, "dominantSide": "Top" },
      "Q4": { "components": 1386, "dominantSide": "Bottom" }
    }
  }
  ```

#### `GET /api/board/geometry`
Devuelve las coordenadas completas de los componentes, contorno de la placa (`Outline`), pines y redes con sus voltajes nominales inferidos para renderizado directo en Canvas 2D o WebGL.

#### `GET /api/board/presets`
Lista todas las plantillas diagnósticas curadas y auto-generadas disponibles en SQLite para la placa activa.
* **Respuesta 200 OK:** Array de `DiagnosticPresetSummaryDto`.

#### `GET /api/board/net/{netName}/quadrants`
Analiza la distribución espacial de una red específica por cuadrantes e identifica capacitores shunt conectados a tierra (GND).

#### `GET /api/board/component/{componentId}`
Retorna los detalles completos de un componente: cuadrante, cara (`TOP`/`BOTTOM`), número de pines, redes conectadas, contexto semántico y páginas correspondientes en el esquemático PDF.

#### `POST /api/board/simulate`
Ejecuta una simulación eléctrica y térmica en memoria según la ley de Ohm ($V = I \times R$) y el efecto Joule ($P = I^2 \times R$).
* **Payload:**
  ```json
  {
    "netName": "PPBUS_G3H",
    "injectedVoltage": 1.5,
    "currentLimit": 2.5,
    "liftedComponentId": "F7040"
  }
  ```
* **Respuesta 200 OK:** `SimulationResultDto` con caída de tensión, corriente consumida, temperatura pico en grados Celsius y componente sospechoso en corto. Emite simultáneamente un evento WebSocket a la UI.

#### `POST /api/board/debugger/trace`
Calcula o actualiza el sendero de pasos (*Circuit Maze Trace*) para un síntoma o red, integrando datos estadísticos de fallas previas de SQLite y aplicando el filtro de desacoplo en paralelo (v3).
* **Payload:**
  ```json
  {
    "queryOrSymptom": "los puertos usb no funcionan",
    "currentStepNumber": 1,
    "markFailureAtStep": null,
    "isFilterEnabled": true
  }
  ```
* **Respuesta 200 OK:** `MazeTraceDto` con la secuencia de nodos a medir, lecturas esperadas, diagnósticos de falla y atajos predictivos.

---

### 1.2. Módulo de Espacios de Trabajo (`/api/workspaces/*`)

#### `GET /api/workspaces`
Lista todos los espacios de trabajo registrados en la base de datos local con su conteo de archivos, placa asociada y fecha de actualización.

#### `GET /api/workspaces/active`
Devuelve el espacio de trabajo que se encuentra cargado actualmente en memoria.

#### `POST /api/workspaces/active/{id}`
Conmuta dinámicamente el espacio de trabajo activo en tiempo de ejecución. Recarga el grafo y sincroniza a todos los clientes WebSocket.

#### `POST /api/workspaces/upload` (Multipart Form)
Crea un nuevo espacio de trabajo persistente y clasifica automáticamente los archivos adjuntos.
* **Parámetros Form:**
  * `name` (string, obligatorio): Nombre del proyecto (ej. *"MacBook A2918 M3 Taller"*).
  * `description` (string, opcional): Notas de recepción del cliente.
  * `files` (array de IFormFile): Archivos `.brd`, `.pdf`, etc.
  * `autoAnalyze` (bool, opcional): Si es `true`, dispara el análisis autónomo de esquemático en segundo plano.

#### `POST /api/workspaces/{id}/trigger-analysis`
Inicia el motor autónomo de análisis de esquemáticos (`AutomatedSchematicAnalyzer`) con `PdfPig` e IA en segundo plano.

#### `GET /api/workspaces/{id}/analysis-status`
Consulta el progreso del análisis autónomo (`NotRequested`, `Queued`, `Processing`, `Completed`, `Failed`).

---

### 1.3. Módulo de Historial de Reparaciones (`/api/repairs/*`)

#### `GET /api/repairs/stats?boardId={id}&symptomKey={key}`
Devuelve la lista clasificada de componentes con su probabilidad histórica de falla para priorizar el Stepper.
* **Respuesta 200 OK:**
  ```json
  [
    {
      "componentId": "C7513",
      "failureCount": 18,
      "probabilityPercentage": 81.8,
      "mostFrequentFaultType": "SHORT_TO_GND",
      "averageImpedance": 0.35
    }
  ]
  ```

#### `POST /api/repairs`
Registra un caso resuelto con éxito para enriquecer la memoria estadística del taller.
* **Payload:**
  ```json
  {
    "workspaceId": "41cb1b50-ba7f-4712-b85c-2c477197b91a",
    "boardId": "820-02757",
    "symptomKey": "USB_PORTS_DEAD",
    "faultyComponent": "F5601",
    "faultType": "OPEN_CIRCUIT",
    "measuredVoltage": 0.0,
    "measuredImpedance": null,
    "notes": "Fusible de entrada CD3217 abierto por sobrecorriente externa."
  }
  ```

---

### 1.4. Módulo de Sesiones Diagnósticas (`/api/sessions/*`)

#### `GET /api/sessions/active?workspaceId={wId}&boardId={bId}`
Recupera la sesión de diagnóstico en progreso para reanudar el trabajo en el paso exacto donde se interrumpió.

#### `POST /api/sessions`
Crea una nueva sesión diagnóstica guardando las mediciones intermedias en formato JSON.

#### `PUT /api/sessions/{id}/progress`
Actualiza el paso actual, estado (`IN_PROGRESS`, `RESOLVED`, `ABANDONED`) y array de mediciones registradas.

---

### 1.5. Módulo del Agente de IA y Capacidades (`/api/agent/*`)

#### `GET /api/agent/capabilities`
Inspecciona el estado de conectividad de los 3 niveles de inferencia (Ollama, Gemini, Determinista).
* **Respuesta 200 OK:**
  ```json
  {
    "activeTier": "Tier2_GeminiCloud",
    "provider": "Gemini",
    "model": "gemini-3.5-flash",
    "displayTitle": "gemini-3.5-flash",
    "badgeColor": "#22c55e",
    "isOnline": true,
    "isAvailable": true,
    "isLocal": false,
    "errorMessage": null,
    "availableModels": ["gemini-3.5-flash", "gemini-flash-lite-latest", "gemini-3.5-flash-lite", "gemini-flash-latest"],
    "description": "Conexión activa con Google Gemini (gemini-3.5-flash)."
  }
  ```

#### `POST /api/agent/capabilities/retry`
Fuerza un reintento de conexión inmediato descartando la caché previa de sondeo.

#### `POST /api/agent/capabilities/apikey`
Actualiza el API Key de Gemini en tiempo de ejecución: `{"apiKey": "AIzaSy..."}`.

#### `POST /api/agent/capabilities/model`
Establece el modelo preferido en tiempo de ejecución: `{"model": "gemini-3.5-flash"}`.

#### `POST /api/agent/query`
Procesa una consulta en lenguaje natural ejecutando llamadas a funciones en dos turnos (*Function Calling*).
* **Payload:**
  ```json
  {
    "prompt": "los puertos usb no funcionan",
    "history": []
  }
  ```

#### `POST /api/agent/stream` (Server-Sent Events)
Transmite la respuesta del agente token a token con formato `data: {"text": "..."}\n\n` finalizando con `data: [DONE]\n\n`.

---

## 2. Protocolo WebSocket (`/ws/obv`)

El endpoint `/ws/obv` maneja mensajes JSON bidireccionales con la estructura `ObvUiCommandPayload`:

```json
{
  "action": "highlight" | "maze_debugger" | "simulate_flow" | "circuit_switch",
  "component": "F5601",
  "net": "PPDCIN_USBC_AON",
  "quadrant": "Q1",
  "x": 396,
  "y": 11032,
  "message": "Paso 1 del Diagnóstico USB-C: Medir F5601 (20V)",
  "simulation": null,
  "mazeTrace": { ... }
}
```

* **Acción `highlight`:** Centra la cámara y enciende un halo de selección sobre el componente especificado.
* **Acción `maze_debugger`:** Actualiza el Stepper visual, posiciona la mira sobre el componente activo y dibuja las líneas de entrada y salida.
* **Acción `simulate_flow`:** Inicia la animación de partículas de corriente DC fluyendo por las pistas hacia el componente en cortocircuito.
* **Acción `circuit_switch`:** Notifica a todos los clientes que la placa activa ha cambiado, forzando la recarga de geometrías.

---

## 3. Parámetros de Línea de Comandos (CLI Flags)

El servidor puede ejecutarse con flags especiales para diagnósticos y verificación automática:

```bash
# 1. Ejecución estándar del servidor web
dotnet run --project src/OpenBoardView.AI/OpenBoardView.AI.csproj

# 2. Cargar una placa y esquemático específicos por ruta
dotnet run --project src/OpenBoardView.AI/OpenBoardView.AI.csproj -- --brd "/ruta/placa.brd" --pdf "/ruta/esquematico.pdf"

# 3. Verificación automática de la persistencia v4 (Crea espacio de prueba, verifica CRUD y limpia)
dotnet run --project src/OpenBoardView.AI/OpenBoardView.AI.csproj -- --test-persistence

# 4. Verificación de topología, cuadrantes y grafo de la placa activa
dotnet run --project src/OpenBoardView.AI/OpenBoardView.AI.csproj -- --verify

# 5. Consulta directa en terminal al agente LLM sin abrir el navegador
dotnet run --project src/OpenBoardView.AI/OpenBoardView.AI.csproj -- --ask "los puertos usb no funcionan"
```

---

## 4. Suite de Pruebas Unitarias (xUnit)

La solución cuenta con una suite integral de **80 pruebas unitarias automatizadas** ubicada en `tests/OpenBoardView.AI.Tests`:

### 4.1. Archivos de Prueba
* **`PersistenceTests.cs`**:
  * Validación del esquema SQLite y modo WAL.
  * Creación y borrado en cascada de `Workspaces` y `WorkspaceFiles`.
  * Cálculo de hashes SHA-256 e inicio en caché instantáneo (`< 0.1s`).
  * Persistencia de historial de reparaciones y cálculo estadístico de probabilidad de falla.
  * Guardado y recuperación de sesiones de diagnóstico pausadas.
* **`KnowledgeAndDebuggerTests.cs`**:
  * Parseo y validación de bases de conocimiento curadas (`apple-820-3115-b.json`, `apple-820-02757-m3.json`).
  * Construcción dinámica de rutas en el grafo topológico para rieles maestros (`PPBUS_G3H`, `PPDCIN_USBC_AON`).
  * Evaluación de árboles de decisión (ej. `PP5V_S3` con corto en `C6213` vs falta de enable en `Q7221`).
  * Marcado de fallas intermedias (*breakpoints*) y generación de diagnósticos automáticos.
* **`StepperParallelFilterTests.cs`**:
  * Agrupación y colapso de capacitores de desacoplo en paralelo conectados a GND.
  * Comportamiento del switch reactivo (Modo Filtrado vs Modo Expandido).
  * Preservación intacta de componentes críticos en serie (fusibles, bobinas, resistencias sensoras).

### 4.2. Ejecución de Pruebas
```bash
# Ejecutar todas las pruebas unitarias
dotnet test tests/OpenBoardView.AI.Tests/OpenBoardView.AI.Tests.csproj

# Ejecución detallada por consola
dotnet test tests/OpenBoardView.AI.Tests/OpenBoardView.AI.Tests.csproj --logger "console;verbosity=normal"
```

> [!NOTE]
> En entornos macOS con restricciones de sandbox, el ejecutable `vstest.console` requiere comunicación por sockets de red local (loopback) para comunicarse con el proceso secundario `testhost`. Si ejecutas en un entorno de sandbox estricto, asegúrate de habilitar permisos de red local para que `testhost` no sufra tiempos de espera de conexión.
