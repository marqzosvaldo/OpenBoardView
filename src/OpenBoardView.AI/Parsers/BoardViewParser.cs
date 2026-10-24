using System.Text;
using OpenBoardView.AI.Models;

namespace OpenBoardView.AI.Parsers;

/// <summary>
/// Fase 1: Extractor de Topología para archivos .brd (TestLink / Apple / Lenovo BRD).
/// Soporta desencriptación bit-shift nativa (firma 0x23 0xE2 0x63 0x28), extracción de Parts, Pins, Nails,
/// cálculo de BoundingBox y asignación espacial por cuadrantes (Q1-Q4).
/// </summary>
public class BoardViewParser
{
    private static readonly byte[] EncodedHeader = { 0x23, 0xE2, 0x63, 0x28 };

    public BoardTopology ParseFile(string filePath)
    {
        if (!File.Exists(filePath))
            throw new FileNotFoundException($"No se encontró el archivo BRD en: {filePath}");

        byte[] rawBytes = File.ReadAllBytes(filePath);
        return ParseBytes(rawBytes, Path.GetFileName(filePath));
    }

    public BoardTopology ParseBytes(byte[] rawBytes, string fileName = "board.brd")
    {
        if (rawBytes.Length <= 4)
            throw new InvalidDataException("El archivo BRD está vacío o dañado.");

        bool isEncoded = rawBytes[0] == EncodedHeader[0] &&
                         rawBytes[1] == EncodedHeader[1] &&
                         rawBytes[2] == EncodedHeader[2] &&
                         rawBytes[3] == EncodedHeader[3];

        byte[] buffer = new byte[rawBytes.Length];
        Array.Copy(rawBytes, buffer, rawBytes.Length);

        if (isEncoded)
        {
            for (int i = 0; i < buffer.Length; i++)
            {
                byte x = buffer[i];
                if (x != (byte)'\r' && x != (byte)'\n' && x != 0)
                {
                    int c = x;
                    buffer[i] = (byte)(~(((c >> 6) & 3) | (c << 2)) & 0xFF);
                }
            }
        }

        string content = Encoding.Latin1.GetString(buffer);
        var lines = content.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.RemoveEmptyEntries);

        var topology = new BoardTopology
        {
            FileName = fileName,
            WasEncrypted = isEncoded
        };

        int currentBlock = 0;
        int numFormat = 0, numParts = 0, numPins = 0, numNails = 0;

        var rawParts = new List<(string Name, MountingSide Side, ComponentType Type, int EndOfPins)>();
        var rawPins = new List<(int X, int Y, int Probe, int PartIndex, string NetName)>();
        var rawNails = new List<(int Probe, int X, int Y, MountingSide Side, string NetName)>();
        var nailsToNets = new Dictionary<int, string>();

        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();
            if (string.IsNullOrEmpty(line)) continue;

            if (line.Equals("str_length:", StringComparison.OrdinalIgnoreCase)) { currentBlock = 1; continue; }
            if (line.Equals("var_data:", StringComparison.OrdinalIgnoreCase)) { currentBlock = 2; continue; }
            if (line.Equals("Format:", StringComparison.OrdinalIgnoreCase)) { currentBlock = 3; continue; }
            if (line.Equals("Parts:", StringComparison.OrdinalIgnoreCase) || line.Equals("Pins1:", StringComparison.OrdinalIgnoreCase)) { currentBlock = 4; continue; }
            if (line.Equals("Pins:", StringComparison.OrdinalIgnoreCase) || line.Equals("Pins2:", StringComparison.OrdinalIgnoreCase)) { currentBlock = 5; continue; }
            if (line.Equals("Nails:", StringComparison.OrdinalIgnoreCase)) { currentBlock = 6; continue; }

            var tokens = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length == 0) continue;

            switch (currentBlock)
            {
                case 2: // var_data
                    if (tokens.Length >= 4)
                    {
                        int.TryParse(tokens[0], out numFormat);
                        int.TryParse(tokens[1], out numParts);
                        int.TryParse(tokens[2], out numPins);
                        int.TryParse(tokens[3], out numNails);
                    }
                    break;

                case 3: // Format (Outline points)
                    if ( (numFormat == 0 || topology.OutlinePoints.Count < numFormat) && tokens.Length >= 2)
                    {
                        if (int.TryParse(tokens[0], out int fx) && int.TryParse(tokens[1], out int fy))
                        {
                            topology.OutlinePoints.Add((fx, fy));
                        }
                    }
                    break;

                case 4: // Parts
                    if ((numParts == 0 || rawParts.Count < numParts) && tokens.Length >= 3)
                    {
                        string partName = tokens[0];
                        int.TryParse(tokens[1], out int tmp);
                        int.TryParse(tokens[2], out int endOfPins);

                        var partType = (tmp & 0xC) != 0 ? ComponentType.SMD : ComponentType.ThroughHole;
                        var side = MountingSide.Both;
                        if (tmp == 1 || (tmp >= 4 && tmp < 8)) side = MountingSide.Top;
                        else if (tmp == 2 || tmp >= 8) side = MountingSide.Bottom;

                        rawParts.Add((partName, side, partType, endOfPins));
                    }
                    break;

                case 5: // Pins
                    if ((numPins == 0 || rawPins.Count < numPins) && tokens.Length >= 4)
                    {
                        int.TryParse(tokens[0], out int px);
                        int.TryParse(tokens[1], out int py);
                        int.TryParse(tokens[2], out int probe);
                        int.TryParse(tokens[3], out int partIdx);
                        string netName = tokens.Length >= 5 ? tokens[4] : string.Empty;

                        rawPins.Add((px, py, probe, partIdx, netName));
                    }
                    break;

                case 6: // Nails
                    if (tokens.Length >= 5)
                    {
                        int.TryParse(tokens[0], out int probe);
                        int.TryParse(tokens[1], out int nx);
                        int.TryParse(tokens[2], out int ny);
                        int.TryParse(tokens[3], out int nside);
                        string netName = tokens[4];
                        nailsToNets[probe] = netName;
                        rawNails.Add((probe, nx, ny, nside == 1 ? MountingSide.Top : MountingSide.Bottom, netName));
                    }
                    break;
            }
        }

        // Construir lista de componentes (1-indexed en el formato BRD)
        var components = new List<Component>(rawParts.Count);
        foreach (var rp in rawParts)
        {
            components.Add(new Component
            {
                Id = rp.Name,
                Side = rp.Side,
                PartType = rp.Type
            });
        }

        var netsDict = new Dictionary<string, Net>(StringComparer.OrdinalIgnoreCase);
        var pinCountersPerPart = new Dictionary<int, int>();

        foreach (var rpin in rawPins)
        {
            if (rpin.PartIndex < 1 || rpin.PartIndex > components.Count)
                continue;

            var comp = components[rpin.PartIndex - 1];
            string netName = rpin.NetName;

            if (string.IsNullOrEmpty(netName) && nailsToNets.TryGetValue(rpin.Probe, out var mappedNet))
            {
                netName = mappedNet;
            }
            if (string.IsNullOrEmpty(netName))
            {
                netName = "UNCONNECTED";
            }

            pinCountersPerPart.TryGetValue(rpin.PartIndex, out int currentPinNum);
            currentPinNum++;
            pinCountersPerPart[rpin.PartIndex] = currentPinNum;

            var pin = new Pin
            {
                Number = currentPinNum,
                PinName = currentPinNum.ToString(),
                Probe = rpin.Probe,
                X = rpin.X,
                Y = rpin.Y,
                Side = comp.Side,
                NetName = netName,
                ComponentId = comp.Id
            };

            comp.Pins.Add(pin);

            if (!netsDict.TryGetValue(netName, out var net))
            {
                net = new Net { Name = netName };
                netsDict[netName] = net;
            }
            net.Pins.Add(pin);
        }

        // Calcular centroide (X, Y) de cada componente en base a sus pines
        foreach (var comp in components)
        {
            if (comp.Pins.Count > 0)
            {
                comp.X = (int)Math.Round(comp.Pins.Average(p => p.X));
                comp.Y = (int)Math.Round(comp.Pins.Average(p => p.Y));
            }
        }

        // Calcular BoundingBox de la placa
        var validComponents = components.Where(c => c.Pins.Count > 0).ToList();
        var bbox = new BoundingBox();

        if (validComponents.Count > 0)
        {
            bbox.MinX = validComponents.Min(c => c.Pins.Min(p => p.X));
            bbox.MaxX = validComponents.Max(c => c.Pins.Max(p => p.X));
            bbox.MinY = validComponents.Min(c => c.Pins.Min(p => p.Y));
            bbox.MaxY = validComponents.Max(c => c.Pins.Max(p => p.Y));
        }
        else if (topology.OutlinePoints.Count > 0)
        {
            bbox.MinX = topology.OutlinePoints.Min(p => p.X);
            bbox.MaxX = topology.OutlinePoints.Max(p => p.X);
            bbox.MinY = topology.OutlinePoints.Min(p => p.Y);
            bbox.MaxY = topology.OutlinePoints.Max(p => p.Y);
        }

        // Asignar cuadrante espacial (Q1, Q2, Q3, Q4) a cada componente
        foreach (var comp in components)
        {
            comp.Quadrant = bbox.GetQuadrant(comp.X, comp.Y);
        }

        topology.BoundingBox = bbox;
        topology.Components = components;
        topology.Nets = netsDict.Values.OrderBy(n => n.Name).ToList();
        topology.TestPoints = rawNails.Select(n => new Pin
        {
            Number = n.Probe,
            PinName = $"TP{n.Probe}",
            Probe = n.Probe,
            X = n.X,
            Y = n.Y,
            Side = n.Side,
            NetName = n.NetName,
            ComponentId = "TESTPOINT"
        }).ToList();

        return topology;
    }
}
