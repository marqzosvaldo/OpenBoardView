using System.Text;
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
}

/// <summary>
/// Fase 2: Extractor de Semántica de esquemáticos en PDF usando UglyToad.PdfPig.
/// Construye un índice inverso que mapea cada ID de componente (ej. U7000, R7040) y Red (ej. PPBUS_G3H)
/// al texto circundante en el PDF (valores, voltajes, roles y páginas).
/// </summary>
public class SchematicPdfReader
{
    private static readonly Regex ComponentRegex = new(
        @"\b([CRLUQDFJYX][0-9]{3,5}[A-Z]?)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex PowerNetRegex = new(
        @"\b(PP[A-Z0-9_]{3,}|SMC_[A-Z0-9_]{3,}|PM_[A-Z0-9_]{3,}|PLT_[A-Z0-9_]{3,}|ALL_SYS_PWRGD|CHGR_[A-Z0-9_]{3,})\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

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
            ? new HashSet<string>(topology.Nets.Where(n => !n.IsGround && !n.IsUnconnected).Select(n => n.Name), StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        using var document = PdfDocument.Open(pdfPath);
        index.TotalPages = document.NumberOfPages;

        foreach (Page page in document.GetPages())
        {
            var words = page.GetWords().ToList();
            if (words.Count == 0) continue;

            // Agrupar palabras por renglones aproximados (coordenada Y similar)
            var lines = words
                .GroupBy(w => (int)Math.Round(w.BoundingBox.Bottom / 6.0))
                .OrderByDescending(g => g.Key)
                .Select(g => string.Join(" ", g.OrderBy(w => w.BoundingBox.Left).Select(w => w.Text)))
                .ToList();

            string fullPageText = string.Join("\n", lines);
            index.PageTexts[page.Number] = fullPageText;

            for (int i = 0; i < lines.Count; i++)
            {
                string currentLine = lines[i];

                // Crear ventana de contexto (línea anterior + actual + siguiente)
                int startIdx = Math.Max(0, i - 1);
                int endIdx = Math.Min(lines.Count - 1, i + 1);
                string contextWindow = string.Join(" | ", lines.Skip(startIdx).Take(endIdx - startIdx + 1));
                if (contextWindow.Length > 260)
                    contextWindow = contextWindow.Substring(0, 260) + "...";

                // 1. Indexar componentes detectados en la línea
                foreach (Match match in ComponentRegex.Matches(currentLine))
                {
                    string compId = match.Groups[1].Value.ToUpperInvariant();
                    if (knownCompIds.Count == 0 || knownCompIds.Contains(compId))
                    {
                        AddEntry(index.ComponentContext, compId, page.Number, contextWindow);
                    }
                }

                // 2. Indexar redes eléctricas detectadas en la línea
                foreach (Match match in PowerNetRegex.Matches(currentLine))
                {
                    string netName = match.Groups[1].Value.ToUpperInvariant();
                    if (knownNets.Count == 0 || knownNets.Contains(netName))
                    {
                        AddEntry(index.NetContext, netName, page.Number, contextWindow);
                    }
                }
            }
        }

        // Enriquecer directamente el BoardTopology si se proporcionó
        if (topology != null)
        {
            foreach (var comp in topology.Components)
            {
                if (index.ComponentContext.TryGetValue(comp.Id, out var entries))
                {
                    comp.SchematicPages = entries.Select(e => e.PageNumber).Distinct().OrderBy(p => p).ToList();
                    comp.SchematicContext = string.Join(" // ", entries.Take(4).Select(e => $"[Pág {e.PageNumber}] {e.Snippet}"));
                }
            }

            foreach (var net in topology.Nets)
            {
                if (index.NetContext.TryGetValue(net.Name, out var entries))
                {
                    net.SchematicPages = entries.Select(e => e.PageNumber).Distinct().OrderBy(p => p).ToList();
                    net.SchematicContext = string.Join(" // ", entries.Take(4).Select(e => $"[Pág {e.PageNumber}] {e.Snippet}"));
                }
            }
        }

        return index;
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
