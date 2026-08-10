using Companion.Core.Domain;

namespace Companion.Core.Abstractions;

/// <summary>
/// Write-through hook for a derived vector index. The authoritative memory store calls this after
/// every write so an in-memory index stays exactly in step with the tables — no periodic rebuild,
/// no polling, no window in which the index and the truth disagree.
/// <para>
/// Implementations must be idempotent and must derive eligibility from the memory itself: a memory
/// that has no embedding, or is <see cref="MemoryStatus.Deleted"/> / <see cref="MemoryStatus.Candidate"/>,
/// is removed from the index; anything else with an embedding is (re)indexed. Calling <see cref="Sync"/>
/// twice with the same memory is a no-op, so a store can call it unconditionally after any save.
/// </para>
/// </summary>
public interface IVectorIndexMaintenance
{
    /// <summary>
    /// Reflects a memory's current state in the index: indexed if it carries an embedding and is
    /// retrievable, removed otherwise. Cheap and synchronous — it only touches in-memory state.
    /// </summary>
    void Sync(IMemory memory);
}
