# Setting up a fresh Windows machine

One command, from a clean clone:

```powershell
.\start-all.ps1
```

That resolves the same configuration the application loads, works out which models and adapters
it requires, downloads what is missing, verifies it, and starts Ava. On a machine that is already
provisioned it checks and starts — it does not download again.

If a required model cannot be acquired or verified, **startup is refused**. Nothing is ever
substituted: a companion running on a different model than she was configured with is the failure
this exists to prevent, and it is the kind that looks like success.

## Prerequisites

Install these first. The bootstrap detects each one and tells you which is missing, but it cannot
install them for you.

| Tool | Why | Where |
|---|---|---|
| **.NET 9 SDK** | builds and runs everything | <https://dotnet.microsoft.com/download> |
| **Ollama** | serves every language model in the roster | <https://ollama.com/download> |
| **Git LFS** | the run-1c adapter weights are LFS objects | <https://git-lfs.com> |
| **Python + `huggingface_hub`** | only for Hugging Face artifacts | `pip install huggingface_hub` |

Right after cloning, before anything else:

```powershell
git lfs install; git lfs pull
```

Skip that and the adapter file still *exists* — as a 130-byte text pointer with the right name.
The bootstrap detects exactly this and says so rather than letting a model fail to load later.

## The commands

```powershell
.\start-all.ps1 -Inventory
```

What this configuration requires, where each artifact should live, and which entries belong to a
capability that is switched off.

```powershell
.\start-all.ps1 -DryRun
```

What would be checked and downloaded. Touches nothing, starts nothing.

```powershell
.\verify-models.ps1
```

Check everything active. Downloads nothing; exits non-zero if anything required is missing or
invalid. Suitable for a health check. `.\start-all.ps1 -VerifyOnly` is the same check.

```powershell
.\start-all.ps1 -Force model.conversation
```

Reacquire one named dependency even though it looks present. Ids come from `-Inventory`. There is
deliberately no "force everything" — on this roster that is tens of gigabytes of intentional
waste.

```powershell
.\start-all.ps1 -AllConfigured
```

Also acquire models that configuration *names* but no enabled capability uses. Normal startup
does not, which is why turning on the specialist ONNX models is a deliberate act rather than a
side effect of a first run.

## What "required" means

A dependency is **active** when the effective configuration actually calls it. Everything else is
listed with the setting that switched it off. As shipped, that means:

- **Active**: the eight language-model roles Ollama serves, the run-1c adapter, and the merged
  `renderer-shadow` model.
- **Inactive**: the four specialist ONNX models (`CognitiveModels:*:Enabled` is false), the safety
  classifier (`Safety:Enabled` is false), the audio endpoints (a separate optional server), and
  the renderer base model (needed only to rebuild the adapter, not to run the app).

Several roles share one model. They are listed per role so you can see which role uses what; the
acquirer pulls each distinct tag once.

## Verification, honestly

Two different things get called "verified", and the report distinguishes them:

- **verified** — a SHA-256 is pinned in configuration and the file matches it. Only the run-1c
  adapter is in this category today, pinned by `Companion:RendererShadow:AdapterSha256`.
- **present** — the strongest check available passed, and *nothing pins it*. For an Ollama tag
  that means the server serves that exact tag. The report says so explicitly rather than letting
  "present" read as "verified".

## The mouth

The renderer adapter is a first-class dependency, and the report states which of three things is
true:

- **DISABLED** — `Companion:RendererShadow:Enabled` is false; the adapter is never loaded.
- **SHADOW ONLY** — observed beside every eligible turn; no user sees its output.
- **CANARY** — one configured user id sees its replies, production as immediate fallback.

`renderer-shadow` is **built locally** by `tools/build_renderer_model.py` from the adapter merged
onto its pinned base. Nothing downloads it, and the bootstrap will never pull a public model that
happens to share the name — that would be a silent substitution of a different mouth. If it is
missing, the report tells you which script produces it.

Rebuilding it needs the base model, which is pinned to an exact revision:

```powershell
python training\renderer\fetch_base.py
python tools\build_renderer_model.py
```

## Adding a model

Acquisition metadata lives beside the model configuration it describes, so adding a model and
saying where it comes from are the same edit:

```jsonc
"CognitiveModels": {
  "Classifier": {
    "Enabled": true,
    "Path": "classifier.onnx",
    "Sha256": "…",                      // optional; its absence is reported, not assumed
    "Source": {
      "Repository": "org/repo",         // required — never inferred from the filename
      "Revision": "abc123…",            // a branch name is not a pin
      "File": "model.onnx"
    }
  }
}
```

Without a `Source`, the bootstrap reports the artifact as unacquirable and says what configuration
is missing. It will not guess a repository from a filename — `classifier.onnx` names no
repository, and guessing one is how a bootstrap downloads plausible-looking wrong weights and
calls it success.

## If something goes wrong

The report names the problem and the fix. The common ones:

| Report says | Do |
|---|---|
| `ollama is not on PATH` | Install Ollama, then re-run |
| `the Ollama server could not be reached` | `ollama serve` |
| `file is a Git LFS pointer` | `git lfs install; git lfs pull` |
| `sha256 MISMATCH` | Re-acquire it, or correct the pin if it legitimately changed |
| `configuration records no source` | Add a `Source` block (above) |
| `appears to be gated` | `huggingface-cli login` |

Diagnostics never print tokens, keys, or credential-bearing URLs. A configured direct URI is
referred to but not echoed, because a URI is the one field that can carry a credential in a query
string.
