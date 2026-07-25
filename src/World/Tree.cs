namespace ColonySim.World;

// A big pine tree: a stack of trunk segments topped with a tapering stack
// of canopy segments, rooted at one coarse tile. Segment height is one
// coarse "old block" (TileSize) each — the same scale as terrain height
// variation, so a full-grown tree meaningfully towers over the landscape.
public readonly struct Tree
{
  public int TileX { get; }
  public int TileZ { get; }
  public int TrunkHeight { get; }  // 3-5 segments
  public int CanopyHeight { get; } // 3-4 segments, stacked above the trunk

  public Tree(int tileX, int tileZ, int trunkHeight, int canopyHeight)
  {
    TileX = tileX;
    TileZ = tileZ;
    TrunkHeight = trunkHeight;
    CanopyHeight = canopyHeight;
  }
}
