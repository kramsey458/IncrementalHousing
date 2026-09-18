# Incremental Housing — Preview 6

Standalone Timberborn 1.1 mod, built against **1.1.2.4**. Version **0.5.0**.
Gradually improves home-to-assigned-workplace commutes while protecting breeding capacity.

## Installation

1. Close Timberborn. Extract `IncrementalHousing-preview6.zip` into `Documents/Timberborn/Mods`.
   The ZIP contains an `IncrementalHousing-Preview` folder. Existing users can overwrite that folder.
2. Launch Timberborn, enable **Incremental Housing - Preview 0.5.0**, and restart.
3. Load a copy of your save. New daily sweeps start at daytime start; saved work resumes on load.
   Relevant live changes can also queue work before the next morning.

Standalone: no additional mod dependencies. Both multiplayer players must install the identical package.

**Save compatibility:** schemas 1 and 2 migrate to schema 3. Pending actors are retained; an older
unfinished search is requeued and its obsolete caches are discarded. Keep an original save copy.
Do not downgrade a save written by Preview 6.

## Changes in Preview 6

- **Fairer extended search.** Retain all eligible seed partners, in deterministic order, rather than
  only the first eight IDs. A saved cursor tracks the home, seed and last evaluated resident across
  search passes. Visit seed partners for each home before advancing to the next home. Large resident
  groups are prepared incrementally and retained, so they can finish across multiple search budgets.
  The limit remains **256 extended exploration steps per search**, followed by bounded validation.
- **Targeted invalidation.** Household and job events invalidate resident groups for the affected
  homes and resting/validation decisions for their district. Unrelated districts, home lists and
  route minima remain reusable. Building additions/removals invalidate that district's home list.
  Navigation updates still conservatively invalidate planner/route data globally.
- **Same-day reconsideration.** Changed workers enter a deduplicated priority queue. Affected
  districts and navigation changes trigger incremental rechecks, including after the daily queue
  has drained. Priority admissions alternate with regular work, and district/global/daily enumeration
  rotates so recurring events do not monopolize queue preparation.
- **Assigned jobs remain assigned.** Paused or automated-off workplaces are no longer converted
  into unemployment for commute scoring. When their access remains valid, the assigned route counts.
  A disabled or cross-district assigned job excludes its worker from a plan instead of scoring it
  as an unemployed zero-cost partner. Physically blocked/unreachable destinations still need a valid
  route before any move can be accepted.
- **Disconnected commute recovery.** A housed adult with a usable home but no route to its assigned
  workplace can move to reachable housing. Recovery may also use a safe swap, chain or rotation,
  provided every other employed participant has valid before/after routes and none gets a longer
  commute. Invalid numeric scores are rejected; they are not treated as disconnected routes.
- **Incremental preparation.** Saved ordered catalogs replace daily full population enumeration
  and sorting. Daily queue construction, home-list preparation and resident-group preparation consume
  work steps. Deleted people/homes are removed from the catalogs and event subscriptions. Catalog
  traversal uses binary search: Timberborn's Mono implementation of sorted-set range views performs
  a hidden full-range count, so it is deliberately avoided.

Diagnostics remain small: existing move/swap/chain/rotation counters plus a recovery count in saved
state. There is no telemetry service, per-tick logging or new diagnostic UI. The test runner catches
failures, reports them and exits normally; no test executable is included in the install ZIP.

## Commute and breeding rules

Use Timberborn's **zipline-aware road-route cost**, returned by `Accessible.FindRoadPath`.
This follows real navigation routes and includes zipline speed weighting. It is not straight-line
distance, literal meters walked, congestion or a full-day travel-time model.

Ordinary moves must improve the initiating adult's commute and reduce combined cost by more than
**0.5**. Other participants may lose convenience if the total saving exceeds that threshold.
Disconnected-route recovery is a separate objective: restoring the actor's route takes priority,
with no worsening permitted for the other participants. Recovery candidates are scored again before
commit; a route that returns during planning is evaluated under the ordinary saving rule.

Only housed adults with assigned workplaces initiate searches. Truly unemployed adults can be
partners at zero assigned-work cost. Children never move. Homes must be usable and in the same district.
The mod does not evacuate blocked homes or house homeless beavers.

For breeding houses, reserve `max(0, min(child slots, floor(adults / 2)) - children)` empty beds
after an incoming adult. Children already occupy their slots; child-only homes have no pairs to
reserve for. Direct moves and vacancy chains cannot reduce combined adult-pair count or remaining
newborn capacity at their source and final destination. Swaps and rotations preserve each home's
adult and child counts. These are structural safeguards, not a population-recovery guarantee.

Residents keep their homes while planning. The selected move, swap or rotation applies synchronously
within one simulation tick. Candidates are revalidated, including capacity, participants and routes.

## Scheduling and multiplayer

- **16 work steps per simulation tick**, at most one completed actor per tick. This includes queue
  enumeration, actor admission, home/resident preparation, candidate scoring and revalidation.
  Three-person scoring uses at most six planner distance lookups per step, **96 per tick**.
- Queue membership, preparation progress, extended-search cursors, priority requests and catalog
  memberships are saved. Canonically ordered maps ensure identical serialized state after reload.
  No frame-time, stopwatch or route-cache-warmth rule controls planning decisions.
- Failed searches rest until three daytime passes after their evaluation, unless relevant changes
  invalidate them. Workplace route minima expire by day; completed resident groups refresh after
  more than three days. Unfinished group preparation survives that refresh interval.
- Existing pending work is retained across mornings. A changed active assignment cancels the old
  search and requeues that worker. A finite shortlist cannot guarantee the best currently available
  alternative outside the retained 16 candidates.
- The 8,192-entry successful-route cache evicts one entry at a time. Endpoint identity, coordinates
  and blocking are checked. Navigation updates, daytime start and load clear route data.
  Multi-access workplaces use the game's native query with tick-local caching.
- Observer subscriptions restore after scene initialization, before simulation resumes. New people
  and committed housing changes update the catalogs through game lifecycle events.

The budget limits operations, **not milliseconds**. Initial catalog construction/subscription restore
happens during loading; a newly finished district may also need initial registration. Ordered catalog
membership changes can shift ID arrays. Collection growth, save serialization and a cold native path
query are not hard time-bounded. Event activity and larger populations can still increase total work.

This remains a local optimizer. Extended searches can take multiple daily passes; changing colonies,
longer cycles, mobile work sites and non-work travel can prevent or fall outside the best assignment.

## Validation

**109 checks passed**, including the compiled adapter against the installed game's component blacklist
and its use of the assigned-workplace API. Release build: zero warnings/errors.

Coverage includes ordinary and recovery moves, breeding/child-space safeguards, unavailable jobs,
same-day queue wakeups, scoped invalidation, queue fairness, incremental preparation, large resident
groups, late seed coverage, old-save migration and exact save/reload state/action replay.

A controlled fixture with eight unhelpful early seeds and a useful ninth seed now finds the rotation
within four daily passes, reducing combined route cost **220 to 190**. This verifies improved search
coverage; it is not an in-game FPS measurement or a guarantee for every colony.
The stable 200-worker/80-home fixture makes **358 planner distance lookups**. Home preparation is now
spread across ticks, so that fixture takes 24 planner ticks instead of Preview 5's 19.

Native Unity gameplay and live two-player join/rehost testing remain **untested**. Use a copy of a save
to test job changes, births/deaths, housing construction, paused jobs, disconnected roads/ziplines,
recovery moves and joining during a partially completed scan.

## Build and test

Requires .NET SDK 8 and a local Timberborn installation. No NuGet packages or distributed game DLLs.

```powershell
dotnet build IncrementalHousing/IncrementalHousing.csproj -c Release -p:GameManaged="C:\path\Timberborn_Data\Managed"
dotnet run --project IncrementalHousing.Tests -c Release -- IncrementalHousing/bin/Release/netstandard2.1/IncrementalHousing.dll "C:\path\Timberborn_Data\Managed"
./package.ps1
```

The tests' Newtonsoft.Json HintPath defaults to the standard Steam installation; adjust it if needed.
Omitting the two test arguments runs 108 planner/catalog/cache checks without the compiled-adapter check.
Packaging writes installer/source ZIPs and notes/checksums to `dist`; move existing archives aside
before repackaging.

To uninstall, disable this mod and restart. Beavers retain normal game home assignments. Keep an
original save copy to undo moves already made or to return to an older mod version.
