namespace OpenBoardView.AI.Services;

public static class FileRoleClassifier
{
    private static readonly string[] PhysicalCadExtensions = { ".brd", ".pcb", ".cad", ".fz", ".bdv" };

    public static string Classify(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
            return "AUX_DOC";

        var ext = Path.GetExtension(fileName).ToLowerInvariant();
        var lowerName = Path.GetFileNameWithoutExtension(fileName).ToLowerInvariant();

        if (PhysicalCadExtensions.Contains(ext))
            return "PHYSICAL_CAD";

        if (ext == ".pdf")
        {
            if (lowerName.Contains("dimension") || 
                lowerName.Contains("boardview") || 
                lowerName.Contains("manual") || 
                lowerName.Contains("layout") || 
                lowerName.Contains("cota") ||
                lowerName.Contains("guide") ||
                lowerName.Contains("guia"))
            {
                return "AUX_DOC";
            }

            if (lowerName.Contains("schematic") || 
                lowerName.Contains("esquematico") || 
                lowerName.Contains("diagram") ||
                lowerName.Contains("esquema"))
            {
                if (lowerName.Contains("apr") || 
                    lowerName.Contains("old") || 
                    lowerName.Contains("rev") || 
                    lowerName.Contains("prelim") ||
                    lowerName.Contains("draft") ||
                    lowerName.Contains("alt"))
                {
                    return "SCHEMATIC_ALT";
                }

                return "SCHEMATIC_PRIMARY";
            }

            return "SCHEMATIC_PRIMARY";
        }

        return "AUX_DOC";
    }
}
