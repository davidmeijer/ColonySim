using System.Numerics;
using Raylib_cs;
using ColonySim.World;

namespace ColonySim.Entities;

public class Actor
{
  // The fine voxel the actor currently occupies — movement/pathfinding run
  // entirely on the fine grid now (see TileMap's own class doc comment).
  public int FineX { get; private set; }
  public int FineZ { get; private set; }

  // This actor's own collision footprint/height, in fine voxels — see
  // TileMap.CanOccupy. 1x1 is the smallest sane default; the check
  // machinery itself supports any size mover. HeightVoxels is derived from
  // the actual rendered body height below (LegHeight+TorsoHeight+HeadSize)
  // rather than a made-up number, so it stays truthful to the model. Not
  // load-bearing for collision yet (obstacles don't compare a mover's
  // height against their own — see TileMap's _obstacleHeight doc comment)
  // — stored now so the actor is already "tall" in the same terms an
  // obstacle is, ready for when standing on a future floor makes the
  // comparison matter.
  public const int FootprintSize = 1;
  public const int HeightVoxels = (int)((LegHeight + TorsoHeight + HeadSize) / TileMap.VoxelSize);

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

  // Whether this actor will autonomously pull tasks off the shared
  // WorkQueue whenever it's idle — see Program.UpdateBuilders. Actors never
  // carry material themselves any more; digging/depositing move it
  // straight into/out of the shared global Inventory.
  public bool IsBuilder { get; set; }

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

  // --- Idle wandering ---------------------------------------------------
  // How long since this actor was last given ANY path — a real order, an
  // auto-reroute, or a previous wander — reset in ApplyPath so it covers
  // all three uniformly. Once it clears a randomised threshold (rerolled
  // every time it's reset, so a group doesn't all set off in lockstep)
  // while genuinely idle, Program.cs sends it ambling a few tiles in a
  // random direction — see WantsToWander/DeferWander.
  float _idleTimer;
  float _wanderDelayTarget = NextWanderDelay();
  const float MinWanderDelay = 3f;
  const float MaxWanderDelay = 9f;

  static float NextWanderDelay() => MinWanderDelay + Random.Shared.NextSingle() * (MaxWanderDelay - MinWanderDelay);

  // True once an actor with nothing else going on (no path at all — not a
  // real order, not mid-wander already) has sat idle past its threshold.
  // Program.cs is the one that actually knows how to find a walkable tile
  // and path to it, so this just flags "ready", it doesn't act on it.
  public bool WantsToWander => _path.Count == 0 && _idleTimer >= _wanderDelayTarget;

  // Called instead of SetPath when a wander attempt couldn't find anywhere
  // to send this actor this time (e.g. boxed in) — waits out another full
  // idle spell before trying again, rather than retrying every frame.
  public void DeferWander() => ResetIdleTimer();

  void ResetIdleTimer()
  {
    _idleTimer = 0f;
    _wanderDelayTarget = NextWanderDelay();
  }

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

  // Two "eye" squares on the front of the head — otherwise arms, legs,
  // torso and head are all perfectly front/back symmetric, so there'd be
  // no way to tell which way an actor is actually facing (its Heading)
  // just by looking at it. Two separated squares specifically, not one
  // wide bar: a single dark stripe read ambiguously (could as easily be
  // hair on the back of the head as a face on the front) — two distinct
  // dots either side of centre, with visible skin colour between them, is
  // a much less ambiguous "this is a face, this is the front" cue.
  static readonly Color FaceColor = new(40, 30, 26, 255);
  const float EyeSize = HeadSize * 0.16f;
  const float EyeGap = HeadSize * 0.14f;
  const float EyeOffsetX = EyeSize / 2f + EyeGap / 2f;
  const float FaceDepth = 1.5f;

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

  // A slow, free-running rise and fall of the chest — always on, walking
  // or not, same as a real person keeps breathing while they walk. Purely
  // a couple of world units of torso height, not gated behind any other
  // state, so it never has to coordinate with the walk cycle, digging, etc.
  float _breathPhase;
  const float BreathRate = 1.6f; // radians of phase per second
  const float BreathAmount = HeadSize * 0.16f; // peak-to-peak torso height change

  // Continuous work animation, driven by Program.UpdateBuilders while a
  // builder is on a WorkTask — unlike Jump, this isn't a fixed-length
  // one-shot: a task can run for many seconds, so the swing just keeps
  // cycling (via _workPhase, uncapped) until StopWorking() is called.
  bool _working;
  bool _workConstructive; // true: deposit-style lean/place; false: dig-style chop
  float _workPhase;
  const float DigCyclePeriod = 0.5f;
  const float DigRaiseDeg = 50f;
  const float DigSwingDeg = 70f;
  const float DepositCyclePeriod = 0.5f;
  const float DepositLeanDeg = 18f;
  const float DepositArmDeg = 55f;

  // Cached per-frame pose, computed once in Update() and read by all three
  // Draw* methods so they agree on where the limbs are this frame.
  float _poseLeftLegDeg, _poseRightLegDeg, _poseLeftArmDeg, _poseRightArmDeg, _poseTorsoLeanDeg, _poseBreathLift;

  public Actor(TileMap map, int fineX, int fineZ)
  {
    _map = map;
    FineX = fineX;
    FineZ = fineZ;
    _worldPos = VoxelTop(fineX, fineZ);
    _shirtColor = ShirtPalette[_nextShirtIndex++ % ShirtPalette.Length];
  }

  // The ground position at the centre of a fine voxel — where this actor's
  // feet belong. Samples the real fine-voxel terrain height directly, so
  // an actor's feet always match the actual rendered surface it's
  // standing on.
  Vector3 VoxelTop(int fx, int fz)
  {
    float worldX = fx * TileMap.VoxelSize + TileMap.VoxelSize / 2f;
    float worldZ = fz * TileMap.VoxelSize + TileMap.VoxelSize / 2f;
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
    ResetIdleTimer();
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
      _idleTimer += dt;
    }
    else
    {
      var next = _path.Peek();
      float targetX = next.X * TileMap.VoxelSize + TileMap.VoxelSize / 2f;
      float targetZ = next.Y * TileMap.VoxelSize + TileMap.VoxelSize / 2f;

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
        // Close enough to count as "arrived": snap onto the voxel and pop it.
        _worldPos.X = targetX;
        _worldPos.Z = targetZ;
        FineX = next.X;
        FineZ = next.Y;
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

  // Starts (or re-styles) the looping work swing — called once a builder
  // arrives at a WorkTask's stand tile, and again if the task's direction
  // changes. constructive picks the deposit-style lean/place motion over
  // the dig-style chop.
  public void StartWorking(bool constructive)
  {
    _working = true;
    _workConstructive = constructive;
    _workPhase = 0f;
  }

  // Called once a task completes, is cancelled, or the actor gets pulled
  // off it by a manual order.
  public void StopWorking() => _working = false;

  void UpdateJump(float dt)
  {
    if (!_jumping) return;
    _jumpTimer += dt;
    if (_jumpTimer >= JumpDuration) _jumping = false;
  }

  void UpdateActionTimers(float dt)
  {
    if (_working) _workPhase += dt;
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

    _breathPhase += dt * BreathRate;
    _poseBreathLift = MathF.Sin(_breathPhase) * (BreathAmount / 2f);

    float swing = MathF.Sin(_walkPhase) * WalkSwingDeg * _walkBlend;
    float jumpTuck = _jumping
      ? MathF.Sin(Math.Clamp(_jumpTimer / JumpDuration, 0f, 1f) * MathF.PI) * JumpLegTuckDeg
      : 0f;

    _poseLeftLegDeg = swing + jumpTuck;
    _poseRightLegDeg = -swing + jumpTuck;
    _poseLeftArmDeg = -swing * ArmSwingScale;
    _poseRightArmDeg = swing * ArmSwingScale;
    _poseTorsoLeanDeg = 0f;

    if (_working && !_workConstructive)
    {
      // A repeating swing: raised back at the start of each cycle, chopping
      // forward/down through the midpoint, settling back by the end —
      // loops for as long as the task takes rather than firing once.
      float p = _workPhase % DigCyclePeriod / DigCyclePeriod;
      _poseRightArmDeg = -DigRaiseDeg + (DigRaiseDeg + DigSwingDeg) * MathF.Sin(p * MathF.PI);
    }

    if (_working && _workConstructive)
    {
      float p = _workPhase % DepositCyclePeriod / DepositCyclePeriod;
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
    // Breathing changes the torso's own height slightly, growing/shrinking
    // upward only — the bottom (waist, where the legs attach) always stays
    // exactly at LegHeight, so it reads as a chest rising and falling
    // rather than the whole torso sliding up and down. Shoulders, the
    // head, and the eyes all key off torsoTop instead of the raw
    // LegHeight + TorsoHeight, so they ride along with each breath too.
    float torsoHeight = TorsoHeight + _poseBreathLift;
    float torsoTop = LegHeight + torsoHeight;

    yield return new BodyPart(
      new Vector3(0, LegHeight + torsoHeight / 2f, 0), Vector3.Zero, _poseTorsoLeanDeg,
      new Vector3(TorsoWidth, torsoHeight, TorsoDepth), _shirtColor);

    yield return new BodyPart(
      new Vector3(0, torsoTop + HeadSize / 2f, 0), Vector3.Zero, 0f,
      new Vector3(HeadSize, HeadSize, HeadSize), SkinColor);

    // Sit just proud of the head's front face (local +Z — see UpdatePose's
    // use of Atan2(moveDirX, moveDirZ), which puts "forward" on +Z at
    // Heading == 0) so they don't z-fight with the head box behind them.
    float eyeY = torsoTop + HeadSize / 2f;
    float eyeZ = HeadSize / 2f + FaceDepth / 2f;
    yield return new BodyPart(
      new Vector3(-EyeOffsetX, eyeY, eyeZ), Vector3.Zero, 0f,
      new Vector3(EyeSize, EyeSize, FaceDepth), FaceColor);
    yield return new BodyPart(
      new Vector3(EyeOffsetX, eyeY, eyeZ), Vector3.Zero, 0f,
      new Vector3(EyeSize, EyeSize, FaceDepth), FaceColor);

    yield return new BodyPart(
      new Vector3(-HipOffsetX, LegHeight, 0), new Vector3(0, -LegHeight / 2f, 0), _poseLeftLegDeg,
      new Vector3(LegThickness, LegHeight, LegThickness), PantsColor);

    yield return new BodyPart(
      new Vector3(HipOffsetX, LegHeight, 0), new Vector3(0, -LegHeight / 2f, 0), _poseRightLegDeg,
      new Vector3(LegThickness, LegHeight, LegThickness), PantsColor);

    yield return new BodyPart(
      new Vector3(-ShoulderOffsetX, torsoTop, 0), new Vector3(0, -ArmLength / 2f, 0), _poseLeftArmDeg,
      new Vector3(ArmThickness, ArmLength, ArmThickness), SkinColor);

    yield return new BodyPart(
      new Vector3(ShoulderOffsetX, torsoTop, 0), new Vector3(0, -ArmLength / 2f, 0), _poseRightArmDeg,
      new Vector3(ArmThickness, ArmLength, ArmThickness), SkinColor);
  }

  // The lit fill. Draw this inside a lighting shader's BeginMode/EndMode.
  // Uses hand-rotated world-space boxes (DrawRotatedBox), not raylib's own
  // DrawCube wrapped in a PushMatrix/Rotatef stack: the world's lighting
  // shader (see SunLight) deliberately skips a model-matrix transform and
  // expects fragPosition/fragNormal to already be world space, which only
  // holds if the rotation is baked into the vertices themselves.
  public void DrawSolid(bool showPathDots)
  {
    // Draw the remaining path as small dots so you can see the plan —
    // optional (off by default): with several actors all mid-route, the
    // dots add up to a fair bit of visual noise.
    if (showPathDots)
    {
      foreach (var (x, y) in _path)
      {
        Vector3 top = VoxelTop(x, y);
        Raylib.DrawSphere(new Vector3(top.X, top.Y + 2f, top.Z), 3f, Color.Yellow);
      }
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

    // Emitted as (a, d, c, b), not the seemingly-obvious (a, b, c, d): with
    // this method's corner numbering, the naive order comes out wound
    // backward relative to its own stated normal — the same discrepancy
    // TileMap.AddQuad's own comment calls out ("raylib backface-culls by
    // default, so a triangle wound the wrong way gets discarded from the
    // very side it's meant to be seen from"), and the reason a whole actor
    // used to render "inside out": every near face was being culled,
    // showing the far faces through it instead.
    void Face(int a, int b, int c, int d, Vector3 localNormal)
    {
      Vector3 n = Vector3.TransformNormal(localNormal, rotation);
      Rlgl.Normal3f(n.X, n.Y, n.Z);
      Rlgl.Vertex3f(world[a].X, world[a].Y, world[a].Z);
      Rlgl.Vertex3f(world[d].X, world[d].Y, world[d].Z);
      Rlgl.Vertex3f(world[c].X, world[c].Y, world[c].Z);
      Rlgl.Vertex3f(world[b].X, world[b].Y, world[b].Z);
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
      // reads as "here" rather than following the actor into the air — that
      // means it can't just reuse _worldPos.Y, which already includes the
      // jump arc; re-sample the ground height directly instead.
      Vector3 ringCenter = new(_worldPos.X, _map.SmoothSurfaceY(_worldPos.X, _worldPos.Z) + 1f, _worldPos.Z);
      Raylib.DrawCircle3D(ringCenter, Radius * 1.6f, new Vector3(1f, 0f, 0f), 90f, Color.Lime);
    }
  }
}
