using Companion.Core.Services;

namespace Companion.Api;

/// <summary>
/// Persists prompt overrides as one plain text file per key (<c>prompts/renderer.core.txt</c>) —
/// hand-editable, git-ignorable, no database involved. Loaded once at startup; the /prompts API
/// (and the editor UI on top of it) writes through this so edits survive restarts.
/// </summary>
public static class PromptOverrideFiles
{
    public const string Extension = ".txt";

    /// <summary>Loads every recognized override file. Unknown keys are logged and skipped.</summary>
    public static int LoadAll(string directory, ILogger logger)
    {
        if (!Directory.Exists(directory))
            return 0;

        var loaded = 0;
        foreach (var file in Directory.EnumerateFiles(directory, "*" + Extension))
        {
            var key = Path.GetFileNameWithoutExtension(file);
            if (!Prompts.Exists(key))
            {
                logger.LogWarning("Prompt override file {File} has no matching prompt key; ignored.", file);
                continue;
            }

            Prompts.SetOverride(key, File.ReadAllText(file));
            loaded++;
        }

        if (loaded > 0)
            logger.LogInformation("Loaded {Count} prompt override(s) from {Directory}.", loaded, directory);
        return loaded;
    }

    public static void Save(string directory, string key, string text)
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, key + Extension), text);
    }

    public static void Delete(string directory, string key)
    {
        var path = Path.Combine(directory, key + Extension);
        if (File.Exists(path))
            File.Delete(path);
    }
}
