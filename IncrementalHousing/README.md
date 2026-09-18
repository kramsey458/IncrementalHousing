# Incremental Housing — Preview 1

Standalone Timberborn 1.1 mod, built against installed game **1.1.2.4**. Version **0.1.0**.
Gradually improves beavers' home-to-work commutes through beneficial moves and swaps.

## Installation

1. Close Timberborn. Extract `IncrementalHousing-preview1.zip` into
   `Documents/Timberborn/Mods`. The ZIP contains an `IncrementalHousing-Preview` folder.
2. Launch Timberborn and enable **Incremental Housing - Preview 0.1.0** in the mod manager.
3. Restart the game to apply the change.
4. Load a copy of your save. Processing begins at the next daytime-start event.
   On subsequent loads, an existing saved queue resumes immediately.

This is a standalone mod with no additional mod dependencies.
For multiplayer, install the identical package on both computers.

## Behavior

- At daytime start, queue district beavers in persistent entity-ID order.
  Pending work stays ahead of newly queued work, so large towns cannot starve
  beavers at the end of the list.
- Evaluate employed, housed adult beavers. Leave children, homeless beavers and
  unemployed actors to vanilla housing. An unemployed adult can be a swap partner;
  its assigned-workplace commute cost is zero.
- Measure actual road-path distance from home to the assigned workplace.
  Ignore unreachable routes, paused/automatically disabled workplaces and homes,
  blocked homes, deleted entities, and cross-district assignments.
- Evaluate vacancies in all existing homes in the actor's district. A vacancy
  means a free adult-eligible bed, not necessarily a completely empty building.
  Keep Folktails procreation-house child capacity available. Other houses may use
  any unoccupied bed.
- If a closer home has no adult-eligible vacancy, evaluate its adult occupants as
  swap partners. The initiating beaver must improve by more than **0.5 path-distance
  units**, and total saving for both beavers must also exceed **0.5**. For example,
  saving 12 while costing the partner 4 is accepted; saving 12 while costing 13 is rejected.
- Choose the largest combined saving found. Ties prefer the actor's shorter
  commute, then a direct move, then stable home and partner IDs.
- Recheck the chosen move at commit time. Changed jobs/homes/districts invalidate
  an actor's unfinished search. A stale destination or newly harmful swap is skipped.
  The next daily pass can reconsider it.
- Existing homes remain assigned throughout planning. Only actual movers are
  unassigned. A full-house swap frees both adult beds and assigns both beavers
  synchronously in the same simulation tick.

The algorithm makes local improvements; it does not guarantee a globally optimal
assignment. Three-way cycles, future jobs, mobile workers' actual work sites,
non-work travel, happiness and family/breeding arrangements are outside its objective.
Vanilla systems may subsequently change housing. The mod preserves child beds and
does not swap children, but adult moves can change a home's breeding population.

## Performance and multiplayer design

- **16 candidate steps per simulation tick**, at most one completed actor per tick.
  A step examines one home or one potential swap partner. Large searches resume
  over multiple ticks; the budget never depends on elapsed milliseconds or frame rate.
- At most 64 planner distance lookups per tick, with duplicate home/work queries
  cached within that tick. Actual path API calls are normally fewer. Timberborn's
  underlying road flow-field cache is also reused.
- No daily all-resident eviction; no per-workplace dictionary of every house's
  distance followed by sorting. A daily queue snapshot remains O(population log population).
  Starting an actor snapshots and sorts district home IDs; entering an occupied
  candidate snapshots and sorts its adults. These list operations are not separately
  time-sliced. One cold native path query also has no wall-clock time guarantee.
- The budget bounds per-tick candidate work, not total daily work. A dense settlement
  may take multiple game days to complete its queue. Exploring all houses and swap
  partners spreads the work across ticks. No FPS or milliseconds improvement is
  claimed without in-game profiling.
- Uses normal `ITickableSingleton` simulation ticks and committed district/path
  data, not `Update`, wall time, random numbers, placement-preview registries or
  background Unity access.
- Queue, scan cursors, candidate snapshots and best plan are saved with the game.
  This avoids restarting different scans after a join/rehost/save-load. Distance
  caches are tick-local and cannot change the number of candidate steps.

## Validation

Release build against the installed game assemblies: zero errors and warnings.
36 automated planner checks cover moves, beneficial/harmful swaps, protected child
beds, malformed/unreachable distances, job/home/district changes, deleted beavers,
newly blocked/full houses, deterministic ties, queue rollover and save/load of both
home and resident scans. Randomized mirrored simulations check identical decisions,
capacity and non-increasing total commute over 40 layouts and three passes each.

These are managed planner tests using a fake world; the adapter is compiled against
real game APIs. Native Unity pathfinding, event callbacks and a live two-player
session have **not** been exercised. This is a playtest preview; multiplayer
compatibility still requires live testing.

Recommended first playtest: run through several daytime starts, save/rehost during
processing, change a worker's job, pause/automate housing and remove a road. Confirm
both peers retain identical home assignments and there are no IncrementalHousing
exceptions. Profile morning spikes, per-tick time, path calls and queue completion
on the same colony before raising the work budget.

## Build and tests

Requires .NET SDK 8 and a local Timberborn installation. No NuGet packages required.

```powershell
dotnet build IncrementalHousing/IncrementalHousing.csproj -c Release -p:GameManaged="C:\path\Timberborn_Data\Managed"
dotnet run --project IncrementalHousing.Tests -c Release
```

The tests' Newtonsoft.Json reference defaults to the same standard Steam installation
path; adjust the test project HintPath on another machine. No game DLLs are distributed.

To uninstall, disable this mod in the mod manager and restart the game.
The saved queue can remain in the save; beavers retain normal game home assignments.
Keep an original save copy to undo moves already made.
