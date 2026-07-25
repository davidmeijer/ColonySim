using System.IO;
using System.Numerics;
using Raylib_cs;
using ColonySim.World;
using ColonySim.Entities;
using ColonySim.Pathfinding;
using ColonySim.Rendering;
using ColonySim.Tasks;

namespace ColonySim;

public static class Program
{
    const int ScreenWidth = 1280;
    const int ScreenHeight = 720;
    const int ActorCount = 3;
    const int WorldWidth = 40;
    const int WorldDepth = 30;

    // Everything a running game needs — built fresh by NewGame or restored
    // by SaveSystem.Load, and torn down by Unload when the window closes or
    // the player returns to the main menu. Plain public fields (not
    // properties) throughout: Update/Draw's camera parameters are `ref`,
    // which only works against a field, not a property.
    public class GameSession
    {
        public TileMap Map = null!;
        public List<Actor> Actors = null!;
        public SelectionState Selection = new();
        public SettingsMenuState SettingsMenu = new();
        public BuildMenuState BuildMenu = new();
        public WorkQueue WorkQueue = new();
        public Inventory GlobalInventory = new(capacity: 100_000);
        public SunLight Sun = null!;
        public Camera3D Camera;
        public Vector3 CamTarget;
        public float CamYaw = 45f;
        public float CamPitch = 50f;
        public float CamDistance = 620f;
        public bool ShowGrid;
        public bool ShowPathDots;
        public int Seed;

        // A brief "Saved!" confirmation next to the save button — set by
        // UpdateSaveButton, counted down and drawn each frame.
        public string? SaveMessage;
        public float SaveMessageTimer;

        public static GameSession NewGame(int seed)
        {
            var map = new TileMap(WorldWidth, WorldDepth, seed);

            var spawnRng = new Random(99);
            var spawnTiles = map.WalkableTiles().OrderBy(_ => spawnRng.Next()).Take(ActorCount).ToList();
            var actors = spawnTiles.Select(t => new Actor(map, t.X, t.Y)).ToList();

            var sun = new SunLight();
            map.SetTerrainShader(sun.Shader);

            var camTarget = new Vector3(map.WidthPx / 2f, 0f, map.DepthPx / 2f);

            return new GameSession
            {
                Map = map,
                Actors = actors,
                Sun = sun,
                Seed = seed,
                CamTarget = camTarget,
                Camera = new Camera3D
                {
                    Target = camTarget,
                    Up = new Vector3(0f, 1f, 0f),
                    FovY = 45f,
                    Projection = CameraProjection.Perspective,
                },
            };
        }

        public void Save(string path) => SaveSystem.Save(path, this);

        public void Unload()
        {
            Sun.Unload();
            Map.Unload();
        }
    }

    // The startup screen's own input state: the seed text box, and any
    // error to show if a load fails.
    class MainMenuState
    {
        public string SeedText = "1234";
        public bool SeedFieldFocused;
        public string? StatusMessage;
    }

    // How far (in screen pixels) the mouse has to move while a button is held
    // before a press+release counts as a drag instead of a click.
    const float DragThreshold = 6f;

    // Mutable input/selection state for the frame loop. Bundled into one
    // object instead of a growing pile of ref parameters.
    public class SelectionState
    {
        public readonly HashSet<Actor> Selected = new();

        public Vector2? LeftDownPos;
        public bool BoxSelecting;

        public Vector2? RightDownPos;
        public bool RightDragged;
    }

    // The action menu: a few buttons that appear along the bottom of the
    // screen whenever at least one actor is selected. Fixed screen positions,
    // so Update (input) and Draw (rendering) can each compute the same
    // rectangles independently without sharing extra layout state.
    static readonly Rectangle JumpButtonRect = new(10, ScreenHeight - 66, 80, 28);

    // Toggles Actor.IsBuilder for the whole current selection — the only
    // way actors get assigned work now; see Program.UpdateBuilders.
    static readonly Rectangle BuilderButtonRect = new(100, ScreenHeight - 66, 100, 28);

    // Top-right gear icon, always visible, that opens/closes the settings
    // panel below it. Everything in the panel (grid toggle, path-dots
    // toggle, day/night freeze, time-of-day and speed sliders) is laid out
    // relative to it.
    static readonly Rectangle SettingsButtonRect = new(ScreenWidth - 46, 10, 36, 36);

    const float PanelWidth = 260f;
    const float PanelX = ScreenWidth - PanelWidth - 10f;
    const float PanelY = 56f;
    const float PanelHeight = 230f;

    static readonly Rectangle SettingsPanelRect = new(PanelX, PanelY, PanelWidth, PanelHeight);
    static readonly Rectangle GridButtonRect = new(PanelX + 14, PanelY + 34, 108, 28);
    static readonly Rectangle FreezeButtonRect = new(PanelX + 138, PanelY + 34, 108, 28);
    static readonly Rectangle PathDotsButtonRect = new(PanelX + 14, PanelY + 70, PanelWidth - 28, 28);

    static readonly Rectangle TimeSliderTrack = new(PanelX + 14, PanelY + 134, PanelWidth - 28, 6);
    static readonly Rectangle TimeSliderHitRect = new(TimeSliderTrack.X, TimeSliderTrack.Y - 10, TimeSliderTrack.Width, 26);

    static readonly Rectangle SpeedSliderTrack = new(PanelX + 14, PanelY + 202, PanelWidth - 28, 6);
    static readonly Rectangle SpeedSliderHitRect = new(SpeedSliderTrack.X, SpeedSliderTrack.Y - 10, SpeedSliderTrack.Width, 26);

    const float MinSpeed = 0f;
    const float MaxSpeed = 5f;

    // State for the settings panel: whether it's open, and whether a slider
    // inside it is currently being dragged (has to persist across frames,
    // same reason SelectionState does).
    public class SettingsMenuState
    {
        public bool Open;
        public bool DraggingTime;
        public bool DraggingSpeed;
    }

    // Save button: always visible during play, off to the right of the HUD
    // text and well clear of the build palette so it never overlaps either.
    static readonly Rectangle SaveButtonRect = new(320, 10, 90, 36);
    const float SaveMessageDuration = 2f;

    // --- Main menu (startup screen) -----------------------------------------
    static readonly Rectangle SeedBoxRect = new(ScreenWidth / 2f - 110, 320, 220, 36);
    static readonly Rectangle NewGameButtonRect = new(ScreenWidth / 2f - 110, 378, 220, 42);
    static readonly Rectangle LoadGameButtonRect = new(ScreenWidth / 2f - 110, 430, 220, 42);
    const int MaxSeedDigits = 9;

    // Top build icon (kept clear of the "Selected: x/y" HUD text to its
    // left, the settings gear at the opposite corner, and — since its own
    // panel opens directly beneath it — clear of the task queue panel's
    // footprint too) that opens a small palette of work-order tools.
    // Picking one arms ArmedTool — the next left-click on a valid tile
    // queues a WorkTask there instead of touching the map directly (see
    // WorkQueue / Program.UpdateBuilders).
    static readonly Rectangle BuildButtonRect = new(260, 10, 36, 36);

    // Which tool (if any) is currently armed from the build palette.
    public enum ToolKind { None, Campfire, DemolishCampfire, Spring, DemolishSpring, Dig, Deposit }

    const float BuildPanelWidth = 150f;
    const float BuildPanelX = 260f;
    const float BuildPanelY = 56f;
    const int BuildPaletteRows = 6;
    const float BuildRowHeight = 46f;
    static readonly Rectangle BuildPanelRect = new(BuildPanelX, BuildPanelY, BuildPanelWidth, 8f + BuildPaletteRows * BuildRowHeight);

    static Rectangle BuildPaletteItemRect(int row) => new(BuildPanelX + 8, BuildPanelY + 8 + row * BuildRowHeight, BuildPanelWidth - 16, 38);
    static readonly Rectangle CampfireItemRect = BuildPaletteItemRect(0);
    static readonly Rectangle DemolishCampfireItemRect = BuildPaletteItemRect(1);
    static readonly Rectangle SpringItemRect = BuildPaletteItemRect(2);
    static readonly Rectangle DemolishSpringItemRect = BuildPaletteItemRect(3);
    static readonly Rectangle DigItemRect = BuildPaletteItemRect(4);
    static readonly Rectangle DepositItemRect = BuildPaletteItemRect(5);

    public class BuildMenuState
    {
        public bool Open;
        public ToolKind ArmedTool = ToolKind.None;

        // Voxel drag-select state (Dig/Deposit only): where the drag
        // started — in both screen space (to measure the drag threshold,
        // same as every other drag gesture in this file) and fine-voxel
        // space (the actual selection) — and whether it's turned into a
        // real drag yet. Mirrors SelectionState's actor box-select, just
        // picking fine voxels instead of actors.
        public Vector2? LeftDownScreenPos;
        public (int Fx, int Fz)? DragStartVoxel;
        public bool VoxelDragging;

        // Tracked separately from SelectionState's identical right-click
        // fields so a right-drag to orbit the camera while lining up a
        // placement doesn't get mistaken for the cancel gesture below.
        public Vector2? RightDownPos;
        public bool RightDragged;
    }

    // --- Task queue panel (left edge) --------------------------------------
    const float QueuePanelX = 10f;
    const float QueuePanelY = 56f;
    const float QueuePanelWidth = 230f;
    const float QueueRowHeight = 40f;
    const int QueueMaxVisibleRows = 10;

    static Rectangle TaskQueuePanelRect(int taskCount)
    {
        int rows = Math.Min(taskCount, QueueMaxVisibleRows);
        float height = 34f + Math.Max(rows, 1) * QueueRowHeight;
        return new Rectangle(QueuePanelX, QueuePanelY, QueuePanelWidth, height);
    }

    static Rectangle TaskQueueCancelRect(int row) =>
        new(QueuePanelX + QueuePanelWidth - 26, QueuePanelY + 34 + row * QueueRowHeight + 4, 18, 18);

    // --- Work task timing/tuning --------------------------------------------
    // Dig/Deposit always move exactly one voxel, so their duration is just
    // a flat per-action time rather than scaling with any distance/count.
    const float DigVoxelDuration = 0.15f;
    const float DepositVoxelDuration = 0.15f;
    const float BuildCampfireDuration = 1.5f;
    const float DemolishCampfireDuration = 1f;

    // Digging down to the water table is real work, so a spring takes
    // noticeably longer to open than a campfire takes to lay.
    const float BuildSpringDuration = 4f;
    const float DemolishSpringDuration = 2f;

    // Safety clamp on a single drag-select's footprint (Dig/Deposit) — the
    // queue and per-frame builder scans are all O(task count), so a stray
    // drag across most of the map shouldn't be allowed to create tens of
    // thousands of tasks in one go.
    const int MaxDragVoxelsPerAxis = 60;

    public static void Main()
    {
        // Create the OS window and cap the loop at 60 frames per second.
        Raylib.InitWindow(ScreenWidth, ScreenHeight, "Colony Sim - v0.3 (3D)");
        Raylib.SetTargetFPS(60);

        var menu = new MainMenuState();

        // Null until the player either starts a new game or loads one from
        // the startup screen — see UpdateMainMenu. Everything below only
        // runs once a session actually exists.
        GameSession? session = null;

        // The main loop. WindowShouldClose() becomes true when the user hits the
        // window's close button or presses ESC.
        while (!Raylib.WindowShouldClose())
        {
            // dt = "delta time": how many seconds the last frame took. Multiplying
            // movement by dt makes speeds frame-rate independent.
            float dt = Raylib.GetFrameTime();

            if (session == null)
            {
                UpdateMainMenu(menu, ref session);
                DrawMainMenu(menu);
                continue;
            }

            Update(dt, session, session.Map, session.Actors, session.Selection, session.SettingsMenu, session.BuildMenu,
                session.WorkQueue, session.GlobalInventory, session.Sun,
                ref session.Camera, ref session.CamTarget, ref session.CamYaw, ref session.CamPitch, ref session.CamDistance,
                ref session.ShowGrid, ref session.ShowPathDots);
            session.Map.UpdateWater(dt);
            session.Map.UpdateVegetation(dt);
            session.Sun.Update(dt);
            if (session.SaveMessageTimer > 0f) session.SaveMessageTimer = Math.Max(0f, session.SaveMessageTimer - dt);

            Draw(session, session.Map, session.Actors, session.Selection, session.SettingsMenu, session.BuildMenu,
                session.WorkQueue, session.GlobalInventory, session.Camera, session.Sun, session.ShowGrid, session.ShowPathDots);
        }

        session?.Unload();
        Raylib.CloseWindow();
    }

    // The startup screen's own input handling: typing/backspacing digits
    // into the seed box, and the two buttons that either spin up a fresh
    // GameSession from that seed or restore one from the save file. Assigns
    // `session` on success, which is what switches Main's loop over to
    // actual gameplay from the next frame on.
    static void UpdateMainMenu(MainMenuState menu, ref GameSession? session)
    {
        Vector2 mouse = Raylib.GetMousePosition();
        bool leftPressed = Raylib.IsMouseButtonPressed(MouseButton.Left);

        if (leftPressed)
            menu.SeedFieldFocused = Raylib.CheckCollisionPointRec(mouse, SeedBoxRect);

        if (menu.SeedFieldFocused)
        {
            int key = Raylib.GetCharPressed();
            while (key > 0)
            {
                if (key >= '0' && key <= '9' && menu.SeedText.Length < MaxSeedDigits)
                    menu.SeedText += (char)key;
                key = Raylib.GetCharPressed();
            }

            if (Raylib.IsKeyPressed(KeyboardKey.Backspace) && menu.SeedText.Length > 0)
                menu.SeedText = menu.SeedText[..^1];
        }

        bool saveExists = File.Exists(SaveSystem.DefaultPath);

        if (!leftPressed) return;

        if (Raylib.CheckCollisionPointRec(mouse, NewGameButtonRect))
        {
            int seed = int.TryParse(menu.SeedText, out int parsed) ? parsed : Environment.TickCount;
            session = GameSession.NewGame(seed);
        }
        else if (saveExists && Raylib.CheckCollisionPointRec(mouse, LoadGameButtonRect))
        {
            try
            {
                session = SaveSystem.Load(SaveSystem.DefaultPath);
            }
            catch (Exception ex)
            {
                menu.StatusMessage = $"Couldn't load save: {ex.Message}";
            }
        }
    }

    static void DrawMainMenu(MainMenuState menu)
    {
        Raylib.BeginDrawing();
        Raylib.ClearBackground(new Color(38, 58, 40, 255));

        const string title = "Colony Sim";
        int titleWidth = Raylib.MeasureText(title, 48);
        Raylib.DrawText(title, (ScreenWidth - titleWidth) / 2, 180, 48, Color.White);

        Raylib.DrawText("Seed", (int)SeedBoxRect.X, (int)SeedBoxRect.Y - 22, 16, Color.White);
        Raylib.DrawRectangleRec(SeedBoxRect, Color.White);
        Raylib.DrawRectangleLinesEx(SeedBoxRect, 2f, menu.SeedFieldFocused ? Color.SkyBlue : Color.DarkGray);
        Raylib.DrawText(menu.SeedText, (int)SeedBoxRect.X + 10, (int)SeedBoxRect.Y + 8, 20, Color.Black);

        DrawMenuButton(NewGameButtonRect, "New Game", enabled: true);

        bool saveExists = File.Exists(SaveSystem.DefaultPath);
        DrawMenuButton(LoadGameButtonRect, saveExists ? "Load Game" : "Load Game (none saved)", saveExists);

        if (menu.StatusMessage is { } status)
        {
            int w = Raylib.MeasureText(status, 16);
            Raylib.DrawText(status, (ScreenWidth - w) / 2, (int)LoadGameButtonRect.Y + 60, 16, new Color(255, 160, 120, 255));
        }

        Raylib.EndDrawing();
    }

    static void DrawMenuButton(Rectangle rect, string label, bool enabled)
    {
        bool hovered = enabled && Raylib.CheckCollisionPointRec(Raylib.GetMousePosition(), rect);
        Color fill = !enabled
            ? new Color(130, 130, 130, 255)
            : hovered ? new Color(140, 200, 140, 255) : new Color(200, 230, 200, 255);
        Raylib.DrawRectangleRec(rect, fill);
        Raylib.DrawRectangleLinesEx(rect, 2f, enabled ? Color.DarkGreen : Color.Gray);

        int textWidth = Raylib.MeasureText(label, 18);
        Raylib.DrawText(label, (int)(rect.X + (rect.Width - textWidth) / 2f), (int)(rect.Y + 12), 18, Color.Black);
    }

    // The always-visible in-game save button: writes the whole session to
    // SaveSystem.DefaultPath and pops up a brief confirmation. Returns
    // whether this frame's click landed on it, so the caller can keep it
    // out of world/selection clicks the same way every other UI element here does.
    static bool UpdateSaveButton(GameSession session)
    {
        if (!Raylib.IsMouseButtonPressed(MouseButton.Left)) return false;
        if (!Raylib.CheckCollisionPointRec(Raylib.GetMousePosition(), SaveButtonRect)) return false;

        session.Save(SaveSystem.DefaultPath);
        session.SaveMessage = "Saved!";
        session.SaveMessageTimer = SaveMessageDuration;
        return true;
    }

    static void DrawSaveButton(GameSession session)
    {
        DrawButton(SaveButtonRect, "Save");

        if (session.SaveMessageTimer > 0f && session.SaveMessage is { } message)
            Raylib.DrawText(message, (int)SaveButtonRect.X, (int)(SaveButtonRect.Y + SaveButtonRect.Height + 4), 14, Color.DarkGreen);
    }

    static void Update(float dt, GameSession session, TileMap map, List<Actor> actors, SelectionState selection, SettingsMenuState settingsMenu,
        BuildMenuState buildMenu, WorkQueue workQueue, Inventory globalInventory, SunLight sun, ref Camera3D camera, ref Vector3 camTarget, ref float yaw, ref float pitch,
        ref float distance, ref bool showGrid, ref bool showPathDots)
    {
        // --- Zoom with the mouse wheel, clamped to a sane range ---
        float wheel = Raylib.GetMouseWheelMove();
        if (wheel != 0)
            distance = Math.Clamp(distance - wheel * 40f, 150f, 1400f);

        // --- Orbit by holding the right mouse button and dragging (needs a mouse) ---
        if (Raylib.IsMouseButtonDown(MouseButton.Right))
        {
            Vector2 mouseDelta = Raylib.GetMouseDelta();
            yaw -= mouseDelta.X * 0.3f;
            pitch = Math.Clamp(pitch - mouseDelta.Y * 0.3f, 15f, 85f);
        }

        // --- Orbit with the keyboard too, since a trackpad right-drag isn't obvious ---
        float rotateSpeed = 90f * dt;
        if (Raylib.IsKeyDown(KeyboardKey.Q)) yaw -= rotateSpeed;
        if (Raylib.IsKeyDown(KeyboardKey.E)) yaw += rotateSpeed;
        if (Raylib.IsKeyDown(KeyboardKey.R)) pitch = Math.Clamp(pitch + rotateSpeed, 15f, 85f);
        if (Raylib.IsKeyDown(KeyboardKey.F)) pitch = Math.Clamp(pitch - rotateSpeed, 15f, 85f);

        // --- Pan the camera's look-at point with WASD or the arrow keys ---
        // Relative to which way the camera is currently facing (yaw), so "D"
        // always means "screen right", even after rotating with Q/E.
        float yawRad = yaw * (MathF.PI / 180f);
        Vector3 forward = new(-MathF.Sin(yawRad), 0f, -MathF.Cos(yawRad));
        Vector3 right = new(MathF.Cos(yawRad), 0f, -MathF.Sin(yawRad));

        float panSpeed = 260f * dt;
        if (Raylib.IsKeyDown(KeyboardKey.W) || Raylib.IsKeyDown(KeyboardKey.Up))    camTarget += forward * panSpeed;
        if (Raylib.IsKeyDown(KeyboardKey.S) || Raylib.IsKeyDown(KeyboardKey.Down))  camTarget -= forward * panSpeed;
        if (Raylib.IsKeyDown(KeyboardKey.A) || Raylib.IsKeyDown(KeyboardKey.Left))  camTarget -= right * panSpeed;
        if (Raylib.IsKeyDown(KeyboardKey.D) || Raylib.IsKeyDown(KeyboardKey.Right)) camTarget += right * panSpeed;

        // Recompute the camera position from target + spherical angles.
        float pitchRad = pitch * (MathF.PI / 180f);
        Vector3 offset = new(
            distance * MathF.Cos(pitchRad) * MathF.Sin(yawRad),
            distance * MathF.Sin(pitchRad),
            distance * MathF.Cos(pitchRad) * MathF.Cos(yawRad));

        camera.Target = camTarget;
        camera.Position = camTarget + offset;

        bool shiftHeld = Raylib.IsKeyDown(KeyboardKey.LeftShift) || Raylib.IsKeyDown(KeyboardKey.RightShift);

        // The build menu is checked first: while a tool is armed, it
        // swallows every click itself, so the settings panel and action menu
        // are skipped entirely for that frame — otherwise a placement click
        // that happens to land over one of their buttons (bottom-left
        // action menu, top-right settings) would fire that button instead of
        // placing anything.
        bool armed = buildMenu.ArmedTool != ToolKind.None;
        bool buildConsumedClick = UpdateBuildMenu(map, workQueue, buildMenu, camera);

        // The task queue panel (left edge) needs the same treatment: a click
        // on its cancel buttons shouldn't also fall through to a world click.
        bool queueConsumedClick = !armed && UpdateTaskQueuePanel(workQueue);

        // The settings panel lives outside the selection-driven action menu
        // (it has to work with nothing selected), but its clicks still need
        // consuming for the same reason: otherwise they'd also register as
        // world clicks and stomp the selection.
        bool settingsConsumedClick = !armed && UpdateSettingsMenu(sun, settingsMenu, ref showGrid, ref showPathDots);

        // Same story for the save button — a click on it shouldn't also
        // clear whatever's currently selected.
        bool saveConsumedClick = !armed && UpdateSaveButton(session);

        // Action-menu clicks are handled first and, if one lands, consumed —
        // otherwise clicking "Jump" would also register as a world click and
        // immediately clear the very selection you just acted on.
        bool menuConsumedClick = !armed && UpdateActionMenu(map, selection);
        UpdateSelection(actors, selection, camera, shiftHeld,
            menuConsumedClick || settingsConsumedClick || buildConsumedClick || queueConsumedClick || saveConsumedClick);
        if (!armed) UpdateMoveOrders(map, selection, camera, workQueue);

        // Everyone seeks their own next waypoint independently — no per-tile
        // locking — then overlaps get shoved apart, then anyone who's made
        // no real progress for a while (stuck against something a shove
        // can't clear) gets a fresh route. Together this is what lets a
        // whole group arrive without freezing solid or leaving stragglers
        // parked mid-route.
        foreach (var actor in actors) actor.Update(dt);
        ResolveOverlaps(actors);
        RepathStuckActors(map, actors);
        UpdateBuilders(map, actors, workQueue, globalInventory, dt);
        UpdateWandering(map, actors, workQueue);
    }

    // Soft collision: pushes any pair of actors that end up closer than two
    // radii back apart, split evenly, in the horizontal plane only (height
    // always comes from the terrain). A few relaxation passes keep a knot of
    // 3+ actors from jittering instead of settling.
    static void ResolveOverlaps(List<Actor> actors)
    {
        const float MinDist = Actor.Radius * 2f;

        for (int pass = 0; pass < 3; pass++)
        {
            for (int i = 0; i < actors.Count; i++)
            {
                for (int j = i + 1; j < actors.Count; j++)
                {
                    Vector3 delta3 = actors[j].WorldPos - actors[i].WorldPos;
                    Vector2 delta = new(delta3.X, delta3.Z);
                    float dist = delta.Length();
                    if (dist >= MinDist) continue;

                    // Coincident centres (rare) push along an arbitrary axis
                    // rather than dividing by zero.
                    Vector2 pushDir = dist > 0.0001f ? delta / dist : new Vector2(1f, 0f);
                    Vector2 correction = pushDir * ((MinDist - dist) * 0.5f);

                    actors[i].Nudge(-correction);
                    actors[j].Nudge(correction);
                }
            }
        }
    }

    // Anyone who's been stuck for too long (see Actor.Stuck) gets a brand new
    // route to the same final destination. Uses Reroute (not SetPath) so
    // repeated failures still count toward Actor's own give-up timeout
    // instead of resetting it every try. If no route exists any more, this
    // just leaves them stopped where they are.
    static void RepathStuckActors(TileMap map, List<Actor> actors)
    {
        foreach (var actor in actors)
        {
            if (!actor.Stuck || actor.FinalDestination is not { } dest) continue;

            var path = AStar.FindPath(map, actor.TileX, actor.TileZ, dest.X, dest.Y);
            actor.Reroute(path);
        }
    }

    // Drops whatever WorkTask this actor currently holds (if any) back into
    // the unclaimed pool — its Progress is left untouched, so another
    // builder can pick it up later without losing what's already been done.
    // Called whenever a manual move order interrupts a builder mid-task.
    static void ReleaseAssignment(WorkQueue workQueue, Actor actor)
    {
        var task = workQueue.Tasks.FirstOrDefault(t => t.Worker == actor);
        if (task == null) return;

        task.Worker = null;
        task.Started = false;
        task.Progress = 0f;
        actor.StopWorking();
    }

    // Every IsBuilder actor either keeps working whatever WorkTask it's
    // already holding, or — once truly idle — claims the next one it can
    // actually do. Runs after RepathStuckActors (so a builder's own
    // stuck-recovery has already happened this frame) and before
    // UpdateWandering (so a builder with real work available never gets
    // sent ambling instead of doing it — once TryAssignTask gives it a
    // path, IsMoving is true and WantsToWander naturally no longer holds).
    static void UpdateBuilders(TileMap map, List<Actor> actors, WorkQueue workQueue, Inventory globalInventory, float dt)
    {
        workQueue.Prune(map);

        foreach (var actor in actors)
        {
            if (!actor.IsBuilder) continue;

            var task = workQueue.Tasks.FirstOrDefault(t => t.Worker == actor);
            if (task == null)
            {
                if (!actor.IsMoving) TryAssignTask(map, workQueue, globalInventory, actor);
                continue;
            }

            bool arrived = !actor.IsMoving && actor.TileX == task.StandX && actor.TileZ == task.StandZ;
            if (!arrived) continue;

            if (!task.Started)
            {
                actor.StartWorking(IsConstructive(task));
                task.Started = true;
            }

            task.Progress += dt;
            if (task.Progress < task.Duration) continue;

            // At/past the required work time — try to actually apply the
            // effect. Dig always succeeds; Deposit can come up short on
            // material and just keeps waiting here, retried every
            // subsequent frame, until the pool refills.
            if (TryComplete(task, map, globalInventory))
            {
                actor.StopWorking();
                task.Complete = true;
            }
        }

        // Drop anything that finished (or was satisfied by something else)
        // this frame so the queue panel and in-world bars never show stale
        // work for even one extra frame.
        workQueue.Prune(map);
    }

    // Scans the queue in order for the first unclaimed, currently-doable
    // task this actor can actually path to, claims it, and sends it on its
    // way — same AStar/SetPath plumbing as a manual move order. Leaves the
    // actor idle for this frame if nothing qualifies.
    static void TryAssignTask(TileMap map, WorkQueue workQueue, Inventory globalInventory, Actor actor)
    {
        // Actors don't block each other's movement any more, but a work
        // stand tile is a fixed exact point (the tile's centre) two
        // builders would otherwise walk straight on top of each other to
        // reach — each endlessly re-approaching the same spot ResolveOverlaps
        // just shoved them off of. So this one exception still applies:
        // never send two actors to work the same tile at the same time.
        var takenStandTiles = workQueue.Tasks
            .Where(t => t.Worker != null && t.Worker != actor)
            .Select(t => (t.StandX, t.StandZ))
            .ToHashSet();

        foreach (var candidate in workQueue.ClaimableInOrder(map, globalInventory))
        {
            if (takenStandTiles.Contains((candidate.StandX, candidate.StandZ))) continue;

            var path = AStar.FindPath(map, actor.TileX, actor.TileZ, candidate.StandX, candidate.StandZ);
            if (path.Count == 0) continue;

            candidate.Worker = actor;
            candidate.Started = false;
            candidate.Progress = 0f;
            actor.SetPath(path);
            return;
        }
    }

    // Whether a task's work animation should be the constructive
    // (deposit-lean) pose or the destructive (dig-chop) one.
    static bool IsConstructive(WorkTask task) => task.Kind switch
    {
        TaskKind.BuildCampfire or TaskKind.BuildSpring or TaskKind.Deposit => true,
        _ => false,
    };

    // Applies a finished task's effect. Returns false only for Deposit
    // running out of material right at the moment of completion —
    // everything else always succeeds once its Duration has elapsed.
    static bool TryComplete(WorkTask task, TileMap map, Inventory globalInventory) => task.Kind switch
    {
        TaskKind.Dig => CompleteDig(task, map, globalInventory),
        TaskKind.Deposit => CompleteDeposit(task, map, globalInventory),
        TaskKind.BuildCampfire => map.PlaceCampfire(task.TileX, task.TileZ) != null,
        TaskKind.DemolishCampfire => map.RemoveCampfire(task.TileX, task.TileZ),
        TaskKind.BuildSpring => map.PlaceSpring(task.TileX, task.TileZ) != null,
        TaskKind.DemolishSpring => map.RemoveSpring(task.TileX, task.TileZ),
        _ => true,
    };

    // A voxel that's since become undiggable (someone else got there first,
    // or the terrain changed underneath) is still a "done, nothing more to
    // do" outcome, not a stall — only Deposit's material check can actually
    // hold a task open waiting.
    static bool CompleteDig(WorkTask task, TileMap map, Inventory globalInventory)
    {
        int dug = map.DigVoxel(task.FineX, task.FineZ);
        if (dug > 0) globalInventory.Add("Dirt", dug);
        return true;
    }

    static bool CompleteDeposit(WorkTask task, TileMap map, Inventory globalInventory)
    {
        if (!globalInventory.TryRemove("Dirt", 1)) return false;
        return map.DepositVoxel(task.FineX, task.FineZ);
    }

    // Left-click on a queued task's cancel "x" removes it (releasing its
    // worker, if any); left-click anywhere else inside the panel just
    // consumes the click so it doesn't fall through to a world click.
    // Returns false (consuming nothing) when the queue is empty, since the
    // panel isn't even drawn then.
    static bool UpdateTaskQueuePanel(WorkQueue workQueue)
    {
        if (workQueue.Tasks.Count == 0) return false;
        if (!Raylib.IsMouseButtonPressed(MouseButton.Left)) return false;

        Vector2 mouse = Raylib.GetMousePosition();
        int shown = Math.Min(workQueue.Tasks.Count, QueueMaxVisibleRows);
        for (int i = 0; i < shown; i++)
        {
            if (!Raylib.CheckCollisionPointRec(mouse, TaskQueueCancelRect(i))) continue;

            var task = workQueue.Tasks[i];
            if (task.Worker is { } worker) worker.StopWorking();
            workQueue.Cancel(task);
            return true;
        }

        return Raylib.CheckCollisionPointRec(mouse, TaskQueuePanelRect(workQueue.Tasks.Count));
    }

    // How far (in coarse tiles) an idle actor might wander off to, and how
    // many random spots it'll try before giving up for this attempt (see
    // Actor.DeferWander) — small numbers on purpose, so this reads as
    // ambling near where it already is, not a cross-map errand.
    const int WanderRadius = 4;
    const int WanderAttempts = 6;

    // Actors with nothing else going on amble a few tiles in a random
    // direction once they've sat idle long enough (Actor.WantsToWander) —
    // routed through the exact same SetPath/pathfinding machinery as a
    // real player order, so a wandering actor gets stuck-detection,
    // auto-reroute, and give-up behaviour for free, and a player order
    // issued mid-wander overrides it exactly like it would anything else.
    //
    // Actor has no concept of "standing still on purpose" — WantsToWander
    // only ever sees an empty path plus an idle timer, which is exactly
    // what a builder stationed on a WorkTask looks like once it arrives.
    // Without excluding assigned workers here, one would eventually wander
    // off mid-dig (still playing the work animation, since nothing told it
    // to stop) while its task's Progress simply stopped advancing — see
    // UpdateBuilders' `arrived` check, which requires the actor to still be
    // standing on the stand tile.
    static void UpdateWandering(TileMap map, List<Actor> actors, WorkQueue workQueue)
    {
        foreach (var actor in actors)
        {
            if (!actor.WantsToWander) continue;
            if (workQueue.Tasks.Any(t => t.Worker == actor)) continue;

            if (PickWanderTile(map, actor.TileX, actor.TileZ) is not { } target)
            {
                actor.DeferWander();
                continue;
            }

            var path = AStar.FindPath(map, actor.TileX, actor.TileZ, target.X, target.Z);
            actor.SetPath(path);
        }
    }

    // A handful of random tries within WanderRadius tiles, returning the
    // first one that's actually walkable — good enough on a mostly-open
    // map (the common case), and cheap to just give up on for a spot boxed
    // in tight rather than searching harder for one.
    static (int X, int Z)? PickWanderTile(TileMap map, int cx, int cz)
    {
        for (int i = 0; i < WanderAttempts; i++)
        {
            int dx = Random.Shared.Next(-WanderRadius, WanderRadius + 1);
            int dz = Random.Shared.Next(-WanderRadius, WanderRadius + 1);
            if (dx == 0 && dz == 0) continue;

            int x = cx + dx, z = cz + dz;
            if (map.IsWalkable(x, z)) return (x, z);
        }
        return null;
    }

    // The gear icon toggles the panel; everything else here only reacts
    // while it's open. Returns true whenever a click landed on the gear
    // icon or somewhere inside the open panel, so that same click doesn't
    // also fall through to world/actor selection. A click that closes the
    // panel by landing *outside* it is deliberately NOT consumed, so it
    // still acts as a normal world click (e.g. clearing selection).
    static bool UpdateSettingsMenu(SunLight sun, SettingsMenuState menu, ref bool showGrid, ref bool showPathDots)
    {
        Vector2 mouse = Raylib.GetMousePosition();
        bool leftPressed = Raylib.IsMouseButtonPressed(MouseButton.Left);

        if (leftPressed && Raylib.CheckCollisionPointRec(mouse, SettingsButtonRect))
        {
            menu.Open = !menu.Open;
            return true;
        }

        if (!menu.Open) return false;

        if (leftPressed)
        {
            if (Raylib.CheckCollisionPointRec(mouse, GridButtonRect))
            {
                showGrid = !showGrid;
                return true;
            }

            if (Raylib.CheckCollisionPointRec(mouse, FreezeButtonRect))
            {
                sun.Frozen = !sun.Frozen;
                return true;
            }

            if (Raylib.CheckCollisionPointRec(mouse, PathDotsButtonRect))
            {
                showPathDots = !showPathDots;
                return true;
            }

            if (Raylib.CheckCollisionPointRec(mouse, TimeSliderHitRect)) menu.DraggingTime = true;
            if (Raylib.CheckCollisionPointRec(mouse, SpeedSliderHitRect)) menu.DraggingSpeed = true;
        }

        if (Raylib.IsMouseButtonReleased(MouseButton.Left))
        {
            menu.DraggingTime = false;
            menu.DraggingSpeed = false;
        }

        if (menu.DraggingTime)
        {
            float t = Math.Clamp((mouse.X - TimeSliderTrack.X) / TimeSliderTrack.Width, 0f, 1f);
            sun.TimeOfDay = t;
        }

        if (menu.DraggingSpeed)
        {
            float t = Math.Clamp((mouse.X - SpeedSliderTrack.X) / SpeedSliderTrack.Width, 0f, 1f);
            sun.SpeedMultiplier = MinSpeed + t * (MaxSpeed - MinSpeed);
        }

        if (menu.DraggingTime || menu.DraggingSpeed) return true;

        // A click anywhere else inside the panel (its background, or the
        // gap between controls) still shouldn't leak through to the world.
        if (leftPressed && Raylib.CheckCollisionPointRec(mouse, SettingsPanelRect)) return true;

        // A click outside the panel while it's open closes it, but is left
        // unconsumed so it behaves like the ordinary world click it looks like.
        if (leftPressed) menu.Open = false;

        return false;
    }

    // The action menu only does anything once at least one actor is selected.
    // Returns true if a button was actually clicked this frame, so the
    // world-selection click handling below can skip that same click.
    static bool UpdateActionMenu(TileMap map, SelectionState selection)
    {
        if (selection.Selected.Count == 0) return false;
        if (!Raylib.IsMouseButtonPressed(MouseButton.Left)) return false;

        Vector2 mouse = Raylib.GetMousePosition();

        if (Raylib.CheckCollisionPointRec(mouse, JumpButtonRect))
        {
            foreach (var actor in selection.Selected) actor.Jump();
            return true;
        }

        if (Raylib.CheckCollisionPointRec(mouse, BuilderButtonRect))
        {
            // Same "all -> off, anything else -> on" toggle style as the
            // settings panel's Freeze button: if every selected actor is
            // already a builder, unset them all; otherwise make them all
            // builders. Builders autonomously pull work off the shared
            // WorkQueue — see UpdateBuilders.
            bool allBuilders = selection.Selected.All(a => a.IsBuilder);
            foreach (var actor in selection.Selected) actor.IsBuilder = !allBuilders;
            return true;
        }

        return false;
    }

    // Left-click: select one actor (or add/toggle with shift), clear selection
    // on empty ground, or drag out a box to select everyone inside it.
    // uiConsumedClick suppresses all of that for a click the action menu
    // already handled.
    static void UpdateSelection(
        List<Actor> actors, SelectionState selection, Camera3D camera, bool shiftHeld, bool uiConsumedClick)
    {
        if (Raylib.IsMouseButtonPressed(MouseButton.Left) && !uiConsumedClick)
        {
            selection.LeftDownPos = Raylib.GetMousePosition();
            selection.BoxSelecting = false;
        }

        if (Raylib.IsMouseButtonDown(MouseButton.Left) && selection.LeftDownPos is { } down)
        {
            if (Vector2.Distance(down, Raylib.GetMousePosition()) > DragThreshold)
                selection.BoxSelecting = true;
        }

        if (Raylib.IsMouseButtonReleased(MouseButton.Left) && selection.LeftDownPos is { } start)
        {
            Vector2 end = Raylib.GetMousePosition();

            if (selection.BoxSelecting)
            {
                var rect = new Rectangle(
                    MathF.Min(start.X, end.X), MathF.Min(start.Y, end.Y),
                    MathF.Abs(end.X - start.X), MathF.Abs(end.Y - start.Y));

                if (!shiftHeld) selection.Selected.Clear();
                foreach (var actor in actors)
                {
                    Vector2 screenPos = Raylib.GetWorldToScreen(actor.WorldPos, camera);
                    if (Raylib.CheckCollisionPointRec(screenPos, rect))
                        selection.Selected.Add(actor);
                }
            }
            else
            {
                // A plain click: find the nearest actor under the cursor, if any.
                Actor? clicked = null;
                float bestDist = 20f; // pixel pick radius
                foreach (var actor in actors)
                {
                    float d = Vector2.Distance(Raylib.GetWorldToScreen(actor.WorldPos, camera), end);
                    if (d < bestDist) { bestDist = d; clicked = actor; }
                }

                if (clicked != null)
                {
                    if (shiftHeld)
                    {
                        if (!selection.Selected.Remove(clicked))
                            selection.Selected.Add(clicked);
                    }
                    else
                    {
                        selection.Selected.Clear();
                        selection.Selected.Add(clicked);
                    }
                }
                else if (!shiftHeld)
                {
                    selection.Selected.Clear();
                }
            }

            selection.LeftDownPos = null;
            selection.BoxSelecting = false;
        }

        foreach (var actor in actors)
            actor.Selected = selection.Selected.Contains(actor);
    }

    // Right-click (a click, not a camera-orbit drag): send every selected
    // actor toward the clicked tile, each to its own nearby free spot.
    static void UpdateMoveOrders(TileMap map, SelectionState selection, Camera3D camera, WorkQueue workQueue)
    {
        if (Raylib.IsMouseButtonPressed(MouseButton.Right))
        {
            selection.RightDownPos = Raylib.GetMousePosition();
            selection.RightDragged = false;
        }

        if (Raylib.IsMouseButtonDown(MouseButton.Right) && selection.RightDownPos is { } down)
        {
            if (Vector2.Distance(down, Raylib.GetMousePosition()) > DragThreshold)
                selection.RightDragged = true;
        }

        if (Raylib.IsMouseButtonReleased(MouseButton.Right))
        {
            if (!selection.RightDragged && selection.Selected.Count > 0 &&
                TryPickTile(map, camera, Raylib.GetMousePosition(), out int hitX, out int hitZ) &&
                map.IsWalkable(hitX, hitZ))
            {
                // A direct order always overrides whatever an actor was
                // doing before, including a give-up — SetPath (not
                // Reroute) resets its stuck budget for the new attempt.
                var movers = selection.Selected.ToList();
                var destinations = AssignDestinations(map, movers, hitX, hitZ);
                foreach (var (actor, dest) in destinations)
                {
                    var path = AStar.FindPath(map, actor.TileX, actor.TileZ, dest.X, dest.Z);

                    // A manual order always wins over whatever work-queue
                    // assignment this actor was mid-way through — release it
                    // (progress is kept on the task itself) so another
                    // builder can pick it back up later.
                    ReleaseAssignment(workQueue, actor);
                    actor.SetPath(path);
                }
            }

            selection.RightDownPos = null;
            selection.RightDragged = false;
        }
    }

    // Ray-casts from a screen position through every coarse tile's column
    // bounds and returns the nearest hit point — the shared "what point on
    // the ground is the mouse over" query both TryPickTile (coarse) and
    // TryPickVoxel (fine) derive their own result from.
    static bool TryPickGround(TileMap map, Camera3D camera, Vector2 screenPos, out Vector3 hitPoint)
    {
        Ray ray = Raylib.GetScreenToWorldRay(screenPos, camera);

        float bestDist = float.MaxValue;
        hitPoint = default;
        bool hitAny = false;
        for (int x = 0; x < map.Width; x++)
        {
            for (int z = 0; z < map.Depth; z++)
            {
                var hit = Raylib.GetRayCollisionBox(ray, map.ColumnBounds(x, z));
                if (hit.Hit && hit.Distance < bestDist)
                {
                    bestDist = hit.Distance;
                    hitPoint = hit.Point;
                    hitAny = true;
                }
            }
        }

        return hitAny;
    }

    // Which coarse tile the mouse is over — used by move orders and
    // campfire placement.
    static bool TryPickTile(TileMap map, Camera3D camera, Vector2 screenPos, out int tileX, out int tileZ)
    {
        if (!TryPickGround(map, camera, screenPos, out Vector3 hitPoint))
        {
            tileX = tileZ = -1;
            return false;
        }

        tileX = Math.Clamp((int)MathF.Floor(hitPoint.X / TileMap.TileSize), 0, map.Width - 1);
        tileZ = Math.Clamp((int)MathF.Floor(hitPoint.Z / TileMap.TileSize), 0, map.Depth - 1);
        return true;
    }

    // Which exact fine voxel column the mouse is over — the finer-grained
    // pick Dig/Deposit's per-voxel selection needs.
    static bool TryPickVoxel(TileMap map, Camera3D camera, Vector2 screenPos, out int fineX, out int fineZ)
    {
        if (!TryPickGround(map, camera, screenPos, out Vector3 hitPoint))
        {
            fineX = fineZ = -1;
            return false;
        }

        fineX = Math.Clamp((int)MathF.Floor(hitPoint.X / TileMap.VoxelSize), 0, map.Width * TileMap.FineSubdivisions - 1);
        fineZ = Math.Clamp((int)MathF.Floor(hitPoint.Z / TileMap.VoxelSize), 0, map.Depth * TileMap.FineSubdivisions - 1);
        return true;
    }

    // The build icon toggles the palette; picking a tool arms it. Campfire/
    // DemolishCampfire act on a plain click (like before); Dig/Deposit
    // instead go through UpdateVoxelDrag, which supports both a single
    // click and a drag-select rectangle of fine voxels. Right-click or
    // Escape cancels without closing anything else. Campfire disarms after
    // a single click (placing one thing at a time); every other tool stays
    // armed so the player can stamp out several tasks in a row.
    static bool UpdateBuildMenu(TileMap map, WorkQueue workQueue, BuildMenuState menu, Camera3D camera)
    {
        Vector2 mouse = Raylib.GetMousePosition();
        bool leftPressed = Raylib.IsMouseButtonPressed(MouseButton.Left);

        if (leftPressed && Raylib.CheckCollisionPointRec(mouse, BuildButtonRect))
        {
            menu.Open = !menu.Open;
            if (!menu.Open) { menu.ArmedTool = ToolKind.None; ResetVoxelDrag(menu); }
            return true;
        }

        if (menu.ArmedTool != ToolKind.None)
        {
            if (Raylib.IsKeyPressed(KeyboardKey.Escape))
            {
                menu.ArmedTool = ToolKind.None;
                ResetVoxelDrag(menu);
                return true;
            }

            // A genuine right-click (press+release without dragging past
            // the threshold) cancels; a right-drag is left alone so it
            // still orbits the camera, same distinction UpdateMoveOrders
            // makes for its own right-click.
            if (Raylib.IsMouseButtonPressed(MouseButton.Right))
            {
                menu.RightDownPos = mouse;
                menu.RightDragged = false;
            }
            if (Raylib.IsMouseButtonDown(MouseButton.Right) && menu.RightDownPos is { } rightDown)
            {
                if (Vector2.Distance(rightDown, mouse) > DragThreshold) menu.RightDragged = true;
            }
            if (Raylib.IsMouseButtonReleased(MouseButton.Right))
            {
                bool wasDrag = menu.RightDragged;
                menu.RightDownPos = null;
                menu.RightDragged = false;
                if (!wasDrag)
                {
                    menu.ArmedTool = ToolKind.None;
                    ResetVoxelDrag(menu);
                    return true;
                }
            }

            if (menu.ArmedTool is ToolKind.Dig or ToolKind.Deposit)
            {
                UpdateVoxelDrag(map, workQueue, menu, camera);
                return true;
            }

            if (leftPressed)
            {
                TryPlaceArmedTool(map, workQueue, menu, camera);
                return true;
            }

            return true; // swallow every click (and non-click) frame while armed
        }

        if (!menu.Open) return false;

        if (leftPressed)
        {
            if (Raylib.CheckCollisionPointRec(mouse, CampfireItemRect)) { menu.ArmedTool = ToolKind.Campfire; menu.Open = false; return true; }
            if (Raylib.CheckCollisionPointRec(mouse, DemolishCampfireItemRect)) { menu.ArmedTool = ToolKind.DemolishCampfire; menu.Open = false; return true; }
            if (Raylib.CheckCollisionPointRec(mouse, SpringItemRect)) { menu.ArmedTool = ToolKind.Spring; menu.Open = false; return true; }
            if (Raylib.CheckCollisionPointRec(mouse, DemolishSpringItemRect)) { menu.ArmedTool = ToolKind.DemolishSpring; menu.Open = false; return true; }
            if (Raylib.CheckCollisionPointRec(mouse, DigItemRect)) { menu.ArmedTool = ToolKind.Dig; menu.Open = false; return true; }
            if (Raylib.CheckCollisionPointRec(mouse, DepositItemRect)) { menu.ArmedTool = ToolKind.Deposit; menu.Open = false; return true; }

            if (Raylib.CheckCollisionPointRec(mouse, BuildPanelRect)) return true;

            menu.Open = false; // click outside closes it, same as the settings panel
        }

        return false;
    }

    // Applies the coarse-tile tools (the campfire and spring pairs) to the
    // tile under the mouse — queuing a WorkTask on success, doing nothing
    // on an invalid tile. Disarms afterward only for the two build tools;
    // the demolish tools stay armed for repeated stamping.
    static void TryPlaceArmedTool(TileMap map, WorkQueue workQueue, BuildMenuState menu, Camera3D camera)
    {
        if (!TryPickTile(map, camera, Raylib.GetMousePosition(), out int tx, out int tz))
        {
            if (menu.ArmedTool is ToolKind.Campfire or ToolKind.Spring) menu.ArmedTool = ToolKind.None;
            return;
        }

        switch (menu.ArmedTool)
        {
            case ToolKind.Campfire:
                if (map.CanPlaceCampfire(tx, tz))
                    workQueue.Enqueue(WorkTask.ForTile(TaskKind.BuildCampfire, tx, tz, tx, tz, BuildCampfireDuration));
                menu.ArmedTool = ToolKind.None;
                break;

            case ToolKind.DemolishCampfire:
                if (map.Campfires.Any(f => f.TileX == tx && f.TileZ == tz) &&
                    NearestWalkableNeighbor(map, tx, tz) is { } stand)
                {
                    workQueue.Enqueue(WorkTask.ForTile(TaskKind.DemolishCampfire, tx, tz, stand.X, stand.Z, DemolishCampfireDuration));
                }
                break;

            // Unlike a campfire, a spring doesn't block its own tile, so
            // the worker stands right on it for both building and capping.
            case ToolKind.Spring:
                if (map.CanPlaceSpring(tx, tz))
                    workQueue.Enqueue(WorkTask.ForTile(TaskKind.BuildSpring, tx, tz, tx, tz, BuildSpringDuration));
                menu.ArmedTool = ToolKind.None;
                break;

            case ToolKind.DemolishSpring:
                if (map.Springs.Any(s => s.TileX == tx && s.TileZ == tz))
                    workQueue.Enqueue(WorkTask.ForTile(TaskKind.DemolishSpring, tx, tz, tx, tz, DemolishSpringDuration));
                break;
        }
    }

    // Dig/Deposit's own input handling: press-drag-release over the fine
    // voxel grid, the same press/threshold/release shape UpdateSelection
    // uses for the actor box-select, just picking fine voxels instead of
    // actors. A plain click (no real drag) queues just the one voxel under
    // the cursor; a real drag queues every valid voxel in the rectangle
    // between where the drag started and where it ended.
    static void UpdateVoxelDrag(TileMap map, WorkQueue workQueue, BuildMenuState menu, Camera3D camera)
    {
        Vector2 mouse = Raylib.GetMousePosition();

        if (Raylib.IsMouseButtonPressed(MouseButton.Left))
        {
            menu.LeftDownScreenPos = mouse;
            menu.VoxelDragging = false;
            menu.DragStartVoxel = TryPickVoxel(map, camera, mouse, out int fx, out int fz) ? (fx, fz) : null;
        }

        if (Raylib.IsMouseButtonDown(MouseButton.Left) && menu.LeftDownScreenPos is { } down)
        {
            if (Vector2.Distance(down, mouse) > DragThreshold) menu.VoxelDragging = true;
        }

        if (Raylib.IsMouseButtonReleased(MouseButton.Left) && menu.DragStartVoxel is { } start)
        {
            bool haveEnd = TryPickVoxel(map, camera, mouse, out int endFx, out int endFz);

            if (menu.VoxelDragging && haveEnd)
            {
                int minFx = Math.Min(start.Fx, endFx);
                int maxFx = Math.Min(Math.Max(start.Fx, endFx), minFx + MaxDragVoxelsPerAxis - 1);
                int minFz = Math.Min(start.Fz, endFz);
                int maxFz = Math.Min(Math.Max(start.Fz, endFz), minFz + MaxDragVoxelsPerAxis - 1);

                for (int fx = minFx; fx <= maxFx; fx++)
                    for (int fz = minFz; fz <= maxFz; fz++)
                        EnqueueVoxelTask(workQueue, map, menu.ArmedTool, fx, fz);
            }
            else
            {
                EnqueueVoxelTask(workQueue, map, menu.ArmedTool, start.Fx, start.Fz);
            }

            ResetVoxelDrag(menu);
        }
    }

    static void ResetVoxelDrag(BuildMenuState menu)
    {
        menu.LeftDownScreenPos = null;
        menu.DragStartVoxel = null;
        menu.VoxelDragging = false;
    }

    // Queues one Dig or Deposit task for a single fine voxel — silently
    // does nothing if that exact voxel isn't a legal target right now, or
    // already has a pending task of the same kind (repeated clicks/drags
    // over the same spot shouldn't pile up duplicate work).
    static void EnqueueVoxelTask(WorkQueue workQueue, TileMap map, ToolKind tool, int fineX, int fineZ)
    {
        TaskKind kind = tool == ToolKind.Dig ? TaskKind.Dig : TaskKind.Deposit;
        bool valid = kind == TaskKind.Dig ? map.CanDigVoxel(fineX, fineZ) : map.CanDepositVoxel(fineX, fineZ);
        if (!valid || workQueue.HasPendingVoxelTask(kind, fineX, fineZ)) return;

        float duration = kind == TaskKind.Dig ? DigVoxelDuration : DepositVoxelDuration;
        workQueue.Enqueue(WorkTask.ForVoxel(kind, fineX, fineZ, duration));
    }

    // The nearest walkable orthogonal neighbour of a tile that's itself
    // unwalkable (a campfire) — where a DemolishCampfire task's worker has
    // to stand, since it can't stand on the fire itself. Null in the (rare)
    // case all four sides are blocked too.
    static (int X, int Z)? NearestWalkableNeighbor(TileMap map, int x, int z)
    {
        foreach (var (dx, dz) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) })
        {
            int nx = x + dx, nz = z + dz;
            if (map.IsWalkable(nx, nz)) return (nx, nz);
        }
        return null;
    }

    // Gives each mover its own nearby walkable tile around the target, so a
    // group order doesn't send everyone to stack on the exact same spot.
    // Closer actors claim the closer slots first. Actors no longer block
    // each other's paths or destinations (see ResolveOverlaps for how a
    // crowd still avoids fully overlapping) — "claimed" here is only about
    // this one order not sending two of its own movers to the same tile,
    // not about staying off tiles other actors happen to occupy.
    static Dictionary<Actor, (int X, int Z)> AssignDestinations(
        TileMap map, List<Actor> movers, int targetX, int targetZ)
    {
        var claimed = new HashSet<(int, int)>();
        var result = new Dictionary<Actor, (int, int)>();

        foreach (var actor in movers.OrderBy(p => Math.Abs(p.TileX - targetX) + Math.Abs(p.TileZ - targetZ)))
        {
            var tile = FindNearestFreeTile(map, targetX, targetZ, claimed);
            if (tile is { } t)
            {
                claimed.Add(t);
                result[actor] = t;
            }
        }
        return result;
    }

    // Spirals outward ring by ring from (cx, cz) until it finds a walkable
    // tile nobody else in this order has claimed.
    static (int X, int Z)? FindNearestFreeTile(TileMap map, int cx, int cz, HashSet<(int, int)> claimed)
    {
        if (map.IsWalkable(cx, cz) && !claimed.Contains((cx, cz))) return (cx, cz);

        int maxRadius = Math.Max(map.Width, map.Depth);
        for (int radius = 1; radius <= maxRadius; radius++)
        {
            for (int dx = -radius; dx <= radius; dx++)
            {
                for (int dz = -radius; dz <= radius; dz++)
                {
                    if (Math.Max(Math.Abs(dx), Math.Abs(dz)) != radius) continue; // only this ring's edge
                    int x = cx + dx, z = cz + dz;
                    if (!map.IsWalkable(x, z)) continue;
                    if (claimed.Contains((x, z))) continue;
                    return (x, z);
                }
            }
        }
        return null; // no free tile anywhere — shouldn't happen in practice
    }

    static void Draw(GameSession session, TileMap map, List<Actor> actors, SelectionState selection, SettingsMenuState settingsMenu,
        BuildMenuState buildMenu, WorkQueue workQueue, Inventory globalInventory, Camera3D camera, SunLight sun, bool showGrid, bool showPathDots)
    {
        // Every campfire's point light, refreshed each frame (flicker means
        // even a stationary fire's colour keeps changing) — has to happen
        // before the lit pass below reads these uniforms, and before the
        // shadow pass too for tidiness even though shadows don't use them.
        var pointLightPositions = new List<Vector3>();
        var pointLightColors = new List<Vector3>();
        foreach (var (pos, color) in map.CampfireLights())
        {
            pointLightPositions.Add(pos);
            pointLightColors.Add(color);
        }
        sun.SetPointLights(pointLightPositions, pointLightColors);

        // Rendered from the light's point of view into its own depth
        // texture before anything else — the main lit pass right below
        // needs that result (via the shader's shadowMap uniform) already
        // in hand for this same frame.
        sun.RenderShadowMap(map, actors);

        Raylib.BeginDrawing();
        Raylib.ClearBackground(sun.SkyColor);

        // Everything between BeginMode3D/EndMode3D is drawn in *world* space,
        // so it moves and scales with the camera.
        Raylib.BeginMode3D(camera);

        sun.DrawCelestialBodies(new Vector3(map.WidthPx / 2f, 100f, map.DepthPx / 2f));

        // Campfire flames are unlit for the same reason the sun/moon are —
        // they're the light source, not something the light should shade.
        map.DrawCampfiresGlow();

        // Lit pass: shading depends on which way each face points, so the
        // sun alone gives hills a sense of depth.
        sun.BeginLit();
        map.DrawSolid();
        sun.BindWhiteAlbedo();
        map.DrawTrees();
        map.DrawBushes();
        map.DrawCampfiresLit();
        map.DrawSpringsLit();
        foreach (var actor in actors) actor.DrawSolid(showPathDots);

        // Water goes last in the lit pass: it's the only translucent thing
        // in the scene, so everything it might tint — terrain, springs, an
        // actor wading through it — has to already be in the buffer for
        // the blend to have anything to blend against.
        map.DrawWater();
        sun.EndLit();

        // Unlit pass: faint edges on every block so height steps are legible
        // even where the shading alone doesn't make it obvious, plus each
        // actor's outline and selection ring. The grid itself is optional —
        // toggled from the settings panel — everything else here isn't.
        if (showGrid) map.DrawOutlines();
        foreach (var actor in actors) actor.DrawOutline();

        // A persistent blue glow around every voxel with a queued Dig/
        // Deposit task on it — the moment a voxel is selected it marks
        // itself this way, and it stays marked until a builder actually
        // gets to it (the task leaving workQueue.Tasks, via Prune, is what
        // makes the glow disappear).
        foreach (var task in workQueue.Tasks.Where(t => t.Kind is TaskKind.Dig or TaskKind.Deposit && !t.Complete))
            DrawVoxelMarker(map, task.FineX, task.FineZ, task.Kind == TaskKind.Deposit, Color.SkyBlue, wireOnly: true);

        // A ghost preview on whichever tile/voxel the mouse is over while a
        // tool is armed: orange for Campfire (matching its previous
        // colour), blue-ish for Dig/Deposit, red whenever the hovered spot
        // isn't a legal target — so the player knows before clicking.
        if (buildMenu.ArmedTool is ToolKind.Campfire or ToolKind.DemolishCampfire or ToolKind.Spring or ToolKind.DemolishSpring &&
            TryPickTile(map, camera, Raylib.GetMousePosition(), out int hoverX, out int hoverZ))
        {
            bool valid = buildMenu.ArmedTool switch
            {
                ToolKind.Campfire => map.CanPlaceCampfire(hoverX, hoverZ),
                ToolKind.Spring => map.CanPlaceSpring(hoverX, hoverZ),
                ToolKind.DemolishSpring => map.Springs.Any(s => s.TileX == hoverX && s.TileZ == hoverZ),
                _ => map.Campfires.Any(f => f.TileX == hoverX && f.TileZ == hoverZ),
            };

            // Springs preview blue rather than the campfire's orange —
            // they're the one build tool whose whole point is water.
            bool watery = buildMenu.ArmedTool is ToolKind.Spring or ToolKind.DemolishSpring;
            Color ringColor = valid
                ? (watery ? new Color(80, 170, 255, 220) : new Color(255, 170, 60, 220))
                : new Color(220, 60, 60, 220);

            Vector3 ringCenter = new(
                hoverX * TileMap.TileSize + TileMap.TileSize / 2f,
                map.SurfaceY(hoverX, hoverZ) + 2f,
                hoverZ * TileMap.TileSize + TileMap.TileSize / 2f);
            Raylib.DrawCircle3D(ringCenter, TileMap.TileSize * 0.45f, new Vector3(1f, 0f, 0f), 90f, ringColor);
        }
        else if (buildMenu.ArmedTool is ToolKind.Dig or ToolKind.Deposit)
        {
            DrawVoxelToolPreview(map, buildMenu, camera);
        }

        Raylib.EndMode3D();

        // The drag-box for box-select, drawn in screen space over everything.
        if (selection.BoxSelecting && selection.LeftDownPos is { } start)
        {
            Vector2 end = Raylib.GetMousePosition();
            var rect = new Rectangle(
                MathF.Min(start.X, end.X), MathF.Min(start.Y, end.Y),
                MathF.Abs(end.X - start.X), MathF.Abs(end.Y - start.Y));
            Raylib.DrawRectangleRec(rect, new Color(120, 255, 120, 40));
            Raylib.DrawRectangleLinesEx(rect, 1.5f, Color.Lime);
        }

        // A small completion bar hovering over every task currently being
        // worked, projected from its tile (or, for Dig/Deposit, its exact
        // voxel) into screen space.
        foreach (var task in workQueue.Tasks.Where(t => t.Started && !t.Complete))
        {
            Vector3 worldPos = task.Kind is TaskKind.Dig or TaskKind.Deposit
                ? new Vector3(
                    task.FineX * TileMap.VoxelSize + TileMap.VoxelSize / 2f,
                    map.VoxelHeightAt(task.FineX, task.FineZ) * TileMap.VoxelSize + TileMap.TileSize * 0.3f,
                    task.FineZ * TileMap.VoxelSize + TileMap.VoxelSize / 2f)
                : new Vector3(
                    task.TileX * TileMap.TileSize + TileMap.TileSize / 2f,
                    map.SurfaceY(task.TileX, task.TileZ) + TileMap.TileSize * 0.9f,
                    task.TileZ * TileMap.TileSize + TileMap.TileSize / 2f);
            Vector2 screenPos = Raylib.GetWorldToScreen(worldPos, camera);

            const float barWidth = 40f, barHeight = 6f;
            var track = new Rectangle(screenPos.X - barWidth / 2f, screenPos.Y - barHeight / 2f, barWidth, barHeight);
            Raylib.DrawRectangleRec(track, new Color(50, 50, 50, 180));
            float t = task.Duration > 0f ? Math.Clamp(task.Progress / task.Duration, 0f, 1f) : 1f;
            Raylib.DrawRectangle((int)track.X, (int)track.Y, (int)(t * track.Width), (int)track.Height, new Color(120, 190, 120, 255));
        }

        // The HUD is drawn in *screen* space, so it stays put.
        string globalItems = globalInventory.Counts.Count == 0
            ? "(empty)"
            : string.Join(", ", globalInventory.Counts.Select(kv => $"{kv.Key} x{kv.Value}"));
        Raylib.DrawText($"Selected: {selection.Selected.Count}/{actors.Count}", 10, 10, 18, Color.Black);
        Raylib.DrawText($"Global inventory: {globalItems}", 10, 32, 16, Color.Black);

        // Total water and how many springs are feeding it. Springs run
        // forever and only the map's rim drains, so watching this number
        // is how the player tells a river that's found its level from one
        // that's still quietly filling a basin somewhere.
        Raylib.DrawText($"Water: {map.TotalWaterVolume():N0}  ({map.Springs.Count} spring{(map.Springs.Count == 1 ? "" : "s")})",
            10, 52, 16, Color.Black);

        DrawSettingsMenu(sun, settingsMenu, showGrid, showPathDots);
        DrawBuildMenu(buildMenu);
        DrawTaskQueue(workQueue);
        DrawSaveButton(session);

        // The action menu, only while something's selected.
        if (selection.Selected.Count > 0)
        {
            DrawButton(JumpButtonRect, "Jump");
            DrawButton(BuilderButtonRect, "Builder", active: selection.Selected.All(a => a.IsBuilder));
        }

        Raylib.DrawFPS(10, ScreenHeight - 30);

        Raylib.EndDrawing();
    }

    // Where a Dig/Deposit ghost/marker box for one fine voxel should sit:
    // hugging the current top voxel for Dig (that's what would be removed),
    // or the not-yet-there next slot up for Deposit (that's where the new
    // one would land).
    static Vector3 VoxelMarkerCenter(TileMap map, int fineX, int fineZ, bool isDeposit)
    {
        int voxelLevel = map.VoxelHeightAt(fineX, fineZ) + (isDeposit ? 1 : 0);
        return new Vector3(
            fineX * TileMap.VoxelSize + TileMap.VoxelSize / 2f,
            (voxelLevel - 0.5f) * TileMap.VoxelSize,
            fineZ * TileMap.VoxelSize + TileMap.VoxelSize / 2f);
    }

    // Draws one voxel's marker box — either just a wireframe glow (the
    // persistent "this voxel is queued" indicator) or a solid translucent
    // fill plus wireframe (the interactive hover/drag preview, colour-coded
    // by validity). A Dig marker at ground level (nothing left there) is
    // skipped rather than drawn as a flat sliver.
    static void DrawVoxelMarker(TileMap map, int fineX, int fineZ, bool isDeposit, Color color, bool wireOnly)
    {
        if (!isDeposit && map.VoxelHeightAt(fineX, fineZ) <= 0) return;

        Vector3 center = VoxelMarkerCenter(map, fineX, fineZ, isDeposit);
        if (!wireOnly)
        {
            Color fill = new(color.R, color.G, color.B, (byte)140);
            Raylib.DrawCube(center, TileMap.VoxelSize * 0.94f, TileMap.VoxelSize * 0.94f, TileMap.VoxelSize * 0.94f, fill);
        }
        Raylib.DrawCubeWires(center, TileMap.VoxelSize, TileMap.VoxelSize, TileMap.VoxelSize, color);
    }

    // The interactive Dig/Deposit hover preview: a single voxel box under
    // the cursor, or — mid-drag — one box per cell of the whole rectangle
    // between where the drag started and the current hover, each coloured
    // blue if it's a legal target or red if not.
    static void DrawVoxelToolPreview(TileMap map, BuildMenuState menu, Camera3D camera)
    {
        Vector2 mouse = Raylib.GetMousePosition();
        if (!TryPickVoxel(map, camera, mouse, out int hoverFx, out int hoverFz)) return;

        int minFx = hoverFx, maxFx = hoverFx, minFz = hoverFz, maxFz = hoverFz;
        if (menu.VoxelDragging && menu.DragStartVoxel is { } start)
        {
            minFx = Math.Min(start.Fx, hoverFx); maxFx = Math.Min(Math.Max(start.Fx, hoverFx), minFx + MaxDragVoxelsPerAxis - 1);
            minFz = Math.Min(start.Fz, hoverFz); maxFz = Math.Min(Math.Max(start.Fz, hoverFz), minFz + MaxDragVoxelsPerAxis - 1);
        }

        bool isDeposit = menu.ArmedTool == ToolKind.Deposit;
        for (int fx = minFx; fx <= maxFx; fx++)
        {
            for (int fz = minFz; fz <= maxFz; fz++)
            {
                bool valid = isDeposit ? map.CanDepositVoxel(fx, fz) : map.CanDigVoxel(fx, fz);
                Color color = valid ? new Color(70, 140, 230, 255) : new Color(220, 60, 60, 255);
                DrawVoxelMarker(map, fx, fz, isDeposit, color, wireOnly: false);
            }
        }
    }

    // The task queue panel along the left edge: one row per queued/
    // in-progress WorkTask (label, progress bar, cancel "x"), capped at
    // QueueMaxVisibleRows with an overflow line for the rest. Not drawn at
    // all when the queue is empty.
    static void DrawTaskQueue(WorkQueue workQueue)
    {
        if (workQueue.Tasks.Count == 0) return;

        var panelRect = TaskQueuePanelRect(workQueue.Tasks.Count);
        Raylib.DrawRectangleRec(panelRect, new Color(245, 245, 245, 235));
        Raylib.DrawRectangleLinesEx(panelRect, 1.5f, Color.DarkGray);
        Raylib.DrawText("Task Queue", (int)QueuePanelX + 10, (int)QueuePanelY + 8, 16, Color.Black);

        int shown = Math.Min(workQueue.Tasks.Count, QueueMaxVisibleRows);
        for (int i = 0; i < shown; i++)
        {
            var task = workQueue.Tasks[i];
            float rowY = QueuePanelY + 34 + i * QueueRowHeight;

            Raylib.DrawText(task.Label, (int)QueuePanelX + 10, (int)rowY, 14, Color.Black);

            var barTrack = new Rectangle(QueuePanelX + 10, rowY + 18, QueuePanelWidth - 44, 6);
            Raylib.DrawRectangleRec(barTrack, new Color(210, 210, 210, 255));
            float t = task.Duration > 0f ? Math.Clamp(task.Progress / task.Duration, 0f, 1f) : 0f;
            Raylib.DrawRectangle((int)barTrack.X, (int)barTrack.Y, (int)(t * barTrack.Width), (int)barTrack.Height, new Color(120, 190, 120, 255));

            var cancelRect = TaskQueueCancelRect(i);
            Raylib.DrawRectangleRec(cancelRect, new Color(230, 120, 120, 255));
            Raylib.DrawText("x", (int)(cancelRect.X + 5), (int)(cancelRect.Y + 1), 14, Color.Black);
        }

        if (workQueue.Tasks.Count > shown)
            Raylib.DrawText($"+{workQueue.Tasks.Count - shown} more", (int)QueuePanelX + 10,
                (int)(QueuePanelY + 34 + shown * QueueRowHeight), 14, Color.DarkGray);
    }

    // active marks a toggle button as currently "on" (e.g. the grid button
    // while the grid is showing) with a darker fill, independent of hover.
    static void DrawButton(Rectangle rect, string label, bool active = false)
    {
        bool hovered = Raylib.CheckCollisionPointRec(Raylib.GetMousePosition(), rect);
        Color fill = active
            ? (hovered ? new Color(90, 160, 90, 255) : new Color(120, 190, 120, 255))
            : (hovered ? new Color(140, 200, 140, 255) : new Color(200, 230, 200, 255));
        Raylib.DrawRectangleRec(rect, fill);
        Raylib.DrawRectangleLinesEx(rect, 1.5f, Color.DarkGreen);

        int textWidth = Raylib.MeasureText(label, 16);
        Raylib.DrawText(label, (int)(rect.X + (rect.Width - textWidth) / 2f), (int)(rect.Y + 7), 16, Color.Black);
    }

    // The gear icon plus, when open, the panel of controls below it: grid
    // on/off, actor path-dots on/off, freezing the day/night cycle in
    // place, and sliders for jumping to a time of day and for how fast the
    // cycle runs.
    static void DrawSettingsMenu(SunLight sun, SettingsMenuState menu, bool showGrid, bool showPathDots)
    {
        DrawGearButton(SettingsButtonRect, menu.Open);
        if (!menu.Open) return;

        Raylib.DrawRectangleRec(SettingsPanelRect, new Color(245, 245, 245, 235));
        Raylib.DrawRectangleLinesEx(SettingsPanelRect, 1.5f, Color.DarkGray);
        Raylib.DrawText("Settings", (int)PanelX + 14, (int)PanelY + 8, 18, Color.Black);

        DrawButton(GridButtonRect, "Grid", active: showGrid);
        DrawButton(FreezeButtonRect, sun.Frozen ? "Frozen" : "Freeze", active: sun.Frozen);
        DrawButton(PathDotsButtonRect, "Path Dots", active: showPathDots);

        DrawSlider(TimeSliderTrack, $"Time of day: {FormatTimeOfDay(sun.TimeOfDay)}", sun.TimeOfDay, 0f, 1f);
        DrawSlider(SpeedSliderTrack, $"Speed: {sun.SpeedMultiplier:0.0}x", sun.SpeedMultiplier, MinSpeed, MaxSpeed);
    }

    // The build icon plus, when open, the palette of work-order tools — or,
    // while one is armed, a hint instead of the palette itself.
    static void DrawBuildMenu(BuildMenuState menu)
    {
        DrawButton(BuildButtonRect, "+", active: menu.Open || menu.ArmedTool != ToolKind.None);

        if (menu.ArmedTool != ToolKind.None)
        {
            string hint = menu.ArmedTool switch
            {
                ToolKind.Campfire => "Click a tile to place the campfire (Esc / right-click to cancel)",
                ToolKind.DemolishCampfire => "Click a campfire to demolish it (Esc / right-click to cancel)",
                ToolKind.Spring => "Click a tile to dig a spring there (Esc / right-click to cancel)",
                ToolKind.DemolishSpring => "Click a spring to cap it (Esc / right-click to cancel)",
                ToolKind.Dig or ToolKind.Deposit => "Click a voxel, or drag to select several (Esc / right-click to cancel)",
                _ => "",
            };
            Raylib.DrawText(hint, (int)BuildButtonRect.X, (int)(BuildButtonRect.Y + BuildButtonRect.Height + 6), 16, Color.Black);
            return;
        }

        if (!menu.Open) return;

        Raylib.DrawRectangleRec(BuildPanelRect, new Color(245, 245, 245, 235));
        Raylib.DrawRectangleLinesEx(BuildPanelRect, 1.5f, Color.DarkGray);
        DrawButton(CampfireItemRect, "Campfire");
        DrawButton(DemolishCampfireItemRect, "Demolish Fire");
        DrawButton(SpringItemRect, "Spring");
        DrawButton(DemolishSpringItemRect, "Cap Spring");
        DrawButton(DigItemRect, "Dig");
        DrawButton(DepositItemRect, "Deposit");
    }

    // A gear-shaped icon button: a ring of teeth around a circular hub,
    // drawn with plain rotated rectangles rather than a texture asset.
    // "active" (the panel being open) darkens it the same way DrawButton's
    // active toggles do.
    static void DrawGearButton(Rectangle rect, bool active)
    {
        bool hovered = Raylib.CheckCollisionPointRec(Raylib.GetMousePosition(), rect);
        Color fill = active
            ? (hovered ? new Color(90, 160, 90, 255) : new Color(120, 190, 120, 255))
            : (hovered ? new Color(140, 200, 140, 255) : new Color(200, 230, 200, 255));
        Raylib.DrawRectangleRec(rect, fill);
        Raylib.DrawRectangleLinesEx(rect, 1.5f, Color.DarkGreen);

        Vector2 center = new(rect.X + rect.Width / 2f, rect.Y + rect.Height / 2f);
        float outerR = MathF.Min(rect.Width, rect.Height) * 0.26f;
        float innerR = outerR * 0.5f;
        float toothLen = outerR * 0.55f;
        float toothWidth = outerR * 0.55f;

        for (int i = 0; i < 8; i++)
        {
            var tooth = new Rectangle(center.X, center.Y, toothWidth, outerR + toothLen);
            Raylib.DrawRectanglePro(tooth, new Vector2(toothWidth / 2f, outerR + toothLen), i * 45f, Color.DarkGreen);
        }
        Raylib.DrawCircleV(center, outerR, Color.DarkGreen);
        Raylib.DrawCircleV(center, innerR, fill);
    }

    // A labelled horizontal slider: a filled track up to the current value
    // plus a knob, matching the panel's own light/dark-green button styling.
    static void DrawSlider(Rectangle track, string label, float value, float min, float max)
    {
        Raylib.DrawText(label, (int)track.X, (int)track.Y - 20, 16, Color.Black);

        Raylib.DrawRectangleRec(track, new Color(210, 210, 210, 255));
        Raylib.DrawRectangleLinesEx(track, 1f, Color.DarkGray);

        float t = Math.Clamp((value - min) / (max - min), 0f, 1f);
        float knobX = track.X + t * track.Width;
        Raylib.DrawRectangle((int)track.X, (int)track.Y, (int)(t * track.Width), (int)track.Height, new Color(120, 190, 120, 255));
        Raylib.DrawCircle((int)knobX, (int)(track.Y + track.Height / 2f), 8f, Color.DarkGreen);
    }

    // Renders a 0..1 time-of-day fraction as a 24-hour clock string.
    static string FormatTimeOfDay(float t)
    {
        float hoursFloat = ((t % 1f) + 1f) % 1f * 24f;
        int hours = (int)hoursFloat;
        int minutes = (int)((hoursFloat - hours) * 60f);
        return $"{hours:00}:{minutes:00}";
    }
}
