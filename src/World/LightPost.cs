namespace ColonySim.World;

// A player-placed light source: a slim dark-gray post with a glowing voxel
// on top, meant for lining paths rather than lighting a whole camp the way
// a Campfire does. Genuinely blocks its (small) footprint now that
// pathfinding runs on the fine voxel grid — actors just route around it
// like any other small obstacle, the same as a bush. Position isn't cached
// here; like Campfire and Spring, world position is recomputed from
// AnchorFx/AnchorFz against the live terrain height wherever it's needed.
public readonly struct LightPost
{
  // Collision footprint/height, in fine voxels — see TileMap.CanOccupy.
  public const int Footprint = 3;
  public const int Height = 8;

  public int AnchorFx { get; }
  public int AnchorFz { get; }

  public LightPost(int anchorFx, int anchorFz)
  {
    AnchorFx = anchorFx;
    AnchorFz = anchorFz;
  }
}
