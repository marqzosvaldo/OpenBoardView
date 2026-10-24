namespace OpenBoardView.AI.Models;

public enum MountingSide
{
    Both,
    Bottom,
    Top
}

public enum ComponentType
{
    SMD,
    ThroughHole
}

public class Pin
{
    public int Number { get; set; }
    public string PinName { get; set; } = string.Empty;
    public int Probe { get; set; }
    public int X { get; set; }
    public int Y { get; set; }
    public MountingSide Side { get; set; }
    public string NetName { get; set; } = string.Empty;
    public string ComponentId { get; set; } = string.Empty;
}

public class Component
{
    public string Id { get; set; } = string.Empty;
    public int X { get; set; }
    public int Y { get; set; }
    public MountingSide Side { get; set; }
    public ComponentType PartType { get; set; }
    public int PinCount => Pins.Count;
    public string Quadrant { get; set; } = "Q1";
    public List<Pin> Pins { get; set; } = new();
    public string? SchematicContext { get; set; }
    public List<int> SchematicPages { get; set; } = new();

    public string Category => Id.Length > 0 ? char.ToUpperInvariant(Id[0]) switch
    {
        'C' => "Capacitor",
        'R' => "Resistor",
        'L' => "Inductor/Coil",
        'U' => "IC/Chip",
        'Q' => "Transistor/MOSFET",
        'D' => "Diode",
        'F' => "Fuse",
        'J' => "Connector",
        'Y' or 'X' => "Crystal/Oscillator",
        _ => "Component"
    } : "Unknown";
}

public class Net
{
    public string Name { get; set; } = string.Empty;
    public List<Pin> Pins { get; set; } = new();
    public string? SchematicContext { get; set; }
    public List<int> SchematicPages { get; set; } = new();

    public bool IsGround =>
        Name.Equals("GND", StringComparison.OrdinalIgnoreCase) ||
        Name.StartsWith("GND_", StringComparison.OrdinalIgnoreCase) ||
        Name.EndsWith("_GND", StringComparison.OrdinalIgnoreCase) ||
        Name.Contains("GND", StringComparison.OrdinalIgnoreCase) ||
        Name.Contains("VSS", StringComparison.OrdinalIgnoreCase);

    public bool IsUnconnected =>
        string.IsNullOrWhiteSpace(Name) ||
        Name.Equals("UNCONNECTED", StringComparison.OrdinalIgnoreCase) ||
        Name.Equals("NC", StringComparison.OrdinalIgnoreCase) ||
        Name.StartsWith("NC_", StringComparison.OrdinalIgnoreCase);
}

public class BoundingBox
{
    public int MinX { get; set; }
    public int MaxX { get; set; }
    public int MinY { get; set; }
    public int MaxY { get; set; }

    public int Width => Math.Max(1, MaxX - MinX);
    public int Height => Math.Max(1, MaxY - MinY);
    public double MidX => MinX + (Width / 2.0);
    public double MidY => MinY + (Height / 2.0);

    /// <summary>
    /// Divide la placa en una cuadrícula espacial 2x2:
    /// Q1: Top-Left (X < MidX, Y >= MidY)
    /// Q2: Top-Right (X >= MidX, Y >= MidY)
    /// Q3: Bottom-Left (X < MidX, Y < MidY)
    /// Q4: Bottom-Right (X >= MidX, Y < MidY)
    /// </summary>
    public string GetQuadrant(int x, int y)
    {
        bool isRight = x >= MidX;
        bool isTop = y >= MidY;

        if (!isRight && isTop) return "Q1";
        if (isRight && isTop) return "Q2";
        if (!isRight && !isTop) return "Q3";
        return "Q4";
    }
}

public class BoardTopology
{
    public string FileName { get; set; } = string.Empty;
    public bool WasEncrypted { get; set; }
    public BoundingBox BoundingBox { get; set; } = new();
    public List<Component> Components { get; set; } = new();
    public List<Net> Nets { get; set; } = new();
    public List<Pin> TestPoints { get; set; } = new();
    public List<(int X, int Y)> OutlinePoints { get; set; } = new();
}
