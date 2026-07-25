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

  // Water depth, in (fractional) voxels, per fine column — sitting
  // directly on top of that column's ground (_voxelHeight). Continuous
  // rather than whole-voxel: the terrain is voxel/discrete because that's
  // what makes digging read as real excavation, but water is the opposite
  // case — it should settle to a genuinely level surface, and an integer
  // voxel count can only ever get within one voxel of level before there's
  // nothing left to move. No separate "is this cell open" concept is
  // needed the way a 3D water grid would need one: the fine terrain has no
  // caves or overhangs, it's a plain heightmap, so water only ever has one
  // place to be at a given (fx, fz) — right on the surface. The solver
  // itself lives in WaterSim; it's handed _voxelHeight by reference, so
  // digging a channel is visible to it on the very next tick.
  readonly WaterSim _waterSim;
  float _waterTimer;

  // Springs: player-placed, permanent water sources. Each one pushes
  // SpringFlowRate voxels of water per tick into a small disc around its
  // tile centre — a disc rather than the single centre column so the
  // outflow reads as a welling spring instead of a one-voxel jet.
  //
  // The rate is the main dial on how the map plays. A spring produces
  // water forever and only the map's rim takes it away, so anything that
  // outruns the drainage will eventually flood inland basins; the question
  // is how long "eventually" is. At 3 a spring on the starting map's high
  // ground runs a clear river down into the lake and covers about a tenth
  // of the map after five minutes, which is fast enough to watch happen
  // and slow enough that capping it stays a real decision. Tripling it
  // floods a fifth of the map in the same time.
  const float SpringFlowRate = 3f;
  const int SpringRadiusVoxels = 3;
  readonly List<Spring> _springs = new();
  readonly HashSet<(int X, int Z)> _springTiles = new();

  // The disc a spring feeds, as offsets from its centre column. Precomputed
  // once since every spring uses the same shape every tick.
  static readonly (int Dx, int Dz)[] SpringDisc =
    (from dx in Enumerable.Range(-SpringRadiusVoxels, SpringRadiusVoxels * 2 + 1)
     from dz in Enumerable.Range(-SpringRadiusVoxels, SpringRadiusVoxels * 2 + 1)
     where dx * dx + dz * dz <= SpringRadiusVoxels * SpringRadiusVoxels
     select (dx, dz)).ToArray();

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
  // changed — an advancing stream muddies fresh ground every tick, so
  // rebuilding all ~200 chunks on every such change would visibly stutter.
  Shader? _terrainShader;
  readonly Model?[,] _chunkModels;
  readonly bool[,] _chunkDirty;

  // Water gets the same treatment on its own set of chunks, tracked
  // separately because the two go stale at completely different rates:
  // terrain only changes when someone digs, whereas flowing water changes
  // every tick. Drawing water as one cube per wet column (as it used to
  // be) meant a lake pushed hundreds of thousands of vertices through the
  // CPU every single frame; as chunked meshes, moving water costs a
  // rebuild only where it's actually moving, and water that has settled
  // costs nothing at all beyond one DrawModel per chunk.
  readonly Model?[,] _waterChunkModels;

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
    _waterChunkModels = new Model?[chunksX, chunksZ];
    _chunkDirty = new bool[chunksX, chunksZ];
    MarkAllChunksDirty();

    var generated = WorldGenerator.GenerateRollingHillsWithLake(
      width, depth, FineSubdivisions, MaxHeight * FineSubdivisions, seed);

    _voxelHeight = generated.VoxelHeight;
    _voxelTop = generated.VoxelTop;
    _dryTime = new float[FineWidth, FineDepth];

    _waterSim = new WaterSim(FineWidth, FineDepth, _voxelHeight, ChunkSize);
    foreach (var (pos, waterDepth) in generated.Water)
      _waterSim.AddWater(pos.Fx, pos.Fz, waterDepth);

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

    foreach (var (springX, springZ) in generated.Springs)
    {
      _springs.Add(new Spring(springX, springZ));
      _springTiles.Add((springX, springZ));
    }
  }

  void MarkAllChunksDirty()
  {
    for (int cx = 0; cx < ChunksX; cx++)
      for (int cz = 0; cz < ChunksZ; cz++)
        _chunkDirty[cx, cz] = true;

    // Null during construction: MarkAllChunksDirty runs once before the
    // sim exists, and the sim starts with nothing built anyway.
    _waterSim?.MarkAllChunksDirty();
  }

  // Dirties a fine column's terrain chunk — and its water chunk with it,
  // since the water surface is drawn at ground + depth, so changing the
  // ground under standing water moves the water too.
  void MarkChunkDirtyAt(int fx, int fz)
  {
    int cx = fx / ChunkSize, cz = fz / ChunkSize;
    if (cx >= 0 && cz >= 0 && cx < ChunksX && cz < ChunksZ) _chunkDirty[cx, cz] = true;
    _waterSim.MarkChunkDirtyAt(fx, fz);
  }

  // For edits that change a column's HEIGHT, as opposed to just the
  // material on top of it. On top of dirtying the mesh, this wakes the
  // water sim up at that column: the solver only walks the region water is
  // currently in, so a channel dug ahead of the waterline has to announce
  // itself or it wouldn't be looked at until water happened to spread
  // there on its own. Material-only edits (regrowth, scorching, shoreline
  // mud) deliberately don't call this — they'd drag the active region out
  // across the whole map for no reason.
  void MarkTerrainChangedAt(int fx, int fz)
  {
    MarkChunkDirtyAt(fx, fz);
    _waterSim.Touch(fx, fz);
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

        if (_waterSim.DepthAt(fx, fz) > 0f)
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
        MarkTerrainChangedAt(fx, fz);
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
        MarkTerrainChangedAt(fx, fz);
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
    return _waterSim.DepthAt(fx, fz) <= 0f;
  }

  // Whether a specific fine column has room to grow by one more voxel —
  // same parent-tile and water gating as CanDigVoxel, just checking height
  // room instead of material to remove.
  public bool CanDepositVoxel(int fx, int fz)
  {
    if (!InBoundsFine(fx, fz)) return false;
    if (!IsWalkable(fx / FineSubdivisions, fz / FineSubdivisions)) return false;
    if (_voxelHeight[fx, fz] >= MaxHeight * FineSubdivisions) return false;
    return _waterSim.DepthAt(fx, fz) <= 0f;
  }

  // Removes exactly the top voxel of one fine column. Returns 1 on success
  // (matching Dig's "voxels actually removed" convention so callers can
  // hand it straight to Inventory.Add), 0 if it wasn't diggable.
  public int DigVoxel(int fx, int fz)
  {
    if (!CanDigVoxel(fx, fz)) return 0;
    _voxelHeight[fx, fz] -= 1;
    _voxelTop[fx, fz] = TileType.Dirt;
    MarkTerrainChangedAt(fx, fz);
    return 1;
  }

  // Adds exactly one voxel on top of one fine column.
  public bool DepositVoxel(int fx, int fz)
  {
    if (!CanDepositVoxel(fx, fz)) return false;
    _voxelHeight[fx, fz] += 1;
    _voxelTop[fx, fz] = TileType.Dirt;
    MarkTerrainChangedAt(fx, fz);
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
    return _waterSim.DepthAt(fx, fz);
  }

  // A single fine column's water depth — what the water mesh builder and
  // the debug readout want, as opposed to WaterDepth's coarse-tile sample.
  public float WaterDepthFine(int fx, int fz) => _waterSim.DepthAt(fx, fz);

  // Total water on the map, in voxel-volumes — surfaced in the HUD so it's
  // obvious at a glance whether springs are outpacing what drains off the
  // rim, or whether a basin is still filling.
  public float TotalWaterVolume() => _waterSim.TotalVolume();

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

  // --- Water ------------------------------------------------------------
  //
  // The solver itself is WaterSim; this is just the driver. Each tick the
  // springs push their water in, the sim steps, and any column that went
  // from dry to wet muddies the grass around it.

  public void UpdateWater(float dt)
  {
    _waterTimer += dt;
    while (_waterTimer >= WaterSim.TickInterval)
    {
      _waterTimer -= WaterSim.TickInterval;

      foreach (var spring in _springs)
      {
        var (cfx, cfz) = FineCenter(spring.TileX, spring.TileZ);
        float perColumn = SpringFlowRate / SpringDisc.Length;
        foreach (var (dx, dz) in SpringDisc)
          _waterSim.AddWater(cfx + dx, cfz + dz, perColumn);
      }

      _waterSim.Step();
      MudNewShorelines();
    }
  }

  // How far the muddying effect of standing/flowing water reaches beyond
  // the columns it's actually sitting on — a shoreline, not just the
  // waterline itself.
  const int MudRadius = 5;

  // Water washes grass to mud in a radius around it. Driven off the sim's
  // newly-wet list rather than off every wet column: re-walking an 11x11
  // neighbourhood around every column of a large lake, every tick, was
  // close to a million iterations a tick for something that has nothing
  // left to do after the lake's first tick. A column only enters this list
  // the tick it actually becomes wet, so a settled lake costs nothing and
  // an advancing stream costs only its own front.
  void MudNewShorelines()
  {
    foreach (var (fx, fz) in _waterSim.NewlyWet)
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

  // --- Springs ------------------------------------------------------------

  public IReadOnlyList<Spring> Springs => _springs;

  // Same siting rule as a campfire — ordinary open ground, nothing already
  // on it — except that a spring is allowed on wet ground. Placing one in
  // a pond to raise its level is a perfectly reasonable thing to want, and
  // unlike a fire there is nothing about a spring that water ruins.
  public bool CanPlaceSpring(int x, int z) =>
    InBounds(x, z) &&
    TopMaterial(x, z) != TileType.Rock &&
    !_treeTiles.Contains((x, z)) &&
    !_bushTiles.Contains((x, z)) &&
    !_campfireTiles.Contains((x, z)) &&
    !_springTiles.Contains((x, z));

  public Spring? PlaceSpring(int x, int z)
  {
    if (!CanPlaceSpring(x, z)) return null;

    var spring = new Spring(x, z);
    _springs.Add(spring);
    _springTiles.Add((x, z));

    // Wake the sim at the spring's own column, so its first tick of water
    // is simulated even if it was placed nowhere near any existing water.
    var (fx, fz) = FineCenter(x, z);
    _waterSim.Touch(fx, fz);
    return spring;
  }

  // Caps a spring. The water it already produced is left exactly where it
  // is: it drains off the map rim if it can reach one, and otherwise just
  // sits there like any other pond. That's the point of being able to cap
  // one — it stops the supply, it doesn't undo the flood.
  public bool RemoveSpring(int x, int z)
  {
    int index = _springs.FindIndex(s => s.TileX == x && s.TileZ == z);
    if (index < 0) return false;

    _springs.RemoveAt(index);
    _springTiles.Remove((x, z));
    return true;
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
    foreach (var model in _waterChunkModels)
      if (model is { } m) Raylib.UnloadModel(m);
  }

  // --- Water rendering ---
  //
  // Same chunked-mesh approach as the terrain, for the same reason, but
  // with two differences that come from water being a fluid rather than a
  // solid: a column's top face sits at ground + depth (so it moves as the
  // water does), and side faces are only drawn down to whatever the
  // NEIGHBOUR's surface is, not down to the ground. That second part is
  // what stops a lake being drawn as thousands of individual submerged
  // cube walls — inside a body of water every neighbour is at the same
  // surface height, so no side faces are generated at all and only the
  // rim of the water gets walls.

  // Water thinner than this isn't drawn: a film a thousandth of a voxel
  // deep is invisible anyway, and its top face would z-fight with the
  // ground directly underneath it.
  const float MinRenderDepth = 0.05f;

  // Shallow water is nearly clear and shows the mud through it; deep water
  // is darker and hides it. Depth is measured against DepthForFullColor,
  // past which it stops getting any darker.
  const float DepthForFullColor = 6f;
  static readonly Color ShallowWater = new(112, 178, 226, 120);
  static readonly Color DeepWater = new(38, 96, 176, 225);

  static Color WaterColorFor(float depth)
  {
    float t = Math.Clamp(depth / DepthForFullColor, 0f, 1f);
    return new Color(
      (byte)(ShallowWater.R + (DeepWater.R - ShallowWater.R) * t),
      (byte)(ShallowWater.G + (DeepWater.G - ShallowWater.G) * t),
      (byte)(ShallowWater.B + (DeepWater.B - ShallowWater.B) * t),
      (byte)(ShallowWater.A + (DeepWater.A - ShallowWater.A) * t));
  }

  // The height a neighbouring column's water surface presents to this one.
  // Off the map it's just the ground, so water at the rim is drawn with a
  // proper wall on that side rather than being clipped flat.
  float NeighborWaterSurface(int fx, int fz) =>
    InBoundsFine(fx, fz) ? _voxelHeight[fx, fz] + _waterSim.DepthAt(fx, fz) : 0f;

  public void DrawWater()
  {
    RebuildDirtyWaterChunks();

    // Water is translucent, so it must not write depth: two chunks'
    // surfaces overlapping on screen would otherwise let whichever drew
    // first stop the other from blending at all, leaving visible seams
    // along chunk boundaries. Depth TESTING stays on, so water still gets
    // correctly hidden behind terrain and actors in front of it.
    Rlgl.DrawRenderBatchActive();
    Rlgl.DisableDepthMask();

    foreach (var model in _waterChunkModels)
      if (model is { } m) Raylib.DrawModel(m, Vector3.Zero, 1f, Color.White);

    Rlgl.DrawRenderBatchActive();
    Rlgl.EnableDepthMask();
  }

  void RebuildDirtyWaterChunks()
  {
    for (int cz = 0; cz < ChunksZ; cz++)
    {
      for (int cx = 0; cx < ChunksX; cx++)
      {
        if (!_waterSim.IsChunkDirty(cx, cz)) continue;

        int fx0 = cx * ChunkSize, fz0 = cz * ChunkSize;
        int fxCount = Math.Min(ChunkSize, FineWidth - fx0);
        int fzCount = Math.Min(ChunkSize, FineDepth - fz0);

        if (_waterChunkModels[cx, cz] is { } old) Raylib.UnloadModel(old);
        _waterChunkModels[cx, cz] = BuildWaterChunkModel(fx0, fz0, fxCount, fzCount);
        _waterSim.MarkChunkRendered(cx, cz);
      }
    }
  }

  unsafe Model? BuildWaterChunkModel(int fx0, int fz0, int fxCount, int fzCount)
  {
    var verts = new List<Vector3>();
    var norms = new List<Vector3>();
    var cols = new List<Color>();
    var indices = new List<ushort>();

    // Same winding convention as BuildChunkModel — see the note there.
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
        float depth = _waterSim.DepthAt(fx, fz);
        if (depth < MinRenderDepth) continue;

        float groundY = _voxelHeight[fx, fz];
        float surface = groundY + depth;
        var color = WaterColorFor(depth);

        float x0 = fx * VoxelSize, x1 = (fx + 1) * VoxelSize;
        float z0 = fz * VoxelSize, z1 = (fz + 1) * VoxelSize;
        float topY = surface * VoxelSize;

        AddQuad(
          new Vector3(x0, topY, z0), new Vector3(x1, topY, z0),
          new Vector3(x1, topY, z1), new Vector3(x0, topY, z1),
          color, new Vector3(0, 1, 0));

        // A side wall spans from this column's own surface down to
        // whatever the neighbour presents — its own water surface if it's
        // wet (usually equal, so nothing is drawn), or its bare ground if
        // it's dry or lower. Clamped to this column's ground so the wall
        // never extends below the bed the water is sitting on.
        void AddSide(int nfx, int nfz, Vector3 a, Vector3 b, Vector3 normal)
        {
          float bottom = Math.Clamp(NeighborWaterSurface(nfx, nfz), groundY, surface);
          if (surface - bottom < 1e-4f) return;

          float bottomY = bottom * VoxelSize;
          AddQuad(
            a with { Y = topY }, b with { Y = topY },
            b with { Y = bottomY }, a with { Y = bottomY },
            color, normal);
        }

        AddSide(fx - 1, fz, new Vector3(x0, 0, z0), new Vector3(x0, 0, z1), new Vector3(-1, 0, 0));
        AddSide(fx + 1, fz, new Vector3(x1, 0, z1), new Vector3(x1, 0, z0), new Vector3(1, 0, 0));
        AddSide(fx, fz - 1, new Vector3(x1, 0, z0), new Vector3(x0, 0, z0), new Vector3(0, 0, -1));
        AddSide(fx, fz + 1, new Vector3(x0, 0, z1), new Vector3(x1, 0, z1), new Vector3(0, 0, 1));
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

      // Unlike the terrain's, this alpha is meaningful — the lit shader
      // passes fragColor.a straight through to its output.
      mesh.Colors[i * 4 + 0] = cols[i].R;
      mesh.Colors[i * 4 + 1] = cols[i].G;
      mesh.Colors[i * 4 + 2] = cols[i].B;
      mesh.Colors[i * 4 + 3] = cols[i].A;
    }
    for (int i = 0; i < indices.Count; i++) mesh.Indices[i] = indices[i];

    Raylib.UploadMesh(ref mesh, false);
    var model = Raylib.LoadModelFromMesh(mesh);
    if (_terrainShader is { } shader) model.Materials[0].Shader = shader;
    return model;
  }

  // A spring: a ring of stones around a dark, wet mouth, sitting a touch
  // above the ground so it stays visible once its own water pools over it.
  const float SpringRingRadius = TileSize * 0.3f;
  const int SpringStoneCount = 8;
  static readonly Color SpringStoneColor = new(122, 128, 134, 255);
  static readonly Color SpringMouthColor = new(30, 78, 128, 255);

  public void DrawSpringsLit()
  {
    foreach (var spring in _springs)
    {
      float worldX = spring.TileX * TileSize + TileSize / 2f;
      float worldZ = spring.TileZ * TileSize + TileSize / 2f;
      float baseY = SmoothSurfaceY(worldX, worldZ);

      Raylib.DrawCube(
        new Vector3(worldX, baseY + TileSize * 0.05f, worldZ),
        TileSize * 0.42f, TileSize * 0.1f, TileSize * 0.42f, SpringMouthColor);

      for (int i = 0; i < SpringStoneCount; i++)
      {
        float a = i / (float)SpringStoneCount * MathF.Tau;
        Vector3 stonePos = new(
          worldX + MathF.Cos(a) * SpringRingRadius,
          baseY + TileSize * 0.08f,
          worldZ + MathF.Sin(a) * SpringRingRadius);
        Raylib.DrawCube(stonePos, TileSize * 0.15f, TileSize * 0.16f, TileSize * 0.15f, SpringStoneColor);
      }
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
