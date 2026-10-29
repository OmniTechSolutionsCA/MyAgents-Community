using System.Buffers;
using System.Text.RegularExpressions;

namespace MyAgents.Memory;

/// <summary>
/// High-performance in-memory BM25 (Best Matching 25) sparse lexical index and ranking engine.
/// Computes probabilistic relevance using term frequency saturation and document length normalization.
/// </summary>
public sealed partial class BM25Index
{
    private readonly struct Posting(int docIndex, int freq)
    {
        public int DocIndex { get; } = docIndex;
        public int Freq { get; } = freq;
    }

    [GeneratedRegex(@"[a-zA-Z0-9_\-\.]{2,}")]
    private static partial Regex GetTokenRegex();

    private readonly double _k1;
    private readonly double _b;
    private readonly Lock _sync = new();

    private readonly Dictionary<string, int> _docIndexMap = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _docIds = [];
    private readonly List<int> _docLengths = [];
    private readonly List<bool> _docActive = [];
    private readonly List<string[]> _docTokens = [];
    private readonly Dictionary<string, List<Posting>> _invertedIndex = new(StringComparer.OrdinalIgnoreCase);

    private int _activeDocCount;
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
            lock (_sync) return _activeDocCount;
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
            if (_docIndexMap.TryGetValue(docId, out var existingIdx))
            {
                RemoveInternal(docId, existingIdx);
            }

            var docIdx = _docIds.Count;
            _docIds.Add(docId);
            _docLengths.Add(tokens.Length);
            _docActive.Add(true);
            _docTokens.Add(tokens);
            _docIndexMap[docId] = docIdx;
            _activeDocCount++;
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
                    postingList = [];
                    _invertedIndex[term] = postingList;
                }
                postingList.Add(new Posting(docIdx, freq));
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
            if (!_docIndexMap.TryGetValue(docId, out var docIdx) || !_docActive[docIdx])
            {
                return false;
            }

            RemoveInternal(docId, docIdx);
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
            _docIndexMap.Clear();
            _docIds.Clear();
            _docLengths.Clear();
            _docActive.Clear();
            _docTokens.Clear();
            _invertedIndex.Clear();
            _activeDocCount = 0;
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
            var nDocs = _activeDocCount;
            if (nDocs == 0 || _totalDocLength == 0)
            {
                return [];
            }

            var avgDocLength = (double)_totalDocLength / nDocs;
            var maxDocIndex = _docIds.Count;

            // Deduplicate query terms for IDF
            var distinctTerms = new List<string>(queryTokens.Length);
            for (var i = 0; i < queryTokens.Length; i++)
            {
                var t = queryTokens[i];
                var exists = false;
                for (var j = 0; j < distinctTerms.Count; j++)
                {
                    if (string.Equals(distinctTerms[j], t, StringComparison.OrdinalIgnoreCase))
                    {
                        exists = true;
                        break;
                    }
                }
                if (!exists) distinctTerms.Add(t);
            }

            var scores = ArrayPool<double>.Shared.Rent(maxDocIndex);
            Array.Clear(scores, 0, maxDocIndex);

            try
            {
                var anyMatches = false;
                var k1Factor = _k1 * (1.0 - _b);
                var bFactor = _k1 * _b / avgDocLength;

                for (var t = 0; t < distinctTerms.Count; t++)
                {
                    var term = distinctTerms[t];
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

                    var numeratorBase = idf * (_k1 + 1.0);

                    for (var p = 0; p < postings.Count; p++)
                    {
                        var posting = postings[p];
                        var docIdx = posting.DocIndex;
                        if (!_docActive[docIdx])
                        {
                            continue;
                        }

                        var docLen = _docLengths[docIdx];
                        var freq = posting.Freq;
                        var denominator = freq + k1Factor + bFactor * docLen;
                        scores[docIdx] += (numeratorBase * freq) / denominator;
                        anyMatches = true;
                    }
                }

                if (!anyMatches)
                {
                    return [];
                }

                var minHeap = new PriorityQueue<int, double>(maxResults);
                for (var i = 0; i < maxDocIndex; i++)
                {
                    var s = scores[i];
                    if (s <= 0.0) continue;

                    if (minHeap.Count < maxResults)
                    {
                        minHeap.Enqueue(i, s);
                    }
                    else
                    {
                        minHeap.TryPeek(out _, out var minScore);
                        if (s > minScore)
                        {
                            minHeap.DequeueEnqueue(i, s);
                        }
                    }
                }

                var results = new (string DocId, double Score)[minHeap.Count];
                for (var i = results.Length - 1; i >= 0; i--)
                {
                    minHeap.TryDequeue(out var docIdx, out var score);
                    results[i] = (_docIds[docIdx], score);
                }
                return results;
            }
            finally
            {
                ArrayPool<double>.Shared.Return(scores);
            }
        }
    }

    private void RemoveInternal(string docId, int docIdx)
    {
        _docActive[docIdx] = false;
        _totalDocLength -= _docLengths[docIdx];
        _docLengths[docIdx] = 0;
        _activeDocCount--;
        _docIndexMap.Remove(docId);

        var tokens = _docTokens[docIdx];
        _docTokens[docIdx] = [];

        var distinctTerms = tokens.Distinct(StringComparer.OrdinalIgnoreCase);
        foreach (var term in distinctTerms)
        {
            if (_invertedIndex.TryGetValue(term, out var postings))
            {
                for (var i = postings.Count - 1; i >= 0; i--)
                {
                    if (postings[i].DocIndex == docIdx)
                    {
                        postings.RemoveAt(i);
                        break;
                    }
                }
                if (postings.Count == 0)
                {
                    _invertedIndex.Remove(term);
                }
            }
        }

        if (_activeDocCount == 0)
        {
            Clear();
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
            var token = string.Create(match.Length, (text, match.Index), static (span, state) =>
            {
                var src = state.text.AsSpan(state.Index, span.Length);
                for (var i = 0; i < span.Length; i++)
                {
                    span[i] = char.ToLowerInvariant(src[i]);
                }
            });
            list.Add(token);
        }

        return list.Count == 0 ? [] : list.ToArray();
    }
}
