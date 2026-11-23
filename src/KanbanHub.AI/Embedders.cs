using System.ClientModel;
using System.Text.RegularExpressions;
using KanbanHub.Domain.Ai;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace KanbanHub.AI;

/// <summary>
/// Embeds text with an Azure OpenAI embedding deployment through <c>IEmbeddingGenerator</c> (section 11.2).
/// A vector of another size than the database column holds is dropped and logged: the deployment is the wrong one.
/// </summary>
public sealed partial class EmbeddingModel(
    IEmbeddingGenerator<string, Embedding<float>> generator, string deployment, ILogger<EmbeddingModel> logger) : IEmbedder
{
    public async Task<TextEmbedding?> EmbedAsync(string text, CancellationToken cancellationToken)
    {
        try
        {
            GeneratedEmbeddings<Embedding<float>> generated = await generator.GenerateAsync([text], cancellationToken: cancellationToken);
            float[] vector = generated[0].Vector.ToArray();
            if (vector.Length == DuplicateRule.Dimensions)
                return new TextEmbedding(vector, deployment, (int)Math.Min(int.MaxValue, generated.Usage?.TotalTokenCount ?? 0));

            LogWrongSize(logger, deployment, vector.Length, DuplicateRule.Dimensions);
            return null;
        }
        catch (Exception refused) when (refused is ClientResultException or HttpRequestException)
        {
            LogNoEmbedding(logger, refused, deployment);
            return null;
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Deployment {Deployment} returns vectors of {Actual} dimensions; the database holds {Expected}. Nothing was stored.")]
    private static partial void LogWrongSize(ILogger logger, string deployment, int actual, int expected);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Deployment {Deployment} gave no embedding; the card is not compared this time.")]
    private static partial void LogNoEmbedding(ILogger logger, Exception exception, string deployment);
}

/// <summary>Stands where an embedding model would be when none is configured: nothing is embedded, nothing is suggested.</summary>
public sealed class NoEmbedder : IEmbedder
{
    public Task<TextEmbedding?> EmbedAsync(string text, CancellationToken cancellationToken) => Task.FromResult<TextEmbedding?>(null);
}

/// <summary>
/// Stands in for the embedding model on a development machine and in tests: each word adds to one of the vector's
/// positions, so two texts are as close as the words they share. Good enough to see near-duplicates found;
/// it knows nothing of meaning.
/// </summary>
public sealed partial class WordHashEmbedder : IEmbedder
{
    public const string ModelName = "word-hash (stand-in)";

    private static readonly HashSet<string> Fillers = ["a", "an", "the", "of", "for", "to", "on", "in", "and", "or", "is", "at", "with"];

    private int embedded;

    public int TokensPerText { get; set; } = 20;

    /// <summary>How many texts were embedded, so a test can see that an unchanged card wasn't sent again.</summary>
    public int Embedded => embedded;

    public Task<TextEmbedding?> EmbedAsync(string text, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref embedded);
        float[] vector = new float[DuplicateRule.Dimensions];
        foreach (string word in WordsOf(text))
            vector[PositionOf(word)] += 1;

        return Task.FromResult<TextEmbedding?>(new TextEmbedding(vector, ModelName, TokensPerText));
    }

    /// <summary>Lower-case words without the fillers, and without a plural "s": "carts" and "cart" are one word.</summary>
    private static IEnumerable<string> WordsOf(string text) => Word().Matches(text.ToLowerInvariant())
        .Select(match => match.Value.Length > 3 ? match.Value.TrimEnd('s') : match.Value)
        .Where(word => !Fillers.Contains(word));

    /// <summary>FNV-1a, because <c>string.GetHashCode</c> changes from run to run and stored vectors must not.</summary>
    private static int PositionOf(string word)
    {
        uint hash = 2166136261;
        foreach (char letter in word)
            hash = (hash ^ letter) * 16777619;

        return (int)(hash % DuplicateRule.Dimensions);
    }

    [GeneratedRegex(@"[\p{L}\p{Nd}]+")]
    private static partial Regex Word();
}
