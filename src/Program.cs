using System.Numerics;
using Raylib_cs;
using ColonySim.World;
using ColonySim.Entities;
using ColonySim.Pathfinding;
using ColonySim.Rendering;

namespace ColonySim;

public static class Program
{
    const int ScreenWidth = 1280;
    const int ScreenHeight = 720;
    const int ActorCount = 3;

    // How far (in screen pixels) the mouse has to move while a button is held
    // before a press+release counts as a drag instead of a click.
    const float DragThreshold = 6f;

    // Mutable input/selection state for the frame loop. Bundled into one
    // object instead of a growing pile of ref parameters.
    class SelectionState
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
    static readonly Rectangle DigButtonRect = new(100, ScreenHeight - 66, 80, 28);
    static readonly Rectangle DepositButtonRect = new(190, ScreenHeight - 66, 100, 28);

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
    class SettingsMenuState
    {
        public bool Open;
        public bool DraggingTime;
        public bool DraggingSpeed;
    }

    // Top-left build icon (kept well clear of the "Selected: x/y" text next
    // to it, and of the settings gear in the opposite corner) that opens a
    // small palette of placeable items. Picking an item arms Placing —
    // the next left-click on a valid tile drops it there.
    static readonly Rectangle BuildButtonRect = new(150, 10, 36, 36);

    const float BuildPanelWidth = 150f;
    const float BuildPanelX = 150f;
    const float BuildPanelY = 56f;
    static readonly Rectangle BuildPanelRect = new(BuildPanelX, BuildPanelY, BuildPanelWidth, 54f);
    static readonly Rectangle CampfireItemRect = new(BuildPanelX + 8, BuildPanelY + 8, BuildPanelWidth - 16, 38);

    class BuildMenuState
    {
        public bool Open;
        public bool Placing; // an item is armed; the next tile click places it

        // Tracked separately from SelectionState's identical right-click
        // fields so a right-drag to orbit the camera while lining up a
        // placement doesn't get mistaken for the cancel gesture below.
        public Vector2? RightDownPos;
        public bool RightDragged;
    }

    public static void Main()
    {
        // Create the OS window and cap the loop at 60 frames per second.
        Raylib.InitWindow(ScreenWidth, ScreenHeight, "Colony Sim - v0.3 (3D)");
        Raylib.SetTargetFPS(60);

        // The world: a voxel grid with height. The seed makes generation repeatable.
        var map = new TileMap(40, 30, seed: 1234);

        // A roster of actors, scattered across distinct walkable tiles.
        var spawnRng = new Random(99);
        var spawnTiles = map.WalkableTiles().OrderBy(_ => spawnRng.Next()).Take(ActorCount).ToList();
        var actors = spawnTiles.Select(t => new Actor(map, t.X, t.Y)).ToList();

        var selection = new SelectionState();
        var settingsMenu = new SettingsMenuState();
        var buildMenu = new BuildMenuState();
        bool showGrid = false;
        bool showPathDots = false;

        // A slowly-drifting directional light, so faces catch shading
        // depending on which way they point — that plus TileMap's block
        // outlines are what make the hills read as 3D instead of a flat
        // green blob.
        var sun = new SunLight();

        // The terrain mesh draws through its own material, not
        // BeginShaderMode, so it has to be wired to the sun's shader explicitly.
        map.SetTerrainShader(sun.Shader);

        // Orbit-camera state: where it's looking, and its angle/distance from that point.
        var camTarget = new Vector3(map.WidthPx / 2f, 0f, map.DepthPx / 2f);
        float camYaw = 45f;
        float camPitch = 50f;
        float camDistance = 620f;

        var camera = new Camera3D
        {
            Target = camTarget,
            Up = new Vector3(0f, 1f, 0f),
            FovY = 45f,
            Projection = CameraProjection.Perspective
        };

        // The main loop. WindowShouldClose() becomes true when the user hits the
        // window's close button or presses ESC.
        while (!Raylib.WindowShouldClose())
        {
            // dt = "delta time": how many seconds the last frame took. Multiplying
            // movement by dt makes speeds frame-rate independent.
            float dt = Raylib.GetFrameTime();

            Update(dt, map, actors, selection, settingsMenu, buildMenu, sun, ref camera, ref camTarget, ref camYaw, ref camPitch, ref camDistance, ref showGrid, ref showPathDots);
            map.UpdateWater(dt);
            map.UpdateVegetation(dt);
            sun.Update(dt);
            Draw(map, actors, selection, settingsMenu, buildMenu, camera, sun, showGrid, showPathDots);
        }

        sun.Unload();
        map.Unload();
        Raylib.CloseWindow();
    }

    static void Update(float dt, TileMap map, List<Actor> actors, SelectionState selection, SettingsMenuState settingsMenu,
        BuildMenuState buildMenu, SunLight sun, ref Camera3D camera, ref Vector3 camTarget, ref float yaw, ref float pitch,
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

        // The build menu is checked first: while an item is armed
        // (Placing), it swallows every click itself, so the settings panel
        // and action menu are skipped entirely for that frame — otherwise a
        // placement click that happens to land over one of their buttons
        // (bottom-left action menu, top-right settings) would fire that
        // button instead of placing anything.
        bool buildConsumedClick = UpdateBuildMenu(map, buildMenu, camera);

        // The settings panel lives outside the selection-driven action menu
        // (it has to work with nothing selected), but its clicks still need
        // consuming for the same reason: otherwise they'd also register as
        // world clicks and stomp the selection.
        bool settingsConsumedClick = !buildMenu.Placing && UpdateSettingsMenu(sun, settingsMenu, ref showGrid, ref showPathDots);

        // Action-menu clicks are handled first and, if one lands, consumed —
        // otherwise clicking "Jump" would also register as a world click and
        // immediately clear the very selection you just acted on.
        bool menuConsumedClick = !buildMenu.Placing && UpdateActionMenu(map, selection);
        UpdateSelection(actors, selection, camera, shiftHeld, menuConsumedClick || settingsConsumedClick || buildConsumedClick);
        if (!buildMenu.Placing) UpdateMoveOrders(map, actors, selection, camera);

        // Everyone seeks their own next waypoint independently — no per-tile
        // locking — then overlaps get shoved apart, then anyone who's made
        // no real progress for a while (stuck against something a shove
        // can't clear) gets a fresh route. Together this is what lets a
        // whole group arrive without freezing solid or leaving stragglers
        // parked mid-route.
        foreach (var actor in actors) actor.Update(dt);
        ResolveOverlaps(actors);
        RepathStuckActors(map, actors);
        UpdateWandering(map, actors);
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
    // route to the same final destination, re-checking blocking actors right
    // now. Uses Reroute (not SetPath) so repeated failures still count
    // toward Actor's own give-up timeout instead of resetting it every try.
    // If no route exists any more, this just leaves them stopped where they are.
    static void RepathStuckActors(TileMap map, List<Actor> actors)
    {
        foreach (var actor in actors)
        {
            if (!actor.Stuck || actor.FinalDestination is not { } dest) continue;

            var blockingTiles = BlockingTiles(actors, except: actor);
            var path = AStar.FindPath(map, actor.TileX, actor.TileZ, dest.X, dest.Y, PathBlocker(blockingTiles, actor));
            actor.Reroute(path);
        }
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
    static void UpdateWandering(TileMap map, List<Actor> actors)
    {
        foreach (var actor in actors)
        {
            if (!actor.WantsToWander) continue;

            if (PickWanderTile(map, actor.TileX, actor.TileZ) is not { } target)
            {
                actor.DeferWander();
                continue;
            }

            var blockingTiles = BlockingTiles(actors, except: actor);
            var path = AStar.FindPath(map, actor.TileX, actor.TileZ, target.X, target.Z, PathBlocker(blockingTiles, actor));
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

        if (Raylib.CheckCollisionPointRec(mouse, DigButtonRect))
        {
            // Digs the whole topmost layer at once, capped by whatever room
            // is left in each actor's inventory. Silently does nothing for
            // an actor that isn't standing on Grass or Dirt, or has no room
            // at all. No explicit re-snap needed: Actor.Update re-reads the
            // ground height under it every frame, so it settles automatically.
            foreach (var actor in selection.Selected)
            {
                int room = actor.Inventory.Room;
                if (room <= 0) continue;
                int dug = map.Dig(actor.TileX, actor.TileZ, room);
                if (dug > 0)
                {
                    actor.Inventory.Add("Dirt", dug);
                    actor.PlayDig();
                }
            }
            return true;
        }

        if (Raylib.CheckCollisionPointRec(mouse, DepositButtonRect))
        {
            // Silently does nothing for an actor that doesn't have enough
            // carried voxels to top off its current tile's partial level,
            // or whose tile is already at the maximum height.
            foreach (var actor in selection.Selected)
            {
                int needed = map.VoxelsNeededToRaise(actor.TileX, actor.TileZ);
                if (actor.Inventory.Total < needed) continue;
                if (map.Deposit(actor.TileX, actor.TileZ) == 0) continue;

                actor.Inventory.TryRemove("Dirt", needed);
                actor.PlayDeposit();
            }
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
    static void UpdateMoveOrders(TileMap map, List<Actor> actors, SelectionState selection, Camera3D camera)
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
                var blockingTiles = BlockingTiles(actors);
                var destinations = AssignDestinations(map, movers, hitX, hitZ, blockingTiles);
                foreach (var (actor, dest) in destinations)
                {
                    var path = AStar.FindPath(map, actor.TileX, actor.TileZ, dest.X, dest.Z,
                        PathBlocker(blockingTiles, actor));
                    actor.SetPath(path);
                }
            }

            selection.RightDownPos = null;
            selection.RightDragged = false;
        }
    }

    // Ray-casts from a screen position through every coarse tile's column
    // bounds and returns the nearest one hit — the shared "what tile is the
    // mouse over" query used by move orders and campfire placement alike.
    static bool TryPickTile(TileMap map, Camera3D camera, Vector2 screenPos, out int tileX, out int tileZ)
    {
        Ray ray = Raylib.GetScreenToWorldRay(screenPos, camera);

        int hitX = -1, hitZ = -1;
        float bestDist = float.MaxValue;
        for (int x = 0; x < map.Width; x++)
        {
            for (int z = 0; z < map.Depth; z++)
            {
                var hit = Raylib.GetRayCollisionBox(ray, map.ColumnBounds(x, z));
                if (hit.Hit && hit.Distance < bestDist)
                {
                    bestDist = hit.Distance;
                    hitX = x;
                    hitZ = z;
                }
            }
        }

        tileX = hitX;
        tileZ = hitZ;
        return hitX >= 0;
    }

    // The build icon toggles the palette; picking "Campfire" arms Placing,
    // and the very next left-click (wherever it lands, on a valid tile or
    // not) either places the fire or is simply swallowed — see the
    // Placing-gated calls in Update() for why every other click handler
    // steps aside while this is true. Right-click or Escape cancels
    // placement without closing anything else.
    static bool UpdateBuildMenu(TileMap map, BuildMenuState menu, Camera3D camera)
    {
        Vector2 mouse = Raylib.GetMousePosition();
        bool leftPressed = Raylib.IsMouseButtonPressed(MouseButton.Left);

        if (leftPressed && Raylib.CheckCollisionPointRec(mouse, BuildButtonRect))
        {
            menu.Open = !menu.Open;
            if (!menu.Open) menu.Placing = false;
            return true;
        }

        if (menu.Placing)
        {
            if (Raylib.IsKeyPressed(KeyboardKey.Escape))
            {
                menu.Placing = false;
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
                    menu.Placing = false;
                    return true;
                }
            }

            if (leftPressed)
            {
                if (TryPickTile(map, camera, mouse, out int tx, out int tz) && map.CanPlaceCampfire(tx, tz))
                    map.PlaceCampfire(tx, tz);
                menu.Placing = false;
                return true;
            }

            return true; // swallow every click (and non-click) frame while armed
        }

        if (!menu.Open) return false;

        if (leftPressed)
        {
            if (Raylib.CheckCollisionPointRec(mouse, CampfireItemRect))
            {
                menu.Placing = true;
                menu.Open = false;
                return true;
            }

            if (Raylib.CheckCollisionPointRec(mouse, BuildPanelRect)) return true;

            menu.Open = false; // click outside closes it, same as the settings panel
        }

        return false;
    }

    // Tiles that should be routed around like solid rock: actors standing
    // still with no route, AND actors currently Stuck (moving in name only —
    // they haven't actually gone anywhere in a while). Without including
    // Stuck actors here, a fresh route (manual or automatic) could plan
    // straight back through the exact jam it's trying to get out of, which
    // made it look like re-ordering a stuck actor did nothing. Actors making
    // real progress are left out; they're expected to be elsewhere by the
    // time anyone else gets there.
    static HashSet<(int X, int Z)> BlockingTiles(List<Actor> actors, Actor? except = null) =>
        actors.Where(p => p != except && (!p.IsMoving || p.Stuck)).Select(p => (p.TileX, p.TileZ)).ToHashSet();

    // True for a tile standing in for solid ground: occupied by some OTHER
    // blocking actor. An actor is never blocked by its own current tile.
    static Func<int, int, bool> PathBlocker(HashSet<(int X, int Z)> blockingTiles, Actor mover) =>
        (x, z) => blockingTiles.Contains((x, z)) && (x, z) != (mover.TileX, mover.TileZ);

    // Gives each mover its own nearby walkable tile around the target, so a
    // group order doesn't send everyone to stack on the exact same spot.
    // Closer actors claim the closer slots first.
    static Dictionary<Actor, (int X, int Z)> AssignDestinations(
        TileMap map, List<Actor> movers, int targetX, int targetZ, HashSet<(int X, int Z)> blockingTiles)
    {
        var claimed = new HashSet<(int, int)>();
        var result = new Dictionary<Actor, (int, int)>();

        foreach (var actor in movers.OrderBy(p => Math.Abs(p.TileX - targetX) + Math.Abs(p.TileZ - targetZ)))
        {
            var tile = FindNearestFreeTile(map, targetX, targetZ, claimed, PathBlocker(blockingTiles, actor));
            if (tile is { } t)
            {
                claimed.Add(t);
                result[actor] = t;
            }
        }
        return result;
    }

    // Spirals outward ring by ring from (cx, cz) until it finds a walkable
    // tile nobody else in this order has claimed, and no idle actor is
    // already standing on.
    static (int X, int Z)? FindNearestFreeTile(
        TileMap map, int cx, int cz, HashSet<(int, int)> claimed, Func<int, int, bool> blocked)
    {
        if (map.IsWalkable(cx, cz) && !claimed.Contains((cx, cz)) && !blocked(cx, cz)) return (cx, cz);

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
                    if (blocked(x, z)) continue;
                    return (x, z);
                }
            }
        }
        return null; // no free tile anywhere — shouldn't happen in practice
    }

    static void Draw(TileMap map, List<Actor> actors, SelectionState selection, SettingsMenuState settingsMenu,
        BuildMenuState buildMenu, Camera3D camera, SunLight sun, bool showGrid, bool showPathDots)
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
        map.DrawTrees();
        map.DrawBushes();
        map.DrawCampfiresLit();
        map.DrawWater();
        foreach (var actor in actors) actor.DrawSolid(showPathDots);
        sun.EndLit();

        // Unlit pass: faint edges on every block so height steps are legible
        // even where the shading alone doesn't make it obvious, plus each
        // actor's outline and selection ring. The grid itself is optional —
        // toggled from the settings panel — everything else here isn't.
        if (showGrid) map.DrawOutlines();
        foreach (var actor in actors) actor.DrawOutline();

        // A ghost ring on whichever tile the mouse is over while an item is
        // armed: green if it's a legal spot, red if not (water, a tree, an
        // existing fire, ...), so the player knows before clicking.
        if (buildMenu.Placing && TryPickTile(map, camera, Raylib.GetMousePosition(), out int hoverX, out int hoverZ))
        {
            bool valid = map.CanPlaceCampfire(hoverX, hoverZ);
            Color ringColor = valid ? new Color(255, 170, 60, 220) : new Color(220, 60, 60, 220);
            Vector3 ringCenter = new(
                hoverX * TileMap.TileSize + TileMap.TileSize / 2f,
                map.SurfaceY(hoverX, hoverZ) + 2f,
                hoverZ * TileMap.TileSize + TileMap.TileSize / 2f);
            Raylib.DrawCircle3D(ringCenter, TileMap.TileSize * 0.45f, new Vector3(1f, 0f, 0f), 90f, ringColor);
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

        // The HUD is drawn in *screen* space, so it stays put.
        Raylib.DrawText($"Selected: {selection.Selected.Count}/{actors.Count}", 10, 10, 18, Color.Black);

        DrawSettingsMenu(sun, settingsMenu, showGrid, showPathDots);
        DrawBuildMenu(buildMenu);

        // The action menu, only while something's selected.
        if (selection.Selected.Count > 0)
        {
            DrawButton(JumpButtonRect, "Jump");
            DrawButton(DigButtonRect, "Dig");
            DrawButton(DepositButtonRect, "Deposit");

            if (selection.Selected.Count == 1)
            {
                var actor = selection.Selected.First();
                string items = actor.Inventory.Counts.Count == 0
                    ? "(empty)"
                    : string.Join(", ", actor.Inventory.Counts.Select(kv => $"{kv.Key} x{kv.Value}"));
                int needed = map.VoxelsNeededToRaise(actor.TileX, actor.TileZ);
                Raylib.DrawText(
                    $"Inventory ({actor.Inventory.Total}/{Inventory.Capacity}): {items}   " +
                    $"(deposit needs {needed})",
                    300, ScreenHeight - 58, 16, Color.Black);
            }
        }

        Raylib.DrawFPS(10, ScreenHeight - 30);

        Raylib.EndDrawing();
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

    // The build icon plus, when open, a small palette of placeable items
    // (currently just Campfire) — or, while an item is armed, a hint that
    // placement mode is active instead of the palette itself.
    static void DrawBuildMenu(BuildMenuState menu)
    {
        DrawButton(BuildButtonRect, "+", active: menu.Open || menu.Placing);

        if (menu.Placing)
        {
            Raylib.DrawText("Click a tile to place the campfire (Esc / right-click to cancel)",
                (int)BuildButtonRect.X, (int)(BuildButtonRect.Y + BuildButtonRect.Height + 6), 16, Color.Black);
            return;
        }

        if (!menu.Open) return;

        Raylib.DrawRectangleRec(BuildPanelRect, new Color(245, 245, 245, 235));
        Raylib.DrawRectangleLinesEx(BuildPanelRect, 1.5f, Color.DarkGray);
        DrawButton(CampfireItemRect, "Campfire");
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
