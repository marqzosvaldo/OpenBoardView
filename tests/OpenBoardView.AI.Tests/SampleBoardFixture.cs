using OpenBoardView.AI.Graph;
using OpenBoardView.AI.Knowledge;
using OpenBoardView.AI.Models;
using OpenBoardView.AI.Parsers;

namespace OpenBoardView.AI.Tests;

/// <summary>
/// Carga una sola vez la placa de referencia (MacBook Pro A1278 820-3115-B) incluida en Plan/.
/// El índice del PDF se omite a propósito: estas pruebas validan topología y conocimiento, y deben ser rápidas.
/// </summary>
public sealed class SampleBoardFixture
{
    public const string SampleBrdName = "Apple MacBook Pro 13 A1278 i5 2.5GHz 820-3115-B Boardview.brd";

    public string RepoRoot { get; }
    public string KnowledgeDir { get; }
    public BoardTopology Topology { get; }
    public CircuitGraphBuilder Graph { get; }

    private readonly Lazy<SchematicIndex> _schematic;

    /// <summary>Índice del esquemático PDF de Plan/. Se construye solo si una prueba lo pide (tarda unos segundos).</summary>
    public SchematicIndex Schematic => _schematic.Value;

    public SampleBoardFixture()
    {
        RepoRoot = FindRepoRoot();
        KnowledgeDir = Path.Combine(RepoRoot, "src", "OpenBoardView.AI", "Knowledge");
        string brd = Path.Combine(RepoRoot, "Plan", SampleBrdName);
        Topology = new BoardViewParser().ParseFile(brd);
        Graph = new CircuitGraphBuilder(Topology, null);

        _schematic = new Lazy<SchematicIndex>(() =>
        {
            string pdf = Directory.GetFiles(Path.Combine(RepoRoot, "Plan"), "*.pdf").First();
            return new SchematicPdfReader().ReadAndIndexPdf(pdf, Topology);
        });
    }

    public FlowKnowledgeBase KnowledgeFor(string boardFileName) =>
        FlowKnowledgeBase.LoadFromDirectories(new[] { KnowledgeDir }, boardFileName);

    public CircuitMazeDebugger DebuggerFor(string boardFileName) =>
        new(Graph, KnowledgeFor(boardFileName));

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "global.json")) &&
                Directory.Exists(Path.Combine(dir.FullName, "Plan")))
                return dir.FullName;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("No se encontró la raíz del repositorio (global.json + Plan/).");
    }
}
