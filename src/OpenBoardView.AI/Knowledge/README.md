# Base de Conocimiento de Flujos y Presets de Diagnóstico (v4)

Cada archivo `*.json` de esta carpeta describe los **flujos de diagnóstico curados** de una familia de placa para el **Circuit Step Debugger**.

En la arquitectura v4 de OpenBoardView AI, los flujos provienen de **tres fuentes complementarias**:
1. **Flujos Curados en Archivos JSON (`Knowledge/*.json`):** Definidos manualmente por técnicos expertos para placas de referencia (ej. `apple-820-3115-b.json` y `apple-820-02757-m3.json`).
2. **Presets Persistentes en SQLite (`DiagnosticPresets`):** Importados automáticamente mediante [`PresetImporterService`](file:///Users/marqzosvaldo/Documents/GitHub/OpenBoardView/src/OpenBoardView.AI/Services/PresetImporterService.cs) desde `Presets/*.json` o creados dinámicamente por la aplicación.
3. **Flujos Auto-Generados por IA (`AutomatedSchematicAnalyzer`):** Generados de manera autónoma al analizar esquemáticos PDF complejos con `PdfPig` y LLM, validados contra el archivo CAD `.brd` y persistidos en SQLite y disco.

---

## Reglas Importantes de Validación

- Un archivo JSON **solo se aplica** si el nombre del `.brd` cargado contiene alguno de los textos de `boardMatch`.
  Si cargas otra placa sin flujos curados ni presets en SQLite, el depurador calcula automáticamente la ruta desde la topología física del `.brd` (`DYNAMIC_PPDCIN_USBC_AON`, etc.) y etiqueta las mediciones como *inferred* (inferidas).
- `voltageSource: "curated"` indica que un técnico verificó el valor en el esquemático. `voltageSource: "inferred"` indica que el valor fue calculado algorítmicamente o deducido por el modelo.
- Los nombres de **componentes y redes deben coincidir exactamente con el archivo CAD `.brd`**, ya que en ocasiones el esquemático utiliza alias ligeramente distintos (ej.: el esquemático dice `PP18V5_DCIN_CONN` y el `.brd` de la 820-3115-B usa `PP18V5_DCIN_FUSE`).

---

## Formato Estándar de Flujo Curado

```json
{
  "family": "apple-macbook-pro-14-m3-820-02757",
  "boardMatch": [ "820-02757" ],
  "flows": [
    {
      "id": "usb-c-charging-fail",
      "title": "USB-C No Negocia 20V / Sin Carga",
      "symptom": "Puertos USB-C bloqueados en 5V o sin corriente (PPDCIN_USBC_AON)",
      "keywords": [ "usb", "usbc", "puertos", "no carga", "5v" ],
      "steps": [
        {
          "component": "F5601",
          "stage": "1. Fusible Entrada CD3217",
          "inputNet": "PPDCIN_USBC_AON",
          "outputNet": "PPHV_INT5_AONSW",
          "expectedVoltage": 20.0,
          "voltageSource": "curated",
          "pinToMeasure": "Pines 1 y 2 de F5601",
          "howItWorks": "Fusible de protección en la línea de entrada USB-PD hacia el controlador.",
          "failDiagnosis": "Si hay 20V a la entrada pero 0V a la salida, F5601 está abierto o PPHV_INT5_AONSW tiene un corto a tierra."
        }
      ]
    }
  ]
}
```

---

## Validación Automatizada con xUnit

Para comprobar que tus archivos JSON cumplen con la topología real del circuito, ejecuta:

```bash
dotnet test tests/OpenBoardView.AI.Tests/OpenBoardView.AI.Tests.csproj
```

La suite valida automáticamente:
1. Que cada componente y red existan físicamente en el `.brd`.
2. Que el componente esté conectado a las redes de entrada y salida declaradas.
3. Que los pines y páginas esquemáticas concuerden con el índice semántico.
