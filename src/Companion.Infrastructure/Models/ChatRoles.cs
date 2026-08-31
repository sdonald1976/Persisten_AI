namespace Companion.Infrastructure.Models;

/// <summary>Keyed-DI service keys for the per-job chat models.</summary>
internal static class ChatRoles
{
    public const string Conversation = "conversation";
    public const string Extraction = "extraction";
    public const string Summarizer = "summarizer";
    public const string Reranker = "reranker";
    public const string Safety = "safety";
    public const string TaskAuditor = "task-auditor";
    public const string ToolPlanner = "tool-planner";

    /// <summary>The Stheno-free route's planning seat: refines the native plan/4, never speaks.</summary>
    public const string ExecutivePlanner = "executive-planner";

    /// <summary>
    /// Post-turn reflection. Used to ride the conversational model ("her own voice"); it is a
    /// separately configured role now so the background never depends on the reply model -
    /// with the conversational model down, reflection still runs and the displayed reply was
    /// never its business anyway.
    /// </summary>
    public const string Reflection = "reflection";
}
