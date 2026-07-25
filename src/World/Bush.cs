namespace ColonySim.World;

// A small shrub: 1-2 squat, tapering blobs sitting directly on the ground,
// rooted at one coarse tile, blocking pathfinding on that tile same as a
// Tree — just with no trunk. SizeVariant and ColorVariant are just small
// indices (not raw world units or a Color) so this stays decoupled from
// TileMap's own sizing/palette constants, the same way Tree stores
// segment counts rather than world-unit sizes.
public readonly struct Bush
{
  public int TileX { get; }
  public int TileZ { get; }
  public int Layers { get; }       // 1-2 stacked blobs
  public int SizeVariant { get; }  // 0-2, a little width variety
  public int ColorVariant { get; } // 0-2, indexes a small colour palette

  public Bush(int tileX, int tileZ, int layers, int sizeVariant, int colorVariant)
  {
    TileX = tileX;
    TileZ = tileZ;
    Layers = layers;
    SizeVariant = sizeVariant;
    ColorVariant = colorVariant;
  }
}
