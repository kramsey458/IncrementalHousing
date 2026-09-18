using IncrementalHousing;
// Fixtures may enqueue only selected actors. The production adapter uses StartDay,
// whose catalog traversal is exercised separately by Preview6Checks.
static class FixtureQueue
{
    public static void EnqueueDay(this Optimizer optimizer, IEnumerable<Guid> ids)
    {
        optimizer.StartDay(); optimizer.State.DailyScan=false;
        foreach(var id in ids.Distinct().OrderBy(x=>x)) optimizer.Enqueue(id);
    }
}
