using System.IO;
using System.Numerics;
using System.Runtime.InteropServices;
using Raylib_cs;
using ColonySim.Entities;

namespace ColonySim.World;

// A voxel world with two grid resolutions layered on top of each other:
//
// - The FINE grid (Width*FineSubdivisions x Depth*FineSubdivisions,
//   VoxelSize world units per cell) is the real terrain data, what gets
//   rendered (small real voxel cubes, not a blended/smoothed surface, so
//   the ground has actual walls wherever height changes but reads as
//   "smooth-ish" at normal viewing distance simply because the steps are
//   small), AND what movement/collision actually operates on: every
//   placeable/movable thing (actors, trees, buildings, ...) has a square
//   footprint (in fine voxels) and a height (see CanOccupy) — that
//   footprint *is* its collision shape, positioned at any fine voxel, not
//   snapped to a coarser lattice.
// - The COARSE grid (Width x Depth, TileSize world units per cell) still
//   exists purely as a rendering/UI convenience: terrain mesh chunking,
//   mouse-ray tile picking (TryPickGround/ColumnBounds), and actor spawn
//   scatter (WalkableTiles/FirstWalkable) all still think in coarse tiles,
//   because that's cheap and none of them are collision-sensitive. Nothing
//   in this file uses the coarse grid to decide whether something can move
//   or be placed anywhere any more.
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

  // The disc a spring feeds, as offsets from its centre column. Precomputed
  // once since every spring uses the same shape every tick.
  static readonly (int Dx, int Dz)[] SpringDisc =
    (from dx in Enumerable.Range(-SpringRadiusVoxels, SpringRadiusVoxels * 2 + 1)
     from dz in Enumerable.Range(-SpringRadiusVoxels, SpringRadiusVoxels * 2 + 1)
     where dx * dx + dz * dz <= SpringRadiusVoxels * SpringRadiusVoxels
     select (dx, dz)).ToArray();

  // Trees, scattered in clusters (see WorldGenerator.ScatterTrees) — one
  // per coarse tile of clustering distance, but the actual collision
  // footprint (Tree.Footprint fine voxels around its anchor) is much
  // smaller than that whole tile; see _reservedVoxels/_obstacleHeight.
  readonly List<Tree> _trees = new();

  // Bushes, sprinkled individually — same footprint-vs-scatter-distance
  // split as trees.
  readonly List<Bush> _bushes = new();

  // Campfires, placed by the player through the build menu (see
  // Program.cs).
  readonly List<Campfire> _campfires = new();

  // Shared clock for flame-flicker animation (both the visible flame and
  // its point light read off this), and for how often scorched ground gets
  // refreshed — see UpdateVegetation.
  float _campfireTime;

  // Light posts, placed by the player through the build menu.
  readonly List<LightPost> _lightPosts = new();

  // Every fine voxel any footprint above (tree/bush/campfire/spring/light
  // post) currently occupies — checked by placement validity so nothing
  // new can overlap something already there, regardless of whether the
  // existing occupant blocks movement (a Spring reserves space here but
  // never enters _obstacleHeight, so actors still walk right over it).
  readonly HashSet<(int Fx, int Fz)> _reservedVoxels = new();

  // Only the fine voxels occupied by something with Height > 0 get an
  // entry here (see CanOccupy) — Rock/deep-water aren't obstacles in this
  // sense, they're a pure terrain check (VoxelTopAt/WaterDepthFine), and
  // Spring (Height == 0) never adds one. The stored value is the
  // occupant's own Height; CanOccupy doesn't compare it against a mover's
  // height yet (see the type's own doc comment for why — multi-story
  // floors are what will make that comparison matter).
  readonly Dictionary<(int Fx, int Fz), int> _obstacleHeight = new();

  // Rendering: one Model per chunk of the fine grid. Tracked per chunk
  // (not one global flag) and rebuilt only where something actually
  // changed — an advancing stream muddies fresh ground every tick, so
  // rebuilding all ~200 chunks on every such change would visibly stutter.
  Shader? _terrainShader;

  // One slot per texturable material (Grass/Dirt/Rock — see MaterialSlot),
  // per chunk: a chunk's terrain can't be textured as a single draw call
  // since different materials need different textures, so each chunk is
  // really up to 3 small sub-models, one per material actually present.
  const int MaterialSlotCount = 3;
  readonly Model?[,,] _chunkModels;
  readonly bool[,] _chunkDirty;

  // The actual per-material textures — see LoadMaterialTextures. A missing
  // file becomes a 1x1 white pixel rather than skipping texturing
  // entirely, so the shader's texColor * vertexColor multiply is always
  // valid and just no-ops back to the plain lit vertex colour when no art
  // has been supplied yet.
  static readonly string[] MaterialBaseName = { "grass", "dirt", "rock" };
  static readonly string[] TextureExtensions = { ".png", ".jpg", ".jpeg" };

  // A tile texture repeats every TextureWorldSize world units on screen —
  // it never benefits from being bigger than this, so a source photo
  // straight off a phone (multi-thousand-pixel, a couple MB) gets scaled
  // down to it on load rather than eating that much GPU memory verbatim.
  const int MaxTextureDimension = 256;

  readonly Texture2D[] _materialTextures = new Texture2D[MaterialSlotCount];

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

  // generate=false skips procedural generation entirely, leaving a blank
  // (all-air, dry) map — used when restoring a saved game, where every
  // voxel/tree/bush/campfire/spring is about to be populated explicitly by
  // the loader instead (see SetVoxel, AddLoaded*, CreateForLoad).
  public TileMap(int width, int depth, int seed, bool generate = true)
  {
    Width = width;
    Depth = depth;

    int chunksX = (FineWidth + ChunkSize - 1) / ChunkSize;
    int chunksZ = (FineDepth + ChunkSize - 1) / ChunkSize;
    _chunkModels = new Model?[chunksX, chunksZ, MaterialSlotCount];
    _waterChunkModels = new Model?[chunksX, chunksZ];
    _chunkDirty = new bool[chunksX, chunksZ];
    MarkAllChunksDirty();

    LoadMaterialTextures();

    if (generate)
    {
      var generated = WorldGenerator.GenerateRollingHillsWithLake(
        width, depth, FineSubdivisions, MaxHeight * FineSubdivisions, seed);

      _voxelHeight = generated.VoxelHeight;
      _voxelTop = generated.VoxelTop;
      _dryTime = new float[FineWidth, FineDepth];

      _waterSim = new WaterSim(FineWidth, FineDepth, _voxelHeight, ChunkSize);
      foreach (var (pos, waterDepth) in generated.Water)
        _waterSim.AddWater(pos.Fx, pos.Fz, waterDepth);

      foreach (var tree in generated.Trees) AddLoadedTree(tree);
      foreach (var bush in generated.Bushes) AddLoadedBush(bush);

      // WorldGenerator's springs are coarse tile candidates (see
      // WorldGenerator.HighestTile) — converted to a fine anchor here,
      // same as every other footprint.
      foreach (var (springX, springZ) in generated.Springs)
      {
        var (fx, fz) = FineCenter(springX, springZ);
        AddLoadedSpring(new Spring(fx, fz));
      }
    }
    else
    {
      _voxelHeight = new int[FineWidth, FineDepth];
      _voxelTop = new TileType[FineWidth, FineDepth];
      _dryTime = new float[FineWidth, FineDepth];
      _waterSim = new WaterSim(FineWidth, FineDepth, _voxelHeight, ChunkSize);
    }
  }

  // A blank map for SaveSystem.Load to populate voxel-by-voxel — see the
  // generate=false branch above.
  public static TileMap CreateForLoad(int width, int depth) => new(width, depth, seed: 0, generate: false);

  // --- Save/load support ---------------------------------------------------
  //
  // Plain setters/adders used only while restoring a saved game (see
  // SaveSystem.Load) — normal gameplay always goes through Dig/Deposit/
  // PlaceCampfire/etc., which enforce the rules those actions are allowed to
  // break; a load is instead handed a state that was already valid when it
  // was saved, so it just needs to be poured back in directly.

  public IReadOnlyList<Tree> Trees => _trees;
  public IReadOnlyList<Bush> Bushes => _bushes;

  public TileType VoxelTopAt(int fx, int fz) => InBoundsFine(fx, fz) ? _voxelTop[fx, fz] : TileType.Air;

  public void SetVoxel(int fx, int fz, int height, TileType top)
  {
    if (!InBoundsFine(fx, fz)) return;
    _voxelHeight[fx, fz] = height;
    _voxelTop[fx, fz] = top;
  }

  public void SetWaterDepth(int fx, int fz, float depth) => _waterSim.SetDepth(fx, fz, depth);

  // Marks every fine voxel of a size x size footprint centred on
  // (anchorFx, anchorFz) as reserved, and — if height > 0 — as a movement
  // obstacle of that height. Shared by every Place*/AddLoaded* below so
  // there's exactly one place that keeps _reservedVoxels/_obstacleHeight
  // in sync with what's actually placed.
  void ReserveFootprint(int anchorFx, int anchorFz, int size, int height)
  {
    foreach (var (fx, fz) in FootprintVoxels(anchorFx, anchorFz, size))
    {
      _reservedVoxels.Add((fx, fz));
      if (height > 0) _obstacleHeight[(fx, fz)] = height;
    }
  }

  void UnreserveFootprint(int anchorFx, int anchorFz, int size)
  {
    foreach (var (fx, fz) in FootprintVoxels(anchorFx, anchorFz, size))
    {
      _reservedVoxels.Remove((fx, fz));
      _obstacleHeight.Remove((fx, fz));
    }
  }

  public void AddLoadedTree(Tree tree)
  {
    _trees.Add(tree);
    ReserveFootprint(tree.AnchorFx, tree.AnchorFz, Tree.Footprint, Tree.Height);
  }

  public void AddLoadedBush(Bush bush)
  {
    _bushes.Add(bush);
    ReserveFootprint(bush.AnchorFx, bush.AnchorFz, Bush.Footprint, Bush.Height);
  }

  // No player-facing way to fell a tree or clear a bush exists yet — these
  // exist for AirStrike (see DestroyObstaclesIn), same RemoveAt +
  // UnreserveFootprint pattern as RemoveCampfire/RemoveSpring/RemoveLightPost.
  public bool RemoveTree(int anchorFx, int anchorFz)
  {
    int index = _trees.FindIndex(t => t.AnchorFx == anchorFx && t.AnchorFz == anchorFz);
    if (index < 0) return false;

    _trees.RemoveAt(index);
    UnreserveFootprint(anchorFx, anchorFz, Tree.Footprint);
    return true;
  }

  public bool RemoveBush(int anchorFx, int anchorFz)
  {
    int index = _bushes.FindIndex(b => b.AnchorFx == anchorFx && b.AnchorFz == anchorFz);
    if (index < 0) return false;

    _bushes.RemoveAt(index);
    UnreserveFootprint(anchorFx, anchorFz, Bush.Footprint);
    return true;
  }

  public void AddLoadedCampfire(Campfire campfire)
  {
    _campfires.Add(campfire);
    ReserveFootprint(campfire.AnchorFx, campfire.AnchorFz, Campfire.Footprint, Campfire.Height);
  }

  public void AddLoadedSpring(Spring spring)
  {
    _springs.Add(spring);
    ReserveFootprint(spring.AnchorFx, spring.AnchorFz, Spring.Footprint, Spring.Height);
  }

  public void AddLoadedLightPost(LightPost lightPost)
  {
    _lightPosts.Add(lightPost);
    ReserveFootprint(lightPost.AnchorFx, lightPost.AnchorFz, LightPost.Footprint, LightPost.Height);
  }

  // Called once every voxel/tree/bush/campfire/spring/light post has been poured back
  // in — makes sure every chunk actually gets (re)built from the loaded
  // state on the first frame rather than relying on whatever dirty flags
  // happened to be left over.
  public void FinishLoading() => MarkAllChunksDirty();

  // Looks for Assets/Textures/{grass,dirt,rock}.{png,jpg,jpeg} next to the
  // built executable — see Assets/Textures/README.md for exactly what to
  // drop in there. Whatever's missing just gets a 1x1 white pixel instead,
  // which is what keeps a material's appearance identical to today's flat
  // colour until real art shows up for it.
  static Texture2D BlankAlbedo()
  {
    Image blank = Raylib.GenImageColor(1, 1, Color.White);
    var tex = Raylib.LoadTextureFromImage(blank);
    Raylib.UnloadImage(blank);
    return tex;
  }

  void LoadMaterialTextures()
  {
    string dir = Path.Combine(AppContext.BaseDirectory, "Assets", "Textures");
    for (int slot = 0; slot < MaterialSlotCount; slot++)
    {
      string? path = null;
      foreach (var ext in TextureExtensions)
      {
        string candidate = Path.Combine(dir, MaterialBaseName[slot] + ext);
        if (File.Exists(candidate)) { path = candidate; break; }
      }

      if (path == null)
      {
        _materialTextures[slot] = BlankAlbedo();
        continue;
      }

      Image img = Raylib.LoadImage(path);

      // A found-but-undecodable file (wrong format despite the extension,
      // corrupt data, a format stb_image doesn't support — e.g. this
      // build's raylib has no real JPG decoder even though it'll read a
      // .jpg's bytes) mustn't turn into an invalid, all-black texture: an
      // Image that failed to decode still gets bound to the material, and
      // an incomplete GL texture samples as solid black rather than
      // falling back cleanly. Catch it here instead and fall back to the
      // same blank pixel a missing file gets.
      if (!Raylib.IsImageValid(img))
      {
        Console.Error.WriteLine(
          $"[TileMap] Couldn't decode texture '{path}' — falling back to the plain colour for this material. " +
          "(Raylib's PNG loader can't read non-PNG bytes even if the extension says .png — this build also has no real JPG decoder at all.)");
        _materialTextures[slot] = BlankAlbedo();
        continue;
      }

      if (img.Width > MaxTextureDimension || img.Height > MaxTextureDimension)
      {
        float scale = MaxTextureDimension / (float)Math.Max(img.Width, img.Height);
        Raylib.ImageResize(ref img, Math.Max(1, (int)(img.Width * scale)), Math.Max(1, (int)(img.Height * scale)));
      }

      var tex = Raylib.LoadTextureFromImage(img);
      Raylib.UnloadImage(img);
      Raylib.SetTextureWrap(tex, TextureWrap.Repeat);
      Raylib.SetTextureFilter(tex, TextureFilter.Bilinear);
      _materialTextures[slot] = tex;
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
        ScorchAround(fire.AnchorFx, fire.AnchorFz, ScorchRadiusVoxels);
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

  // --- Coarse-grid queries (rendering/UI convenience only — see this
  // file's own class doc comment; nothing collision-related lives here
  // any more) ---

  public bool InBounds(int x, int z) => x >= 0 && z >= 0 && x < Width && z < Depth;

  // The fine voxel at the centre of a coarse tile — used to seed a
  // footprint anchor from a coarse-tile candidate (spawn scatter, springs'
  // WorldGenerator.HighestTile) and by a few rendering helpers below that
  // still sample "the" representative column of a coarse tile.
  public (int Fx, int Fz) FineCenter(int coarseX, int coarseZ) =>
    (coarseX * FineSubdivisions + FineSubdivisions / 2, coarseZ * FineSubdivisions + FineSubdivisions / 2);

  // A coarse tile's height in fine voxels, sampled at its centre fine
  // column — the single representative value a few rendering helpers
  // (SurfaceY, ColumnBounds, ...) treat that whole tile as having.
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

  // Whether a specific fine column can be dug: bounded, not sitting under
  // something that actually occupies that exact voxel (a tree trunk, a
  // campfire, ...), has material left with a Grass/Dirt top (not bedrock),
  // and isn't under standing water.
  public bool CanDigVoxel(int fx, int fz)
  {
    if (!InBoundsFine(fx, fz)) return false;
    if (_obstacleHeight.ContainsKey((fx, fz))) return false;
    if (_voxelHeight[fx, fz] <= 0) return false;
    var top = _voxelTop[fx, fz];
    if (top != TileType.Grass && top != TileType.Dirt) return false;
    return _waterSim.DepthAt(fx, fz) <= 0f;
  }

  // Whether a specific fine column has room to grow by one more voxel —
  // same obstacle/water gating as CanDigVoxel, just checking height room
  // instead of material to remove.
  public bool CanDepositVoxel(int fx, int fz)
  {
    if (!InBoundsFine(fx, fz)) return false;
    if (_obstacleHeight.ContainsKey((fx, fz))) return false;
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

  // Removes exactly the top voxel of one fine column for AirStrike, which —
  // unlike a hand dig — is allowed to crater through anything except
  // standing water: no Grass/Dirt-only restriction, and no obstacle check
  // (callers are expected to have already cleared obstacles in the blast
  // area via DestroyObstaclesIn). Returns 1 on success, matching DigVoxel's
  // convention, 0 if there's nothing left to remove or the column's wet.
  public int BlastVoxel(int fx, int fz)
  {
    if (!InBoundsFine(fx, fz)) return 0;
    if (_voxelHeight[fx, fz] <= 0) return 0;
    if (_waterSim.DepthAt(fx, fz) > 0f) return 0;
    _voxelHeight[fx, fz] -= 1;
    _voxelTop[fx, fz] = TileType.Dirt;
    MarkTerrainChangedAt(fx, fz);
    return 1;
  }

  // Destroys every tree/bush/campfire/spring/light post whose footprint
  // overlaps any of the given fine columns — an AirStrike catching the edge
  // of a tree takes out the whole tree, not just whatever voxels sit
  // directly under its trunk. Each match is unreserved the same way its own
  // Remove* method would.
  public void DestroyObstaclesIn(IEnumerable<(int Fx, int Fz)> columns)
  {
    var hit = new HashSet<(int Fx, int Fz)>(columns);
    bool Overlaps(int anchorFx, int anchorFz, int size) =>
      FootprintVoxels(anchorFx, anchorFz, size).Any(hit.Contains);

    _trees.RemoveAll(t =>
    {
      if (!Overlaps(t.AnchorFx, t.AnchorFz, Tree.Footprint)) return false;
      UnreserveFootprint(t.AnchorFx, t.AnchorFz, Tree.Footprint);
      return true;
    });
    _bushes.RemoveAll(b =>
    {
      if (!Overlaps(b.AnchorFx, b.AnchorFz, Bush.Footprint)) return false;
      UnreserveFootprint(b.AnchorFx, b.AnchorFz, Bush.Footprint);
      return true;
    });
    _campfires.RemoveAll(c =>
    {
      if (!Overlaps(c.AnchorFx, c.AnchorFz, Campfire.Footprint)) return false;
      UnreserveFootprint(c.AnchorFx, c.AnchorFz, Campfire.Footprint);
      return true;
    });
    _springs.RemoveAll(s =>
    {
      if (!Overlaps(s.AnchorFx, s.AnchorFz, Spring.Footprint)) return false;
      UnreserveFootprint(s.AnchorFx, s.AnchorFz, Spring.Footprint);
      return true;
    });
    _lightPosts.RemoveAll(l =>
    {
      if (!Overlaps(l.AnchorFx, l.AnchorFz, LightPost.Footprint)) return false;
      UnreserveFootprint(l.AnchorFx, l.AnchorFz, LightPost.Footprint);
      return true;
    });
  }

  // --- Campfires ---

  public IReadOnlyList<Campfire> Campfires => _campfires;

  // How far (in fine voxels) a campfire's heat scorches Grass to Dirt
  // around it — see ScorchAround.
  const float ScorchRadiusVoxels = 16f;

  // A spot has to have its whole footprint on ordinary open ground: in
  // bounds, no Rock, dry throughout, and not already reserved by another
  // footprint (tree/bush/another campfire/spring/light post).
  public bool CanPlaceCampfire(int anchorFx, int anchorFz)
  {
    if (!FootprintClear(anchorFx, anchorFz, Campfire.Footprint)) return false;
    foreach (var (fx, fz) in FootprintVoxels(anchorFx, anchorFz, Campfire.Footprint))
      if (_waterSim.DepthAt(fx, fz) > 0f) return false;
    return true;
  }

  // Places a lit campfire anchored at a fine voxel, immediately scorching
  // the grass around it (see ScorchAround) and blocking its whole
  // footprint from movement — an actor can't stand in the fire, the same
  // way it can't stand in a tree. Returns null without changing anything
  // if the spot isn't valid; callers should check CanPlaceCampfire first
  // if they want to know why.
  public Campfire? PlaceCampfire(int anchorFx, int anchorFz)
  {
    if (!CanPlaceCampfire(anchorFx, anchorFz)) return null;

    var fire = new Campfire(anchorFx, anchorFz, Random.Shared.NextSingle() * MathF.Tau);
    _campfires.Add(fire);
    ReserveFootprint(anchorFx, anchorFz, Campfire.Footprint, Campfire.Height);

    ScorchAround(anchorFx, anchorFz, ScorchRadiusVoxels);
    return fire;
  }

  // Tears down a campfire a DemolishCampfire task has finished working —
  // frees its footprint back up for movement. Leaves the scorched ground
  // alone; ordinary regrowth (see StepRegrowth) will grass it back over in
  // time same as any other patch of dry Dirt. Returns false if there was no
  // campfire at that anchor to begin with (shouldn't happen in practice,
  // since WorkQueue prunes a demolish task the moment its target vanishes).
  public bool RemoveCampfire(int anchorFx, int anchorFz)
  {
    int index = _campfires.FindIndex(f => f.AnchorFx == anchorFx && f.AnchorFz == anchorFz);
    if (index < 0) return false;

    _campfires.RemoveAt(index);
    UnreserveFootprint(anchorFx, anchorFz, Campfire.Footprint);
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
      float worldX = fire.AnchorFx * VoxelSize + VoxelSize / 2f;
      float worldZ = fire.AnchorFz * VoxelSize + VoxelSize / 2f;
      float baseY = SmoothSurfaceY(worldX, worldZ);
      Vector3 pos = new(worldX, baseY + CampfireLightHeight, worldZ);
      yield return (pos, CampfireLightColor * Flicker(fire.FlickerPhase));
    }
  }

  // --- Footprint collision (the real movement/placement rule) -----------
  //
  // Every placeable/movable thing is a size x size square of fine voxels
  // centred on an anchor voxel (see e.g. Campfire.Footprint) plus a height
  // (see e.g. Campfire.Height). This is the single shared "does this
  // footprint fit here" check both pathfinding (via CanStep) and building
  // placement (via CanPlaceCampfire/etc., layered with their own extra
  // rules like dryness) are built from.

  // The min/max fine-voxel corners of a size x size footprint centred on
  // (anchorFx, anchorFz). size is always odd for every type in this game,
  // so the anchor sits exactly in the middle — no rounding bias.
  public static (int MinFx, int MinFz, int MaxFx, int MaxFz) FootprintBounds(int anchorFx, int anchorFz, int size)
  {
    int half = size / 2;
    int minFx = anchorFx - half, minFz = anchorFz - half;
    return (minFx, minFz, minFx + size - 1, minFz + size - 1);
  }

  public static IEnumerable<(int Fx, int Fz)> FootprintVoxels(int anchorFx, int anchorFz, int size)
  {
    var (minFx, minFz, maxFx, maxFz) = FootprintBounds(anchorFx, anchorFz, size);
    for (int fx = minFx; fx <= maxFx; fx++)
      for (int fz = minFz; fz <= maxFz; fz++)
        yield return (fx, fz);
  }

  public bool FootprintInBounds(int anchorFx, int anchorFz, int size)
  {
    var (minFx, minFz, maxFx, maxFz) = FootprintBounds(anchorFx, anchorFz, size);
    return InBoundsFine(minFx, minFz) && InBoundsFine(maxFx, maxFz);
  }

  // Whether a footprint's exact voxel at (fx, fz) falls within an existing
  // footprint anchored at (anchorFx, anchorFz) — used to hit-test an
  // existing building from anywhere in its footprint (a demolish click
  // anywhere on a campfire, not just its exact anchor pixel).
  public static bool FootprintContains(int anchorFx, int anchorFz, int size, int fx, int fz)
  {
    var (minFx, minFz, maxFx, maxFz) = FootprintBounds(anchorFx, anchorFz, size);
    return fx >= minFx && fx <= maxFx && fz >= minFz && fz <= maxFz;
  }

  // Whether a NEW footprint has room to go here: in bounds, no Rock
  // anywhere under it, and not overlapping anything already reserved
  // (tree/bush/campfire/spring/light post) — the common placement check
  // every CanPlace* shares before layering its own extra rules (dryness
  // for a campfire, none for a spring/light post) on top.
  bool FootprintClear(int anchorFx, int anchorFz, int size)
  {
    if (!FootprintInBounds(anchorFx, anchorFz, size)) return false;
    foreach (var (fx, fz) in FootprintVoxels(anchorFx, anchorFz, size))
    {
      if (VoxelTopAt(fx, fz) == TileType.Rock) return false;
      if (_reservedVoxels.Contains((fx, fz))) return false;
    }
    return true;
  }

  // Whether a mover with the given footprint/height could stand with its
  // footprint centred on (anchorFx, anchorFz) right now: every voxel in
  // bounds, no Rock, no water too deep to wade, and no obstacle occupying
  // it. height isn't compared against anything yet — see _obstacleHeight's
  // own doc comment for why it's still threaded through (multi-story
  // floors are what will make the comparison matter; today any obstacle
  // voxel simply blocks, since every mover and every obstacle share the
  // same ground-level base).
  public bool CanOccupy(int anchorFx, int anchorFz, int footprintSize, int height)
  {
    if (!FootprintInBounds(anchorFx, anchorFz, footprintSize)) return false;
    foreach (var (fx, fz) in FootprintVoxels(anchorFx, anchorFz, footprintSize))
    {
      if (VoxelTopAt(fx, fz) == TileType.Rock) return false;
      if (_waterSim.DepthAt(fx, fz) > MaxWadeableWaterVoxels) return false;
      if (_obstacleHeight.ContainsKey((fx, fz))) return false;
    }
    return true;
  }

  // Whether a mover can take one step from its footprint centred at
  // (fromFx, fromFz) to one centred at the adjacent (toFx, toFz) — adjacent
  // meaning any of the 8 surrounding voxels, straight or diagonal (see
  // AStar.Neighbours). The destination has to be occupiable on its own
  // terms, and the climb (compared at the two anchors — footprints wider
  // than 1 voxel only check their own centre column's rise, a reasonable
  // approximation until something bigger than today's 1-voxel actor default
  // actually exists) can't be steeper than half an old block. Stepping down
  // is never limited — only climbing.
  public bool CanStep(int fromFx, int fromFz, int toFx, int toFz, int footprintSize, int height)
  {
    if (!CanOccupy(toFx, toFz, footprintSize, height)) return false;
    if (_voxelHeight[toFx, toFz] - _voxelHeight[fromFx, fromFz] > MaxStepUpLevels) return false;

    // A diagonal step also needs both of its flanking orthogonal steps to
    // be legal on their own — otherwise it's cutting across the corner of
    // whatever's blocking (or too steep at) one of those two flanking
    // voxels, which would read as clipping straight through it rather than
    // walking around. The recursive calls are always straight steps (one
    // coordinate is unchanged), so this can't recurse into itself again.
    int dx = toFx - fromFx;
    int dz = toFz - fromFz;
    if (dx != 0 && dz != 0)
    {
      if (!CanStep(fromFx, fromFz, toFx, fromFz, footprintSize, height)) return false;
      if (!CanStep(fromFx, fromFz, fromFx, toFz, footprintSize, height)) return false;
    }

    return true;
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

  // Coarse "is roughly walkable" check — used ONLY for actor spawn scatter
  // (WalkableTiles/FirstWalkable) below, nowhere movement-related any more
  // (see CanOccupy, which is what pathfinding actually uses). Samples the
  // tile's centre fine voxel with a 1-voxel footprint; the height argument
  // doesn't affect the result yet (see CanOccupy's own note), so any value
  // works here.
  public bool IsWalkable(int x, int z)
  {
    if (!InBounds(x, z)) return false;
    var (fx, fz) = FineCenter(x, z);
    return CanOccupy(fx, fz, 1, 0);
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
        float perColumn = SpringFlowRate / SpringDisc.Length;
        foreach (var (dx, dz) in SpringDisc)
          _waterSim.AddWater(spring.AnchorFx + dx, spring.AnchorFz + dz, perColumn);
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
  // reserved there — except that a spring is allowed on wet ground.
  // Placing one in a pond to raise its level is a perfectly reasonable
  // thing to want, and unlike a fire there is nothing about a spring that
  // water ruins. Spring.Height is 0, so it's never a movement obstacle
  // (ReserveFootprint below only adds an _obstacleHeight entry for
  // Height > 0) — an actor still walks straight over it.
  public bool CanPlaceSpring(int anchorFx, int anchorFz) => FootprintClear(anchorFx, anchorFz, Spring.Footprint);

  public Spring? PlaceSpring(int anchorFx, int anchorFz)
  {
    if (!CanPlaceSpring(anchorFx, anchorFz)) return null;

    var spring = new Spring(anchorFx, anchorFz);
    _springs.Add(spring);
    ReserveFootprint(anchorFx, anchorFz, Spring.Footprint, Spring.Height);

    // Wake the sim at the spring's own column, so its first tick of water
    // is simulated even if it was placed nowhere near any existing water.
    _waterSim.Touch(anchorFx, anchorFz);
    return spring;
  }

  // Caps a spring. The water it already produced is left exactly where it
  // is: it drains off the map rim if it can reach one, and otherwise just
  // sits there like any other pond. That's the point of being able to cap
  // one — it stops the supply, it doesn't undo the flood.
  public bool RemoveSpring(int anchorFx, int anchorFz)
  {
    int index = _springs.FindIndex(s => s.AnchorFx == anchorFx && s.AnchorFz == anchorFz);
    if (index < 0) return false;

    _springs.RemoveAt(index);
    UnreserveFootprint(anchorFx, anchorFz, Spring.Footprint);
    return true;
  }

  // --- Light Posts ---------------------------------------------------------

  public IReadOnlyList<LightPost> LightPosts => _lightPosts;

  // Same open-ground rule as a spring, minus the "allowed on wet ground"
  // exception (a post doesn't care about water either way, so the plain
  // FootprintClear rule already covers it — no extra dryness check needed).
  public bool CanPlaceLightPost(int anchorFx, int anchorFz) => FootprintClear(anchorFx, anchorFz, LightPost.Footprint);

  public LightPost? PlaceLightPost(int anchorFx, int anchorFz)
  {
    if (!CanPlaceLightPost(anchorFx, anchorFz)) return null;

    var post = new LightPost(anchorFx, anchorFz);
    _lightPosts.Add(post);
    ReserveFootprint(anchorFx, anchorFz, LightPost.Footprint, LightPost.Height);
    return post;
  }

  public bool RemoveLightPost(int anchorFx, int anchorFz)
  {
    int index = _lightPosts.FindIndex(l => l.AnchorFx == anchorFx && l.AnchorFz == anchorFz);
    if (index < 0) return false;

    _lightPosts.RemoveAt(index);
    UnreserveFootprint(anchorFx, anchorFz, LightPost.Footprint);
    return true;
  }

  // A point light per light post, steady and faint blue — the whole point
  // is to mark a path at night without turning it into another campfire, so
  // this deliberately skips Flicker's per-frame animation. Height matches
  // where the glowing cap cube is actually drawn (see DrawLightPostsGlow),
  // so the light appears to come from the visible cube, not float above it.
  static readonly Vector3 LightPostLightColor = new(0.22f, 0.42f, 0.85f);
  const float LightPostLightHeight = PostHeight + PostCapSize / 2f;

  public IEnumerable<(Vector3 Position, Vector3 Color)> LightPostLights()
  {
    foreach (var post in _lightPosts)
    {
      float worldX = post.AnchorFx * VoxelSize + VoxelSize / 2f;
      float worldZ = post.AnchorFz * VoxelSize + VoxelSize / 2f;
      float baseY = SmoothSurfaceY(worldX, worldZ);
      yield return (new Vector3(worldX, baseY + LightPostLightHeight, worldZ), LightPostLightColor);
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

        for (int slot = 0; slot < MaterialSlotCount; slot++)
          if (_chunkModels[cx, cz, slot] is { } old) Raylib.UnloadModel(old);

        var built = BuildChunkModel(fx0, fz0, fxCount, fzCount);
        for (int slot = 0; slot < MaterialSlotCount; slot++)
          _chunkModels[cx, cz, slot] = built[slot];

        _chunkDirty[cx, cz] = false;
      }
    }
  }

  // Which of the 3 texturable buckets a material's faces land in — see
  // MaterialTextureFile/_materialTextures and BuildChunkModel.
  static int MaterialSlot(TileType t) => t switch
  {
    TileType.Grass => 0,
    TileType.Dirt => 1,
    TileType.Rock => 2,
    _ => 1 // shouldn't happen (Air never gets a face)
  };

  // Accumulates one material's worth of a chunk's geometry. A chunk builds
  // up to MaterialSlotCount of these (see BuildChunkModel) since a single
  // Model can only carry one texture, and grass/dirt/rock need different
  // ones.
  sealed class ChunkMeshBuilder
  {
    public readonly List<Vector3> Verts = new();
    public readonly List<Vector3> Norms = new();
    public readonly List<Color> Cols = new();
    public readonly List<Vector2> Uvs = new();
    public readonly List<ushort> Indices = new();
  }

  // World units one texture tile spans, for both top-face and wall UVs —
  // one coarse tile's worth, so a texture is authored at "this is what one
  // tile of this material looks like" scale.
  const float TextureWorldSize = TileSize;

  // Builds one chunk's worth of voxel-cube geometry (top + exposed side
  // faces only), split into up to MaterialSlotCount per-material sub-
  // models. Raylib's Mesh is a handful of raw unmanaged buffers (no
  // managed-array wrapper for custom meshes), so vertex/normal/colour/uv/
  // index data is accumulated in plain Lists first, then copied into
  // NativeMemory-allocated buffers at the end; UnloadModel frees that same
  // memory later since raylib's allocator and .NET's NativeMemory both
  // ultimately go through the platform's malloc/free.
  unsafe Model?[] BuildChunkModel(int fx0, int fz0, int fxCount, int fzCount)
  {
    var builders = new ChunkMeshBuilder[MaterialSlotCount];
    for (int i = 0; i < MaterialSlotCount; i++) builders[i] = new ChunkMeshBuilder();

    // Winding has to actually match each face's stated normal, not just the
    // normal data itself — raylib backface-culls by default, so a triangle
    // wound the wrong way gets discarded from the very side it's meant to
    // be seen from (only visible from behind), regardless of what its
    // vertex normals say. (a, c, b) / (a, d, c) is the order that comes out
    // CCW as seen from the direction each of this method's callers' normal
    // actually points.
    void AddQuad(
      TileType material, Vector3 a, Vector3 b, Vector3 c, Vector3 d,
      Color colA, Color colB, Color colC, Color colD,
      Vector2 uvA, Vector2 uvB, Vector2 uvC, Vector2 uvD, Vector3 normal)
    {
      var m = builders[MaterialSlot(material)];
      ushort baseIndex = (ushort)m.Verts.Count;
      m.Verts.Add(a); m.Verts.Add(b); m.Verts.Add(c); m.Verts.Add(d);
      m.Norms.Add(normal); m.Norms.Add(normal); m.Norms.Add(normal); m.Norms.Add(normal);
      m.Cols.Add(colA); m.Cols.Add(colB); m.Cols.Add(colC); m.Cols.Add(colD);
      m.Uvs.Add(uvA); m.Uvs.Add(uvB); m.Uvs.Add(uvC); m.Uvs.Add(uvD);
      m.Indices.Add(baseIndex); m.Indices.Add((ushort)(baseIndex + 2)); m.Indices.Add((ushort)(baseIndex + 1));
      m.Indices.Add(baseIndex); m.Indices.Add((ushort)(baseIndex + 3)); m.Indices.Add((ushort)(baseIndex + 2));
    }

    // Walls stay flat-shaded (one colour for all 4 corners) — only top
    // faces get the full per-corner AO treatment; see CornerBrightness.
    void AddQuadFlat(
      TileType material, Vector3 a, Vector3 b, Vector3 c, Vector3 d, Color color,
      Vector2 uvA, Vector2 uvB, Vector2 uvC, Vector2 uvD, Vector3 normal) =>
      AddQuad(material, a, b, c, d, color, color, color, color, uvA, uvB, uvC, uvD, normal);

    Vector2 UV(float u, float v) => new(u / TextureWorldSize, v / TextureWorldSize);

    for (int fz = fz0; fz < fz0 + fzCount; fz++)
    {
      for (int fx = fx0; fx < fx0 + fxCount; fx++)
      {
        int height = _voxelHeight[fx, fz];
        if (height <= 0) continue; // dug all the way down to nothing

        float x0 = fx * VoxelSize, x1 = (fx + 1) * VoxelSize;
        float z0 = fz * VoxelSize, z1 = (fz + 1) * VoxelSize;
        float topY = height * VoxelSize;

        var topMat = _voxelTop[fx, fz];
        var topColor = WithNoise(ColorFor(topMat), fx, fz);
        AddQuad(topMat,
          new Vector3(x0, topY, z0), new Vector3(x1, topY, z0),
          new Vector3(x1, topY, z1), new Vector3(x0, topY, z1),
          Shaded(topColor, CornerBrightness(fx, fz, height, -1, -1)),
          Shaded(topColor, CornerBrightness(fx, fz, height, +1, -1)),
          Shaded(topColor, CornerBrightness(fx, fz, height, +1, +1)),
          Shaded(topColor, CornerBrightness(fx, fz, height, -1, +1)),
          UV(x0, z0), UV(x1, z0), UV(x1, z1), UV(x0, z1),
          new Vector3(0, 1, 0));

        int westH = FineHeightOrZero(fx - 1, fz);
        if (westH < height)
          foreach (var (bottom, top, mat) in WallBands(fx, fz, westH, height))
            AddQuadFlat(mat,
              new Vector3(x0, top * VoxelSize, z0), new Vector3(x0, top * VoxelSize, z1),
              new Vector3(x0, bottom * VoxelSize, z1), new Vector3(x0, bottom * VoxelSize, z0),
              WithNoise(ColorFor(mat), fx, fz),
              UV(z0, top * VoxelSize), UV(z1, top * VoxelSize), UV(z1, bottom * VoxelSize), UV(z0, bottom * VoxelSize),
              new Vector3(-1, 0, 0));

        int eastH = FineHeightOrZero(fx + 1, fz);
        if (eastH < height)
          foreach (var (bottom, top, mat) in WallBands(fx, fz, eastH, height))
            AddQuadFlat(mat,
              new Vector3(x1, top * VoxelSize, z1), new Vector3(x1, top * VoxelSize, z0),
              new Vector3(x1, bottom * VoxelSize, z0), new Vector3(x1, bottom * VoxelSize, z1),
              WithNoise(ColorFor(mat), fx, fz),
              UV(z1, top * VoxelSize), UV(z0, top * VoxelSize), UV(z0, bottom * VoxelSize), UV(z1, bottom * VoxelSize),
              new Vector3(1, 0, 0));

        int southH = FineHeightOrZero(fx, fz - 1);
        if (southH < height)
          foreach (var (bottom, top, mat) in WallBands(fx, fz, southH, height))
            AddQuadFlat(mat,
              new Vector3(x1, top * VoxelSize, z0), new Vector3(x0, top * VoxelSize, z0),
              new Vector3(x0, bottom * VoxelSize, z0), new Vector3(x1, bottom * VoxelSize, z0),
              WithNoise(ColorFor(mat), fx, fz),
              UV(x1, top * VoxelSize), UV(x0, top * VoxelSize), UV(x0, bottom * VoxelSize), UV(x1, bottom * VoxelSize),
              new Vector3(0, 0, -1));

        int northH = FineHeightOrZero(fx, fz + 1);
        if (northH < height)
          foreach (var (bottom, top, mat) in WallBands(fx, fz, northH, height))
            AddQuadFlat(mat,
              new Vector3(x0, top * VoxelSize, z1), new Vector3(x1, top * VoxelSize, z1),
              new Vector3(x1, bottom * VoxelSize, z1), new Vector3(x0, bottom * VoxelSize, z1),
              WithNoise(ColorFor(mat), fx, fz),
              UV(x0, top * VoxelSize), UV(x1, top * VoxelSize), UV(x1, bottom * VoxelSize), UV(x0, bottom * VoxelSize),
              new Vector3(0, 0, 1));
      }
    }

    var models = new Model?[MaterialSlotCount];
    for (int slot = 0; slot < MaterialSlotCount; slot++)
      models[slot] = UploadChunkMesh(builders[slot], slot);
    return models;
  }

  unsafe Model? UploadChunkMesh(ChunkMeshBuilder b, int materialSlot)
  {
    if (b.Verts.Count == 0) return null;

    int vertexCount = b.Verts.Count;
    int triangleCount = b.Indices.Count / 3;

    var mesh = new Mesh { VertexCount = vertexCount, TriangleCount = triangleCount };
    mesh.Vertices = (float*)NativeMemory.Alloc((nuint)(vertexCount * 3 * sizeof(float)));
    mesh.Normals = (float*)NativeMemory.Alloc((nuint)(vertexCount * 3 * sizeof(float)));
    mesh.Colors = (byte*)NativeMemory.Alloc((nuint)(vertexCount * 4));
    mesh.TexCoords = (float*)NativeMemory.Alloc((nuint)(vertexCount * 2 * sizeof(float)));
    mesh.Indices = (ushort*)NativeMemory.Alloc((nuint)(b.Indices.Count * sizeof(ushort)));

    for (int i = 0; i < vertexCount; i++)
    {
      mesh.Vertices[i * 3 + 0] = b.Verts[i].X;
      mesh.Vertices[i * 3 + 1] = b.Verts[i].Y;
      mesh.Vertices[i * 3 + 2] = b.Verts[i].Z;

      mesh.Normals[i * 3 + 0] = b.Norms[i].X;
      mesh.Normals[i * 3 + 1] = b.Norms[i].Y;
      mesh.Normals[i * 3 + 2] = b.Norms[i].Z;

      mesh.Colors[i * 4 + 0] = b.Cols[i].R;
      mesh.Colors[i * 4 + 1] = b.Cols[i].G;
      mesh.Colors[i * 4 + 2] = b.Cols[i].B;
      mesh.Colors[i * 4 + 3] = 255;

      mesh.TexCoords[i * 2 + 0] = b.Uvs[i].X;
      mesh.TexCoords[i * 2 + 1] = b.Uvs[i].Y;
    }
    for (int i = 0; i < b.Indices.Count; i++) mesh.Indices[i] = b.Indices[i];

    Raylib.UploadMesh(ref mesh, false);
    var model = Raylib.LoadModelFromMesh(mesh);
    model.Materials[0].Maps[(int)MaterialMapIndex.Albedo].Texture = _materialTextures[materialSlot];
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
      float worldX = tree.AnchorFx * VoxelSize + VoxelSize / 2f;
      float worldZ = tree.AnchorFz * VoxelSize + VoxelSize / 2f;
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
      float worldX = bush.AnchorFx * VoxelSize + VoxelSize / 2f;
      float worldZ = bush.AnchorFz * VoxelSize + VoxelSize / 2f;
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
      float worldX = fire.AnchorFx * VoxelSize + VoxelSize / 2f;
      float worldZ = fire.AnchorFz * VoxelSize + VoxelSize / 2f;
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
      float worldX = fire.AnchorFx * VoxelSize + VoxelSize / 2f;
      float worldZ = fire.AnchorFz * VoxelSize + VoxelSize / 2f;
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
    foreach (var tex in _materialTextures)
      Raylib.UnloadTexture(tex);
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
      float worldX = spring.AnchorFx * VoxelSize + VoxelSize / 2f;
      float worldZ = spring.AnchorFz * VoxelSize + VoxelSize / 2f;
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

  // A light post: a slim dark-gray stick, ordinary lit geometry like a
  // campfire's logs. The glowing cube on top is drawn separately, unlit, by
  // DrawLightPostsGlow — see that method for why.
  const float PostHeight = TileSize * 0.75f;
  const float PostThickness = TileSize * 0.08f;
  static readonly Color PostColor = new(58, 58, 62, 255);

  public void DrawLightPostsLit()
  {
    foreach (var post in _lightPosts)
    {
      float worldX = post.AnchorFx * VoxelSize + VoxelSize / 2f;
      float worldZ = post.AnchorFz * VoxelSize + VoxelSize / 2f;
      float baseY = SmoothSurfaceY(worldX, worldZ);

      Vector3 stickCenter = new(worldX, baseY + PostHeight / 2f, worldZ);
      Raylib.DrawCube(stickCenter, PostThickness, PostHeight, PostThickness, PostColor);
    }
  }

  // The glowing cube on top: unlit, like a campfire's flame, so it stays
  // vividly visible regardless of ambient darkness — it IS the light, not
  // something lit by it. No flicker (unlike a flame) since a lamp should
  // read as steady, not guttering.
  const float PostCapSize = TileSize * 0.14f;
  static readonly Color PostCapColor = new(120, 190, 255, 235);

  public void DrawLightPostsGlow()
  {
    foreach (var post in _lightPosts)
    {
      float worldX = post.AnchorFx * VoxelSize + VoxelSize / 2f;
      float worldZ = post.AnchorFz * VoxelSize + VoxelSize / 2f;
      float baseY = SmoothSurfaceY(worldX, worldZ);

      Vector3 capCenter = new(worldX, baseY + PostHeight + PostCapSize / 2f, worldZ);
      Raylib.DrawCube(capCenter, PostCapSize, PostCapSize, PostCapSize, PostCapColor);
    }
  }

  // --- Cheap terrain-surface polish ---------------------------------------
  //
  // Two things that make flat-coloured voxels read as real geometry instead
  // of a solid-shaded blob, both static/deterministic functions of a fine
  // column's position so nothing extra needs to be stored per voxel:
  //
  //  - WithNoise: a small per-column brightness jitter, so a big flat plain
  //    doesn't read as one dead-flat colour.
  //  - CornerBrightness: per-corner ambient occlusion on top faces — a
  //    taller neighbouring column shadows the corner it shares with this
  //    one, which is what makes a dug pit's rim, or the base of a rise,
  //    actually read as a nook instead of just a flat shaded plane.

  const float ColorNoiseAmplitude = 0.06f;

  // Cheap deterministic hash -> roughly uniform in [-1, 1]. Doesn't need to
  // be a good hash, just stable and fast — this runs per vertex, per chunk
  // rebuild.
  static float ColumnNoise(int fx, int fz)
  {
    int h = fx * 374761393 + fz * 668265263;
    h = (h ^ (h >> 13)) * 1274126177;
    h ^= h >> 16;
    return ((h & 0xFFFF) / 65535f) * 2f - 1f;
  }

  static Color WithNoise(Color c, int fx, int fz)
  {
    float n = 1f + ColumnNoise(fx, fz) * ColorNoiseAmplitude;
    return new Color(
      (byte)Math.Clamp(c.R * n, 0, 255),
      (byte)Math.Clamp(c.G * n, 0, 255),
      (byte)Math.Clamp(c.B * n, 0, 255),
      c.A);
  }

  static Color Shaded(Color c, float brightness) => new(
    (byte)Math.Clamp(c.R * brightness, 0, 255),
    (byte)Math.Clamp(c.G * brightness, 0, 255),
    (byte)Math.Clamp(c.B * brightness, 0, 255),
    c.A);

  // 0 occluding neighbours -> full brightness, 3 -> darkest corner.
  static readonly float[] AOBrightness = { 1f, 0.82f, 0.66f, 0.52f };

  // How occluded one corner of column (fx, fz)'s top face is. (dx, dz)
  // (each ±1) picks which of the 4 corners: the two edge-adjacent
  // neighbours are (fx+dx, fz) and (fx, fz+dz), the diagonal one sharing
  // just that corner is (fx+dx, fz+dz). A neighbour taller than this
  // column is solid all the way up past our own top, so it shadows the
  // corner it shares with us — true from a plain heightmap even without
  // real 3D occupancy, since digging/height never leaves overhangs. The
  // "both edges occluded -> max, ignore the diagonal" rule is the standard
  // voxel-AO trick (see 0fps.net's writeup): it stops a corner already
  // boxed in by its two edges from being double-counted against the
  // diagonal too.
  float CornerBrightness(int fx, int fz, int height, int dx, int dz)
  {
    bool side1 = FineHeightOrZero(fx + dx, fz) > height;
    bool side2 = FineHeightOrZero(fx, fz + dz) > height;
    bool corner = FineHeightOrZero(fx + dx, fz + dz) > height;
    int occlusion = side1 && side2 ? 3 : (side1 ? 1 : 0) + (side2 ? 1 : 0) + (corner ? 1 : 0);
    return AOBrightness[occlusion];
  }

  static Color ColorFor(TileType t) => t switch
  {
    TileType.Grass => Color.Green,
    TileType.Dirt  => Color.Brown,
    TileType.Rock  => Color.DarkGray,
    _              => Color.Magenta // should never happen
  };
}
