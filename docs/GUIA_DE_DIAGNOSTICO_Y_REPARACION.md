# 🛠 Guía Completa de Diagnóstico y Reparación de Placas Base con Boardview (.brd) y Esquemáticos (.pdf)

Esta guía explica en detalle cómo cualquier técnico o entusiasta de la microelectrónica puede utilizar un archivo **Boardview (`.brd`)** y un **Diagrama Esquemático (`.pdf`)** en conjunto con **OpenBoardView AI** para diagnosticar y reparar placas base (MacBooks, laptops PC, tarjetas gráficas, etc.) de manera profesional, paso a paso.

---

## 1. La Dualidad Fundamental: ¿Por qué se necesitan ambos archivos?

En la reparación electrónica a nivel de componentes no se trabaja a ciegas. Se utilizan dos planos complementarios:

| Herramienta | Formato | Función | ¿Qué te dice? | ¿Qué NO te dice? |
| :--- | :--- | :--- | :--- | :--- |
| **Boardview** | `.brd`, `.bdv`, `.cad` | **Mapa Físico (2D)** | Dónde está soldado cada componente, cara TOP o BOTTOM, y a qué pista de cobre va cada pin. | No dice qué voltaje debe haber ni qué valor tiene el componente (ohmios, microfaradios). |
| **Esquemático** | `.pdf` | **Mapa Lógico/Eléctrico** | Cómo se comunican los chips entre sí, voltajes esperados, señales de habilitación y valores de componentes. | No dice dónde está físicamente el componente en la placa real. |

> **La Clave de OpenBoardView AI:**  
> Tradicionalmente el técnico debe buscar en 80 páginas de PDF y luego cambiar de ventana al Boardview. OpenBoardView AI **indexa ambos archivos a la vez**, vinculando cada clic físico en la placa con su página correspondiente en el esquemático.

---

## 2. Preparación: Cómo conseguir los archivos de tu placa

1. **Identifica el número de placa (Motherboard Part Number):**
   - En MacBooks: impreso en el PCB con formato `820-XXXX` (ejemplo: `820-3115`, `820-00165`, `820-02020`).
   - En PC (Dell, Lenovo, Asus, HP): formato Compal/Quanta/Wistron (ejemplo: `LA-D751P`, `DA0X83MB6H0`, `NM-A311`).
2. **Descarga el `.brd` y el `.pdf` correspondientes a ese número de modelo.**
   - *(En la carpeta `Plan/` de este repositorio incluimos el `.brd` y el `.pdf` de la MacBook Pro A1278 820-3115-B como caso de estudio listo para usar).*

---

## 3. Demostración en Video y Configuración

### 🎬 Video Demostración
> 📹 Puedes ver la grabación de pantalla del software en funcionamiento aquí:  
> **[asset/openboardview_ai_demo.mov](../asset/openboardview_ai_demo.mov)**  
> Muestra en vivo la inyección de tensión DC, el flujo de partículas hacia el capacitor en corto, la cámara térmica FLIR y el modo laberinto.

---

### 📂 Dónde Colocar tus Archivos (.brd y .pdf)

Tienes **3 opciones muy sencillas** para cargar cualquier placa en el software:

1. **Opción A (La más recomendada - Auto-detección en `Boards/`):**  
   Simplemente copia tus archivos dentro de la carpeta **[`Boards/`](../Boards/)** en la raíz del proyecto:
   - `Boards/TuPlaca.brd`
   - `Boards/TuPlaca_Esquematico.pdf`  
   *El programa detectará y cargará automáticamente cualquier archivo `.brd` y `.pdf` presente en esa carpeta al arrancar.*

2. **Opción B (Configuración manual en `src/OpenBoardView.AI/appsettings.json`):**  
   Puedes especificar rutas relativas o absolutas a cualquier carpeta de tu equipo:
   ```json
   "BoardFiles": {
     "BrdPath": "C:/MisPlacas/820-00165.brd",
     "PdfPath": "C:/MisPlacas/820-00165_Schematics.pdf"
   }
   ```

3. **Opción C (Por línea de comandos):**  
   ```bash
   dotnet run --project src/OpenBoardView.AI/OpenBoardView.AI.csproj -- --brd "/ruta/placa.brd" --pdf "/ruta/esquematico.pdf"
   ```

*(Si no agregas ningún archivo, el sistema cargará automáticamente los archivos de prueba incluidos en `Plan/`: MacBook Pro A1278 820-3115-B).*

---

### 🔑 Cómo Obtener y Configurar tu API Key de IA

El motor de IA utiliza **`Microsoft.Extensions.AI`** y te permite elegir entre varias opciones:

#### Opción 1: Google Gemini (Recomendado - Gratuito y muy veloz)
1. **Consigue tu clave gratis:** Entra a **[Google AI Studio](https://aistudio.google.com/app/apikey)**, inicia sesión con tu cuenta de Google y pulsa en **"Create API Key"**. Copia el código que te da (inicia con `AIzaSy...`).
2. **Configúrala en `src/OpenBoardView.AI/appsettings.json`:**
   ```json
   {
     "BoardFiles": {
       "BrdPath": "../../Plan/Tu_Placa_Boardview.brd",
       "PdfPath": "../../Plan/Tu_Placa_Esquematico.pdf"
     },
     "AiProvider": {
       "Provider": "Gemini",
       "Gemini": {
         "ApiKey": "AIzaSy...",
         "Model": "gemini-3.1-flash-lite"
       }
     }
   }
   ```
3. **O como variable de entorno (sin tocar archivos):**
   ```bash
   export GEMINI_API_KEY="AIzaSy..."
   ```

#### Opción 2: Ollama Local (100% Gratis, Privado y Offline)
Si trabajas en un taller sin conexión a internet:
1. Instala [Ollama](https://ollama.ai) y corre:
   ```bash
   ollama run qwen2.5-coder:7b
   ```
2. En `appsettings.json`, cambia `"Provider": "Ollama"`.

#### Iniciar el Servidor:
```bash
dotnet run --project src/OpenBoardView.AI/OpenBoardView.AI.csproj
```
Y abre tu navegador en **`http://127.0.0.1:5080`**.

---

## 4. Flujo de Trabajo Profesional del Técnico (Paso a Paso)

```
[ Inspección Visual ] ➔ [ Rastreo en Laberinto con Multímetro ] ➔ [ Detección de Corto ] ➔ [ Inyección DC / Cámara IR ] ➔ [ Aislamiento y Sustitución ]
```

### Paso 1: Localización del Síntoma
Cuando un equipo no enciende o no carga, la falla siempre se origina en una línea de energía principal ausente.
* **Ejemplos en Apple MacBook:**
  - `PPBUS_G3H` (12.6V en modelos antiguos, 8.6V en modelos Retina): Alimenta casi todos los convertidores Buck del sistema.
  - `PP3V42_G3H` (3.42V): Alimenta el chip SMC y la luz del cargador MagSafe (One-Wire).
* **Ejemplos en Laptops PC:**
  - `+19V_VIN` / `B+`: Entrada del adaptador de corriente.
  - `+3VALW` / `+5VALW`: Líneas de energía en espera (*Always-On*).

---

### Paso 2: Modo Multímetro Guiado (Circuit Step Debugger / Laberinto)
En lugar de medir al azar, usa el panel **Circuit Step Debugger** ubicado en la esquina superior derecha del visor web:

1. **Selecciona el síntoma o árbol de decisión:**
   - *Ejemplo 1:* `🔋 Síntoma: PPBUS_G3H Ausente / No Carga` (9 pasos).
   - *Ejemplo 2:* `💡 Síntoma: PP3V42_G3H Ausente / Sin Luz MagSafe` (6 pasos).
   - *Ejemplo 3:* `🖥 Síntoma: PPVOUT_SW_LCDBKLT Ausente / Sin Backlight` (6 pasos).
   - *Ejemplo 4 (Bifurcación Lógica):* `⚡ Síntoma: PP5V_S3 Ausente / Árbol de Decisión` (6 pasos: Corto en riel `C6213` vs falta de habilitación `Q7221` / `PM_SLP_S4_L`).
   - *Ejemplo 5 (Alimentación CPU):* `🧠 Síntoma: PPVCORE_S0_CPU Ausente / VCORE CPU` (5 pasos: Impedancia en bobinas `L7510`/`L7520`, señal global `ALL_SYS_PWRGD` en `R7402`/`Q7950`, y habilitación `CPU_VR_EN` en `U7400`).
2. **Sigue las instrucciones en pantalla:**
   - El sistema dibuja un sendero con nodos numerados sobre la placa PCB.
   - **Medición de Tensión DC:** Te indica colocar la punta negra en tierra (GND) y la roja en el pin a medir, mostrando la tensión esperada (ej. `16.50 V`, `3.42 V`, `12.56 V`).
   - **Medición de Resistencia / Impedancia ($\Omega$):** En árboles de decisión (como `L7260` en `PP5V_S3`), te indica medir ohmios a tierra con la placa desenergizada para bifurcar el diagnóstico:
     - **$< 5\ \Omega$ (ej. $0.4\ \Omega$):** Corto físico en el riel $\rightarrow$ avanza a **Rama 1** (aislar componentes de filtrado como `C6213`).
     - **$> 1\ \text{k}\Omega$:** Riel íntegro sin corto $\rightarrow$ avanza a **Rama 2** (revisar tensiones auxiliares de `U7200`, compuerta de `Q7221` y estado `PM_SLP_S4_L` del PCH `U1800`).
3. **Evalúa con el multímetro:**
   - **¿Hay voltaje / lectura esperada?** Presiona el botón verde **`✅ Hay Voltaje (Sig ⏩)`**. La animación de corriente avanzará al siguiente nodo (fusible, diodo o chip).
   - **¿El voltaje se cayó a 0V?** Presiona el botón rojo **`🛑 0V Aquí (Falla)`**.
   - **Diagnóstico Automático:** El sistema marcará un punto de interrupción (*breakpoint*) y te explicará la causa raíz:
     > *"Si hay 0V en la salida pero sí había voltaje en la entrada, el componente X está abierto/quemado, o la compuerta del MOSFET no está polarizada, o la línea de salida tiene un capacitor en cortocircuito a tierra."*

---

### Paso 3: Simulación de Fuente de Laboratorio DC e Inyección de Voltaje
Cuando detectas una línea en cortocircuito a tierra (resistencia menor a 1–2 ohmios en la escala de continuidad del multímetro), se utiliza la técnica de **inyección de tensión**:

> ⚠️ **Regla de Oro de la Inyección:**  
> **NUNCA** inyectes más voltaje del que la línea tolera nominalmente:
> - En una línea de 12.6V (`PPBUS_G3H`), inyecta entre **1.0V y 1.5V** con límite de corriente de **2.0A a 3.0A**.
> - En una línea de CPU o PCH (0.9V – 1.05V), **NUNCA** inyectes más de **0.8V**, o destruirás el procesador irreversiblemente.

En el panel **⚡ Fuente de Inyección DC & Flujo**:
1. Escribe el nombre de la línea (autocompleta cualquiera de las 2,361 redes de la placa).
2. Ajusta el slider de voltaje inyectado (ej. `1.5V`) y corriente máxima (ej. `2.5A`).
3. Presiona **`🔥 Simular Corto + Flujo`**:
   - Verás partículas de corriente fluyendo a través de las pistas de cobre.
   - El capacitor o chip culpable absorberá la energía y se marcará en color rojo intenso.
4. **Técnica de Aislamiento Virtual (Levantar Componentes):**
   - Para saber si el corto está antes o después de la etapa de potencia, haz clic en el fusible o bobina (ej. `F7040`) y presiona **`🔧 Levantar F7040`**.
   - El simulador abrirá el circuito virtualmente: si el voltaje se recupera en la entrada, confirmas que el corto está en el lado del sistema (*System Load*).

---

### Paso 4: Detección Térmica (Cámara IR Infrarroja / FLIR Glow)
El calor generado por un cortocircuito se rige por la ley de Joule:
$$P = I^2 \times R$$

1. Activa el botón **`🌡 Cámara Térmica IR`** en la barra superior.
2. El visor superpondrá un gradiente de radiación térmica tipo FLIR centrado en el componente en falla.
3. Esto le indica al técnico exactamente dónde aplicar alcohol isopropílico, flux o apuntar la cámara térmica real en el microscopio sin perder tiempo tanteando con los dedos.

---

### Paso 5: Consultar al Agente IA de Diagnóstico
En la barra inferior del panel lateral tienes al asistente IA integrado:
* Puedes preguntarle casos reales como:
  - *"Tengo 16.5V en el cargador pero 0V en la compuerta de Q7080. ¿Qué condiciones necesita el ISL6259 para abrir el paso de corriente?"*
  - *"PPBUS_G3H mide 1.2V con resistencia de 0.8 ohmios a tierra. ¿En qué cuadrante debo buscar capacitores en corto?"*
* El agente ejecutará herramientas en segundo plano consultando el grafo BRD y el PDF esquemático, centrará la cámara automáticamente en el componente y te entregará el plan de prueba.

---

## 5. Resumen de Buenas Prácticas para Reparadores

1. **Inspección visual antes de encender:** Revisa con microscopio o lupa en busca de corrosión verde/blanca por humedad, capacitores agrietados o pines sulfatados en conectores.
2. **Medir resistencia antes de inyectar voltaje:** Siempre mide resistencia a tierra en las bobinas principales (`PPBUS`, `3V3`, `5V`, `VCORE`) antes de conectar el cargador.
3. **No sustituir fusibles por puentes de alambre:** Un fusible se quema para salvar el resto de la placa; si lo puenteas con cable, el siguiente en quemarse será el procesador o la placa misma.
4. **Documentar tus mediciones:** Usa el Circuit Step Debugger para anotar qué nodos tienen voltaje normal y en cuál se produce la caída.
