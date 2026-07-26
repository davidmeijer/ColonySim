namespace ColonySim.World;

// A player-placed water source: a permanent spring that pushes a fixed
// amount of water into the ground around it every water tick, forever (see
// TileMap.SpringFlowRate). It's the counterpart to digging — digging
// decides where water can go, a spring decides where it comes from — and
// the two together are what make channel-building a thing you can actually
// do rather than just watch. Position isn't cached here; like Tree, Bush
// and Campfire, world position is recomputed from AnchorFx/AnchorFz against
// the live terrain height wherever it's needed.
public readonly struct Spring
{
  // Collision footprint, in fine voxels — see TileMap.CanOccupy. Height is
  // 0: a spring never blocks movement, same as always — you can walk
  // straight over it.
  public const int Footprint = 7;
  public const int Height = 0;

  public int AnchorFx { get; }
  public int AnchorFz { get; }

  public Spring(int anchorFx, int anchorFz)
  {
    AnchorFx = anchorFx;
    AnchorFz = anchorFz;
  }
}
