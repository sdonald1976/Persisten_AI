# Goldens

Byte-comparison artifacts. They exist so that a refactor can **prove** it changed nothing,
rather than argue it. Every file here is an *input* to a test, never an output of one.

| File | Pinned by | What it protects |
|---|---|---|
| `compact-v4.txt` | `CompactV4GoldenTests` | The plan/4 wire format, one case per structural axis of the FRAME section |
| `prompt-render.txt` | `PromptRenderGoldenTests` | The exact string the chat model receives, across bare / full / trimmed / clarification packets |
| `ef-model.txt` | `EfModelSnapshotTests` | The built EF model — all 41 entities, their properties, keys, indexes and foreign keys |
| `PROVENANCE.txt` | written by the tool | Protocol version, source commit and hash for each regenerated golden |

Sibling goldens for plan/3 live in `tools/Companion.PlanV3.Prototype/Goldens/`, next to the
804-plan plan/2 corpus golden they extend.

## Changing one

Nothing regenerates automatically. There is no hook in the build, the test run, or CI, and
the tests never write to this directory — a golden that rewrites itself asserts nothing.

To see whether anything has drifted:

```bash
dotnet run --project tools/Companion.Goldens -c Release
```

That reports per-golden status, line counts, hashes and the first few differing lines, then
**exits non-zero without writing anything**. Read the diff. The question to answer is not
"does it still pass" but "did I mean for these bytes to move".

If every difference is intended:

```bash
dotnet run --project tools/Companion.Goldens -c Release -- --accept
```

Add `--only=NAME` to restrict it to one golden (`compact-v4`, `prompt-render`, `ef-model`,
`compact-v3-manifest`, `compact-v3-samples`).

Commit regenerated goldens as their **own** change, separate from the code that moved them,
with the reason the bytes were expected to change. A golden updated in the same commit as the
behaviour it guards is indistinguishable from a golden that was never checked.

## Why provenance sits in a sidecar

`PROVENANCE.txt` records the protocol version, the source commit and the content hash for
each golden the tool has written. It is deliberately *beside* the goldens rather than inside
them: a commit hash embedded in a golden would change its bytes on every commit, which is the
opposite of what a golden is for.

A commit recorded as `<sha>-dirty` means the golden was generated from an uncommitted working
tree and cannot be reproduced from that commit alone. Treat it as provisional.

## Line endings

`.gitattributes` pins this directory to `eol=lf`. The tool writes LF and UTF-8 without a BOM,
so the bytes do not depend on which machine produced them. Without that pin, every
regeneration on Windows would leave the files looking modified when nothing had changed.
