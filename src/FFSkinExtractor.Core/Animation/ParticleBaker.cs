using System.Numerics;
using System.Text.Json.Nodes;

namespace FFSkinExtractor.Core.Animation;

/// <summary>Une particule cuite : vie, couleur moyenne et positions successives dans le repère Unity de son émetteur.</summary>
public sealed class BakedParticle
{
    public required int Emitter { get; init; }
    public required float Birth { get; init; }
    public float Death { get; set; }
    public Vector4 Color { get; set; } = Vector4.One;
    /// <summary>Rectangle de texture (feuille d'animation : première image), en UV Unity.</summary>
    public Vector4 UvRect { get; init; } = new(0, 0, 1, 1);
    public List<(float Time, Vector3 Position, Vector3 Size, float Rotation)> Keys { get; } = [];
}

/// <summary>
/// Simule les systèmes de particules d'un effet sur la durée de l'emote, pour les écrire dans le GLB : glTF (comme
/// FBX) ne décrit pas de particules, elles deviennent des plans animés. Même logique que l'aperçu (wwwroot/vfx.js),
/// simplifiée : la simulation se fait dans le repère de l'émetteur, l'orientation des objets animés n'est pas suivie.
/// </summary>
public static class ParticleBaker
{
    const float Step = 1 / 30f, KeyStep = 1 / 15f, Gravity = -9.81f;
    public const int MaxPerEmitter = 120, MaxTotal = 2500;

    public static List<BakedParticle> Bake(EmoteVfx vfx, float end, Func<int, float, bool> visible)
    {
        var all = new List<BakedParticle>();
        for (int i = 0; i < vfx.Emitters.Count && all.Count < MaxTotal; i++)
        {
            try { all.AddRange(new Sim(vfx, i, visible).Run(end, MaxTotal - all.Count)); }
            catch (Exception) { /* réglage inattendu : émetteur ignoré */ }
        }
        return all;
    }

    sealed class Live
    {
        public required BakedParticle Baked;
        public Vector3 Position, Velocity, Size;
        public float Rotation, Age, Life;
        public Vector4 Start;
        public required float[] R;
        public float NextKey;
    }

    sealed class Sim(EmoteVfx vfx, int index, Func<int, float, bool> visible)
    {
        readonly VfxEmitter _e = vfx.Emitters[index];
        readonly Random _rng = new(1234 + index);
        JsonObject Sys => _e.System;
        JsonObject? Module(string name) => Sys[name] is JsonObject m && Bool(m["enabled"]) ? m : null;

        public List<BakedParticle> Run(float end, int budget)
        {
            var result = new List<BakedParticle>();
            var live = new List<Live>();
            var init = Sys["InitialModule"] as JsonObject;
            int max = (int)Math.Min(Num(init?["maxNumParticles"], 100), MaxPerEmitter);
            float duration = Math.Max(Num(Sys["lengthInSec"], 5), 0.01f), speed = Num(Sys["simulationSpeed"], 1);
            bool looping = Bool(Sys["looping"]);
            var em = Module("EmissionModule");
            var gravity = LocalGravity();
            bool active = false;
            float time = 0, delay = 0, emitted = 0, lastLocal = 0;
            for (float t = 0; t <= end; t += Step)
            {
                if (!visible(_e.Node, t))
                {
                    if (active) { foreach (var p in live) Finish(p, t); live.Clear(); active = false; }
                    continue;
                }
                if (!active) { active = true; time = 0; emitted = 0; lastLocal = -1e-6f; delay = MinMax(Sys["startDelay"], 0, R()); }
                float dt = Step * speed;
                time += dt;
                float st = time - delay;
                if (st >= 0 && (looping || st <= duration) && em != null)
                {
                    float local = looping ? st % duration : st, nt = local / duration;
                    void Emit()
                    {
                        if (live.Count >= max || result.Count >= budget) return;
                        var p = Spawn(nt, t, init);
                        live.Add(p);
                        result.Add(p.Baked);
                    }
                    emitted += MinMax(em["rateOverTime"], nt, R()) * dt;
                    while (emitted >= 1) { emitted--; Emit(); }
                    bool wrapped = local < lastLocal;
                    foreach (var b in em["m_Bursts"] as JsonArray ?? [])
                    {
                        if (b is not JsonObject burst) continue;
                        int reps = Math.Max((int)Num(burst["cycleCount"], 1), 1);
                        for (int k = 0; k < reps; k++)
                        {
                            float when = Num(burst["time"]) + k * Math.Max(Num(burst["repeatInterval"]), 0.01f);
                            bool hit = wrapped ? when > lastLocal || when <= local : when > lastLocal && when <= local;
                            if (when > duration || !hit || R() > Num(burst["probability"], 1)) continue;
                            int count = (int)MathF.Round(MinMax(burst["countCurve"], nt, R()));
                            for (int c = 0; c < count; c++) Emit();
                        }
                    }
                    lastLocal = local;
                }
                Update(live, dt, t, gravity);
            }
            foreach (var p in live) Finish(p, end);
            return result;
        }

        // Gravité du monde dans le repère de l'émetteur (orientation et échelle fixes de ses parents)
        Vector3 LocalGravity()
        {
            var rot = Quaternion.Identity;
            float scale = 1;
            for (int n = _e.Node; n >= 0; n = vfx.Nodes[n].Parent)
            {
                rot = vfx.Nodes[n].Rotation * rot;
                scale *= vfx.Nodes[n].Scale.Y is var s && s != 0 ? s : 1;
                if (vfx.Nodes[n].AttachBone != null) break;
            }
            return Vector3.Transform(new Vector3(0, Gravity, 0), Quaternion.Inverse(rot)) / scale;
        }

        void Update(List<Live> live, float dt, float t, Vector3 gravity)
        {
            var init = Sys["InitialModule"] as JsonObject;
            var vel = Module("VelocityModule");
            var force = Module("ForceModule");
            var size = Module("SizeModule");
            var rot = Module("RotationModule");
            var color = Module("ColorModule");
            for (int i = live.Count - 1; i >= 0; i--)
            {
                var p = live[i];
                p.Age += dt;
                if (p.Age >= p.Life) { Finish(p, t); live.RemoveAt(i); continue; }
                float lt = p.Age / p.Life;
                p.Velocity += gravity * MinMax(init?["gravityModifier"], lt, p.R[0]) * dt;
                if (force != null) p.Velocity += new Vector3(MinMax(force["x"], lt, p.R[1]), MinMax(force["y"], lt, p.R[2]), MinMax(force["z"], lt, p.R[3])) * dt;
                p.Position += p.Velocity * dt;
                if (vel != null) p.Position += new Vector3(MinMax(vel["x"], lt, p.R[4]), MinMax(vel["y"], lt, p.R[5]), MinMax(vel["z"], lt, p.R[6])) * dt;
                if (rot != null) p.Rotation += MinMax(rot["curve"], lt, p.R[7]) * dt;
                if (p.Age >= p.NextKey)
                {
                    var s = p.Size;
                    if (size != null)
                    {
                        float k = MinMax(size["curve"], lt, p.R[5]);
                        s = Bool(size["separateAxes"]) ? new Vector3(s.X * k, s.Y * MinMax(size["y"], lt, p.R[6]), s.Z * MinMax(size["z"], lt, p.R[7])) : s * k;
                    }
                    // glTF ne sait pas animer la couleur : la transparence au fil de la vie réduit la taille
                    float alpha = color != null ? MinMaxGradient(color["gradient"], lt, p.R[4]).W : 1;
                    p.Baked.Keys.Add((t, p.Position, s * Math.Clamp(alpha * 1.5f, 0, 1), p.Rotation));
                    p.NextKey = p.Age + KeyStep;
                }
            }
        }

        static void Finish(Live p, float t) => p.Baked.Death = t;

        Live Spawn(float nt, float t, JsonObject? init)
        {
            var r = Enumerable.Range(0, 12).Select(_ => R()).ToArray();
            var (pos, dir) = Shape(Module("ShapeModule"));
            float s = MinMax(init?["startSize"], nt, r[9]);
            var size = Bool(init?["size3D"]) ? new Vector3(s, MinMax(init?["startSizeY"], nt, r[10]), MinMax(init?["startSizeZ"], nt, r[11])) : new Vector3(s);
            float rotation = MinMax(init?["startRotation"], nt, r[2]);
            if (R() < Num(init?["randomizeRotationDirection"])) rotation = -rotation;
            var start = MinMaxGradient(init?["startColor"], nt, r[1]);
            // Couleur moyenne sur la vie (la couleur reste fixe dans le GLB)
            var over = Module("ColorModule") is { } cm
                ? Enumerable.Range(0, 5).Select(k => MinMaxGradient(cm["gradient"], k / 4f, r[4])).Aggregate(Vector4.Zero, (a, b) => a + b) / 5
                : Vector4.One;
            var uv = Module("UVModule");
            var rect = new Vector4(0, 0, 1, 1);
            if (uv != null)
            {
                int tx = Math.Max((int)Num(uv["tilesX"], 1), 1), ty = Math.Max((int)Num(uv["tilesY"], 1), 1);
                rect = new Vector4(0, 1 - 1f / ty, 1f / tx, 1f / ty);
            }
            var baked = new BakedParticle { Emitter = index, Birth = t, Color = start * over, UvRect = rect };
            return new Live
            {
                Baked = baked, Position = pos, Velocity = dir * MinMax(init?["startSpeed"], nt, r[8]), Size = size, Rotation = rotation,
                Life = Math.Max(MinMax(init?["startLifetime"], nt, r[3]), 0.01f), Start = start, R = r,
            };
        }

        (Vector3 Pos, Vector3 Dir) Shape(JsonObject? shape)
        {
            var pos = Vector3.Zero;
            var dir = Vector3.UnitZ;
            if (shape == null) return (pos, dir);
            float radius = Num((shape["radius"] as JsonObject)?["value"], 1), thick = Num(shape["radiusThickness"], 1);
            float arc = Num((shape["arc"] as JsonObject)?["value"], 360) * MathF.PI / 180 * R();
            int type = (int)Num(shape["type"]);
            switch (type)
            {
                case 0 or 1 or 2 or 3:
                    dir = RandomUnit();
                    if (type >= 2) dir.Z = MathF.Abs(dir.Z);
                    pos = dir * radius * Lerp(1, MathF.Cbrt(R()), type % 2 == 1 ? 0 : thick);
                    break;
                case 4 or 7 or 8 or 9:
                {
                    float f = Lerp(1, MathF.Sqrt(R()), type is 7 or 9 ? 0 : thick);
                    float angle = Num(shape["angle"], 25) * MathF.PI / 180 * f;
                    pos = new Vector3(MathF.Cos(arc) * radius * f, MathF.Sin(arc) * radius * f, 0);
                    dir = new Vector3(MathF.Cos(arc) * MathF.Sin(angle), MathF.Sin(arc) * MathF.Sin(angle), MathF.Cos(angle));
                    if (type >= 8) pos += dir * Num(shape["length"], 5) * R();
                    break;
                }
                case 5 or 15 or 16:
                    pos = new Vector3(R() - 0.5f, R() - 0.5f, R() - 0.5f);
                    break;
                case 10 or 11:
                    dir = new Vector3(MathF.Cos(arc), MathF.Sin(arc), 0);
                    pos = dir * radius * (type == 11 ? 1 : Lerp(1, MathF.Sqrt(R()), thick));
                    break;
                case 12:
                    pos = new Vector3((R() * 2 - 1) * radius, 0, 0);
                    dir = Vector3.UnitY;
                    break;
                case 18:
                    pos = new Vector3(R() - 0.5f, R() - 0.5f, 0);
                    break;
                default:
                    dir = RandomUnit();
                    break;
            }
            var rot = shape["m_Rotation"] as JsonObject;
            var q = ClipSampler.FromEuler(new Vector3(Num(rot?["x"]), Num(rot?["y"]), Num(rot?["z"])));
            var sc = shape["m_Scale"] is JsonObject so ? new Vector3(Num(so["x"], 1), Num(so["y"], 1), Num(so["z"], 1)) : Vector3.One;
            var p0 = shape["m_Position"] is JsonObject po ? new Vector3(Num(po["x"]), Num(po["y"]), Num(po["z"])) : Vector3.Zero;
            pos = Vector3.Transform(pos * sc, q) + p0;
            dir = Vector3.Transform(dir * sc, q);
            dir = dir.LengthSquared() > 0 ? Vector3.Normalize(dir) : Vector3.UnitZ;
            float random = Num(shape["randomDirectionAmount"]), spherical = Num(shape["sphericalDirectionAmount"]);
            if (random > 0) dir = Vector3.Normalize(Vector3.Lerp(dir, RandomUnit(), random));
            if (spherical > 0 && pos.LengthSquared() > 0) dir = Vector3.Normalize(Vector3.Lerp(dir, Vector3.Normalize(pos), spherical));
            return (pos, dir);
        }

        float R() => (float)_rng.NextDouble();

        Vector3 RandomUnit()
        {
            float z = R() * 2 - 1, a = R() * MathF.PI * 2, s = MathF.Sqrt(1 - z * z);
            return new Vector3(MathF.Cos(a) * s, MathF.Sin(a) * s, z);
        }
    }

    static float Lerp(float a, float b, float t) => a + (b - a) * t;

    static float Num(JsonNode? n, float fallback = 0)
    {
        if (n is not JsonValue v) return fallback;
        if (v.TryGetValue(out float f)) return f;
        if (v.TryGetValue(out double d)) return (float)d;
        if (v.TryGetValue(out long l)) return l;
        if (v.TryGetValue(out int i)) return i;
        if (v.TryGetValue(out bool b)) return b ? 1 : 0;
        return fallback;
    }

    static bool Bool(JsonNode? n) => Num(n) != 0;

    // Courbe Unity (clés Hermite)
    static float Curve(JsonNode? c, float t)
    {
        if ((c as JsonObject)?["m_Curve"] is not JsonArray k || k.Count == 0) return 0;
        float T(int i) => Num(k[i]?["time"]);
        float V(int i) => Num(k[i]?["value"]);
        if (t <= T(0)) return V(0);
        if (t >= T(k.Count - 1)) return V(k.Count - 1);
        int j = 1;
        while (T(j) < t) j++;
        float dt = T(j) - T(j - 1);
        if (dt <= 0) return V(j);
        float s = (t - T(j - 1)) / dt, s2 = s * s, s3 = s2 * s;
        return (2 * s3 - 3 * s2 + 1) * V(j - 1) + (s3 - 2 * s2 + s) * Num(k[j - 1]?["outSlope"]) * dt
            + (-2 * s3 + 3 * s2) * V(j) + (s3 - s2) * Num(k[j]?["inSlope"]) * dt;
    }

    static float MinMax(JsonNode? node, float t, float r)
    {
        if (node is not JsonObject m) return 0;
        float scalar = Num(m["scalar"]), min = Num(m["minScalar"]);
        return (int)Num(m["minMaxState"]) switch
        {
            1 => Curve(m["maxCurve"], t) * scalar,
            2 => Lerp(Curve(m["minCurve"], t) * (min != 0 ? min : scalar), Curve(m["maxCurve"], t) * scalar, r),
            3 => Lerp(min, scalar, r),
            _ => scalar,
        };
    }

    static Vector4 Color(JsonNode? c) => c is JsonObject o ? new Vector4(Num(o["r"], 1), Num(o["g"], 1), Num(o["b"], 1), Num(o["a"], 1)) : Vector4.One;

    static Vector4 Gradient(JsonNode? node, float t)
    {
        if (node is not JsonObject g) return Vector4.One;
        bool fixedMode = Num(g["m_Mode"]) == 1;
        float Sample(int count, string times, Func<Vector4, float> pick)
        {
            count = Math.Max(count, 1);
            float Time(int i) => Num(g[times + i]) / 65535f;
            float Key(int i) => pick(Color(g["key" + i]));
            if (t <= Time(0)) return Key(0);
            for (int i = 1; i < count; i++)
                if (t <= Time(i))
                    return fixedMode ? Key(i) : Lerp(Key(i - 1), Key(i), (t - Time(i - 1)) / Math.Max(Time(i) - Time(i - 1), 1e-6f));
            return Key(count - 1);
        }
        int nc = (int)Num(g["m_NumColorKeys"], 2), na = (int)Num(g["m_NumAlphaKeys"], 2);
        return new Vector4(Sample(nc, "ctime", c => c.X), Sample(nc, "ctime", c => c.Y), Sample(nc, "ctime", c => c.Z), Sample(na, "atime", c => c.W));
    }

    static Vector4 MinMaxGradient(JsonNode? node, float t, float r)
    {
        if (node is not JsonObject m) return Vector4.One;
        return (int)Num(m["minMaxState"]) switch
        {
            1 => Gradient(m["maxGradient"], t),
            2 => Vector4.Lerp(Color(m["minColor"]), Color(m["maxColor"]), r),
            3 => Vector4.Lerp(Gradient(m["minGradient"], t), Gradient(m["maxGradient"], t), r),
            4 => Gradient(m["maxGradient"], r),
            _ => Color(m["maxColor"]),
        };
    }
}
