using System.Globalization;
using System.Text.RegularExpressions;
using OpenBoardView.AI.Models;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;

namespace OpenBoardView.AI.Parsers;

public class SchematicIndex
{
    public string PdfFileName { get; set; } = string.Empty;
    public int TotalPages { get; set; }
    public Dictionary<string, List<(int PageNumber, string Snippet)>> ComponentContext { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, List<(int PageNumber, string Snippet)>> NetContext { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<int, string> PageTexts { get; set; } = new();

    /// <summary>Voltajes nominales/máximos extraídos directamente de propiedades VOLTAGE=...V en el PDF.</summary>
    public Dictionary<string, (double Voltage, int PageNumber)> NetVoltages { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Mapa de alias de red (ej. PPVIN_S0_CPUIMVP -> PPBUS_S5_HS_COMPUTING_ISNS) extraído de páginas de Power Aliases.</summary>
    public Dictionary<string, string> NetAliases { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Redes marcadas con SWITCH_NODE=TRUE en el esquemático.</summary>
    public HashSet<string> SwitchNodeNets { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Valores eléctricos extraídos por vecindad espacial 2D para cada componente (ej. "0.003 Ω", "82UF 16V", "8AMP-24V").</summary>
    public Dictionary<string, string> ComponentValues { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Componentes marcados explícitamente como NO STUFF / NOSTUFF en el esquemático (no poblados en placa).</summary>
    public HashSet<string> NoStuffComponents { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// Fase 2: Extractor de Semántica y Parámetros CAD de esquemáticos en PDF usando UglyToad.PdfPig.
/// Combina indexación textual con análisis espacial 2D (coordenadas X/Y) para extraer:
/// - El 100% de las redes del .brd presentes en el PDF (sin limitarse a prefijos).
/// - Atributos CAD explícitos VOLTAGE=...V y SWITCH_NODE=TRUE asociados a cada red.
/// - Bloques de alias MAKE_BASE=TRUE (Power/Signal Aliases).
/// - Valores de componentes (ohmios, faradios, amperaje) y banderas NO STUFF / NOSTUFF por vecindad de Voronoi.
/// </summary>
public class SchematicPdfReader
{
    private static readonly Regex ComponentRegex = new(
        @"^([CRLUQDFJYX][0-9]{3,5}[A-Z]?)$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex InlineComponentRegex = new(
        @"\b([CRLUQDFJYX][0-9]{3,5}[A-Z]?)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex PowerNetFallbackRegex = new(
        @"^=?(PP[A-Z0-9_]{2,}|GND[A-Z0-9_]*|SMC_[A-Z0-9_]{2,}|PM_[A-Z0-9_]{2,}|PLT_[A-Z0-9_]{2,}|ALL_SYS_PWRGD|CHGR_[A-Z0-9_]{2,}|SYS_ONEWIRE|ADAPTER_SENSE|BKLT_[A-Z0-9_]+|LCD_[A-Z0-9_]+|LED_RETURN_[0-9])$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex VoltageAttrRegex = new(
        @"VOLTAGE\s*=\s*(-?[0-9]+(?:\.[0-9]+)?)\s*V?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex CapValueRegex = new(
        @"^([0-9]+(?:\.[0-9]+)?(?:UF|PF|NF)(?:-[0-9A-Z.]+)?)$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex CapVoltageRegex = new(
        @"^([0-9]+(?:\.[0-9]+)?V)$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex FuseValueRegex = new(
        @"^([0-9]+(?:\.[0-9]+)?AMP(?:-[0-9A-Z.]+)*)$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex InductorValueRegex = new(
        @"^([0-9]+(?:\.[0-9]+)?(?:UH|NH|MH)(?:-[0-9A-Z.%]+)*|FERR-[0-9A-Z.-]+|[0-9]+-OHM-[0-9A-Z.-]+)$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex ResistorValueRegex = new(
        @"^(SHORT|[0-9]+(?:\.[0-9]+)?(?:[KMG]?OHM|[KMG])?|0\.[0-9]+)$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly HashSet<string> PackageAndMetaTokens = new(StringComparer.OrdinalIgnoreCase)
    {
        "201", "0201", "402", "0402", "402-1", "402-LF", "0402-1", "0402-LF",
        "603", "0603", "603-1", "603-2", "603-HF", "805", "0805", "1206", "1206-1", "1210", "1210-1",
        "0612", "0612-3", "0504", "RL1632W",
        "MF", "MF-LF", "MF-1", "CRITICAL", "OMIT", "OMIT_TABLE", "NOSTUFF", "NO", "STUFF",
        "TOP", "BOTTOM", "TRUE", "FALSE", "SM", "SMD", "TH", "BGA", "QFN", "TQFN", "UTQFN", "DFN", "SOT23", "SC70", "VESM",
        "X5R", "X7R", "CERM", "C0G", "NP0", "ELEC", "POLY", "TANT", "POLY-TANT",
        "1", "2", "3", "4", "5", "6", "7", "8", "9", "10", "12", "21", "34", "43"
    };

    private readonly record struct PositionedWord(
        string Text,
        double Left,
        double Bottom,
        double Right,
        double Top,
        double Cx,
        double Cy);

    public SchematicIndex ReadAndIndexPdf(string pdfPath, BoardTopology? topology = null)
    {
        if (!File.Exists(pdfPath))
            throw new FileNotFoundException($"No se encontró el archivo esquemático PDF en: {pdfPath}");

        var index = new SchematicIndex
        {
            PdfFileName = Path.GetFileName(pdfPath)
        };

        var knownCompIds = topology != null
            ? new HashSet<string>(topology.Components.Select(c => c.Id), StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var knownNets = topology != null
            ? new HashSet<string>(topology.Nets.Where(n => !n.IsUnconnected).Select(n => n.Name), StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Conjuntos para decidir si un componente es realmente NOSTUFF:
        // Si aparece en una página de circuito como poblado (sin NOSTUFF) y en otra solo en nota, prevalece poblado.
        var stuffedComponents = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var noStuffCandidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        using var document = PdfDocument.Open(pdfPath);
        index.TotalPages = document.NumberOfPages;

        foreach (Page page in document.GetPages())
        {
            var rawWords = page.GetWords().ToList();
            if (rawWords.Count == 0) continue;

            var pWords = new List<PositionedWord>(rawWords.Count);
            foreach (var w in rawWords)
            {
                var box = w.BoundingBox;
                pWords.Add(new PositionedWord(
                    w.Text,
                    box.Left,
                    box.Bottom,
                    box.Right,
                    box.Top,
                    (box.Left + box.Right) * 0.5,
                    (box.Bottom + box.Top) * 0.5));
            }

            // 1. Reconstruir líneas horizontales para PageTexts y búsquedas generales
            var lines = rawWords
                .GroupBy(w => (int)Math.Round(w.BoundingBox.Bottom / 6.0))
                .OrderByDescending(g => g.Key)
                .Select(g => string.Join(" ", g.OrderBy(w => w.BoundingBox.Left).Select(w => w.Text)))
                .ToList();

            string fullPageText = string.Join("\n", lines);
            index.PageTexts[page.Number] = fullPageText;

            for (int i = 0; i < lines.Count; i++)
            {
                string currentLine = lines[i];
                int startIdx = Math.Max(0, i - 1);
                int endIdx = Math.Min(lines.Count - 1, i + 1);
                string contextWindow = string.Join(" | ", lines.Skip(startIdx).Take(endIdx - startIdx + 1));
                if (contextWindow.Length > 260)
                    contextWindow = contextWindow.Substring(0, 260) + "...";

                // Indexar componentes en la línea
                foreach (Match match in InlineComponentRegex.Matches(currentLine))
                {
                    string compId = match.Groups[1].Value.ToUpperInvariant();
                    if (knownCompIds.Count == 0 || knownCompIds.Contains(compId))
                    {
                        AddEntry(index.ComponentContext, compId, page.Number, contextWindow);
                    }
                }

                // Indexar el 100% de las redes conocidas del .brd presentes en la línea (más fallback regex si no hay topología)
                var tokens = currentLine.Split(new[] { ' ', '\t', ',', ';', '(', ')', '[', ']', ':' }, StringSplitOptions.RemoveEmptyEntries);
                foreach (var rawTok in tokens)
                {
                    string cleanNet = rawTok.TrimStart('=').Trim().ToUpperInvariant();
                    if (cleanNet.Length < 2) continue;

                    bool isKnownNet = (knownNets.Count > 0 && knownNets.Contains(cleanNet))
                                      || PowerNetFallbackRegex.IsMatch(rawTok);
                    if (isKnownNet)
                    {
                        AddEntry(index.NetContext, cleanNet, page.Number, contextWindow);
                    }
                }
            }

            // 2. Extracción Espacial 2D de Redes, Atributos VOLTAGE=...V, SWITCH_NODE=TRUE y Aliases (MAKE_BASE=TRUE)
            ExtractPageNetAttributes2D(page.Number, pWords, knownNets, index);

            // 3. Extracción Espacial 2D (Voronoi local) de Valores de Componentes y Banderas NO STUFF / NOSTUFF
            // Solo en páginas de circuito (evitando índice pág 1, BOM pág 5 y páginas de reglas >= 78)
            if (page.Number >= 6 && page.Number <= 77)
            {
                ExtractPageComponentAttributes2D(pWords, knownCompIds, index, stuffedComponents, noStuffCandidates);
            }
        }

        foreach (var id in noStuffCandidates)
        {
            if (!stuffedComponents.Contains(id))
            {
                index.NoStuffComponents.Add(id);
            }
        }

        // Propagar voltajes a través de alias (ej. si =PPBUS_S0_LCDBKLT es alias de PPBUS_G3H)
        foreach (var kvp in index.NetAliases)
        {
            string aliasNet = kvp.Key;
            string baseNet = kvp.Value;
            if (!index.NetVoltages.ContainsKey(aliasNet) && index.NetVoltages.TryGetValue(baseNet, out var baseVolt))
            {
                index.NetVoltages[aliasNet] = baseVolt;
            }
        }

        // Enriquecer directamente el BoardTopology si se proporcionó
        if (topology != null)
        {
            EnrichTopologyWithIndex(topology, index);
        }

        return index;
    }

    public static void EnrichTopologyWithIndex(BoardTopology topology, SchematicIndex index)
    {
        if (topology == null || index == null) return;

        foreach (var comp in topology.Components)
        {
            if (index.ComponentContext.TryGetValue(comp.Id, out var entries))
            {
                comp.SchematicPages = entries.Select(e => e.PageNumber).Distinct().OrderBy(p => p).ToList();
                comp.SchematicContext = string.Join(" // ", entries.Take(4).Select(e => $"[Pág {e.PageNumber}] {e.Snippet}"));
            }

            if (index.ComponentValues.TryGetValue(comp.Id, out var val))
            {
                comp.SchematicValue = val;
            }

            if (index.NoStuffComponents.Contains(comp.Id))
            {
                comp.IsNoStuff = true;
            }
        }

        foreach (var net in topology.Nets)
        {
            if (index.NetContext.TryGetValue(net.Name, out var entries))
            {
                net.SchematicPages = entries.Select(e => e.PageNumber).Distinct().OrderBy(p => p).ToList();
                net.SchematicContext = string.Join(" // ", entries.Take(4).Select(e => $"[Pág {e.PageNumber}] {e.Snippet}"));
            }

            if (index.NetAliases.TryGetValue(net.Name, out var baseRail))
            {
                net.BaseRailName = baseRail;
            }

            if (index.SwitchNodeNets.Contains(net.Name))
            {
                net.IsSwitchNode = true;
            }

            if (index.NetVoltages.TryGetValue(net.Name, out var vInfo))
            {
                net.SchematicVoltage = vInfo.Voltage;
                net.SchematicVoltagePage = vInfo.PageNumber;
            }
        }
    }

    private static void ExtractPageNetAttributes2D(
        int pageNumber,
        List<PositionedWord> words,
        HashSet<string> knownNets,
        SchematicIndex index)
    {
        // Localizar todas las menciones de nombres de redes en la página
        var netMentions = new List<(string NetName, bool IsAlias, PositionedWord Word)>();

        foreach (var w in words)
        {
            string raw = w.Text.Trim();
            if (raw.Length < 3 || raw.Contains('='))
            {
                if (!raw.StartsWith('=')) continue;
            }

            bool isAlias = raw.StartsWith('=');
            string clean = raw.TrimStart('=').Trim().ToUpperInvariant();
            if (clean.Length < 3 || clean.Contains('=')) continue;

            if ((knownNets.Count > 0 && knownNets.Contains(clean)) ||
                clean.StartsWith("PP", StringComparison.OrdinalIgnoreCase) ||
                clean.Equals("GND", StringComparison.OrdinalIgnoreCase) ||
                clean.StartsWith("GND_", StringComparison.OrdinalIgnoreCase))
            {
                netMentions.Add((clean, isAlias, w));
            }
        }

        if (netMentions.Count == 0) return;

        // 1. Asociar cada propiedad VOLTAGE=<num>V a la etiqueta de red de su bloque CAD
        for (int i = 0; i < words.Count; i++)
        {
            var w = words[i];
            var m = VoltageAttrRegex.Match(w.Text.Trim());
            if (!m.Success) continue;

            // Ignorar notas genéricas de Intel como "Note. VOLTAGE=1.05V"
            bool precededByNote = false;
            for (int j = Math.Max(0, i - 3); j <= Math.Min(words.Count - 1, i + 3); j++)
            {
                if (j == i) continue;
                var other = words[j];
                if (Math.Abs(other.Cy - w.Cy) < 4.0 &&
                    other.Left < w.Left &&
                    w.Left - other.Right < 18.0 &&
                    other.Text.StartsWith("Note", StringComparison.OrdinalIgnoreCase))
                {
                    precededByNote = true;
                    break;
                }
            }
            if (precededByNote) continue;

            if (!double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double volts))
                continue;

            // En Cadence Concept HDL, las líneas del bloque de propiedades de una red comparten la misma alineación X (Left)
            // y el nombre de la red (sin '=') está en el encabezado del bloque justo encima de MIN_LINE_WIDTH / VOLTAGE=.
            (string NetName, bool IsAlias, PositionedWord Word)? bestNet = null;
            double bestScore = double.MaxValue;

            foreach (var nm in netMentions)
            {
                if (nm.IsAlias) continue; // Los alias (=PP...) nunca son dueños de un bloque de definición VOLTAGE=

                double dLeft = Math.Abs(nm.Word.Left - w.Left);
                double dy = nm.Word.Cy - w.Cy; // Positivo cuando el nombre de la red está encima de VOLTAGE=

                if (dLeft <= 80.0 && dy >= -35.0 && dy <= 80.0)
                {
                    double directionPenalty = dy < -4.0 ? 18.0 : 0.0;
                    double score = (dLeft * 2.0) + Math.Abs(dy) + directionPenalty;
                    if (score < bestScore)
                    {
                        bestScore = score;
                        bestNet = nm;
                    }
                }
            }

            if (bestNet.HasValue)
            {
                string netName = bestNet.Value.NetName;
                // Preferir el voltaje de páginas de circuito reales (págs. 9-77) sobre la tabla de reglas de espaciado (pág. 7)
                if (!index.NetVoltages.TryGetValue(netName, out var existing) ||
                    (existing.PageNumber is 7 or 8 && pageNumber > 8))
                {
                    index.NetVoltages[netName] = (volts, pageNumber);
                }
            }
        }

        // 2. Detectar SWITCH_NODE=TRUE
        foreach (var w in words)
        {
            if (!w.Text.Equals("SWITCH_NODE=TRUE", StringComparison.OrdinalIgnoreCase)) continue;

            (string NetName, bool IsAlias, PositionedWord Word)? bestNet = null;
            double bestScore = double.MaxValue;
            foreach (var nm in netMentions)
            {
                double dLeft = Math.Abs(nm.Word.Left - w.Left);
                double dCy = Math.Abs(nm.Word.Cy - w.Cy);
                if (dLeft <= 38.0 && dCy <= 38.0)
                {
                    double score = (dLeft * 2.0) + dCy + (nm.IsAlias ? 20.0 : 0.0);
                    if (score < bestScore)
                    {
                        bestScore = score;
                        bestNet = nm;
                    }
                }
            }

            if (bestNet.HasValue)
            {
                index.SwitchNodeNets.Add(bestNet.Value.NetName);
            }
        }

        // 3. En las páginas de Power/Signal Aliases (págs. 7 y 8), vincular alias (=PP...) con su riel base (MAKE_BASE=TRUE)
        if (pageNumber is 7 or 8)
        {
            var baseRailsOnPage = netMentions.Where(n => !n.IsAlias && n.NetName.StartsWith("PP", StringComparison.OrdinalIgnoreCase)).ToList();
            var aliasesOnPage = netMentions.Where(n => n.IsAlias && n.NetName.StartsWith("PP", StringComparison.OrdinalIgnoreCase)).ToList();

            foreach (var alias in aliasesOnPage)
            {
                // En la página 7, los alias (=PP...) están en una columna justo a la izquierda de su bloque base (dx entre 20 y 165 pt)
                string? matchedBase = null;
                double bestDist = double.MaxValue;

                foreach (var baseRail in baseRailsOnPage)
                {
                    double dx = baseRail.Word.Left - alias.Word.Left;
                    double dy = Math.Abs(baseRail.Word.Cy - alias.Word.Cy);
                    if (dx >= -15.0 && dx <= 165.0 && dy <= 55.0)
                    {
                        double dist = Math.Abs(dx) * 0.6 + dy;
                        if (dist < bestDist)
                        {
                            bestDist = dist;
                            matchedBase = baseRail.NetName;
                        }
                    }
                }

                if (matchedBase != null && !string.Equals(alias.NetName, matchedBase, StringComparison.OrdinalIgnoreCase))
                {
                    index.NetAliases.TryAdd(alias.NetName, matchedBase);
                }
            }
        }
    }

    private static void ExtractPageComponentAttributes2D(
        List<PositionedWord> words,
        HashSet<string> knownCompIds,
        SchematicIndex index,
        HashSet<string> stuffedComponents,
        HashSet<string> noStuffCandidates)
    {
        // Localizar todos los componentes válidos en la página (excluyendo listas separadas por comas de tablas BOM)
        var compWords = new List<(string CompId, PositionedWord Word)>();

        foreach (var w in words)
        {
            string txt = w.Text.Trim();
            if (!ComponentRegex.IsMatch(txt)) continue;

            string compId = txt.ToUpperInvariant();
            if (knownCompIds.Count > 0 && !knownCompIds.Contains(compId)) continue;

            // Descartar si está en una fila de tabla BOM al pie de página (tiene un part number 999S9999 en la misma Y)
            bool inBomTable = words.Any(other =>
                Math.Abs(other.Cy - w.Cy) < 4.0 &&
                Regex.IsMatch(other.Text, @"^\d{3}[S-]\d{4}$", RegexOptions.IgnoreCase));
            if (inBomTable) continue;

            compWords.Add((compId, w));
        }

        if (compWords.Count == 0) return;

        // Asignar cada palabra de la página a su componente más cercano (celda de Voronoi local dentro de radio máximo)
        var neighborhood = new Dictionary<int, List<PositionedWord>>();
        for (int cIdx = 0; cIdx < compWords.Count; cIdx++)
        {
            neighborhood[cIdx] = new List<PositionedWord>();
        }

        foreach (var w in words)
        {
            if (ComponentRegex.IsMatch(w.Text.Trim())) continue;

            int nearestIdx = -1;
            double nearestDist = double.MaxValue;

            for (int cIdx = 0; cIdx < compWords.Count; cIdx++)
            {
                var cw = compWords[cIdx].Word;
                double dx = Math.Abs(w.Cx - cw.Cx);
                double dy = Math.Abs(w.Cy - cw.Cy);

                if (dx <= 42.0 && dy <= 34.0)
                {
                    double dist = Math.Sqrt(dx * dx + dy * dy);
                    if (dist < nearestDist)
                    {
                        nearestDist = dist;
                        nearestIdx = cIdx;
                    }
                }
            }

            if (nearestIdx >= 0)
            {
                neighborhood[nearestIdx].Add(w);
            }
        }

        for (int cIdx = 0; cIdx < compWords.Count; cIdx++)
        {
            string compId = compWords[cIdx].CompId;
            var localWords = neighborhood[cIdx]
                .OrderBy(w => Math.Sqrt(Math.Pow(w.Cx - compWords[cIdx].Word.Cx, 2) + Math.Pow(w.Cy - compWords[cIdx].Word.Cy, 2)))
                .ToList();

            // 1. Verificar NOSTUFF / NO STUFF (estrictamente en mayúsculas para no confundir con notas en minúscula)
            bool hasNoStuff = localWords.Any(w => w.Text.Equals("NOSTUFF", StringComparison.Ordinal))
                              || HasAdjacentNoStuff(localWords);

            if (hasNoStuff)
            {
                noStuffCandidates.Add(compId);
            }
            else
            {
                stuffedComponents.Add(compId);
            }

            // 2. Extraer valor eléctrico según el tipo de componente
            if (!index.ComponentValues.ContainsKey(compId))
            {
                string? extractedValue = ExtractComponentValueFromNeighborhood(compId, localWords);
                if (!string.IsNullOrWhiteSpace(extractedValue))
                {
                    index.ComponentValues[compId] = extractedValue;
                }
            }
        }
    }

    private static bool HasAdjacentNoStuff(List<PositionedWord> localWords)
    {
        for (int i = 0; i < localWords.Count; i++)
        {
            if (!localWords[i].Text.Equals("NO", StringComparison.Ordinal)) continue;
            for (int j = 0; j < localWords.Count; j++)
            {
                if (i == j) continue;
                if (localWords[j].Text.Equals("STUFF", StringComparison.Ordinal) &&
                    Math.Abs(localWords[i].Cy - localWords[j].Cy) < 4.5 &&
                    Math.Abs(localWords[j].Left - localWords[i].Right) < 12.0)
                {
                    return true;
                }
            }
        }
        return false;
    }

    private static string? ExtractComponentValueFromNeighborhood(string compId, List<PositionedWord> localWords)
    {
        if (string.IsNullOrEmpty(compId) || localWords.Count == 0) return null;
        char prefix = char.ToUpperInvariant(compId[0]);

        switch (prefix)
        {
            case 'C':
            {
                string? cap = localWords.Select(w => w.Text.Trim()).FirstOrDefault(t => CapValueRegex.IsMatch(t));
                if (cap == null) return null;
                string? volt = localWords.Select(w => w.Text.Trim()).FirstOrDefault(t => CapVoltageRegex.IsMatch(t));
                return volt != null ? $"{cap.ToUpperInvariant()} {volt.ToUpperInvariant()}" : cap.ToUpperInvariant();
            }

            case 'F':
            {
                return localWords.Select(w => w.Text.Trim()).FirstOrDefault(t => FuseValueRegex.IsMatch(t))?.ToUpperInvariant();
            }

            case 'L':
            {
                return localWords.Select(w => w.Text.Trim()).FirstOrDefault(t => InductorValueRegex.IsMatch(t))?.ToUpperInvariant();
            }

            case 'R':
            {
                foreach (var w in localWords)
                {
                    string t = w.Text.Trim();
                    if (PackageAndMetaTokens.Contains(t)) continue;
                    if (t.EndsWith("%", StringComparison.Ordinal) || t.EndsWith("W", StringComparison.OrdinalIgnoreCase)) continue;
                    if (t.Contains('=') || t.Contains(':')) continue;

                    if (ResistorValueRegex.IsMatch(t))
                    {
                        return t.Equals("SHORT", StringComparison.OrdinalIgnoreCase) ? "0 Ω (SHORT)" : $"{t.ToUpperInvariant()} Ω";
                    }
                }
                return null;
            }

            case 'U' or 'Q' or 'D':
            {
                foreach (var w in localWords)
                {
                    string t = w.Text.Trim();
                    if (t.Length < 4 || PackageAndMetaTokens.Contains(t)) continue;
                    if (t.Contains('=') || t.Contains(':') || t.EndsWith("%", StringComparison.Ordinal)) continue;
                    if (Regex.IsMatch(t, @"^[A-Z]{2,6}[0-9]{2,6}[A-Z0-9_-]*$", RegexOptions.IgnoreCase) &&
                        !ComponentRegex.IsMatch(t))
                    {
                        return t.ToUpperInvariant();
                    }
                }
                return null;
            }

            default:
                return null;
        }
    }

    private static void AddEntry(
        Dictionary<string, List<(int PageNumber, string Snippet)>> dict,
        string key,
        int pageNumber,
        string snippet)
    {
        if (!dict.TryGetValue(key, out var list))
        {
            list = new List<(int PageNumber, string Snippet)>();
            dict[key] = list;
        }

        if (list.Count < 8 && !list.Any(x => x.PageNumber == pageNumber && x.Snippet == snippet))
        {
            list.Add((pageNumber, snippet));
        }
    }
}
