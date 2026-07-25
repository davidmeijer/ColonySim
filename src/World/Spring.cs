namespace ColonySim.World;

// A player-placed water source: a permanent spring that pushes a fixed
// amount of water into the ground around it every water tick, forever (see
// TileMap.SpringFlowRate). It's the counterpart to digging — digging
// decides where water can go, a spring decides where it comes from — and
// the two together are what make channel-building a thing you can actually
// do rather than just watch. Position isn't cached here; like Tree, Bush
// and Campfire, world position is recomputed from TileX/TileZ against the
// live terrain height wherever it's needed.
public readonly struct Spring
{
  public int TileX { get; }
  public int TileZ { get; }

  public Spring(int tileX, int tileZ)
  {
    TileX = tileX;
    TileZ = tileZ;
  }
}
