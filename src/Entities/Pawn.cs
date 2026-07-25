using System.Numerics;
using Raylib_cs;
using ColonySim.World;

namespace ColonySim.Entities;

public class Pawn
{
  // The tile the pawn currently occupies.
  public int TileX { get; private set; }
  public int TileZ { get; private set; }

  // Whether this pawn is part of the current selection (for input + the
  // selection ring drawn under it).
  public bool Selected { get; set; }

  public bool IsMoving => _path.Count > 0;

  // The final tile this pawn is trying to reach, if it's moving — kept
  // around so a stuck pawn can ask the pathfinder for a fresh route to the
  // same place instead of just giving up.
  public (int X, int Y)? FinalDestination { get; private set; }

  // True once this pawn has gone too long without closing distance on its
  // current waypoint — stuck against something a shove alone won't clear
  // (typically a pawn that went idle mid-route). Program.cs watches this and
  // re-plans a route for it.
  public bool Stuck { get; private set; }

  // What this pawn is carrying.
  public Inventory Inventory { get; } = new();

  public Vector3 WorldPos => _worldPos;

  // Smooth world position, so the pawn glides between tiles (and up/down
  // slopes) instead of teleporting.
  Vector3 _worldPos;

  readonly Queue<(int X, int Y)> _path = new();
  readonly TileMap _map;

  // Movement speed in world units per second.
  const float Speed = 90f;

  // Progress tracking for stuck detection: the closest we've gotten to the
  // current waypoint so far, and how long it's been since that improved.
  float _bestDistToWaypoint = float.MaxValue;
  float _stuckTimer;
  const float ProgressEpsilon = 1f; // world units; ignores float/push jitter
  const float StuckTimeout = 1.5f; // no progress this long -> ask for a new route

  // Total time stuck across repeated auto-reroute attempts (NOT reset by
  // Reroute — only by real progress or a fresh manual SetPath). Once this
  // runs out, the pawn gives up entirely instead of retrying forever.
  float _totalStuckTime;
  const float GiveUpTimeout = 6f;

  // The pawn is drawn as a simple ball resting on the ground.
  public const float Radius = TileMap.TileSize * 0.35f;

  // A real hop — genuinely leaves the ground (_worldPos.Y actually rises),
  // not just a cosmetic draw-time offset. First step toward pawns not being
  // strictly bound to "always exactly on the ground".
  bool _jumping;
  float _jumpTimer;
  const float JumpDuration = 0.45f;
  const float JumpHeight = TileMap.TileSize * 0.8f;

  public Pawn(TileMap map, int tileX, int tileZ)
  {
    _map = map;
    TileX = tileX;
    TileZ = tileZ;
    _worldPos = TileTop(tileX, tileZ);
  }

  // The world position resting on top of the ground at the centre of a
  // (pathfinding) tile. Samples the real fine-voxel terrain height, not
  // just the tile's coarse representative height, so a pawn's feet always
  // match the actual rendered surface it's standing on.
  Vector3 TileTop(int x, int z)
  {
    float worldX = x * TileMap.TileSize + TileMap.TileSize / 2f;
    float worldZ = z * TileMap.TileSize + TileMap.TileSize / 2f;
    return new Vector3(worldX, _map.SmoothSurfaceY(worldX, worldZ) + Radius, worldZ);
  }

  // Replace the current route with a fresh one from a direct order (a click,
  // not an automatic reroute) — always gives the pawn a full stuck budget,
  // even if it had been struggling before. Passing an empty (or single-tile)
  // path just stops the pawn where it is.
  public void SetPath(List<(int X, int Y)> path)
  {
    ApplyPath(path);
    _totalStuckTime = 0f;
  }

  // Same as SetPath, but for Program's automatic re-route of a Stuck pawn:
  // it keeps counting toward GiveUpTimeout instead of refilling it, so
  // repeatedly failing to find a way through eventually gives up for good
  // rather than retrying forever.
  public void Reroute(List<(int X, int Y)> path) => ApplyPath(path);

  void ApplyPath(List<(int X, int Y)> path)
  {
    _path.Clear();
    // path[0] is the tile we're already standing on, so start at index 1.
    for (int i = 1; i < path.Count; i++)
      _path.Enqueue(path[i]);

    FinalDestination = _path.Count > 0 ? path[^1] : null;
    ResetProgress();
  }

  void ResetProgress()
  {
    _bestDistToWaypoint = float.MaxValue;
    _stuckTimer = 0f;
    Stuck = false;
  }

  // Seeks continuously toward the next waypoint every frame — no hard
  // per-tile locking. Other pawns are kept from overlapping by a separate
  // soft push pass (see Program.ResolveOverlaps), so a crowd shoulders its
  // way past itself instead of queuing rigidly. If a push (or some obstacle)
  // keeps this pawn from actually closing distance for a while, it flags
  // itself Stuck so Program can ask the pathfinder for a new route; if that
  // keeps failing for GiveUpTimeout seconds straight, it cancels the order
  // entirely rather than retrying forever.
  //
  // Horizontal seeking only ever looks at X/Z — Y is handled completely
  // separately below, as "whatever the ground is doing right here, plus the
  // jump arc". That split means neither a slope nor a jump ever distorts
  // the other: a jump can't make horizontal progress look closer/farther
  // than it is, and the ground height is always current with no separate
  // "re-snap" step needed even the instant the ground under the pawn changes.
  public void Update(float dt)
  {
    UpdateJump(dt);

    if (_path.Count == 0)
    {
      ResetProgress();
      _totalStuckTime = 0f;
    }
    else
    {
      var next = _path.Peek();
      float targetX = next.X * TileMap.TileSize + TileMap.TileSize / 2f;
      float targetZ = next.Y * TileMap.TileSize + TileMap.TileSize / 2f;

      float dx = targetX - _worldPos.X;
      float dz = targetZ - _worldPos.Z;
      float dist = MathF.Sqrt(dx * dx + dz * dz);
      float step = Speed * dt;

      if (dist <= step)
      {
        // Close enough to count as "arrived": snap onto the tile and pop it.
        _worldPos.X = targetX;
        _worldPos.Z = targetZ;
        TileX = next.X;
        TileZ = next.Y;
        _path.Dequeue();
        ResetProgress();
        _totalStuckTime = 0f;
      }
      else
      {
        _worldPos.X += dx / dist * step;
        _worldPos.Z += dz / dist * step;

        if (dist < _bestDistToWaypoint - ProgressEpsilon)
        {
          // Real progress — both the short retry timer and the give-up
          // budget refill, since whatever was in the way is evidently no
          // longer stuck.
          _bestDistToWaypoint = dist;
          _stuckTimer = 0f;
          _totalStuckTime = 0f;
        }
        else
        {
          _stuckTimer += dt;
          _totalStuckTime += dt;
        }

        Stuck = _stuckTimer >= StuckTimeout;

        if (_totalStuckTime >= GiveUpTimeout)
        {
          // Been stuck through repeated reroutes for too long — stop trying.
          _path.Clear();
          FinalDestination = null;
          ResetProgress();
          _totalStuckTime = 0f;
        }
      }
    }

    _worldPos.Y = _map.SmoothSurfaceY(_worldPos.X, _worldPos.Z) + Radius + JumpArc();
  }

  // Shoves the pawn sideways by a small amount; used by the separation pass
  // to keep pawns from overlapping. Never touches Y — height always comes
  // from the terrain under the pawn's seek movement above.
  public void Nudge(Vector2 xzDelta) => _worldPos += new Vector3(xzDelta.X, 0f, xzDelta.Y);

  // Kicks off a little hop, if one isn't already playing. Works whether the
  // pawn is standing still or mid-walk.
  public void Jump()
  {
    if (_jumping) return;
    _jumping = true;
    _jumpTimer = 0f;
  }

  void UpdateJump(float dt)
  {
    if (!_jumping) return;
    _jumpTimer += dt;
    if (_jumpTimer >= JumpDuration) _jumping = false;
  }

  // A sine arc: 0 at takeoff and landing, peaking at JumpHeight halfway
  // through. Added onto _worldPos.Y directly in Update — this is a real
  // height, not a rendering trick.
  float JumpArc() => _jumping
    ? MathF.Sin(MathF.Min(_jumpTimer / JumpDuration, 1f) * MathF.PI) * JumpHeight
    : 0f;

  // The lit fill. Draw this inside a lighting shader's BeginMode/EndMode.
  public void DrawSolid()
  {
    // Draw the remaining path as small dots so you can see the plan.
    foreach (var (x, y) in _path)
    {
      Vector3 top = TileTop(x, y);
      Raylib.DrawSphere(new Vector3(top.X, top.Y - Radius + 2f, top.Z), 3f, Color.Yellow);
    }

    // Draw the pawn itself as a ball.
    Raylib.DrawSphere(_worldPos, Radius, Color.Red);
  }

  // Just the ball, no path-plan dots — used for the shadow depth pass,
  // where those tiny dots would only cost render time on shadows nobody's
  // meant to see. Colour is irrelevant here (only depth is recorded).
  public void DrawShadowCaster() => Raylib.DrawSphere(_worldPos, Radius, Color.White);

  // A faint outline on the pawn, plus a ring on the ground if it's selected.
  // Wireframes carry no normals, so keep this outside any lighting shader —
  // draw with the default shader.
  public void DrawOutline()
  {
    Raylib.DrawSphereWires(_worldPos, Radius, 8, 8, Color.Maroon);

    if (Selected)
    {
      // The selection ring stays on the ground even mid-jump, so it still
      // reads as "this tile" rather than following the pawn into the air.
      Vector3 ringCenter = new(_worldPos.X, _map.SurfaceY(TileX, TileZ) + 1f, _worldPos.Z);
      Raylib.DrawCircle3D(ringCenter, Radius * 1.6f, new Vector3(1f, 0f, 0f), 90f, Color.Lime);
    }
  }
}
