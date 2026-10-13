using FFSkinExtractor.Core.Uma;

namespace FFSkinExtractor.Core;

/// <summary>
/// Repère les morceaux de peau d'un maillage de vêtement. Un vêtement a un grand sous-maillage
/// (le tissu, qui couvre presque toute sa texture) et parfois un petit (cou, mains, taille).
/// Si les UV du petit retombent sur la zone déjà occupée par le tissu, il ne peut pas utiliser
/// la même texture : c'est de la peau, texturée avec le corps de base.
/// </summary>
public static class UvCoverage
{
    const int Res = 128;
    const double SmallCoverage = 0.15;
    const double MinOverlap = 0.4;

    public static HashSet<int> SkinSubmeshes(UmaMesh mesh)
    {
        var result = new HashSet<int>();
        if (mesh.Submeshes.Count < 2) return result;
        var masks = mesh.Submeshes.Select(t => Rasterize(mesh, t)).ToList();
        var counts = masks.Select(m => m.Count(b => b)).ToList();
        int big = counts.IndexOf(counts.Max());
        for (int s = 0; s < masks.Count; s++)
        {
            if (s == big || counts[s] == 0 || counts[s] > SmallCoverage * Res * Res) continue;
            int shared = 0;
            for (int i = 0; i < masks[s].Length; i++) if (masks[s][i] && masks[big][i]) shared++;
            if (shared >= MinOverlap * counts[s]) result.Add(s);
        }
        return result;
    }

    static bool[] Rasterize(UmaMesh mesh, int[] tri)
    {
        var mask = new bool[Res * Res];
        for (int t = 0; t + 2 < tri.Length; t += 3)
        {
            var a = mesh.UVs[tri[t]] * Res; var b = mesh.UVs[tri[t + 1]] * Res; var c = mesh.UVs[tri[t + 2]] * Res;
            int x0 = Math.Max(0, (int)MathF.Floor(MathF.Min(a.X, MathF.Min(b.X, c.X))));
            int x1 = Math.Min(Res - 1, (int)MathF.Ceiling(MathF.Max(a.X, MathF.Max(b.X, c.X))));
            int y0 = Math.Max(0, (int)MathF.Floor(MathF.Min(a.Y, MathF.Min(b.Y, c.Y))));
            int y1 = Math.Min(Res - 1, (int)MathF.Ceiling(MathF.Max(a.Y, MathF.Max(b.Y, c.Y))));
            float area = Edge(a, b, c);
            if (MathF.Abs(area) < 1e-6f) continue;
            for (int y = y0; y <= y1; y++)
                for (int x = x0; x <= x1; x++)
                {
                    var p = new System.Numerics.Vector2(x + 0.5f, y + 0.5f);
                    float w0 = Edge(b, c, p) / area, w1 = Edge(c, a, p) / area, w2 = Edge(a, b, p) / area;
                    if (w0 >= 0 && w1 >= 0 && w2 >= 0) mask[y * Res + x] = true;
                }
        }
        return mask;
    }

    static float Edge(System.Numerics.Vector2 a, System.Numerics.Vector2 b, System.Numerics.Vector2 p) =>
        (b.X - a.X) * (p.Y - a.Y) - (b.Y - a.Y) * (p.X - a.X);
}
