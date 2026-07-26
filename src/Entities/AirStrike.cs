using System.Numerics;
using Raylib_cs;
using ColonySim.World;

namespace ColonySim.Entities;

// A player-called-in bombardment: a falling streak of light converges on a
// targeted voxel, then blasts a shallow crater out of the ground around it —
// see Program.TryPlaceArmedTool (ToolKind.AirStrike), which is the only
// place these get created. Every voxel the blast frees is handed to
// TileMap.DigVoxel exactly like a normal Dig task would, so the same
// Grass/Dirt-only, no-Rock/no-obstacle/no-deep-water rule already enforced
// by CanDigVoxel (see its own doc comment) applies here too — the strike
// can never touch anything a builder couldn't otherwise dig by hand. Each
// freed voxel then flies up and out in its own little arc and, on landing,
// turns into the same ItemDrop a manual dig leaves behind, so cleanup after
// a strike works exactly like cleanup after digging: an actor just has to
// walk over the debris. Not persisted through SaveSystem — like ItemDrop,
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
  public const int BlastRadiusVoxels = 5;

  // How many voxels deep each affected column gets dug, capped by whatever
  // CanDigVoxel still allows once a column bottoms out on Rock or empty
  // ground short of that.
  const int CraterDepth = 2;

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

  public void Update(float dt, TileMap map, List<ItemDrop> itemDrops)
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
          itemDrops.Add(new ItemDrop("Dirt", 1, _debris[i].LandingPos));
          _debris.RemoveAt(i);
        }
        if (_debris.Count == 0) _phase = Phase.Done;
        break;
    }
  }

  // Digs every diggable column within BlastRadiusVoxels (a circle, not a
  // square footprint — this isn't anchored to any building's shape) up to
  // CraterDepth layers deep, launching a Debris chunk for each voxel
  // actually removed. Columns that aren't diggable at all (Rock, an
  // obstacle, deep water — same checks CanDigVoxel always applies) are
  // silently skipped rather than the strike failing outright, same as a
  // Dig drag-select over mixed terrain today.
  void Detonate(TileMap map)
  {
    var rng = Random.Shared;
    int r2 = BlastRadiusVoxels * BlastRadiusVoxels;

    for (int dz = -BlastRadiusVoxels; dz <= BlastRadiusVoxels; dz++)
    {
      for (int dx = -BlastRadiusVoxels; dx <= BlastRadiusVoxels; dx++)
      {
        if (dx * dx + dz * dz > r2) continue;

        int fx = _targetFx + dx;
        int fz = _targetFz + dz;

        for (int layer = 0; layer < CraterDepth; layer++)
        {
          if (!map.CanDigVoxel(fx, fz)) break;

          float worldX = fx * TileMap.VoxelSize + TileMap.VoxelSize / 2f;
          float worldZ = fz * TileMap.VoxelSize + TileMap.VoxelSize / 2f;
          Vector3 launchPos = new(worldX, map.SmoothSurfaceY(worldX, worldZ), worldZ);

          if (map.DigVoxel(fx, fz) == 0) break;

          // Scatter the landing spot a little so debris doesn't all pile
          // straight back down into the hole it just came out of.
          float scatterAngle = rng.NextSingle() * MathF.Tau;
          float scatterDist = rng.NextSingle() * TileMap.VoxelSize * 3f;
          float landX = worldX + MathF.Cos(scatterAngle) * scatterDist;
          float landZ = worldZ + MathF.Sin(scatterAngle) * scatterDist;
          Vector3 landingPos = new(landX, map.SmoothSurfaceY(landX, landZ), landZ);

          _debris.Add(new Debris(launchPos, landingPos));
        }
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
  // it lands — a simple lerp across XZ with a sine arc added on top for
  // height, same shape as Actor's own jump arc.
  class Debris
  {
    public Vector3 LandingPos { get; }

    readonly Vector3 _start;
    readonly float _duration;
    readonly float _arcHeight;
    float _t;

    static readonly Color DirtColor = new(120, 84, 52, 255);
    const float Size = TileMap.TileSize * 0.22f;

    public Debris(Vector3 start, Vector3 landingPos)
    {
      _start = start;
      LandingPos = landingPos;
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
      Vector3 pos = Vector3.Lerp(_start, LandingPos, t);
      pos.Y += MathF.Sin(t * MathF.PI) * _arcHeight;
      Raylib.DrawCube(pos, Size, Size, Size, DirtColor);
    }
  }
}
