namespace ColonySim.World;

// A player-placed light source: a small stack of logs that's always lit,
// scorches the grass in a radius around it (see TileMap.PlaceCampfire), and
// glows at night via a point light (SunLight.SetPointLights). FlickerPhase
// gives each fire its own flame rhythm so a cluster of them doesn't pulse in
// lockstep. Position isn't cached here — like Tree and Bush, world position
// is recomputed from TileX/TileZ against the live terrain height wherever
// it's needed.
public readonly struct Campfire
{
  public int TileX { get; }
  public int TileZ { get; }
  public float FlickerPhase { get; }

  public Campfire(int tileX, int tileZ, float flickerPhase)
  {
    TileX = tileX;
    TileZ = tileZ;
    FlickerPhase = flickerPhase;
  }
}
