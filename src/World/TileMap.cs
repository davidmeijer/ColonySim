using System.Numerics;
using System.Runtime.InteropServices;
using Raylib_cs;
using ColonySim.Entities;

namespace ColonySim.World;

// A voxel world with two grid resolutions layered on top of each other:
//
// - The COARSE grid (Width x Depth, TileSize world units per cell) is what
//   pathfinding, walkability, tile occupancy, and digging all operate on —
//   an actor occupies exactly one coarse tile, same as it always has.
// - The FINE grid (Width*FineSubdivisions x Depth*FineSubdivisions,
//   VoxelSize world units per cell — one coarse tile's worth of fine
//   voxels is a 10x10 patch) is the real terrain data and what gets
//   rendered: small real voxel cubes, not a blended/smoothed surface, so
//   the ground has actual walls wherever height changes, but reads as
//   "smooth-ish" at normal viewing distance simply because the steps are
//   small. A coarse tile's height/material (SurfaceY, TopMaterial, ...) is
//   sampled from its centre fine column — a single representative value,
//   consistent with "one coarse tile is one pathfinding unit".
public class TileMap
{
  // Coarse pathfinding tile size, world units.
  public const int TileSize = 24;

  // Fine voxels per coarse tile, per axis — the "10x10x10" subdivision.
  public const int FineSubdivisions = 10;

  // The one fundamental voxel size: every axis (X, Y, Z) uses this.
  public const float VoxelSize = TileSize / (float)FineSubdivisions;

  // Coarse "old block" vertical levels — still the unit terrain generation
  // and the dig/deposit height cap think in, even though water now lives
  // on the fine grid too.
  public const int MaxHeight = 10;

  // Water sitting deeper than this (in voxels) on a tile is too deep to
  // wade through.
  const float MaxWadeableWaterVoxels = 6f;

  // How many voxels of Dirt sit under the exposed top voxel before it
  // turns to Rock — same real-world thickness as before (3 old blocks),
  // just re-expressed in the finer unit.
  const int DirtBandVoxels = 3 * FineSubdivisions;

  // An actor can climb at most half an old block in one step; anything
  // steeper has to be routed around (or, eventually, dug into a ramp).
  const int MaxStepUpLevels = FineSubdivisions / 2;

  // Fine columns per rendering chunk, per axis. Raylib's custom Mesh uses
  // 16-bit vertex indices, so the whole map can't be one mesh at this
  // resolution — chunking keeps every chunk's vertex count comfortably
  // under that limit (worst case per column is bounded: 1 top face + 4
  // sides x at most 3 material bands each = 13 quads = 52 vertices, so a
  // ChunkSize x ChunkSize chunk tops out at ChunkSize^2 x 52, nowhere near
  // 65535 for ChunkSize = 24).
  const int ChunkSize = 24;

  public int Width { get; }
  public int Depth { get; }

  public int WidthPx => Width * TileSize;
  public int DepthPx => Depth * TileSize;

  int FineWidth => Width * FineSubdivisions;
  int FineDepth => Depth * FineSubdivisions;

  // The real terrain data: height (in voxels) and the current topmost
  // voxel's material, per fine column. Materials below the top aren't
  // stored — they're always derivable from depth-below-the-current-top
  // (see MaterialAtVoxel), since digging only ever removes from the top.
  readonly int[,] _voxelHeight;
  readonly TileType[,] _voxelTop;

  // Sparse water depth, in (fractional) voxels, per fine column — sitting
  // directly on top of that column's ground (_voxelHeight). Continuous
  // rather than whole-voxel: the terrain is voxel/discrete because that's
  // what makes digging read as real excavation, but water is the opposite
  // case — it should settle to a genuinely level surface, and an integer
  // voxel count can only ever get within one voxel of level before there's
  // nothing left to move. No separate "is this cell open" concept is
  // needed the way the old coarse 3D water grid needed one: the fine
  // terrain has no caves or overhangs, it's a plain heightmap, so water
  // only ever has one place to be at a given (fx, fz) — right on the
  // surface — and the only way it moves is sideways, flowing toward
  // whichever neighbour's surface (ground + its water) is lower.
  readonly Dictionary<(int Fx, int Fz), float> _water = new();

  const float WaterTickInterval = 0.1f;
  const float WaterEpsilon = 0.01f;
  const float WaterFlowRate = 0.25f; // fraction of the surface-height difference that equalises per tick

  // Optional continuous spring: if a preset sets one (see
  // WorldGenerator.Result.HasWaterSource), this many voxels of water are
  // added at the source every tick, forever, instead of the map only ever
  // having whatever was seeded at generation time. The current lake preset
  // doesn't use this — a lake is filled once and left to settle — but the
  // mechanism stays here ready for a future preset (a river, say) that does.
  const float SourceFlowRate = 0.3f;
  float _waterTimer;
  bool _hasSource;
  int _sourceFx, _sourceFz;

  static readonly (int Dx, int Dz)[] HorizontalDirs = { (1, 0), (-1, 0), (0, 1), (0, -1) };

  // Trees, scattered in clusters. Tracked by coarse tile: only one per
  // tile, and a tiled tile blocks pathfinding just like rock or deep water
  // — you can't walk through a trunk.
  readonly List<Tree> _trees = new();
  readonly HashSet<(int X, int Z)> _treeTiles = new();

  // Bushes, sprinkled individually. Blocks pathfinding the same way a tree
  // does — an actor can't push through a shrub any more than a trunk.
  readonly List<Bush> _bushes = new();
  readonly HashSet<(int X, int Z)> _bushTiles = new();

  // Campfires, placed by the player through the build menu (see
  // Program.cs). Blocks pathfinding on its own tile — you can't stand in
  // the fire — the same way a tree or bush tile does.
  readonly List<Campfire> _campfires = new();
  readonly HashSet<(int X, int Z)> _campfireTiles = new();

  // Shared clock for flame-flicker animation (both the visible flame and
  // its point light read off this), and for how often scorched ground gets
  // refreshed — see UpdateVegetation.
  float _campfireTime;

  // Rendering: one Model per chunk of the fine grid. Tracked per chunk
  // (not one global flag) and rebuilt only where something actually
  // changed — water alone can dirty a chunk every tick while a stream is
  // actively spreading over new ground, so rebuilding all ~200 chunks on
  // every such change would visibly stutter.
  Shader? _terrainShader;
  readonly Model?[,] _chunkModels;
  readonly bool[,] _chunkDirty;

  int ChunksX => _chunkDirty.GetLength(0);
  int ChunksZ => _chunkDirty.GetLength(1);

  // How long (in seconds) exposed, dry Dirt takes to regrow Grass. Being
  // underwater — or getting freshly dug/washed — resets this. Buried
  // material never enters into it at all: MaterialAtVoxel only ever
  // returns Grass for the literal current top of a column, so grass can't
  // exist underground by construction.
  const float RegrowthSeconds = 20f;
  const float RegrowthTickInterval = 1f;
  readonly float[,] _dryTime;
  float _regrowthTimer;

  public TileMap(int width, int depth, int seed)
  {
    Width = width;
    Depth = depth;

    int chunksX = (FineWidth + ChunkSize - 1) / ChunkSize;
    int chunksZ = (FineDepth + ChunkSize - 1) / ChunkSize;
    _chunkModels = new Model?[chunksX, chunksZ];
    _chunkDirty = new bool[chunksX, chunksZ];
    MarkAllChunksDirty();

    var generated = WorldGenerator.GenerateRollingHillsWithLake(
      width, depth, FineSubdivisions, MaxHeight * FineSubdivisions, seed);

    _voxelHeight = generated.VoxelHeight;
    _voxelTop = generated.VoxelTop;
    _dryTime = new float[FineWidth, FineDepth];

    foreach (var (pos, depth2) in generated.Water)
      _water[pos] = depth2;

    foreach (var tree in generated.Trees)
    {
      _trees.Add(tree);
      _treeTiles.Add((tree.TileX, tree.TileZ));
    }

    foreach (var bush in generated.Bushes)
    {
      _bushes.Add(bush);
      _bushTiles.Add((bush.TileX, bush.TileZ));
    }

    if (generated.HasWaterSource)
    {
      _hasSource = true;
      _sourceFx = generated.SourceFx;
      _sourceFz = generated.SourceFz;
    }
  }

  void MarkAllChunksDirty()
  {
    for (int cx = 0; cx < ChunksX; cx++)
      for (int cz = 0; cz < ChunksZ; cz++)
        _chunkDirty[cx, cz] = true;
  }

  void MarkChunkDirtyAt(int fx, int fz)
  {
    int cx = fx / ChunkSize, cz = fz / ChunkSize;
    if (cx >= 0 && cz >= 0 && cx < ChunksX && cz < ChunksZ) _chunkDirty[cx, cz] = true;
  }

  // Dirt that's currently the exposed, sun-lit top of its column — and has
  // stayed dry and undisturbed for RegrowthSeconds straight — regrows
  // Grass. A full scan rather than tracking individual columns: simpler
  // and self-correcting (no risk of missing a spot that turned Dirt), and
  // at 120-ish thousand simple array reads once a second this is trivial
  // either way.
  public void UpdateVegetation(float dt)
  {
    _campfireTime += dt;

    _regrowthTimer += dt;
    while (_regrowthTimer >= RegrowthTickInterval)
    {
      _regrowthTimer -= RegrowthTickInterval;
      StepRegrowth(RegrowthTickInterval);

      // A campfire keeps its clearing scorched for as long as it burns —
      // without this, ordinary regrowth would eventually grass back over
      // ground right next to a fire that's still lit.
      foreach (var fire in _campfires)
      {
        var (fx, fz) = FineCenter(fire.TileX, fire.TileZ);
        ScorchAround(fx, fz, ScorchRadiusVoxels);
      }
    }
  }

  void StepRegrowth(float elapsed)
  {
    for (int fx = 0; fx < FineWidth; fx++)
    {
      for (int fz = 0; fz < FineDepth; fz++)
      {
        if (_voxelHeight[fx, fz] <= 0 || _voxelTop[fx, fz] != TileType.Dirt)
        {
          _dryTime[fx, fz] = 0f;
          continue;
        }

        if (_water.TryGetValue((fx, fz), out float w) && w > WaterEpsilon)
        {
          _dryTime[fx, fz] = 0f;
          continue;
        }

        float t = _dryTime[fx, fz] + elapsed;
        if (t >= RegrowthSeconds)
        {
          _voxelTop[fx, fz] = TileType.Grass;
          _dryTime[fx, fz] = 0f;
          MarkChunkDirtyAt(fx, fz);
        }
        else
        {
          _dryTime[fx, fz] = t;
        }
      }
    }
  }

  // --- Coarse-grid queries (pathfinding, walkability, digging) ---

  public bool InBounds(int x, int z) => x >= 0 && z >= 0 && x < Width && z < Depth;

  (int Fx, int Fz) FineCenter(int coarseX, int coarseZ) =>
    (coarseX * FineSubdivisions + FineSubdivisions / 2, coarseZ * FineSubdivisions + FineSubdivisions / 2);

  // A coarse tile's height in fine voxels, sampled at its centre fine
  // column — the single representative value pathfinding and walkability
  // treat that whole tile as having.
  public int HeightLevels(int x, int z)
  {
    if (!InBounds(x, z)) return 0;
    var (fx, fz) = FineCenter(x, z);
    return _voxelHeight[fx, fz];
  }

  // A coarse tile's height in whole "old blocks" — used only by water.
  public int SurfaceHeight(int x, int z) => HeightLevels(x, z) / FineSubdivisions;

  // World-space Y of the ground at a coarse tile's centre.
  public float SurfaceY(int x, int z) => HeightLevels(x, z) * VoxelSize;

  public TileType TopMaterial(int x, int z)
  {
    if (!InBounds(x, z)) return TileType.Air;
    var (fx, fz) = FineCenter(x, z);
    return _voxelHeight[fx, fz] > 0 ? _voxelTop[fx, fz] : TileType.Air;
  }

  // The material of a specific voxel within a fine column, derived from its
  // depth below that column's CURRENT top (not baked in at generation
  // time) — so once the top voxel is dug away, whatever's now exposed
  // above it reads correctly without needing to track history beyond
  // "is the very top voxel Grass or Dirt", which _voxelTop already does.
  TileType MaterialAtVoxel(int fx, int fz, int voxelY)
  {
    int depthFromTop = _voxelHeight[fx, fz] - 1 - voxelY;
    if (depthFromTop == 0) return _voxelTop[fx, fz];
    return depthFromTop <= DirtBandVoxels ? TileType.Dirt : TileType.Rock;
  }

  // A full tile-level's worth of voxels — the 10x10 fine footprint of one
  // coarse tile. Digging/depositing move exact voxel counts (matching
  // inventory 1:1) rather than uniformly stripping/adding a whole level's
  // height across every column regardless of how much was actually asked for.
  public const int VoxelsPerLevel = FineSubdivisions * FineSubdivisions;

  // The sum of all 100 fine columns' heights within a coarse tile — its
  // total remaining material, independent of exactly how that material is
  // currently distributed across those columns.
  public int TotalVoxels(int x, int z)
  {
    int fx0 = x * FineSubdivisions, fz0 = z * FineSubdivisions;
    int total = 0;
    for (int fz = fz0; fz < fz0 + FineSubdivisions; fz++)
      for (int fx = fx0; fx < fx0 + FineSubdivisions; fx++)
        total += _voxelHeight[fx, fz];
    return total;
  }

  // Redistributes a coarse tile's 100 fine columns to be as flat as
  // possible for a given total volume: a base height everywhere, with just
  // enough columns one voxel taller to account for the remainder. This is
  // what keeps a tile settling into a clean, mostly-flat plateau after a
  // dig or deposit changes its volume by exactly one voxel, instead of
  // leaving a single-column pinprick hole or spike.
  void Equalize(int x, int z, int totalVolume, TileType topMaterial)
  {
    int baseHeight = totalVolume / VoxelsPerLevel;
    int remainder = totalVolume % VoxelsPerLevel;

    int fx0 = x * FineSubdivisions, fz0 = z * FineSubdivisions;
    int filled = 0;
    for (int fz = fz0; fz < fz0 + FineSubdivisions; fz++)
    {
      for (int fx = fx0; fx < fx0 + FineSubdivisions; fx++)
      {
        int h = baseHeight + (filled < remainder ? 1 : 0);
        filled++;
        _voxelHeight[fx, fz] = h;
        if (h > 0) _voxelTop[fx, fz] = topMaterial;
        MarkChunkDirtyAt(fx, fz);
      }
    }
  }

  // How many voxels a single dig action removes at most, regardless of how
  // much room the digging actor has. Deliberately well below a full layer
  // (100): digging a flat tile should visibly grow a dug patch across
  // several actions, not change the whole tile's height in one click.
  const int DigRatePerAction = 10;

  // Digs into a Grass- or Dirt-topped coarse tile — Rock can't be dug.
  // Removes real, positional voxels: only from whichever fine columns are
  // currently at the tile's tallest height (its actual top layer), one
  // voxel each, up to DigRatePerAction (further capped by maxVoxels,
  // typically the digging actor's remaining inventory room). Deliberately
  // does NOT touch — let alone re-flatten — the rest of the tile, so a
  // plateau visibly erodes into an uneven, growing pit across repeated
  // digs instead of the whole tile's height dropping in one action; only
  // once every column in the current top layer has been worn down does the
  // tile's height actually go down, one layer at a time. Grass is only
  // ever a thin skin over dirt, so a successful dig always exposes (and
  // yields) Dirt underneath. Returns how many voxels were actually removed
  // (0 if the tile isn't currently Grass or Dirt topped).
  public int Dig(int x, int z, int maxVoxels)
  {
    if (!InBounds(x, z) || maxVoxels <= 0) return 0;
    var top = TopMaterial(x, z);
    if (top != TileType.Grass && top != TileType.Dirt) return 0;

    int cap = Math.Min(maxVoxels, DigRatePerAction);

    int fx0 = x * FineSubdivisions, fz0 = z * FineSubdivisions;
    int maxH = 0;
    for (int fz = fz0; fz < fz0 + FineSubdivisions; fz++)
      for (int fx = fx0; fx < fx0 + FineSubdivisions; fx++)
        maxH = Math.Max(maxH, _voxelHeight[fx, fz]);
    if (maxH <= 0) return 0;

    int dug = 0;
    for (int fz = fz0; fz < fz0 + FineSubdivisions && dug < cap; fz++)
    {
      for (int fx = fx0; fx < fx0 + FineSubdivisions && dug < cap; fx++)
      {
        if (_voxelHeight[fx, fz] != maxH) continue; // only the current top layer
        _voxelHeight[fx, fz] -= 1;
        _voxelTop[fx, fz] = TileType.Dirt;
        MarkChunkDirtyAt(fx, fz);
        dug++;
      }
    }

    return dug;
  }

  // How many voxels are needed to top off this tile's current partially-
  // filled level (the lowest level that isn't completely full) — i.e. how
  // much Deposit needs from an actor's inventory to raise this tile by one
  // whole unit.
  public int VoxelsNeededToRaise(int x, int z)
  {
    if (!InBounds(x, z)) return VoxelsPerLevel;
    int remainder = TotalVoxels(x, z) % VoxelsPerLevel;
    return remainder == 0 ? VoxelsPerLevel : VoxelsPerLevel - remainder;
  }

  // Tops off the tile's current partial level, raising its equalized
  // height by exactly one unit. Returns how many voxels that took (the
  // caller is responsible for actually removing that many items from the
  // depositing actor's inventory), or 0 if the tile's already at the
  // maximum height or out of bounds.
  public int Deposit(int x, int z)
  {
    if (!InBounds(x, z)) return 0;

    int needed = VoxelsNeededToRaise(x, z);
    int newTotal = TotalVoxels(x, z) + needed;
    if (newTotal / VoxelsPerLevel > MaxHeight * FineSubdivisions) return 0; // already at the height cap

    Equalize(x, z, newTotal, TileType.Dirt);
    return needed;
  }

  // --- Per-voxel dig/deposit (the task system's Dig/Deposit tools) ------
  //
  // The player picks an exact fine column to work on (a click, or one cell
  // of a drag-selected rectangle — see Program.cs's UpdateVoxelDrag), and a
  // builder always moves exactly one voxel there — no "search for the
  // tallest column" the whole-tile Dig above does, since the column is
  // already an explicit choice.

  public bool InBoundsFine(int fx, int fz) => fx >= 0 && fz >= 0 && fx < FineWidth && fz < FineDepth;

  // A single fine column's current height, in voxels — used to position the
  // hover/queued-task highlight at the right height.
  public int VoxelHeightAt(int fx, int fz) => InBoundsFine(fx, fz) ? _voxelHeight[fx, fz] : 0;

  // Whether a specific fine column can be dug: bounded, its parent coarse
  // tile has to be otherwise workable ground (no rock/tree/bush/campfire/
  // deep water — same rule IsWalkable already encodes), the column itself
  // has to have material left with a Grass/Dirt top (not bedrock), and it
  // can't be sitting under standing water.
  public bool CanDigVoxel(int fx, int fz)
  {
    if (!InBoundsFine(fx, fz)) return false;
    if (!IsWalkable(fx / FineSubdivisions, fz / FineSubdivisions)) return false;
    if (_voxelHeight[fx, fz] <= 0) return false;
    var top = _voxelTop[fx, fz];
    if (top != TileType.Grass && top != TileType.Dirt) return false;
    return !_water.TryGetValue((fx, fz), out float w) || w <= 0f;
  }

  // Whether a specific fine column has room to grow by one more voxel —
  // same parent-tile and water gating as CanDigVoxel, just checking height
  // room instead of material to remove.
  public bool CanDepositVoxel(int fx, int fz)
  {
    if (!InBoundsFine(fx, fz)) return false;
    if (!IsWalkable(fx / FineSubdivisions, fz / FineSubdivisions)) return false;
    if (_voxelHeight[fx, fz] >= MaxHeight * FineSubdivisions) return false;
    return !_water.TryGetValue((fx, fz), out float w) || w <= 0f;
  }

  // Removes exactly the top voxel of one fine column. Returns 1 on success
  // (matching Dig's "voxels actually removed" convention so callers can
  // hand it straight to Inventory.Add), 0 if it wasn't diggable.
  public int DigVoxel(int fx, int fz)
  {
    if (!CanDigVoxel(fx, fz)) return 0;
    _voxelHeight[fx, fz] -= 1;
    _voxelTop[fx, fz] = TileType.Dirt;
    MarkChunkDirtyAt(fx, fz);
    return 1;
  }

  // Adds exactly one voxel on top of one fine column.
  public bool DepositVoxel(int fx, int fz)
  {
    if (!CanDepositVoxel(fx, fz)) return false;
    _voxelHeight[fx, fz] += 1;
    _voxelTop[fx, fz] = TileType.Dirt;
    MarkChunkDirtyAt(fx, fz);
    return true;
  }

  // --- Campfires ---

  public IReadOnlyList<Campfire> Campfires => _campfires;

  // How far (in fine voxels) a campfire's heat scorches Grass to Dirt
  // around it — see ScorchAround.
  const float ScorchRadiusVoxels = 16f;

  // A spot has to be ordinary open ground: walkable, dry, and not already
  // occupied by another campfire.
  public bool CanPlaceCampfire(int x, int z) =>
    IsWalkable(x, z) && WaterDepth(x, z) <= 0f && !_campfireTiles.Contains((x, z));

  // Places a lit campfire on a coarse tile, immediately scorching the grass
  // around it (see ScorchAround) and blocking that tile from pathfinding —
  // an actor can't stand in the fire, the same way it can't stand in a
  // tree. Returns null without changing anything if the spot isn't valid;
  // callers should check CanPlaceCampfire first if they want to know why.
  public Campfire? PlaceCampfire(int x, int z)
  {
    if (!CanPlaceCampfire(x, z)) return null;

    var fire = new Campfire(x, z, Random.Shared.NextSingle() * MathF.Tau);
    _campfires.Add(fire);
    _campfireTiles.Add((x, z));

    var (fx, fz) = FineCenter(x, z);
    ScorchAround(fx, fz, ScorchRadiusVoxels);
    return fire;
  }

  // Tears down a campfire a DemolishCampfire task has finished working —
  // frees its tile back up for pathfinding. Leaves the scorched ground
  // alone; ordinary regrowth (see StepRegrowth) will grass it back over in
  // time same as any other patch of dry Dirt. Returns false if there was no
  // campfire on that tile to begin with (shouldn't happen in practice,
  // since WorkQueue prunes a demolish task the moment its target vanishes).
  public bool RemoveCampfire(int x, int z)
  {
    int index = _campfires.FindIndex(f => f.TileX == x && f.TileZ == z);
    if (index < 0) return false;

    _campfires.RemoveAt(index);
    _campfireTiles.Remove((x, z));
    return true;
  }

  // Turns every Grass column within radiusVoxels of (centerFx, centerFz)
  // to Dirt — scorched earth, the same "wash the grass to mud" move
  // StepWater's shoreline effect makes, just centred on a fire instead of
  // water. Already-Dirt/Rock columns are left alone, and this is safe to
  // call repeatedly on the same spot (re-applied periodically in
  // UpdateVegetation so regrowth can't creep back in under a burning fire).
  void ScorchAround(int centerFx, int centerFz, float radiusVoxels)
  {
    int r = (int)MathF.Ceiling(radiusVoxels);
    for (int dx = -r; dx <= r; dx++)
    {
      for (int dz = -r; dz <= r; dz++)
      {
        if (dx * dx + dz * dz > radiusVoxels * radiusVoxels) continue;
        int fx = centerFx + dx, fz = centerFz + dz;
        if (fx < 0 || fz < 0 || fx >= FineWidth || fz >= FineDepth) continue;
        if (_voxelTop[fx, fz] != TileType.Grass) continue;

        _voxelTop[fx, fz] = TileType.Dirt;
        MarkChunkDirtyAt(fx, fz);
      }
    }
  }

  // 0..1-ish flicker driven off the shared campfire clock, phase-offset per
  // fire so a cluster of them doesn't pulse in lockstep. Shared by the
  // point light colour (CampfireLights) and the visible flame's scale
  // (DrawCampfiresGlow) so the glow and the flame that's supposedly casting
  // it always move together.
  float Flicker(float phase) =>
    1f + 0.15f * MathF.Sin(_campfireTime * 8f + phase) + 0.08f * MathF.Sin(_campfireTime * 19f + phase * 2.3f);

  // A point light per campfire, positioned roughly at flame height — for
  // SunLight.SetPointLights to upload to the lighting shader each frame.
  // No shadow-casting for these (a full shadow map per campfire would be a
  // lot of machinery for a small cosmetic glow); at night, against the
  // sun's near-zero ambient, the glow alone reads clearly.
  static readonly Vector3 CampfireLightColor = new(1.6f, 0.85f, 0.35f);
  const float CampfireLightHeight = TileSize * 0.6f;

  public IEnumerable<(Vector3 Position, Vector3 Color)> CampfireLights()
  {
    foreach (var fire in _campfires)
    {
      float worldX = fire.TileX * TileSize + TileSize / 2f;
      float worldZ = fire.TileZ * TileSize + TileSize / 2f;
      float baseY = SmoothSurfaceY(worldX, worldZ);
      Vector3 pos = new(worldX, baseY + CampfireLightHeight, worldZ);
      yield return (pos, CampfireLightColor * Flicker(fire.FlickerPhase));
    }
  }

  // Whether an actor standing at (fromX, fromZ) can take one step onto the
  // adjacent tile (toX, toZ): the destination has to be walkable on its
  // own terms, and the climb can't be steeper than half an old block.
  // Stepping down is never limited — only climbing.
  public bool CanStep(int fromX, int fromZ, int toX, int toZ)
  {
    if (!IsWalkable(toX, toZ)) return false;
    return EdgeRise(fromX, fromZ, toX, toZ) <= MaxStepUpLevels;
  }

  // The height an actor actually has to climb crossing directly from one
  // coarse tile into the next. Deliberately NOT the two tiles' single
  // centre fine columns (one point out of the 100 in each tile) — on
  // rolling terrain that single sample is easily unrepresentative of the
  // tile as a whole, which is exactly what let a visually gradual ramp get
  // rejected as an unclimbable cliff. Instead this walks every fine-voxel
  // column pair straddling the shared border — the actual ground an actor's
  // feet cross when it takes the step — and takes the median rise across
  // it, so a genuinely gradual slope reads as gradual even when a column
  // or two right on the border is locally bumpy, while a real step/cliff
  // spanning most of the border still reads as one.
  int EdgeRise(int fromX, int fromZ, int toX, int toZ)
  {
    var rises = new List<int>(FineSubdivisions);
    foreach (var (fromFx, fromFz, toFx, toFz) in BorderColumnPairs(fromX, fromZ, toX, toZ))
      rises.Add(_voxelHeight[toFx, toFz] - _voxelHeight[fromFx, fromFz]);
    rises.Sort();
    return rises[rises.Count / 2];
  }

  // The FineSubdivisions fine-voxel column pairs directly facing each
  // other across the shared border of two orthogonally adjacent coarse
  // tiles — the last column of `from` against the first column of `to`.
  // Pathfinding is 4-directional only, so exactly one of dx/dz is nonzero.
  IEnumerable<(int FromFx, int FromFz, int ToFx, int ToFz)> BorderColumnPairs(int fromX, int fromZ, int toX, int toZ)
  {
    int dx = toX - fromX, dz = toZ - fromZ;
    int fromFx0 = fromX * FineSubdivisions, fromFz0 = fromZ * FineSubdivisions;
    int toFx0 = toX * FineSubdivisions, toFz0 = toZ * FineSubdivisions;

    if (dx != 0)
    {
      int fromFx = dx > 0 ? fromFx0 + FineSubdivisions - 1 : fromFx0;
      int toFx = dx > 0 ? toFx0 : toFx0 + FineSubdivisions - 1;
      for (int i = 0; i < FineSubdivisions; i++)
        yield return (fromFx, fromFz0 + i, toFx, fromFz0 + i);
    }
    else
    {
      int fromFz = dz > 0 ? fromFz0 + FineSubdivisions - 1 : fromFz0;
      int toFz = dz > 0 ? toFz0 : toFz0 + FineSubdivisions - 1;
      for (int i = 0; i < FineSubdivisions; i++)
        yield return (fromFx0 + i, fromFz, fromFx0 + i, toFz);
    }
  }

  // How many voxels of water are sitting on a coarse tile, sampled at its
  // centre fine column (same representative-value convention as
  // HeightLevels/TopMaterial).
  public float WaterDepth(int x, int z)
  {
    if (!InBounds(x, z)) return 0f;
    var (fx, fz) = FineCenter(x, z);
    return _water.TryGetValue((fx, fz), out float v) ? v : 0f;
  }

  // Bare rock faces block movement, so does water too deep to wade, and so
  // does a tree, a bush, or a lit campfire — you can't walk through a
  // trunk, a shrub, or a fire.
  public bool IsWalkable(int x, int z)
  {
    if (!InBounds(x, z)) return false;
    if (TopMaterial(x, z) == TileType.Rock) return false;
    if (WaterDepth(x, z) > MaxWadeableWaterVoxels) return false;
    if (_treeTiles.Contains((x, z))) return false;
    if (_bushTiles.Contains((x, z))) return false;
    if (_campfireTiles.Contains((x, z))) return false;
    return true;
  }

  // Find any walkable tile: used to place an actor at startup.
  public (int X, int Y) FirstWalkable()
  {
    for (int x = 0; x < Width; x++)
      for (int z = 0; z < Depth; z++)
        if (IsWalkable(x, z)) return (x, z);
    return (0, 0);
  }

  // Every walkable tile: used to scatter a starting roster of actors.
  public IEnumerable<(int X, int Y)> WalkableTiles()
  {
    for (int x = 0; x < Width; x++)
      for (int z = 0; z < Depth; z++)
        if (IsWalkable(x, z)) yield return (x, z);
  }

  // The full-column bounding box, used for mouse-ray tile picking.
  public BoundingBox ColumnBounds(int x, int z) => new(
    new Vector3(x * TileSize, 0, z * TileSize),
    new Vector3((x + 1) * TileSize, SurfaceY(x, z), (z + 1) * TileSize));

  // The actual ground height at an arbitrary world position — the true
  // fine-voxel height, not the coarse tile's representative value. This is
  // what actors stand on, so their feet always match the real rendered
  // surface (including immediately after a dig — nothing needs to catch up).
  public float SmoothSurfaceY(float worldX, float worldZ)
  {
    int fx = Math.Clamp((int)MathF.Floor(worldX / VoxelSize), 0, FineWidth - 1);
    int fz = Math.Clamp((int)MathF.Floor(worldZ / VoxelSize), 0, FineDepth - 1);
    return _voxelHeight[fx, fz] * VoxelSize;
  }

  // --- Water simulation (fine grid, continuous depth): if a preset set a
  // continuous spring (see SourceFlowRate), it keeps adding water at the
  // source every tick — the lake preset doesn't, so this is a no-op for it.
  // Every water-bearing column then checks its 4 neighbours and flows a
  // fraction (WaterFlowRate) of the surface-height difference toward any
  // neighbour whose own surface (ground + its water) is lower — repeated
  // ticks asymptotically level the surface out, genuinely flat rather than
  // stuck a voxel short the way whole-voxel spilling would be. A neighbour
  // off the edge of the map is treated as a bottomless drain (surface 0):
  // water that flows that way is simply discarded, so water reaching the
  // map boundary drains away instead of pooling there forever. There's no
  // separate "falling" phase the way the old coarse 3D water grid needed:
  // the fine terrain is a plain heightmap, so water only ever has one
  // place to sit at a given (fx, fz) — directly on the surface — and
  // flowing sideways is the only way it moves.

  public void UpdateWater(float dt)
  {
    _waterTimer += dt;
    while (_waterTimer >= WaterTickInterval)
    {
      _waterTimer -= WaterTickInterval;
      StepWater();
    }
  }

  // How far the muddying effect of standing/flowing water reaches beyond
  // the tiles it's actually sitting on — a shoreline, not just the
  // waterline itself.
  const int MudRadius = 5;

  void StepWater()
  {
    if (_hasSource)
    {
      var sourcePos = (_sourceFx, _sourceFz);
      _water.TryGetValue(sourcePos, out float current);
      _water[sourcePos] = current + SourceFlowRate;
    }

    if (_water.Count == 0) return;

    var next = new Dictionary<(int Fx, int Fz), float>(_water);

    foreach (var pos in _water.Keys)
    {
      var (fx, fz) = pos;
      if (!next.TryGetValue(pos, out float amount) || amount <= WaterEpsilon) continue;

      foreach (var (dx, dz) in HorizontalDirs)
      {
        if (amount <= WaterEpsilon) break;

        int nfx = fx + dx, nfz = fz + dz;
        bool offMap = nfx < 0 || nfz < 0 || nfx >= FineWidth || nfz >= FineDepth;

        float neighborWater = 0f;
        var npos = (nfx, nfz);
        if (!offMap) next.TryGetValue(npos, out neighborWater);

        float mySurface = _voxelHeight[fx, fz] + amount;
        float neighborSurface = offMap ? 0f : _voxelHeight[nfx, nfz] + neighborWater;
        float diff = mySurface - neighborSurface;
        if (diff <= WaterEpsilon) continue;

        float flow = MathF.Min(diff * WaterFlowRate, amount);
        amount -= flow;
        next[pos] = amount;
        if (!offMap) next[npos] = neighborWater + flow; // otherwise it just drains off the map
      }
    }

    _water.Clear();
    foreach (var (pos, amount) in next)
      if (amount > WaterEpsilon) _water[pos] = amount;

    // Standing or flowing water washes grass to mud — not just where it's
    // actually sitting, but in a radius around it, like a muddy shoreline.
    // Only iterates around the (typically modest) set of wet columns, not
    // the whole map, and skips columns that are already Dirt, so a settled
    // lake costs almost nothing here after its first tick.
    foreach (var (fx, fz) in _water.Keys)
    {
      for (int dx = -MudRadius; dx <= MudRadius; dx++)
      {
        for (int dz = -MudRadius; dz <= MudRadius; dz++)
        {
          if (dx * dx + dz * dz > MudRadius * MudRadius) continue;
          int nfx = fx + dx, nfz = fz + dz;
          if (nfx < 0 || nfz < 0 || nfx >= FineWidth || nfz >= FineDepth) continue;
          if (_voxelTop[nfx, nfz] != TileType.Grass) continue;

          _voxelTop[nfx, nfz] = TileType.Dirt;
          MarkChunkDirtyAt(nfx, nfz);
        }
      }
    }
  }

  // --- Drawing ---
  //
  // Real voxel cubes again (not a blended mesh): every fine column draws
  // its own top face, plus a wall face toward any neighbour that's shorter
  // — including "off the map", which is treated as height 0, so the map's
  // outer edge naturally gets a full wall down to the ground with no
  // separate skirt system needed. Small voxels (VoxelSize, a tenth of the
  // old block size) are what make this read as smooth-ish at normal
  // viewing distance without faking it via blending, and it's why a dug
  // tile shows a real stepped hole with visible walls instead of a
  // diluted dimple.

  static readonly Color OutlineColor = new(20, 20, 20, 90);

  // Wires this map's terrain chunks up to the lighting shader. DrawModel
  // doesn't respect BeginShaderMode (it always uses its own material's
  // shader), so this has to be assigned directly rather than just wrapping
  // DrawSolid() in BeginLit/EndLit like the cube-based actors and water are.
  public void SetTerrainShader(Shader shader)
  {
    _terrainShader = shader;
    MarkAllChunksDirty();
  }

  int FineHeightOrZero(int fx, int fz) =>
    fx >= 0 && fz >= 0 && fx < FineWidth && fz < FineDepth ? _voxelHeight[fx, fz] : 0;

  // Bands a fine column's exposed wall segment [fromVoxel, toVoxelExclusive)
  // by material, so a tall wall correctly shows (from the top down) a
  // sliver of Grass/Dirt, then a Dirt band, then Rock, instead of one
  // uniform colour.
  IEnumerable<(int Bottom, int Top, TileType Material)> WallBands(int fx, int fz, int fromVoxel, int toVoxelExclusive)
  {
    int y = fromVoxel;
    while (y < toVoxelExclusive)
    {
      var mat = MaterialAtVoxel(fx, fz, y);
      int start = y;
      while (y < toVoxelExclusive && MaterialAtVoxel(fx, fz, y) == mat) y++;
      yield return (start, y, mat);
    }
  }

  // Rebuilds only the chunks actually marked dirty since the last draw.
  void RebuildDirtyChunks()
  {
    for (int cz = 0; cz < ChunksZ; cz++)
    {
      for (int cx = 0; cx < ChunksX; cx++)
      {
        if (!_chunkDirty[cx, cz]) continue;

        int fx0 = cx * ChunkSize, fz0 = cz * ChunkSize;
        int fxCount = Math.Min(ChunkSize, FineWidth - fx0);
        int fzCount = Math.Min(ChunkSize, FineDepth - fz0);

        if (_chunkModels[cx, cz] is { } old) Raylib.UnloadModel(old);
        _chunkModels[cx, cz] = BuildChunkModel(fx0, fz0, fxCount, fzCount);
        _chunkDirty[cx, cz] = false;
      }
    }
  }

  // Builds one chunk's worth of voxel-cube geometry (top + exposed side
  // faces only) and uploads it as its own small Model. Raylib's Mesh is a
  // handful of raw unmanaged buffers (no managed-array wrapper for custom
  // meshes), so vertex/normal/colour/index data is accumulated in plain
  // Lists first, then copied into NativeMemory-allocated buffers at the
  // end; UnloadModel frees that same memory later since raylib's allocator
  // and .NET's NativeMemory both ultimately go through the platform's
  // malloc/free.
  unsafe Model? BuildChunkModel(int fx0, int fz0, int fxCount, int fzCount)
  {
    var verts = new List<Vector3>();
    var norms = new List<Vector3>();
    var cols = new List<Color>();
    var indices = new List<ushort>();

    // Winding has to actually match each face's stated normal, not just the
    // normal data itself — raylib backface-culls by default, so a triangle
    // wound the wrong way gets discarded from the very side it's meant to
    // be seen from (only visible from behind), regardless of what its
    // vertex normals say. (a, c, b) / (a, d, c) is the order that comes out
    // CCW as seen from the direction each of this method's callers' normal
    // actually points.
    void AddQuad(Vector3 a, Vector3 b, Vector3 c, Vector3 d, Color color, Vector3 normal)
    {
      ushort baseIndex = (ushort)verts.Count;
      verts.Add(a); verts.Add(b); verts.Add(c); verts.Add(d);
      for (int i = 0; i < 4; i++) { norms.Add(normal); cols.Add(color); }
      indices.Add(baseIndex); indices.Add((ushort)(baseIndex + 2)); indices.Add((ushort)(baseIndex + 1));
      indices.Add(baseIndex); indices.Add((ushort)(baseIndex + 3)); indices.Add((ushort)(baseIndex + 2));
    }

    for (int fz = fz0; fz < fz0 + fzCount; fz++)
    {
      for (int fx = fx0; fx < fx0 + fxCount; fx++)
      {
        int height = _voxelHeight[fx, fz];
        if (height <= 0) continue; // dug all the way down to nothing

        float x0 = fx * VoxelSize, x1 = (fx + 1) * VoxelSize;
        float z0 = fz * VoxelSize, z1 = (fz + 1) * VoxelSize;
        float topY = height * VoxelSize;

        AddQuad(
          new Vector3(x0, topY, z0), new Vector3(x1, topY, z0),
          new Vector3(x1, topY, z1), new Vector3(x0, topY, z1),
          ColorFor(_voxelTop[fx, fz]), new Vector3(0, 1, 0));

        int westH = FineHeightOrZero(fx - 1, fz);
        if (westH < height)
          foreach (var (bottom, top, mat) in WallBands(fx, fz, westH, height))
            AddQuad(
              new Vector3(x0, top * VoxelSize, z0), new Vector3(x0, top * VoxelSize, z1),
              new Vector3(x0, bottom * VoxelSize, z1), new Vector3(x0, bottom * VoxelSize, z0),
              ColorFor(mat), new Vector3(-1, 0, 0));

        int eastH = FineHeightOrZero(fx + 1, fz);
        if (eastH < height)
          foreach (var (bottom, top, mat) in WallBands(fx, fz, eastH, height))
            AddQuad(
              new Vector3(x1, top * VoxelSize, z1), new Vector3(x1, top * VoxelSize, z0),
              new Vector3(x1, bottom * VoxelSize, z0), new Vector3(x1, bottom * VoxelSize, z1),
              ColorFor(mat), new Vector3(1, 0, 0));

        int southH = FineHeightOrZero(fx, fz - 1);
        if (southH < height)
          foreach (var (bottom, top, mat) in WallBands(fx, fz, southH, height))
            AddQuad(
              new Vector3(x1, top * VoxelSize, z0), new Vector3(x0, top * VoxelSize, z0),
              new Vector3(x0, bottom * VoxelSize, z0), new Vector3(x1, bottom * VoxelSize, z0),
              ColorFor(mat), new Vector3(0, 0, -1));

        int northH = FineHeightOrZero(fx, fz + 1);
        if (northH < height)
          foreach (var (bottom, top, mat) in WallBands(fx, fz, northH, height))
            AddQuad(
              new Vector3(x0, top * VoxelSize, z1), new Vector3(x1, top * VoxelSize, z1),
              new Vector3(x1, bottom * VoxelSize, z1), new Vector3(x0, bottom * VoxelSize, z1),
              ColorFor(mat), new Vector3(0, 0, 1));
      }
    }

    if (verts.Count == 0) return null;

    int vertexCount = verts.Count;
    int triangleCount = indices.Count / 3;

    var mesh = new Mesh { VertexCount = vertexCount, TriangleCount = triangleCount };
    mesh.Vertices = (float*)NativeMemory.Alloc((nuint)(vertexCount * 3 * sizeof(float)));
    mesh.Normals = (float*)NativeMemory.Alloc((nuint)(vertexCount * 3 * sizeof(float)));
    mesh.Colors = (byte*)NativeMemory.Alloc((nuint)(vertexCount * 4));
    mesh.Indices = (ushort*)NativeMemory.Alloc((nuint)(indices.Count * sizeof(ushort)));

    for (int i = 0; i < vertexCount; i++)
    {
      mesh.Vertices[i * 3 + 0] = verts[i].X;
      mesh.Vertices[i * 3 + 1] = verts[i].Y;
      mesh.Vertices[i * 3 + 2] = verts[i].Z;

      mesh.Normals[i * 3 + 0] = norms[i].X;
      mesh.Normals[i * 3 + 1] = norms[i].Y;
      mesh.Normals[i * 3 + 2] = norms[i].Z;

      mesh.Colors[i * 4 + 0] = cols[i].R;
      mesh.Colors[i * 4 + 1] = cols[i].G;
      mesh.Colors[i * 4 + 2] = cols[i].B;
      mesh.Colors[i * 4 + 3] = 255;
    }
    for (int i = 0; i < indices.Count; i++) mesh.Indices[i] = indices[i];

    Raylib.UploadMesh(ref mesh, false);
    var model = Raylib.LoadModelFromMesh(mesh);
    if (_terrainShader is { } shader) model.Materials[0].Shader = shader;
    return model;
  }

  // The lit fill. Draw this inside a lighting shader's BeginMode/EndMode —
  // though for these chunk models specifically, what actually makes them
  // lit is the shader assigned in SetTerrainShader; BeginLit/EndLit only
  // matter here for staying consistent with the actors/water drawn
  // alongside them.
  public void DrawSolid()
  {
    RebuildDirtyChunks();
    foreach (var model in _chunkModels)
      if (model is { } m) Raylib.DrawModel(m, Vector3.Zero, 1f, Color.White);
  }

  // Trees are simple immediate-mode cube stacks, not batched chunks — a
  // few dozen trees at ~8 cubes each is nowhere near enough to need that.
  // A trunk of TrunkHeight segments, then a canopy of CanopyHeight segments
  // that tapers from wide at the bottom to narrow at the top, giving a
  // blocky pine-tree silhouette. DrawCube (unlike DrawModel) does respect
  // the currently active shader, so call this inside the same
  // BeginLit/EndLit block as the rest of the lit scene for consistent shading.
  const float TrunkWidth = TileSize * 0.4f;
  const float CanopyBaseWidth = TileSize * 1.8f;
  const float CanopyTopWidth = TileSize * 0.6f;

  public void DrawTrees()
  {
    foreach (var tree in _trees)
    {
      float worldX = tree.TileX * TileSize + TileSize / 2f;
      float worldZ = tree.TileZ * TileSize + TileSize / 2f;
      float baseY = SmoothSurfaceY(worldX, worldZ);

      for (int i = 0; i < tree.TrunkHeight; i++)
      {
        Vector3 center = new(worldX, baseY + (i + 0.5f) * TileSize, worldZ);
        Raylib.DrawCube(center, TrunkWidth, TileSize, TrunkWidth, Color.Brown);
      }

      float canopyBaseY = baseY + tree.TrunkHeight * TileSize;
      for (int j = 0; j < tree.CanopyHeight; j++)
      {
        float t = tree.CanopyHeight <= 1 ? 0f : j / (float)(tree.CanopyHeight - 1);
        float width = CanopyBaseWidth + (CanopyTopWidth - CanopyBaseWidth) * t;
        Vector3 center = new(worldX, canopyBaseY + (j + 0.5f) * TileSize, worldZ);
        Raylib.DrawCube(center, width, TileSize, width, Color.DarkGreen);
      }
    }
  }

  // Same immediate-mode approach as trees, just squatter — 1-2 tapering
  // layers with no trunk, sitting directly on the ground.
  const float BushBaseWidth = TileSize * 1.0f;
  const float BushLayerHeight = TileSize * 0.45f;
  static readonly Color[] BushPalette = { new(34, 120, 40, 255), new(48, 142, 56, 255), new(26, 98, 34, 255) };

  public void DrawBushes()
  {
    foreach (var bush in _bushes)
    {
      float worldX = bush.TileX * TileSize + TileSize / 2f;
      float worldZ = bush.TileZ * TileSize + TileSize / 2f;
      float baseY = SmoothSurfaceY(worldX, worldZ);

      float sizeMul = 0.85f + bush.SizeVariant * 0.15f; // 0.85 / 1.0 / 1.15
      Color color = BushPalette[bush.ColorVariant % BushPalette.Length];

      for (int i = 0; i < bush.Layers; i++)
      {
        float t = bush.Layers <= 1 ? 0f : i / (float)(bush.Layers - 1);
        float width = (BushBaseWidth - t * BushBaseWidth * 0.35f) * sizeMul;
        Vector3 center = new(worldX, baseY + (i + 0.5f) * BushLayerHeight, worldZ);
        Raylib.DrawCube(center, width, BushLayerHeight, width, color);
      }
    }
  }

  // The wood + stone ring: ordinary lit geometry, shaded and shadowed like
  // anything else, so call this inside BeginLit/EndLit (and include it in
  // the shadow pass) same as trees/bushes. The flame itself is drawn
  // separately by DrawCampfiresGlow, unlit — it's the light source, not
  // something the light should be shading.
  const float LogLength = TileSize * 0.85f;
  const float LogThickness = TileSize * 0.16f;
  const int StoneCount = 8;
  const float StoneRingRadius = TileSize * 0.42f;
  static readonly Color LogColor = new(92, 58, 34, 255);
  static readonly Color[] StoneColors = { new(120, 120, 118, 255), new(102, 102, 100, 255), new(134, 132, 128, 255) };

  public void DrawCampfiresLit()
  {
    foreach (var fire in _campfires)
    {
      float worldX = fire.TileX * TileSize + TileSize / 2f;
      float worldZ = fire.TileZ * TileSize + TileSize / 2f;
      float baseY = SmoothSurfaceY(worldX, worldZ);

      Vector3 logCenter = new(worldX, baseY + LogThickness / 2f, worldZ);
      Raylib.DrawCube(logCenter, LogLength, LogThickness, LogThickness * 0.9f, LogColor);
      Raylib.DrawCube(logCenter, LogThickness * 0.9f, LogThickness, LogLength, LogColor);

      for (int i = 0; i < StoneCount; i++)
      {
        float a = i / (float)StoneCount * MathF.Tau;
        Vector3 stonePos = new(
          worldX + MathF.Cos(a) * StoneRingRadius, baseY + TileSize * 0.06f, worldZ + MathF.Sin(a) * StoneRingRadius);
        Raylib.DrawCube(stonePos, TileSize * 0.14f, TileSize * 0.12f, TileSize * 0.14f, StoneColors[i % StoneColors.Length]);
      }
    }
  }

  // The flame itself: drawn unlit, like SunLight's sun/moon spheres, so it
  // stays vividly visible regardless of ambient darkness — it IS the light,
  // not something lit by it. The actual "lights up its surroundings" effect
  // comes from the real point light in CampfireLights, not from anything
  // drawn here; an earlier version of this also drew a big translucent
  // "glow" sphere around the flame for a soft-bloom look, but with
  // backface culling off a flat-alpha sphere shows both its near and far
  // surface at once, which reads as a hard-edged solid blob rather than a
  // glow — not worth it for a purely decorative touch, so it's gone.
  // Scale/height flicker off the same clock (see Flicker) that drives the
  // point light's colour, so the visible flame and the light it's
  // supposedly casting move in lockstep.
  static readonly Color FlameColorBase = new(255, 140, 40, 235);
  static readonly Color FlameColorTip = new(255, 220, 90, 210);

  public void DrawCampfiresGlow()
  {
    foreach (var fire in _campfires)
    {
      float worldX = fire.TileX * TileSize + TileSize / 2f;
      float worldZ = fire.TileZ * TileSize + TileSize / 2f;
      float baseY = SmoothSurfaceY(worldX, worldZ);
      float flicker = Flicker(fire.FlickerPhase);

      // DrawCylinder's position is the base (near) end, with radiusBottom
      // there and radiusTop at the far end — so the tapering-to-a-point
      // shape of a flame needs the SMALL radius as radiusTop, not radiusBottom.
      Vector3 basePos = new(worldX, baseY + LogThickness, worldZ);
      float h1 = TileSize * 0.55f * flicker;
      Raylib.DrawCylinder(basePos, TileSize * 0.02f, TileSize * 0.16f, h1, 8, FlameColorBase);

      Vector3 tipPos = new(worldX, basePos.Y + h1 * 0.35f, worldZ);
      float h2 = TileSize * 0.35f * flicker;
      Raylib.DrawCylinder(tipPos, TileSize * 0.01f, TileSize * 0.10f, h2, 8, FlameColorTip);
    }
  }

  // Faint lines along the coarse gameplay tile grid (not the fine voxel
  // grid) so tile boundaries stay legible, at a sane line count. Kept as
  // plain immediate-mode lines rather than DrawModelWires so it's
  // guaranteed to render as a flat, uniform colour regardless of the
  // terrain's own lighting shader — draw this with the default shader.
  public void DrawOutlines()
  {
    int vertsX = Width + 1, vertsZ = Depth + 1;
    Vector3 At(int vx, int vz)
    {
      int tx = Math.Clamp(vx, 0, Width - 1), tz = Math.Clamp(vz, 0, Depth - 1);
      return new Vector3(vx * TileSize, SurfaceY(tx, tz), vz * TileSize);
    }

    for (int vz = 0; vz < vertsZ; vz++)
      for (int vx = 0; vx < vertsX - 1; vx++)
        Raylib.DrawLine3D(At(vx, vz), At(vx + 1, vz), OutlineColor);

    for (int vx = 0; vx < vertsX; vx++)
      for (int vz = 0; vz < vertsZ - 1; vz++)
        Raylib.DrawLine3D(At(vx, vz), At(vx, vz + 1), OutlineColor);
  }

  // Frees the terrain chunk models' GPU/native resources. Call once at shutdown.
  public void Unload()
  {
    foreach (var model in _chunkModels)
      if (model is { } m) Raylib.UnloadModel(m);
  }

  public void DrawWater()
  {
    foreach (var ((fx, fz), depth) in _water)
    {
      if (depth <= 0f) continue;

      float bottomY = _voxelHeight[fx, fz] * VoxelSize;
      float cubeHeight = depth * VoxelSize;
      Vector3 center = new(
        fx * VoxelSize + VoxelSize / 2f,
        bottomY + cubeHeight / 2f,
        fz * VoxelSize + VoxelSize / 2f);

      Raylib.DrawCube(center, VoxelSize * 0.98f, cubeHeight, VoxelSize * 0.98f, new Color(64, 130, 220, 190));
    }
  }

  static Color ColorFor(TileType t) => t switch
  {
    TileType.Grass => Color.Green,
    TileType.Dirt  => Color.Brown,
    TileType.Rock  => Color.DarkGray,
    _              => Color.Magenta // should never happen
  };
}
