namespace ColonySim.World;

// A player-placed storage container — a big iron box actors deliver
// harvested food into, and draw from when hungry (see Program.UpdateHunger).
// General-purpose (backed by the same Inventory class as the shared global
// pool) rather than a food-only special case, even though food is the only
// thing this feature actually moves in or out of it.
public readonly struct StorageBox
{
  // Collision footprint/height, in fine voxels — see TileMap.CanOccupy.
  // 10x10 fine voxels (one whole coarse tile, size x size per TileMap's
  // footprint convention) — a real building's worth of ground, not a
  // trinket, but nowhere near the size its 10,000 capacity might suggest.
  public const int Footprint = 10;
  public const int Height = 5;
  public const int Capacity = 10_000;

  public int AnchorFx { get; }
  public int AnchorFz { get; }

  // A reference type field on an otherwise-immutable struct — same trick
  // the List<StorageBox> holding these already relies on: the struct itself
  // (position) never changes after placement, but its contents do, every
  // time something is harvested/stored/eaten.
  public Entities.Inventory Storage { get; }

  public StorageBox(int anchorFx, int anchorFz, Entities.Inventory? storage = null)
  {
    AnchorFx = anchorFx;
    AnchorFz = anchorFz;
    Storage = storage ?? new Entities.Inventory(Capacity);
  }
}
