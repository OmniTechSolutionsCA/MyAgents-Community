using MyAgents.Core;

namespace MyAgents.Memory;

/// <summary>
/// Zero-dependency, allocation-free subword token estimator implementing <see cref="ITokenCounter"/>
/// and modeling modern BPE tokenizers (approx. 4 characters per alphanumeric subword token plus punctuation runs).
/// </summary>
public sealed class ApproximateTokenCounter : ITokenCounter
{
    /// <summary>
    /// Shared singleton instance.
    /// </summary>
    public static ApproximateTokenCounter Instance { get; } = new();

    /// <inheritdoc />
    public int CountTokens(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return CountTokens(text.AsSpan());
    }

    /// <summary>
    /// Counts the estimated number of tokens in <paramref name="text"/> without allocating.
    /// </summary>
    public static int CountTokens(ReadOnlySpan<char> text)
    {
        if (text.IsEmpty)
        {
            return 0;
        }

        var tokens = 0;
        var i = 0;
        while (i < text.Length)
        {
            var ch = text[i];
            if (char.IsWhiteSpace(ch))
            {
                i++;
                continue;
            }

            if (char.IsLetterOrDigit(ch))
            {
                var start = i;
                while (i < text.Length && char.IsLetterOrDigit(text[i]))
                {
                    i++;
                }

                var wordLen = i - start;
                tokens += (wordLen + 3) / 4;
            }
            else
            {
                tokens++;
                i++;
            }
        }

        return tokens;
    }

    /// <summary>
    /// Finds the maximum character length of a prefix of <paramref name="text"/> that does not exceed <paramref name="maxTokens"/>.
    /// Operates in a single allocation-free pass without binary search.
    /// </summary>
    /// <param name="text">The source character span.</param>
    /// <param name="maxTokens">The maximum token budget allowed.</param>
    /// <returns>The character count of the maximal prefix within the token budget.</returns>
    public static int FindMaxLengthForTokenBudget(ReadOnlySpan<char> text, int maxTokens)
    {
        if (text.IsEmpty || maxTokens <= 0)
        {
            return 0;
        }

        var tokens = 0;
        var i = 0;
        while (i < text.Length)
        {
            var ch = text[i];
            if (char.IsWhiteSpace(ch))
            {
                i++;
                continue;
            }

            if (char.IsLetterOrDigit(ch))
            {
                var start = i;
                while (i < text.Length && char.IsLetterOrDigit(text[i]))
                {
                    i++;
                }

                var wordLen = i - start;
                var wordTokens = (wordLen + 3) / 4;
                var remaining = maxTokens - tokens;
                if (wordTokens <= remaining)
                {
                    tokens += wordTokens;
                }
                else
                {
                    var allowedChars = Math.Min(wordLen, remaining * 4);
                    return start + allowedChars;
                }
            }
            else
            {
                if (tokens + 1 > maxTokens)
                {
                    return i;
                }

                tokens++;
                i++;
            }
        }

        return text.Length;
    }
}
