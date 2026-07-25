using ColonySim.Entities;
using ColonySim.World;

namespace ColonySim.Tasks;

// The player's FIFO work order list. Builders never own their own private
// to-do list — they all pull from this single shared queue, in order,
// picking the first entry nobody's claimed yet that they can actually do
// right now (see ClaimableInOrder).
public class WorkQueue
{
  readonly List<WorkTask> _tasks = new();
  public IReadOnlyList<WorkTask> Tasks => _tasks;

  public void Enqueue(WorkTask task) => _tasks.Add(task);

  // Removes a task outright — used by the queue panel's cancel button.
  // Releasing an in-progress worker is the caller's job (Program.cs also
  // has to StopWorking() the actor), this just drops the bookkeeping.
  public void Cancel(WorkTask task) => _tasks.Remove(task);

  // Whether a specific fine voxel already has an unclaimed-or-in-progress
  // task of the given kind queued — checked before enqueuing a new one so
  // repeatedly clicking/dragging over the same voxel doesn't pile up
  // duplicate work.
  public bool HasPendingVoxelTask(TaskKind kind, int fineX, int fineZ) =>
    _tasks.Any(t => t.Kind == kind && t.FineX == fineX && t.FineZ == fineZ && !t.Complete);

  // Drops any task whose target has already been satisfied by something
  // else (another task finishing on the same voxel, a campfire someone
  // else already built/demolished there, ...) so the queue panel — and the
  // in-world blue highlight — never shows stale work. Cheap enough to call
  // once a frame given the task counts this game deals with.
  public void Prune(TileMap map)
  {
    _tasks.RemoveAll(t => t.Complete || IsAlreadySatisfied(t, map));
  }

  static bool IsAlreadySatisfied(WorkTask t, TileMap map) => t.Kind switch
  {
    TaskKind.Dig => !map.CanDigVoxel(t.FineX, t.FineZ),
    TaskKind.Deposit => !map.CanDepositVoxel(t.FineX, t.FineZ),
    TaskKind.BuildCampfire => map.Campfires.Any(f => f.TileX == t.TileX && f.TileZ == t.TileZ),
    TaskKind.DemolishCampfire => !map.Campfires.Any(f => f.TileX == t.TileX && f.TileZ == t.TileZ),
    _ => false,
  };

  // Whether a task's precondition is met right now — Dig/BuildCampfire/
  // DemolishCampfire have none beyond "still a valid target", but Deposit
  // needs at least one unit of material already sitting in the global pool
  // before it's worth sending a builder over.
  public static bool IsClaimable(WorkTask t, TileMap map, Inventory globalInventory)
  {
    if (t.Worker != null || t.Complete) return false;

    return t.Kind switch
    {
      TaskKind.Dig => map.CanDigVoxel(t.FineX, t.FineZ),
      TaskKind.Deposit => map.CanDepositVoxel(t.FineX, t.FineZ) && globalInventory.Has("Dirt", 1),
      TaskKind.BuildCampfire => map.CanPlaceCampfire(t.TileX, t.TileZ),
      TaskKind.DemolishCampfire => true,
      _ => false,
    };
  }

  // Unclaimed, currently-doable tasks in queue order — what an idle builder
  // scans through looking for its next job.
  public IEnumerable<WorkTask> ClaimableInOrder(TileMap map, Inventory globalInventory) =>
    _tasks.Where(t => IsClaimable(t, map, globalInventory));
}
