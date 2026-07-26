using System.Numerics;
using Raylib_cs;
using ColonySim.World;

namespace ColonySim.Entities;

// A unit of material freed from the terrain (currently only "Dirt", from
// digging — see Program.CompleteDig) that hovers where it was dug up instead
// of crediting the global Inventory the instant the voxel comes out. An
// actor walking over it picks it up (see Program.UpdateItemDrops), which is
// where it actually lands in storage. Purely a visual/collectible token — no
// collision, doesn't block movement or pathfinding, and isn't tracked by
// TileMap the way Trees/Campfires/etc. are, since it's meant to be
// short-lived and isn't persisted through SaveSystem.
public class ItemDrop
{
  public string Kind { get; }
  public int Amount { get; }

  // The point on the ground actors are measured against for pickup — not
  // the bobbing visual position, so collection doesn't depend on catching
  // the drop at a particular point in its bob cycle.
  public Vector3 GroundPos { get; }

  // Elapsed lifetime, driving both the spin and the bob. Randomised at
  // spawn so a handful of voxels dug up together don't all spin/bob in
  // lockstep.
  float _age;

  const float SpinRateRadPerSec = MathF.PI * 0.5f;
  const float BobRateRadPerSec = MathF.PI * 1.1f;
  const float BobAmount = TileMap.TileSize * 0.08f;
  const float HoverHeight = TileMap.TileSize * 0.45f;
  const float Size = TileMap.TileSize * 0.26f;

  // How close an actor's feet need to get (horizontally) to sweep this up —
  // generous enough that just walking past is enough, no need to path
  // exactly onto the drop's voxel.
  public const float PickupRadius = TileMap.TileSize * 0.5f;

  static readonly Color DirtColor = new(120, 84, 52, 255);

  public ItemDrop(string kind, int amount, Vector3 groundPos)
  {
    Kind = kind;
    Amount = amount;
    GroundPos = groundPos;
    _age = Random.Shared.NextSingle() * MathF.Tau;
  }

  public void Update(float dt) => _age += dt;

  Vector3 VisualPos => GroundPos + new Vector3(0f, HoverHeight + MathF.Sin(_age * BobRateRadPerSec) * BobAmount, 0f);

  public void Draw()
  {
    Color color = Kind switch { "Dirt" => DirtColor, _ => Color.White };
    DrawRotatedCube(VisualPos, new Vector3(Size, Size, Size), _age * SpinRateRadPerSec, color);
  }

  // A cube spinning about world Y, with the rotation baked directly into its
  // vertices/normals rather than left to raylib's matrix stack — same
  // requirement (and reasoning) as Actor.DrawRotatedBox: the world's
  // lighting shader deliberately skips a model-matrix transform and expects
  // fragPosition/fragNormal to already be world space.
  static void DrawRotatedCube(Vector3 worldCenter, Vector3 size, float angleRad, Color color)
  {
    Matrix4x4 rotation = Matrix4x4.CreateRotationY(angleRad);
    Vector3 h = size / 2f;
    Vector3[] local =
    {
      new(-h.X, -h.Y, -h.Z), new(h.X, -h.Y, -h.Z), new(h.X, h.Y, -h.Z), new(-h.X, h.Y, -h.Z),
      new(-h.X, -h.Y, h.Z), new(h.X, -h.Y, h.Z), new(h.X, h.Y, h.Z), new(-h.X, h.Y, h.Z),
    };
    var world = new Vector3[8];
    for (int i = 0; i < 8; i++) world[i] = worldCenter + Vector3.Transform(local[i], rotation);

    // Same (a, d, c, b) winding as Actor.DrawRotatedBox — with this corner
    // numbering the "obvious" (a, b, c, d) order comes out backward relative
    // to the stated normal and gets backface-culled from the very side it's
    // meant to be seen from.
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
}
