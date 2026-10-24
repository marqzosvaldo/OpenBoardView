using OpenBoardView.AI.Models;

namespace OpenBoardView.AI.Graph;

public record MazeStepDto(
    int StepNumber,
    string StageTitle,
    string ComponentId,
    string Category,
    string Quadrant,
    string Side,
    int X,
    int Y,
    string InputNet,
    string OutputNet,
    double ExpectedVoltage,
    string PinToMeasure,
    string HowItWorks,
    string FailDiagnosis,
    string Status); // "PASSED", "ACTIVE_STEP", "BLOCKED_FAULT", "PENDING"

public record MazeTraceResultDto(
    string TraceId,
    string Title,
    string SymptomDescription,
    int CurrentStepNumber,
    int? BlockedAtStepNumber,
    string OverallDiagnosis,
    List<MazeStepDto> Steps);

public class MazeTraceRequestDto
{
    public string QueryOrSymptom { get; set; } = "no carga";
    public int? ActiveStepNumber { get; set; }
    public int? BlockedAtStepNumber { get; set; }
}

/// <summary>
/// Fase 6: Circuit Step Debugger ("Modo Laberinto Paso a Paso").
/// Modela el viaje secuencial de la corriente/señales a través de puertas lógicas, fusibles, reguladores y MOSFETs
/// como un Debugger interactivo (Step Over / Play / Breakpoint) sin sustituir la simulación DC ni la cámara térmica.
/// </summary>
public class CircuitMazeDebugger
{
    private readonly CircuitGraphBuilder _graph;

    public CircuitMazeDebugger(CircuitGraphBuilder graph)
    {
        _graph = graph ?? throw new ArgumentNullException(nameof(graph));
    }

    public MazeTraceResultDto BuildTrace(MazeTraceRequestDto request)
    {
        string q = (request.QueryOrSymptom ?? "no carga").Trim().ToLowerInvariant();

        List<(string CompId, string StageTitle, string InputNet, string OutputNet, double ExpectedV, string PinHint, string HowItWorks, string FailDiag)> rawSteps;
        string title;
        string symptomDesc;

        if (q.Contains("backlight") || q.Contains("brillo") || q.Contains("imagen") || q.Contains("pantalla") || q.Contains("lcd") || q.Contains("f9700") || q.Contains("u9701"))
        {
            title = "Laberinto de Backlight (Retroiluminación LCD)";
            symptomDesc = "La laptop enciende y da imagen al fondo, pero la pantalla está oscura (sin voltaje en LEDs).";
            rawSteps = GetBacklightMazeSequence();
        }
        else if (q.Contains("magsafe") || q.Contains("verde") || q.Contains("3v42") || q.Contains("onewire") || q.Contains("u6990") || q.Contains("u6900"))
        {
            title = "Laberinto One-Wire & Luz Verde MagSafe (PP3V42_G3H)";
            symptomDesc = "El cargador MagSafe no enciende luz verde/naranja y el SMC no despierta.";
            rawSteps = GetMagSafeGreenLightMazeSequence();
        }
        else if (q.Contains("carga") || q.Contains("bateria") || q.Contains("batería") || q.Contains("enciende") || q.Contains("ppbus") || q.Contains("u7000") || q.Contains("f7040"))
        {
            title = "Laberinto de Carga y Generación de PPBUS_G3H (No Carga / No Enciende)";
            symptomDesc = "Seguimiento paso a paso desde el conector MagSafe (16.5V), pasando por U7000 (ISL6259) y F7040 hasta la batería.";
            rawSteps = GetChargingAndPpbusMazeSequence();
        }
        else
        {
            // Rastreador dinámico sobre el grafo para cualquier red o componente personalizado
            title = $"Laberinto Dinámico de Señal/Potencia: {request.QueryOrSymptom.ToUpperInvariant()}";
            symptomDesc = $"Ruta topológica calculada automáticamente en el grafo BRD para '{request.QueryOrSymptom}'.";
            rawSteps = BuildDynamicGraphSequence(request.QueryOrSymptom);
        }

        int blockedStep = request.BlockedAtStepNumber ?? 0;
        int activeStep = request.ActiveStepNumber ?? (blockedStep > 0 ? blockedStep : 1);

        var steps = new List<MazeStepDto>();
        int stepIdx = 1;

        foreach (var item in rawSteps)
        {
            var comp = _graph.GetComponentDetails(item.CompId)
                       ?? FallbackComponentSearch(item.InputNet, item.OutputNet, item.CompId);

            int x = comp?.X ?? (1200 + stepIdx * 450);
            int y = comp?.Y ?? 2200;
            string quad = comp?.Quadrant ?? "Q3";
            string side = comp?.Side ?? "Bottom";
            string cat = comp?.Category ?? "Component";

            string status;
            if (blockedStep > 0)
            {
                if (stepIdx < blockedStep) status = "PASSED";
                else if (stepIdx == blockedStep) status = "BLOCKED_FAULT";
                else status = "PENDING";
            }
            else
            {
                if (stepIdx < activeStep) status = "PASSED";
                else if (stepIdx == activeStep) status = "ACTIVE_STEP";
                else status = "PENDING";
            }

            steps.Add(new MazeStepDto(
                StepNumber: stepIdx,
                StageTitle: item.StageTitle,
                ComponentId: comp?.ComponentId ?? item.CompId,
                Category: cat,
                Quadrant: quad,
                Side: side,
                X: x,
                Y: y,
                InputNet: item.InputNet,
                OutputNet: item.OutputNet,
                ExpectedVoltage: item.ExpectedV,
                PinToMeasure: item.PinHint,
                HowItWorks: item.HowItWorks,
                FailDiagnosis: item.FailDiag,
                Status: status
            ));

            stepIdx++;
        }

        activeStep = Math.Clamp(activeStep, 1, Math.Max(1, steps.Count));
        var currentStepObj = steps.FirstOrDefault(s => s.StepNumber == (blockedStep > 0 ? blockedStep : activeStep)) ?? steps.First();

        string overallDiag = blockedStep > 0
            ? $"🛑 MURO EN EL LABERINTO (Paso {blockedStep} - {currentStepObj.ComponentId}): La corriente llegó con éxito hasta el Paso {Math.Max(1, blockedStep - 1)}, pero se interrumpe en {currentStepObj.ComponentId} ({currentStepObj.OutputNet}). Causa probable: {currentStepObj.FailDiagnosis}"
            : $"🧭 DEBUGGER EN PASO {activeStep}/{steps.Count} ({currentStepObj.ComponentId} - {currentStepObj.Quadrant}): Mide {currentStepObj.ExpectedVoltage:F2}V en {currentStepObj.PinToMeasure} ({currentStepObj.OutputNet}). Si está presente, avanza al siguiente paso; si marca 0V, presiona 'Marcar Falla Aquí'.";

        return new MazeTraceResultDto(
            TraceId: Guid.NewGuid().ToString("N")[..8],
            Title: title,
            SymptomDescription: symptomDesc,
            CurrentStepNumber: activeStep,
            BlockedAtStepNumber: blockedStep > 0 ? blockedStep : null,
            OverallDiagnosis: overallDiag,
            Steps: steps
        );
    }

    private ComponentNodeSummary? FallbackComponentSearch(string inNet, string outNet, string defaultId)
    {
        var onOut = _graph.GetComponentsOnNet(outNet).FirstOrDefault();
        if (onOut != null) return onOut;
        return _graph.GetComponentsOnNet(inNet).FirstOrDefault();
    }

    private List<(string CompId, string StageTitle, string InputNet, string OutputNet, double ExpectedV, string PinHint, string HowItWorks, string FailDiag)> GetChargingAndPpbusMazeSequence()
    {
        return new()
        {
            ("J6900", "1. Entrada DC-In (Conector MagSafe)", "ADAPTER_SENSE", "PP18V5_DCIN_CONN", 16.50, "Pines 1 y 2 de J6900",
                "Recibe los 16.5V del cargador MagSafe original y los entrega a la placa.",
                "Conector MagSafe sucio/quemado o cargador defectuoso. Reemplazar DC-In board."),

            ("F6905", "2. Fusible Principal de Entrada DC", "PP18V5_DCIN_CONN", "PPDCIN_G3H", 16.50, "Ambos extremos de F6905 (6A)",
                "Protege la placa contra sobretensiones en la línea de entrada de 16.5V.",
                "Fusible F6905 abierto. Antes de reemplazarlo, mide continuidad a tierra en PPDCIN_G3H para descartar corto en Q7080/U7000."),

            ("U6990", "3. Regulador Siempre-Activo 3.42V (G3Hot)", "PPDCIN_G3H", "PP3V42_G3H", 3.42, "Pin 4 (VIN) y Bobina L6995 (3.42V)",
                "Convierte los 16.5V del cargador en 3.42V para alimentar al SMC (U4900) y al circuito de luz verde.",
                "Sin 3.42V el SMC está muerto y nunca habrá carga ni luz en MagSafe. Revisar D6990, R6990, U6990 o corto en PP3V42_G3H."),

            ("R7005", "4. Alimentación VCC al Charger IC (ISL6259)", "PPDCIN_G3H", "PPCHGR_DCIN", 16.50, "Resistencia R7005 (20 Ω) hacia Pin 2 de U7000",
                "Alimenta el pin DCIN (Pin 2) del integrado cargador U7000 a través de la resistencia fusible R7005.",
                "R7005 abierta o desvalorizada (suele quemarse cuando C7005 o U7000 entran en corto)."),

            ("U7000", "5. Detección de Cargador (ACIN & SMC_BC_ACOK)", "CHGR_ACIN", "SMC_BC_ACOK", 3.30, "Pin 3 (ACIN > 3.9V) y Pin 14 (ACOK = 3.3V)",
                "U7000 verifica el divisor resistivo R7010/R7011 en ACIN; si es ~4V, levanta SMC_BC_ACOK para encender el MagSafe y avisar al SMC.",
                "Divisor R7010/R7011 alterado o U7000 dañado. Sin ACOK no se habilita el circuito de carga."),

            ("R7020", "6. Sensado de Corriente de Entrada (CSI_P / CSI_N)", "PPDCIN_G3H_INRUSH", "PPDCIN_G3H_CHGR", 16.50, "Pines 27 y 28 de U7000 (lazo R7020 0.02Ω)",
                "U7000 mide cuánta corriente consume la placa del cargador mediante las resistencias R7021/R7022 hacia los pines 27/28.",
                "Si R7021 o R7022 se abren, U7000 cree que hay sobrecorriente y apaga inmediatamente la generación de PPBUS_G3H."),

            ("L7030", "7. Bobina Buck Converter de Carga", "CHGR_PHASE", "PPVBAT_G3H_CHGR_REG", 12.56, "Bobina L7030 y Resistencia Shunt R7050",
                "Los MOSFETs Q7030/Q7035 conmutados por U7000 reducen los 16.5V a 12.56V regulados (8.4V si el SMC no está comunicando).",
                "Si aquí hay 0V o <3V: revisar Q7030/Q7035 en corto, R7051/R7052 (lazo CSOP/CSON pines 17/18 de U7000) o corto después de F7040."),

            ("F7040", "8. Fusible Maestro del Sistema (PPBUS_G3H)", "PPVBAT_G3H_CHGR_REG", "PPBUS_G3H", 12.56, "Pines 1 y 2 de F7040 (8A)",
                "Une la salida del cargador con el riel principal PPBUS_G3H que alimenta toda la placa y la etapa de batería.",
                "Si hay 12.56V en el Pin 1 pero 0V en el Pin 2, F7040 está abierto por un corto en PPBUS_G3H (Cuadrantes Q1/Q2)."),

            ("Q7055", "9. MOSFET de Paso hacia la Batería & SMBus", "PPBUS_G3H", "PPVBAT_G3H_CONN", 12.56, "Conector de Batería J6950 (Pines 1-3 y SCL/SDA Pines 4-6)",
                "El SMC conversa con la batería por SMBus (R5280/R5281); si la reconoce, U7000 abre Q7055 para inyectar corriente de carga.",
                "Si PPBUS_G3H tiene 12.56V pero no carga: si mide 12.23V el SMC no lee la batería (revisar R5280/R5281, D6950 o ISL6259). Si mide 12.56V, probar otra batería o Q7055.")
        };
    }

    private List<(string CompId, string StageTitle, string InputNet, string OutputNet, double ExpectedV, string PinHint, string HowItWorks, string FailDiag)> GetMagSafeGreenLightMazeSequence()
    {
        return new()
        {
            ("J6900", "1. Entrada MagSafe (DC-In)", "PP18V5_DCIN_CONN", "PPDCIN_G3H", 16.50, "Pin 1/2 de J6900",
                "Ingresa tensión del adaptador MagSafe.", "Revisar conector MagSafe o F6905."),
            ("D6990", "2. Diodo Dual Alimentación 3V42", "PPDCIN_G3H", "PPVIN_G3H_P3V42G3H", 16.20, "Salida de D6990 / R6990",
                "Selecciona entre el cargador o la batería para alimentar el regulador de 3.42V.", "R6990 (4.7Ω) quemada o D6990 abierto."),
            ("U6990", "3. Regulador Switching 3.42V (LT3470A)", "PPVIN_G3H_P3V42G3H", "PP3V42_G3H", 3.42, "Bobina L6995",
                "Genera los 3.42V indispensables para el SMC y el circuito One-Wire.", "Corto en PP3V42_G3H o U6990 dañado."),
            ("U7000", "4. Señal ACOK del Charger (ISL6259)", "PPDCIN_G3H", "SMC_BC_ACOK", 3.30, "Pin 14 de U7000",
                "Indica que el adaptador AC es válido.", "U7000 no recibe DCIN (R7005) o ACIN (R7010)."),
            ("U4900", "5. Controlador SMC (System Management Controller)", "PP3V42_G3H", "SYS_ONEWIRE", 3.42, "Línea SYS_ONEWIRE",
                "El SMC despierta con 3.42V y envía el protocolo serie One-Wire hacia el cargador.", "SMC con sulfato, sin SMC_RESET_L (U5010) o dañado."),
            ("U6900", "6. Compuerta Buffer One-Wire hacia MagSafe", "SYS_ONEWIRE", "ADAPTER_SENSE", 3.42, "Pin 5 de U6900 -> Pin 5 de J6900",
                "Transmite los datos One-Wire al pin central del MagSafe para encender el LED verde/naranja.", "U6900 o U6901 dañados por sobretensión en el pin central del MagSafe.")
        };
    }

    private List<(string CompId, string StageTitle, string InputNet, string OutputNet, double ExpectedV, string PinHint, string HowItWorks, string FailDiag)> GetBacklightMazeSequence()
    {
        return new()
        {
            ("F9700", "1. Fusible de Backlight (3A)", "PPBUS_G3H", "PPBUS_S0_LCDBKLT_FUSED", 12.56, "Ambos lados de F9700 (Cuadrante Q1)",
                "Toma los 12.56V de PPBUS_G3H para alimentar la etapa elevadora (Boost) de pantalla.", "Fusible F9700 abierto por corto en la salida o cable LVDS dañado."),
            ("Q9706", "2. MOSFET de Paso de Backlight", "PPBUS_S0_LCDBKLT_FUSED", "PPBUS_SW_LCDBKLT_PWR", 12.56, "Pines de salida 1,2,5,6 de Q9706",
                "Se abre cuando BKLT_PLT_RST_L y LCD_BKLT_EN habilitan el divisor R9788/R9789.", "Si el Gate tiene 12.5V en vez de ~5V, Q9707 no está activando el divisor."),
            ("L9701", "3. Bobina Boost de Backlight", "PPBUS_SW_LCDBKLT_PWR", "LCDBKLT_BOOST", 12.56, "Bobina L9701",
                "Acumula energía para elevar los 12.5V hasta ~28V-40V.", "Bobina abierta o pista sulfatada entre L9701 y D9701."),
            ("D9701", "4. Diodo Rectificador Schottky Boost", "LCDBKLT_BOOST", "PPVOUT_SW_LCDBKLT", 28.50, "Cátodo de D9701",
                "Rectifica los pulsos de alta tensión hacia los LEDs de la pantalla.", "Si mide 12.5V en vez de ~28V, el driver U9701 no está haciendo switching."),
            ("U9701", "5. Driver LED LP8550 (Control I2C y Retorno)", "PPVOUT_SW_LCDBKLT", "PPVOUT_SW_LCDBKLT_FB", 28.50, "Bola A5 (FB) y resistencias de retorno",
                "Regula el brillo por PWM/I2C y cierra el retorno de las tiras LED.", "Pista de Feedback (bola A5 de U9701) comida por humedad o U9701 dañado."),
            ("J9000", "6. Conector de Pantalla LVDS (Pines 38-40)", "PPVOUT_SW_LCDBKLT", "LED_RETURN_1", 28.50, "Pines 38, 39 y 40 de J9000",
                "Entrega los ~28V a la pantalla LCD.", "Pines 38-40 carbonizados en el conector J9000 o fusible L9080 abierto.")
        };
    }

    private List<(string CompId, string StageTitle, string InputNet, string OutputNet, double ExpectedV, string PinHint, string HowItWorks, string FailDiag)> BuildDynamicGraphSequence(string query)
    {
        var list = new List<(string CompId, string StageTitle, string InputNet, string OutputNet, double ExpectedV, string PinHint, string HowItWorks, string FailDiag)>();
        var matchedNets = _graph.SearchNetNames(query, 5);
        string startNet = matchedNets.FirstOrDefault() ?? "PPBUS_G3H";
        double nomV = CircuitSimulator.InferNominalVoltage(startNet);

        var hops = _graph.GetUpstreamPath(null, startNet, maxDepth: 3).Take(6).ToList();
        if (hops.Count == 0)
        {
            var comps = _graph.GetComponentsOnNet(startNet).Take(5).ToList();
            int idx = 1;
            foreach (var c in comps)
            {
                list.Add((c.ComponentId, $"Nodo {idx} en {startNet}", startNet, startNet, nomV, $"Pines de {c.ComponentId}",
                    $"Componente {c.Category} conectado a {startNet} en cuadrante {c.Quadrant}.",
                    $"Verificar continuidad e impedancia en {c.ComponentId}."));
                idx++;
            }
            return list;
        }

        int step = 1;
        foreach (var h in hops)
        {
            double v = CircuitSimulator.InferNominalVoltage(h.ToNet);
            list.Add((h.ThroughComponentId, $"{step}. Puente {h.ComponentCategory} ({h.ThroughComponentId})",
                h.FromNet, h.ToNet, v, $"Medir en {h.ThroughComponentId}",
                $"Conecta la red {h.FromNet} hacia {h.ToNet} en el cuadrante {h.Quadrant}.",
                $"Si hay tensión en {h.FromNet} pero no en {h.ToNet}, {h.ThroughComponentId} está abierto o {h.ToNet} está en corto."));
            step++;
        }

        return list;
    }
}
