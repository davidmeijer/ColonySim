namespace ColonySim.Tasks;

// The work orders a builder can be given. Dig/Deposit each act on a
// single fine voxel column (see WorkTask.FineX/FineZ) — the player picks
// the exact column, a builder always moves exactly one voxel there. The
// rest act on a whole coarse tile.
public enum TaskKind
{
  Dig,
  Deposit,
  BuildCampfire,
  DemolishCampfire,
  BuildSpring,
  DemolishSpring,
}
