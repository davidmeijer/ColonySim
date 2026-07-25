using System.Numerics;
using Raylib_cs;
using ColonySim.World;

namespace ColonySim.Entities;

public class Actor
{
  // The tile the actor currently occupies.
  public int TileX { get; private set; }
  public int TileZ { get; private set; }

  // Whether this actor is part of the current selection (for input + the
  // selection ring drawn under it).
  public bool Selected { get; set; }

  public bool IsMoving => _path.Count > 0;

  // The final tile this actor is trying to reach, if it's moving — kept
  // around so a stuck actor can ask the pathfinder for a fresh route to the
  // same place instead of just giving up.
  public (int X, int Y)? FinalDestination { get; private set; }

  // True once this actor has gone too long without closing distance on its
  // current waypoint — stuck against something a shove alone won't clear
  // (typically an actor that went idle mid-route). Program.cs watches this
  // and re-plans a route for it.
  public bool Stuck { get; private set; }

  // What this actor is carrying.
  public Inventory Inventory { get; } = new();

  // Position of the actor's feet on the ground (plus any jump arc) — the
  // root everything else (body parts, camera picking, overlap pushes) is
  // measured from.
  public Vector3 WorldPos => _worldPos;

  // Smooth world position, so the actor glides between tiles (and up/down
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
  // runs out, the actor gives up entirely instead of retrying forever.
  float _totalStuckTime;
  const float GiveUpTimeout = 6f;

  // Horizontal-only footprint radius: how close two actors are allowed to
  // get before ResolveOverlaps shoves them apart, and the radius the
  // selection ring is sized from. Not a vertical offset any more — the
  // actor's feet sit exactly on the ground; see the body layout below for
  // how tall it actually stands.
  public const float Radius = TileMap.TileSize * 0.22f;

  // A real hop — genuinely leaves the ground (_worldPos.Y actually rises),
  // not just a cosmetic draw-time offset.
  bool _jumping;
  float _jumpTimer;
  const float JumpDuration = 0.45f;
  const float JumpHeight = TileMap.TileSize * 0.8f;

  // --- Body layout -----------------------------------------------------
  // A small blocky humanoid built from six boxes (torso, head, two legs,
  // two arms), proportioned relative to TileSize so an actor reads as
  // roughly person-sized next to a tile. Kept in the same voxel-primitive
  // style as the rest of the world instead of a loaded/rigged mesh.
  const float LegHeight = TileMap.TileSize * 0.42f;
  const float TorsoHeight = TileMap.TileSize * 0.34f;
  const float HeadSize = TileMap.TileSize * 0.26f;
  const float TorsoWidth = TileMap.TileSize * 0.34f;
  const float TorsoDepth = TileMap.TileSize * 0.20f;
  const float HipOffsetX = TileMap.TileSize * 0.11f;
  const float LegThickness = TileMap.TileSize * 0.11f;
  const float ShoulderOffsetX = TileMap.TileSize * 0.20f;
  const float ArmLength = TileMap.TileSize * 0.34f;
  const float ArmThickness = TileMap.TileSize * 0.09f;

  static readonly Color SkinColor = new(224, 172, 132, 255);
  static readonly Color PantsColor = new(64, 60, 58, 255);

  // Each new actor gets the next colour in the list, so a group reads as
  // distinct individuals instead of an identical crowd.
  static readonly Color[] ShirtPalette =
  {
    new(196, 64, 64, 255), new(64, 120, 196, 255), new(196, 160, 48, 255),
    new(80, 160, 90, 255), new(150, 90, 170, 255), new(210, 130, 60, 255),
  };
  static int _nextShirtIndex;
  readonly Color _shirtColor;

  // --- Animation state ---------------------------------------------------
  // Which way the actor is facing, smoothed toward the direction it's
  // actually moving rather than snapping — so turns read as a turn.
  float _heading;
  static readonly float TurnRateRadPerSec = MathF.PI * 3f;

  // Gait cycle: phase only advances while actually moving; the blend eases
  // the swing amplitude in/out so stopping mid-stride doesn't snap the
  // limbs straight back to a neutral pose.
  float _walkPhase;
  float _walkBlend;
  const float WalkCycleRate = 9f; // radians of phase per second
  const float WalkSwingDeg = 30f;
  const float ArmSwingScale = 0.8f;
  const float WalkBlendRate = 10f;
  const float JumpLegTuckDeg = 25f;

  bool _digging;
  float _digTimer;
  const float DigDuration = 0.5f;
  const float DigRaiseDeg = 50f;
  const float DigSwingDeg = 70f;

  bool _depositing;
  float _depositTimer;
  const float DepositDuration = 0.5f;
  const float DepositLeanDeg = 18f;
  const float DepositArmDeg = 55f;

  // Cached per-frame pose, computed once in Update() and read by all three
  // Draw* methods so they agree on where the limbs are this frame.
  float _poseLeftLegDeg, _poseRightLegDeg, _poseLeftArmDeg, _poseRightArmDeg, _poseTorsoLeanDeg;

  public Actor(TileMap map, int tileX, int tileZ)
  {
    _map = map;
    TileX = tileX;
    TileZ = tileZ;
    _worldPos = TileTop(tileX, tileZ);
    _shirtColor = ShirtPalette[_nextShirtIndex++ % ShirtPalette.Length];
  }

  // The ground position at the centre of a (pathfinding) tile — where this
  // actor's feet belong. Samples the real fine-voxel terrain height, not
  // just the tile's coarse representative height, so an actor's feet always
  // match the actual rendered surface it's standing on.
  Vector3 TileTop(int x, int z)
  {
    float worldX = x * TileMap.TileSize + TileMap.TileSize / 2f;
    float worldZ = z * TileMap.TileSize + TileMap.TileSize / 2f;
    return new Vector3(worldX, _map.SmoothSurfaceY(worldX, worldZ), worldZ);
  }

  // Replace the current route with a fresh one from a direct order (a click,
  // not an automatic reroute) — always gives the actor a full stuck budget,
  // even if it had been struggling before. Passing an empty (or single-tile)
  // path just stops the actor where it is.
  public void SetPath(List<(int X, int Y)> path)
  {
    ApplyPath(path);
    _totalStuckTime = 0f;
  }

  // Same as SetPath, but for Program's automatic re-route of a Stuck actor:
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
  // per-tile locking. Other actors are kept from overlapping by a separate
  // soft push pass (see Program.ResolveOverlaps), so a crowd shoulders its
  // way past itself instead of queuing rigidly. If a push (or some obstacle)
  // keeps this actor from actually closing distance for a while, it flags
  // itself Stuck so Program can ask the pathfinder for a new route; if that
  // keeps failing for GiveUpTimeout seconds straight, it cancels the order
  // entirely rather than retrying forever.
  //
  // Horizontal seeking only ever looks at X/Z — Y is handled completely
  // separately below, as "whatever the ground is doing right here, plus the
  // jump arc". That split means neither a slope nor a jump ever distorts
  // the other: a jump can't make horizontal progress look closer/farther
  // than it is, and the ground height is always current with no separate
  // "re-snap" step needed even the instant the ground under the actor changes.
  public void Update(float dt)
  {
    UpdateJump(dt);
    UpdateActionTimers(dt);

    bool movingThisFrame = false;
    float moveDirX = 0f, moveDirZ = 0f;

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

      if (dist > 0.0001f)
      {
        movingThisFrame = true;
        moveDirX = dx / dist;
        moveDirZ = dz / dist;
      }

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
        _worldPos.X += moveDirX * step;
        _worldPos.Z += moveDirZ * step;

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

    _worldPos.Y = _map.SmoothSurfaceY(_worldPos.X, _worldPos.Z) + JumpArc();
    UpdatePose(dt, movingThisFrame, moveDirX, moveDirZ);
  }

  // Shoves the actor sideways by a small amount; used by the separation pass
  // to keep actors from overlapping. Never touches Y — height always comes
  // from the terrain under the actor's seek movement above.
  public void Nudge(Vector2 xzDelta) => _worldPos += new Vector3(xzDelta.X, 0f, xzDelta.Y);

  // Kicks off a little hop, if one isn't already playing. Works whether the
  // actor is standing still or mid-walk.
  public void Jump()
  {
    if (_jumping) return;
    _jumping = true;
    _jumpTimer = 0f;
  }

  // Cosmetic swing/lean animations, triggered whenever Program.cs performs
  // the corresponding action (the dig/deposit itself always happens
  // instantly regardless — these are purely a visual flourish layered on
  // top, same as Jump above).
  public void PlayDig()
  {
    _digging = true;
    _digTimer = 0f;
  }

  public void PlayDeposit()
  {
    _depositing = true;
    _depositTimer = 0f;
  }

  void UpdateJump(float dt)
  {
    if (!_jumping) return;
    _jumpTimer += dt;
    if (_jumpTimer >= JumpDuration) _jumping = false;
  }

  void UpdateActionTimers(float dt)
  {
    if (_digging)
    {
      _digTimer += dt;
      if (_digTimer >= DigDuration) _digging = false;
    }

    if (_depositing)
    {
      _depositTimer += dt;
      if (_depositTimer >= DepositDuration) _depositing = false;
    }
  }

  // A sine arc: 0 at takeoff and landing, peaking at JumpHeight halfway
  // through. Added onto _worldPos.Y directly in Update — this is a real
  // height, not a rendering trick.
  float JumpArc() => _jumping
    ? MathF.Sin(MathF.Min(_jumpTimer / JumpDuration, 1f) * MathF.PI) * JumpHeight
    : 0f;

  // Turns _heading, _walkPhase and the dig/deposit timers into this frame's
  // limb angles. Cached on the actor (rather than recomputed per Draw* call)
  // so the solid pass, its shadow, and the outline all agree on the pose.
  void UpdatePose(float dt, bool movingThisFrame, float moveDirX, float moveDirZ)
  {
    if (movingThisFrame)
    {
      float targetHeading = MathF.Atan2(moveDirX, moveDirZ);
      float delta = WrapAngle(targetHeading - _heading);
      float maxStep = TurnRateRadPerSec * dt;
      _heading += Math.Clamp(delta, -maxStep, maxStep);
    }

    float walkTarget = movingThisFrame && !_jumping ? 1f : 0f;
    _walkBlend += (walkTarget - _walkBlend) * Math.Clamp(dt * WalkBlendRate, 0f, 1f);
    if (movingThisFrame) _walkPhase += dt * WalkCycleRate;

    float swing = MathF.Sin(_walkPhase) * WalkSwingDeg * _walkBlend;
    float jumpTuck = _jumping
      ? MathF.Sin(Math.Clamp(_jumpTimer / JumpDuration, 0f, 1f) * MathF.PI) * JumpLegTuckDeg
      : 0f;

    _poseLeftLegDeg = swing + jumpTuck;
    _poseRightLegDeg = -swing + jumpTuck;
    _poseLeftArmDeg = -swing * ArmSwingScale;
    _poseRightArmDeg = swing * ArmSwingScale;
    _poseTorsoLeanDeg = 0f;

    if (_digging)
    {
      // A single swing: raised back at the start, chopping forward/down
      // through the midpoint, settling back by the end.
      float p = Math.Clamp(_digTimer / DigDuration, 0f, 1f);
      _poseRightArmDeg = -DigRaiseDeg + (DigRaiseDeg + DigSwingDeg) * MathF.Sin(p * MathF.PI);
    }

    if (_depositing)
    {
      float p = Math.Clamp(_depositTimer / DepositDuration, 0f, 1f);
      float blend = MathF.Sin(p * MathF.PI);
      _poseTorsoLeanDeg = blend * DepositLeanDeg;
      _poseLeftArmDeg = blend * DepositArmDeg;
      _poseRightArmDeg = blend * DepositArmDeg;
    }
  }

  static float WrapAngle(float radians)
  {
    radians %= MathF.Tau;
    if (radians > MathF.PI) radians -= MathF.Tau;
    if (radians < -MathF.PI) radians += MathF.Tau;
    return radians;
  }

  // One box of the body: rotates by AngleDeg (about local X, i.e. a
  // forward/back swing) around PivotLocal, then the box itself sits
  // HangLocal further out from that pivot — e.g. a leg's pivot is the hip,
  // and its box hangs half a leg-length below that pivot. A part with no
  // swing and HangLocal == Zero (torso, head) just rotates in place.
  readonly record struct BodyPart(Vector3 PivotLocal, Vector3 HangLocal, float AngleDeg, Vector3 Size, Color Color);

  IEnumerable<BodyPart> BuildParts()
  {
    yield return new BodyPart(
      new Vector3(0, LegHeight + TorsoHeight / 2f, 0), Vector3.Zero, _poseTorsoLeanDeg,
      new Vector3(TorsoWidth, TorsoHeight, TorsoDepth), _shirtColor);

    yield return new BodyPart(
      new Vector3(0, LegHeight + TorsoHeight + HeadSize / 2f, 0), Vector3.Zero, 0f,
      new Vector3(HeadSize, HeadSize, HeadSize), SkinColor);

    yield return new BodyPart(
      new Vector3(-HipOffsetX, LegHeight, 0), new Vector3(0, -LegHeight / 2f, 0), _poseLeftLegDeg,
      new Vector3(LegThickness, LegHeight, LegThickness), PantsColor);

    yield return new BodyPart(
      new Vector3(HipOffsetX, LegHeight, 0), new Vector3(0, -LegHeight / 2f, 0), _poseRightLegDeg,
      new Vector3(LegThickness, LegHeight, LegThickness), PantsColor);

    yield return new BodyPart(
      new Vector3(-ShoulderOffsetX, LegHeight + TorsoHeight, 0), new Vector3(0, -ArmLength / 2f, 0), _poseLeftArmDeg,
      new Vector3(ArmThickness, ArmLength, ArmThickness), SkinColor);

    yield return new BodyPart(
      new Vector3(ShoulderOffsetX, LegHeight + TorsoHeight, 0), new Vector3(0, -ArmLength / 2f, 0), _poseRightArmDeg,
      new Vector3(ArmThickness, ArmLength, ArmThickness), SkinColor);
  }

  // The lit fill. Draw this inside a lighting shader's BeginMode/EndMode.
  // Uses hand-rotated world-space boxes (DrawRotatedBox), not raylib's own
  // DrawCube wrapped in a PushMatrix/Rotatef stack: the world's lighting
  // shader (see SunLight) deliberately skips a model-matrix transform and
  // expects fragPosition/fragNormal to already be world space, which only
  // holds if the rotation is baked into the vertices themselves.
  public void DrawSolid()
  {
    // Draw the remaining path as small dots so you can see the plan.
    foreach (var (x, y) in _path)
    {
      Vector3 top = TileTop(x, y);
      Raylib.DrawSphere(new Vector3(top.X, top.Y + 2f, top.Z), 3f, Color.Yellow);
    }

    Matrix4x4 headingRot = Matrix4x4.CreateRotationY(_heading);
    foreach (var part in BuildParts())
    {
      Matrix4x4 swingRot = Matrix4x4.CreateRotationX(part.AngleDeg * (MathF.PI / 180f));
      Matrix4x4 worldRot = swingRot * headingRot;
      Vector3 pivotWorld = _worldPos + Vector3.Transform(part.PivotLocal, headingRot);
      Vector3 center = pivotWorld + Vector3.Transform(part.HangLocal, worldRot);
      DrawRotatedBox(center, part.Size, worldRot, part.Color);
    }
  }

  // Draws a box at an arbitrary world position/rotation with correct
  // per-face normals, by computing its 8 corners on the CPU rather than
  // relying on raylib's DrawCube (which can't rotate) or the rlgl matrix
  // stack (whose transform never reaches this shader's fragPosition/
  // fragNormal — see DrawSolid's comment).
  static void DrawRotatedBox(Vector3 worldCenter, Vector3 size, Matrix4x4 rotation, Color color)
  {
    Vector3 h = size / 2f;
    Vector3[] local =
    {
      new(-h.X, -h.Y, -h.Z), new(h.X, -h.Y, -h.Z), new(h.X, h.Y, -h.Z), new(-h.X, h.Y, -h.Z),
      new(-h.X, -h.Y, h.Z), new(h.X, -h.Y, h.Z), new(h.X, h.Y, h.Z), new(-h.X, h.Y, h.Z),
    };
    var world = new Vector3[8];
    for (int i = 0; i < 8; i++) world[i] = worldCenter + Vector3.Transform(local[i], rotation);

    void Face(int a, int b, int c, int d, Vector3 localNormal)
    {
      Vector3 n = Vector3.TransformNormal(localNormal, rotation);
      Rlgl.Normal3f(n.X, n.Y, n.Z);
      Rlgl.Vertex3f(world[a].X, world[a].Y, world[a].Z);
      Rlgl.Vertex3f(world[b].X, world[b].Y, world[b].Z);
      Rlgl.Vertex3f(world[c].X, world[c].Y, world[c].Z);
      Rlgl.Vertex3f(world[d].X, world[d].Y, world[d].Z);
    }

    Rlgl.Begin(DrawMode.Quads);
    Rlgl.Color4ub(color.R, color.G, color.B, color.A);
    Face(0, 1, 2, 3, new Vector3(0, 0, -1)); // back
    Face(5, 4, 7, 6, new Vector3(0, 0, 1));  // front
    Face(4, 0, 3, 7, new Vector3(-1, 0, 0)); // left
    Face(1, 5, 6, 2, new Vector3(1, 0, 0));  // right
    Face(3, 2, 6, 7, new Vector3(0, 1, 0));  // top
    Face(4, 5, 1, 0, new Vector3(0, -1, 0)); // bottom
    Rlgl.End();
  }

  // Walks the body via raylib's own rlgl PushMatrix/Rotatef stack instead of
  // DrawRotatedBox — fine here because neither caller needs correct
  // fragPosition/fragNormal: the shadow pass is depth-only (no colour
  // attachment, so only gl_Position — which the matrix stack does drive
  // correctly — matters), and the outline pass uses the plain unlit default
  // shader, which doesn't read those varyings at all.
  void ForEachPartViaMatrixStack(Action<Vector3, Vector3> drawPart)
  {
    Matrix4x4 headingRot = Matrix4x4.CreateRotationY(_heading);
    float headingDeg = _heading * (180f / MathF.PI);
    foreach (var part in BuildParts())
    {
      Vector3 pivotWorld = _worldPos + Vector3.Transform(part.PivotLocal, headingRot);
      Rlgl.PushMatrix();
      Rlgl.Translatef(pivotWorld.X, pivotWorld.Y, pivotWorld.Z);
      Rlgl.Rotatef(headingDeg, 0f, 1f, 0f);
      Rlgl.Rotatef(part.AngleDeg, 1f, 0f, 0f);
      drawPart(part.HangLocal, part.Size);
      Rlgl.PopMatrix();
    }
  }

  // Just the body, no path-plan dots — used for the shadow depth pass,
  // where those tiny dots would only cost render time on shadows nobody's
  // meant to see. Colour is irrelevant here (only depth is recorded).
  public void DrawShadowCaster() =>
    ForEachPartViaMatrixStack((center, size) => Raylib.DrawCubeV(center, size, Color.White));

  // Just the selection ring on the ground, if selected — the solid boxes
  // from DrawSolid are enough to read as a body on their own; a wireframe
  // cage over six separate boxes turned out to look like a busy red scaffold
  // rather than an outline, so there's no per-part wireframe here any more.
  public void DrawOutline()
  {
    if (Selected)
    {
      // The selection ring stays on the ground even mid-jump, so it still
      // reads as "this tile" rather than following the actor into the air.
      Vector3 ringCenter = new(_worldPos.X, _map.SurfaceY(TileX, TileZ) + 1f, _worldPos.Z);
      Raylib.DrawCircle3D(ringCenter, Radius * 1.6f, new Vector3(1f, 0f, 0f), 90f, Color.Lime);
    }
  }
}
