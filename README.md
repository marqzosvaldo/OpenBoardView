# OpenBoardView — AI & Circuit Simulator Edition (v4)

> **Aviso de Fork & Créditos a los Autores Originales:**  
> Este proyecto es un fork especializado y ampliado de [OpenBoardView](https://github.com/OpenBoardView/OpenBoardView), creado originalmente por los colaboradores de OpenBoardView bajo la licencia MIT.  
> Expresamos nuestro más sincero agradecimiento a los autores originales por proveer el motor base de renderizado en C++, el pipeline gráfico SDL2/ImGui y el soporte multi-formato de placas (`.brd`, `.bdv`, `.cad`, `.fz`).
> 
> **Novedades de esta Edición (v4 Persistencia, Espacios de Trabajo e Inteligencia Predictiva):**
> - 🤖 **Asistente IA de Diagnóstico en .NET 10 & `Microsoft.Extensions.AI`:** Soporta Google Gemini con cascada multi-modelo resiliente (`gemini-3.5-flash`), Ollama local offline (`qwen2.5-coder`) y OpenAI.
> - 💾 **Persistencia Híbrida Local-First (SQLite WAL):** Gestión de Espacios de Trabajo (*Workspaces*) multi-archivo con clasificación de roles (`PHYSICAL_CAD`, `SCHEMATIC_PRIMARY`, `SCHEMATIC_ALT`, `AUX_DOC`) y arranque instantáneo (**< 0.10s**) mediante caché de placas y componentes validada por hash SHA-256.
> - 🟢 **Chip Interactivo de Estado de IA en Chat:** Indicador visual en tiempo real (Verde Online / Rojo Offline), selector desplegable de modelos al vuelo, botón de reintentos con animación y popover para actualizar el API Key sin reiniciar el servidor.
> - ⚡ **Fuente de Inyección DC de Laboratorio & Simulador Térmico IR (FLIR):** Cálculo de leyes de Ohm y Joule, partículas de corriente animadas por las pistas de cobre y cámara infrarroja simulada para localizar componentes en cortocircuito.
> - 🧭 **Circuit Step Debugger con Filtro Topológico de Paralelo (v3):** Rastreo guiado con multímetro (tensión DC y ohmios en frío), árboles de decisión con bifurcación lógica y switch reactivo para omitir decenas de capacitores de desacoplo redundantes conectados a GND.
> - 📊 **Memoria Predictiva de Reparación:** Registro estadístico de casos resueltos en SQLite y cálculo dinámico de probabilidad de falla para sugerir atajos directos al técnico (*Shortcut Chips*).
> - 📄 **Analizador Autónomo de Esquemáticos (PdfPig + LLM):** Filtrado léxico de esquemáticos de más de 150 páginas reduciendo el consumo de tokens en un 95% (de 250k a ~8k tokens) y validación cruzada con el archivo CAD para eliminar alucinaciones.
> - 🔌 **Puente WebSocket Bidireccional:** Sincroniza en tiempo real la cámara, el zoom y la iluminación de componentes entre el visualizador Canvas Web y la aplicación nativa C++ de escritorio.

---

### 📚 Índice de Documentación Técnica

Toda la documentación detallada del proyecto se encuentra organizada en la carpeta **[`docs/`](docs/)**:

| Documento | Descripción |
| :--- | :--- |
| 🏛️ **[docs/ARQUITECTURA_SISTEMA_V4.md](docs/ARQUITECTURA_SISTEMA_V4.md)** | Arquitectura de software integral: Core C++, Backend .NET 10, esquema SQLite WAL, caché <0.1s y sincronización reactiva. |
| 🧠 **[docs/GUIA_PROVEEDORES_IA_Y_RESILIENCIA.md](docs/GUIA_PROVEEDORES_IA_Y_RESILIENCIA.md)** | Detección 3-Tier, cascada de modelos Gemini (15s timeout), preservación de `thoughtSignature`, Ollama local y chip de estado. |
| 💻 **[docs/MANUAL_DE_DESARROLLO_Y_REFERENCIA_API.md](docs/MANUAL_DE_DESARROLLO_Y_REFERENCIA_API.md)** | Referencia exhaustiva de endpoints REST, SSE, WebSockets, DTOs, CLI flags (`--test-persistence`) y 80 pruebas unitarias. |
| 🧭 **[docs/SISTEMA_PREDICTIVO_Y_STEPPER.md](docs/SISTEMA_PREDICTIVO_Y_STEPPER.md)** | Funcionamiento del Circuit Step Debugger, árboles de decisión, memoria de averías, filtro de desacoplo (v3) y análisis PDF. |
| 🛠 **[docs/GUIA_DE_DIAGNOSTICO_Y_REPARACION.md](docs/GUIA_DE_DIAGNOSTICO_Y_REPARACION.md)** | Guía práctica paso a paso para el técnico de reparación: inspección física, multímetro, inyección DC y cámara térmica IR. |
| 🔐 **[docs/CONFIGURACION_SEGURA_API_KEY.md](docs/CONFIGURACION_SEGURA_API_KEY.md)** | 4 métodos seguros para configurar tu API Key (Popover en chat, `appsettings.Local.json`, terminal, `~/.zshrc`) sin fugas a Git. |
| 📑 **[docs/COMO_CONSEGUIR_ARCHIVOS_BOARDVIEW_Y_ESQUEMATICOS.md](docs/COMO_CONSEGUIR_ARCHIVOS_BOARDVIEW_Y_ESQUEMATICOS.md)** | Dónde y cómo descargar los archivos `.brd` y `.pdf` para MacBooks (M1/M2/M3/Intel), laptops PC (Compal/Quanta) y GPUs. |

---

### 🎬 Demostración en Video / Live Video Demo
[![OpenBoardView AI Video Demo](https://img.shields.io/badge/Demo%20Video-Ver%20Grabaci%C3%B3n%20en%20Acci%C3%B3n-0284c7?style=for-the-badge&logo=apple)](asset/openboardview_ai_demo.mov)

> 📹 **[Descargar / Ver Video de Demostración (openboardview_ai_demo.mov)](asset/openboardview_ai_demo.mov)**: Grabación en tiempo real mostrando el simulador de inyección DC, las partículas de corriente viajando hacia el componente en corto, la cámara térmica infrarroja (FLIR glow) y el recorrido guiado del Circuit Step Debugger (Laberinto).

---

## ⚡ Inicio Rápido (Quick Start)

### 1. Requisitos Previos
* **[.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)** (`net10.0`) instalado en tu equipo.
* *(Opcional)* Navegador web moderno (Chrome, Edge, Safari, Firefox).
* *(Opcional)* Clave gratuita de [Google AI Studio](https://aistudio.google.com/app/apikey) o proceso local de [Ollama](https://ollama.ai).

### 2. Ejecutar el Servidor de IA y Visor Web
```bash
# 1. Clonar el repositorio
git clone --recursive https://github.com/OpenBoardView/OpenBoardView.git
cd OpenBoardView

# 2. Iniciar el servidor backend (.NET 10)
dotnet run --project src/OpenBoardView.AI/OpenBoardView.AI.csproj
```

### 3. Abrir en el Navegador
Abre tu navegador en:
👉 **`http://127.0.0.1:5080`**

El sistema cargará automáticamente el espacio de trabajo predeterminado (MacBook Pro 14" M3 A2918 o MacBook Pro A1278). Desde la interfaz podrás:
* Usar el menú **`📁 Workspaces`** para arrastrar y cargar tus propias tarjetas madre.
* Configurar o actualizar tu API Key al instante desde el **Chip de Estado de IA** en el chat.
* Alternar entre modelos (`gemini-3.5-flash`, `gemini-flash-lite-latest`, etc.).
* Probar la inyección DC y el depurador guiado por pasos.

---

## 🧪 Ejecución de Pruebas Unitarias

La solución cuenta con **80 pruebas unitarias automatizadas** que validan la persistencia SQLite, el clasificador de archivos, los grafos topológicos, los árboles de decisión y el filtro de capacitores en paralelo:

```bash
dotnet test tests/OpenBoardView.AI.Tests/OpenBoardView.AI.Tests.csproj
```

---

## 🛠 Compilación de la Aplicación de Escritorio C++ (Opcional)

Si deseas utilizar la aplicación clásica de escritorio con aceleración SDL2 e ImGui en lugar del navegador:

### macOS
```bash
brew install cmake sdl2
./build.sh
open ./openboardview.app
```

### Linux (Ubuntu / Debian)
```bash
sudo apt-get install git build-essential cmake libsdl2-dev libgtk-3-dev
./build.sh
./bin/openboardview
```

Al abrir la aplicación C++, accede a **`View -> AI Diagnostics Assistant (.NET 10)`** para conectarte automáticamente al servidor WebSocket en `ws://127.0.0.1:5080/ws/obv`.
