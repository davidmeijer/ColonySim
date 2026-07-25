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
    const int PawnCount = 10;

    // How far (in screen pixels) the mouse has to move while a button is held
    // before a press+release counts as a drag instead of a click.
    const float DragThreshold = 6f;

    // Mutable input/selection state for the frame loop. Bundled into one
    // object instead of a growing pile of ref parameters.
    class SelectionState
    {
        public readonly HashSet<Pawn> Selected = new();

        public Vector2? LeftDownPos;
        public bool BoxSelecting;

        public Vector2? RightDownPos;
        public bool RightDragged;
    }

    // The action menu: a few buttons that appear along the bottom of the
    // screen whenever at least one pawn is selected. Fixed screen positions,
    // so Update (input) and Draw (rendering) can each compute the same
    // rectangles independently without sharing extra layout state.
    static readonly Rectangle JumpButtonRect = new(10, ScreenHeight - 66, 80, 28);
    static readonly Rectangle DigButtonRect = new(100, ScreenHeight - 66, 80, 28);
    static readonly Rectangle DepositButtonRect = new(190, ScreenHeight - 66, 100, 28);

    // Top-right, independent of the selection-driven action menu — grid
    // visibility isn't tied to having a pawn selected.
    static readonly Rectangle GridButtonRect = new(ScreenWidth - 100, 10, 90, 28);

    public static void Main()
    {
        // Create the OS window and cap the loop at 60 frames per second.
        Raylib.InitWindow(ScreenWidth, ScreenHeight, "Colony Sim - v0.3 (3D)");
        Raylib.SetTargetFPS(60);

        // The world: a voxel grid with height. The seed makes generation repeatable.
        var map = new TileMap(40, 30, seed: 1234);

        // A roster of pawns, scattered across distinct walkable tiles.
        var spawnRng = new Random(99);
        var spawnTiles = map.WalkableTiles().OrderBy(_ => spawnRng.Next()).Take(PawnCount).ToList();
        var pawns = spawnTiles.Select(t => new Pawn(map, t.X, t.Y)).ToList();

        var selection = new SelectionState();
        bool showGrid = true;

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

            Update(dt, map, pawns, selection, ref camera, ref camTarget, ref camYaw, ref camPitch, ref camDistance, ref showGrid);
            map.UpdateWater(dt);
            map.UpdateVegetation(dt);
            sun.Update(dt);
            Draw(map, pawns, selection, camera, sun, showGrid);
        }

        sun.Unload();
        map.Unload();
        Raylib.CloseWindow();
    }

    static void Update(float dt, TileMap map, List<Pawn> pawns, SelectionState selection, ref Camera3D camera,
        ref Vector3 camTarget, ref float yaw, ref float pitch, ref float distance, ref bool showGrid)
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

        // The grid toggle lives outside the selection-driven action menu (it
        // has to work with nothing selected), but its click still needs
        // consuming for the same reason: otherwise it'd also register as a
        // world click and stomp the selection.
        bool gridConsumedClick = UpdateGridToggle(ref showGrid);

        // Action-menu clicks are handled first and, if one lands, consumed —
        // otherwise clicking "Jump" would also register as a world click and
        // immediately clear the very selection you just acted on.
        bool menuConsumedClick = UpdateActionMenu(map, selection);
        UpdateSelection(pawns, selection, camera, shiftHeld, menuConsumedClick || gridConsumedClick);
        UpdateMoveOrders(map, pawns, selection, camera);

        // Everyone seeks their own next waypoint independently — no per-tile
        // locking — then overlaps get shoved apart, then anyone who's made
        // no real progress for a while (stuck against something a shove
        // can't clear) gets a fresh route. Together this is what lets a
        // whole group arrive without freezing solid or leaving stragglers
        // parked mid-route.
        foreach (var pawn in pawns) pawn.Update(dt);
        ResolveOverlaps(pawns);
        RepathStuckPawns(map, pawns);
    }

    // Soft collision: pushes any pair of pawns that end up closer than two
    // radii back apart, split evenly, in the horizontal plane only (height
    // always comes from the terrain). A few relaxation passes keep a knot of
    // 3+ pawns from jittering instead of settling.
    static void ResolveOverlaps(List<Pawn> pawns)
    {
        const float MinDist = Pawn.Radius * 2f;

        for (int pass = 0; pass < 3; pass++)
        {
            for (int i = 0; i < pawns.Count; i++)
            {
                for (int j = i + 1; j < pawns.Count; j++)
                {
                    Vector3 delta3 = pawns[j].WorldPos - pawns[i].WorldPos;
                    Vector2 delta = new(delta3.X, delta3.Z);
                    float dist = delta.Length();
                    if (dist >= MinDist) continue;

                    // Coincident centres (rare) push along an arbitrary axis
                    // rather than dividing by zero.
                    Vector2 pushDir = dist > 0.0001f ? delta / dist : new Vector2(1f, 0f);
                    Vector2 correction = pushDir * ((MinDist - dist) * 0.5f);

                    pawns[i].Nudge(-correction);
                    pawns[j].Nudge(correction);
                }
            }
        }
    }

    // Anyone who's been stuck for too long (see Pawn.Stuck) gets a brand new
    // route to the same final destination, re-checking blocking pawns right
    // now. Uses Reroute (not SetPath) so repeated failures still count
    // toward Pawn's own give-up timeout instead of resetting it every try.
    // If no route exists any more, this just leaves them stopped where they are.
    static void RepathStuckPawns(TileMap map, List<Pawn> pawns)
    {
        foreach (var pawn in pawns)
        {
            if (!pawn.Stuck || pawn.FinalDestination is not { } dest) continue;

            var blockingTiles = BlockingTiles(pawns, except: pawn);
            var path = AStar.FindPath(map, pawn.TileX, pawn.TileZ, dest.X, dest.Y, PathBlocker(blockingTiles, pawn));
            pawn.Reroute(path);
        }
    }

    // Flips the coarse 10x10 grid overlay on/off. Always live, unlike the
    // action menu, since it isn't tied to having a pawn selected.
    static bool UpdateGridToggle(ref bool showGrid)
    {
        if (!Raylib.IsMouseButtonPressed(MouseButton.Left)) return false;
        if (!Raylib.CheckCollisionPointRec(Raylib.GetMousePosition(), GridButtonRect)) return false;

        showGrid = !showGrid;
        return true;
    }

    // The action menu only does anything once at least one pawn is selected.
    // Returns true if a button was actually clicked this frame, so the
    // world-selection click handling below can skip that same click.
    static bool UpdateActionMenu(TileMap map, SelectionState selection)
    {
        if (selection.Selected.Count == 0) return false;
        if (!Raylib.IsMouseButtonPressed(MouseButton.Left)) return false;

        Vector2 mouse = Raylib.GetMousePosition();

        if (Raylib.CheckCollisionPointRec(mouse, JumpButtonRect))
        {
            foreach (var pawn in selection.Selected) pawn.Jump();
            return true;
        }

        if (Raylib.CheckCollisionPointRec(mouse, DigButtonRect))
        {
            // Digs the whole topmost layer at once, capped by whatever room
            // is left in each pawn's inventory. Silently does nothing for a
            // pawn that isn't standing on Grass or Dirt, or has no room at
            // all. No explicit re-snap needed: Pawn.Update re-reads the
            // ground height under it every frame, so it settles automatically.
            foreach (var pawn in selection.Selected)
            {
                int room = pawn.Inventory.Room;
                if (room <= 0) continue;
                int dug = map.Dig(pawn.TileX, pawn.TileZ, room);
                if (dug > 0) pawn.Inventory.Add("Dirt", dug);
            }
            return true;
        }

        if (Raylib.CheckCollisionPointRec(mouse, DepositButtonRect))
        {
            // Silently does nothing for a pawn that doesn't have enough
            // carried voxels to top off its current tile's partial level,
            // or whose tile is already at the maximum height.
            foreach (var pawn in selection.Selected)
            {
                int needed = map.VoxelsNeededToRaise(pawn.TileX, pawn.TileZ);
                if (pawn.Inventory.Total < needed) continue;
                if (map.Deposit(pawn.TileX, pawn.TileZ) == 0) continue;

                pawn.Inventory.TryRemove("Dirt", needed);
            }
            return true;
        }

        return false;
    }

    // Left-click: select one pawn (or add/toggle with shift), clear selection
    // on empty ground, or drag out a box to select everyone inside it.
    // uiConsumedClick suppresses all of that for a click the action menu
    // already handled.
    static void UpdateSelection(
        List<Pawn> pawns, SelectionState selection, Camera3D camera, bool shiftHeld, bool uiConsumedClick)
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
                foreach (var pawn in pawns)
                {
                    Vector2 screenPos = Raylib.GetWorldToScreen(pawn.WorldPos, camera);
                    if (Raylib.CheckCollisionPointRec(screenPos, rect))
                        selection.Selected.Add(pawn);
                }
            }
            else
            {
                // A plain click: find the nearest pawn under the cursor, if any.
                Pawn? clicked = null;
                float bestDist = 20f; // pixel pick radius
                foreach (var pawn in pawns)
                {
                    float d = Vector2.Distance(Raylib.GetWorldToScreen(pawn.WorldPos, camera), end);
                    if (d < bestDist) { bestDist = d; clicked = pawn; }
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

        foreach (var pawn in pawns)
            pawn.Selected = selection.Selected.Contains(pawn);
    }

    // Right-click (a click, not a camera-orbit drag): send every selected
    // pawn toward the clicked tile, each to its own nearby free spot.
    static void UpdateMoveOrders(TileMap map, List<Pawn> pawns, SelectionState selection, Camera3D camera)
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
            if (!selection.RightDragged && selection.Selected.Count > 0)
            {
                Ray ray = Raylib.GetScreenToWorldRay(Raylib.GetMousePosition(), camera);

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

                if (hitX >= 0 && map.IsWalkable(hitX, hitZ))
                {
                    // A direct order always overrides whatever a pawn was
                    // doing before, including a give-up — SetPath (not
                    // Reroute) resets its stuck budget for the new attempt.
                    var movers = selection.Selected.ToList();
                    var blockingTiles = BlockingTiles(pawns);
                    var destinations = AssignDestinations(map, movers, hitX, hitZ, blockingTiles);
                    foreach (var (pawn, dest) in destinations)
                    {
                        var path = AStar.FindPath(map, pawn.TileX, pawn.TileZ, dest.X, dest.Z,
                            PathBlocker(blockingTiles, pawn));
                        pawn.SetPath(path);
                    }
                }
            }

            selection.RightDownPos = null;
            selection.RightDragged = false;
        }
    }

    // Tiles that should be routed around like solid rock: pawns standing
    // still with no route, AND pawns currently Stuck (moving in name only —
    // they haven't actually gone anywhere in a while). Without including
    // Stuck pawns here, a fresh route (manual or automatic) could plan
    // straight back through the exact jam it's trying to get out of, which
    // made it look like re-ordering a stuck pawn did nothing. Pawns making
    // real progress are left out; they're expected to be elsewhere by the
    // time anyone else gets there.
    static HashSet<(int X, int Z)> BlockingTiles(List<Pawn> pawns, Pawn? except = null) =>
        pawns.Where(p => p != except && (!p.IsMoving || p.Stuck)).Select(p => (p.TileX, p.TileZ)).ToHashSet();

    // True for a tile standing in for solid ground: occupied by some OTHER
    // blocking pawn. A pawn is never blocked by its own current tile.
    static Func<int, int, bool> PathBlocker(HashSet<(int X, int Z)> blockingTiles, Pawn mover) =>
        (x, z) => blockingTiles.Contains((x, z)) && (x, z) != (mover.TileX, mover.TileZ);

    // Gives each mover its own nearby walkable tile around the target, so a
    // group order doesn't send everyone to stack on the exact same spot.
    // Closer pawns claim the closer slots first.
    static Dictionary<Pawn, (int X, int Z)> AssignDestinations(
        TileMap map, List<Pawn> movers, int targetX, int targetZ, HashSet<(int X, int Z)> blockingTiles)
    {
        var claimed = new HashSet<(int, int)>();
        var result = new Dictionary<Pawn, (int, int)>();

        foreach (var pawn in movers.OrderBy(p => Math.Abs(p.TileX - targetX) + Math.Abs(p.TileZ - targetZ)))
        {
            var tile = FindNearestFreeTile(map, targetX, targetZ, claimed, PathBlocker(blockingTiles, pawn));
            if (tile is { } t)
            {
                claimed.Add(t);
                result[pawn] = t;
            }
        }
        return result;
    }

    // Spirals outward ring by ring from (cx, cz) until it finds a walkable
    // tile nobody else in this order has claimed, and no idle pawn is
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

    static void Draw(TileMap map, List<Pawn> pawns, SelectionState selection, Camera3D camera, SunLight sun, bool showGrid)
    {
        // Rendered from the light's point of view into its own depth
        // texture before anything else — the main lit pass right below
        // needs that result (via the shader's shadowMap uniform) already
        // in hand for this same frame.
        sun.RenderShadowMap(map, pawns);

        Raylib.BeginDrawing();
        Raylib.ClearBackground(sun.SkyColor);

        // Everything between BeginMode3D/EndMode3D is drawn in *world* space,
        // so it moves and scales with the camera.
        Raylib.BeginMode3D(camera);

        sun.DrawCelestialBodies(new Vector3(map.WidthPx / 2f, 100f, map.DepthPx / 2f));

        // Lit pass: shading depends on which way each face points, so the
        // sun alone gives hills a sense of depth.
        sun.BeginLit();
        map.DrawSolid();
        map.DrawTrees();
        map.DrawBushes();
        map.DrawWater();
        foreach (var pawn in pawns) pawn.DrawSolid();
        sun.EndLit();

        // Unlit pass: faint edges on every block so height steps are legible
        // even where the shading alone doesn't make it obvious, plus each
        // pawn's outline and selection ring. The grid itself is optional —
        // toggled by the top-right button — everything else here isn't.
        if (showGrid) map.DrawOutlines();
        foreach (var pawn in pawns) pawn.DrawOutline();

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
        Raylib.DrawText(
            "WASD: pan   Q/E: rotate   R/F: tilt   wheel: zoom   " +
            "left-click/drag/shift: select   right-click: move selected",
            10, 10, 18, Color.Black);
        Raylib.DrawText($"Selected: {selection.Selected.Count}/{pawns.Count}", 10, 32, 18, Color.Black);

        DrawButton(GridButtonRect, "Grid", active: showGrid);

        // The action menu, only while something's selected.
        if (selection.Selected.Count > 0)
        {
            DrawButton(JumpButtonRect, "Jump");
            DrawButton(DigButtonRect, "Dig");
            DrawButton(DepositButtonRect, "Deposit");

            if (selection.Selected.Count == 1)
            {
                var pawn = selection.Selected.First();
                string items = pawn.Inventory.Counts.Count == 0
                    ? "(empty)"
                    : string.Join(", ", pawn.Inventory.Counts.Select(kv => $"{kv.Key} x{kv.Value}"));
                int needed = map.VoxelsNeededToRaise(pawn.TileX, pawn.TileZ);
                Raylib.DrawText(
                    $"Inventory ({pawn.Inventory.Total}/{Inventory.Capacity}): {items}   " +
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
}
