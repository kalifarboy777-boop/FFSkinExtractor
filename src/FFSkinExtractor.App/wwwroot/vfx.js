// Particules des effets d'emote : le GLB porte les objets animés (« vfxNNN_… »), les systèmes de particules
// du jeu sont simulés ici à partir de leurs réglages (durée, émission, forme, couleur et taille au fil de la vie…).
import * as THREE from 'three';

const MAX_PARTICLES = 500;
const GRAVITY = -9.81;

// Courbe Unity (clés Hermite)
function curve(c, t) {
  const k = c?.m_Curve;
  if (!k?.length) return 0;
  if (t <= k[0].time) return k[0].value;
  const last = k[k.length - 1];
  if (t >= last.time) return last.value;
  let i = 1;
  while (k[i].time < t) i++;
  const a = k[i - 1], b = k[i], dt = b.time - a.time;
  if (dt <= 0) return b.value;
  const s = (t - a.time) / dt, s2 = s * s, s3 = s2 * s;
  return (2 * s3 - 3 * s2 + 1) * a.value + (s3 - 2 * s2 + s) * a.outSlope * dt
    + (-2 * s3 + 3 * s2) * b.value + (s3 - s2) * b.inSlope * dt;
}

// MinMaxCurve : constante, courbe, deux courbes ou deux constantes (r : tirage propre à la particule)
function minMax(m, t, r) {
  if (!m) return 0;
  switch (m.minMaxState) {
    case 1: return curve(m.maxCurve, t) * m.scalar;
    case 2: return THREE.MathUtils.lerp(curve(m.minCurve, t) * (m.minScalar || m.scalar), curve(m.maxCurve, t) * m.scalar, r);
    case 3: return THREE.MathUtils.lerp(m.minScalar, m.scalar, r);
    default: return m.scalar;
  }
}

const col = c => [c.r, c.g, c.b, c.a];
const mix = (a, b, r) => a.map((v, i) => v + (b[i] - v) * r);

function gradient(g, t) {
  const keys = (n, times) => Array.from({ length: Math.max(n, 1) }, (_, i) => ({ t: g[times + i] / 65535, k: g['key' + i] }));
  const sample = (list, pick) => {
    if (t <= list[0].t) return pick(list[0].k);
    for (let i = 1; i < list.length; i++)
      if (t <= list[i].t) {
        if (g.m_Mode === 1) return pick(list[i].k);
        const s = (t - list[i - 1].t) / Math.max(list[i].t - list[i - 1].t, 1e-6);
        return mix(pick(list[i - 1].k), pick(list[i].k), s);
      }
    return pick(list[list.length - 1].k);
  };
  const rgb = sample(keys(g.m_NumColorKeys, 'ctime'), k => [k.r, k.g, k.b]);
  const [a] = sample(keys(g.m_NumAlphaKeys, 'atime'), k => [k.a]);
  return [...rgb, a];
}

function minMaxGradient(m, t, r) {
  if (!m) return [1, 1, 1, 1];
  switch (m.minMaxState) {
    case 1: return gradient(m.maxGradient, t);
    case 2: return mix(col(m.minColor), col(m.maxColor), r);
    case 3: return mix(gradient(m.minGradient, t), gradient(m.maxGradient, t), r);
    case 4: return gradient(m.maxGradient, r);
    default: return col(m.maxColor);
  }
}

const v3 = o => new THREE.Vector3(o?.x ?? 0, o?.y ?? 0, o?.z ?? 0);
const deg = THREE.MathUtils.degToRad;
function randomUnit() {
  const z = Math.random() * 2 - 1, a = Math.random() * Math.PI * 2, s = Math.sqrt(1 - z * z);
  return new THREE.Vector3(Math.cos(a) * s, Math.sin(a) * s, z);
}

const vertexShader = `
attribute vec3 iPos; attribute vec3 iSize; attribute float iRot; attribute vec4 iColor; attribute vec4 iUv; attribute vec3 iVel;
attribute vec3 iX; attribute vec3 iY; attribute vec3 iZ;
uniform int uMode; uniform float uLen; uniform float uVelScale; uniform vec4 uST;
varying vec2 vUv; varying vec4 vColor;
void main() {
  vColor = iColor;
  vUv = (iUv.xy + uv * iUv.zw) * uST.xy + uST.zw;
  if (uMode == 4) {
    gl_Position = projectionMatrix * viewMatrix * vec4(iPos + iX * position.x + iY * position.y + iZ * position.z, 1.0);
    return;
  }
  vec2 c = position.xy;
  float s = sin(iRot), k = cos(iRot);
  vec2 r = vec2(c.x * k - c.y * s, c.x * s + c.y * k) * iSize.xy;
  if (uMode == 2) { gl_Position = projectionMatrix * viewMatrix * vec4(iPos + vec3(r.x, 0.0, r.y), 1.0); return; }
  vec4 mv = viewMatrix * vec4(iPos, 1.0);
  if (uMode == 1) {
    vec3 v = (viewMatrix * vec4(iVel, 0.0)).xyz;
    vec2 d = length(v.xy) > 1e-6 ? normalize(v.xy) : vec2(0.0, 1.0);
    float len = iSize.x * uLen + length(iVel) * uVelScale;
    mv.xy += d * (c.x * len) + vec2(-d.y, d.x) * (c.y * iSize.y);
  } else mv.xy += r;
  gl_Position = projectionMatrix * mv;
}`;
const fragmentShader = `
uniform sampler2D uMap; uniform bool uHasMap; uniform vec4 uColor;
varying vec2 vUv; varying vec4 vColor;
void main() {
  vec4 c = (uHasMap ? texture2D(uMap, vUv) : vec4(1.0)) * vColor * uColor;
  if (c.a < 0.004) discard;
  gl_FragColor = c;
}`;

class Emitter {
  constructor(def, node, texture) {
    this.def = def;
    this.node = node;
    this.sys = def.system;
    const r = def.renderer ?? {};
    this.mode = r.m_RenderMode ?? 0;
    if (this.mode === 3) this.mode = 0; // billboard vertical : approché par un billboard
    const mat = def.material;
    const blend = mat?.blend ?? 'Additive';
    let base;
    if (this.mode === 4 && def.mesh) {
      base = new THREE.BufferGeometry();
      base.setAttribute('position', new THREE.Float32BufferAttribute(def.mesh.positions, 3));
      base.setAttribute('uv', def.mesh.uvs ? new THREE.Float32BufferAttribute(def.mesh.uvs, 2)
        : new THREE.Float32BufferAttribute(new Float32Array(def.mesh.positions.length / 3 * 2), 2));
      base.setIndex(def.mesh.indices);
    } else {
      if (this.mode === 4) this.mode = 0;
      base = new THREE.PlaneGeometry(1, 1);
    }
    this.max = Math.min(this.sys.InitialModule?.maxNumParticles ?? 100, MAX_PARTICLES);
    const geo = this.geometry = new THREE.InstancedBufferGeometry();
    geo.index = base.index;
    geo.setAttribute('position', base.getAttribute('position'));
    geo.setAttribute('uv', base.getAttribute('uv'));
    const attr = (name, size) => {
      const a = new THREE.InstancedBufferAttribute(new Float32Array(this.max * size), size);
      a.setUsage(THREE.DynamicDrawUsage);
      geo.setAttribute(name, a);
      return a;
    };
    this.a = { pos: attr('iPos', 3), size: attr('iSize', 3), rot: attr('iRot', 1), color: attr('iColor', 4), uv: attr('iUv', 4), vel: attr('iVel', 3),
      x: attr('iX', 3), y: attr('iY', 3), z: attr('iZ', 3) };
    geo.instanceCount = 0;
    const c = mat?.color ?? [1, 1, 1, 1];
    this.material = new THREE.ShaderMaterial({
      vertexShader, fragmentShader,
      uniforms: {
        uMode: { value: this.mode }, uLen: { value: r.m_LengthScale ?? 2 }, uVelScale: { value: r.m_VelocityScale ?? 0 },
        uST: { value: new THREE.Vector4(mat?.tiling?.[0] ?? 1, mat?.tiling?.[1] ?? 1, mat?.offset?.[0] ?? 0, mat?.offset?.[1] ?? 0) },
        uMap: { value: texture }, uHasMap: { value: !!texture }, uColor: { value: new THREE.Vector4(...c) },
      },
      transparent: true,
      blending: blend === 'Alpha' || blend === 'Cutout' ? THREE.NormalBlending : THREE.AdditiveBlending,
      depthWrite: false, side: THREE.DoubleSide,
    });
    this.mesh = new THREE.Mesh(geo, this.material);
    this.mesh.frustumCulled = false;
    this.mesh.renderOrder = 10 + (r.m_SortingFudge ? -Math.sign(r.m_SortingFudge) : 0);
    this.particles = [];
    this.active = false;
    this.local = (this.sys.moveWithTransform ?? 0) === 0;
  }

  reset() {
    this.particles = [];
    this.active = false;
    this.geometry.instanceCount = 0;
  }

  visible() {
    for (let o = this.node; o; o = o.parent) if (!o.visible) return false;
    return Math.abs(this.node.matrixWorld.determinant()) > 1e-12;
  }

  start() {
    this.active = true;
    this.time = 0;
    this.emitted = 0;
    this.lastLocal = -1e-6;
    this.delay = minMax(this.sys.startDelay, 0, Math.random());
    this.particles = [];
  }

  update(dt) {
    if (!this.visible()) { if (this.active) this.reset(); return; }
    if (!this.active) this.start();
    const sys = this.sys, init = sys.InitialModule ?? {};
    dt *= sys.simulationSpeed ?? 1;
    this.time += dt;
    const world = this.node.matrixWorld;
    const basis = new THREE.Matrix3().setFromMatrix4(world);
    const scale = Math.cbrt(Math.abs(world.determinant()));
    const t = this.time - this.delay, duration = Math.max(sys.lengthInSec ?? 5, 0.01);
    // Émission (continue et salves) pendant la durée du système, ou en boucle
    if (t >= 0 && (sys.looping || t <= duration)) {
      const local = sys.looping ? t % duration : t;
      const nt = local / duration;
      const em = sys.EmissionModule;
      if (em?.enabled) {
        this.emitted += minMax(em.rateOverTime, nt, Math.random()) * dt;
        while (this.emitted >= 1) { this.emitted--; this.emit(nt, world, basis); }
        const wrapped = local < this.lastLocal;
        const inRange = x => wrapped ? (x > this.lastLocal || x <= local) : (x > this.lastLocal && x <= local);
        for (const b of em.m_Bursts ?? []) {
          const reps = b.cycleCount > 0 ? b.cycleCount : 1;
          for (let k = 0; k < reps; k++) {
            const when = b.time + k * Math.max(b.repeatInterval, 0.01);
            if (when > duration || !inRange(when) || Math.random() > (b.probability ?? 1)) continue;
            const count = Math.round(minMax(b.countCurve, nt, Math.random()));
            for (let i = 0; i < count; i++) this.emit(nt, world, basis);
          }
        }
      }
      this.lastLocal = local;
    }
    // Vie des particules
    const gravity = new THREE.Vector3(0, GRAVITY * 2, 0);
    if (this.local) gravity.applyMatrix3(basis.clone().invert());
    const vel = sys.VelocityModule, force = sys.ForceModule, size = sys.SizeModule, rot = sys.RotationModule;
    const tmp = new THREE.Vector3();
    this.particles = this.particles.filter(p => (p.age += dt) < p.life);
    for (const p of this.particles) {
      const lt = p.age / p.life;
      p.vel.addScaledVector(gravity, minMax(init.gravityModifier, lt, p.r[0]) * dt);
      if (force?.enabled) {
        tmp.set(-minMax(force.x, lt, p.r[1]), minMax(force.y, lt, p.r[2]), minMax(force.z, lt, p.r[3]));
        p.vel.addScaledVector(this.convert(tmp, force.inWorldSpace, basis), dt);
      }
      p.pos.addScaledVector(p.vel, dt);
      if (vel?.enabled) {
        tmp.set(-minMax(vel.x, lt, p.r[4]), minMax(vel.y, lt, p.r[5]), minMax(vel.z, lt, p.r[6]));
        p.pos.addScaledVector(this.convert(tmp, vel.inWorldSpace, basis), dt);
      }
      if (rot?.enabled) p.rot -= minMax(rot.curve, lt, p.r[7]) * dt;
    }
    this.write(world, basis, scale, size, sys.ColorModule, sys.UVModule);
  }

  // Vecteur d'un module (repère de l'objet ou du monde) vers l'espace de simulation
  convert(v, inWorld, basis) {
    if (this.local && inWorld) return v.applyMatrix3(basis.clone().invert());
    if (!this.local && !inWorld) return v.applyMatrix3(basis);
    if (inWorld) v.multiplyScalar(2);
    return v;
  }

  emit(nt, world, basis) {
    if (this.particles.length >= this.max) return;
    const init = this.sys.InitialModule ?? {}, shape = this.sys.ShapeModule;
    const r = Array.from({ length: 12 }, Math.random);
    const pos = new THREE.Vector3(), dir = new THREE.Vector3(0, 0, 1);
    if (shape?.enabled) {
      const radius = shape.radius?.value ?? 1, thick = shape.radiusThickness ?? 1;
      const arc = deg(shape.arc?.value ?? 360) * Math.random();
      switch (shape.type) {
        case 0: case 1: case 2: case 3: {
          dir.copy(randomUnit());
          if (shape.type >= 2) dir.z = Math.abs(dir.z);
          pos.copy(dir).multiplyScalar(radius * THREE.MathUtils.lerp(1, Math.cbrt(Math.random()), shape.type % 2 ? 0 : thick));
          break;
        }
        case 4: case 7: case 8: case 9: {
          const f = THREE.MathUtils.lerp(1, Math.sqrt(Math.random()), shape.type === 7 || shape.type === 9 ? 0 : thick);
          const angle = deg(shape.angle ?? 25) * f;
          pos.set(Math.cos(arc) * radius * f, Math.sin(arc) * radius * f, 0);
          dir.set(Math.cos(arc) * Math.sin(angle), Math.sin(arc) * Math.sin(angle), Math.cos(angle));
          if (shape.type >= 8) pos.addScaledVector(dir, (shape.length ?? 5) * Math.random());
          break;
        }
        case 5: case 15: case 16:
          pos.set(Math.random() - 0.5, Math.random() - 0.5, Math.random() - 0.5);
          break;
        case 10: case 11: {
          const f = shape.type === 11 ? 1 : THREE.MathUtils.lerp(1, Math.sqrt(Math.random()), thick);
          dir.set(Math.cos(arc), Math.sin(arc), 0);
          pos.copy(dir).multiplyScalar(radius * f);
          break;
        }
        case 12:
          pos.set((Math.random() * 2 - 1) * radius, 0, 0);
          dir.set(0, 1, 0);
          break;
        case 17: {
          dir.set(Math.cos(arc), Math.sin(arc), 0);
          const a = Math.random() * Math.PI * 2, d = (shape.donutRadius ?? 0.2) * Math.sqrt(Math.random());
          pos.copy(dir).multiplyScalar(radius + Math.cos(a) * d);
          pos.z = Math.sin(a) * d;
          break;
        }
        case 18:
          pos.set(Math.random() - 0.5, Math.random() - 0.5, 0);
          break;
        default:
          dir.copy(randomUnit());
      }
      const q = new THREE.Quaternion().setFromEuler(new THREE.Euler(deg(shape.m_Rotation?.x ?? 0), deg(shape.m_Rotation?.y ?? 0), deg(shape.m_Rotation?.z ?? 0), 'YXZ'));
      const s = v3(shape.m_Scale ?? { x: 1, y: 1, z: 1 });
      pos.multiply(s).applyQuaternion(q).add(v3(shape.m_Position));
      dir.multiply(s).applyQuaternion(q).normalize();
      if (shape.randomDirectionAmount) dir.lerp(randomUnit(), shape.randomDirectionAmount).normalize();
      if (shape.sphericalDirectionAmount && pos.lengthSq() > 0) dir.lerp(pos.clone().normalize(), shape.sphericalDirectionAmount).normalize();
    }
    // Repère Unity → glTF (X inversé), puis monde si la simulation n'est pas locale
    pos.x = -pos.x; dir.x = -dir.x;
    const vel = dir.multiplyScalar(minMax(init.startSpeed, nt, r[8]));
    if (!this.local) { pos.applyMatrix4(world); vel.applyMatrix3(basis); }
    const s = minMax(init.startSize, nt, r[9]);
    const size = init.size3D ? new THREE.Vector3(s, minMax(init.startSizeY, nt, r[10]), minMax(init.startSizeZ, nt, r[11])) : new THREE.Vector3(s, s, s);
    let rot = minMax(init.startRotation, nt, r[2]);
    if (Math.random() < (init.randomizeRotationDirection ?? 0)) rot = -rot;
    this.particles.push({
      pos, vel, size, rot: -rot, age: 0, r,
      life: Math.max(minMax(init.startLifetime, nt, r[3]), 0.01),
      color: minMaxGradient(init.startColor, nt, r[1]),
      row: Math.random(),
    });
  }

  write(world, basis, scale, size, color, uv) {
    const a = this.a, n = this.particles.length;
    const wp = new THREE.Vector3(), wv = new THREE.Vector3();
    const tiles = uv?.enabled ? [Math.max(uv.tilesX, 1), Math.max(uv.tilesY, 1)] : null;
    for (let i = 0; i < n; i++) {
      const p = this.particles[i], lt = p.age / p.life;
      wp.copy(p.pos); wv.copy(p.vel);
      if (this.local) { wp.applyMatrix4(world); wv.applyMatrix3(basis); }
      a.pos.setXYZ(i, wp.x, wp.y, wp.z);
      a.vel.setXYZ(i, wv.x, wv.y, wv.z);
      let sx = p.size.x, sy = p.size.y, sz = p.size.z;
      if (size?.enabled) {
        const k = minMax(size.curve, lt, p.r[5]);
        if (size.separateAxes) { sx *= k; sy *= minMax(size.y, lt, p.r[6]); sz *= minMax(size.z, lt, p.r[7]); }
        else { sx *= k; sy *= k; sz *= k; }
      }
      if (this.mode === 4) {
        // Particule maillage : axes tournés autour de Z, passés dans le repère de l'objet
        const c = Math.cos(p.rot), s = Math.sin(p.rot);
        const ax = new THREE.Vector3(c * sx, s * sx, 0), ay = new THREE.Vector3(-s * sy, c * sy, 0), az = new THREE.Vector3(0, 0, sz);
        for (const v of [ax, ay, az]) { if (this.local) v.applyMatrix3(basis); else v.multiplyScalar(scale); }
        a.x.setXYZ(i, ax.x, ax.y, ax.z); a.y.setXYZ(i, ay.x, ay.y, ay.z); a.z.setXYZ(i, az.x, az.y, az.z);
      }
      a.size.setXYZ(i, sx * scale, sy * scale, sz * scale);
      a.rot.setX(i, p.rot);
      const over = color?.enabled ? minMaxGradient(color.gradient, lt, p.r[4]) : [1, 1, 1, 1];
      a.color.setXYZW(i, p.color[0] * over[0], p.color[1] * over[1], p.color[2] * over[2], p.color[3] * over[3]);
      if (tiles) {
        const total = uv.animationType === 1 ? tiles[0] : tiles[0] * tiles[1];
        const f = ((minMax(uv.frameOverTime, lt, p.r[8]) + minMax(uv.startFrame, 0, p.r[9]) / total) * Math.max(uv.cycles, 1)) % 1;
        let frame = Math.min(Math.floor(f * total), total - 1);
        if (uv.animationType === 1) frame += (uv.randomRow ? Math.floor(p.row * tiles[1]) : uv.rowIndex) * tiles[0];
        const fx = frame % tiles[0], fy = Math.floor(frame / tiles[0]);
        a.uv.setXYZW(i, fx / tiles[0], 1 - (fy + 1) / tiles[1], 1 / tiles[0], 1 / tiles[1]);
      } else a.uv.setXYZW(i, 0, 0, 1, 1);
    }
    this.geometry.instanceCount = n;
    for (const v of Object.values(a)) v.needsUpdate = true;
  }

  dispose() {
    this.mesh.parent?.remove(this.mesh);
    this.geometry.dispose();
    this.material.dispose();
  }
}

/** Effets d'une emote : particules accrochées aux nœuds du modèle chargé. */
export class VfxPlayer {
  constructor(scene, model, data) {
    this.scene = scene;
    this.textures = {};
    const loader = new THREE.TextureLoader(), loading = [];
    for (const [name, url] of Object.entries(data.textures ?? {})) {
      loading.push(new Promise(done => {
        const t = this.textures[name] = loader.load(url, done, undefined, done);
        t.wrapS = t.wrapT = THREE.RepeatWrapping;
      }));
    }
    /** Résolue quand les textures sont prêtes. */
    this.ready = Promise.all(loading);
    const nodes = {};
    model.traverse(o => { if (o.name.startsWith('vfx')) nodes[o.name] = o; });
    // GLTFLoader remplace les caractères spéciaux des noms de nœuds
    const find = name => nodes[name] ?? nodes[THREE.PropertyBinding.sanitizeNodeName(name)];
    this.emitters = [];
    for (const def of data.emitters ?? []) {
      const node = find(def.node);
      if (!node) continue;
      const e = new Emitter(def, node, def.material?.texture ? this.textures[def.material.texture] : null);
      scene.add(e.mesh);
      this.emitters.push(e);
    }
  }

  get count() { return this.emitters.length; }

  update(dt) { for (const e of this.emitters) e.update(Math.min(dt, 0.1)); }

  restart() { for (const e of this.emitters) e.reset(); }

  dispose() {
    for (const e of this.emitters) e.dispose();
    for (const t of Object.values(this.textures)) t.dispose();
    this.emitters = [];
  }
}
