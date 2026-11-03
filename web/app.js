// live map page. polls /state, draws the svg layer under it with a transform you can nudge
// from the panel until the dots line up with the art, then saves that transform per map.

const canvas = document.getElementById("map");
const ctx = canvas.getContext("2d");

let state = { inRaid: false };
let mapCfg = null;
let mapId = null;
let currentLayer = null;
let image = null;
let mapFailed = false;

let cal = { rotation: 0, scale: 1, offsetX: 0, offsetY: 0, flipX: 1, flipY: 1 };
let view = { zoom: 1, x: 0, y: 0 };
let saveTimer = null;

const DEFAULT_CAL = { rotation: 0, scale: 1, offsetX: 0, offsetY: 0, flipX: 1, flipY: 1 };

/* ---------- sizing ---------- */

function resize() {
  canvas.width = window.innerWidth;
  canvas.height = window.innerHeight;
  draw();
}

window.addEventListener("resize", resize);

/* ---------- coordinate maths ---------- */

// world (x, z) to image pixels, using the layer's own world extent
function worldToImage(x, z, layer) {
  const b = layer.imageBounds;
  const sx = layer.width / (b.Max.x - b.Min.x);
  const sy = layer.height / (b.Max.y - b.Min.y);
  return { u: (x - b.Min.x) * sx, v: (b.Max.y - z) * sy };
}

// image pixels through the alignment transform, about the image centre
function alignPoint(u, v, layer) {
  const cx = layer.width / 2;
  const cy = layer.height / 2;

  const du = (u - cx) * cal.flipX * cal.scale;
  const dv = (v - cy) * cal.flipY * cal.scale;

  const t = (cal.rotation * Math.PI) / 180;
  const cos = Math.cos(t);
  const sin = Math.sin(t);

  return {
    x: cx + cal.offsetX + du * cos - dv * sin,
    y: cy + cal.offsetY + du * sin + dv * cos
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
  const p = worldToImage(x, z, currentLayer);
  return toScreen(alignPoint(p.u, p.v, currentLayer));
}

/* ---------- map loading ---------- */

async function loadMap(id) {
  mapId = id;
  mapFailed = false;
  currentLayer = null;
  image = null;

  try {
    const r = await fetch("/maps/" + encodeURIComponent(id) + "/map.json", { cache: "no-store" });
    if (!r.ok) throw new Error("no art for " + id);
    mapCfg = await r.json();
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
  document.getElementById("mapname").textContent = mapCfg.display;
}

function pickLayer(force) {
  if (!mapCfg) return;

  let chosen = null;

  // full 3d containment. matching on height alone picks underground tunnels whenever the
  // player is standing above one, which is how the first version ended up drawing dots on
  // the wrong floor.
  if (state.inRaid && typeof state.y === "number" && typeof state.x === "number" && typeof state.z === "number") {
    for (const layer of mapCfg.layers) {
      const bounds = layer.gameBounds || [];

      for (const b of bounds) {
        if (state.x >= b.Min.x && state.x <= b.Max.x &&
            state.y >= b.Min.y && state.y <= b.Max.y &&
            state.z >= b.Min.z && state.z <= b.Max.z) {
          if (!chosen || layer.level > chosen.level) chosen = layer;
          break;
        }
      }
    }
  }

  if (!chosen) chosen = mapCfg.layers.find(l => l.level === 0) || mapCfg.layers[0];
  if (!chosen) return;

  if (force || !currentLayer || currentLayer.name !== chosen.name) {
    currentLayer = chosen;
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

  if (!mapCfg || !currentLayer) {
    return;
  }

  if (image && image.complete && image.naturalWidth > 0) {
    const cx = currentLayer.width / 2;
    const cy = currentLayer.height / 2;
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
    ctx.globalAlpha = 0.92;
    ctx.drawImage(image, 0, 0, currentLayer.width, currentLayer.height);
    ctx.restore();
  }

  if (!state.inRaid) return;

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

    if (view.zoom > 0.7) {
      ctx.font = "11px Segoe UI, sans-serif";
      ctx.fillStyle = "#dfe6ee";
      ctx.textAlign = "left";
      ctx.fillText((p.name || "?") + "  " + Math.round(p.dist || 0) + "m", s.x + 7, s.y + 4);
    }
  }

  if (typeof state.x === "number") {
    drawSelf();
  }
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
  cal = Object.assign({}, DEFAULT_CAL);

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
    setStatus(out.ok ? "alignment saved" : "save failed", out.ok ? "good" : "warn");
  } catch (e) {
    setStatus("save failed", "warn");
  }
}

function bindCalibration() {
  const rot = document.getElementById("rot");
  const scale = document.getElementById("scale");
  const ox = document.getElementById("ox");
  const oy = document.getElementById("oy");
  const fx = document.getElementById("fx");
  const fy = document.getElementById("fy");

  function changed(fromSlider) {
    cal.rotation = parseFloat(rot.value);
    cal.scale = parseFloat(scale.value);
    cal.offsetX = parseFloat(ox.value);
    cal.offsetY = parseFloat(oy.value);
    cal.flipX = fx.checked ? -1 : 1;
    cal.flipY = fy.checked ? -1 : 1;

    syncInputs();
    draw();
    if (fromSlider) scheduleSave();
  }

  rot.addEventListener("input", () => changed(true));
  scale.addEventListener("input", () => changed(true));
  ox.addEventListener("input", () => changed(true));
  oy.addEventListener("input", () => changed(true));
  fx.addEventListener("change", () => changed(true));
  fy.addEventListener("change", () => changed(true));

  document.getElementById("save").addEventListener("click", saveCalibration);

  document.getElementById("reset").addEventListener("click", () => {
    cal = Object.assign({}, DEFAULT_CAL);
    syncInputs();
    draw();
    saveCalibration();
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
  canvas.classList.add("dragging");
});

window.addEventListener("mouseup", () => {
  dragging = false;
  dragLast = null;
  canvas.classList.remove("dragging");
});

window.addEventListener("mousemove", e => {
  if (!dragging || !dragLast) return;
  view.x -= (e.clientX - dragLast.x) / view.zoom;
  view.y -= (e.clientY - dragLast.y) / view.zoom;
  dragLast = { x: e.clientX, y: e.clientY };
  draw();
});

canvas.addEventListener("wheel", e => {
  e.preventDefault();
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
    case "s": case "S": saveCalibration(); handled = false; break;
    case "f": case "F": fitView(); handled = false; break;
    default: handled = false;
  }

  if (handled) {
    e.preventDefault();
    syncInputs();
    draw();
    scheduleSave();
  }
});

/* ---------- boot ---------- */

bindCalibration();
resize();
poll();
