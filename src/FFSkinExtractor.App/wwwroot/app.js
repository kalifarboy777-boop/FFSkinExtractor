import * as THREE from 'three';
import { GLTFLoader } from 'three/addons/loaders/GLTFLoader.js';
import { OrbitControls } from 'three/addons/controls/OrbitControls.js';
import { RoomEnvironment } from 'three/addons/environments/RoomEnvironment.js';

// ---------------------------------------------------------------------------
// Données et état
// ---------------------------------------------------------------------------
// Emplacements du constructeur, dans l'ordre du jeu
const SLOTS = [
  { key: 'Hair', label: 'Cheveux', hint: 'Tête, coiffure' },
  { key: 'Mask', label: 'Masque', hint: 'Lunettes, masque' },
  { key: 'Facepaint', label: 'Maquillage', hint: 'Pas encore géré', disabled: true },
  { key: 'Head', label: 'Chapeau', hint: 'Coiffe' },
  { key: 'Top', label: 'Haut', hint: 'Veste, t-shirt' },
  { key: 'Bottom', label: 'Bas', hint: 'Pantalon, jupe' },
  { key: 'Shoe', label: 'Chaussures', hint: 'Chaussures' },
  { key: 'Hands', label: 'Mains', hint: 'Gants' },
  { key: 'Other', label: 'Autre', hint: 'Divers' },
];

const state = {
  catalog: { characters: [], items: [], bundles: [] },
  gender: 'Male',
  hd: true,
  character: null,     // clé « Genre:Tenue » ou null pour le personnage de base
  bundle: null,        // valeur du bundle appliqué (affichage seulement)
  slots: {},           // emplacement → ID d'objet saisi
  options: { base: true, skinned: true, pbr: true, textures: false },
  status: null,
  lookups: {},         // ID inconnu des fichiers → infos ItemID2
};

const $ = id => document.getElementById(id);
const store = {
  get(key, fallback) { try { return JSON.parse(localStorage.getItem('ffss2.' + key)) ?? fallback; } catch { return fallback; } },
  set(key, value) { try { localStorage.setItem('ffss2.' + key, JSON.stringify(value)); } catch { /* stockage indisponible */ } },
};
Object.assign(state, store.get('look', {}));
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

const initials = name => (name ?? '?').split(/[\s_]+/).filter(Boolean).slice(0, 2).map(w => w[0].toUpperCase()).join('');

function thumb(iconUrl, fallbackText, cls = 'thumb') {
  const div = document.createElement('div');
  div.className = cls;
  if (iconUrl) {
    const img = new Image();
    img.loading = 'lazy';
    img.alt = '';
    img.src = iconUrl;
    img.onerror = () => { img.remove(); div.textContent = fallbackText; };
    div.append(img);
  } else div.textContent = fallbackText;
  return div;
}

function el(tag, cls, text) {
  const e = document.createElement(tag);
  if (cls) e.className = cls;
  if (text !== undefined) e.textContent = text;
  return e;
}

// ---------------------------------------------------------------------------

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
      if (s.state === 'Ready') await loadCatalog();
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
    state.catalog = { characters: [], items: [], bundles: [] };
    render();
  } catch (err) { toast(err.message, true); }
};
$('openFolder').onclick = () => api('open-folder', {}).catch(err => toast(err.message, true));


// ---------------------------------------------------------------------------
// Catalogue : personnages, bundles, objets
// ---------------------------------------------------------------------------
async function loadCatalog() {
  state.catalog = await (await api('catalog')).json();
  if (state.character && !state.catalog.characters.some(c => c.key === state.character)) state.character = null;
  render();
  requestModel();
}

const characters = () => state.catalog.characters.filter(c => c.gender === state.gender);
const currentCharacter = () => state.catalog.characters.find(c => c.key === state.character) ?? null;
const itemsFor = slot => state.catalog.items.filter(i => i.slot === slot && i.gender === state.gender);
const itemByValue = value => state.catalog.items.find(i => i.value === value && i.gender === state.gender);
const bundleByValue = value => state.catalog.bundles.find(b => b.value === value);

function look() {
  return {
    gender: state.gender, highDetail: state.hd, character: state.character, slots: state.slots,
    base: state.options.base, skinned: state.options.skinned, pbr: state.options.pbr,
  };
}

function save() {
  store.set('look', { gender: state.gender, hd: state.hd, character: state.character, bundle: state.bundle, slots: state.slots });
  store.set('options', state.options);
}

function render() {
  for (const b of $('genderSwitch').children) b.classList.toggle('active', b.dataset.value === state.gender);
  for (const b of $('qualitySwitch').children) b.classList.toggle('active', (b.dataset.value === 'hd') === state.hd);
  ['optBase', 'optSkinned', 'optPbr', 'optTextures'].forEach(id => { $(id).checked = state.options[id.slice(3).toLowerCase()]; });
  renderCharacterPicker();
  renderBundlePicker();
  renderSlotFields();
  renderCurrent();
  save();
}

// ---- Liste déroulante générique (personnage, bundle, objet) ----
let openPicker = null;
function closePickers() {
  openPicker?.remove();
  openPicker = null;
  document.querySelectorAll('.picker.open').forEach(p => p.classList.remove('open'));
}
document.addEventListener('mousedown', e => {
  if (openPicker && !openPicker.contains(e.target) && !e.target.closest('.picker > .choice') && !e.target.closest('.field-row input')) closePickers();
});
document.addEventListener('keydown', e => { if (e.key === 'Escape') closePickers(); });

/**
 * Ouvre une liste sous <host>. options : [{ value, title, sub, icon, rarity, tag, missing }]
 * tabs : [{ key, label, filter }] facultatif.
 */
function showPopover(host, { options, selected, onPick, placeholder, tabs, search = true, query = null }) {
  closePickers();
  host.classList.add('open');
  const pop = el('div', 'popover');
  let input = null, tab = tabs?.[0]?.key;
  if (search && query === null) {
    input = el('input');
    input.type = 'search';
    input.placeholder = placeholder ?? 'Rechercher…';
    pop.append(input);
  }
  let tabBar = null;
  if (tabs) {
    tabBar = el('div', 'tabs');
    pop.append(tabBar);
  }
  const list = el('div', 'options');
  pop.append(list);
  const draw = (q = '') => {
    if (tabBar) {
      tabBar.innerHTML = '';
      for (const t of tabs) {
        const b = el('button', t.key === tab ? 'active' : '', `${t.label} (${options.filter(t.filter).length})`);
        b.type = 'button';
        b.onclick = () => { tab = t.key; draw(input?.value ?? q); };
        tabBar.append(b);
      }
    }
    const words = q.trim().toLowerCase().split(/\s+/).filter(Boolean);
    let shown = options.filter(o => !tabs || tabs.find(t => t.key === tab).filter(o))
      .filter(o => words.every(w => `${o.title} ${o.sub ?? ''} ${o.value}`.toLowerCase().includes(w)));
    const total = shown.length;
    shown = shown.slice(0, 200);
    list.innerHTML = '';
    if (shown.length === 0) list.append(el('div', 'empty-note', 'Aucun résultat.'));
    for (const o of shown) {
      const b = el('button', 'option' + (o.value === selected ? ' selected' : '') + (o.missing ? ' missing' : ''));
      b.type = 'button';
      if (o.rarity) b.dataset.rarity = o.rarity;
      b.append(thumb(o.icon, initials(o.title)));
      const body = el('div');
      body.style.minWidth = '0';
      const t = el('div', 'title', o.title);
      if (o.tag) t.append(el('span', 'tag' + (o.tagClass ? ' ' + o.tagClass : ''), o.tag));
      body.append(t);
      if (o.sub) body.append(el('div', 'sub', o.sub));
      b.append(body);
      b.onmousedown = e => e.preventDefault();
      b.onclick = () => { closePickers(); onPick(o); };
      list.append(b);
    }
    if (total > shown.length) list.append(el('div', 'empty-note', `${total - shown.length} autres : affine la recherche.`));
  };
  draw(query ?? '');
  if (input) input.oninput = () => draw(input.value);
  host.append(pop);
  openPicker = pop;
  input?.focus();
  return { redraw: draw };
}

// ---- Personnage ----
function renderCharacterPicker() {
  const host = $('characterPicker');
  host.innerHTML = '';
  const c = currentCharacter();
  const btn = el('button', 'choice');
  btn.type = 'button';
  btn.append(thumb(c?.item?.iconUrl, c ? initials(c.name) : (state.gender === 'Male' ? 'A' : 'E')));
  const body = el('div');
  body.style.minWidth = '0';
  body.append(el('div', 'title', c ? c.name : 'Personnage de base'));
  body.append(el('div', 'sub', c ? `${c.internal}${c.item ? ' · ID ' + c.item.id : ''}` : (state.gender === 'Male' ? 'Adam, tenue de départ' : 'Eve, tenue de départ')));
  btn.append(body, el('span', 'chev', '▾'));
  btn.onclick = () => {
    if (host.classList.contains('open')) return closePickers();
    const options = [{ value: '', title: 'Personnage de base', sub: state.gender === 'Male' ? 'Adam' : 'Eve' },
      ...characters().map(x => ({
        value: x.key, title: x.name, sub: x.internal + (x.item ? ' · ID ' + x.item.id : ''),
        icon: x.item?.iconUrl, rarity: x.item?.rarity, tag: /awakening/i.test(x.internal) ? 'Éveillé' : null,
      }))];
    showPopover(host, {
      options, selected: state.character ?? '', placeholder: 'Rechercher un personnage…',
      onPick: o => { state.character = o.value || null; render(); requestModel(); },
    });
  };
  host.append(btn);
}

// ---- Bundle ----
function renderBundlePicker() {
  const host = $('bundlePicker');
  host.innerHTML = '';
  const b = state.bundle ? bundleByValue(state.bundle) : null;
  const btn = el('button', 'choice');
  btn.type = 'button';
  if (b?.rarity) btn.dataset.rarity = b.rarity;
  btn.append(thumb(b?.iconUrl, b ? initials(b.name) : '+'));
  const body = el('div');
  body.style.minWidth = '0';
  body.append(el('div', 'title', b ? b.name : 'Choisir un bundle'));
  const available = state.catalog.bundles.filter(x => x.available && x.gender === state.gender).length;
  body.append(el('div', 'sub', b ? (b.id ? 'ID ' + b.id : b.outfit ?? '') : `${available} bundles disponibles dans tes fichiers`));
  btn.append(body, el('span', 'chev', '▾'));
  btn.onclick = () => {
    if (host.classList.contains('open')) return closePickers();
    const options = state.catalog.bundles
      .filter(x => x.available ? x.gender === state.gender : (x.gender == null || x.gender === state.gender))
      .map(x => ({
        value: x.value, title: x.name, icon: x.iconUrl, rarity: x.rarity, missing: !x.available,
        sub: x.available ? `${x.id ? 'ID ' + x.id + ' · ' : ''}${Object.keys(x.slots).map(slotLabel).join(', ')}`
          : `ID ${x.id} · pas dans tes fichiers : ouvre-le dans le jeu pour le télécharger`,
        available: x.available,
      }));
    showPopover(host, {
      options, selected: state.bundle, placeholder: 'Nom ou ID du bundle…',
      tabs: [{ key: 'ok', label: 'Disponibles', filter: o => o.available }, { key: 'all', label: 'Tous (ItemID2)', filter: () => true }],
      onPick: o => applyBundle(o.value),
    });
  };
  host.append(btn);
}

function applyBundle(value) {
  const b = bundleByValue(value);
  if (!b) return;
  if (!b.available) { toast(`« ${b.name} » n'est pas dans tes fichiers. Ouvre-le dans la boutique du jeu pour qu'il soit téléchargé, puis relance l'analyse.`, true); return; }
  state.bundle = value;
  state.slots = { ...b.slots };
  $('fileName').value = '';
  render();
  requestModel();
}

const slotLabel = key => SLOTS.find(s => s.key === key)?.label ?? key;

// ---- Emplacements (ID d'objet) ----
function renderSlotFields() {
  const box = $('slotFields');
  box.innerHTML = '';
  const used = new Set(state.catalog.items.filter(i => i.gender === state.gender).map(i => i.slot));
  for (const s of SLOTS) {
    if (!s.disabled && !used.has(s.key) && !state.slots[s.key] && !['Hair', 'Mask', 'Top', 'Bottom', 'Shoe'].includes(s.key)) continue;
    const row = el('div', 'field-row' + (s.disabled ? ' disabled' : ''));
    const value = state.slots[s.key] ?? '';
    const item = value ? itemByValue(value) : null;
    row.append(thumb(item?.iconUrl, s.label.slice(0, 2)));
    const lbl = el('div', 'lbl');
    lbl.append(el('strong', '', s.label), el('span', '', s.disabled ? s.hint : 'ID d\'objet'));
    row.append(lbl);
    const input = el('input');
    input.value = value.startsWith('o:') ? '' : value;
    input.placeholder = s.disabled ? 'Bientôt' : (value.startsWith('o:') ? item?.name ?? '' : 'ex. 203000528 ou un nom');
    input.disabled = !!s.disabled;
    input.spellcheck = false;
    row.append(input);
    const st = el('div', 'state');
    row.append(st);
    describeSlot(s.key, value, st);

    const suggest = () => {
      const opts = itemsFor(s.key).map(i => ({
        value: i.value, title: i.name, icon: i.iconUrl, rarity: i.rarity,
        sub: (i.id ? 'ID ' + i.id + ' · ' : '') + i.outfit,
      }));
      showPopover(row, {
        options: opts, selected: state.slots[s.key], query: input.value,
        onPick: o => setSlot(s.key, o.value),
      });
    };
    input.onfocus = suggest;
    input.oninput = () => { if (openPicker && row.contains(openPicker)) { closePickers(); } suggest(); };
    input.onkeydown = e => { if (e.key === 'Enter') { setSlot(s.key, input.value.trim()); closePickers(); input.blur(); } };
    input.onchange = () => {
      const v = input.value.trim();
      if (/^\d+$/.test(v) || v === '') setSlot(s.key, v);
    };
    box.append(row);
  }
}

function setSlot(slot, value) {
  if (value) state.slots[slot] = value; else delete state.slots[slot];
  state.bundle = null;
  render();
  requestModel();
}

// État d'un emplacement : objet trouvé, objet connu mais absent des fichiers, ou ID inconnu
async function describeSlot(slot, value, st) {
  if (!value) { st.textContent = currentCharacter() ? 'Pièce d\'origine du personnage' : 'Pièce de base'; st.className = 'state'; return; }
  const item = itemByValue(value);
  if (item) {
    st.textContent = `✓ ${item.name} · ${item.outfit}`;
    st.className = 'state ok';
    if (item.slot !== slot) st.textContent += ` (va dans « ${slotLabel(item.slot)} »)`;
    return;
  }
  if (!/^\d+$/.test(value)) { st.textContent = 'Saisis un ID d\'objet (chiffres).'; st.className = 'state bad'; return; }
  let info = state.lookups[value];
  if (info === undefined) {
    try { info = await (await api('lookup/' + value)).json(); } catch { info = null; }
    state.lookups[value] = info;
  }
  if (info) { st.textContent = `${info.name} : pas dans tes fichiers (ouvre-le dans le jeu pour le télécharger)`; st.className = 'state warn'; }
  else { st.textContent = 'ID inconnu dans ItemID2.'; st.className = 'state bad'; }
}

function renderCurrent() {
  const box = $('current');
  box.innerHTML = '';
  const c = currentCharacter();
  const b = state.bundle ? bundleByValue(state.bundle) : null;
  box.append(el('div', 'who', c ? c.name : 'Personnage de base'));
  const n = Object.keys(state.slots).length;
  box.append(el('div', 'what', b ? b.name : n ? `${n} objet(s) choisi(s)` : 'Tenue d\'origine'));
  $('fileName').placeholder = defaultName();
  const count = state.catalog.bundles.filter(x => x.available && x.gender === state.gender).length;
  $('exportAll').textContent = `Exporter tous les bundles (${count})`;
}

function defaultName() {
  const c = currentCharacter();
  const b = state.bundle ? bundleByValue(state.bundle) : null;
  const who = c ? c.internal : `${state.gender}_Base`;
  const what = b ? '_' + b.name.replace(/[^\w]+/g, '_').replace(/_+$/, '') : Object.keys(state.slots).length ? '_Custom' : '';
  return `${who}${what}${state.hd ? '_HD' : ''}`;
}

for (const b of $('genderSwitch').children)
  b.onclick = () => {
    if (state.gender === b.dataset.value) return;
    state.gender = b.dataset.value; state.character = null; state.bundle = null; state.slots = {};
    render(); requestModel();
  };
for (const b of $('qualitySwitch').children)
  b.onclick = () => { const hd = b.dataset.value === 'hd'; if (hd !== state.hd) { state.hd = hd; render(); requestModel(); } };
$('clearAll').onclick = () => { state.slots = {}; state.bundle = null; render(); requestModel(); };
for (const [id, key] of [['optBase', 'base'], ['optSkinned', 'skinned'], ['optPbr', 'pbr'], ['optTextures', 'textures']])
  $(id).onchange = e => { state.options[key] = e.target.checked; save(); if (key !== 'textures') requestModel(); };

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
  const empty = !state.character && Object.keys(state.slots).length === 0;
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
      method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(look()), signal: ctrl.signal,
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
  const blob = await (await api('model', look())).blob();
  saveBlob(blob, ($('fileName').value || defaultName()) + '.glb');
});
$('saveExport').onclick = () => busy($('saveExport'), async () => {
  const r = await (await api('export', { look: look(), fileName: $('fileName').value || null, textures: state.options.textures })).json();
  toast('Enregistré : ' + r.path);
});
$('downloadTextures').onclick = () => busy($('downloadTextures'), async () => {
  const blob = await (await api('textures', look())).blob();
  saveBlob(blob, ($('fileName').value || defaultName()) + '_textures.zip');
});
$('exportAll').onclick = () => busy($('exportAll'), async () => {
  const n = state.catalog.bundles.filter(x => x.available && x.gender === state.gender).length;
  const who = currentCharacter()?.name ?? 'le personnage de base';
  if (!confirm(`Exporter les ${n} bundles disponibles portés par ${who} (${state.hd ? 'HD' : 'en jeu'}) dans le dossier d'export ?`)) return;
  await api('export-all', { highDetail: state.hd, gender: state.gender, character: state.character, base: state.options.base });
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
