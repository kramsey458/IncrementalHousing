# Incremental Housing — Preview 4

[Download Preview 4](https://github.com/kramsey458/IncrementalHousing/releases/tag/v0.3.1-preview.4)

Standalone Timberborn 1.1 mod, built against game **1.1.2.4**. Version **0.3.1**.
Gradually reduces home-to-assigned-workplace commute cost through beneficial moves and swaps.

## Crash hotfix

Preview 4 fixes the September 18 crash in `HousingService.GetPopulation`. Timberborn forbids
`GetComponents<BaseComponent>` at runtime. Breeding detection now enumerates the supported
`BaseComponent.AllComponents` collection, avoiding that exception without changing housing rules.
The new compiled-adapter check rejects the Preview 3 DLL with the reported exception and accepts
the fixed DLL against the installed game's real component blacklist. Full Unity gameplay remains untested.

## Installation

1. Close Timberborn. Extract `IncrementalHousing-preview4.zip` into `Documents/Timberborn/Mods`.
   The ZIP contains an `IncrementalHousing-Preview` folder.
2. Launch Timberborn and enable **Incremental Housing - Preview 0.3.1** in the mod manager.
3. Restart the game, then load a copy of your save.
4. Processing starts at the next daytime-start event. A saved queue resumes immediately on load.

No additional mod dependencies. For multiplayer, install the identical package on both computers.

## Commute objective

The score is Timberborn's **zipline-aware road-route cost**, returned by `Accessible.FindRoadPath`.
It follows the real road navigation graph, including zipline speed weighting. It is not straight-line
separation or literal meters walked, and does not model congestion or the full day's travel time.

Every direct move must save more than **0.5 route-cost units**. A swap must improve the initiating
beaver's commute by more than 0.5 and save more than 0.5 across both beavers combined. Saving
12 while costing the partner 4 is accepted; saving 12 while costing 13 is rejected.

The planner considers all existing eligible homes in the same district, including partially occupied
homes with a spare bed. It also considers adult swaps when a direct move is possible, since the swap
may save more overall or preserve a breeding pair. It chooses the largest combined saving. Ties prefer
additional breeding pairs, then the actor's shorter route, then a direct move, then stable entity IDs.
Coworkers exchanging homes cannot save combined commute cost and are skipped before path queries.

Only housed, employed adults initiate searches. Unemployed adults can be swap partners with zero
assigned-workplace commute cost. Children and homeless beavers remain with vanilla housing.
Unreachable routes, invalid costs, disabled/paused/blocked homes or workplaces, and cross-district
assignments are excluded. Already minimal commutes skip the district scan.

## Breeding safeguards

For breeding houses, direct moves cannot reduce the combined adult-pair count or remaining newborn
capacity of the affected homes. Splitting the last pair into two singletons is rejected. Adult swaps
preserve each home's adult and child counts. Children are never moved by this mod.

After a proposed direct move, reserve this many empty beds:
`max(0, min(child slots, floor(adults / 2)) - children)`.
Existing children already occupy those slots. Child-only homes have no pairs to reserve for;
occupancy is recalculated when an adult arrives. Non-breeding houses may use any unoccupied bed.

**Changed from Preview 2:** forming an extra pair no longer overrides commute savings. Separated
survivors may reunite when doing so also shortens an employed adult's commute. This optimizer does
not force a longer commute or move unemployed actors to repair an already fragmented population.
Pair and slot safeguards are structural; they do not guarantee fertility, births or population recovery.

## Gradual work and performance

- At daytime start, queue district beavers in stable ID order. Unfinished work keeps priority.
- Spend **16 work steps per simulation tick**, with at most one completed actor per tick. A step
  admits/skips an actor, examines one home, or examines one adult swap candidate. Children and
  other ineligible actors share the budget instead of each consuming a whole tick.
- At most 64 planner distance lookups per tick. Repeated queries within a tick are memoized.
- Share successful single-access home/work routes across ticks and workers, up to **8,192 entries**.
  Keys include both entity IDs and exact access coordinates. Endpoint validity, blocking and
  building usability are checked before cross-tick reuse. Committed navigation updates clear the
  cache, including road and zipline changes. Daytime start and loading also clear it. Capacity
  overflow clears the cache; failed routes are not cached across ticks. Multi-access workplaces
  use the native query with tick-local memoization.
- Cache warmth never changes work-step counts or candidate order. No elapsed-time budgets,
  random ordering, background Unity access or placement-preview navigation are used.
- Queue, search snapshots, cursors and best candidate are saved. Old saved candidates are
  rechecked under the current rules before applying. Changed jobs, homes or districts cancel
  unfinished searches. Occupancy, reachability and savings are checked again at commit.
- Existing residents keep their homes during planning. Swaps free both beds and assign both
  adults synchronously in one tick; there is no daily eviction and reassignment pass.

This is a local optimizer, not a guaranteed global optimum. Dense colonies may take multiple days
for a queue pass. Daily population sorting, district-home snapshots, resident snapshots and cold
native path queries are not individually time-sliced. Vanilla housing may subsequently move residents.
Three-way cycles, future jobs, mobile workers' actual work sites, non-work travel and happiness are
outside the objective. No FPS or milliseconds improvement is claimed without live profiling.

## Validation

Release build against installed game assemblies: zero warnings and errors. **59 checks (58 planner checks plus one compiled-adapter API check)**
cover harmful/beneficial moves and swaps, breeding safeguards, stale saved plans, route changes,
queue fairness, deterministic ties, save/load and cache behavior. Randomized mirrored peers cover
40 non-breeding layouts across three daily passes, plus 40 breeding layouts. Tests assert identical
states/decisions, capacity limits and non-increasing combined commute cost.

A synthetic shared-workplace fixture made 3,340 planner distance lookups but only 82 underlying
route-query stand-ins with the shared cache. This demonstrates duplicate suppression, not an
in-game speedup measurement. Warm/cold cache tests preserve exact decisions and commit ticks.

The tests use a fake world. The adapter compiles against real game APIs, and navigation/accessibility
invalidation was checked against the installed game code. Native Unity execution and a live
BeaverBuddies two-player session have **not** been exercised. This remains a playtest preview.

For live testing, change roads/ziplines, pause a workplace, save/rehost during a scan, and compare
both peers' home assignments. Profile tick spikes and full-queue completion on the same colony.

## Build and tests

Requires .NET SDK 8 and a local Timberborn installation. No NuGet packages required.

```powershell
dotnet build IncrementalHousing/IncrementalHousing.csproj -c Release -p:GameManaged="C:\path\Timberborn_Data\Managed"
dotnet run --project IncrementalHousing.Tests -c Release -- IncrementalHousing/bin/Release/netstandard2.1/IncrementalHousing.dll "C:\path\Timberborn_Data\Managed"
./package.ps1
```

The tests' Newtonsoft.Json reference defaults to the standard Steam installation; adjust the test
project HintPath on another machine. No game DLLs are distributed. The repository packaging script
writes to `dist`; existing archive names must be moved aside before packaging again.

To uninstall, disable this mod and restart. The saved queue can remain in the save; beavers retain
normal game home assignments. Keep an original save copy to undo moves already made.

