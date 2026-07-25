using System.Numerics;

namespace ColonySim.World;

// Produces the raw data a TileMap needs to populate itself, completely
// decoupled from TileMap's own rendering/gameplay machinery — adding a new
// preset later means adding a new method here, not touching TileMap. Only
// one preset exists so far: rolling hills with a single still lake.
public static class WorldGenerator
{
  public class Result
  {
    public required int[,] VoxelHeight { get; init; }
    public required TileType[,] VoxelTop { get; init; }
    public required List<Tree> Trees { get; init; }
    public required List<Bush> Bushes { get; init; }
    public required Dictionary<(int Fx, int Fz), float> Water { get; init; }

    // Permanent water sources, by coarse tile. The map starts with one on
    // high ground so there's a river running somewhere from the first
    // frame — water that's already moving is the clearest possible
    // demonstration that terrain can be dug to redirect it.
    public required List<(int X, int Z)> Springs { get; init; }
  }

  public static Result GenerateRollingHillsWithLake(
    int width, int depth, int fineSubdivisions, int maxHeightVoxels, int seed, bool generateSpring = false)
  {
    int fineWidth = width * fineSubdivisions;
    int fineDepth = depth * fineSubdivisions;
    var rng = new Random(seed);

    var height = new int[fineWidth, fineDepth];
    var top = new TileType[fineWidth, fineDepth];
    GenerateHeightmap(height, top, fineWidth, fineDepth, maxHeightVoxels, fineSubdivisions, seed);

    var water = CarveLake(height, top, fineWidth, fineDepth, fineSubdivisions, rng);

    // var springs = new List<(int X, int Z)> { HighestTile(height, width, depth, fineSubdivisions) };
    var springs = new List<(int X, int Z)>();
    if (generateSpring)
      springs.Add(HighestTile(height, width, depth, fineSubdivisions));

    var trees = ScatterTrees(width, depth, fineSubdivisions, top, rng);
    var treeTiles = trees.Select(t => (t.TileX, t.TileZ)).ToHashSet();
    var bushes = ScatterBushes(width, depth, fineSubdivisions, top, treeTiles, rng);

    // A spring on a tile a tree or bush also landed on would be hidden
    // under it, so those lose — the spring was placed first and is the
    // more important of the two.
    trees.RemoveAll(t => springs.Contains((t.TileX, t.TileZ)));
    bushes.RemoveAll(b => springs.Contains((b.TileX, b.TileZ)));

    return new Result
    {
      VoxelHeight = height,
      VoxelTop = top,
      Trees = trees,
      Bushes = bushes,
      Water = water,
      Springs = springs,
    };
  }

  // How far in from the rim the starting spring has to sit, as a fraction
  // of the map. The highest ground on a noise heightmap is as likely as
  // not to be in a corner, and a spring there just pours over the edge and
  // vanishes — the map drains at its boundary. Holding it inland gives its
  // water somewhere to actually run.
  const float SpringInlandMargin = 0.25f;

  // The highest coarse tile in the inland part of the map, sampled at each
  // tile's centre fine column the same way TileMap does. Putting the
  // starting spring on high ground gives its water the longest run
  // downhill, so the river it forms crosses a good part of the map rather
  // than pooling where it lands.
  static (int X, int Z) HighestTile(int[,] height, int width, int depth, int fineSubdivisions)
  {
    int marginX = (int)(width * SpringInlandMargin);
    int marginZ = (int)(depth * SpringInlandMargin);

    (int X, int Z) best = (width / 2, depth / 2);
    int bestHeight = -1;

    for (int x = marginX; x < width - marginX; x++)
    {
      for (int z = marginZ; z < depth - marginZ; z++)
      {
        int fx = x * fineSubdivisions + fineSubdivisions / 2;
        int fz = z * fineSubdivisions + fineSubdivisions / 2;
        if (height[fx, fz] <= bestHeight) continue;

        bestHeight = height[fx, fz];
        best = (x, z);
      }
    }

    return best;
  }

  // Rolling hills via a few octaves of value noise, sampled directly on the
  // fine grid so neighbouring fine columns naturally vary a little — what
  // makes small rendered voxels read as smooth-ish terrain instead of a
  // stack of identical steps.
  static void GenerateHeightmap(int[,] height, TileType[,] top, int fineWidth, int fineDepth,
    int maxHeightVoxels, int fineSubdivisions, int seed)
  {
    const float NoiseScale = 0.008f; // 10x finer than a coarse-grid 0.08f would be, since sampling is 10x denser
    int minHeightVoxels = 2 * fineSubdivisions;
    int maxSurfaceVoxels = maxHeightVoxels - 3 * fineSubdivisions;

    for (int fx = 0; fx < fineWidth; fx++)
    {
      for (int fz = 0; fz < fineDepth; fz++)
      {
        float n = FractalNoise(fx * NoiseScale, fz * NoiseScale, seed);
        int h = minHeightVoxels + (int)MathF.Round(n * (maxSurfaceVoxels - minHeightVoxels));
        height[fx, fz] = Math.Clamp(h, 1, maxHeightVoxels);
        top[fx, fz] = TileType.Grass;
      }
    }
  }

  // Carves a shallow bowl into the fine terrain and fills the bottom of it
  // with water up to the rim, once — no ongoing spring — so there's a
  // still pond to path around and watch settle.
  static Dictionary<(int, int), float> CarveLake(
    int[,] height, TileType[,] top, int fineWidth, int fineDepth, int fineSubdivisions, Random rng)
  {
    var water = new Dictionary<(int, int), float>();

    int fineCx = rng.Next(fineWidth);
    int fineCz = rng.Next(fineDepth);
    int fineRadius = (4 + rng.Next(3)) * fineSubdivisions;

    int originalHeight = height[fineCx, fineCz];
    int waterLevel = Math.Max(1, originalHeight - fineSubdivisions);

    for (int fx = fineCx - fineRadius; fx <= fineCx + fineRadius; fx++)
    {
      for (int fz = fineCz - fineRadius; fz <= fineCz + fineRadius; fz++)
      {
        if (fx < 0 || fz < 0 || fx >= fineWidth || fz >= fineDepth) continue;
        float dist = Vector2.Distance(new Vector2(fx, fz), new Vector2(fineCx, fineCz));
        if (dist > fineRadius) continue;

        float bowl = (fineRadius - dist) / fineRadius; // 0 at the rim, 1 at the centre
        int carved = Math.Max(1, height[fx, fz] - (int)MathF.Round(bowl * 4 * fineSubdivisions));
        height[fx, fz] = carved;
        top[fx, fz] = TileType.Dirt; // muddy lake bed, no grass

        if (carved < waterLevel)
          water[(fx, fz)] = waterLevel - carved;
      }
    }

    return water;
  }

  // Scatters big pine trees in a handful of clusters rather than
  // uniformly, so there's forest rather than an even sprinkle. Only roots
  // on Grass — never Dirt, a lake tile (already Dirt by this point), or a
  // tile another tree already occupies.
  static List<Tree> ScatterTrees(int width, int depth, int fineSubdivisions, TileType[,] top, Random rng)
  {
    const int ClusterCount = 8;
    const int TreesPerCluster = 10;
    const int ClusterRadius = 5;

    var trees = new List<Tree>();
    var occupied = new HashSet<(int, int)>();

    for (int c = 0; c < ClusterCount; c++)
    {
      int centerX = rng.Next(width);
      int centerZ = rng.Next(depth);

      for (int t = 0; t < TreesPerCluster; t++)
      {
        int tx = centerX + rng.Next(-ClusterRadius, ClusterRadius + 1);
        int tz = centerZ + rng.Next(-ClusterRadius, ClusterRadius + 1);
        if (tx < 0 || tz < 0 || tx >= width || tz >= depth) continue;
        if (occupied.Contains((tx, tz))) continue;

        int fx = tx * fineSubdivisions + fineSubdivisions / 2;
        int fz = tz * fineSubdivisions + fineSubdivisions / 2;
        if (top[fx, fz] != TileType.Grass) continue;

        int trunkHeight = 3 + rng.Next(3);  // 3-5
        int canopyHeight = 3 + rng.Next(2); // 3-4
        trees.Add(new Tree(tx, tz, trunkHeight, canopyHeight));
        occupied.Add((tx, tz));
      }
    }

    return trees;
  }

  // Scatters bushes individually across the map (not in tight clusters
  // like trees — a light, even sprinkle of undergrowth reads better than
  // more forest blobs) — skipping tree tiles, since a bush would just be
  // hidden under a tree's canopy there.
  static List<Bush> ScatterBushes(
    int width, int depth, int fineSubdivisions, TileType[,] top, HashSet<(int, int)> treeTiles, Random rng)
  {
    int bushCount = (width * depth) / 20;

    var bushes = new List<Bush>();
    var occupied = new HashSet<(int, int)>();

    for (int i = 0; i < bushCount; i++)
    {
      int tx = rng.Next(width);
      int tz = rng.Next(depth);
      if (treeTiles.Contains((tx, tz)) || occupied.Contains((tx, tz))) continue;

      int fx = tx * fineSubdivisions + fineSubdivisions / 2;
      int fz = tz * fineSubdivisions + fineSubdivisions / 2;
      if (top[fx, fz] != TileType.Grass) continue;

      int layers = 1 + rng.Next(2);       // 1-2
      int sizeVariant = rng.Next(3);      // 0-2
      int colorVariant = rng.Next(3);     // 0-2
      bushes.Add(new Bush(tx, tz, layers, sizeVariant, colorVariant));
      occupied.Add((tx, tz));
    }

    return bushes;
  }

  // --- Simple value noise: hashes lattice points, then interpolates. ---

  static float Hash(int x, int y, int seed)
  {
    unchecked
    {
      int h = seed;
      h ^= x * 374761393;
      h ^= y * 668265263;
      h = (h ^ (h >> 13)) * 1274126177;
      h ^= h >> 16;
      return (h & 0x7fffffff) / (float)int.MaxValue;
    }
  }

  static float SmoothNoise(float x, float y, int seed)
  {
    int x0 = (int)MathF.Floor(x), y0 = (int)MathF.Floor(y);
    float tx = x - x0, ty = y - y0;

    float v00 = Hash(x0, y0, seed);
    float v10 = Hash(x0 + 1, y0, seed);
    float v01 = Hash(x0, y0 + 1, seed);
    float v11 = Hash(x0 + 1, y0 + 1, seed);

    float sx = tx * tx * (3f - 2f * tx);
    float sy = ty * ty * (3f - 2f * ty);

    float a = v00 + (v10 - v00) * sx;
    float b = v01 + (v11 - v01) * sx;
    return a + (b - a) * sy;
  }

  // Sum of a few octaves, so hills have both broad shape and small detail.
  static float FractalNoise(float x, float y, int seed)
  {
    float total = 0f, amplitude = 1f, frequency = 1f, max = 0f;
    for (int i = 0; i < 4; i++)
    {
      total += SmoothNoise(x * frequency, y * frequency, seed + i * 101) * amplitude;
      max += amplitude;
      amplitude *= 0.5f;
      frequency *= 2f;
    }
    return total / max;
  }
}
