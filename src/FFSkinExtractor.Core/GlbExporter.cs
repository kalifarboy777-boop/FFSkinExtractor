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
        var rig = options.Skinned ? new RigBuilder(Skeleton.For(gender), parts.Select(p => p.Mesh).Distinct()) : null;
        var joints = rig?.Joints;

        foreach (var part in parts)
        {
            var mesh = new MeshBuilder<VertexPositionNormal, VertexTexture1, VertexJoints4>(part.SlotName);
            var prim = mesh.UsePrimitive(MaterialFor(part, materials, options));
            var src = part.Mesh;
            var tri = src.Submeshes[Math.Min(part.SubmeshIndex, src.Submeshes.Count - 1)];

            // Unity (main gauche) → glTF (main droite) : X inversé, donc ordre des sommets inversé
            var pos = src.Positions.Select(p => new Vector3(-p.X, p.Y, p.Z)).ToArray();
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
        return scene;
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
