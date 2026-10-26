# 📑 Guía Completa: Cómo Conseguir Archivos Boardview (.brd) y Esquemáticos (.pdf)

Para diagnosticar y reparar placas base con **OpenBoardView AI**, es indispensable contar con la pareja de archivos de la tarjeta que deseas intervenir: el **Boardview (`.brd`, `.bdv`, `.cad`, `.fz`)** y el **Diagrama Esquemático (`.pdf`)**.

Esta guía explica en detalle cómo identificar el modelo exacto de cualquier placa, dónde localizar los archivos y cómo integrarlos en el simulador.

---

## 1. Identificar el Número de Parte del PCB (Motherboard Part Number)

Nunca busques por el modelo comercial de la laptop (ej. *"MacBook Air M1"* o *"Dell Inspiron 15"*), ya que una misma laptop puede tener diferentes revisiones de placa madre según el año o procesador. Debes abrir el equipo y buscar el **código serigrafiado en la placa de circuito impreso (PCB)**:

### A. Apple MacBook / iMac / Mac mini
Apple imprime un código estándar con formato **`820-XXXX`** directamente sobre el PCB:
* **Ejemplos clásicos (Intel):**
  * `820-3115` (MacBook Pro 13" A1278 - Incluido como ejemplo en este repositorio).
  * `820-2915` (MacBook Pro 15" A1286).
  * `820-00165` (MacBook Air 13" A1466).
  * `820-00840` / `820-00875` (MacBook Pro 13" Retina).
* **Ejemplos modernos (Apple Silicon M1 / M2 / M3):**
  * `820-02020` (MacBook Pro 13" M1 A2338).
  * `820-02536` (MacBook Air 13.6" M2 A2681).
  * `820-02757` (MacBook Pro 14" M3 A2918 - Soportado en este repositorio).

### B. Laptops PC (Dell, Lenovo, HP, ASUS, Acer)
Los fabricantes de laptops PC encargan el diseño a fabricantes de diseño original (ODM) como **Compal, Quanta, Wistron o Pegatron**:
* **Compal:** Códigos que inician con `LA-` o `NM-` (ejemplos: `LA-D751P`, `LA-B102P`, `NM-A311`).
* **Quanta:** Códigos que inician con `DA0` o `DA` (ejemplos: `DA0X83MB6H0`, `DA0ZAVMB8G0`).
* **Wistron:** Nombres de proyecto internos (ejemplos: `15266-1`, `Husk 11309-1`).
* **ASUS:** Códigos como `X555LD Rev 2.0`, `FX505DT MB`.

### C. Tarjetas Gráficas (GPU)
* Busca la etiqueta blanca del código de barras en la cara trasera o serigrafías cerca del conector PCIe (ejemplos: `MSI MS-V389`, `ASUS D34045`).

---

## 2. Dónde Buscar y Descargar los Archivos

Existen comunidades mundiales de técnicos de microelectrónica y repositorios especializados donde se comparten estos archivos:

### A. Foros Técnicos Especializados (Gratuitos o con Registro)
1. **[RepairWiki](https://repair.wiki/):** Base de datos abierta con guías de síntomas y enlaces a documentación de servicio.
2. **[Badcaps Forums](https://www.badcaps.net/):** Uno de los foros de reparación más antiguos y respetados; sección *"Troubleshooting Laptops and Notebooks"* con miles de esquemáticos compartidos.
3. **[VinaFix](https://vinafix.com/):** Repositorio exhaustivo de BIOS, Boardviews y esquemáticos para laptops y PCs.
4. **[LabOneInside](https://laboneinside.com/):** Gran cantidad de esquemáticos en PDF y archivos `.brd` descargables directamente.
5. **[Laptop-Schematics](https://laptop-schematics.com/):** Catálogo indexado por fabricante y modelo de placa.

### B. Motores de Búsqueda (Google / DuckDuckGo)
Utiliza operadores de búsqueda técnica precisos:
```
"820-02757" "boardview" OR "brd" "schematic" filetype:pdf
"LA-D751P" boardview brd schematic
```

---

## 3. Placas de Prueba Incluidas en este Repositorio

Si deseas probar el software de inmediato sin necesidad de descargar archivos externos, este repositorio ya incluye archivos de prueba listos para usar:

1. **Apple MacBook Pro 14" M3 2023 (820-02757):**
   * Boardview: `820-02757-06 BoardView .brd` (5,298 componentes, 2,528 redes).
   * Esquemático: `X2482 (Apple MacBook Pro 14 A2918 M3 2023) 820-02757.pdf`.
   * Incluye presets de diagnóstico curados y auto-generados para fallas de USB-C (`PPDCIN_USBC_AON`), riel `PPBUS_AON` y riel `PP3V8_AON`.
2. **Apple MacBook Pro 13" A1278 (820-3115-B):**
   * Ubicado en la carpeta `Plan/` y `Boards/`.
   * Incluye descifrado nativo de archivos `.brd` propietarios de Apple.

---

## 4. Cómo Cargar tus Archivos en OpenBoardView AI

Tienes tres opciones sencillas:

### Opción 1: Arrastrar y Soltar en la Interfaz Web (Recomendado)
1. Abre tu navegador en `http://127.0.0.1:5080`.
2. En la barra superior, haz clic en **`📁 Workspaces`** $\rightarrow$ **`+ Nuevo Espacio`**.
3. Arrastra tu archivo `.brd` y tu archivo `.pdf` a la zona de carga.
4. El sistema los clasificará automáticamente (`PHYSICAL_CAD` y `SCHEMATIC_PRIMARY`), creará el espacio de trabajo y lo activará en pantalla en menos de un segundo.

### Opción 2: Carpeta Automática `Boards/`
Copia tus archivos dentro de la carpeta `Boards/` en la raíz del proyecto:
* `Boards/MiPlaca.brd`
* `Boards/MiPlaca_Esquematico.pdf`
Al iniciar el servidor, el software detectará la presencia de los archivos y los cargará automáticamente.

### Opción 3: Por Línea de Comandos al Arrancar
```bash
dotnet run --project src/OpenBoardView.AI/OpenBoardView.AI.csproj -- --brd "/ruta/a/mi_placa.brd" --pdf "/ruta/a/mi_esquematico.pdf"
```
