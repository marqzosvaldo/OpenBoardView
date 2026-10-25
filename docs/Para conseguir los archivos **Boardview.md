Para conseguir los archivos **Boardview (** **.brd** **)** y los **Esquemáticos (** **.pdf** **)** de una placa madre, el procedimiento técnico general consta de los siguientes pasos:

### 1\. Identificar el número de parte exacto de la tarjeta (PCB Part Number)

Antes de buscar, es indispensable ubicar el código impreso en la superficie del PCB[1]:

* **MacBooks (Apple):** Tienen un código con formato **820-XXXX** impreso directamente en la placa (por ejemplo, `820-3115`, `820-00165`, `820-02020`)[1].
* **Laptops PC (Dell, Lenovo, HP, ASUS):** Utilizan nomenclaturas de fabricantes ODM como Compal (`LA-D751P`), Quanta (`DA0X83MB6H0`), Wistron o NM-A311[1].

---

### 2\. Dónde buscar y descargar los archivos

* **Foros y comunidades especializadas de microelectrónica:** Sitios de soporte técnico como *Badcaps Forum*, *VinaFix*, *Laptop-Schematics*, *RepairWiki* o grupos y canales de técnicos en plataformas de mensajería compartida.
* **Búsqueda web directa:** Consultando en motores de búsqueda el código de la placa junto a la extensión deseada (por ejemplo, `"820-3115 boardview .brd pdf schematic"`).
* **Archivos de prueba incluidos en el proyecto:** Dentro de las carpetas **Plan/** o **Boards/** del repositorio del simulador, se incluyen como caso de estudio los archivos completos de la MacBook Pro A1278 (**820-3115-B**)[1][2].

---

### 3\. Cómo integrarlos en el simulador

Una vez descargados los dos archivos de la tarjeta que deseas reparar, únicamente debes colocarlos en la carpeta **Boards/** del software (`Boards/TuPlaca.brd` y `Boards/TuPlaca_Esquematico.pdf`)[2][3]. El sistema los detectará e indexará automáticamente al iniciar[2].