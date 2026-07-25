namespace ColonySim.Tasks;

// The four work orders a builder can be given. Dig/Deposit each act on a
// single fine voxel column (see WorkTask.FineX/FineZ) — the player picks
// the exact column, a builder always moves exactly one voxel there.
public enum TaskKind
{
  Dig,
  Deposit,
  BuildCampfire,
  DemolishCampfire,
}
