# 🛠 Guía Completa de Diagnóstico y Reparación de Placas Base con Boardview (.brd) y Esquemáticos (.pdf)

Esta guía explica en detalle cómo cualquier técnico o entusiasta de la microelectrónica puede utilizar un archivo **Boardview (`.brd`, `.bdv`, `.cad`, `.fz`)** y un **Diagrama Esquemático (`.pdf`)** en conjunto con **OpenBoardView AI (v4)** para diagnosticar y reparar placas base (MacBooks, laptops PC, tarjetas gráficas, etc.) de manera profesional, paso a paso.

---

## 1. La Dualidad Fundamental: ¿Por qué se necesitan ambos archivos?

En la reparación electrónica a nivel de componentes no se trabaja a ciegas. Se utilizan dos planos complementarios:

| Herramienta | Formato | Función | ¿Qué te dice? | ¿Qué NO te dice? |
| :--- | :--- | :--- | :--- | :--- |
| **Boardview** | `.brd`, `.bdv`, `.cad`, `.fz` | **Mapa Físico (2D)** | Dónde está soldado cada componente, cara TOP o BOTTOM, y a qué pista de cobre va cada pin. | No dice qué voltaje debe haber ni qué valor nominal tiene el componente (ohmios, microfaradios). |
| **Esquemático** | `.pdf` | **Mapa Lógico/Eléctrico** | Cómo se comunican los chips entre sí, voltajes esperados, señales de habilitación y valores de componentes. | No dice en qué parte física de la placa está ubicado el componente. |

> **La Clave de OpenBoardView AI:**  
> Tradicionalmente el técnico debe alternar manualmente entre un visor de PDF de 150 páginas y un programa de Boardview. OpenBoardView AI **indexa ambos archivos a la vez**, vinculando cada clic físico en la placa con su página correspondiente en el esquemático y ofreciendo guías de prueba paso a paso.

---

## 2. Preparación: Cómo conseguir los archivos de tu placa

1. **Identifica el número de placa (Motherboard Part Number):**
   * En MacBooks: impreso en el PCB con formato `820-XXXX` (ejemplos: `820-3115`, `820-00165`, `820-02020`, `820-02757`).
   * En PC (Dell, Lenovo, Asus, HP): formato Compal/Quanta/Wistron (ejemplos: `LA-D751P`, `DA0X83MB6H0`, `NM-A311`).
2. **Descarga el `.brd` y el `.pdf` correspondientes a ese número de modelo.**
   * *(Consulta la guía detallada: [COMO_CONSEGUIR_ARCHIVOS_BOARDVIEW_Y_ESQUEMATICOS.md](COMO_CONSEGUIR_ARCHIVOS_BOARDVIEW_Y_ESQUEMATICOS.md)).*

---

## 3. Carga de Archivos y Espacios de Trabajo (Workspaces v4)

Tienes **3 opciones muy sencillas** para cargar cualquier placa en el software:

### Opción A (La más recomendada - Espacios de Trabajo en la Web):
1. Abre tu navegador en **`http://127.0.0.1:5080`**.
2. Haz clic en el botón superior **`📁 Workspaces`** y selecciona **`+ Nuevo Espacio`**.
3. Arrastra tu archivo `.brd` y tu esquemático `.pdf` a la zona de subida:
   * El sistema clasificará los roles automáticamente:
     * 🟦 **Azul (`PHYSICAL_CAD`)**: Archivo de diseño físico.
     * 🟩 **Verde (`SCHEMATIC_PRIMARY`)**: Plano esquemático principal.
     * 🟨 **Ámbar (`SCHEMATIC_ALT` / `AUX_DOC`)**: Esquemáticos de revisión previa o manuales.
4. Presiona **Guardar y Activar**: la placa se indexará y cargará en pantalla en menos de un segundo gracias al motor de persistencia SQLite.

### Opción B (Auto-detección en carpeta `Boards/`):
Copia tus archivos dentro de la carpeta **[`Boards/`](../Boards/)** en la raíz del proyecto:
* `Boards/TuPlaca.brd`
* `Boards/TuPlaca_Esquematico.pdf`  
*El programa detectará y cargará automáticamente cualquier archivo `.brd` y `.pdf` presente en esa carpeta al arrancar.*

### Opción C (Por línea de comandos):
```bash
dotnet run --project src/OpenBoardView.AI/OpenBoardView.AI.csproj -- --brd "/ruta/placa.brd" --pdf "/ruta/esquematico.pdf"
```

---

## 4. Flujo de Trabajo Profesional del Técnico (Paso a Paso)

```
[ Inspección Visual ] ➔ [ Rastreo en Laberinto con Multímetro ] ➔ [ Detección de Corto ] ➔ [ Inyección DC / Cámara IR ] ➔ [ Aislamiento y Sustitución ]
```

### Paso 1: Localización del Síntoma
Cuando un equipo no enciende o no carga, la falla siempre se origina en una línea de energía principal ausente.
* **Líneas Maestras en Apple MacBook:**
  * `PPDCIN_USBC_AON` (20V en USB-PD / 5V antes de negociación): Entrada directa de los puertos USB-C.
  * `PPBUS_G3H` / `PPBUS_AON` (12.6V en modelos antiguos, 8.6V en Retina, 12.0V en Apple Silicon M1-M3): Alimenta casi todos los convertidores Buck del sistema.
  * `PP3V42_G3H` / `PP3V8_AON`: Rieles auxiliares Always-On para el SMC o PMU.
* **Líneas Maestras en Laptops PC:**
  * `+19V_VIN` / `B+`: Entrada del adaptador de corriente.
  * `+3VALW` / `+5VALW`: Líneas de energía en espera (*Always-On*).

---

### Paso 2: Modo Multímetro Guiado (Circuit Step Debugger / Laberinto)
En lugar de medir al azar, usa el panel **Circuit Step Debugger** ubicado en la esquina superior derecha del visor web:

1. **Selecciona el síntoma o preset en el menú:**
   * *Ejemplo 1:* `🔋 Síntoma: PPBUS_G3H Ausente / No Carga` (9 pasos).
   * *Ejemplo 2:* `🔌 Falla en Puertos USB-C / PPDCIN_USBC_AON` (Fusibles F5601, FF200, FF201 y controlador CD3217).
   * *Ejemplo 3:* `⚡ Síntoma: PP5V_S3 Ausente / Árbol de Decisión` (6 pasos con bifurcación lógica).
   * *Ejemplo 4:* `🧠 Síntoma: PPVCORE_S0_CPU Ausente / VCORE CPU` (5 pasos para bobinas de fase y enable).
2. **Optimización con Filtro de Desacoplo (Switch Paralelo ON/OFF):**
   * En la barra del depurador encontrarás el interruptor reactivo:
     `[ Filtro Paralelo: ON / OFF ]`
   * **Modo ON (Predeterminado):** Agrupa decenas de capacitores de desacoplo idénticos en paralelo conectados a GND en un solo paso representativo (`+N omitidos`), dejando visible únicamente la ruta principal de potencia.
   * **Modo OFF:** Despliega el 100% de los capacitores individuales para una inspección exhaustiva.
3. **Mide con el Multímetro:**
   * **Tensión DC:** Punta negra a GND (chasis) y punta roja en el pin a medir. Compara con la tensión esperada indicada en pantalla.
   * **Resistencia / Impedancia ($\Omega$):** En pasos de bifurcación, mide ohmios con la placa desenergizada:
     * **$< 5\ \Omega$ (Corto a tierra):** El depurador te dirige hacia los capacitores de filtrado y técnicas de inyección térmica.
     * **$> 1\ \text{k}\Omega$ (Línea íntegra):** El depurador te dirige a revisar los integrados de conmutación y señales de control del procesador/PCH.
4. **Navegación de Pasos:**
   * **¿Hay voltaje normal?** Presiona **`✅ Hay Voltaje (Sig ⏩)`**.
   * **¿El voltaje cayó a 0V?** Presiona **`🛑 0V Aquí (Falla)`**.
   * El sistema marcará un punto de interrupción (*breakpoint*) y emitirá la explicación diagnóstica de la causa raíz.
5. **Memoria Predictiva y Atajos (*Shortcut Chips*):**
   * Si la base de datos registra fallos frecuentes previos (ej. capacitor `C7513` con 82% de casos resueltos), aparecerá un botón de acceso directo para verificarlo de inmediato.

---

### Paso 3: Simulación de Fuente de Laboratorio DC e Inyección de Voltaje
Cuando detectas una línea en cortocircuito a tierra (resistencia menor a 1–2 ohmios en la escala de continuidad del multímetro), se utiliza la técnica de **inyección de tensión**:

> [!WARNING]  
> **Regla de Oro de la Inyección:**  
> **NUNCA** inyectes más voltaje del que la línea tolera nominalmente:
> - En una línea de 12.6V (`PPBUS_G3H`), inyecta entre **1.0V y 1.5V** con límite de corriente de **2.0A a 3.0A**.
> - En una línea de CPU o memoria RAM (0.8V – 1.2V), **NUNCA** inyectes más de **0.8V**, o destruirás el procesador irreversiblemente.

En el panel **⚡ Fuente de Inyección DC & Flujo**:
1. Escribe el nombre de la línea en corto (ej. `PPBUS_G3H`).
2. Ajusta el slider de voltaje inyectado (ej. `1.5V`) y corriente máxima (ej. `2.5A`).
3. Presiona **`🔥 Simular Corto + Flujo`**:
   * Observarás partículas de corriente fluyendo por las pistas de cobre hacia el componente defectuoso.
   * La pantalla virtual LCD mostrará el consumo en amperios y la temperatura estimada.
4. **Técnica de Aislamiento Virtual (Levantar Componentes):**
   * Para determinar si el corto se encuentra en la etapa de potencia o en la carga del sistema, haz clic sobre el fusible o bobina y pulsa **`🔧 Levantar Componente`**.
   * El simulador abrirá el circuito virtualmente sin aplicar calor real en la placa.

---

### Paso 4: Detección Térmica (Cámara IR Infrarroja / FLIR Glow)
El calor generado por un cortocircuito se rige por la ley de Joule:
$$P = I^2 \times R$$

1. Activa el botón **`🌡 Cámara Térmica IR`** en la barra superior.
2. El visor superpondrá un gradiente de radiación térmica tipo FLIR centrado en el componente en corto.
3. Esto le indica al técnico con exactitud milimétrica dónde aplicar alcohol isopropílico, flux o enfocar la cámara térmica física bajo el microscopio.

---

### Paso 5: Consultar al Agente IA y Conmutador de Modelos
En la barra inferior del panel lateral tienes el asistente IA con su **Chip de Estado Interactivo**:
* **Indicador en Tiempo Real:** 🟢 **Verde (Online)** / 🔴 **Rojo (Offline)**.
* **Selector de Modelos:** Permite conmutar al vuelo entre `gemini-3.5-flash` (recomendado), `gemini-flash-lite-latest`, etc.
* **Botón de Reintento (`🔄`):** Reconecta instantáneamente si hubo cortes de red.
* **Preguntas de Ejemplo:**
  * *"Los puertos USB no funcionan. ¿Qué componentes debo medir primero?"*
  * *"Tengo 20V en el cargador pero 0V en la salida del fusible F5601. ¿Qué significa?"*
  * *"PPBUS_G3H tiene 0.3 ohmios de resistencia a tierra. ¿En qué cuadrante busco el corto?"*
* El agente ejecutará herramientas en segundo plano (`StartCircuitMazeDebuggerAsync`, `HighlightInBoardViewAsync`), iluminando físicamente el componente y sincronizando la cámara de OpenBoardView.

---

### Paso 6: Cerrar el Diagnóstico y Registrar el Caso
Al sustituir el componente dañado y verificar que la placa enciende:
1. Presiona el botón **`Marcar como Caso Resuelto`** en el depurador.
2. Indica el componente culpable (ej. `C7513` o `F5601`) y el tipo de fallo (`SHORT_TO_GND`, `OPEN_CIRCUIT`, etc.).
3. El caso se guardará en la base de datos SQLite local, permitiendo que las futuras reparaciones de esta placa cuenten con atajos estadísticos predictivos.
