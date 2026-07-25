using ColonySim.Entities;
using ColonySim.World;

namespace ColonySim.Tasks;

// One queued work order. Plain data + a bit of bookkeeping — no gameplay
// logic lives here, that's all in WorkQueue/Program.UpdateBuilders so this
// stays trivial to list/cancel/progress-bar from the UI.
public class WorkTask
{
  public TaskKind Kind { get; }

  // The coarse tile the action happens in — for Dig/Deposit this is just
  // the tile containing FineX/FineZ (a builder can dig/deposit any fine
  // column from anywhere on its parent tile, same as the old whole-tile
  // Dig always could); for the campfire kinds it's the fire's own tile.
  public int TileX { get; }
  public int TileZ { get; }

  // Where the worker has to be standing to perform the work — same as
  // TileX/TileZ for every kind except DemolishCampfire, whose target tile
  // is itself unwalkable (the fire is standing on it).
  public int StandX { get; }
  public int StandZ { get; }

  // The exact fine voxel column targeted — meaningful only for Dig/Deposit.
  public int FineX { get; }
  public int FineZ { get; }

  // How long, in seconds of continuous work, this task takes once a worker
  // has arrived. Computed once by the caller at construction time.
  public float Duration { get; }

  // Seconds of work accrued so far by whichever actor is currently on it.
  public float Progress { get; set; }

  // True once the assigned worker has actually arrived and started
  // swinging — distinguishes "still walking over" from "working" for the
  // progress bar and for UpdateBuilders' state machine.
  public bool Started { get; set; }

  public Actor? Worker { get; set; }
  public bool Complete { get; set; }

  WorkTask(TaskKind kind, int tileX, int tileZ, int standX, int standZ, int fineX, int fineZ, float duration)
  {
    Kind = kind;
    TileX = tileX;
    TileZ = tileZ;
    StandX = standX;
    StandZ = standZ;
    FineX = fineX;
    FineZ = fineZ;
    Duration = duration;
  }

  // Dig/Deposit: targets one exact fine voxel column. The containing
  // coarse tile doubles as the stand tile — a builder can work any of its
  // 100 fine columns from anywhere on it.
  public static WorkTask ForVoxel(TaskKind kind, int fineX, int fineZ, float duration)
  {
    int tileX = fineX / TileMap.FineSubdivisions;
    int tileZ = fineZ / TileMap.FineSubdivisions;
    return new WorkTask(kind, tileX, tileZ, tileX, tileZ, fineX, fineZ, duration);
  }

  // BuildCampfire/DemolishCampfire: targets a whole coarse tile; FineX/FineZ
  // are unused. standX/standZ is usually the same tile (BuildCampfire) but
  // can be an adjacent one (DemolishCampfire, since the fire itself blocks
  // its own tile).
  public static WorkTask ForCampfire(TaskKind kind, int tileX, int tileZ, int standX, int standZ, float duration) =>
    new(kind, tileX, tileZ, standX, standZ, fineX: 0, fineZ: 0, duration);

  // Human-readable label for the task queue panel.
  public string Label => Kind switch
  {
    TaskKind.Dig => $"Dig voxel ({FineX},{FineZ})",
    TaskKind.Deposit => $"Deposit voxel ({FineX},{FineZ})",
    TaskKind.BuildCampfire => $"Build campfire ({TileX},{TileZ})",
    TaskKind.DemolishCampfire => $"Demolish campfire ({TileX},{TileZ})",
    _ => Kind.ToString(),
  };
}
