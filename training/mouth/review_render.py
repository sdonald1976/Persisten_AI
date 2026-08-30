"""Render the sealed blind-review pack as a page you can score without seeing arm identities.

The pack and its key were sealed earlier and hashed; nothing here reads KEY.json. The page shows
each item's turn, the plan it was rendered from, and three replies labelled A/B/C whose order was
shuffled per item at seal time. Scores are kept in the browser only - this page cannot phone
anywhere, and the arm labels are simply not present in it to be leaked.

    python review_render.py
"""
import hashlib
import io
import json
from pathlib import Path

ROOT = Path(__file__).parent
PACK = ROOT / "evaluation" / "review-pack"


def sha256_file(path):
    return hashlib.sha256(io.open(path, "rb").read()).hexdigest()


def esc(s):
    return (str(s or "")
            .replace("&", "&amp;").replace("<", "&lt;").replace(">", "&gt;"))


def main():
    pack = json.loads((PACK / "pack.json").read_text(encoding="utf-8"))
    manifest = json.loads((PACK / "MANIFEST.json").read_text(encoding="utf-8"))

    # Proof the rendered page came from the sealed pack, and that the key was not consulted.
    pack_sha = sha256_file(PACK / "pack.json")
    key_sha = sha256_file(PACK / "KEY.json")
    assert pack_sha == manifest["packSha256"], "pack.json has changed since it was sealed"
    assert key_sha == manifest["keySha256"], "KEY.json has changed since it was sealed"

    items = []
    for p in pack:
        plan = p["plan"]
        bullets = []
        if plan["mustExpress"]:
            bullets.append(("must say", "; ".join(plan["mustExpress"])))
        if plan["mayExpress"]:
            bullets.append(("may say", "; ".join(plan["mayExpress"])))
        if plan["background"]:
            bullets.append(("background — must NOT surface", "; ".join(plan["background"])))
        bullets.append(("question policy", plan["questionPolicy"]))

        rows = "".join(
            f'<div class="plan-row"><span class="k">{esc(k)}</span>'
            f'<span class="v">{esc(v)}</span></div>' for k, v in bullets)

        replies = "".join(
            f'''<div class="reply" data-item="{p['item']}" data-label="{esc(label)}">
  <div class="rl">{esc(label)}</div>
  <div class="rt">{esc(text)}</div>
  <div class="score">
    <span class="q">faithful?</span>
    <button class="b" data-dim="faithful" data-val="yes">yes</button>
    <button class="b" data-dim="faithful" data-val="no">no</button>
    <span class="q">natural?</span>
    <button class="b" data-dim="natural" data-val="yes">yes</button>
    <button class="b" data-dim="natural" data-val="no">no</button>
  </div>
</div>''' for label, text in p["replies"].items())

        items.append(f'''<article class="item" id="i{p['item']}">
  <header><span class="num">{p['item']}</span><span class="stratum">{esc(p['stratum'])}</span></header>
  <p class="them"><span class="who">Them</span>{esc(p['userMessage'])}</p>
  <div class="plan">{rows}</div>
  <div class="replies">{replies}</div>
</article>''')

    html = f'''<title>Blind Review Pack</title>
<link rel="preconnect" href="https://fonts.googleapis.com">
<link rel="preconnect" href="https://fonts.gstatic.com" crossorigin>
<link rel="stylesheet" href="https://fonts.googleapis.com/css2?family=Newsreader:ital,opsz,wght@0,6..72,400;0,6..72,600;1,6..72,400&family=IBM+Plex+Mono:wght@400;500;600&display=swap">
<style>
:root{{
  --paper:#FBFAF7; --card:#FFFFFF; --sunk:#F3F1EC;
  --ink:#14120E; --body:#33302A; --muted:#6B6559; --faint:#9A9284;
  --rule:#E0DCD2; --hair:#EDEAE2;
  --accent:#7A4E1F; --accent-soft:#F2E7DA;
  --yes:#2E5F3E; --yes-soft:#E2EFE5;
  --no:#8C2F27; --no-soft:#F6E3E0;
  --serif:"Newsreader",Georgia,serif;
  --mono:"IBM Plex Mono",ui-monospace,Consolas,monospace;
}}
@media (prefers-color-scheme:dark){{
  :root:not([data-theme="light"]){{
    --paper:#100F0D; --card:#191713; --sunk:#141210;
    --ink:#F0EDE6; --body:#CFC9BE; --muted:#948C7E; --faint:#6E6759;
    --rule:#2C2822; --hair:#221F1A;
    --accent:#C9925A; --accent-soft:#2A1F13;
    --yes:#7FB98D; --yes-soft:#17251A;
    --no:#D98C82; --no-soft:#2A1614;
  }}
}}
:root[data-theme="dark"]{{
  --paper:#100F0D; --card:#191713; --sunk:#141210;
  --ink:#F0EDE6; --body:#CFC9BE; --muted:#948C7E; --faint:#6E6759;
  --rule:#2C2822; --hair:#221F1A;
  --accent:#C9925A; --accent-soft:#2A1F13;
  --yes:#7FB98D; --yes-soft:#17251A;
  --no:#D98C82; --no-soft:#2A1614;
}}
*{{box-sizing:border-box}}
body{{margin:0;background:var(--paper);color:var(--body);font-family:var(--serif);
  font-size:17px;line-height:1.6;-webkit-font-smoothing:antialiased}}
.wrap{{max-width:820px;margin:0 auto;padding:0 22px 120px}}
header.top{{padding:52px 0 18px;border-bottom:2px solid var(--ink)}}
h1{{font-family:var(--serif);font-weight:600;font-size:clamp(32px,5vw,46px);
  letter-spacing:-.015em;line-height:1.05;margin:0 0 10px;color:var(--ink);text-wrap:balance}}
.sub{{margin:0;color:var(--muted);max-width:60ch}}
.seals{{font-family:var(--mono);font-size:11px;color:var(--faint);margin-top:14px;
  display:flex;flex-direction:column;gap:3px}}
.how{{background:var(--sunk);border-left:3px solid var(--accent);padding:16px 20px;margin:26px 0 0}}
.how p{{margin:0 0 8px;max-width:62ch}} .how p:last-child{{margin:0}}
.bar{{position:sticky;top:0;z-index:5;background:var(--paper);border-bottom:1px solid var(--rule);
  padding:10px 0;margin:0 0 8px;display:flex;gap:14px;align-items:center;flex-wrap:wrap}}
.bar .prog{{font-family:var(--mono);font-size:12px;color:var(--muted)}}
.bar button{{font-family:var(--mono);font-size:12px;padding:5px 12px;cursor:pointer;
  background:var(--card);color:var(--ink);border:1px solid var(--rule)}}
.bar button:hover{{border-color:var(--accent);color:var(--accent)}}
.item{{background:var(--card);border:1px solid var(--rule);margin:20px 0;padding:22px 24px}}
.item header{{display:flex;gap:12px;align-items:baseline;margin-bottom:12px}}
.num{{font-family:var(--mono);font-size:12px;font-weight:600;color:var(--accent);
  background:var(--accent-soft);padding:2px 8px}}
.stratum{{font-family:var(--mono);font-size:11px;letter-spacing:.1em;text-transform:uppercase;color:var(--faint)}}
.them{{font-size:19px;color:var(--ink);margin:0 0 14px}}
.them .who{{font-family:var(--mono);font-size:10.5px;letter-spacing:.12em;text-transform:uppercase;
  color:var(--faint);display:block;margin-bottom:2px}}
.plan{{background:var(--sunk);padding:12px 16px;margin-bottom:16px;display:grid;gap:5px}}
.plan-row{{display:grid;grid-template-columns:190px 1fr;gap:10px;font-size:14.5px}}
.plan-row .k{{font-family:var(--mono);font-size:11.5px;color:var(--muted);padding-top:3px}}
.plan-row .v{{color:var(--ink)}}
.replies{{display:grid;gap:12px}}
.reply{{border:1px solid var(--hair);padding:14px 16px;display:grid;gap:10px}}
.reply.done{{border-color:var(--accent)}}
.rl{{font-family:var(--mono);font-size:12px;font-weight:600;color:var(--accent)}}
.rt{{font-size:16.5px;color:var(--ink)}}
.score{{display:flex;gap:8px;align-items:center;flex-wrap:wrap;
  border-top:1px solid var(--hair);padding-top:10px}}
.q{{font-family:var(--mono);font-size:11px;color:var(--muted);margin-right:2px}}
.q:not(:first-child){{margin-left:12px}}
.b{{font-family:var(--mono);font-size:11.5px;padding:4px 12px;cursor:pointer;
  background:transparent;color:var(--muted);border:1px solid var(--rule)}}
.b:hover{{border-color:var(--accent);color:var(--accent)}}
.b[aria-pressed="true"][data-val="yes"]{{background:var(--yes-soft);color:var(--yes);border-color:var(--yes)}}
.b[aria-pressed="true"][data-val="no"]{{background:var(--no-soft);color:var(--no);border-color:var(--no)}}
:focus-visible{{outline:2px solid var(--accent);outline-offset:2px}}
footer{{margin-top:40px;padding-top:18px;border-top:1px solid var(--rule);
  font-family:var(--mono);font-size:12px;color:var(--faint)}}
</style>

<div class="wrap">
<header class="top">
  <h1>Blind Review Pack</h1>
  <p class="sub">Thirty turns from the Run-2 evaluation. Each shows what was asked, the plan the
  reply was rendered from, and three replies labelled A, B and C. Which model wrote which is not
  in this page.</p>
  <div class="seals">
    <span>pack.json &nbsp;{pack_sha}</span>
    <span>KEY.json &nbsp;&nbsp;{key_sha} &nbsp;(not read to build this page)</span>
  </div>
</header>

<div class="how">
  <p><strong>Faithful</strong> — does it obey the plan? Required points said, forbidden points
  absent, background not surfaced, question policy respected.</p>
  <p><strong>Natural</strong> — would you accept it as something a person said?</p>
  <p>The label order was shuffled per item when the pack was sealed, so position carries no
  information. Answers stay in this browser; nothing is sent anywhere.</p>
</div>

<div class="bar">
  <span class="prog" id="prog">0 of 180 scored</span>
  <button id="copy">Copy answers</button>
  <button id="clear">Clear</button>
</div>

{"".join(items)}

<footer>{manifest['items']} items · strata {esc(json.dumps(manifest['strata']))} · seed {manifest['seed']}</footer>
</div>

<script>
(function () {{
  var KEY = "run2-blind-review-v1";
  var answers = {{}};
  try {{ answers = JSON.parse(localStorage.getItem(KEY) || "{{}}"); }} catch (e) {{ answers = {{}}; }}

  function save() {{
    try {{ localStorage.setItem(KEY, JSON.stringify(answers)); }} catch (e) {{ /* private mode */ }}
  }}

  function slot(item, label, dim) {{ return item + "/" + label + "/" + dim; }}

  function paint() {{
    var scored = 0;
    document.querySelectorAll(".reply").forEach(function (r) {{
      var item = r.dataset.item, label = r.dataset.label, both = 0;
      r.querySelectorAll(".b").forEach(function (b) {{
        var on = answers[slot(item, label, b.dataset.dim)] === b.dataset.val;
        b.setAttribute("aria-pressed", on ? "true" : "false");
      }});
      ["faithful", "natural"].forEach(function (d) {{
        if (answers[slot(item, label, d)]) {{ scored++; both++; }}
      }});
      r.classList.toggle("done", both === 2);
    }});
    document.getElementById("prog").textContent = scored + " of 180 scored";
  }}

  document.addEventListener("click", function (e) {{
    var b = e.target.closest(".b");
    if (b) {{
      var r = b.closest(".reply");
      var k = slot(r.dataset.item, r.dataset.label, b.dataset.dim);
      answers[k] = (answers[k] === b.dataset.val) ? undefined : b.dataset.val;
      if (answers[k] === undefined) delete answers[k];
      save(); paint(); return;
    }}
    if (e.target.id === "clear") {{ answers = {{}}; save(); paint(); return; }}
    if (e.target.id === "copy") {{
      var lines = ["item,label,faithful,natural"];
      document.querySelectorAll(".reply").forEach(function (r) {{
        var i = r.dataset.item, l = r.dataset.label;
        lines.push([i, l, answers[slot(i, l, "faithful")] || "",
                    answers[slot(i, l, "natural")] || ""].join(","));
      }});
      var text = lines.join("\\n");
      if (navigator.clipboard) {{ navigator.clipboard.writeText(text); }}
      e.target.textContent = "Copied";
      setTimeout(function () {{ e.target.textContent = "Copy answers"; }}, 1400);
    }}
  }});

  paint();
}})();
</script>
'''

    out = PACK / "review.html"
    out.write_text(html, encoding="utf-8")
    print(f"pack sha256 {pack_sha}")
    print(f"key  sha256 {key_sha}  (not read)")
    print(f"-> {out}")


if __name__ == "__main__":
    main()
