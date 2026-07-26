namespace ColonySim.World;

// A player-placed light source: a small stack of logs that's always lit,
// scorches the grass in a radius around it (see TileMap.PlaceCampfire), and
// glows at night via a point light (SunLight.SetPointLights). FlickerPhase
// gives each fire its own flame rhythm so a cluster of them doesn't pulse in
// lockstep. Position isn't cached here — like Tree and Bush, world position
// is recomputed from AnchorFx/AnchorFz against the live terrain height
// wherever it's needed.
public readonly struct Campfire
{
  // Collision footprint/height, in fine voxels — see TileMap.CanOccupy.
  // 9 (not 10) so the footprint is exactly centered on its anchor voxel —
  // visually indistinguishable from the old 10-wide, tile-aligned version.
  public const int Footprint = 9;
  public const int Height = 3;

  public int AnchorFx { get; }
  public int AnchorFz { get; }
  public float FlickerPhase { get; }

  public Campfire(int anchorFx, int anchorFz, float flickerPhase)
  {
    AnchorFx = anchorFx;
    AnchorFz = anchorFz;
    FlickerPhase = flickerPhase;
  }
}
