namespace ColonySim.World;

// A big pine tree: a stack of trunk segments topped with a tapering stack
// of canopy segments, rooted at one fine voxel. Segment height is one
// coarse "old block" (TileSize) each — the same scale as terrain height
// variation, so a full-grown tree meaningfully towers over the landscape.
public readonly struct Tree
{
  // Collision footprint/height, in fine voxels — see TileMap.CanOccupy.
  // Much narrower than the whole coarse tile a tree used to block: only
  // the trunk's real footprint stops movement now.
  public const int Footprint = 3;
  public const int Height = 35;

  public int AnchorFx { get; }
  public int AnchorFz { get; }
  public int TrunkHeight { get; }  // 3-5 segments
  public int CanopyHeight { get; } // 3-4 segments, stacked above the trunk

  public Tree(int anchorFx, int anchorFz, int trunkHeight, int canopyHeight)
  {
    AnchorFx = anchorFx;
    AnchorFz = anchorFz;
    TrunkHeight = trunkHeight;
    CanopyHeight = canopyHeight;
  }
}
