// live map page. polls /state, draws the map art under the dots, and calibrates the art against
// the dots using point pairs: stand somewhere, press P, click where you are on the art. two or
// more points solve a similarity transform (rotation, scale, offset, optional mirror).

const canvas = document.getElementById("map");
const ctx = canvas.getContext("2d");

let state = { inRaid: false };
let mapCfg = null;
let mapId = null;
let currentLayer = null;
let image = null;
let baseLayer = null;
let baseImage = null;
let showStack = true;
let showQuestLoot = true;
let showQuestZones = true;
let infoOpen = false;
let showStory = false;
let infoTab = "quests";
let lastInfoRender = 0;
let mapFailed = false;

// set by the draw itself, so the loading message goes away when something actually made it to
// the canvas rather than when a set of conditions we guessed at happens to agree
let layerPainted = false;
let raidSeenAt = 0;

let cal = { rotation: 0, scale: 1, offsetX: 0, offsetY: 0, flipX: 1, flipY: 1 };
let view = { zoom: 1, x: 0, y: 0 };
let saveTimer = null;

let labels = [];
let labelFlip = false;
let showNames = true;
let showContacts = true;
let follow = true;
let samples = [];
let pending = null;
let lastDown = null;

/* ---------- sizing ---------- */

function resize() {
  canvas.width = window.innerWidth;
  canvas.height = window.innerHeight;
  draw();
}

window.addEventListener("resize", resize);

/* ---------- maths ---------- */

// world (x, z) to image pixels, using the layer's world extent
function worldToImage(x, z, layer) {
  const b = layer.imageBounds;
  const sx = layer.width / (b.Max.x - b.Min.x);
  const sy = layer.height / (b.Max.y - b.Min.y);
  return { x: (x - b.Min.x) * sx, y: (b.Max.y - z) * sy };
}

// art is authored in the config's coordinate rotation. for customs that is 180, which is why
// the unrotated art put a player at old gas in the bottom right corner.
function defaultCal() {
  return {
    rotation: (mapCfg && typeof mapCfg.rotation === "number") ? mapCfg.rotation : 0,
    scale: 1, offsetX: 0, offsetY: 0, flipX: 1, flipY: 1
  };
}

// art pixel -> image pixel, as a 2x2 matrix plus translation, matching the draw chain below
function calMatrix(layer) {
  // a solved affine from three or more points scales x and y independently, which a rotation
  // plus one uniform scale cannot. if it exists it wins, until a slider is touched.
  if (cal && cal.affine) return cal.affine;

  const cx = layer.width / 2;
  const cy = layer.height / 2;
  const t = (cal.rotation * Math.PI) / 180;
  const cos = Math.cos(t);
  const sin = Math.sin(t);
  const sx = cal.scale * cal.flipX;
  const sy = cal.scale * cal.flipY;

  const a = cos * sx;
  const b = -sin * sy;
  const c = sin * sx;
  const d = cos * sy;

  return {
    a: a, b: b, c: c, d: d,
    tx: cx + cal.offsetX - (a * cx + b * cy),
    ty: cy + cal.offsetY - (c * cx + d * cy)
  };
}

function calApply(m, p) {
  return { x: m.a * p.x + m.b * p.y + m.tx, y: m.c * p.x + m.d * p.y + m.ty };
}

// inverse of the art transform, needed to turn a click into an art pixel
function calInvert(q, layer) {
  if (cal && cal.affine) {
    const m = cal.affine;
    const det = m.a * m.d - m.b * m.c;
    if (Math.abs(det) < 1e-9) return { x: q.x, y: q.y };

    const dx = q.x - m.tx;
    const dy = q.y - m.ty;

    return {
      x: (m.d * dx - m.b * dy) / det,
      y: (-m.c * dx + m.a * dy) / det
    };
  }

  const cx = layer.width / 2;
  const cy = layer.height / 2;
  const t = (cal.rotation * Math.PI) / 180;
  const cos = Math.cos(t);
  const sin = Math.sin(t);

  const dx = q.x - (cx + cal.offsetX);
  const dy = q.y - (cy + cal.offsetY);

  const rx = dx * cos + dy * sin;
  const ry = -dx * sin + dy * cos;

  return {
    x: cx + (rx / cal.scale) / cal.flipX,
    y: cy + (ry / cal.scale) / cal.flipY
  };
}

function toScreen(p) {
  return {
    x: (p.x - view.x) * view.zoom + canvas.width / 2,
    y: (p.y - view.y) * view.zoom + canvas.height / 2
  };
}

function screenToImage(sx, sy) {
  return {
    x: (sx - canvas.width / 2) / view.zoom + view.x,
    y: (sy - canvas.height / 2) / view.zoom + view.y
  };
}

function project(x, z) {
  // dots are never calibrated. the art moves around them, not the other way round.
  return toScreen(worldToImage(x, z, currentLayer));
}

/* ---------- map loading ---------- */

async function loadMap(id) {
  layerPainted = false;
  mapId = id;
  mapFailed = false;
  selectedLoot = null;
  currentLayer = null;
  image = null;
  samples = loadSamples(id);

  try {
    const r = await fetch("/maps/" + encodeURIComponent(id) + "/map.json", { cache: "no-store" });
    if (!r.ok) throw new Error("no art for " + id);
    mapCfg = await r.json();
    labels = mapCfg.labels || [];
    baseLayer = mapCfg.layers.find(l => l.level === 0) || mapCfg.layers[0];
    baseImage = null;

    if (baseLayer) {
      baseImage = new Image();
      baseImage.onload = draw;
      baseImage.src = "/maps/" + encodeURIComponent(mapId) + "/" + encodeURIComponent(baseLayer.image);
    }

    // the map can declare that its labels are authored 180 out from its art, which some of the
    // rework sets are. a saved toggle in the browser still wins.
    labelFlip = mapCfg.labelFlip === true;
    try {
      const savedFlip = localStorage.getItem("vzor.labelFlip." + id);
      if (savedFlip !== null) labelFlip = savedFlip === "1";
    } catch (e) { }
  } catch (e) {
    mapCfg = null;
    mapFailed = true;
    setStatus(e.message, "warn");
    document.getElementById("mapname").textContent = id || "unknown map";
    return;
  }

  await loadCalibration();
  pickLayer(true);
  fitView();
  updateSampleUi();
  document.getElementById("mapname").textContent = mapCfg.display;
}

function pickLayer(force) {
  if (!mapCfg) return;

  let chosen = null;

  // full 3d containment. their bounds are x, then depth in y, then height in z. comparing our
  // height against their depth range matched the ground floor box every time and nothing else,
  // which is why resort floors never changed.
  if (state.inRaid && typeof state.y === "number" && typeof state.x === "number" && typeof state.z === "number") {
    for (const layer of mapCfg.layers) {
      for (const b of (layer.gameBounds || [])) {
        if (state.x >= b.Min.x && state.x <= b.Max.x &&
            state.z >= b.Min.y && state.z <= b.Max.y &&
            state.y >= b.Min.z && state.y <= b.Max.z) {
          if (!chosen || layer.level > chosen.level) chosen = layer;
          break;
        }
      }
    }
  }

  if (!chosen && state.inRaid && typeof state.y === "number")
  {
    // the boxes are building footprints, so anywhere the footprint misses (corridors, tunnels,
    // the bottom of labs) used to leave us on the previous floor. if exactly one layer claims
    // our height, trust it: that is unambiguous, and ambiguous heights still fall through.
    let byHeight = null;
    let ambiguous = false;

    for (const layer of mapCfg.layers) {
      let hit = false;
      for (const b of (layer.gameBounds || [])) {
        if (state.y >= b.Min.z && state.y <= b.Max.z) { hit = true; break; }
      }
      if (!hit) continue;
      if (byHeight && byHeight.level !== layer.level) { ambiguous = true; break; }
      byHeight = layer;
    }

    if (byHeight && !ambiguous) chosen = byHeight;
  }

  if (!chosen)
  {
    // their floor bounds are boxes around buildings, not whole floors. standing in a corridor
    // between two boxes used to drop us to ground floor, so keep the last layer instead of
    // guessing, and only fall back on a fresh map.
    if (currentLayer && !force) return;
    chosen = mapCfg.layers.find(l => l.level === 0) || mapCfg.layers[0];
  }

  if (!chosen) return;

  if (force || !currentLayer || currentLayer.name !== chosen.name) {
    currentLayer = chosen;

    const floorLabel = document.getElementById("floor");
    if (floorLabel) floorLabel.textContent = chosen.name;
    image = new Image();
    image.onload = draw;
    image.src = "/maps/" + encodeURIComponent(mapId) + "/" + encodeURIComponent(chosen.image);
  }
}

function fitView() {
  if (!currentLayer) return;

  const fit = Math.min(canvas.width / currentLayer.width, canvas.height / currentLayer.height) * 0.92;
  view.zoom = fit;
  view.x = currentLayer.width / 2;
  view.y = currentLayer.height / 2;
  draw();
}

/* ---------- state polling ---------- */

// the raid is real but we have no art for its location, so say so loudly instead of leaving
// a blank canvas with a tiny status line
function updateLoadFlash() {
  const el = document.getElementById("loadflash");
  if (!el) return;

  if (!state.inRaid) {
    raidSeenAt = 0;
    el.classList.add("hidden");
    return;
  }

  if (!raidSeenAt) raidSeenAt = performance.now();

  // the draw sets the flag, so this clears the moment a layer reaches the canvas. the age
  // fallback exists so a map we genuinely cannot draw does not pulse forever.
  const tooLong = performance.now() - raidSeenAt > 12000;
  const show = !layerPainted && !tooLong;

  el.classList.toggle("hidden", !show);

  if (show) {
    const id = document.getElementById("loadflashid");
    if (id) id.textContent = state.map || "unknown location";
  }
}

async function poll() {
  try {
    const r = await fetch("/state", { cache: "no-store" });
    state = await r.json();

    // belt and braces: the hideout is not a raid, even if an older plugin build says it is
    if (state.inRaid && /^hideout$/i.test(state.map || "")) state.inRaid = false;

    if (!state.inRaid) {
      setStatus("waiting for raid", "dim");
    } else {
      setStatus(state.map || "unknown map", "good");

      if (state.map && state.map !== mapId) {
        await loadMap(state.map);
      } else if (!mapFailed) {
        pickLayer(false);
      }
    }
  } catch (e) {
    setStatus("not connected", "warn");
  }

  updateLoadFlash();

  if (follow && state.inRaid && currentLayer && typeof state.x === "number") {
    const p = worldToImage(state.x, state.z, currentLayer);
    view.x = p.x;
    view.y = p.y;
  }

  // loot spawns over the first couple of minutes, so the table poll runs while the raid lasts
  if (state.inRaid && !wasInRaid) {
    wasInRaid = true;
    raidEndedAt = 0;
    scheduleLootRefresh();
  } else if (!state.inRaid && wasInRaid) {
    // raid just ended. drop the map so the last thing you saw is not still sitting there
    // pretending to be live, and show the end notice for ten seconds.
    wasInRaid = false;
    raidEndedAt = performance.now();
    stopLootRefresh();
    clearMap();
  }

  updateWaitingOverlay();
  renderInfo(false);

  updateCoords();
  draw();

  // follow the plugin's own push rate instead of guessing. one knob in the config, and the
  // page stops asking more often than the data can change.
  const hz = typeof state.rate === "number" && state.rate > 0 ? state.rate : 10;
  setTimeout(poll, Math.max(33, Math.min(1000, Math.round(1000 / hz))));
}

function updateCoords() {
  const el = document.getElementById("coords");
  if (!state.inRaid) {
    el.textContent = "-";
    return;
  }
  el.textContent =
    "x " + fmt(state.x) + "  y " + fmt(state.y) + "  z " + fmt(state.z) +
    "  yaw " + fmt(state.yaw) + "  contacts " + ((state.players || []).length);
}

function fmt(v) {
  return typeof v === "number" ? v.toFixed(1) : "-";
}

function setStatus(text, cls) {
  const el = document.getElementById("status");
  el.textContent = text;
  el.className = cls || "dim";
}

/* ---------- drawing ---------- */

function draw() {
  ctx.setTransform(1, 0, 0, 1, 0, 0);
  ctx.fillStyle = "#0b0e12";
  ctx.fillRect(0, 0, canvas.width, canvas.height);

  if (!mapCfg || !currentLayer) return;

  // upper floors are transparent overlays, so the ground map goes on first and the floor plan
  // sits on top of it. a bare floor layer is a building outline with nothing around it.
  if (showStack && baseLayer && baseLayer !== currentLayer) drawImageLayer(baseImage, baseLayer, 1, true);
  drawImageLayer(image, currentLayer, 1, false);

  // place names come from the map config, they are not in the svg. they belong to the art, so
  // they take the same transform and move with it while you calibrate.
  if (showNames && labels.length) {
    const m = calMatrix(currentLayer);
    ctx.globalAlpha = 0.55;

    for (const L of labels) {
      let p = worldToImage(L.x, L.y, currentLayer);

      // some maps were exported with the drawing rotated inside its own bounds, so the art and
      // the label coordinates disagree by 180. this flips labels only, per map.
      if (labelFlip) p = { x: currentLayer.width - p.x, y: currentLayer.height - p.y };

      const q = calApply(m, p);
      const s = toScreen(q);
      if (s.x < -90 || s.x > canvas.width + 90 || s.y < -20 || s.y > canvas.height + 20) continue;

      const size = Math.max(9, Math.min(26, (L.size || 16) * view.zoom * cal.scale));
      ctx.font = size + "px Segoe UI, sans-serif";
      ctx.textAlign = "center";
      ctx.lineWidth = 3;
      ctx.strokeStyle = "#0b0e12cc";
      ctx.strokeText(L.text, s.x, s.y);
      ctx.fillStyle = "#c9d4e0";
      ctx.fillText(L.text, s.x, s.y);
    }

    ctx.textAlign = "left";
    ctx.globalAlpha = 1;
  }

  if (!state.inRaid) return;

  // exfils. filled cyan when open, hollow red when not, name above the diamond.
  for (const ex of (state.exfils || [])) {
    const s = project(ex.x, ex.z);
    const r = 6;

    ctx.beginPath();
    ctx.moveTo(s.x, s.y - r);
    ctx.lineTo(s.x + r, s.y);
    ctx.lineTo(s.x, s.y + r);
    ctx.lineTo(s.x - r, s.y);
    ctx.closePath();

    if (ex.open) {
      ctx.fillStyle = "#00e5ff";
      ctx.fill();
    } else {
      ctx.strokeStyle = "#ff5555";
      ctx.lineWidth = 2;
      ctx.stroke();
    }

    ctx.font = "10px Segoe UI, sans-serif";
    ctx.textAlign = "center";
    ctx.lineWidth = 3;
    ctx.strokeStyle = "#0b0e12cc";
    ctx.strokeText(ex.name, s.x, s.y - 10);
    ctx.fillStyle = ex.open ? "#8fe9ff" : "#ff9a9a";
    ctx.fillText(ex.name, s.x, s.y - 10);
    ctx.textAlign = "left";
  }

  // transits to another map. purple square so it cannot be confused with an exfil diamond.
  for (const tr of (state.transits || [])) {
    const s = project(tr.x, tr.z);
    const r = 5;

    ctx.strokeStyle = "#c58cff";
    ctx.lineWidth = 2;
    ctx.strokeRect(s.x - r, s.y - r, r * 2, r * 2);

    ctx.font = "10px Segoe UI, sans-serif";
    ctx.textAlign = "center";
    ctx.lineWidth = 3;
    ctx.strokeStyle = "#0b0e12cc";

    // only append the destination when the transit name does not already say it
    const showTarget = tr.t && tr.t !== tr.n && !(tr.n || "").toLowerCase().includes(tr.t.toLowerCase());
    const label = tr.n + (showTarget ? " \u2192 " + tr.t : "");
    ctx.strokeText(label, s.x, s.y - 11);
    ctx.fillStyle = "#d9bcff";
    ctx.fillText(label, s.x, s.y - 11);
    ctx.textAlign = "left";
  }

  // quest items lying on the floor and quest zones, both toggleable
  if (state.quest) {
    if (showQuestLoot) {
      for (const q of (state.quest.loot || [])) {
        const s = project(q.x, q.z);

        ctx.beginPath();
        ctx.moveTo(s.x, s.y - 6);
        ctx.lineTo(s.x + 6, s.y);
        ctx.lineTo(s.x, s.y + 6);
        ctx.lineTo(s.x - 6, s.y);
        ctx.closePath();
        ctx.fillStyle = "#ffb14e";
        ctx.fill();
        ctx.strokeStyle = "#0b0e12";
        ctx.lineWidth = 1.5;
        ctx.stroke();

        if (view.zoom > 1.4) {
          ctx.font = "10px Segoe UI, sans-serif";
          ctx.textAlign = "left";
          ctx.lineWidth = 3;
          ctx.strokeStyle = "#0b0e12cc";
          ctx.strokeText(q.n, s.x + 8, s.y + 4);
          ctx.fillStyle = "#ffcf8a";
          ctx.fillText(q.n, s.x + 8, s.y + 4);
        }
      }
    }

    if (showQuestLoot) {
      // containers that hold something an active quest wants. square, so it reads as "a box"
      for (const box of (state.quest.containers || [])) {
        const s = project(box.x, box.z);

        ctx.strokeStyle = "#ffb14e";
        ctx.lineWidth = 2;
        ctx.strokeRect(s.x - 6, s.y - 6, 12, 12);

        if (view.zoom > 1.2) {
          ctx.font = "9px Segoe UI, sans-serif";
          ctx.textAlign = "center";
          ctx.lineWidth = 3;
          ctx.strokeStyle = "#0b0e12cc";
          ctx.strokeText(box.n, s.x, s.y + 18);
          ctx.fillStyle = "#ffcf8a";
          ctx.fillText(box.n, s.x, s.y + 18);
          ctx.textAlign = "left";
        }
      }
    }

    if (showQuestZones) {
      for (const z of (state.quest.zones || [])) {
        const s = project(z.x, z.z);

        ctx.beginPath();
        ctx.arc(s.x, s.y, 9, 0, Math.PI * 2);
        ctx.strokeStyle = "#b388ff";
        ctx.lineWidth = 2;
        ctx.stroke();

        ctx.font = "10px Segoe UI, sans-serif";
        ctx.textAlign = "center";
        ctx.lineWidth = 3;
        ctx.strokeStyle = "#0b0e12cc";
        ctx.strokeText(z.n, s.x, s.y - 13);
        ctx.fillStyle = "#d9bcff";
        ctx.fillText(z.n, s.x, s.y - 13);
        ctx.textAlign = "left";
      }
    }
  }

  // bodies. anything that was alive and then was not, marked where we last saw it. a plain
  // white x, no label, because a body's name is not actionable and there can be twenty.
  for (const body of (state.bodies || [])) {
    const s = project(body.x, body.z);
    const r = 5;

    ctx.beginPath();
    ctx.moveTo(s.x - r, s.y - r);
    ctx.lineTo(s.x + r, s.y + r);
    ctx.moveTo(s.x + r, s.y - r);
    ctx.lineTo(s.x - r, s.y + r);
    ctx.strokeStyle = "#ffffffee";
    ctx.lineWidth = 2;
    ctx.stroke();
  }

  const players = state.players || [];

  for (const p of players) {
    if (p.me) continue;

    const s = project(p.x, p.z);
    ctx.beginPath();
    ctx.arc(s.x, s.y, 4, 0, Math.PI * 2);
    ctx.fillStyle = roleColor(p.role, p.side);
    ctx.fill();
    ctx.strokeStyle = "#000a";
    ctx.lineWidth = 1;
    ctx.stroke();

    if (showContacts) {
      ctx.font = "11px Segoe UI, sans-serif";
      ctx.fillStyle = "#dfe6ee";
      ctx.textAlign = "left";
      ctx.lineWidth = 3;
      ctx.strokeStyle = "#0b0e12cc";

      const text = contactLabel(p) + "  " + Math.round(p.dist || 0) + "m";
      const tx = s.x + 7;
      const ty = s.y + 4;

      ctx.strokeText(text, tx, ty);
      ctx.fillText(text, tx, ty);

      // chevron after the distance, drawn as a path so it does not depend on the font having a
      // suitable glyph
      let dyUp = 0;
      if (typeof p.y === "number" && typeof state.y === "number") {
        const dy = p.y - state.y;
        if (Math.abs(dy) > 1.2) dyUp = dy > 0 ? 1 : -1;
      }

      if (dyUp !== 0) drawChevron(tx + ctx.measureText(text).width + 7, ty - 4, dyUp > 0);
    }
  }

  if (typeof state.x === "number") drawSelf();

  // favourites stay on the map whether or not you picked them, that is the whole point
  if (currentLayer && lootFavorites.size) {
    ctx.font = "11px Segoe UI, sans-serif";
    ctx.textAlign = "left";

    for (const item of lootItems) {
      if (!lootFavorites.has(lootFavKey(item.n))) continue;

      const s = project(item.x, item.z);
      if (s.x < -60 || s.x > canvas.width + 60 || s.y < -30 || s.y > canvas.height + 30) continue;

      ctx.beginPath();
      ctx.moveTo(s.x, s.y - 6);
      ctx.lineTo(s.x + 6, s.y);
      ctx.lineTo(s.x, s.y + 6);
      ctx.lineTo(s.x - 6, s.y);
      ctx.closePath();
      ctx.strokeStyle = "#ffd23b";
      ctx.lineWidth = 2;
      ctx.stroke();

      ctx.lineWidth = 3;
      ctx.strokeStyle = "#0b0e12cc";
      ctx.strokeText(item.n, s.x + 9, s.y - 4);
      ctx.fillStyle = "#ffd23b";
      ctx.fillText(item.n, s.x + 9, s.y - 4);
    }
  }

  // favourites that are sitting inside something, marked on the container itself
  if (currentLayer && lootFavorites.size && containerItems.length) {
    ctx.font = "11px Segoe UI, sans-serif";
    ctx.textAlign = "left";

    for (const c of containerItems) {
      let hits = null;

      for (const name of (c.i || [])) {
        if (!lootFavorites.has(lootFavKey(name))) continue;
        if (!hits) hits = [];
        hits.push(name);
      }

      if (!hits) continue;

      const s = project(c.x, c.z);
      if (s.x < -60 || s.x > canvas.width + 60 || s.y < -30 || s.y > canvas.height + 30) continue;

      ctx.beginPath();
      ctx.arc(s.x, s.y, 9, 0, Math.PI * 2);
      ctx.strokeStyle = "#ffd23b";
      ctx.lineWidth = 2;
      ctx.stroke();

      const label = hits.join(", ");
      ctx.lineWidth = 3;
      ctx.strokeStyle = "#0b0e12cc";
      ctx.strokeText(label, s.x + 12, s.y + 4);
      ctx.fillStyle = "#ffd23b";
      ctx.fillText(label, s.x + 12, s.y + 4);
    }
  }

  // whatever was picked in the loot table, kept on the map until you pick something else
  if (selectedLoot && currentLayer) {
    const s = project(selectedLoot.x, selectedLoot.z);

    ctx.beginPath();
    ctx.moveTo(s.x, s.y - 7);
    ctx.lineTo(s.x + 7, s.y);
    ctx.lineTo(s.x, s.y + 7);
    ctx.lineTo(s.x - 7, s.y);
    ctx.closePath();
    ctx.fillStyle = "#ffd23b";
    ctx.fill();
    ctx.strokeStyle = "#0b0e12";
    ctx.lineWidth = 2;
    ctx.stroke();

    const label = selectedLoot.n + (selectedLoot.p ? "  " + selectedLoot.p.toLocaleString() : "");
    ctx.font = "11px Segoe UI, sans-serif";
    ctx.textAlign = "left";
    ctx.lineWidth = 3;
    ctx.strokeStyle = "#0b0e12cc";
    ctx.strokeText(label, s.x + 10, s.y + 4);
    ctx.fillStyle = "#ffe9a3";
    ctx.fillText(label, s.x + 10, s.y + 4);
  }

  if (pending) {
    const s = project(pending.x, pending.z);
    ctx.beginPath();
    ctx.arc(s.x, s.y, 12, 0, Math.PI * 2);
    ctx.strokeStyle = "#ffb14e";
    ctx.lineWidth = 2;
    ctx.stroke();
  }
}

function drawImageLayer(img, layer, alpha, dim) {
  if (!img || !layer || !img.complete || img.naturalWidth === 0) return;

  ctx.save();
  ctx.translate(canvas.width / 2, canvas.height / 2);
  ctx.scale(view.zoom, view.zoom);
  ctx.translate(-view.x, -view.y);

  // one matrix covers both the solved affine and the slider built rotation/scale/offset.
  // canvas order is column major: x' = a x + c y + e, y' = b x + d y + f
  const m = calMatrix(layer);
  ctx.transform(m.a, m.c, m.b, m.d, m.tx, m.ty);

  ctx.globalAlpha = alpha;
  ctx.drawImage(img, 0, 0, layer.width, layer.height);
  if (layer === currentLayer) layerPainted = true;

  // darken the base with an explicit pass. canvas filter on a drawimage turned out to be a no
  // op here, and fading both layers is what made them mud in the first place.
  if (dim) {
    ctx.globalAlpha = 0.45;
    ctx.fillStyle = "#000";
    ctx.fillRect(0, 0, layer.width, layer.height);
  }

  ctx.restore();
}

function drawChevron(x, y, up) {
  ctx.beginPath();

  if (up) {
    ctx.moveTo(x - 4, y + 3);
    ctx.lineTo(x, y - 2);
    ctx.lineTo(x + 4, y + 3);
  } else {
    ctx.moveTo(x - 4, y - 2);
    ctx.lineTo(x, y + 3);
    ctx.lineTo(x + 4, y - 2);
  }

  ctx.lineWidth = 2;
  ctx.strokeStyle = "#dfe6ee";
  ctx.stroke();
}

function drawSelf() {
  const me = project(state.x, state.z);
  const yaw = (state.yaw || 0) * Math.PI / 180;

  // unity forward for a yaw is (sin, cos) in the x/z plane
  const ahead = project(state.x + Math.sin(yaw) * 6, state.z + Math.cos(yaw) * 6);
  let dx = ahead.x - me.x;
  let dy = ahead.y - me.y;
  const len = Math.hypot(dx, dy) || 1;
  dx = (dx / len) * 16;
  dy = (dy / len) * 16;

  ctx.beginPath();
  ctx.arc(me.x, me.y, 6, 0, Math.PI * 2);
  ctx.fillStyle = "#ffffff";
  ctx.fill();
  ctx.strokeStyle = "#0b0e12";
  ctx.lineWidth = 2;
  ctx.stroke();

  ctx.beginPath();
  ctx.moveTo(me.x, me.y);
  ctx.lineTo(me.x + dx, me.y + dy);
  ctx.strokeStyle = "#4f8cff";
  ctx.lineWidth = 3;
  ctx.stroke();
}

// the game hands bots generated russian nicknames. these are the english names everyone
// actually calls them, keyed by wildspawn role in lowercase.
const ROLE_LABELS = {
  bossbully: "Reshala",
  bosskilla: "Killa",
  bosskillaagro: "Killa",
  bosskojaniy: "Shturman",
  bossgluhar: "Glukhar",
  bosssanitar: "Sanitar",
  bosstagilla: "Tagilla",
  bosstagillaagro: "Tagilla",
  bossknight: "Knight",
  bosszryachiy: "Zryachiy",
  bossboar: "Kaban",
  bossboarsniper: "Kaban",
  bosskolontay: "Kolontay",
  bosspartisan: "Partisan",
  bosswedge: "Wedge",
  bosswedgelab: "Wedge",
  bossbullyblackdiv: "Reshala",
  followerbully: "Reshala guard",
  followerbullyblackdiv: "Reshala guard",
  followerkojaniy: "Shturman guard",
  followergluharassault: "Glukhar guard",
  followergluharsecurity: "Glukhar guard",
  followergluharscout: "Glukhar guard",
  followergluharsnipe: "Glukhar guard",
  followersanitar: "Sanitar guard",
  followertagilla: "Tagilla guard",
  followerzryachiy: "Zryachiy guard",
  followerboar: "Kaban guard",
  followerboarclose1: "Kaban guard",
  followerboarclose2: "Kaban guard",
  followerbigpipe: "Big Pipe",
  followerbirdeye: "Birdeye",
  followerkolontayassault: "Kolontay guard",
  followerkolontaysecurity: "Kolontay guard",
  followerwedgelab: "Wedge guard",
  pmcbot: "Raider",
  pmcbotblackdiv: "Raider",
  pmcbotblackdivseason: "Raider",
  exusec: "Rogue",
  exusecfree: "Rogue",
  sectantpriest: "Cultist priest",
  sectactpriestevent: "Cultist priest",
  sectantwarrior: "Cultist",
  sectantpredvestnik: "Cultist",
  sectantprizrak: "Cultist",
  sectantoni: "Cultist",
  blackdivision: "Black Division",
  arenafighter: "Arena fighter",
  arenafighterevent: "Arena fighter"
};

function contactLabel(p) {
  const r = (p.role || "").toLowerCase();

  // human players keep their actual name, that is the only one that means anything
  if (r.includes("pmcusec") || r.includes("pmcbear")) return p.name || "?";

  const named = ROLE_LABELS[r];
  if (named) return named;

  if (r.startsWith("boss")) return "Boss";
  if (r.startsWith("follower")) return "Follower";
  if (r.includes("sectant") || r.includes("sectact")) return "Cultist";
  if (r.includes("pmcbot")) return "Raider";
  if (r.includes("exusec")) return "Rogue";
  if (r.includes("blackdivision")) return "Black Division";

  return "Scav";
}

function roleColor(role, side) {
  const r = (role || "").toLowerCase();

  if (r.includes("boss")) return "#ff3b3b";
  if (r.includes("pmcbot") || r.includes("exusec") || r.includes("sectant") ||
      r.includes("follower") || r.includes("raider") || r.includes("blackdivision")) return "#ffd23b";
  if (r.includes("pmcusec")) return "#3b7bff";
  if (r.includes("pmcbear")) return "#2fd16a";
  if (side === "Usec") return "#3b7bff";
  if (side === "Bear") return "#2fd16a";
  return "#d6d6d6";
}

/* ---------- calibration ---------- */

async function loadCalibration() {
  cal = defaultCal();

  // browser copy first. the server side file only carries rotation, scale and offsets, so an
  // anchored or axis matrix cannot survive the round trip through it, and this one can.
  try {
    const raw = localStorage.getItem("vzor.cal.v1." + mapId);
    if (raw) cal = Object.assign(cal, JSON.parse(raw));
  } catch (e) {
    // unreadable, defaults are fine
  }

  try {
    const r = await fetch("/calibration", { cache: "no-store" });
    const all = await r.json();
    if (all[mapId] && !(cal && cal.affine)) cal = Object.assign(cal, all[mapId]);
  } catch (e) {
    // no saved alignment, defaults are fine
  }

  syncInputs();
}

function syncInputs() {
  document.getElementById("rot").value = cal.rotation;
  document.getElementById("scale").value = cal.scale;
  document.getElementById("ox").value = cal.offsetX;
  document.getElementById("oy").value = cal.offsetY;
  document.getElementById("fx").checked = cal.flipX < 0;
  document.getElementById("fy").checked = cal.flipY < 0;
  document.getElementById("vrot").textContent = cal.rotation.toFixed(1);
  document.getElementById("vscale").textContent = cal.scale.toFixed(3);
  document.getElementById("vox").textContent = cal.offsetX.toFixed(1);
  document.getElementById("voy").textContent = cal.offsetY.toFixed(1);
}

function scheduleSave() {
  if (saveTimer) clearTimeout(saveTimer);
  saveTimer = setTimeout(saveCalibration, 500);
}

async function saveCalibration() {
  if (!mapId) return;

  // write the local copy first so a solve survives a refresh even if the post fails
  try {
    localStorage.setItem("vzor.cal.v1." + mapId, JSON.stringify(cal));
  } catch (e) {
    // private mode or full storage, the server copy still exists
  }

  try {
    const r = await fetch("/calibration", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify(Object.assign({ map: mapId }, cal))
    });

    const out = await r.json();
    setStatus(out.ok ? "art saved" : "save failed", out.ok ? "good" : "warn");
  } catch (e) {
    setStatus("save failed", "warn");
  }
}

/* ---------- point and click calibration ---------- */

function sampleKey() {
  return "vzor.samples.v1." + mapId;
}

function loadSamples(id) {
  try {
    const raw = localStorage.getItem("vzor.samples.v1." + id);
    return raw ? JSON.parse(raw) : [];
  } catch (e) {
    return [];
  }
}

function storeSamples() {
  try {
    localStorage.setItem(sampleKey(), JSON.stringify(samples));
  } catch (e) {
    // private mode or quota, not worth a tantrum
  }
  updateSampleUi();
}

function updateSampleUi() {
  const el = document.getElementById("samples");
  if (!el) return;

  el.textContent = samples.length === 0
    ? "0 points"
    : samples.length + (samples.length === 1 ? " point" : " points") + (lastError === null ? "" : "  fit " + lastError.toFixed(1) + "px");
}

// press P while standing somewhere, then click the art where you actually are
function capturePoint() {
  if (!state.inRaid || typeof state.x !== "number") {
    setStatus("no position yet", "warn");
    return;
  }

  pending = { x: state.x, z: state.z };
  canvas.style.cursor = "crosshair";
  setStatus("click the map where you are", "warn");
}

function addSample(clientX, clientY) {
  if (!pending || !currentLayer) return;

  const q = screenToImage(clientX, clientY);
  const p = calInvert(q, currentLayer);

  samples.push({ x: pending.x, z: pending.z, u: p.x, v: p.y });
  pending = null;
  canvas.style.cursor = "grab";
  storeSamples();

  if (samples.length >= 2) solveSamples();
  else setStatus("1 point, add another", "warn");
}

let lastError = null;

// least squares similarity fit, trying both the normal and mirrored model and keeping whichever
// explains the points with less error. two points is enough, three is better, ten is cheating.
// 3x3 linear solve, gaussian elimination with partial pivoting. null when the points are
// collinear or duplicated, in which case there is no unique affine
function solve3(m, b) {
  for (let col = 0; col < 3; col++) {
    let pivot = col;
    for (let r = col + 1; r < 3; r++) {
      if (Math.abs(m[r][col]) > Math.abs(m[pivot][col])) pivot = r;
    }
    if (Math.abs(m[pivot][col]) < 1e-9) return null;

    if (pivot !== col) {
      const tmp = m[col]; m[col] = m[pivot]; m[pivot] = tmp;
      const tb = b[col]; b[col] = b[pivot]; b[pivot] = tb;
    }

    for (let r = 0; r < 3; r++) {
      if (r === col) continue;
      const f = m[r][col] / m[col][col];
      for (let c = col; c < 3; c++) m[r][c] -= f * m[col][c];
      b[r] -= f * b[col];
    }
  }

  const out = [0, 0, 0];
  for (let i = 0; i < 3; i++) {
    if (Math.abs(m[i][i]) < 1e-9) return null;
    out[i] = b[i] / m[i][i];
  }
  return out;
}

// general affine fit. unlike rotation plus one scale this allows a different scale per axis,
// which is what the labs art needs, and it absorbs any shear the export introduced
// generic gaussian solve for n unknowns, used by the anchored fit
function solveN(m, b) {
  const n = b.length;

  for (let col = 0; col < n; col++) {
    let pivot = col;
    for (let r = col + 1; r < n; r++) {
      if (Math.abs(m[r][col]) > Math.abs(m[pivot][col])) pivot = r;
    }
    if (Math.abs(m[pivot][col]) < 1e-9) return null;

    if (pivot !== col) {
      const tmp = m[col]; m[col] = m[pivot]; m[pivot] = tmp;
      const tb = b[col]; b[col] = b[pivot]; b[pivot] = tb;
    }

    for (let r = 0; r < n; r++) {
      if (r === col) continue;
      const f = m[r][col] / m[col][col];
      for (let c = col; c < n; c++) m[r][c] -= f * m[col][c];
      b[r] -= f * b[col];
    }
  }

  const out = new Array(n).fill(0);
  for (let i = 0; i < n; i++) {
    if (Math.abs(m[i][i]) < 1e-9) return null;
    out[i] = b[i] / m[i][i];
  }
  return out;
}

// anchored fit. the map config already tells us the art rotation, so pinning two points is
// enough: horizontal scale, vertical scale and translation are the only unknowns left. this
// cannot shear or spin the art, it can only stretch it between the pins.
function solveAnchored(pairs) {
  if (!mapCfg || typeof mapCfg.rotation !== "number") return null;

  const theta = (mapCfg.rotation * Math.PI) / 180;
  const cosT = Math.cos(theta);
  const sinT = Math.sin(theta);

  const A = [[0, 0, 0, 0], [0, 0, 0, 0], [0, 0, 0, 0], [0, 0, 0, 0]];
  const b = [0, 0, 0, 0];

  for (const s of pairs) {
    const rx = [cosT * s.p.x, -sinT * s.p.y, 1, 0];
    const ry = [sinT * s.p.x, cosT * s.p.y, 0, 1];

    for (let i = 0; i < 4; i++) {
      for (let j = 0; j < 4; j++) A[i][j] += rx[i] * rx[j] + ry[i] * ry[j];
      b[i] += rx[i] * s.q.x + ry[i] * s.q.y;
    }
  }

  const u = solveN(A, b);
  if (!u) return null;

  const sx = u[0], sy = u[1], tx = u[2], ty = u[3];
  if (Math.abs(sx) < 1e-4 || Math.abs(sy) < 1e-4) return null;

  const a = cosT * sx, bb = -sinT * sy;
  const c = sinT * sx, d = cosT * sy;

  let err = 0;
  for (const s of pairs) {
    const ex = a * s.p.x + bb * s.p.y + tx - s.q.x;
    const ey = c * s.p.x + d * s.p.y + ty - s.q.y;
    err += ex * ex + ey * ey;
  }

  return {
    a: a, b: bb, c: c, d: d, tx: tx, ty: ty,
    sx: sx, sy: sy,
    rotation: mapCfg.rotation,
    scale: Math.sqrt(Math.abs(sx * sy)) || 1,
    rms: Math.sqrt(err / pairs.length)
  };
}

// rotation plus independent x and y scale, with the axes kept orthogonal. a free affine has
// six degrees of freedom and will happily shear the map into a parallelogram when the points
// are close to a line, which is what happened on labs: tiny residual, unusable art.
function solveAxes(pairs) {
  let mx = 0, my = 0, mqx = 0, mqy = 0;
  for (const s of pairs) {
    mx += s.p.x; my += s.p.y;
    mqx += s.q.x; mqy += s.q.y;
  }
  mx /= pairs.length; my /= pairs.length;
  mqx /= pairs.length; mqy /= pairs.length;

  const ps = pairs.map(s => ({ x: s.p.x - mx, y: s.p.y - my }));
  const qs = pairs.map(s => ({ x: s.q.x - mqx, y: s.q.y - mqy }));

  // if the clicked art points sit on a line, one axis is unconstrained and any solver will
  // invent a scale for it. refuse instead of drawing nonsense.
  let sxx = 0, syy = 0, sxy = 0;
  for (const p of ps) { sxx += p.x * p.x; syy += p.y * p.y; sxy += p.x * p.y; }
  const trace = sxx + syy;
  const det = sxx * syy - sxy * sxy;
  if (trace < 1e-6 || det / (trace * trace) < 0.02) return null;

  function fit(theta) {
    const cos = Math.cos(theta);
    const sin = Math.sin(theta);

    let numX = 0, denX = 0, numY = 0, denY = 0;
    for (let i = 0; i < ps.length; i++) {
      const rx = cos * qs[i].x + sin * qs[i].y;
      const ry = -sin * qs[i].x + cos * qs[i].y;
      numX += rx * ps[i].x; denX += ps[i].x * ps[i].x;
      numY += ry * ps[i].y; denY += ps[i].y * ps[i].y;
    }

    if (denX < 1e-6 || denY < 1e-6) return null;

    const sx = numX / denX;
    const sy = numY / denY;
    if (Math.abs(sx) < 1e-4 || Math.abs(sy) < 1e-4) return null;

    const a = cos * sx, b = -sin * sy;
    const c = sin * sx, d = cos * sy;

    let err = 0;
    for (let i = 0; i < ps.length; i++) {
      const ex = a * ps[i].x + b * ps[i].y - qs[i].x;
      const ey = c * ps[i].x + d * ps[i].y - qs[i].y;
      err += ex * ex + ey * ey;
    }

    return { a: a, b: b, c: c, d: d, sx: sx, sy: sy, theta: theta, rms: Math.sqrt(err / ps.length) };
  }

  let best = null;

  // coarse sweep then a fine pass around the winner. one axis search, so this is cheap
  for (let deg = 0; deg < 180; deg += 1) {
    const f = fit((deg * Math.PI) / 180);
    if (f && (!best || f.rms < best.rms)) best = f;
  }

  if (!best) return null;

  for (let step = -1; step <= 1; step += 0.02) {
    const f = fit(best.theta + (step * Math.PI) / 180);
    if (f && f.rms < best.rms) best = f;
  }

  return {
    a: best.a, b: best.b, c: best.c, d: best.d,
    tx: mqx - (best.a * mx + best.b * my),
    ty: mqy - (best.c * mx + best.d * my),
    rms: best.rms
  };
}

function solveSamples() {
  if (!currentLayer || samples.length < 2) return null;

  const pairs = samples.map(s => ({
    p: { x: s.u, y: s.v },
    q: worldToImage(s.x, s.z, currentLayer)
  }));

  let affineFit = null;

  // anchored is only meaningful with two or more pins, and it is the model we would rather
  // trust: fixed rotation, so it stretches the art instead of warping it
  let anchoredFit = pairs.length >= 2 ? solveAnchored(pairs) : null;

  if (pairs.length >= 3) {
    const affine = solveAxes(pairs);

    if (affine) {
      let error = 0;
      for (const s of pairs) {
        const ex = (affine.a * s.p.x + affine.b * s.p.y + affine.tx) - s.q.x;
        const ey = (affine.c * s.p.x + affine.d * s.p.y + affine.ty) - s.q.y;
        error += ex * ex + ey * ey;
      }

      const det = affine.a * affine.d - affine.b * affine.c;
      affineFit = {
        affine: affine,
        rms: Math.sqrt(error / pairs.length),
        rotation: Math.atan2(affine.c, affine.a) * 180 / Math.PI,
        scale: Math.sqrt(Math.abs(det)) || 1
      };
    }
  }

  const mp = { x: 0, y: 0 };
  const mq = { x: 0, y: 0 };
  for (const s of pairs) {
    mp.x += s.p.x; mp.y += s.p.y;
    mq.x += s.q.x; mq.y += s.q.y;
  }
  mp.x /= pairs.length; mp.y /= pairs.length;
  mq.x /= pairs.length; mq.y /= pairs.length;

  let den = 0;
  let normalRe = 0, normalIm = 0;
  let mirrorRe = 0, mirrorIm = 0;

  for (const s of pairs) {
    const px = s.p.x - mp.x;
    const py = s.p.y - mp.y;
    const qx = s.q.x - mq.x;
    const qy = s.q.y - mq.y;

    // q * conj(p). fitting q = w p
    normalRe += qx * px + qy * py;
    normalIm += qy * px - qx * py;

    // q * p. fitting q = w conj(p), which is the mirrored case
    mirrorRe += qx * px - qy * py;
    mirrorIm += qy * px + qx * py;

    den += px * px + py * py;
  }

  if (den < 1e-6) return null;

  const models = [];

  for (const flipped of [false, true]) {
    const re = (flipped ? mirrorRe : normalRe) / den;
    const im = (flipped ? mirrorIm : normalIm) / den;

    // q = w p is [[re, -im], [im, re]]. q = w conj(p) is [[re, im], [im, -re]].
    const a = re;
    const b = flipped ? im : -im;
    const c = im;
    const d = flipped ? -re : re;

    const tx = mq.x - (a * mp.x + b * mp.y);
    const ty = mq.y - (c * mp.x + d * mp.y);

    let error = 0;
    for (const s of pairs) {
      const ex = (a * s.p.x + b * s.p.y + tx) - s.q.x;
      const ey = (c * s.p.x + d * s.p.y + ty) - s.q.y;
      error += ex * ex + ey * ey;
    }

    models.push({ a, b, c, d, tx, ty, flipped, rms: Math.sqrt(error / pairs.length) });
  }

  // two points can be satisfied by either handedness, so only accept the mirrored model when
  // it is meaningfully better. otherwise you get a reflected map from a lazy two point fit.
  const best = models[1].rms < models[0].rms - 0.5 ? models[1] : models[0];

  const scale = Math.hypot(best.a, best.c);
  const rotation = Math.atan2(best.c, best.a) * 180 / Math.PI;

  const cx = currentLayer.width / 2;
  const cy = currentLayer.height / 2;

  // t = centre + offset - A centre, therefore offset = t - centre + A centre
  const ax = best.a * cx + best.b * cy;
  const ay = best.c * cx + best.d * cy;

  cal = {
    rotation: rotation,
    scale: scale,
    offsetX: best.tx - cx + ax,
    offsetY: best.ty - cy + ay,
    flipX: 1,
    flipY: best.flipped ? -1 : 1
  };

  // pick whichever model explains the pins best. anchored wins ties because it is the most
  // constrained, so it is the least likely to invent geometry that is not in the art
  let model = "similarity";
  let pick = { rms: best.rms, model: "similarity", matrix: null, rotation: rotation, scale: scale };

  if (affineFit && affineFit.rms < pick.rms - 0.01) {
    pick = { rms: affineFit.rms, model: "axis", matrix: affineFit, rotation: affineFit.rotation, scale: affineFit.scale };
  }

  if (anchoredFit && anchoredFit.rms <= pick.rms + 0.5) {
    pick = { rms: anchoredFit.rms, model: "anchored", matrix: anchoredFit, rotation: anchoredFit.rotation, scale: anchoredFit.scale };
  }

  model = pick.model;
  lastError = pick.rms;

  if (pick.matrix) {
    cal.affine = {
      a: pick.matrix.a, b: pick.matrix.b,
      c: pick.matrix.c, d: pick.matrix.d,
      tx: pick.matrix.tx, ty: pick.matrix.ty
    };
    cal.rotation = pick.rotation;
    cal.scale = pick.scale;
  } else {
    delete cal.affine;
  }

  syncInputs();
  updateSampleUi();
  draw();
  scheduleSave();

  const note = samples.length < 3 ? " (add a third point)" : "";
  const note2 = (pairs.length >= 3 && !affineFit) ? " (points nearly in a line)" : "";

  setStatus("fit " + lastError.toFixed(1) + "px, " + model + ", " + samples.length + " points" +
    (best.flipped && model === "similarity" ? " mirrored" : "") + note + note2, "good");
  return cal;
}

function clearSamples() {
  samples = [];
  lastError = null;
  storeSamples();
  setStatus("points cleared", "dim");
}

function setFollow(on) {
  follow = on;

  const button = document.getElementById("follow");
  if (button) button.textContent = follow ? "follow: on" : "follow: off";

  try { localStorage.setItem("vzor.follow", follow ? "1" : "0"); } catch (e) { }

  if (follow && state.inRaid && currentLayer && typeof state.x === "number") {
    const p = worldToImage(state.x, state.z, currentLayer);
    view.x = p.x;
    view.y = p.y;
    draw();
  }
}

function bindCalibration() {
  const rot = document.getElementById("rot");
  const scale = document.getElementById("scale");
  const ox = document.getElementById("ox");
  const oy = document.getElementById("oy");
  const fx = document.getElementById("fx");
  const fy = document.getElementById("fy");

  function changed() {
    // touching a slider means manual control again
    if (cal.affine) delete cal.affine;
    cal.rotation = parseFloat(rot.value);
    cal.scale = parseFloat(scale.value);
    cal.offsetX = parseFloat(ox.value);
    cal.offsetY = parseFloat(oy.value);
    cal.flipX = fx.checked ? -1 : 1;
    cal.flipY = fy.checked ? -1 : 1;

    syncInputs();
    draw();
    scheduleSave();
  }

  rot.addEventListener("input", changed);
  scale.addEventListener("input", changed);
  ox.addEventListener("input", changed);
  oy.addEventListener("input", changed);
  fx.addEventListener("change", changed);
  fy.addEventListener("change", changed);

  function setRotation(degrees) {
    if (cal.affine) delete cal.affine;
    let value = degrees;
    while (value > 180) value -= 360;
    while (value < -180) value += 360;
    cal.rotation = value;
    syncInputs();
    draw();
    scheduleSave();
  }

  document.getElementById("rotm90").addEventListener("click", () => setRotation(cal.rotation - 90));
  document.getElementById("rot90").addEventListener("click", () => setRotation(cal.rotation + 90));
  document.getElementById("rot180").addEventListener("click", () => setRotation(cal.rotation + 180));

  document.getElementById("take").addEventListener("click", capturePoint);
  document.getElementById("solve").addEventListener("click", solveSamples);
  document.getElementById("clearsamples").addEventListener("click", clearSamples);
  document.getElementById("save").addEventListener("click", saveCalibration);

  document.getElementById("reset").addEventListener("click", () => {
    cal = defaultCal();
    syncInputs();
    draw();
    saveCalibration();
  });


  document.getElementById("stack").addEventListener("change", e => {
    showStack = e.target.checked;
    try { localStorage.setItem("vzor.stack", showStack ? "1" : "0"); } catch (err) { }
    draw();
  });

  document.getElementById("contacts").addEventListener("change", e => {
    showContacts = e.target.checked;
    try { localStorage.setItem("vzor.contacts", showContacts ? "1" : "0"); } catch (err) { }
    draw();
  });

  document.getElementById("names").addEventListener("change", e => {
    showNames = e.target.checked;
    try { localStorage.setItem("vzor.names", showNames ? "1" : "0"); } catch (err) { }
    draw();
  });

  document.getElementById("follow").addEventListener("click", () => setFollow(!follow));

  document.getElementById("alignment").addEventListener("click", () => {
    document.getElementById("panel").classList.toggle("hidden");
  });

  document.getElementById("fit").addEventListener("click", fitView);

  document.getElementById("copy").addEventListener("click", () => {
    navigator.clipboard.writeText(JSON.stringify(Object.assign({ map: mapId }, cal), null, 2));
    setStatus("copied", "good");
  });

  document.getElementById("toggle").addEventListener("click", () => {
    const body = document.getElementById("body");
    const hidden = body.style.display === "none";
    body.style.display = hidden ? "" : "none";
    document.getElementById("toggle").textContent = hidden ? "hide" : "show";
  });
}

/* ---------- input ---------- */

let dragging = false;
let dragLast = null;

canvas.addEventListener("mousedown", e => {
  dragging = true;
  dragLast = { x: e.clientX, y: e.clientY };
  lastDown = { x: e.clientX, y: e.clientY };
  canvas.classList.add("dragging");
});

window.addEventListener("mouseup", e => {
  const wasDrag = lastDown ? Math.hypot(e.clientX - lastDown.x, e.clientY - lastDown.y) : 99;

  dragging = false;
  dragLast = null;
  lastDown = null;
  canvas.classList.remove("dragging");

  // a click, not a drag, while a point is pending means "this is where I am"
  if (pending && wasDrag < 5) addSample(e.clientX, e.clientY);
});

window.addEventListener("mousemove", e => {
  if (!dragging || !dragLast) return;

  const dx = e.clientX - dragLast.x;
  const dy = e.clientY - dragLast.y;
  dragLast = { x: e.clientX, y: e.clientY };

  // shift drags the art instead of the view, which is the fastest way to line a floor up
  if (e.shiftKey) {
    cal.offsetX += dx / view.zoom;
    cal.offsetY += dy / view.zoom;
    syncInputs();
    scheduleSave();
  } else {
    if (follow) setFollow(false);
    view.x -= dx / view.zoom;
    view.y -= dy / view.zoom;
  }

  draw();
});

canvas.addEventListener("wheel", e => {
  e.preventDefault();

  if (e.shiftKey) {
    cal.scale *= e.deltaY < 0 ? 1.01 : 1 / 1.01;
    syncInputs();
    scheduleSave();
    draw();
    return;
  }

  const before = screenToImage(e.clientX, e.clientY);
  view.zoom *= e.deltaY < 0 ? 1.15 : 1 / 1.15;
  const after = screenToImage(e.clientX, e.clientY);
  view.x += before.x - after.x;
  view.y += before.y - after.y;
  draw();
}, { passive: false });

window.addEventListener("keydown", e => {
  const step = e.shiftKey ? 25 : 5;
  let handled = true;

  switch (e.key) {
    case "ArrowLeft": cal.offsetX -= step; break;
    case "ArrowRight": cal.offsetX += step; break;
    case "ArrowUp": cal.offsetY -= step; break;
    case "ArrowDown": cal.offsetY += step; break;
    case "[" : cal.rotation -= e.shiftKey ? 5 : 0.5; break;
    case "]" : cal.rotation += e.shiftKey ? 5 : 0.5; break;
    case "+" : case "=" : cal.scale *= 1.005; break;
    case "-" : case "_" : cal.scale /= 1.005; break;
    case "c": case "C": setFollow(!follow); handled = false; break;
    case "p": case "P": capturePoint(); handled = false; break;
    case "s": case "S": saveCalibration(); handled = false; break;
    case "f": case "F": fitView(); handled = false; break;
    case "Escape":
      if (pending) {
        pending = null;
        canvas.style.cursor = "grab";
        setStatus("cancelled", "dim");
      } else if (selectedLoot) {
        selectedLoot = null;
        draw();
      }
      handled = false;
      break;
    default: handled = false;
  }

  if (handled) {
    e.preventDefault();
    syncInputs();
    draw();
    scheduleSave();
  }
});

/* ---------- asset hot reload ---------- */

// the plugin reports the mtime of app.js. if it changes while this tab is open, reload, so an
// edit lands without anyone remembering to press f5.
let assetVersion = null;

async function checkVersion() {
  try {
    const r = await fetch("/version", { cache: "no-store" });
    if (!r.ok) {
      setTimeout(checkVersion, 3000);
      return;
    }

    const current = await r.json();

    if (assetVersion === null) assetVersion = current.v;
    else if (current.v !== assetVersion) {
      location.reload();
      return;
    }
  } catch (e) {
    // old plugin without the endpoint, or not in raid yet
  }

  setTimeout(checkVersion, 3000);
}

/* ---------- loot table ---------- */

let lootOpen = false;
let lootRefreshTimer = null;
let wasInRaid = false;
let raidEndedAt = 0;
let lootItems = [];
let selectedLoot = null;

// names you want on the map no matter what. by name, not by position, because loot moves every
// raid and a pin to one graphics card is worthless next time.
let lootFavorites = new Set();

function lootFavKey(name) {
  return String(name || "").trim().toLowerCase();
}

function loadLootFavorites() {
  try {
    const raw = localStorage.getItem("vzor.lootfav.v1");
    if (raw) lootFavorites = new Set(JSON.parse(raw).map(lootFavKey));
  } catch (e) {
    lootFavorites = new Set();
  }
}

function saveLootFavorites() {
  try {
    localStorage.setItem("vzor.lootfav.v1", JSON.stringify([...lootFavorites]));
  } catch (e) {
    // private mode, favourites just will not persist
  }
}

// containers and what they hold, kept in step with the loot poll
let containerItems = [];
let containerTimer = null;

async function fetchContainers() {
  try {
    const r = await fetch("/containers", { cache: "no-store" });
    if (!r.ok) return;

    containerItems = await r.json();
    draw();
  } catch (e) {
    // older plugin builds have no endpoint, favourites just stay loose loot only
  }
}

function toggleLootFavorite(name) {
  const key = lootFavKey(name);
  if (!key) return;

  if (lootFavorites.has(key)) lootFavorites.delete(key);
  else lootFavorites.add(key);

  saveLootFavorites();
  renderLoot();
  draw();
}

function lootCategory(type) {
  const s = (type || "").toLowerCase();

  if (s.includes("weapon") || s.includes("knife") || s.includes("throw")) return "weapon";
  if (s.includes("ammo")) return "ammo";
  if (s.includes("med") || s.includes("stim")) return "medical";
  if (s.includes("food") || s.includes("drink")) return "food";
  if (s.includes("key")) return "key";
  if (s.includes("armor") || s.includes("helmet") || s.includes("vest") ||
      s.includes("rig") || s.includes("backpack")) return "gear";
  if (s.includes("mod")) return "mod";
  if (s.includes("barter") || s.includes("valuable")) return "barter";

  return "other";
}

function lootDistance(item) {
  if (typeof state.x !== "number" || typeof state.z !== "number") return 0;
  return Math.hypot(item.x - state.x, item.z - state.z);
}

function escapeHtml(text) {
  return String(text).replace(/[&<>"]/g, c => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;" }[c]));
}

// the plugin only builds the list while a page is asking for it, so the first request can come
// back empty. retry a couple of times and then leave it alone, no interval.
async function fetchLoot(attempt) {
  const tryIndex = attempt || 0;

  try {
    const r = await fetch("/loot", { cache: "no-store" });
    if (!r.ok) throw new Error("no loot feed");
    lootItems = await r.json();
  } catch (e) {
    lootItems = [];
  }

  renderLoot();

  // the plugin rebuilds its list on a five second tick, so keep asking for about that long
  // before deciding the raid genuinely has no loot
  if (lootItems.length === 0 && tryIndex < 5 && lootOpen) {
    setTimeout(() => fetchLoot(tryIndex + 1), 1000);
  }
}

function renderLoot() {
  const rows = document.getElementById("lootrows");
  const count = document.getElementById("lootcount");
  if (!rows) return;

  const search = (document.getElementById("lootsearch").value || "").trim().toLowerCase();
  const category = document.getElementById("lootcat").value;
  const sort = document.getElementById("lootsort").value;

  const list = lootItems.filter(item => {
    if (search && !(item.n || "").toLowerCase().includes(search)) return false;
    if (category && lootCategory(item.t) !== category) return false;
    return true;
  });

  for (const item of list) item.d = lootDistance(item);

  if (sort === "price") list.sort((a, b) => b.p - a.p);
  else if (sort === "name") list.sort((a, b) => String(a.n).localeCompare(String(b.n)));
  else list.sort((a, b) => a.d - b.d);

  const total = list.length;
  const shown = list.slice(0, 300);

  count.textContent = total === 0 ? "nothing" : shown.length + " of " + total;

  let html = "";
  for (const item of shown) {
    const fav = lootFavorites.has(lootFavKey(item.n));

    html += '<div class="lootrow" data-x="' + item.x + '" data-z="' + item.z +
      '" data-n="' + escapeHtml(item.n || "?") + '" data-p="' + (item.p || 0) + '">' +
      '<span class="fav' + (fav ? " on" : "") + '" data-fav="' + escapeHtml(item.n || "") +
      '" title="keep this on the map">&#9733;</span>' +
      '<span class="name">' + escapeHtml(item.n || "?") + '</span>' +
      '<span class="price">' + (item.p ? item.p.toLocaleString() : "-") + '</span>' +
      '<span class="dist">' + Math.round(item.d) + 'm</span>' +
      '</div>';
  }

  rows.innerHTML = html;
}

function clearMap() {
  mapId = null;
  mapCfg = null;
  currentLayer = null;
  image = null;
  baseLayer = null;
  baseImage = null;
  labels = [];
  samples = [];
  selectedLoot = null;

  const name = document.getElementById("mapname");
  if (name) name.textContent = "no raid";

  const floor = document.getElementById("floor");
  if (floor) floor.textContent = "";
}

function updateWaitingOverlay() {
  const el = document.getElementById("waiting");
  if (!el) return;

  if (state.inRaid) {
    el.classList.remove("show");
    return;
  }

  const ended = raidEndedAt > 0 && performance.now() - raidEndedAt < 10000;
  const label = document.getElementById("waitingText");
  const dots = el.querySelector(".dots");

  if (label) label.textContent = ended ? "Raid Ended" : "waiting for raid";
  if (dots) dots.style.display = ended ? "none" : "";

  el.classList.add("show");
}

// loot keeps spawning for the first couple of minutes, so poll on a slow tick rather than taking
// one late snapshot and hoping it caught everything
const LOOT_REFRESH_MS = 30000;

function scheduleLootRefresh() {
  stopLootRefresh();

  lootRefreshTimer = setInterval(() => {
    // not gated on the panel any more, favourites are drawn from this feed
    fetchLoot(0);
  }, LOOT_REFRESH_MS);

  // container contents are fixed at raid start, a slow poll is plenty
  fetchContainers();

  if (containerTimer) clearInterval(containerTimer);
  containerTimer = setInterval(fetchContainers, 60000);
}

function stopLootRefresh() {
  if (lootRefreshTimer) {
    clearInterval(lootRefreshTimer);
    lootRefreshTimer = null;
  }

  if (containerTimer) {
    clearInterval(containerTimer);
    containerTimer = null;
  }
}

function setLootOpen(open) {
  lootOpen = open;
  document.getElementById("lootpanel").classList.toggle("hidden", !open);

  if (open) fetchLoot(0);
}

function bindLoot() {
  loadLootFavorites();
  document.getElementById("loot").addEventListener("click", () => setLootOpen(!lootOpen));
  document.getElementById("lootclose").addEventListener("click", () => setLootOpen(false));
  document.getElementById("lootrefresh").addEventListener("click", () => fetchLoot(0));

  document.getElementById("lootsearch").addEventListener("input", renderLoot);
  document.getElementById("lootcat").addEventListener("change", renderLoot);
  document.getElementById("lootsort").addEventListener("change", renderLoot);

  document.getElementById("lootrows").addEventListener("click", e => {
    const star = e.target.closest(".fav");
    if (star) {
      toggleLootFavorite(star.dataset.fav);
      return;
    }

    const row = e.target.closest(".lootrow[data-x]");
    if (!row || !currentLayer) return;

    const x = parseFloat(row.dataset.x);
    const z = parseFloat(row.dataset.z);
    const q = worldToImage(x, z, currentLayer);

    selectedLoot = {
      x: x,
      z: z,
      n: row.dataset.n || "?",
      p: parseInt(row.dataset.p || "0", 10)
    };

    // centre once. after that it is a marker you can pan away from, not a camera lock.
    view.x = q.x;
    view.y = q.y;
    draw();
  });
}

/* ---------- quests and contacts ---------- */

function setInfoOpen(open, tab) {
  if (tab) infoTab = tab;
  infoOpen = open;

  document.getElementById("infopanel").classList.toggle("hidden", !open);
  if (open) renderInfo(true);
}

function renderInfo(force) {
  if (!infoOpen) return;

  const now = performance.now();
  if (!force && now - lastInfoRender < 700) return;
  lastInfoRender = now;

  const onQuests = infoTab === "quests";

  document.getElementById("tabquests").classList.toggle("active", onQuests);
  document.getElementById("tabcontacts").classList.toggle("active", !onQuests);

  const questHost = document.getElementById("questbody");
  const contactHost = document.getElementById("contactsbody");

  questHost.classList.toggle("hidden", !onQuests);
  contactHost.classList.toggle("hidden", onQuests);

  if (onQuests) renderQuests(questHost);
  else renderContacts(contactHost);
}

function renderQuests(host) {
  const all = (state.quest && state.quest.quests) || [];
  const quests = showStory ? all : all.filter(q => !q.story);
  document.getElementById("infocount").textContent = quests.length + " active";

  if (quests.length === 0) {
    host.innerHTML = '<div class="dim">no active quests</div>';
    return;
  }

  let html = "";

  for (const quest of quests) {
    html += '<div class="quest">' +
      '<div class="qname">' + escapeHtml(quest.n) + '</div>' +
      '<div class="qstatus">' + escapeHtml(quest.s) + '</div>';

    if (quest.i && quest.i.length) {
      html += '<div class="qitems">' + quest.i.map(escapeHtml).join(" &middot; ") + '</div>';
    }

    html += '</div>';
  }

  host.innerHTML = html;
}

function renderContacts(host) {
  // scavs are everywhere and individually worthless. this panel is for the things that matter:
  // players, bosses, their followers, raiders and rogues.
  const players = (state.players || []).filter(p => !p.me && roleColor(p.role, p.side) !== "#d6d6d6");
  players.sort((a, b) => (a.dist || 0) - (b.dist || 0));

  document.getElementById("infocount").textContent = players.length + " contacts";

  let html = '<div class="qrow head"><span class="name">contact</span>' +
    '<span class="kind">type</span><span class="dist">dist</span></div>';

  for (const p of players) {
    let arrow = "";
    if (typeof p.y === "number" && typeof state.y === "number") {
      const dy = p.y - state.y;
      if (Math.abs(dy) > 1.2) arrow = dy > 0 ? " \u25B2" : " \u25BC";
    }

    const type = p.ai ? (p.role || "ai") : "player";

    html += '<div class="qrow" data-x="' + p.x + '" data-z="' + p.z + '">' +
      '<span class="name">' + escapeHtml(contactLabel(p)) + '</span>' +
      '<span class="kind">' + escapeHtml(type) + '</span>' +
      '<span class="dist">' + Math.round(p.dist || 0) + 'm' + arrow + '</span>' +
      '</div>';
  }

  host.innerHTML = html;
}

function bindInfo() {
  document.getElementById("questsbtn").addEventListener("click", () => setInfoOpen(true, "quests"));
  document.getElementById("contactsbtn").addEventListener("click", () => setInfoOpen(true, "contacts"));
  document.getElementById("infoclose").addEventListener("click", () => setInfoOpen(false));

  document.getElementById("storyq").addEventListener("change", e => {
    showStory = e.target.checked;
    try { localStorage.setItem("vzor.storyq", showStory ? "1" : "0"); } catch (err) { }
    renderInfo(true);
  });

  document.getElementById("tabquests").addEventListener("click", () => {
    infoTab = "quests";
    renderInfo(true);
  });

  document.getElementById("tabcontacts").addEventListener("click", () => {
    infoTab = "contacts";
    renderInfo(true);
  });

  document.getElementById("contactsbody").addEventListener("click", e => {
    const row = e.target.closest(".qrow[data-x]");
    if (!row || !currentLayer) return;

    const q = worldToImage(parseFloat(row.dataset.x), parseFloat(row.dataset.z), currentLayer);
    view.x = q.x;
    view.y = q.y;
    draw();
  });

  document.getElementById("qitems").addEventListener("change", e => {
    showQuestLoot = e.target.checked;
    try { localStorage.setItem("vzor.qitems", showQuestLoot ? "1" : "0"); } catch (err) { }
    draw();
  });

  document.getElementById("qzones").addEventListener("change", e => {
    showQuestZones = e.target.checked;
    try { localStorage.setItem("vzor.qzones", showQuestZones ? "1" : "0"); } catch (err) { }
    draw();
  });
}

/* ---------- boot ---------- */

try { showNames = localStorage.getItem("vzor.names") !== "0"; } catch (e) { }
document.getElementById("names").checked = showNames;

try { showContacts = localStorage.getItem("vzor.contacts") !== "0"; } catch (e) { }
document.getElementById("contacts").checked = showContacts;

try { showStack = localStorage.getItem("vzor.stack") !== "0"; } catch (e) { }
document.getElementById("stack").checked = showStack;

try { showStory = localStorage.getItem("vzor.storyq") === "1"; } catch (e) { }
document.getElementById("storyq").checked = showStory;

try { showQuestLoot = localStorage.getItem("vzor.qitems") !== "0"; } catch (e) { }
document.getElementById("qitems").checked = showQuestLoot;

try { showQuestZones = localStorage.getItem("vzor.qzones") !== "0"; } catch (e) { }
document.getElementById("qzones").checked = showQuestZones;

try { follow = localStorage.getItem("vzor.follow") !== "0"; } catch (e) { }
setFollow(follow);

bindCalibration();
bindLoot();
bindInfo();
resize();
updateSampleUi();
checkVersion();
poll();
