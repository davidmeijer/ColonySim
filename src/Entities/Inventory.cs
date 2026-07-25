namespace ColonySim.Entities;

// A pawn's carried materials, tracked as a count per material name rather
// than a list of individual objects — a single dig can hand over up to a
// full tile layer (100 voxels) at once, so per-item objects would just be
// wasted allocations with no benefit.
public class Inventory
{
  public const int Capacity = 1000;

  readonly Dictionary<string, int> _counts = new();
  public IReadOnlyDictionary<string, int> Counts => _counts;

  public int Total => _counts.Values.Sum();
  public bool IsFull => Total >= Capacity;
  public int Room => Math.Max(0, Capacity - Total);

  // Adds up to `count` of an item, capped by whatever room is left.
  // Returns how many were actually added.
  public int Add(string item, int count)
  {
    int actual = Math.Min(count, Room);
    if (actual <= 0) return 0;
    _counts[item] = _counts.GetValueOrDefault(item) + actual;
    return actual;
  }

  // Consumes exactly `count` of an item, or does nothing if there isn't
  // enough. Not a "drop" action a player can trigger arbitrarily — this is
  // only for spending carried material on a purposeful action (e.g.
  // depositing dirt to raise the ground), which does need a way to
  // actually remove items.
  public bool TryRemove(string item, int count)
  {
    if (!_counts.TryGetValue(item, out int have) || have < count) return false;
    int remaining = have - count;
    if (remaining <= 0) _counts.Remove(item);
    else _counts[item] = remaining;
    return true;
  }
}
