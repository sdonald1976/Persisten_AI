# 3D avatar (lip-sync)

The reference web client can show a **3D avatar that lip-syncs the spoken reply**. It renders with
[three.js](https://threejs.org) (vendored under `wwwroot/vendor/three/`, MIT — no CDN, works offline)
and drives the model's mouth from the amplitude of the companion's own speech.

## What model to get

You supply the character — the app ships no model. Get a **glTF `.glb` with facial blendshapes and a
full-body humanoid rig**:

- **Easiest: [Ready Player Me](https://readyplayer.me)** — free, browser-based, exports a full-body
  `.glb` with **ARKit + Oculus visemes** and a standard rig. Guaranteed to lip-sync here.
- Any other `.glb` works **as long as it has mouth blendshapes** — the viewer looks for
  `jawOpen` (ARKit), `mouthOpen`, or the `viseme_aa` / `aa` morph. Without one of those, the model
  loads but the mouth won't move (the panel says so).
- Marketplaces (Sketchfab, CGTrader, …): filter for `glTF`/`glb` + "blendshapes"/"ARKit"/"visemes".
- Mixamo characters have great body rigs but **no facial blendshapes** — good for gestures later, not
  for lip-sync.

Keep it web-friendly: ~10k–80k triangles, baked PBR textures.

The scene lights the model with **image-based lighting** (a neutral studio environment) plus a soft
key light and ACES filmic tone mapping, so skin/eyes/hair render naturally rather than flat and dark.
If it looks too bright or too dim for your model, adjust `renderer.toneMappingExposure` in the avatar
module.

## Using it

1. Click **🧍 Avatar** in the header to open the stage.
2. **Load .glb…** (or drag-and-drop a file onto the stage). To auto-load on open, drop your file at
   `src/Companion.Api/wwwroot/avatar.glb` — the viewer fetches `/avatar.glb` if present.
3. Turn on **🔈 Speak** (or use voice input) so the companion talks — the mouth follows along.
4. **Drag** to turn · **scroll** to zoom.

## How lip-sync works (and its limits)

This first version is **amplitude-based**: a Web Audio analyser on the TTS clip drives the mouth-open
amount from loudness, smoothed frame-to-frame, plus idle blinking. It looks alive and works with any
viseme-capable model, but it's not phoneme-accurate — the mouth opens and closes with the voice, it
doesn't form distinct shapes for "oo" vs "ee". Tuning knobs live at the top of the avatar module in
`index.html` (`MOUTH_GAIN`, `MOUTH_SMOOTH`, blink timing).

**Next step:** phoneme-accurate visemes, driven from Piper's phoneme timings, so the mouth shapes the
actual sounds; then body gestures/emotes. See [`FUTURE_UX_ROADMAP.md`](FUTURE_UX_ROADMAP.md).

## Notes

- All rendering is local; no data leaves the machine and nothing is fetched from a CDN.
- The model isn't persisted between sessions yet — reload it (or use the `wwwroot/avatar.glb` default).
- Needs a WebGL-capable browser; the panel reports if it can't start.
