using System.Text.RegularExpressions;

namespace MyAgents.Memory;

/// <summary>
/// High-performance in-memory BM25 (Best Matching 25) sparse lexical index and ranking engine.
/// Computes probabilistic relevance using term frequency saturation and document length normalization.
/// </summary>
public sealed partial class BM25Index
{
    [GeneratedRegex(@"[a-zA-Z0-9_\-\.]{2,}")]
    private static partial Regex GetTokenRegex();

    private readonly double _k1;
    private readonly double _b;
    private readonly Lock _sync = new();

    // docId -> list of tokens
    private readonly Dictionary<string, string[]> _documents = new(StringComparer.OrdinalIgnoreCase);
    // term -> (docId -> frequency)
    private readonly Dictionary<string, Dictionary<string, int>> _invertedIndex = new(StringComparer.OrdinalIgnoreCase);
    private long _totalDocLength;

    public BM25Index(double k1 = 1.2, double b = 0.75)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(k1);
        ArgumentOutOfRangeException.ThrowIfNegative(b);
        _k1 = k1;
        _b = b;
    }

    /// <summary>
    /// Total number of indexed documents.
    /// </summary>
    public int DocumentCount
    {
        get
        {
            lock (_sync) return _documents.Count;
        }
    }

    /// <summary>
    /// Indexes or updates a document with the given unique identifier and text content.
    /// </summary>
    public void AddOrUpdate(string docId, string content)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(docId);
        ArgumentNullException.ThrowIfNull(content);

        var tokens = Tokenize(content);

        lock (_sync)
        {
            if (_documents.TryGetValue(docId, out var existingTokens))
            {
                RemoveInternal(docId, existingTokens);
            }

            _documents[docId] = tokens;
            _totalDocLength += tokens.Length;

            // Compute term frequencies for this doc
            var termFreqs = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var token in tokens)
            {
                termFreqs[token] = termFreqs.TryGetValue(token, out var count) ? count + 1 : 1;
            }

            foreach (var (term, freq) in termFreqs)
            {
                if (!_invertedIndex.TryGetValue(term, out var postingList))
                {
                    postingList = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                    _invertedIndex[term] = postingList;
                }
                postingList[docId] = freq;
            }
        }
    }

    /// <summary>
    /// Removes a document from the BM25 index.
    /// </summary>
    public bool Remove(string docId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(docId);

        lock (_sync)
        {
            if (!_documents.TryGetValue(docId, out var tokens))
            {
                return false;
            }

            RemoveInternal(docId, tokens);
            return true;
        }
    }

    /// <summary>
    /// Clears all indexed documents.
    /// </summary>
    public void Clear()
    {
        lock (_sync)
        {
            _documents.Clear();
            _invertedIndex.Clear();
            _totalDocLength = 0;
        }
    }

    /// <summary>
    /// Executes a BM25 sparse search over the indexed documents.
    /// </summary>
    /// <param name="query">The search query.</param>
    /// <param name="maxResults">Maximum number of scored results to return.</param>
    /// <returns>Ranked list of document identifiers with their non-negative BM25 scores.</returns>
    public IReadOnlyList<(string DocId, double Score)> Search(string query, int maxResults = 10)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxResults);

        var queryTokens = Tokenize(query);
        if (queryTokens.Length == 0)
        {
            return [];
        }

        lock (_sync)
        {
            var nDocs = _documents.Count;
            if (nDocs == 0 || _totalDocLength == 0)
            {
                return [];
            }

            var avgDocLength = (double)_totalDocLength / nDocs;
            var docScores = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

            // Deduplicate query terms for IDF
            var distinctQueryTerms = queryTokens.Distinct(StringComparer.OrdinalIgnoreCase);

            foreach (var term in distinctQueryTerms)
            {
                if (!_invertedIndex.TryGetValue(term, out var postings))
                {
                    continue;
                }

                var docFreq = postings.Count;
                // Standard Robertson-Spärck Jones IDF with smoothing
                var idf = Math.Log(1.0 + (nDocs - docFreq + 0.5) / (docFreq + 0.5));
                if (idf <= 0.0)
                {
                    continue;
                }

                foreach (var (docId, freq) in postings)
                {
                    var docLen = _documents[docId].Length;
                    var numerator = freq * (_k1 + 1.0);
                    var denominator = freq + _k1 * (1.0 - _b + _b * (docLen / avgDocLength));
                    var termScore = idf * (numerator / denominator);

                    docScores[docId] = docScores.TryGetValue(docId, out var current)
                        ? current + termScore
                        : termScore;
                }
            }

            if (docScores.Count == 0)
            {
                return [];
            }

            if (docScores.Count <= maxResults)
            {
                return docScores
                    .OrderByDescending(kv => kv.Value)
                    .Select(kv => (kv.Key, kv.Value))
                    .ToArray();
            }

            var minHeap = new PriorityQueue<string, double>(maxResults);
            foreach (var (docId, score) in docScores)
            {
                if (minHeap.Count < maxResults)
                {
                    minHeap.Enqueue(docId, score);
                }
                else
                {
                    minHeap.TryPeek(out _, out var minScore);
                    if (score > minScore)
                    {
                        minHeap.DequeueEnqueue(docId, score);
                    }
                }
            }

            var results = new (string DocId, double Score)[minHeap.Count];
            for (var i = results.Length - 1; i >= 0; i--)
            {
                minHeap.TryDequeue(out var docId, out var score);
                results[i] = (docId!, score);
            }
            return results;
        }
    }

    private void RemoveInternal(string docId, string[] tokens)
    {
        _documents.Remove(docId);
        _totalDocLength -= tokens.Length;

        var distinctTerms = tokens.Distinct(StringComparer.OrdinalIgnoreCase);
        foreach (var term in distinctTerms)
        {
            if (_invertedIndex.TryGetValue(term, out var postings))
            {
                postings.Remove(docId);
                if (postings.Count == 0)
                {
                    _invertedIndex.Remove(term);
                }
            }
        }
    }

    private static string[] Tokenize(string text)
    {
        if (string.IsNullOrEmpty(text))
            return [];

        var regex = GetTokenRegex();
        var matches = regex.EnumerateMatches(text.AsSpan());
        var list = new List<string>();
        foreach (var match in matches)
        {
            list.Add(text.Substring(match.Index, match.Length).ToLowerInvariant());
        }

        return list.Count == 0 ? [] : list.ToArray();
    }
}
