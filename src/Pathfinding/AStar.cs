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

            // Consider all 8 surrounding neighbours — straight and diagonal
            // — so a route that's actually diagonal comes back as one
            // straight diagonal line instead of an orthogonal staircase.
            // (Actor.Update seeks continuously toward each waypoint, so a
            // staircase of tiny fine-voxel steps used to visibly read as a
            // jittery left-right shuffle instead of a smooth diagonal glide.)
            foreach (var neighbour in Neighbours(current))
            {
                if (closed.Contains(neighbour)) continue;

                // CanStep folds in CanOccupy, the max-climb rule, and (for a
                // diagonal neighbour) a corner-cut check against the two
                // flanking orthogonal voxels.
                if (!map.CanStep(current.Item1, current.Item2, neighbour.Item1, neighbour.Item2, footprintSize, height)) continue;

                bool diagonal = current.Item1 != neighbour.Item1 && current.Item2 != neighbour.Item2;
                int tentative = gScore[current] + (diagonal ? DiagonalCost : StraightCost);

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

    // Matches Neighbours' step costs below: 10 for a straight step, 14 for a
    // diagonal one (10*sqrt(2), rounded) — scaled-integer costs so the
    // PriorityQueue<T,int> and gScore dictionary can stay integer-keyed
    // instead of switching the whole search to floats.
    const int StraightCost = 10;
    const int DiagonalCost = 14;

    // Octile distance: the cheapest possible mix of diagonal and straight
    // steps between two tiles ignoring obstacles — diagonal steps cover one
    // of each axis at once, so as many as possible (min(dx,dy)) get taken
    // before the remainder is walked straight. Matching Neighbours' actual
    // step costs (rather than reusing plain Manhattan distance) keeps this
    // admissible now that diagonal moves are cheaper per tile of progress
    // than two orthogonal ones — an overestimating heuristic would make the
    // search no longer guaranteed to find the shortest path.
    static int Heuristic((int X, int Y) a, (int X, int Y) b)
    {
        int dx = Math.Abs(a.X - b.X);
        int dy = Math.Abs(a.Y - b.Y);
        int diagonal = Math.Min(dx, dy);
        int straight = Math.Abs(dx - dy);
        return diagonal * DiagonalCost + straight * StraightCost;
    }

    static IEnumerable<(int, int)> Neighbours((int X, int Y) t)
    {
        yield return (t.X + 1, t.Y);
        yield return (t.X - 1, t.Y);
        yield return (t.X, t.Y + 1);
        yield return (t.X, t.Y - 1);
        yield return (t.X + 1, t.Y + 1);
        yield return (t.X + 1, t.Y - 1);
        yield return (t.X - 1, t.Y + 1);
        yield return (t.X - 1, t.Y - 1);
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
