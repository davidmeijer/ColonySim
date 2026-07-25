using ColonySim.World;

namespace ColonySim.Pathfinding;

public static class AStar
{
    // isBlocked marks tiles the map alone doesn't know about — chiefly pawns
    // standing still with nowhere left to go, which should be routed around
    // just like rock. Pawns that are still walking are left out of this: a
    // path in progress is expected to clear out of the way (or the mover
    // waits at worst), so only idle pawns count as fixed obstacles.
    public static List<(int X, int Y)> FindPath(TileMap map, int startX, int startY, int goalX, int goalY,
        Func<int, int, bool>? isBlocked = null)
    {
        var result = new List<(int X, int Y)>();
        isBlocked ??= static (_, _) => false;

        // Can't path onto a blocked goal.
        if (!map.IsWalkable(goalX, goalY) || isBlocked(goalX, goalY)) return result;

        var start = (startX, startY);
        var goal = (goalX, goalY);

        // gScore[t] = cheapest known number of steps from start to t.
        var gScore = new Dictionary<(int, int), int> { [start] = 0 };

        // cameFrom[t] = the tile we reached t from (used to rebuild the path).
        var cameFrom = new Dictionary<(int, int), (int, int)>();

        // The "open set": tiles discovered but not yet expanded. A plain list is
        // fine for small maps; upgrade to a priority queue if maps get large.
        var open = new List<(int, int)> { start };

        while (open.Count > 0)
        {
            // Pick the open tile with the lowest f = g + h.
            var current = open[0];
            int bestF = gScore[current] + Heuristic(current, goal);
            foreach (var node in open)
            {
                int f = gScore[node] + Heuristic(node, goal);
                if (f < bestF) { bestF = f; current = node; }
            }

            // Reached the goal — walk the cameFrom chain back into a path.
            if (current == goal)
                return Reconstruct(cameFrom, current);

            open.Remove(current);

            // Consider the four orthogonal neighbours.
            foreach (var neighbour in Neighbours(current))
            {
                // CanStep folds in IsWalkable, plus the max-climb rule: a
                // neighbour more than half a block higher isn't a valid step
                // even though it's perfectly walkable ground on its own.
                if (!map.CanStep(current.Item1, current.Item2, neighbour.Item1, neighbour.Item2)) continue;
                if (isBlocked(neighbour.Item1, neighbour.Item2)) continue;

                int tentative = gScore[current] + 1; // each step costs 1

                // If this is a new tile, or a cheaper route to a known tile, record it.
                if (!gScore.TryGetValue(neighbour, out int existing) || tentative < existing)
                {
                    cameFrom[neighbour] = current;
                    gScore[neighbour] = tentative;
                    if (!open.Contains(neighbour))
                        open.Add(neighbour);
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
