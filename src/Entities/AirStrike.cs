using System.Numerics;
using Raylib_cs;
using ColonySim.World;

namespace ColonySim.Entities;

// A player-called-in bombardment: a falling streak of light converges on a
// targeted voxel, then blasts a bowl-shaped crater out of the ground around
// it — see Program.TryPlaceArmedTool (ToolKind.AirStrike), which is the only
// place these get created. Unlike a hand dig, the blast doesn't care what
// it hits: every tree/bush/campfire/spring/light post caught in the radius
// is destroyed outright (see TileMap.DestroyObstaclesIn), and literally
// every column short of standing water gets cratered regardless of material
// (see TileMap.BlastVoxel) — water is left alone and just flows into the
// hole afterward, same as it would after a hand-dug channel. The displaced
// volume doesn't vanish as item drops any more: it's piled back onto the
// terrain as a mound around the crater's outer rim (see PileRim), sloped
// gently enough on both its inner and outer edges — same as the bowl itself
// — that an actor can walk down into the crater and back out again, rather
// than hitting a wall. Not persisted through SaveSystem — like ItemDrop,
// it's meant to be short-lived, and a save mid-strike is expected to just
// lose it.
public class AirStrike
{
  enum Phase { Incoming, Airborne, Done }

  Phase _phase = Phase.Incoming;
  float _timer;
  float _flashTimer;

  readonly int _targetFx, _targetFz;
  readonly Vector3 _groundPos;
  readonly List<Debris> _debris = new();

  public bool Done => _phase == Phase.Done;

  // How far out (in fine voxels) the crater reaches from the targeted
  // voxel — public so Program's hover preview can draw the same radius the
  // strike will actually use.
  public const int BlastRadiusVoxels = 25;

  // How deep the bowl gets at its very centre — tapering down to (but never
  // below) a single voxel at BlastRadiusVoxels, see Detonate's depth
  // profile: every column in the blast circle short of standing water gets
  // dug by at least this much.
  const int CraterDepth = 10;

  // How far past BlastRadiusVoxels the ejecta mound spreads — a smooth
  // rise-and-fall across this width, zero height at both ends, see PileRim.
  const int RimWidthVoxels = 10;

  // Only this fraction of what the bowl actually displaces gets piled back
  // onto the rim — a real impact throws most of its ejecta well clear of
  // the crater (dust, scatter, compaction), and depositing the full volume
  // made the mound read as an unrealistically tall wall of dirt. The rest
  // is simply removed from the game, same as it always was pre-explosion.
  const float MoundVolumeFraction = 0.1f;

  const float IncomingDuration = 0.35f;
  const float FlashDuration = 0.25f;

  // The streak's start altitude and the length of its tapering trail —
  // tuned so it's already well off the top of the screen at typical camera
  // zoom, and closes the remaining distance fast enough to read as
  // "falling", not "drifting".
  const float BeamStartHeight = TileMap.TileSize * 40f;
  const float TrailLength = TileMap.TileSize * 6f;
  static readonly Color BeamColor = new(255, 235, 170, 235);
  static readonly Color FlashColor = new(255, 200, 120, 255);

  public AirStrike(TileMap map, int targetFx, int targetFz)
  {
    _targetFx = targetFx;
    _targetFz = targetFz;
    float worldX = targetFx * TileMap.VoxelSize + TileMap.VoxelSize / 2f;
    float worldZ = targetFz * TileMap.VoxelSize + TileMap.VoxelSize / 2f;
    _groundPos = new Vector3(worldX, map.SmoothSurfaceY(worldX, worldZ), worldZ);
  }

  public void Update(float dt, TileMap map)
  {
    _timer += dt;
    if (_flashTimer > 0f) _flashTimer = Math.Max(0f, _flashTimer - dt);

    switch (_phase)
    {
      case Phase.Incoming:
        if (_timer >= IncomingDuration)
        {
          Detonate(map);
          _flashTimer = FlashDuration;
          _phase = _debris.Count > 0 ? Phase.Airborne : Phase.Done;
        }
        break;

      case Phase.Airborne:
        for (int i = _debris.Count - 1; i >= 0; i--)
        {
          if (!_debris[i].Update(dt)) continue;
          _debris.RemoveAt(i);
        }
        if (_debris.Count == 0) _phase = Phase.Done;
        break;
    }
  }

  // Carves the crater, destroys anything caught in the blast, and piles the
  // displaced volume onto the rim. Two passes over the blast circle:
  //   1. Wipe out every tree/bush/campfire/spring/light post overlapping it.
  //   2. Dig a bowl — depth tapers from CraterDepth at the centre down to a
  //      single voxel at the radius, so literally every column short of
  //      standing water gets touched, and the slope from undisturbed ground
  //      into the crater is never more than a voxel or two per step (an
  //      actor can always walk down into it). Wet columns are skipped
  //      entirely; water floods in on its own.
  // Then PileRim mounds the dug volume onto the rim.
  void Detonate(TileMap map)
  {
    int r2 = BlastRadiusVoxels * BlastRadiusVoxels;

    var craterColumns = new List<(int Fx, int Fz)>();
    for (int dz = -BlastRadiusVoxels; dz <= BlastRadiusVoxels; dz++)
    {
      for (int dx = -BlastRadiusVoxels; dx <= BlastRadiusVoxels; dx++)
      {
        if (dx * dx + dz * dz > r2) continue;
        craterColumns.Add((_targetFx + dx, _targetFz + dz));
      }
    }

    map.DestroyObstaclesIn(craterColumns);

    var dugColumns = new List<Vector3>(); // pre-dig surface positions, for debris launch points
    int totalDug = 0;
    foreach (var (fx, fz) in craterColumns)
    {
      float dx = fx - _targetFx, dz = fz - _targetFz;
      int depth = (int)MathF.Max(1f, MathF.Round(CraterDepth * (1f - (dx * dx + dz * dz) / r2)));

      float worldX = fx * TileMap.VoxelSize + TileMap.VoxelSize / 2f;
      float worldZ = fz * TileMap.VoxelSize + TileMap.VoxelSize / 2f;
      bool any = false;
      for (int layer = 0; layer < depth; layer++)
      {
        if (map.BlastVoxel(fx, fz) == 0) break;
        totalDug++;
        any = true;
      }
      if (any) dugColumns.Add(new Vector3(worldX, map.SmoothSurfaceY(worldX, worldZ), worldZ));
    }

    int moundVolume = (int)MathF.Round(totalDug * MoundVolumeFraction);
    if (moundVolume > 0 && dugColumns.Count > 0) PileRim(map, moundVolume, dugColumns);
  }

  // Mounds moundVolume to the ring just outside the crater as a smooth
  // sine-shaped rise and fall: zero height right at the crater's own edge,
  // climbing to a peak roughly midway across RimWidthVoxels, then tapering
  // back to zero at the outer edge — a gentle slope on both sides of the
  // heap, instead of a wall dropped flush against the hole. Deterministic
  // (a pure function of distance, like the crater's own bowl) rather than
  // randomly scattered, so neighbouring columns never differ by more than
  // the shape itself calls for. peakHeight is solved from moundVolume so
  // the mound roughly conserves it; a column that's wet, obstructed, or
  // already at the height cap just doesn't take its share, so a little
  // volume can go missing rather than pile up elsewhere.
  void PileRim(TileMap map, int moundVolume, List<Vector3> dugColumns)
  {
    var rng = Random.Shared;
    int rimOuter = BlastRadiusVoxels + RimWidthVoxels;

    var ring = new List<(int Fx, int Fz, float Shape)>();
    float shapeSum = 0f;
    for (int dz = -rimOuter; dz <= rimOuter; dz++)
    {
      for (int dx = -rimOuter; dx <= rimOuter; dx++)
      {
        float r = MathF.Sqrt(dx * dx + dz * dz);
        if (r <= BlastRadiusVoxels || r > rimOuter) continue;
        float t = (r - BlastRadiusVoxels) / RimWidthVoxels; // 0 at the crater edge, 1 at the outer edge
        float shape = MathF.Sin(MathF.PI * t); // 0..1, zero at both ends
        if (shape <= 0f) continue;
        ring.Add((_targetFx + dx, _targetFz + dz, shape));
        shapeSum += shape;
      }
    }
    if (shapeSum <= 0f) return;

    float peakHeight = moundVolume / shapeSum;

    foreach (var (fx, fz, shape) in ring)
    {
      int height = (int)MathF.Round(peakHeight * shape);
      if (height <= 0) continue;

      float worldX = fx * TileMap.VoxelSize + TileMap.VoxelSize / 2f;
      float worldZ = fz * TileMap.VoxelSize + TileMap.VoxelSize / 2f;
      for (int i = 0; i < height; i++)
      {
        if (!map.DepositVoxel(fx, fz)) break;

        Vector3 landingPos = new(worldX, map.SmoothSurfaceY(worldX, worldZ), worldZ);
        Vector3 launchPos = dugColumns[rng.Next(dugColumns.Count)];
        _debris.Add(new Debris(launchPos, landingPos));
      }
    }
  }

  // Unlit — the incoming streak and impact flash are light sources in their
  // own right, same reasoning as SunLight's sun/moon and the campfire flame
  // (see TileMap.DrawCampfiresGlow): call this before BeginLit, not inside it.
  public void DrawUnlit()
  {
    if (_phase == Phase.Incoming)
    {
      float t = Math.Clamp(_timer / IncomingDuration, 0f, 1f);
      Vector3 head = _groundPos + new Vector3(0f, BeamStartHeight * (1f - t), 0f);
      Vector3 tail = head + new Vector3(0f, TrailLength, 0f);
      Raylib.DrawCylinderEx(tail, head, 1f, 3.5f, 6, BeamColor);
    }

    if (_flashTimer > 0f)
    {
      float t = _flashTimer / FlashDuration; // 1 at impact, fading to 0
      float radius = TileMap.VoxelSize * (BlastRadiusVoxels * 0.5f) * (1f - t) + TileMap.VoxelSize;
      var color = new Color(FlashColor.R, FlashColor.G, FlashColor.B, (byte)(FlashColor.A * t));
      Raylib.DrawSphere(_groundPos + new Vector3(0f, radius * 0.3f, 0f), radius, color);
    }
  }

  // Lit — the actual flying dirt chunks are ordinary shaded geometry, drawn
  // the same pass as actors/ItemDrops (see Program.Draw).
  public void DrawLit()
  {
    foreach (var d in _debris) d.Draw();
  }

  // One dug voxel's flight from where it came out of the ground to wherever
  // it lands on the rim heap — a simple lerp across XZ with a sine arc added
  // on top for height, same shape as Actor's own jump arc. Purely cosmetic:
  // the terrain underneath is already carved/piled the instant Detonate
  // runs, so unlike a manual dig this never leaves anything behind to pick
  // up once it lands.
  class Debris
  {
    readonly Vector3 _start;
    readonly Vector3 _end;
    readonly float _duration;
    readonly float _arcHeight;
    float _t;

    static readonly Color DirtColor = new(120, 84, 52, 255);
    const float Size = TileMap.TileSize * 0.22f;

    public Debris(Vector3 start, Vector3 end)
    {
      _start = start;
      _end = end;
      _duration = 0.45f + Random.Shared.NextSingle() * 0.3f;
      _arcHeight = TileMap.TileSize * (1.1f + Random.Shared.NextSingle() * 0.9f);
    }

    // Returns true once this chunk has landed.
    public bool Update(float dt)
    {
      _t += dt / _duration;
      return _t >= 1f;
    }

    public void Draw()
    {
      float t = Math.Clamp(_t, 0f, 1f);
      Vector3 pos = Vector3.Lerp(_start, _end, t);
      pos.Y += MathF.Sin(t * MathF.PI) * _arcHeight;
      Raylib.DrawCube(pos, Size, Size, Size, DirtColor);
    }
  }
}
