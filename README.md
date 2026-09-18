# Incremental Housing — Preview 5

[Download Preview 5](https://github.com/kramsey458/IncrementalHousing/releases/tag/v0.4.0-preview.5)

Standalone Timberborn 1.1 mod, built against **1.1.2.4**. Version **0.4.0**.
Gradually improves home-to-assigned-workplace commutes while protecting breeding capacity.

## Installation

1. Close Timberborn. Extract `IncrementalHousing-preview5.zip` into `Documents/Timberborn/Mods`.
   The ZIP contains an `IncrementalHousing-Preview` folder. Existing users can overwrite that folder.
2. Launch Timberborn, enable **Incremental Housing - Preview 0.4.0**, and restart.
3. Load a copy of your save. New queues start at daytime start; saved queues resume on load.

Standalone: no additional mod dependencies. Both multiplayer players must install the identical package.

## What changed in Preview 5

- **Rechecked alternatives:** retain the 16 highest-scoring candidates, then score them again over
  budgeted steps before selecting a winner. A stale winner can fall back to a better valid candidate.
  Detected changes during validation restart that validation. The selected action is checked once more
  immediately before applying it. This does not guarantee the best current candidate outside the shortlist.
- **Less repeated work:** share sorted district-home snapshots and workplace minimum-route information.
  An actor already at a surveyed workplace minimum skips the full house scan. No-improvement searches
  back off for up to three daytime passes, unless their assignment changes or a tracked world change
  invalidates them. Daily passes refresh shared home, resident-group and minimum-route data.
- **Equivalent partners grouped:** build resident groups incrementally, one resident per work step,
  keeping the lowest eligible entity ID for each workplace. Reuse completed groups between searches.
  Coworkers offer no net improvement in a two-person swap. Group construction still has a cold-start cost;
  the gain comes from reuse, not moving an unbounded group scan into a single tick.
- **Combined swap threshold:** a swap may qualify when each worker saves less than 0.5 individually,
  provided the initiating worker improves and combined savings exceed 0.5. Direct moves still need more
  than 0.5 savings. A zero-cost initiating commute skips the scan.
- **Incremental cache eviction:** the 8,192-entry route cache uses FIFO eviction, removing one old entry
  per new insertion at capacity. Updating an entry does not duplicate its eviction record.
- **Bounded rotations and vacancy chains:** if simple moves/swaps produce no valid candidate, try
  two-adult vacancy chains and three-adult rotations. Retain at most eight stable seed partners and spend
  at most 256 extended exploration steps per actor, followed by bounded shortlist validation. The home
  starting offset rotates between searches. This is a limited escape from pairwise local optima, not an
  exhaustive colony-wide assignment solver. A cold group too large for this budget can limit coverage.

The test runner also now catches failures, prints diagnostics and exits with a normal nonzero code.
It no longer leaves ordinary failed assertions as unhandled exceptions for Windows crash reporting.

## Route scoring and breeding rules

Use Timberborn's **zipline-aware road-route cost**, returned by `Accessible.FindRoadPath`. This follows
real navigation routes and includes zipline speed weighting; it is not straight-line distance, literal
meters walked, congestion or a full-day travel-time model. There is no straight-line fallback.

All actions must reduce combined assigned-workplace commute cost by more than **0.5**. The initiating
adult must improve. Other participating adults may lose some convenience if the combined gain remains
positive. A chain can move an otherwise stationary partner into a vacancy to free the actor's destination.

Only housed, employed adults initiate searches. Unemployed adults can be partners at zero assigned-work
cost. Children never move. Unreachable routes, invalid distances, unusable buildings and cross-district
assignments are excluded. Disabled/paused workplaces retain the previous behavior of being excluded
from the commute objective; temporarily inactive jobs are not modeled as future commutes.

For breeding houses, reserve `max(0, min(child slots, floor(adults / 2)) - children)` empty beds after
an incoming adult. Existing children already occupy those slots. Child-only houses have no pairs to
reserve for. Non-breeding houses may use any free bed.

Direct moves and vacancy chains cannot reduce combined adult-pair count or remaining newborn capacity
at their source and final destination. A chain's intermediate home retains its adult count. Swaps and
three-way rotations preserve each affected home's adult and child counts. Forming pairs cannot override
the commute-saving requirement. These are structural safeguards, not a population-recovery guarantee.

Existing residents keep their homes during planning. Applying a swap/rotation/chain frees the involved
adult beds and assigns participants synchronously within one simulation tick. There is no daily eviction.

## Scheduling, invalidation and multiplayer

- **16 work steps per simulation tick**, at most one completed actor per tick. Steps include actor
  admission, home exploration, resident-group preparation, partner evaluation and shortlist validation.
  Larger searches continue across ticks. Three-person scoring raises the maximum planner distance
  lookups to **96 per tick**; most steps need fewer. One lookup can visit multiple workplace access points.
- Pending queue work retains priority at daytime start. Entity IDs provide stable ordering and ties.
  No random, frame-time, elapsed-time or cache-warmth decision rules are used.
- Completed scans, house snapshots, groups, minimum-route bounds, shortlists, phase/cursors, pending
  invalidation, fallback days and extension offsets are saved. Schema 1 saves migrate to schema 2;
  the old active actor is requeued without changing its home. Do not downgrade a save written by Preview 5.
- Live road/zipline navigation updates, observed job changes, home changes, migration, deaths and dwelling
  population changes invalidate shared planner data for the next tick. The current scan retains its
  snapshots and uses end-of-scan revalidation. Detected changes reset resting-search suppression.
  A drained queue is reconsidered at the next daytime pass; changes do not run an unlimited immediate scan.
- Event-observer membership is saved in sorted ID sets and restored after scene loading, so a joining peer
  tracks the same known entities. Newly evaluated actors/homes are subscribed as they are discovered.
  Periodic fallback covers changes not yet observed and building eligibility changes without a road update.
- Successful single-access routes are shared across workers. Cross-tick reuse validates endpoint existence,
  usability, blocking and exact coordinates. Committed navigation changes, daytime start and load clear
  route data. Multi-access workplaces use the native minimum-over-accesses query with tick-local caching.
  Failed routes are not retained across ticks. Route-cache warmth does not control planner step counts.

Snapshots/sorting, daily queue construction, initial observer registration and a cold native path query
are not individually time-sliced. Group preparation is time-sliced after its resident-ID snapshot.
Any tracked change currently invalidates shared planner data globally; busy colonies can reduce reuse.
The fixed step budget controls work quantity, not a hard milliseconds or FPS guarantee.

## Validation and measured examples

**84 checks passed**, including the compiled-adapter check against the installed game's real component
blacklist. Release build: zero warnings/errors. The Preview 4 `AllComponents` crash fix is retained.
Tests cover direct/swapped/chained/rotated moves, reserved beds, invalid routes, changing participants,
old-save migration, cache eviction, periodic fallback and save/load at intermediate search boundaries.
Mirrored random worlds test deterministic states/actions and non-increasing total commute costs.

Synthetic audit results:

| Fixture | Preview 4 | Preview 5 |
|---|---:|---:|
| Stable 200-worker, 80-home district: planner ticks | 1,200 | 19 |
| Same district: home snapshots | 200 | 1 |
| Same district: distance lookups | 31,800 | 358 |
| Stale winner: final commute when alternative costs 5 | 19 or 20 | 5 |
| Two workers each able to save 0.4: combined cost | 20 | 19.2 |
| Three-worker rotation fixture: combined cost | 60 | 30 |
| Useful old entries retained after cache overflow | 0 of 8,191 | 8,191 of 8,191 |
| 100 six-worker synthetic layouts: above exact global optimum | 31 | 9 |

The six-worker comparison enumerates all 720 assignments per layout. Mean gap across those layouts
fell from 1.97% to 0.28%; worst gap fell from 19.48% to 12.35%. These small generated fixtures are not
representative gameplay benchmarks. Native Unity path time, FPS and live two-player behavior remain
**untested**. Static inspection of lifecycle APIs does not replace a live rehost/join test.

For live testing, use a copy of a save; exercise job changes, births, roads/ziplines, vacancy chains,
full-home rotations and joining/rehosting during a scan. Compare both peers' assignments and logs.
Vanilla housing can subsequently move residents. Longer cycles, future jobs, mobile work sites,
non-work travel and happiness remain outside the objective.

## Build and test

Requires .NET SDK 8 and a local Timberborn installation. No NuGet packages or distributed game DLLs.

```powershell
dotnet build IncrementalHousing/IncrementalHousing.csproj -c Release -p:GameManaged="C:\path\Timberborn_Data\Managed"
dotnet run --project IncrementalHousing.Tests -c Release -- IncrementalHousing/bin/Release/netstandard2.1/IncrementalHousing.dll "C:\path\Timberborn_Data\Managed"
./package.ps1
```

The tests' Newtonsoft.Json HintPath defaults to the standard Steam installation; adjust it if needed.
Omitting the two test arguments runs the 83 planner/cache checks without the compiled-adapter check.
Packaging writes installer/source ZIPs and notes/checksums to `dist`; move existing archives aside before
repackaging. The test executable is not included in the install ZIP.

To uninstall, disable this mod and restart. Beavers retain normal game home assignments. Keep an original
save copy to undo moves already made or to return to an older mod version.

