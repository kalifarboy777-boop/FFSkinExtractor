import * as THREE from 'three';
import { GLTFLoader } from 'three/addons/loaders/GLTFLoader.js';
import { OrbitControls } from 'three/addons/controls/OrbitControls.js';
import { RoomEnvironment } from 'three/addons/environments/RoomEnvironment.js';

// ---------------------------------------------------------------------------
// Données et état
// ---------------------------------------------------------------------------
const PIECES = [
  { key: 'Top', label: 'Haut' },
  { key: 'Bottom', label: 'Bas' },
  { key: 'Shoe', label: 'Chaussures' },
  { key: 'Head', label: 'Tête' },
  { key: 'Hair', label: 'Cheveux' },
  { key: 'Accessory', label: 'Accessoire' },
  { key: 'Hands', label: 'Mains' },
  { key: 'Other', label: 'Autre' },
];
const pieceLabel = key => PIECES.find(p => p.key === key)?.label ?? key;

const state = {
  outfits: [],
  gender: 'Female',
  hd: true,
  category: 'Outfit',          // « Outfit » = tenues complètes, sinon une pièce
  query: '',
  pieces: {},                  // pièce → id de tenue
  options: { base: true, skinned: true, pbr: true, textures: false },
  status: null,
};

const $ = id => document.getElementById(id);
const store = {
  get(key, fallback) { try { return JSON.parse(localStorage.getItem('ffss.' + key)) ?? fallback; } catch { return fallback; } },
  set(key, value) { try { localStorage.setItem('ffss.' + key, JSON.stringify(value)); } catch { /* stockage indisponible */ } },
};
Object.assign(state, store.get('view', {}));
state.options = { ...state.options, ...store.get('options', {}) };

async function api(path, body) {
  const res = await fetch('api/' + path, body === undefined ? {} : {
    method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body),
  });
  if (!res.ok) {
    let msg = res.statusText;
    try { const j = await res.json(); msg = j.detail || j.title || msg; } catch { try { msg = await res.text(); } catch { } }
    throw new Error(msg);
  }
  return res;
}

function toast(message, error = false) {
  const t = $('toast');
  t.textContent = message;
  t.classList.toggle('error', error);
  t.classList.remove('hidden');
  clearTimeout(toast.timer);
  toast.timer = setTimeout(() => t.classList.add('hidden'), error ? 7000 : 3500);
}

// ---------------------------------------------------------------------------
// Statut, réglages
// ---------------------------------------------------------------------------
let lastState = null;
async function pollStatus() {
  try {
    const s = await (await api('status')).json();
    state.status = s;
    const el = $('status');
    el.className = 'status ' + ({ Ready: 'ready', Scanning: 'scanning', Error: 'error' }[s.state] ?? '');
    $('statusText').textContent = s.message + (s.state === 'Ready' && s.catalog ? ` · ${s.catalog.toLocaleString('fr')} objets ItemID2` : '');
    $('statusProgress').firstElementChild.style.width = Math.round(s.progress * 100) + '%';
    $('version').textContent = s.version ? 'v' + s.version : '';
    $('credit').textContent = s.credit;
    renderBatch(s.batch);
    if (s.state !== lastState) {
      if (s.state === 'Ready') await loadOutfits();
      if ((s.state === 'NotConfigured' || s.state === 'Error') && !$('settings').open) openSettings();
      lastState = s.state;
    }
  } catch { $('statusText').textContent = 'Serveur injoignable : relance FF Skin Studio.'; }
  setTimeout(pollStatus, state.status?.state === 'Scanning' || state.status?.batch?.running ? 500 : 2500);
}

function openSettings() {
  const s = state.status?.settings ?? {};
  $('setData').value = s.dataFolder ?? '';
  $('setPackage').value = s.gamePackage ?? '';
  $('setExport').value = s.exportFolder ?? '';
  const sug = $('suggestions');
  sug.innerHTML = '';
  for (const path of state.status?.suggestions ?? []) {
    const b = document.createElement('button');
    b.type = 'button'; b.className = 'ghost'; b.textContent = path;
    b.onclick = () => { $('setData').value = path; };
    sug.append(b);
  }
  $('settings').showModal();
}

$('openSettings').onclick = openSettings;
$('settingsCancel').onclick = () => $('settings').close();
$('settingsForm').onsubmit = async e => {
  e.preventDefault();
  try {
    await api('settings', { dataFolder: $('setData').value, gamePackage: $('setPackage').value, exportFolder: $('setExport').value });
    $('settings').close();
    lastState = null;
    state.outfits = [];
    render();
  } catch (err) { toast(err.message, true); }
};
$('openFolder').onclick = () => api('open-folder', {}).catch(err => toast(err.message, true));

// ---------------------------------------------------------------------------
// Catalogue
// ---------------------------------------------------------------------------
async function loadOutfits() {
  state.outfits = await (await api('outfits')).json();
  // Les pièces mémorisées qui n'existent plus sont retirées
  for (const [piece, id] of Object.entries(state.pieces))
    if (!state.outfits.some(o => o.id === id)) delete state.pieces[piece];
  render();
  requestModel();
}

const visibleOutfits = () => state.outfits.filter(o => o.gender === state.gender && o.highDetail === state.hd);
const byId = id => state.outfits.find(o => o.id === id);

function matches(o, q) {
  if (!q) return true;
  const hay = [o.displayName, o.name, o.bundle, o.character?.name, o.character?.id,
    ...o.pieces.flatMap(p => [p.item?.name, p.item?.id, ...p.slots])].filter(Boolean).join(' ').toLowerCase();
  return q.split(/\s+/).every(w => hay.includes(w));
}

function render() {
  for (const b of $('genderSwitch').children) b.classList.toggle('active', b.dataset.value === state.gender);
  for (const b of $('qualitySwitch').children) b.classList.toggle('active', (b.dataset.value === 'hd') === state.hd);
  ['optBase', 'optSkinned', 'optPbr', 'optTextures'].forEach(id => {
    $(id).checked = state.options[id.slice(3).toLowerCase()];
  });
  renderCategories();
  renderCards();
  renderSlots();
  store.set('view', { gender: state.gender, hd: state.hd, category: state.category, pieces: state.pieces });
  store.set('options', state.options);
}

function renderCategories() {
  const all = visibleOutfits();
  const nav = $('categories');
  nav.innerHTML = '';
  const cats = [{ key: 'Outfit', label: 'Tenues', count: all.length },
    ...PIECES.map(p => ({ ...p, count: all.filter(o => o.pieces.some(x => x.piece === p.key)).length })).filter(p => p.count > 0)];
  for (const c of cats) {
    const b = document.createElement('button');
    b.className = c.key === state.category ? 'active' : '';
    b.innerHTML = `${c.label}<span class="count">${c.count}</span>`;
    b.onclick = () => { state.category = c.key; render(); };
    nav.append(b);
  }
}

function thumb(item, fallbackText) {
  const div = document.createElement('div');
  div.className = 'thumb';
  if (item?.iconUrl) {
    const img = new Image();
    img.loading = 'lazy';
    img.alt = '';
    img.src = item.iconUrl;
    img.onerror = () => { img.remove(); div.textContent = fallbackText; };
    div.append(img);
  } else div.textContent = fallbackText;
  return div;
}

const initials = name => name.split(/[\s_]+/).filter(Boolean).slice(0, 2).map(w => w[0].toUpperCase()).join('');

function renderCards() {
  const q = state.query.trim().toLowerCase();
  const piece = state.category === 'Outfit' ? null : state.category;
  let list = visibleOutfits().filter(o => matches(o, q));
  if (piece) list = list.filter(o => o.pieces.some(p => p.piece === piece));
  const cards = $('cards');
  cards.innerHTML = '';
  $('listInfo').textContent = state.outfits.length === 0
    ? (state.status?.state === 'Scanning' ? 'Analyse des bundles en cours…' : 'Aucune tenue pour l\'instant.')
    : `${list.length} ${piece ? 'pièce(s) « ' + pieceLabel(piece) + ' »' : 'tenue(s)'}`;

  for (const o of list) {
    const p = piece ? o.pieces.find(x => x.piece === piece) : null;
    const item = piece ? p.item : (o.pieces.find(x => x.piece === 'Top')?.item ?? o.pieces.find(x => x.item)?.item ?? o.character);
    const selected = piece ? state.pieces[piece] === o.id
      : o.pieces.every(x => state.pieces[x.piece] === o.id);
    const card = document.createElement('button');
    card.className = 'card' + (selected ? ' selected' : '');
    card.dataset.rarity = item?.rarity ?? '';
    card.append(thumb(item, initials(o.displayName)));
    const body = document.createElement('div');
    body.style.minWidth = '0';
    const title = piece && p.item ? p.item.name : o.displayName;
    const sub = piece ? `${o.displayName} · ${o.name}` : `${o.name}${o.character ? ' · ' + o.character.name : ''}`;
    body.innerHTML = `<div class="title"></div><div class="sub"></div><div class="tags"></div>`;
    body.querySelector('.title').textContent = title;
    body.querySelector('.sub').textContent = sub;
    const tags = body.querySelector('.tags');
    const addTag = (text, cls = '') => { const t = document.createElement('span'); t.className = 'tag ' + cls; t.textContent = text; tags.append(t); };
    if (piece) { if (p.item) addTag('ID ' + p.item.id, 'id'); }
    else {
      for (const x of o.pieces) addTag(pieceLabel(x.piece));
      if (item?.id) addTag('ID ' + item.id, 'id');
    }
    card.append(body);
    card.title = piece
      ? `${pieceLabel(piece)} : ${p.slots.join(', ')}`
      : o.pieces.map(x => `${pieceLabel(x.piece)}${x.item ? ' — ' + x.item.name + ' (' + x.item.id + ')' : ''}`).join('\n');
    card.onclick = () => {
      if (piece) {
        if (state.pieces[piece] === o.id) delete state.pieces[piece]; else state.pieces[piece] = o.id;
      } else {
        state.pieces = {};
        for (const x of o.pieces) state.pieces[x.piece] = o.id;
        $('fileName').value = '';
      }
      render();
      requestModel();
    };
    cards.append(card);
  }
}

function renderSlots() {
  const box = $('slots');
  box.innerHTML = '';
  const shown = PIECES.filter(p => ['Top', 'Bottom', 'Shoe', 'Head', 'Hair'].includes(p.key) || state.pieces[p.key]);
  for (const p of shown) {
    const o = byId(state.pieces[p.key]);
    const pe = o?.pieces.find(x => x.piece === p.key);
    const row = document.createElement('div');
    row.className = 'slot' + (o ? '' : ' empty');
    row.append(thumb(pe?.item, o ? initials(o.displayName) : '—'));
    const text = document.createElement('div');
    text.style.minWidth = '0';
    text.innerHTML = '<div class="label"></div><div class="name"></div>';
    text.querySelector('.label').textContent = p.label + (pe?.item ? ` · ID ${pe.item.id}` : '');
    text.querySelector('.name').textContent = o ? (pe?.item?.name ?? o.displayName) : (state.options.base ? 'Corps de base' : 'Rien');
    row.append(text);
    if (o) {
      const x = document.createElement('button');
      x.textContent = '×'; x.title = 'Retirer';
      x.onclick = () => { delete state.pieces[p.key]; render(); requestModel(); };
      row.append(x);
    } else row.append(document.createElement('span'));
    row.onclick = e => { if (e.target.tagName !== 'BUTTON') { state.category = p.key; render(); } };
    box.append(row);
  }
  const name = defaultName();
  $('fileName').placeholder = name;
}

function defaultName() {
  const ids = Object.values(state.pieces);
  if (ids.length === 0) return `${state.gender}_Base`;
  const counts = {};
  ids.forEach(id => counts[id] = (counts[id] ?? 0) + 1);
  const main = byId(Object.entries(counts).sort((a, b) => b[1] - a[1])[0][0]);
  return `${state.gender}_${main?.name ?? 'Tenue'}${state.hd ? '_HD' : ''}`;
}

const selection = () => ({
  gender: state.gender, highDetail: state.hd, pieces: state.pieces,
  base: state.options.base, skinned: state.options.skinned, pbr: state.options.pbr,
});

// Changement de qualité : on garde les mêmes tenues dans l'autre qualité quand elles existent
function switchQuality(hd) {
  if (hd === state.hd) return;
  const mapped = {};
  for (const [piece, id] of Object.entries(state.pieces)) {
    const o = byId(id);
    const twin = o && state.outfits.find(x => x.highDetail === hd && x.gender === o.gender && x.name === o.name && x.bundle === o.bundle);
    if (twin && twin.pieces.some(x => x.piece === piece)) mapped[piece] = twin.id;
  }
  state.hd = hd;
  state.pieces = mapped;
  render();
  requestModel();
}

for (const b of $('genderSwitch').children)
  b.onclick = () => { if (state.gender !== b.dataset.value) { state.gender = b.dataset.value; state.pieces = {}; render(); requestModel(); } };
for (const b of $('qualitySwitch').children) b.onclick = () => switchQuality(b.dataset.value === 'hd');
$('search').oninput = e => { state.query = e.target.value; renderCards(); };
$('clearAll').onclick = () => { state.pieces = {}; render(); requestModel(); };
for (const [id, key] of [['optBase', 'base'], ['optSkinned', 'skinned'], ['optPbr', 'pbr'], ['optTextures', 'textures']])
  $(id).onchange = e => { state.options[key] = e.target.checked; render(); if (key !== 'textures') requestModel(); };

// ---------------------------------------------------------------------------
// Aperçu 3D
// ---------------------------------------------------------------------------
const canvas = $('canvas');
const renderer = new THREE.WebGLRenderer({ canvas, antialias: true, alpha: true, preserveDrawingBuffer: true });
renderer.setPixelRatio(Math.min(devicePixelRatio, 2));
renderer.outputColorSpace = THREE.SRGBColorSpace;
renderer.toneMapping = THREE.ACESFilmicToneMapping;
renderer.toneMappingExposure = 1.05;
renderer.shadowMap.enabled = true;
renderer.shadowMap.type = THREE.PCFSoftShadowMap;

const scene = new THREE.Scene();
const pmrem = new THREE.PMREMGenerator(renderer);
scene.environment = pmrem.fromScene(new RoomEnvironment(), 0.04).texture;

const camera = new THREE.PerspectiveCamera(30, 1, 0.01, 100);
camera.position.set(0, 1.1, 4.2);
const controls = new OrbitControls(camera, canvas);
controls.target.set(0, 0.95, 0);
controls.enableDamping = true;
controls.minDistance = 0.3;
controls.maxDistance = 12;

const key = new THREE.DirectionalLight(0xffffff, 1.6);
key.position.set(2, 4, 3);
key.castShadow = true;
key.shadow.mapSize.set(2048, 2048);
Object.assign(key.shadow.camera, { left: -2, right: 2, top: 3, bottom: -1, near: 0.1, far: 12 });
scene.add(key, new THREE.HemisphereLight(0xdde6ff, 0x2a2420, 0.5));

const ground = new THREE.Mesh(new THREE.CircleGeometry(1.6, 64), new THREE.ShadowMaterial({ opacity: 0.35 }));
ground.rotation.x = -Math.PI / 2;
ground.receiveShadow = true;
const grid = new THREE.GridHelper(6, 24, 0x3a4150, 0x262b35);
grid.material.transparent = true;
grid.material.opacity = 0.5;
scene.add(ground, grid);

let model = null, skeletonHelper = null;
const view = { wire: false, skeleton: false, rotate: false };
const loader = new GLTFLoader();

function resize() {
  const { clientWidth: w, clientHeight: h } = canvas.parentElement;
  renderer.setSize(w, h, false);
  camera.aspect = w / Math.max(h, 1);
  camera.updateProjectionMatrix();
}
new ResizeObserver(resize).observe(canvas.parentElement);
resize();

renderer.setAnimationLoop(() => {
  if (view.rotate && model) model.rotation.y += 0.006;
  controls.update();
  renderer.render(scene, camera);
});

function frame(object, keepAngle = false) {
  const box = new THREE.Box3().setFromObject(object);
  if (box.isEmpty()) return;
  const size = box.getSize(new THREE.Vector3());
  const center = box.getCenter(new THREE.Vector3());
  const dist = Math.max(size.y, size.x * 1.4) / (2 * Math.tan(THREE.MathUtils.degToRad(camera.fov / 2))) * 1.25;
  const dir = keepAngle ? camera.position.clone().sub(controls.target).normalize() : new THREE.Vector3(0, 0.08, 1).normalize();
  controls.target.copy(center);
  camera.position.copy(center).addScaledVector(dir, dist);
}

function showModel(gltf) {
  const first = !model;
  if (model) {
    scene.remove(model);
    model.traverse(o => { o.geometry?.dispose(); [].concat(o.material ?? []).forEach(m => { Object.values(m).forEach(v => v?.isTexture && v.dispose()); m.dispose(); }); });
  }
  if (skeletonHelper) { scene.remove(skeletonHelper); skeletonHelper = null; }
  model = gltf.scene;
  let tris = 0, bones = new Set(), mats = new Set();
  model.traverse(o => {
    if (!o.isMesh) return;
    o.castShadow = true;
    tris += (o.geometry.index ? o.geometry.index.count : o.geometry.attributes.position.count) / 3;
    [].concat(o.material).forEach(m => { mats.add(m); m.wireframe = view.wire; });
    o.skeleton?.bones.forEach(b => bones.add(b));
  });
  scene.add(model);
  skeletonHelper = new THREE.SkeletonHelper(model);
  skeletonHelper.visible = view.skeleton;
  scene.add(skeletonHelper);
  if (first) frame(model);
  $('stats').textContent = `${Math.round(tris).toLocaleString('fr')} triangles · ${mats.size} matériaux · ${bones.size} os`;
}

let pending = null, requestTimer = null, lastGlb = null;
function requestModel() {
  clearTimeout(requestTimer);
  requestTimer = setTimeout(loadModel, 120);
}

async function loadModel() {
  if (state.status?.state !== 'Ready') return;
  const empty = Object.keys(state.pieces).length === 0;
  $('empty').classList.toggle('hidden', !(empty && !state.status.baseReady));
  if (empty && !state.status.baseReady) {
    if (model) { scene.remove(model); model = null; }
    if (skeletonHelper) { scene.remove(skeletonHelper); skeletonHelper = null; }
    $('stats').textContent = '';
    return;
  }
  pending?.abort();
  const ctrl = pending = new AbortController();
  $('loading').classList.remove('hidden');
  try {
    const res = await fetch('api/model', {
      method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(selection()), signal: ctrl.signal,
    });
    if (!res.ok) throw new Error((await res.json().catch(() => ({}))).detail ?? res.statusText);
    const buf = await res.arrayBuffer();
    if (ctrl !== pending) return;
    lastGlb = buf;
    const gltf = await loader.parseAsync(buf, '');
    if (ctrl === pending) showModel(gltf);
  } catch (err) {
    if (err.name !== 'AbortError') toast('Aperçu impossible : ' + err.message, true);
  } finally {
    if (ctrl === pending) { $('loading').classList.add('hidden'); pending = null; }
  }
}

const toggle = (id, keyName, apply) => {
  $(id).onclick = () => { view[keyName] = !view[keyName]; $(id).classList.toggle('on', view[keyName]); apply?.(); };
};
toggle('toolRotate', 'rotate', () => { if (!view.rotate && model) model.rotation.y = 0; });
toggle('toolWire', 'wire', () => model?.traverse(o => o.isMesh && [].concat(o.material).forEach(m => m.wireframe = view.wire)));
toggle('toolSkeleton', 'skeleton', () => { if (skeletonHelper) skeletonHelper.visible = view.skeleton; });
$('toolReset').onclick = () => model && frame(model);
$('toolLight').onclick = () => $('viewer').classList.toggle('light');
$('toolShot').onclick = () => {
  renderer.render(scene, camera);
  const a = document.createElement('a');
  a.href = canvas.toDataURL('image/png');
  a.download = ($('fileName').value || defaultName()) + '.png';
  a.click();
};

// ---------------------------------------------------------------------------
// Export
// ---------------------------------------------------------------------------
function saveBlob(blob, name) {
  const a = document.createElement('a');
  a.href = URL.createObjectURL(blob);
  a.download = name;
  a.click();
  setTimeout(() => URL.revokeObjectURL(a.href), 10000);
}

async function busy(button, work) {
  button.disabled = true;
  try { await work(); } catch (err) { toast(err.message, true); } finally { button.disabled = false; }
}

$('download').onclick = () => busy($('download'), async () => {
  const blob = await (await api('model', selection())).blob();
  saveBlob(blob, ($('fileName').value || defaultName()) + '.glb');
});
$('saveExport').onclick = () => busy($('saveExport'), async () => {
  const r = await (await api('export', { selection: selection(), fileName: $('fileName').value || null, textures: state.options.textures })).json();
  toast('Enregistré : ' + r.path);
});
$('downloadTextures').onclick = () => busy($('downloadTextures'), async () => {
  const blob = await (await api('textures', selection())).blob();
  saveBlob(blob, ($('fileName').value || defaultName()) + '_textures.zip');
});
$('exportAll').onclick = () => busy($('exportAll'), async () => {
  const n = visibleOutfits().length;
  if (!confirm(`Exporter les ${n} tenues ${state.gender === 'Female' ? 'femme' : 'homme'} (${state.hd ? 'HD' : 'en jeu'}) dans le dossier d'export ?`)) return;
  await api('export-all', { highDetail: state.hd, gender: state.gender, base: state.options.base });
  setTimeout(pollStatus, 300);
});
$('batchCancel').onclick = () => api('export-all/cancel', {});

let wasRunning = false;
function renderBatch(b) {
  const box = $('batch');
  if (!b || (!b.running && !wasRunning)) { if (!b?.running) box.classList.toggle('hidden', !b?.folder); }
  if (!b) return;
  box.classList.remove('hidden');
  box.querySelector('.progress > div').style.width = (b.total ? Math.round(b.done / b.total * 100) : 0) + '%';
  $('batchCancel').classList.toggle('hidden', !b.running);
  $('exportAll').disabled = b.running;
  if (b.running) $('batchText').textContent = `${b.done} / ${b.total} · ${b.current ?? ''}`;
  else if (b.folder) $('batchText').textContent = `${b.cancelled ? 'Annulé' : 'Terminé'} : ${b.done} tenue(s) dans ${b.folder}` + (b.errors.length ? ` · ${b.errors.length} erreur(s)` : '');
  else box.classList.add('hidden');
  if (wasRunning && !b.running) toast(`Export terminé : ${b.done} tenue(s).`);
  wasRunning = b.running;
}

render();
pollStatus();
