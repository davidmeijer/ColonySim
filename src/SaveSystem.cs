using System.Numerics;
using Raylib_cs;
using ColonySim.World;
using ColonySim.Entities;
using ColonySim.Tasks;
using ColonySim.Rendering;

namespace ColonySim;

// Reads/writes a whole GameSession to a single binary file: the terrain
// (every fine voxel's height/material/water depth), trees/bushes/campfires/
// springs, actors, the shared inventory and work queue, the sun's clock, and
// the camera — everything Program.Main needs to resume exactly where the
// player left off. Deliberately plain BinaryWriter/BinaryReader rather than
// JSON: a 40x30 map is 120,000 fine columns, and writing each as a handful
// of raw fields is both far more compact and far faster to load than
// round-tripping that many objects through a text format.
public static class SaveSystem
{
  public static readonly string DefaultPath = Path.Combine(AppContext.BaseDirectory, "Saves", "savegame.dat");

  // Bumped whenever the file layout below changes, so a save from an older
  // build fails loudly (see Load) instead of silently misreading fields.
  const uint Magic = 0x4D495343; // "CSIM"
  const int Version = 4;

  public static void Save(string path, Program.GameSession session)
  {
    string? dir = Path.GetDirectoryName(path);
    if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

    using var stream = new FileStream(path, FileMode.Create, FileAccess.Write);
    using var w = new BinaryWriter(stream);

    w.Write(Magic);
    w.Write(Version);
    w.Write(session.Seed);

    var map = session.Map;
    w.Write(map.Width);
    w.Write(map.Depth);

    int fineWidth = map.Width * TileMap.FineSubdivisions;
    int fineDepth = map.Depth * TileMap.FineSubdivisions;
    for (int fx = 0; fx < fineWidth; fx++)
    {
      for (int fz = 0; fz < fineDepth; fz++)
      {
        w.Write(map.VoxelHeightAt(fx, fz));
        w.Write((byte)map.VoxelTopAt(fx, fz));
        w.Write(map.WaterDepthFine(fx, fz));
      }
    }

    w.Write(map.Trees.Count);
    foreach (var t in map.Trees)
    {
      w.Write(t.AnchorFx); w.Write(t.AnchorFz);
      w.Write(t.TrunkHeight); w.Write(t.CanopyHeight);
    }

    w.Write(map.Bushes.Count);
    foreach (var b in map.Bushes)
    {
      w.Write(b.AnchorFx); w.Write(b.AnchorFz);
      w.Write(b.Layers); w.Write(b.SizeVariant); w.Write(b.ColorVariant);
    }

    w.Write(map.Campfires.Count);
    foreach (var c in map.Campfires)
    {
      w.Write(c.AnchorFx); w.Write(c.AnchorFz); w.Write(c.FlickerPhase);
    }

    w.Write(map.Springs.Count);
    foreach (var s in map.Springs)
    {
      w.Write(s.AnchorFx); w.Write(s.AnchorFz);
    }

    w.Write(map.LightPosts.Count);
    foreach (var l in map.LightPosts)
    {
      w.Write(l.AnchorFx); w.Write(l.AnchorFz);
    }

    w.Write(map.StorageBoxes.Count);
    foreach (var b in map.StorageBoxes)
    {
      w.Write(b.AnchorFx); w.Write(b.AnchorFz);
      w.Write(b.Storage.Counts.Count);
      foreach (var (item, count) in b.Storage.Counts)
      {
        w.Write(item); w.Write(count);
      }
    }

    w.Write(session.Actors.Count);
    foreach (var a in session.Actors)
    {
      w.Write(a.FineX); w.Write(a.FineZ); w.Write(a.IsBuilder);
      w.Write(a.Name); w.Write(a.Hunger); w.Write(a.Health);
    }

    w.Write(session.GlobalInventory.Counts.Count);
    foreach (var (item, count) in session.GlobalInventory.Counts)
    {
      w.Write(item); w.Write(count);
    }

    // Worker assignment/progress-toward-arrival aren't saved — a builder
    // mid-route to a task simply re-claims the nearest doable one after
    // load, same as it would after any other manual interruption (see
    // Program.ReleaseAssignment). Progress already accrued on a task IS
    // kept, so a half-dug voxel doesn't lose that work.
    w.Write(session.WorkQueue.Tasks.Count);
    foreach (var t in session.WorkQueue.Tasks)
    {
      w.Write((byte)t.Kind);
      w.Write(t.FineX); w.Write(t.FineZ);
      w.Write(t.StandX); w.Write(t.StandZ);
      w.Write(t.Duration);
      w.Write(t.Progress);
    }

    w.Write(session.Sun.TimeOfDay);
    w.Write(session.Sun.Frozen);
    w.Write(session.Sun.SpeedMultiplier);

    w.Write(session.CamTarget.X); w.Write(session.CamTarget.Y); w.Write(session.CamTarget.Z);
    w.Write(session.CamYaw); w.Write(session.CamPitch); w.Write(session.CamDistance);

    w.Write(session.ShowGrid);
    w.Write(session.ShowPathDots);
  }

  public static Program.GameSession Load(string path)
  {
    using var stream = new FileStream(path, FileMode.Open, FileAccess.Read);
    using var r = new BinaryReader(stream);

    if (r.ReadUInt32() != Magic) throw new InvalidDataException("Not a ColonySim save file.");
    int version = r.ReadInt32();
    if (version != Version) throw new InvalidDataException($"Save file is from an incompatible version ({version}).");

    int seed = r.ReadInt32();
    int width = r.ReadInt32();
    int depth = r.ReadInt32();

    var map = TileMap.CreateForLoad(width, depth);

    int fineWidth = width * TileMap.FineSubdivisions;
    int fineDepth = depth * TileMap.FineSubdivisions;
    for (int fx = 0; fx < fineWidth; fx++)
    {
      for (int fz = 0; fz < fineDepth; fz++)
      {
        int height = r.ReadInt32();
        var top = (TileType)r.ReadByte();
        float water = r.ReadSingle();
        map.SetVoxel(fx, fz, height, top);
        if (water > 0f) map.SetWaterDepth(fx, fz, water);
      }
    }

    int treeCount = r.ReadInt32();
    for (int i = 0; i < treeCount; i++)
      map.AddLoadedTree(new Tree(r.ReadInt32(), r.ReadInt32(), r.ReadInt32(), r.ReadInt32()));

    int bushCount = r.ReadInt32();
    for (int i = 0; i < bushCount; i++)
      map.AddLoadedBush(new Bush(r.ReadInt32(), r.ReadInt32(), r.ReadInt32(), r.ReadInt32(), r.ReadInt32()));

    int campfireCount = r.ReadInt32();
    for (int i = 0; i < campfireCount; i++)
      map.AddLoadedCampfire(new Campfire(r.ReadInt32(), r.ReadInt32(), r.ReadSingle()));

    int springCount = r.ReadInt32();
    for (int i = 0; i < springCount; i++)
      map.AddLoadedSpring(new Spring(r.ReadInt32(), r.ReadInt32()));

    int lightPostCount = r.ReadInt32();
    for (int i = 0; i < lightPostCount; i++)
      map.AddLoadedLightPost(new LightPost(r.ReadInt32(), r.ReadInt32()));

    int storageBoxCount = r.ReadInt32();
    for (int i = 0; i < storageBoxCount; i++)
    {
      int boxFx = r.ReadInt32(), boxFz = r.ReadInt32();
      var storage = new Inventory(StorageBox.Capacity);
      int boxItemCount = r.ReadInt32();
      for (int j = 0; j < boxItemCount; j++)
        storage.Add(r.ReadString(), r.ReadInt32());
      map.AddLoadedStorageBox(new StorageBox(boxFx, boxFz, storage));
    }

    map.FinishLoading();

    int actorCount = r.ReadInt32();
    var actors = new List<Actor>(actorCount);
    for (int i = 0; i < actorCount; i++)
    {
      int fx = r.ReadInt32(), fz = r.ReadInt32();
      bool isBuilder = r.ReadBoolean();
      string name = r.ReadString();
      float hunger = r.ReadSingle(), health = r.ReadSingle();
      var actor = new Actor(map, fx, fz, name) { IsBuilder = isBuilder };
      actor.RestoreNeeds(hunger, health);
      actors.Add(actor);
    }

    var globalInventory = new Inventory(capacity: 100_000);
    int itemCount = r.ReadInt32();
    for (int i = 0; i < itemCount; i++)
    {
      string item = r.ReadString();
      int count = r.ReadInt32();
      globalInventory.Add(item, count);
    }

    var workQueue = new WorkQueue();
    int taskCount = r.ReadInt32();
    for (int i = 0; i < taskCount; i++)
    {
      var kind = (TaskKind)r.ReadByte();
      int fineX = r.ReadInt32(), fineZ = r.ReadInt32();
      int standX = r.ReadInt32(), standZ = r.ReadInt32();
      float duration = r.ReadSingle();
      float progress = r.ReadSingle();

      var task = new WorkTask(kind, fineX, fineZ, standX, standZ, duration) { Progress = progress };
      workQueue.Enqueue(task);
    }

    var sun = new SunLight();
    map.SetTerrainShader(sun.Shader);
    sun.TimeOfDay = r.ReadSingle();
    sun.Frozen = r.ReadBoolean();
    sun.SpeedMultiplier = r.ReadSingle();

    var camTarget = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
    float camYaw = r.ReadSingle(), camPitch = r.ReadSingle(), camDistance = r.ReadSingle();

    bool showGrid = r.ReadBoolean();
    bool showPathDots = r.ReadBoolean();

    var session = new Program.GameSession
    {
      Map = map,
      Actors = actors,
      WorkQueue = workQueue,
      GlobalInventory = globalInventory,
      Sun = sun,
      Seed = seed,
      CamTarget = camTarget,
      CamYaw = camYaw,
      CamPitch = camPitch,
      CamDistance = camDistance,
      ShowGrid = showGrid,
      ShowPathDots = showPathDots,
      Camera = new Camera3D
      {
        Target = camTarget,
        Up = new Vector3(0f, 1f, 0f),
        FovY = 45f,
        Projection = CameraProjection.Perspective,
      },
    };
    return session;
  }
}
