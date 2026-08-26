# plan/2 and plan/3 goldens

| File | Pinned by | What it protects |
|---|---|---|
| `compact-v3-manifest.txt` | `CompactV3GoldenTests` | `CompactV3` bytes for every corpus plan, as `id sha256` |
| `compact-v3-samples.txt` | `CompactV3GoldenTests` | Ten full `CompactV3` renderings, so the format is readable in a diff |

The plan/2 golden has no file here: `CorpusGoldenTests` compares the producer hop against
`CompactV2` computed in-process, and against the frozen `plan2` strings already stored in
`training/renderer/`. That golden covers **804** plans (761 scenarios + 32 unseen + 11
fixtures); the frozen-string half of it applies to the **289** that have a frozen string.

## `refused-by-lint`

Two entries in the manifest read `refused-by-lint` rather than a hash. Those plans pass
`PlanV3Codec.Validate` and are then **refused** by `CompactV3`, whose coaching lint runs at
serialization time.

That is pinned deliberately: a refactor that quietly started accepting them would otherwise
go unnoticed. It is also a recorded contract-layer finding — validation succeeding where
serialization refuses is a mismatch to resolve before the plan/4 corpus freeze, by deciding
where the lint belongs, **not** by excluding those plans or weakening the lint.

## Regenerating

See `tests/Companion.Tests/Goldens/README.md`. The same tool owns every golden:

```bash
dotnet run --project tools/Companion.Goldens -c Release
```

reports drift and writes nothing; `-- --accept` writes. Nothing regenerates automatically.
