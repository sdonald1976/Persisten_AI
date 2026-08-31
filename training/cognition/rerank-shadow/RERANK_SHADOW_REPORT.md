# Reranker shadow evaluation

- shadow records: **0**
- reviewed turns: **0** (usable positive-bearing: 0, ambiguous: 0)

## Agreement, latency, reliability (all shadow records)
- _no shadow records yet — start the companion with RerankShadow on and take some turns._

## Ranking metrics vs reviewed relevance (paired)
- _no reviewed positive-bearing labels that match a shadow record yet._
- **This does not block the pipeline** — every stage above is exercised; promotion is what waits on labels.

## Promotion gate (conservative — not met until real reviewed data supports it)
- CE must not lose to RULE on any sufficiently-populated user-grouped fold;
- CE must match or beat 3B on the frozen reviewed real set (paired CI ≥ 0);
- CE latency p99 and failure rate must support replacing the live 3B call;
- weak/mechanical labels guide collection only; they never authorize promotion.

_Weak/synthetic/borrowed strata are reported in their own files and never mixed into the reviewed-real numbers above._
