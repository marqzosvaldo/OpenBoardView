# Base de conocimiento de flujos de diagnóstico

Cada archivo `*.json` de esta carpeta describe los **flujos de diagnóstico curados** de una familia de placa
(el recorrido paso a paso del Circuit Step Debugger). Se cargan al arrancar el servidor.

## Reglas importantes

- Un archivo **solo se aplica** si el nombre del `.brd` cargado contiene alguno de los textos de `boardMatch`.
  Si cargas otra placa, estos flujos **no se usan**: el debugger calcula la ruta desde la topología del `.brd`
  y marca los voltajes como *estimados*.
- `voltageSource: "curated"` significa "lo escribió una persona leyendo el esquemático". No se verifica
  automáticamente el valor, por eso la interfaz lo etiqueta.
- Los nombres de **componentes y redes deben ser los del `.brd`**, que a veces difieren del esquemático
  (ej.: el esquemático dice `PP18V5_DCIN_CONN` y el `.brd` de la 820-3115-B usa `PP18V5_DCIN_FUSE`).

## Formato

```json
{
  "family": "mi-familia",
  "boardMatch": [ "820-XXXX" ],
  "flows": [
    {
      "id": "no-carga",
      "title": "Título visible",
      "symptom": "Descripción del síntoma",
      "keywords": [ "carga", "ppbus" ],
      "steps": [
        {
          "component": "F7040",
          "stage": "1. Fusible principal",
          "inputNet": "RED_ENTRADA",
          "outputNet": "RED_SALIDA",
          "expectedVoltage": 12.56,
          "voltageSource": "curated",
          "pinToMeasure": "Pines 1 y 2",
          "howItWorks": "Qué hace este nodo.",
          "failDiagnosis": "Qué significa que aquí haya 0V."
        }
      ]
    }
  ]
}
```

El orden de los flujos en el archivo define la prioridad cuando varias `keywords` coinciden con la consulta.

## Cómo validar tu archivo

```bash
dotnet test tests/OpenBoardView.AI.Tests/OpenBoardView.AI.Tests.csproj
```

Las pruebas comprueban que cada componente y red citados **existan en el `.brd`**, que el componente esté
**conectado a alguna de las dos redes del paso**, y que aparezcan en el **esquemático PDF**.

> Nota: las pruebas incluidas validan la placa de ejemplo de `Plan/` (820-3115-B). Para tu propia familia,
> agrega su `.brd` de ejemplo y su prueba equivalente.
