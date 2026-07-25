namespace ColonySim.World;

// The water solver: a shallow-water "virtual pipes" model running on the
// fine voxel heightmap. Every fine column has a continuous water DEPTH
// sitting directly on its ground, and every EDGE between two columns has a
// signed FLUX — how much depth crosses it per tick. Water accelerates
// toward whichever side has the lower surface (ground + its own water) and
// keeps some of that speed between ticks, so it behaves like something
// with momentum rather than a puddle that instantly re-levels: a channel
// dug downhill actually develops a current, water rounds a corner instead
// of stopping dead at it, and opening a full reservoir produces a surge.
//
// Three things fall out of storing flux per edge rather than per direction:
//
//  - Mass is conserved exactly. A cell's change is
//    (flux in from west - flux out to east) + (same for Z), and the very
//    same float is one cell's outflow and its neighbour's inflow, so
//    nothing can be created or lost by asymmetric rounding.
//  - The step is order-independent. Each of the three passes below reads
//    only state the previous pass finished writing, so no cell ever sees a
//    half-updated neighbour and the result doesn't depend on which order
//    cells are visited. That makes the sim deterministic and reproducible
//    from a seed, which the old dictionary-order version was not.
//  - It parallelises cleanly if it ever needs to, since no pass writes
//    anything another cell in the same pass reads.
//
// Arrays are flat (indexed by hand) rather than 2D: the hot loops run over
// the whole active region several times a tick, and flat indexing skips
// .NET's per-access bounds checks on multidimensional arrays.
public sealed class WaterSim
{
  // How often the sim steps, in seconds. Everything below is tuned per
  // tick, so changing this alone changes how water behaves — Damping and
  // Accel have to move with it.
  public const float TickInterval = 0.05f;

  // How much of an edge's flux survives to the next tick. This is the
  // momentum: at 0 water re-levels instantly and stops the moment the
  // slope does (the old behaviour), at 1 it never loses speed and sloshes
  // forever. 0.82 gives a current that visibly carries around bends and
  // through flat stretches, while a disturbed lake still goes still in a
  // couple of seconds.
  const float Damping = 0.82f;

  // How hard a surface-height difference accelerates the water across an
  // edge. With Damping, an edge under a constant slope settles at roughly
  // Accel / (1 - Damping) = 2.2 depth-units per tick per unit of slope.
  const float Accel = 0.4f;

  // Hard ceiling on an edge's flux. Without it, water spilling off a tall
  // cliff builds a flux the terrain below can't absorb, which then takes
  // many ticks to damp back down and overshoots badly on the way. Capping
  // it caps the maximum flow speed, which is the stability limit that
  // actually matters here.
  const float MaxFlux = 4f;

  // The most of its own water a cell may hand out in one tick. The
  // limiter below already stops a cell going negative; keeping it under 1
  // additionally stops a column emptying completely in a single tick,
  // which is what produces checkerboard flicker on steep ground.
  const float MaxOutflowFraction = 0.6f;

  // Water shallower than this is dropped. This is what stops an
  // ever-thinner film from creeping outward forever: without it a single
  // spilled bucket eventually touches every column on the map at 1e-20
  // depth, and both the active region below and the shoreline pass would
  // degrade to whole-map work. The lost volume is bounded by this times
  // the number of wet columns, i.e. invisible.
  const float Epsilon = 0.003f;

  // How much a column's depth must differ from what its mesh was last
  // built with before that mesh is worth rebuilding. Purely a rendering
  // threshold — the sim itself always uses exact depths.
  const float VisualEpsilon = 0.01f;

  readonly int _w, _h;

  // The terrain this water sits on. Held by reference, not copied:
  // TileMap digs and deposits straight into this array, and the sim has to
  // see those edits on the very next tick without anything having to
  // remember to notify it.
  readonly int[,] _ground;

  readonly float[] _depth;      // [_w * _h] — water depth per column, in voxels

  // Flux is stored per edge, indexed by the cell on the +X (or +Z) side of
  // it, so there is one extra plane in each direction: index 0 is the edge
  // between off-map and column 0, index _w is the edge between column
  // _w-1 and off-map. Positive means flow in +X (or +Z). Handling the two
  // outer boundaries as ordinary edges is what makes water run off the
  // rim of the map without a separate special case.
  readonly float[] _fluxX;      // [(_w + 1) * _h]
  readonly float[] _fluxZ;      // [_w * (_h + 1)]

  readonly float[] _outScale;   // [_w * _h] — per-cell outflow limiter, scratch
  readonly float[] _shownDepth; // [_w * _h] — depth each column's mesh was last built with

  readonly int _chunkSize;
  readonly bool[,] _chunkDirty;

  // The bounding box of every wet column, so a still pond in one corner
  // doesn't cost a whole-map sweep. Water moves at most one column per
  // tick, so growing this by a small margin each step is enough to catch
  // wherever it's about to spread to.
  int _minX, _maxX, _minZ, _maxZ;

  // Columns that went from dry to wet this tick — TileMap's shoreline
  // muddying only needs to look at these, instead of re-walking a radius
  // around every wet column on the map every tick.
  readonly List<(int Fx, int Fz)> _newlyWet = new();
  public IReadOnlyList<(int Fx, int Fz)> NewlyWet => _newlyWet;

  public int ChunksX => _chunkDirty.GetLength(0);
  public int ChunksZ => _chunkDirty.GetLength(1);

  public WaterSim(int width, int height, int[,] ground, int chunkSize)
  {
    _w = width;
    _h = height;
    _ground = ground;
    _chunkSize = chunkSize;

    _depth = new float[_w * _h];
    _fluxX = new float[(_w + 1) * _h];
    _fluxZ = new float[_w * (_h + 1)];
    _outScale = new float[_w * _h];
    _shownDepth = new float[_w * _h];
    _chunkDirty = new bool[(_w + chunkSize - 1) / chunkSize, (_h + chunkSize - 1) / chunkSize];

    ClearActiveRegion();
  }

  int Idx(int fx, int fz) => fz * _w + fx;
  int IdxX(int ex, int fz) => fz * (_w + 1) + ex;
  int IdxZ(int fx, int ez) => ez * _w + fx;

  bool InBounds(int fx, int fz) => fx >= 0 && fz >= 0 && fx < _w && fz < _h;

  public float DepthAt(int fx, int fz) => InBounds(fx, fz) ? _depth[Idx(fx, fz)] : 0f;

  // Ground + water, i.e. the height the water's surface actually sits at —
  // the quantity flow is driven by, and the one that makes water level out
  // across uneven terrain rather than sit in equal-depth puddles.
  public float SurfaceAt(int fx, int fz) =>
    InBounds(fx, fz) ? _ground[fx, fz] + _depth[Idx(fx, fz)] : 0f;

  // --- Editing -----------------------------------------------------------

  public void AddWater(int fx, int fz, float amount)
  {
    if (!InBounds(fx, fz) || amount == 0f) return;
    int c = Idx(fx, fz);
    _depth[c] = MathF.Max(0f, _depth[c] + amount);
    MarkChunkDirtyAt(fx, fz);
    Touch(fx, fz);
  }

  // Sets a column's depth outright rather than adding to it — used when
  // restoring a saved map, where the loaded value already IS the absolute
  // depth rather than a delta to apply.
  public void SetDepth(int fx, int fz, float depth)
  {
    if (!InBounds(fx, fz)) return;
    _depth[Idx(fx, fz)] = MathF.Max(0f, depth);
    if (depth > 0f)
    {
      MarkChunkDirtyAt(fx, fz);
      Touch(fx, fz);
    }
  }

  // Brings a column into the active region even though nothing about its
  // water changed. TileMap calls this after digging or depositing, so that
  // a channel cut just beyond the current wet area is simulated on the
  // next tick instead of being skipped until water happens to reach it.
  public void Touch(int fx, int fz)
  {
    if (!InBounds(fx, fz)) return;
    if (fx < _minX) _minX = fx;
    if (fx > _maxX) _maxX = fx;
    if (fz < _minZ) _minZ = fz;
    if (fz > _maxZ) _maxZ = fz;
  }

  void ClearActiveRegion()
  {
    _minX = _w;
    _maxX = -1;
    _minZ = _h;
    _maxZ = -1;
  }

  bool ActiveRegionEmpty => _maxX < _minX || _maxZ < _minZ;

  // --- Rendering hand-off ------------------------------------------------

  public bool IsChunkDirty(int cx, int cz) => _chunkDirty[cx, cz];

  public void MarkAllChunksDirty()
  {
    for (int cx = 0; cx < ChunksX; cx++)
      for (int cz = 0; cz < ChunksZ; cz++)
        _chunkDirty[cx, cz] = true;
  }

  public void MarkChunkDirtyAt(int fx, int fz)
  {
    int cx = fx / _chunkSize, cz = fz / _chunkSize;
    if (cx >= 0 && cz >= 0 && cx < ChunksX && cz < ChunksZ) _chunkDirty[cx, cz] = true;
  }

  // Called once a chunk's water mesh has been rebuilt: records the depths
  // that mesh was actually built from, so the next tick compares against
  // what's on screen rather than against the last depth that happened to
  // cross the threshold. Without this, a column drifting by less than
  // VisualEpsilon per tick — a slowly filling basin — would drift
  // arbitrarily far from its drawn geometry and never trigger a rebuild.
  public void MarkChunkRendered(int cx, int cz)
  {
    _chunkDirty[cx, cz] = false;

    int fx0 = cx * _chunkSize, fz0 = cz * _chunkSize;
    int fx1 = Math.Min(fx0 + _chunkSize, _w);
    int fz1 = Math.Min(fz0 + _chunkSize, _h);

    for (int fz = fz0; fz < fz1; fz++)
      for (int fx = fx0; fx < fx1; fx++)
        _shownDepth[Idx(fx, fz)] = _depth[Idx(fx, fz)];
  }

  // --- The step ----------------------------------------------------------

  public void Step()
  {
    _newlyWet.Clear();
    if (ActiveRegionEmpty) return;

    // One tick can only push water one column further out, but flux at the
    // frontier lives one edge beyond the wet columns themselves, so the
    // working area is grown by two: enough for both, and enough that
    // leftover flux just outside a shrinking pool keeps getting damped
    // toward zero instead of being frozen mid-flow and reused later.
    int x0 = Math.Max(0, _minX - 2), x1 = Math.Min(_w - 1, _maxX + 2);
    int z0 = Math.Max(0, _minZ - 2), z1 = Math.Min(_h - 1, _maxZ + 2);

    AccelerateFlux(x0, x1, z0, z1);
    LimitOutflow(x0, x1, z0, z1);
    ApplyFlux(x0, x1, z0, z1);
  }

  // Pass 1: every edge in the working area accelerates toward its lower
  // side, keeping a Damping fraction of the speed it already had.
  //
  // An edge on the rim of the map is driven by the depth of the one column
  // it touches rather than by a surface difference. Treating the void as
  // "surface 0" instead would make a column of dry high ground behave as
  // though it were under enormous pressure to drain, building up a huge
  // flux that does nothing until water arrives and then flushes it off the
  // map instantly. Driving it by depth means the rim drains at a rate
  // proportional to how much water is actually standing on it, and doesn't
  // pull at all when it's dry.
  void AccelerateFlux(int x0, int x1, int z0, int z1)
  {
    for (int fz = z0; fz <= z1; fz++)
    {
      for (int ex = x0; ex <= x1 + 1; ex++)
      {
        bool leftIn = ex - 1 >= 0;
        bool rightIn = ex <= _w - 1;

        float dh;
        if (leftIn && rightIn) dh = SurfaceAt(ex - 1, fz) - SurfaceAt(ex, fz);
        else if (leftIn) dh = _depth[Idx(ex - 1, fz)];
        else dh = -_depth[Idx(ex, fz)];

        int fi = IdxX(ex, fz);
        _fluxX[fi] = Math.Clamp(_fluxX[fi] * Damping + Accel * dh, -MaxFlux, MaxFlux);
      }
    }

    for (int ez = z0; ez <= z1 + 1; ez++)
    {
      for (int fx = x0; fx <= x1; fx++)
      {
        bool nearIn = ez - 1 >= 0;
        bool farIn = ez <= _h - 1;

        float dh;
        if (nearIn && farIn) dh = SurfaceAt(fx, ez - 1) - SurfaceAt(fx, ez);
        else if (nearIn) dh = _depth[Idx(fx, ez - 1)];
        else dh = -_depth[Idx(fx, ez)];

        int fi = IdxZ(fx, ez);
        _fluxZ[fi] = Math.Clamp(_fluxZ[fi] * Damping + Accel * dh, -MaxFlux, MaxFlux);
      }
    }
  }

  // Pass 2: work out, for every cell, how much of the flow leaving it can
  // actually be honoured. Pass 1 sizes each edge from slope alone and has
  // no idea whether there's water there to move, so four steep neighbours
  // can easily ask a shallow puddle for more than it has. Computing every
  // cell's scale factor up front — before any of it is applied — is what
  // keeps the result independent of visiting order; scaling cell by cell
  // as we went would let whoever ran first drain the shared water.
  void LimitOutflow(int x0, int x1, int z0, int z1)
  {
    for (int fz = z0; fz <= z1; fz++)
    {
      for (int fx = x0; fx <= x1; fx++)
      {
        int c = Idx(fx, fz);
        float d = _depth[c];

        float outflow =
          MathF.Max(0f, -_fluxX[IdxX(fx, fz)]) +      // west edge, flowing -X
          MathF.Max(0f, _fluxX[IdxX(fx + 1, fz)]) +   // east edge, flowing +X
          MathF.Max(0f, -_fluxZ[IdxZ(fx, fz)]) +      // near edge, flowing -Z
          MathF.Max(0f, _fluxZ[IdxZ(fx, fz + 1)]);    // far edge, flowing +Z

        _outScale[c] = outflow > 1e-6f
          ? MathF.Min(1f, d * MaxOutflowFraction / outflow)
          : 1f;
      }
    }
  }

  // Pass 3: scale each edge by the limiter of whichever cell it drains,
  // then move the water. The scaled value is stored back rather than used
  // and discarded, so an edge pulling at a column that has nothing left
  // doesn't keep its imaginary momentum and dump it the instant water
  // shows up.
  //
  // An edge whose SOURCE lies outside the working region is cut to zero
  // rather than scaled. Only cells inside the region had a limiter
  // computed for them this tick, so scaling by whatever happens to be left
  // in _outScale from the last time that cell was simulated would let a
  // stale value through — and since the cell is also not having its own
  // depth updated, water crossing that edge would be added to the receiver
  // without ever being taken from the giver. That fabricates water, which
  // is far worse than the alternative: the region always extends two
  // columns past the last wet one, so a source outside it is dry and has
  // nothing to legitimately contribute anyway.
  void ApplyFlux(int x0, int x1, int z0, int z1)
  {
    for (int fz = z0; fz <= z1; fz++)
    {
      for (int ex = x0; ex <= x1 + 1; ex++)
      {
        int fi = IdxX(ex, fz);
        float f = _fluxX[fi];
        if (f > 0f) f = ex - 1 >= x0 ? f * _outScale[Idx(ex - 1, fz)] : 0f;
        else if (f < 0f) f = ex <= x1 ? f * _outScale[Idx(ex, fz)] : 0f;
        _fluxX[fi] = f;
      }
    }

    for (int ez = z0; ez <= z1 + 1; ez++)
    {
      for (int fx = x0; fx <= x1; fx++)
      {
        int fi = IdxZ(fx, ez);
        float f = _fluxZ[fi];
        if (f > 0f) f = ez - 1 >= z0 ? f * _outScale[Idx(fx, ez - 1)] : 0f;
        else if (f < 0f) f = ez <= z1 ? f * _outScale[Idx(fx, ez)] : 0f;
        _fluxZ[fi] = f;
      }
    }

    ClearActiveRegion();

    for (int fz = z0; fz <= z1; fz++)
    {
      for (int fx = x0; fx <= x1; fx++)
      {
        int c = Idx(fx, fz);
        float before = _depth[c];

        float net =
          _fluxX[IdxX(fx, fz)] - _fluxX[IdxX(fx + 1, fz)] +
          _fluxZ[IdxZ(fx, fz)] - _fluxZ[IdxZ(fx, fz + 1)];

        float d = before + net;
        if (d < Epsilon) d = 0f;

        if (d != before)
        {
          _depth[c] = d;
          if (before == 0f && d > 0f) _newlyWet.Add((fx, fz));
          if (MathF.Abs(d - _shownDepth[c]) > VisualEpsilon) MarkChunkDirtyAt(fx, fz);
        }

        if (d > 0f) Touch(fx, fz);
      }
    }
  }

  // Total water on the map, in voxel-volumes — drives the on-screen
  // readout, and is the figure to watch when checking that a sealed basin
  // neither gains nor loses water.
  //
  // Accumulated in double rather than float. Tens of thousands of small
  // depths summed into a float accumulator lose enough precision at a
  // total in the tens of thousands that the reading wobbles by a tenth of
  // a unit between ticks on its own — which is larger than the per-tick
  // changes anyone would be reading this number to see.
  public float TotalVolume()
  {
    if (ActiveRegionEmpty) return 0f;

    double total = 0;
    for (int fz = _minZ; fz <= _maxZ; fz++)
      for (int fx = _minX; fx <= _maxX; fx++)
        total += _depth[Idx(fx, fz)];
    return (float)total;
  }
}
