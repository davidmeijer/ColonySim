namespace ColonySim.World;

// A player-placed light source: a slim dark-gray post with a glowing voxel
// on top, meant for lining paths rather than lighting a whole camp the way
// a Campfire does. Doesn't block its tile (see TileMap.CanPlaceLightPost) —
// a thin post shouldn't get in an actor's way any more than a spring does.
// Position isn't cached here; like Campfire and Spring, world position is
// recomputed from TileX/TileZ against the live terrain height wherever it's
// needed.
public readonly struct LightPost
{
  public int TileX { get; }
  public int TileZ { get; }

  public LightPost(int tileX, int tileZ)
  {
    TileX = tileX;
    TileZ = tileZ;
  }
}
