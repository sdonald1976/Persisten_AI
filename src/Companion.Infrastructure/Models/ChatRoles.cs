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
}
