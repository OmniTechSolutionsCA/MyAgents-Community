using System.Text;
using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.AI;
using MyAgents.Memory;

namespace MyAgents.Benchmarks.Memory;

[MemoryDiagnoser]
public class RagIngestionBenchmarks
{
    private sealed class DeterministicEmbeddingGenerator(int dimensions) : IEmbeddingGenerator<string, Embedding<float>>
    {
        private readonly Embedding<float> _cachedEmbedding = new(CreateNormalizedVector(dimensions));

        private static float[] CreateNormalizedVector(int dim)
        {
            var vec = new float[dim];
            var val = 1.0f / MathF.Sqrt(dim);
            Array.Fill(vec, val);
            return vec;
        }

        public EmbeddingGeneratorMetadata Metadata => new("Deterministic");

        public void Dispose() { }

        public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
            IEnumerable<string> values,
            EmbeddingGenerationOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            var count = values is ICollection<string> col ? col.Count : values.Count();
            var list = new GeneratedEmbeddings<Embedding<float>>();
            for (var i = 0; i < count; i++)
            {
                list.Add(_cachedEmbedding);
            }
            return Task.FromResult(list);
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
    }

    private string _documentText = null!;
    private RecursiveCharacterTextChunker _chunker = null!;
    private TextChunkingOptions _charOptions = null!;
    private TextChunkingOptions _tokenOptions = null!;
    private DefaultDocumentIngestor _ingestor = null!;
    private IngestDocument[] _batchDocs = null!;

    [GlobalSetup]
    public void Setup()
    {
        var sb = new StringBuilder(12_000);
        for (var p = 0; p < 40; p++)
        {
            sb.Append("Paragraph ");
            sb.Append(p);
            sb.Append(": Distributed agent workflows require deterministic checkpointing, lease fencing, and hybrid BM25 + dense vector retrieval. ");
            sb.Append("Reciprocal rank fusion merges candidate rankings across lexical and semantic indices without score calibration.\n\n");
        }

        _documentText = sb.ToString();
        _chunker = new RecursiveCharacterTextChunker();
        _charOptions = new TextChunkingOptions(ChunkSize: 1000, ChunkOverlap: 100);
        _tokenOptions = new TextChunkingOptions(ChunkSize: 1000, ChunkOverlap: 100, MaxTokens: 150);

        var embedder = new DeterministicEmbeddingGenerator(384);
        var vectorStore = new VectorMemoryStore(embedder);
        var retriever = new HybridMemoryRetriever(vectorStore, vectorStore);
        _ingestor = new DefaultDocumentIngestor(vectorStore, _chunker, retriever, vectorStore);

        _batchDocs =
        [
            new IngestDocument("doc://bench-1", _documentText, "Benchmark Doc 1", ChunkingOptions: _charOptions),
            new IngestDocument("doc://bench-2", _documentText, "Benchmark Doc 2", ChunkingOptions: _charOptions),
            new IngestDocument("doc://bench-3", _documentText, "Benchmark Doc 3", ChunkingOptions: _charOptions),
        ];
    }

    [Benchmark]
    public IReadOnlyList<TextChunk> Chunk_10KChars_CharacterBudget()
        => _chunker.Chunk(_documentText, _charOptions);

    [Benchmark]
    public IReadOnlyList<TextChunk> Chunk_10KChars_TokenBudget()
        => _chunker.Chunk(_documentText, _tokenOptions);

    [Benchmark]
    public async ValueTask<BatchDocumentIngestionResult> IngestBatch_ThreeDocuments()
        => await _ingestor.IngestBatchAsync("bench_rag", _batchDocs).ConfigureAwait(false);
}
