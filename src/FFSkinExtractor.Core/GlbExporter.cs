using System.Numerics;
using SharpGLTF.Geometry;
using SharpGLTF.Geometry.VertexTypes;
using SharpGLTF.Materials;
using SharpGLTF.Scenes;

namespace FFSkinExtractor.Core;

using VERTEX = VertexBuilder<VertexPosition, VertexTexture1, VertexEmpty>;

/// <summary>Exporte une tenue en GLB (maillages + textures, statique pour l'instant).</summary>
public static class GlbExporter
{
    public static void Export(Outfit outfit, string path)
    {
        var scene = new SceneBuilder(outfit.Gender + "_" + outfit.Name);
        var materials = new Dictionary<string, MaterialBuilder>();

        foreach (var part in outfit.Parts)
        {
            var mat = MaterialFor(part, materials);
            var mesh = new MeshBuilder<VertexPosition, VertexTexture1>(part.SlotName);
            var prim = mesh.UsePrimitive(mat);
            var tri = part.Mesh.Submeshes[Math.Min(part.SubmeshIndex, part.Mesh.Submeshes.Count - 1)];
            for (int i = 0; i + 2 < tri.Length; i += 3)
            {
                // Unity (main gauche) → glTF (main droite) : X inversé, donc ordre des sommets inversé
                prim.AddTriangle(V(part, tri[i]), V(part, tri[i + 2]), V(part, tri[i + 1]));
            }
            scene.AddRigidMesh(mesh, Matrix4x4.Identity);
        }
        scene.ToGltf2().SaveGLB(path);
    }

    static VERTEX V(OutfitPart part, int i)
    {
        var p = part.Mesh.Positions[i];
        var uv = part.Mesh.UVs[i];
        return new VERTEX(new VertexPosition(-p.X, p.Y, p.Z), new VertexTexture1(new Vector2(uv.X, 1 - uv.Y)));
    }

    static MaterialBuilder MaterialFor(OutfitPart part, Dictionary<string, MaterialBuilder> cache)
    {
        string key = part.Texture?.Name ?? (part.IsSkin ? "peau" : "(sans texture)");
        if (cache.TryGetValue(key, out var mat)) return mat;
        mat = new MaterialBuilder(key).WithDoubleSide(true).WithMetallicRoughnessShader()
            .WithMetallicRoughness(0, 0.8f);
        if (part.Texture != null)
            mat.WithBaseColor(new SharpGLTF.Memory.MemoryImage(part.Texture.Png));
        else if (part.IsSkin)
            mat.WithBaseColor(new Vector4(0.87f, 0.66f, 0.52f, 1)); // couleur de peau, faute d'APK
        else
            mat.WithBaseColor(new Vector4(0.7f, 0.7f, 0.7f, 1));
        cache[key] = mat;
        return mat;
    }
}
