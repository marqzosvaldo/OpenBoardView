# 📑 Cómo Conseguir Archivos Boardview (.brd) y Esquemáticos (.pdf)

> [!NOTE]  
> Esta guía ha sido ampliada y consolidada con instrucciones detalladas, lista de comunidades mundiales y soporte para procesadores Apple Silicon (M1/M2/M3) y laptops PC en el documento principal:  
> 👉 **[COMO_CONSEGUIR_ARCHIVOS_BOARDVIEW_Y_ESQUEMATICOS.md](COMO_CONSEGUIR_ARCHIVOS_BOARDVIEW_Y_ESQUEMATICOS.md)**

---

## Resumen Rápido

Para conseguir los archivos **Boardview (`.brd`)** y los **Esquemáticos (`.pdf`)** de una placa madre, el procedimiento técnico general consta de los siguientes pasos:

### 1. Identificar el número de parte exacto de la tarjeta (PCB Part Number)
Antes de buscar, es indispensable ubicar el código impreso en la superficie del PCB:
* **MacBooks (Apple):** Tienen un código con formato **820-XXXX** impreso directamente en la placa (por ejemplo, `820-3115`, `820-00165`, `820-02020`, `820-02757`).
* **Laptops PC (Dell, Lenovo, HP, ASUS):** Utilizan nomenclaturas de fabricantes ODM como Compal (`LA-D751P`), Quanta (`DA0X83MB6H0`), Wistron o NM-A311.

---

### 2. Dónde buscar y descargar los archivos
* **Foros y comunidades especializadas de microelectrónica:** Sitios de soporte técnico como *RepairWiki*, *Badcaps Forum*, *VinaFix*, *Laptop-Schematics* o grupos de técnicos especializados.
* **Búsqueda web directa:** Consultando en motores de búsqueda el código de la placa junto a la extensión deseada (por ejemplo, `"820-02757 boardview .brd pdf schematic"`).
* **Archivos de prueba incluidos en el proyecto:** Dentro de las carpetas **Plan/** y **Data/Workspaces/** del repositorio se incluyen como casos de estudio listos para usar la MacBook Pro A1278 (**820-3115-B**) y la MacBook Pro 14" M3 (**820-02757**).

---

### 3. Cómo integrarlos en el simulador
1. **Arrastrar y soltar (Web):** Abre `http://127.0.0.1:5080`, ve a *Workspaces* y arrastra tus archivos `.brd` y `.pdf`.
2. **Carpeta automática:** Colócalos en la carpeta **Boards/** del software (`Boards/TuPlaca.brd` y `Boards/TuPlaca_Esquematico.pdf`). El sistema los detectará e indexará automáticamente al iniciar.
