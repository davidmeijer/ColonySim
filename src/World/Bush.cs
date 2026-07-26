namespace ColonySim.World;

// A small shrub: 1-2 squat, tapering blobs sitting directly on the ground,
// rooted at one fine voxel, blocking movement on its own small footprint
// same as a Tree — just lower and with no trunk. SizeVariant and
// ColorVariant are just small indices (not raw world units or a Color) so
// this stays decoupled from TileMap's own sizing/palette constants, the
// same way Tree stores segment counts rather than world-unit sizes.
public readonly struct Bush
{
  // Collision footprint/height, in fine voxels — see TileMap.CanOccupy.
  public const int Footprint = 3;
  public const int Height = 5;

  public int AnchorFx { get; }
  public int AnchorFz { get; }
  public int Layers { get; }       // 1-2 stacked blobs
  public int SizeVariant { get; }  // 0-2, a little width variety
  public int ColorVariant { get; } // 0-2, indexes a small colour palette

  public Bush(int anchorFx, int anchorFz, int layers, int sizeVariant, int colorVariant)
  {
    AnchorFx = anchorFx;
    AnchorFz = anchorFz;
    Layers = layers;
    SizeVariant = sizeVariant;
    ColorVariant = colorVariant;
  }
}
