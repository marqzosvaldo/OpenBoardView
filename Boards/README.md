# 📂 Carpeta de Placas y Esquemáticos (`Boards/`)

¡Esta es la carpeta donde puedes soltar tus propios archivos para cargarlos en OpenBoardView AI!

## ¿Cómo funciona la auto-detección?
Si colocas tus archivos aquí:
1. `Boards/TuPlaca.brd` *(el mapa físico Boardview)*
2. `Boards/TuPlaca_Esquematico.pdf` *(el esquemático del circuito)*

El software **los detectará y cargará automáticamente** al iniciar, sin necesidad de editar ningún archivo de configuración.

---

### Formatos compatibles:
* **Boardview:** `.brd` (incluyendo formato Apple con descifrado nativo), `.bdv`, `.asc`, etc.
* **Esquemático:** `.pdf` (diagramas electrónicos esquemáticos vectoriales o escaneados con texto indexable).

---

*(Si esta carpeta está vacía, el sistema cargará automáticamente el caso de estudio de prueba incluido en la carpeta `Plan/`: MacBook Pro A1278 820-3115-B).*
