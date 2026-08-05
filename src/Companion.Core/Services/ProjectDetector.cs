using Companion.Core.Text;

namespace Companion.Core.Services;

/// <summary>
/// Lightweight project detection for Phase 2: matches the query against the names of
/// projects the user already has memories about. Returns the best match only when the
/// overlap is unambiguous enough; otherwise null (honest "I don't know which project").
/// Alias-aware, confidence-scored entity resolution arrives in Phase 4.
/// </summary>
public static class ProjectDetector
{
    /// <summary>Best-matching known project name for the query, or null if none is clear.</summary>
    public static string? Detect(string query, IEnumerable<string> knownProjects)
    {
        var queryTokens = new HashSet<string>(Tokenizer.Tokenize(query));
        if (queryTokens.Count == 0)
            return null;

        string? best = null;
        var bestScore = 0.0;
        var secondScore = 0.0;

        foreach (var project in knownProjects.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var projectTokens = Tokenizer.Tokenize(project);
            if (projectTokens.Count == 0)
                continue;

            var matched = projectTokens.Count(queryTokens.Contains);
            var score = (double)matched / projectTokens.Count;

            if (score > bestScore)
            {
                secondScore = bestScore;
                bestScore = score;
                best = project;
            }
            else if (score > secondScore)
            {
                secondScore = score;
            }
        }

        // Require a real overlap and a clear winner over the runner-up.
        if (bestScore >= 0.5 && bestScore - secondScore >= 0.25)
            return best;

        return null;
    }
}
