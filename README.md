# OpenBoardView — AI & Circuit Simulator Edition

> **Fork Notice & Credits to Original Authors:**  
> This project is a specialized fork of the renowned [OpenBoardView](https://github.com/OpenBoardView/OpenBoardView) project, created by the OpenBoardView contributors and distributed under the MIT License.  
> We express our sincere gratitude to the original creators and maintainers for providing the foundational C++ viewing engine, SDL2/ImGui rendering pipeline, and board format support.
> 
> **What this fork adds:**
> - 🤖 **AI Diagnostics Assistant** powered by **.NET 10** & **`Microsoft.Extensions.AI`** (supporting Gemini, Ollama, and OpenAI).
> - ⚡ **DC Bench Power Supply & Voltage Injection Simulator** with animated current flow particles and Ohm's law calculations.
> - 🌡 **Thermal IR Infrared Camera (FLIR glow)** simulation for hotspot localization.
> - 🧭 **Circuit Step Debugger (Interactive Maze Debugger)**: Trace power and signal lines node-by-node, measure expected voltages, set fault breakpoints, and diagnose why a laptop won't charge or turn on.
> - 📄 **PDF Schematic Indexer**: Cross-references `.brd` components with electronic schematic diagrams automatically.
> - 🔓 **Apple `.brd` Native Decryption**: Direct bit-shift decryption support for encrypted Apple boardview formats (e.g., MacBook 820-3115-B).
> - 🔌 **Bidirectional WebSocket Bridge**: Seamlessly synchronizes camera zoom, component selection, and AI suggestions between the native C++ ImGui app and the web visualizer.
>
> 📖 **Guía Práctica para Técnicos y Reparadores:**  
> ¿Quieres aprender cómo usar un archivo `.brd` junto a un esquemático `.pdf` para diagnosticar fallas paso a paso?  
> Consulta la **[Guía Completa de Diagnóstico y Reparación con Boardview y Esquemáticos](docs/GUIA_DE_DIAGNOSTICO_Y_REPARACION.md)**.

### 🎬 Demostración en Video / Live Video Demo
[![OpenBoardView AI Video Demo](https://img.shields.io/badge/Demo%20Video-Ver%20Grabaci%C3%B3n%20en%20Acci%C3%B3n-0284c7?style=for-the-badge&logo=apple)](asset/openboardview_ai_demo.mov)

> 📹 **[Descargar / Ver Video de Demostración (openboardview_ai_demo.mov)](asset/openboardview_ai_demo.mov)**: Video en tiempo real mostrando el simulador de inyección de voltaje DC, las partículas de corriente viajando hacia el componente en cortocircuito, la cámara térmica infrarroja (FLIR glow) y el recorrido guiado del Circuit Step Debugger (Laberinto).

---

## Open Board Viewer (Original Core Engine)

Linux/macOS/Windows SDL/ImGui edition software for viewing .brd files, intended as a drop-in
replacement for the "Test_Link" software and "Landrex".


[![Walkthrough of OpenBoardView R7.2](http://img.youtube.com/vi/6CrNRo1UP5g/0.jpg)](http://www.youtube.com/watch?v=6CrNRo1UP5g "OpenBoardView R7.2 demonstration, with voice-over")

[![Demo Video](https://github.com/OpenBoardView/OpenBoardView/blob/master/asset/screenshot.png)]()


![Common net pin halo](https://github.com/OpenBoardView/OpenBoardView/blob/master/asset/screenshot-halo.png)

![Part searching](https://github.com/OpenBoardView/OpenBoardView/blob/master/asset/screenshot-partsearch.png)


### Features

- Annotations (per board database file)
- Part and pin sizes better represented
- Better outlining of irregular objects (such as connectors)
- Drag and drop
- Recently used file history
- Non-orthagonally orientated caps/resistors/diodes now drawn more realistically
- Adjustable DPI (for working on 2K/4K screens)
- Works with multiple concurrent instances


### TODO

- Decode more board formats
- Compound project/file format


### Prerequisites

#### macOS
	$ brew install cmake sdl2

If you get the following error after running `build.sh`:

	urllib.error.URLError: <urlopen error [SSL: CERTIFICATE_VERIFY_FAILED] certificate verify failed: unable to get local issuer certificate (_ssl.c:997)>


Run the script at `/Applications/Python3.x/Install Certificates.command`.


#### Ubuntu

For Ubuntu developers, you'll need the following packages at a minimum;

	$ apt-get install git build-essential cmake libsdl2-dev libgtk-3-dev

#### Fedora

For Fedora, you will need the following packages at a minimum;

	$ dnf install @development-tools

	$ dnf install SDL2-devel gtk3-devel fontconfig-devel cmake

### Installation

1. Clone the project
```
$ git clone --recursive 'https://github.com/OpenBoardView/OpenBoardView'
```
2. Build it
```
$ ./build.sh
```
3. Sign it (skip if you're not on running macOS)
```
$ codesign --force --deep --sign - ./openboardview.app
```
4. Run it!
```
$ ./bin/openboardview
```
...or...
```
$ ./openboardview.sh
```
...or...
```
$ open ./openboardview.app
```

### Usage

- Ctrl-O: Open file select dialog

- w/a/s/d: pan viewport over board
- x: Reset zoom and center
- Mouse scroll, -/=: Zoom out/in
- Mouse click-hold-drag, Numeric pad up/down/left/right: pan viewport over board
- Numeric pad +/-: zoom board
- Numeric pad 5: Reset zoom and center
- Space, Middle mouse click: Flip board
- R/./Numpad-Del: Rotate clockwise
- ,/Numpad-Ins: Rotate counter-clockwise

- /, Ctrl-F: Search
- ESC: Clear search results and selected parts

- p: Toggle pin display
- m: Mirror board across Y-axis

- L: Show net list
- K: Show part list

---

## ⚡ AI Diagnostics Assistant & Electrical/Thermal Simulator (.NET 10)

This fork introduces a dedicated backend written in **C# / .NET 10** utilizing **`Microsoft.Extensions.AI`** to provide real-time electrical simulations, thermal analysis, and LLM-assisted board repair diagnostics.

### Features Overview

1. **AI Diagnostics Assistant (`Microsoft.Extensions.AI`):**
   - Integrates with Gemini (`gemini-3.1-flash-lite`, `gemini-2.5-flash`), local Ollama (`qwen2.5-coder`, `llama3`), or OpenAI-compatible endpoints.
   - Built-in function calling (`BoardDiagnosticsTools`) enabling the AI to query component properties, list high-density quadrants, trace upstream power rails, run short-circuit simulations, and step through circuit maze debugging.
2. **DC Bench Power Supply & Current Flow Simulation:**
   - Inject arbitrary voltages (0.5V – 12.6V) and current limits into any of the 2,361 nets.
   - Calculates real-time Ohm's law voltage drops, identifies shorted nets, and animates current flow particles across copper traces directly to short-circuit candidates (e.g. ceramic capacitors like `C7513`).
   - Virtual component lifting: Simulate desoldering fuses (`F7040`) or coils (`L7030`) to isolate whether a short is on the power generation side or system load side.
3. **Thermal Infrared Camera (IR / FLIR Glow) Simulation:**
   - Estimates Joule heating dissipation ($P = I^2 \times R$) and generates heatmaps with radiant glow centered on hotspots.
4. **Circuit Step Debugger (Interactive Maze Debugger):**
   - Traces power lines step-by-step from the charger connector (MagSafe `J6900`) through input filters, buck controllers (`ISL6259` / `U7000`), inductor switching loops, sensing resistors, and battery switches.
   - Allows hardware technicians to verify multimeter measurements node-by-node and mark fault breakpoints with automated root-cause diagnosis.
5. **PDF Schematics Integration (`PdfPig`):**
   - Indexes schematic diagrams (e.g., Apple 820-3115-B) page-by-page and links every board component to its schematic context.

### Quick Start (.NET 10 Backend & Web UI)

1. **Install Prerequisites:**
   - [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)

2. **📁 Dónde colocar tus archivos (.brd y .pdf):**
   Tienes **3 formas fáciles** de cargar la placa que quieras reparar:
   - **Método A (Recomendado - Auto-detección en carpeta `Boards/`):**  
     Simplemente suelta tus archivos dentro de la carpeta **[`Boards/`](Boards/)**:
     - `Boards/TuPlaca.brd`
     - `Boards/TuPlaca_Esquematico.pdf`  
     *(El software detectará y cargará automáticamente el `.brd` y `.pdf` que pongas en esa carpeta al iniciar).*
   - **Método B (Configuración en `appsettings.json`):**  
     Edita `src/OpenBoardView.AI/appsettings.json` con la ruta relativa o absoluta de tus archivos:
     ```json
     "BoardFiles": {
       "BrdPath": "../../Boards/TuPlaca.brd",
       "PdfPath": "../../Boards/TuPlaca_Esquematico.pdf"
     }
     ```
   - **Método C (Por comando al arrancar):**  
     ```bash
     dotnet run --project src/OpenBoardView.AI/OpenBoardView.AI.csproj -- --brd "/ruta/a/placa.brd" --pdf "/ruta/a/esquematico.pdf"
     ```
   *(Nota: Si no colocas ningún archivo, el sistema cargará automáticamente los archivos de prueba incluidos en `Plan/`: MacBook Pro 820-3115-B).*

3. **Configure AI Provider & API Key (Configuración de la API Key):**

   El sistema utiliza **`Microsoft.Extensions.AI`** y soporta **Google Gemini**, **Ollama local** (100% offline y gratis) u **OpenAI**.

   #### Opción A: Google Gemini (Recomendado - Gratuito y muy rápido)
   1. Obtén tu clave gratuita en: 👉 **[Google AI Studio](https://aistudio.google.com/app/apikey)** (inicia sesión con tu cuenta de Google y pulsa *"Create API Key"*).
   2. Configúrala en `src/OpenBoardView.AI/appsettings.json`:
      ```json
      "AiProvider": {
        "Provider": "Gemini",
        "Gemini": {
          "ApiKey": "AIzaSy...",
          "Model": "gemini-3.1-flash-lite"
        }
      }
      ```
   3. *O como variable de entorno en tu terminal (sin editar archivos):*
      ```bash
      export GEMINI_API_KEY="AIzaSy..."
      ```

   #### Opción B: Ollama Local (100% Gratis, Privado y Offline)
   Si prefieres no usar internet ni enviar datos a la nube:
   1. Instala [Ollama](https://ollama.ai) y descarga un modelo de código:
      ```bash
      ollama run qwen2.5-coder:7b
      ```
   2. En `src/OpenBoardView.AI/appsettings.json`, cambia el proveedor:
      ```json
      "AiProvider": {
        "Provider": "Ollama",
        "OllamaOrOpenAI": {
          "Endpoint": "http://localhost:11434/api/chat",
          "Model": "qwen2.5-coder:7b"
        }
      }
      ```

   #### Opción C: OpenAI o Endpoint Compatible
   Configura `"Provider": "OpenAI"`, tu endpoint y tu clave en `appsettings.json`.

3. **Run the AI Server:**
   ```bash
   dotnet run --project src/OpenBoardView.AI/OpenBoardView.AI.csproj
   ```

4. **Access the Visualizer:**
   Open your browser at **`http://127.0.0.1:5080`**.

---

### 📖 How to Use the Diagnostic Suite (Step-by-Step / Modo de Uso)

#### 1. Interactive Board & PDF Inspector (Inspección Física y Esquemática)
- **Pan & Zoom:** Click and drag the canvas to pan; use mouse wheel to zoom in and out.
- **Top / Bottom / Both:** Toggle `TOP`, `BOTTOM`, or `Ambas` buttons in the top toolbar to switch board sides.
- **Quadrants (`Q1`–`Q4`):** Toggle `Cuadrantes` to divide the board into spatial density quadrants.
- **Component Click:** Click any component on the board to view its pinout, connected nets, and the exact **Schematic PDF context** (including page numbers).
- **Search:** Type any component ID (e.g. `F7040`, `U7000`, `C7513`) or net name (e.g. `PPBUS_G3H`) in the top search box and click **Ir**.

#### 2. Circuit Step Debugger / Modo Laberinto (Multimeter Guided Tracing)
Located at the top-right of the canvas, this tool guides you step-by-step through power paths like a maze:
1. **Select a Symptom Preset:**
   - `Mi laptop NO carga / PPBUS_G3H` (9-step trace from MagSafe `J6900` to battery charger).
   - `Sin Luz Verde en MagSafe / 3V42` (6-step trace for One-Wire and SMC power).
   - `Pantalla Oscura / Sin Backlight` (6-step trace for LCD backlight boost circuit).
2. **Measure with your Multimeter:**
   - Place your multimeter's black probe on GND (chassis) and red probe on the indicated component pin.
   - Compare your measurement with the **Expected Voltage** (e.g. `16.50 V`, `3.42 V`, `12.56 V`).
3. **Step Navigation:**
   - **Voltage Present?** Click **`✅ Hay Voltaje (Sig ⏩)`** to advance to the next circuit node.
   - **Voltage Dropped to 0V?** Click **`🛑 0V Aquí (Falla)`**. The debugger marks a fault breakpoint and displays an instant diagnostic explanation (e.g. blown fuse, unpolarized MOSFET gate, or shorted capacitor).
   - **Auto-Play:** Click **`▶️ Play` / `⏸ Pause`** to run an automated walkthrough animation across all nodes.

#### 3. DC Bench Power Supply & Voltage Injection Simulator (Fuente de Inyección DC)
Located at the top-left of the canvas, this simulates real-world power supply injection and short detection:
1. **Choose a Net:** Select or type any net name (e.g., `PPBUS_G3H`).
2. **Adjust Controls:**
   - **Inyectar V:** Slider from `0.5V` to `12.6V` (always start low, e.g. `1.2V`–`1.5V`).
   - **Límite I:** Slider from `0.5A` to `5.0A`.
3. **Simulate Short & Current Flow:** Click **`🔥 Simular Corto + Flujo`**.
   - Yellow particles animate along copper traces showing current flowing directly into the shorted component.
   - The virtual LCD displays simulated line voltage, drawn current, and peak IR temperature.
4. **Virtual Component Lifting (Isolating Shorts):**
   - Click **`🔧 Levantar F7040`** (or click any component in the inspector and select *Levantar Componente*).
   - The simulator opens the circuit without heat or soldering, letting you see if the short is before or after the power switch.

#### 4. Thermal Infrared Camera / FLIR Glow (`🌡 Cámara Térmica IR`)
- Toggle **`🌡 Cámara Térmica IR`** in the top toolbar.
- Renders simulated thermal emission heatmaps ($P = I^2 \times R$) over hot components, helping you locate bad MLCC capacitors before applying freeze spray, flux, or touching components physically.

#### 5. AI Diagnostics Agent (`Microsoft.Extensions.AI`)
- Located in the right-side panel:
  - Click any **Quick Prompt** button (e.g., *Debugger: No Carga*, *Simular Corto PPBUS_G3H*) or type a custom question.
  - The AI assistant queries the board graph, indexes the PDF, calls internal diagnostic tools, and highlights relevant components directly on the board.

---

### Connecting with the Native C++ OpenBoardView App

The C++ desktop app includes an integrated client (`src/openboardview/AI/ObvAiBridge.cpp`) that automatically connects via WebSockets to `ws://127.0.0.1:5080/ws/obv`:
- Open the desktop app via `./build/openboardview.app` or `./bin/openboardview`.
- Enable the AI Diagnostics window under **`View -> AI Diagnostics Assistant (.NET 10)`**.
- Any component or net inspected by the AI or the web visualizer automatically centers, zooms, and highlights in the native C++ window.

