using System.Numerics;
using FFSkinExtractor.Core.Rig;
using SharpGLTF.Geometry;
using SharpGLTF.Geometry.VertexTypes;
using SharpGLTF.Materials;
using SharpGLTF.Scenes;

namespace FFSkinExtractor.Core;

using VERTEX = VertexBuilder<VertexPositionNormal, VertexTexture1, VertexJoints4>;

public sealed record ExportOptions
{
    /// <summary>Ajoute l'armature et les poids (le modèle peut être posé et animé dans Blender).</summary>
    public bool Skinned { get; init; } = true;

    /// <summary>Ajoute normales, rugosité/métal et occlusion quand le jeu les fournit.</summary>
    public bool Pbr { get; init; } = true;

    /// <summary>Emote jouée par le squelette (piste d'animation du GLB), ou null.</summary>
    public Animation.EmoteClip? Emote { get; init; }

    /// <summary>Effet de l'emote (objets animés ; les particules sont jouées par l'aperçu), ou null.</summary>
    public Animation.EmoteVfx? Vfx { get; init; }
}

/// <summary>Exporte une tenue en GLB : maillages, textures, normales lissées et squelette.</summary>
public static class GlbExporter
{
    public static void Export(Outfit outfit, string path, ExportOptions? options = null) =>
        Build(outfit, options).ToGltf2().SaveGLB(path);

    public static byte[] ExportToBytes(Outfit outfit, ExportOptions? options = null)
    {
        var model = Build(outfit, options).ToGltf2();
        return model.WriteGLB().ToArray();
    }

    public static SceneBuilder Build(Outfit outfit, ExportOptions? options = null) =>
        Build(outfit.Gender + "_" + outfit.Name, outfit.Gender, outfit.Parts, options);

    /// <summary>Assemble des pièces (éventuellement venant de tenues différentes) sur un même squelette.</summary>
    public static SceneBuilder Build(string name, string gender, IReadOnlyList<OutfitPart> parts, ExportOptions? options = null)
    {
        options ??= new ExportOptions();
        var scene = new SceneBuilder(name);
        var materials = new Dictionary<string, MaterialBuilder>();
        var skeleton = Skeleton.For(gender);
        var world = skeleton.WorldMatrices();
        var rig = options.Skinned ? new RigBuilder(skeleton, parts.Select(p => p.Mesh).Distinct()) : null;
        var joints = rig?.Joints;

        foreach (var part in parts)
        {
            var mesh = new MeshBuilder<VertexPositionNormal, VertexTexture1, VertexJoints4>(part.SlotName);
            var prim = mesh.UsePrimitive(MaterialFor(part, materials, options));
            var src = part.Mesh;
            var tri = src.Submeshes[Math.Min(part.SubmeshIndex, src.Submeshes.Count - 1)];

            // Unity (main gauche) → glTF (main droite) : X inversé, donc ordre des sommets inversé
            var pos = ModelPositions(src, skeleton, world).Select(p => new Vector3(-p.X, p.Y, p.Z)).ToArray();
            var normals = SmoothNormals(pos, tri);
            var table = rig?.TableFor(src);
            var cache = new Dictionary<int, VERTEX>();

            VERTEX V(int i)
            {
                if (cache.TryGetValue(i, out var v)) return v;
                var uv = src.UVs[i];
                var skin = rig == null ? default : new VertexJoints4(rig.Bind(src, i, pos[i], table!));
                return cache[i] = new VERTEX(new VertexPositionNormal(pos[i], normals[i]),
                    new VertexTexture1(new Vector2(uv.X, 1 - uv.Y)), skin);
            }

            for (int i = 0; i + 2 < tri.Length; i += 3)
                prim.AddTriangle(V(tri[i]), V(tri[i + 2]), V(tri[i + 1]));

            if (rig != null) scene.AddSkinnedMesh(mesh, Matrix4x4.Identity, joints!);
            else scene.AddRigidMesh(mesh, Matrix4x4.Identity);
        }
        if (rig != null && parts.Count == 0) scene.AddNode(rig.Root);
        if (rig != null && options.Emote is { } emote) Animate(rig, emote);
        if (options.Vfx is { } vfx) AddVfx(scene, rig, vfx, options.Emote?.Name ?? vfx.Name);
        return scene;
    }

    /// <summary>Clés de l'emote sur les articulations du même nom (Unity → glTF : miroir sur X, comme le squelette).</summary>
    static void Animate(RigBuilder rig, Animation.EmoteClip emote)
    {
        string track = emote.Name;
        var byName = rig.Joints.GroupBy(j => j.Name).ToDictionary(g => g.Key, g => g.First());
        foreach (var t in emote.Tracks)
        {
            if (!byName.TryGetValue(t.Bone, out var node)) continue;
            if (t.Positions.Count > 0)
            {
                var curve = node.UseTranslation(track);
                foreach (var (time, p) in t.Positions) curve.SetPoint(time, new Vector3(-p.X, p.Y, p.Z));
            }
            if (t.Rotations.Count > 0)
            {
                var curve = node.UseRotation(track);
                foreach (var (time, r) in t.Rotations) curve.SetPoint(time, new Quaternion(r.X, -r.Y, -r.Z, r.W));
            }
            if (t.Scales.Count > 0)
            {
                var curve = node.UseScale(track);
                foreach (var (time, sc) in t.Scales) curve.SetPoint(time, sc);
            }
        }
    }

    /// <summary>Nom du nœud d'un objet d'effet dans le GLB (unique : l'aperçu y accroche les particules).</summary>
    public static string VfxNodeName(int index, string name) => $"vfx{index:D3}_{name}";

    static Vector3 Mirror(Vector3 p) => new(-p.X, p.Y, p.Z);
    static Quaternion Mirror(Quaternion q) => new(q.X, -q.Y, -q.Z, q.W);
    static readonly Matrix4x4 MirrorX = Matrix4x4.CreateScale(-1, 1, 1);

    /// <summary>
    /// Ajoute l'effet d'une emote : hiérarchie d'objets (en unités du jeu, sous un nœud à l'échelle d'export),
    /// maillages, et animations ; un objet caché est mis à l'échelle zéro.
    /// </summary>
    static void AddVfx(SceneBuilder scene, RigBuilder? rig, Animation.EmoteVfx vfx, string track)
    {
        var root = new NodeBuilder("VFX_" + vfx.Name);
        root.LocalTransform = new SharpGLTF.Transforms.AffineTransform(new Vector3(Uma.UmaMeshDecoder.ExportScale), Quaternion.Identity, Vector3.Zero);
        var joints = rig?.Joints.GroupBy(j => j.Name).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var nodes = new NodeBuilder[vfx.Nodes.Count];
        float end = vfx.Nodes.Select(n => Math.Max(n.Track?.Positions.LastOrDefault().Time ?? 0, Math.Max(n.Track?.Rotations.LastOrDefault().Time ?? 0,
            n.ActiveKeys?.LastOrDefault().Time ?? 0))).DefaultIfEmpty(0).Max();
        for (int i = 0; i < vfx.Nodes.Count; i++)
        {
            var n = vfx.Nodes[i];
            NodeBuilder parent = n.Parent >= 0 ? nodes[n.Parent] : root;
            // Objet accroché à un os du personnage (« Head », « RightHand ») : il suit l'animation de l'os
            if (n.AttachBone is { } bone && !bone.Equals("Root", StringComparison.OrdinalIgnoreCase) && joints != null
                && (joints.GetValueOrDefault("bone_" + bone) ?? joints.GetValueOrDefault(bone)) is { } joint)
            {
                var holder = joint.CreateNode("VFX_" + bone);
                holder.LocalTransform = new SharpGLTF.Transforms.AffineTransform(new Vector3(Uma.UmaMeshDecoder.ExportScale), Quaternion.Identity, Vector3.Zero);
                parent = holder;
            }
            var node = nodes[i] = parent.CreateNode(VfxNodeName(i, n.Name));
            node.LocalTransform = new SharpGLTF.Transforms.AffineTransform(n.Scale, Mirror(n.Rotation), Mirror(n.Position));
            if (n.Track is { } t)
            {
                if (t.Positions.Count > 0) { var c = node.UseTranslation(track); foreach (var (time, p) in t.Positions) c.SetPoint(time, Mirror(p)); }
                if (t.Rotations.Count > 0) { var c = node.UseRotation(track); foreach (var (time, q) in t.Rotations) c.SetPoint(time, Mirror(q)); }
            }
            // Visibilité : l'objet actif ou non (et son rendu activé ou non), par l'échelle
            bool Visible(float time)
            {
                bool active = n.ActiveKeys is { Count: > 0 } k ? k.LastOrDefault(x => x.Time <= time + 1e-4f, k[0]).Active : n.Active;
                if (n.EnabledKeys is { Count: > 0 } e) active &= e.LastOrDefault(x => x.Time <= time + 1e-4f, e[0]).Active;
                else if (!n.RendererEnabled) active = false;
                return active;
            }
            bool animatedScale = n.Track?.Scales.Count > 0, toggles = n.ActiveKeys != null || n.EnabledKeys != null;
            if (animatedScale || toggles || !Visible(0))
            {
                var c = node.UseScale(track);
                var times = (animatedScale ? n.Track!.Scales.Select(x => x.Time) : Enumerable.Empty<float>())
                    .Concat(n.ActiveKeys?.Select(x => x.Time) ?? []).Concat(n.EnabledKeys?.Select(x => x.Time) ?? [])
                    .Append(0f).Append(end).Distinct().Order().ToList();
                foreach (float time in times)
                {
                    var sc = animatedScale ? n.Track!.Scales.LastOrDefault(x => x.Time <= time + 1e-4f, n.Track.Scales[0]).Value : n.Scale;
                    c.SetPoint(time, Visible(time) ? sc : Vector3.Zero, false);
                }
            }
        }
        scene.AddNode(root);

        var materials = new Dictionary<Animation.VfxMaterial, MaterialBuilder>();
        foreach (var m in vfx.Meshes)
        {
            var src = m.Mesh;
            var pos = src.Positions.Select(Mirror).ToArray();
            var nrm = src.Normals?.Select(Mirror).ToArray();
            bool skinned = m.Bones != null && src.Skin != null && src.BindPoses.Length > 0;
            for (int s = 0; s < src.Submeshes.Count; s++)
            {
                var tri = src.Submeshes[s];
                if (tri.Length < 3) continue;
                var mat = m.Materials.ElementAtOrDefault(Math.Min(s, m.Materials.Length - 1));
                var material = VfxMaterialFor(mat, materials);
                var tile = mat?.Tiling ?? Vector2.One;
                var off = mat?.Offset ?? Vector2.Zero;
                Vector2 Uv(int i) => src.UVs is { } uv ? new Vector2(uv[i].X * tile.X + off.X, 1 - (uv[i].Y * tile.Y + off.Y)) : Vector2.Zero;
                Vector4 Col(int i) => src.Colors?[i] ?? Vector4.One;
                if (skinned)
                {
                    var mesh = new MeshBuilder<VertexPositionNormal, VertexColor1Texture1, VertexJoints4>(src.Name);
                    var prim = mesh.UsePrimitive(material);
                    VertexBuilder<VertexPositionNormal, VertexColor1Texture1, VertexJoints4> V(int i)
                    {
                        var (bi, bw) = src.Skin![i];
                        var pairs = Enumerable.Range(0, 4).Where(k => bw[k] > 0 && bi[k] < src.BindPoses.Length).Select(k => (bi[k], bw[k])).ToArray();
                        if (pairs.Length == 0) pairs = [(0, 1f)];
                        return new(new VertexPositionNormal(pos[i], nrm?[i] ?? Vector3.UnitY), new VertexColor1Texture1(Col(i), Uv(i)), new VertexJoints4(pairs));
                    }
                    for (int i = 0; i + 2 < tri.Length; i += 3) prim.AddTriangle(V(tri[i]), V(tri[i + 2]), V(tri[i + 1]));
                    var jointList = src.BindPoses.Select((b, k) =>
                    {
                        int nodeIndex = m.Bones!.ElementAtOrDefault(k);
                        var jn = nodeIndex >= 0 && nodeIndex < nodes.Length && m.Bones.Length > k ? nodes[nodeIndex] : nodes[m.Node];
                        return (jn, MirrorX * b * MirrorX);
                    }).ToArray();
                    scene.AddSkinnedMesh(mesh, jointList);
                }
                else
                {
                    var mesh = new MeshBuilder<VertexPositionNormal, VertexColor1Texture1, VertexEmpty>(src.Name);
                    var prim = mesh.UsePrimitive(material);
                    VertexBuilder<VertexPositionNormal, VertexColor1Texture1, VertexEmpty> V(int i) =>
                        new(new VertexPositionNormal(pos[i], nrm?[i] ?? Vector3.UnitY), new VertexColor1Texture1(Col(i), Uv(i)));
                    for (int i = 0; i + 2 < tri.Length; i += 3) prim.AddTriangle(V(tri[i]), V(tri[i + 2]), V(tri[i + 1]));
                    scene.AddRigidMesh(mesh, nodes[m.Node]);
                }
            }
        }
    }

    /// <summary>
    /// Matériau d'un objet d'effet. Le nom commence par le mode de mélange (« ADD_ », « BLEND_ ») :
    /// glTF ne connaît pas le mélange additif, l'aperçu le rétablit.
    /// </summary>
    static MaterialBuilder VfxMaterialFor(Animation.VfxMaterial? m, Dictionary<Animation.VfxMaterial, MaterialBuilder> cache)
    {
        if (m == null) return new MaterialBuilder("VFX").WithDoubleSide(true).WithBaseColor(new Vector4(0.8f, 0.8f, 0.8f, 1));
        if (cache.TryGetValue(m, out var mat)) return mat;
        string prefix = m.Blend switch { Animation.VfxBlend.Additive => "ADD_", Animation.VfxBlend.Alpha => "BLEND_", Animation.VfxBlend.Cutout => "CUT_", _ => "" };
        mat = new MaterialBuilder(prefix + m.Name).WithDoubleSide(true);
        if (m.Blend is Animation.VfxBlend.Additive or Animation.VfxBlend.Alpha) mat.WithUnlitShader(); else mat.WithMetallicRoughnessShader().WithMetallicRoughness(0, 0.8f);
        var color = Vector4.Clamp(m.Color, Vector4.Zero, Vector4.One);
        if (m.Texture != null) mat.WithBaseColor(Image(m.Texture), color); else mat.WithBaseColor(color);
        if (m.Blend is Animation.VfxBlend.Additive or Animation.VfxBlend.Alpha) mat.WithAlpha(AlphaMode.BLEND);
        else if (m.Blend == Animation.VfxBlend.Cutout) mat.WithAlpha(AlphaMode.MASK, 0.5f);
        return cache[m] = mat;
    }

    // Repère habituel des maillages : Z vers le haut dans les données, déjà converti par le décodeur
    static readonly Matrix4x4 UsualFrame = new(1, 0, 0, 0, 0, 0, -1, 0, 0, 1, 0, 0, 0, 0, 0, 1);

    /// <summary>
    /// Positions dans le repère du personnage. La plupart des maillages sont rangés Z vers le haut, mais certains
    /// (Racergirl, Scorpio, cheveux de Seaman…) sont rangés Y vers le haut, ou autour d'un os (la tête) :
    /// leurs matrices de liaison (bindPoses) et le squelette de base donnent le bon repère.
    /// </summary>
    static Vector3[] ModelPositions(Uma.UmaMesh mesh, Skeleton skeleton, Matrix4x4[] world)
    {
        var binds = mesh.OwnBoneHashes.Zip(mesh.OwnBindPoses).Concat(mesh.ExtraBoneHashes.Zip(mesh.ExtraBindPoses));
        foreach (var (hash, bind) in binds)
        {
            int i = skeleton.IndexOf(hash);
            if (i < 0) continue;
            var b = bind;
            b.M41 *= Uma.UmaMeshDecoder.ExportScale; b.M42 *= Uma.UmaMeshDecoder.ExportScale; b.M43 *= Uma.UmaMeshDecoder.ExportScale;
            var frame = b * world[i];
            if (Close(frame, UsualFrame)) return mesh.Positions;
            float scale = new Vector3(frame.M11, frame.M12, frame.M13).Length();
            if (scale is < 0.8f or > 1.25f) return mesh.Positions;   // matrice inattendue : on garde le décodage habituel
            // Retour aux coordonnées des données (le décodeur a fait x, z, -y), puis repère du personnage
            return mesh.Positions.Select(p => Vector3.Transform(new Vector3(p.X, -p.Z, p.Y), frame)).ToArray();
        }
        return mesh.Positions;
    }

    static bool Close(Matrix4x4 a, Matrix4x4 b)
    {
        for (int r = 0; r < 4; r++)
            for (int c = 0; c < 4; c++)
                if (Math.Abs(a[r, c] - b[r, c]) > (r == 3 ? 0.01f : 0.02f)) return false;
        return true;
    }

    /// <summary>
    /// Normales lissées. Les sommets à la même position (coutures d'UV) partagent leur normale
    /// pour éviter les arêtes visibles.
    /// </summary>
    static Vector3[] SmoothNormals(Vector3[] pos, int[] tri)
    {
        var byPos = new Dictionary<(int, int, int), Vector3>();
        static (int, int, int) Key(Vector3 p) => ((int)MathF.Round(p.X * 2000), (int)MathF.Round(p.Y * 2000), (int)MathF.Round(p.Z * 2000));
        for (int i = 0; i + 2 < tri.Length; i += 3)
        {
            // Même ordre que les triangles émis (i, i+2, i+1)
            Vector3 a = pos[tri[i]], b = pos[tri[i + 2]], c = pos[tri[i + 1]];
            var n = Vector3.Cross(b - a, c - a);       // pondérée par l'aire
            foreach (int v in new[] { tri[i], tri[i + 1], tri[i + 2] })
            {
                var k = Key(pos[v]);
                byPos[k] = byPos.GetValueOrDefault(k) + n;
            }
        }
        var result = new Vector3[pos.Length];
        for (int i = 0; i < pos.Length; i++)
        {
            var n = byPos.GetValueOrDefault(Key(pos[i]));
            result[i] = n.LengthSquared() > 1e-20f ? Vector3.Normalize(n) : Vector3.UnitY;
        }
        return result;
    }

    static MaterialBuilder MaterialFor(OutfitPart part, Dictionary<string, MaterialBuilder> cache, ExportOptions options)
    {
        var tx = part.Textures;
        string key = (tx.BaseColor?.Name ?? (part.IsSkin ? "peau" : "(sans texture)"))
            + (part.Material == MaterialKind.Opaque ? "" : "_" + part.Material);
        if (cache.TryGetValue(key, out var mat)) return mat;
        mat = new MaterialBuilder(key).WithDoubleSide(true).WithMetallicRoughnessShader();
        if (tx.BaseColor != null)
        {
            mat.WithBaseColor(Image(tx.BaseColor),
                part.Material == MaterialKind.Transparent ? new Vector4(1, 1, 1, 0.4f) : Vector4.One);
        }
        else if (part.IsSkin)
            mat.WithBaseColor(new Vector4(0.87f, 0.66f, 0.52f, 1)); // couleur de peau, faute d'APK
        else
            mat.WithBaseColor(new Vector4(0.7f, 0.7f, 0.7f, 1));

        if (options.Pbr && tx.Normal != null) mat.WithNormal(Image(tx.Normal));
        if (options.Pbr && tx.GltfMaps() is var (mr, ao))
        {
            // Les facteurs à 1 laissent la texture décider
            mat.WithMetallicRoughness(Image(mr), 1, 1);
            if (ao != null) mat.WithOcclusion(Image(ao));
        }
        else
            mat.WithMetallicRoughness(0, 0.8f);

        if (part.Material == MaterialKind.Cutout) mat.WithAlpha(AlphaMode.MASK, 0.5f);
        else if (part.Material == MaterialKind.Transparent) mat.WithAlpha(AlphaMode.BLEND);
        cache[key] = mat;
        return mat;
    }

    static SharpGLTF.Memory.MemoryImage Image(DecodedTexture t) => new(t.Png);
}
