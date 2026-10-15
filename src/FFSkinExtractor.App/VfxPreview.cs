using System.Numerics;
using System.Text.Json.Nodes;
using FFSkinExtractor.Core;
using FFSkinExtractor.Core.Animation;

namespace FFSkinExtractor.App;

/// <summary>
/// Particules d'un effet d'emote pour l'aperçu : le GLB ne sait pas les décrire, la page les simule à partir
/// des réglages des systèmes de particules du jeu, accrochés aux nœuds « vfxNNN_… » du modèle.
/// </summary>
public static class VfxPreview
{
    public static JsonObject Json(EmoteVfx? vfx)
    {
        var emitters = new JsonArray();
        var textures = new JsonObject();
        if (vfx != null)
            foreach (var e in vfx.Emitters)
            {
                var m = e.Material;
                string? texture = m?.Texture is { } t ? t.Name : null;
                if (m?.Texture is { } tex && !textures.ContainsKey(tex.Name))
                    textures[tex.Name] = "data:image/png;base64," + Convert.ToBase64String(tex.Png);
                emitters.Add(new JsonObject
                {
                    ["node"] = GlbExporter.VfxNodeName(e.Node, vfx.Nodes[e.Node].Name),
                    ["system"] = e.System.DeepClone(),
                    ["renderer"] = e.Renderer.DeepClone(),
                    ["material"] = m == null ? null : new JsonObject
                    {
                        ["name"] = m.Name,
                        ["blend"] = m.Blend.ToString(),
                        ["color"] = Array(m.Color.X, m.Color.Y, m.Color.Z, m.Color.W),
                        ["texture"] = texture,
                        ["tiling"] = Array(m.Tiling.X, m.Tiling.Y),
                        ["offset"] = Array(m.Offset.X, m.Offset.Y),
                    },
                    ["mesh"] = e.Mesh == null ? null : Mesh(e.Mesh),
                });
            }
        return new JsonObject { ["emitters"] = emitters, ["textures"] = textures };
    }

    // Maillage d'une particule « Mesh », déjà passé dans les axes glTF (X inversé, triangles retournés)
    static JsonObject Mesh(UnityMesh mesh)
    {
        var indices = new JsonArray();
        foreach (var sub in mesh.Submeshes)
            for (int i = 0; i + 2 < sub.Length; i += 3) { indices.Add(sub[i]); indices.Add(sub[i + 2]); indices.Add(sub[i + 1]); }
        return new JsonObject
        {
            ["positions"] = new JsonArray(mesh.Positions.SelectMany(p => new[] { -p.X, p.Y, p.Z }).Select(Round).ToArray()),
            ["uvs"] = mesh.UVs == null ? null : new JsonArray(mesh.UVs.SelectMany(u => new[] { u.X, u.Y }).Select(Round).ToArray()),
            ["indices"] = indices,
        };
    }

    static JsonNode Round(float v) => JsonValue.Create(MathF.Round(float.IsFinite(v) ? v : 0, 5));
    static JsonArray Array(params float[] v) => new(v.Select(Round).ToArray());
}
