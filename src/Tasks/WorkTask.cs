using ColonySim.Entities;

namespace ColonySim.Tasks;

// One queued work order. Plain data + a bit of bookkeeping — no gameplay
// logic lives here, that's all in WorkQueue/Program.UpdateBuilders so this
// stays trivial to list/cancel/progress-bar from the UI.
public class WorkTask
{
  public TaskKind Kind { get; }

  // The fine voxel this task targets — the exact voxel for Dig/Deposit, or
  // the footprint anchor for a Campfire/Spring/LightPost build/demolish.
  public int FineX { get; }
  public int FineZ { get; }

  // The fine voxel a worker has to be standing on to perform the work.
  // Equal to FineX/FineZ for Dig/Deposit and for any build/demolish whose
  // target never blocks its own footprint (Spring, in both directions —
  // see Spring.Height). Resolved to a nearby free voxel instead whenever
  // the target's footprint is (or is about to become) blocked: Demolish*
  // because the structure is already standing there, and BuildCampfire/
  // BuildLightPost because the structure gets placed — and blocks its own
  // anchor — the moment the task completes, which would otherwise wall the
  // worker in right where it's standing.
  public int StandX { get; }
  public int StandZ { get; }

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

  public WorkTask(TaskKind kind, int fineX, int fineZ, int standX, int standZ, float duration)
  {
    Kind = kind;
    FineX = fineX;
    FineZ = fineZ;
    StandX = standX;
    StandZ = standZ;
    Duration = duration;
  }

  // Human-readable label for the task queue panel.
  public string Label => Kind switch
  {
    TaskKind.Dig => $"Dig voxel ({FineX},{FineZ})",
    TaskKind.Deposit => $"Deposit voxel ({FineX},{FineZ})",
    TaskKind.BuildCampfire => $"Build campfire ({FineX},{FineZ})",
    TaskKind.DemolishCampfire => $"Demolish campfire ({FineX},{FineZ})",
    TaskKind.BuildSpring => $"Dig spring ({FineX},{FineZ})",
    TaskKind.DemolishSpring => $"Cap spring ({FineX},{FineZ})",
    TaskKind.BuildLightPost => $"Build light post ({FineX},{FineZ})",
    TaskKind.DemolishLightPost => $"Remove light post ({FineX},{FineZ})",
    TaskKind.BuildStorageBox => $"Build storage box ({FineX},{FineZ})",
    TaskKind.DemolishStorageBox => $"Demolish storage box ({FineX},{FineZ})",
    TaskKind.HarvestBush => $"Harvest bush ({FineX},{FineZ})",
    _ => Kind.ToString(),
  };
}
