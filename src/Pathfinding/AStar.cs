using ColonySim.World;

namespace ColonySim.Pathfinding;

public static class AStar
{
    // Actors never block pathfinding for one another (see Program.cs's
    // ResolveOverlaps for how a crowd still avoids fully overlapping) — the
    // only obstacles a route has to route around are ones the map itself
    // knows about (rock, deep water, trees/bushes/campfires/light posts,
    // too-steep steps). footprintSize/height are the mover's own
    // footprint/height (see TileMap.CanOccupy) — every node visited has to
    // actually fit the mover, not just be occupiable for some generic point.
    //
    // Runs on the fine voxel grid (up to 800x600 = 480,000 cells on the
    // default map, ~100x the old coarse grid), so the open set is a real
    // binary-heap PriorityQueue rather than a linearly-scanned list — a
    // linear scan was fine at coarse-grid scale but would visibly stall on
    // a long or failed search at this resolution (e.g. RepathStuckActors
    // retrying across open ground). Lazy deletion (skip a popped node if
    // it's already closed) stands in for a real decrease-key, since
    // PriorityQueue<T,TPriority> doesn't expose one.
    public static List<(int X, int Y)> FindPath(
        TileMap map, int startX, int startY, int goalX, int goalY, int footprintSize, int height)
    {
        var result = new List<(int X, int Y)>();

        // Can't path onto a blocked goal.
        if (!map.CanOccupy(goalX, goalY, footprintSize, height)) return result;

        var start = (startX, startY);
        var goal = (goalX, goalY);

        // gScore[t] = cheapest known number of steps from start to t.
        var gScore = new Dictionary<(int, int), int> { [start] = 0 };

        // cameFrom[t] = the tile we reached t from (used to rebuild the path).
        var cameFrom = new Dictionary<(int, int), (int, int)>();

        var open = new PriorityQueue<(int, int), int>();
        open.Enqueue(start, Heuristic(start, goal));
        var closed = new HashSet<(int, int)>();

        while (open.Count > 0)
        {
            var current = open.Dequeue();

            // A stale duplicate of a node already finalized through a
            // cheaper route — this is the lazy-deletion half of the trick,
            // skip it rather than re-expanding.
            if (!closed.Add(current)) continue;

            // Reached the goal — walk the cameFrom chain back into a path.
            if (current == goal)
                return Reconstruct(cameFrom, current);

            // Consider the four orthogonal neighbours.
            foreach (var neighbour in Neighbours(current))
            {
                if (closed.Contains(neighbour)) continue;

                // CanStep folds in CanOccupy, plus the max-climb rule: a
                // neighbour more than half a block higher isn't a valid step
                // even though it's perfectly occupiable ground on its own.
                if (!map.CanStep(current.Item1, current.Item2, neighbour.Item1, neighbour.Item2, footprintSize, height)) continue;

                int tentative = gScore[current] + 1; // each step costs 1

                // If this is a new tile, or a cheaper route to a known tile, record it.
                if (!gScore.TryGetValue(neighbour, out int existing) || tentative < existing)
                {
                    cameFrom[neighbour] = current;
                    gScore[neighbour] = tentative;
                    open.Enqueue(neighbour, tentative + Heuristic(neighbour, goal));
                }
            }
        }

        // Open set emptied without reaching the goal: no path exists.
        return result;
    }

    // Manhattan distance: grid steps between two tiles, ignoring obstacles.
    static int Heuristic((int X, int Y) a, (int X, int Y) b) =>
        Math.Abs(a.X - b.X) + Math.Abs(a.Y - b.Y);

    static IEnumerable<(int, int)> Neighbours((int X, int Y) t)
    {
        yield return (t.X + 1, t.Y);
        yield return (t.X - 1, t.Y);
        yield return (t.X, t.Y + 1);
        yield return (t.X, t.Y - 1);
    }

    // Follow cameFrom from the goal back to the start, then reverse.
    static List<(int X, int Y)> Reconstruct(Dictionary<(int, int), (int, int)> cameFrom, (int, int) current)
    {
        var path = new List<(int X, int Y)> { current };
        while (cameFrom.ContainsKey(current))
        {
            current = cameFrom[current];
            path.Add(current);
        }
        path.Reverse();
        return path;
    }
}
