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
let mapFailed = false;

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
  mapId = id;
  mapFailed = false;
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

async function poll() {
  try {
    const r = await fetch("/state", { cache: "no-store" });
    state = await r.json();

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

  if (follow && state.inRaid && currentLayer && typeof state.x === "number") {
    const p = worldToImage(state.x, state.z, currentLayer);
    view.x = p.x;
    view.y = p.y;
  }

  updateCoords();
  draw();
  setTimeout(poll, 120);
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

  const cx = layer.width / 2;
  const cy = layer.height / 2;
  const t = (cal.rotation * Math.PI) / 180;

  ctx.save();
  ctx.translate(canvas.width / 2, canvas.height / 2);
  ctx.scale(view.zoom, view.zoom);
  ctx.translate(-view.x, -view.y);
  ctx.translate(cx + cal.offsetX, cy + cal.offsetY);
  ctx.rotate(t);
  ctx.scale(cal.scale, cal.scale);
  ctx.scale(cal.flipX, cal.flipY);
  ctx.translate(-cx, -cy);
  ctx.globalAlpha = alpha;
  ctx.drawImage(img, 0, 0, layer.width, layer.height);

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

  try {
    const r = await fetch("/calibration", { cache: "no-store" });
    const all = await r.json();
    if (all[mapId]) cal = Object.assign(cal, all[mapId]);
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
function solveSamples() {
  if (!currentLayer || samples.length < 2) return null;

  const pairs = samples.map(s => ({
    p: { x: s.u, y: s.v },
    q: worldToImage(s.x, s.z, currentLayer)
  }));

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

  lastError = best.rms;
  syncInputs();
  updateSampleUi();
  draw();
  scheduleSave();

  const note = samples.length < 3 ? " (add a third point)" : "";
  setStatus("fit " + best.rms.toFixed(1) + "px" + (best.flipped ? " mirrored" : "") + note, "good");
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
      if (pending) { pending = null; canvas.style.cursor = "grab"; setStatus("cancelled", "dim"); }
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

/* ---------- boot ---------- */

try { showNames = localStorage.getItem("vzor.names") !== "0"; } catch (e) { }
document.getElementById("names").checked = showNames;

try { showContacts = localStorage.getItem("vzor.contacts") !== "0"; } catch (e) { }
document.getElementById("contacts").checked = showContacts;

try { showStack = localStorage.getItem("vzor.stack") !== "0"; } catch (e) { }
document.getElementById("stack").checked = showStack;

try { follow = localStorage.getItem("vzor.follow") !== "0"; } catch (e) { }
setFollow(follow);

bindCalibration();
resize();
updateSampleUi();
checkVersion();
poll();
