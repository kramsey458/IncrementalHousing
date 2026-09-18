using System;
using System.Collections.Generic;

namespace IncrementalHousing;

public sealed class Person { public Guid Id, Home, Work, District; public bool Adult, WorkUnavailable; }
public interface IHousingWorld
{
    Person GetPerson(Guid id);
    bool NextPerson(Guid district, Guid after, out Guid person);
    bool NextHome(Guid district, Guid after, out Guid home);
    bool NextAdult(Guid home, Guid after, out Guid adult);
    bool UsableHome(Guid home, Guid district);
    bool HasAdultVacancy(Guid home);
    HousingPopulation GetPopulation(Guid home);
    bool TryDistance(Guid home, Guid work, out float distance);
    void Move(Guid person, Guid home);
    void Swap(Guid person, Guid other);
    void Transfer(Guid actor, Guid target, Guid partner, Guid lastHome, Guid third);
}
public sealed class HousingPopulation
{
    public int Adults, Children, Capacity, ChildSlots;
    public bool Breeding;
    public int Pairs => Breeding ? Adults / 2 : 0;
    public int BirthSlots => Breeding ? Math.Max(0, Math.Min(ChildSlots, Pairs) - Children) : 0;
    public int Free => Capacity - Adults - Children;
    public HousingPopulation WithAdults(int adults) => new HousingPopulation {
        Adults = adults, Children = Children, Capacity = Capacity, ChildSlots = ChildSlots, Breeding = Breeding };
}
public sealed class Plan
{
    public Guid Home, Partner, LastHome, Third;
    public float Gain, NewDistance;
    public int PairGain;
    public bool Recovery;
}
public sealed class ResidentGroups
{
    public Guid After;
    public int Day;
    public SortedDictionary<Guid, Guid> ByWork = new SortedDictionary<Guid, Guid>();
    public List<Guid> Preparing = new List<Guid>();
    public List<Guid> Representatives;
}
public sealed class RestingSearch { public Person Actor; public long Revision; public int UntilDay; }
public sealed class WorkplaceMinimum
{
    public Guid District, Work;
    public float Cost;
    public int Day;
    public long HomeVersion;
}
public sealed class Search
{
    public Person Actor;
    public List<Guid> Homes = new List<Guid>();
    public bool PreparingHomes;
    public Guid HomeAfter;
    public int HomeIndex;
    public Guid Target;
    public List<Guid> Residents = new List<Guid>();
    public int ResidentIndex;
    public Plan Best;
    public List<Plan> Candidates = new List<Plan>();
    public List<Plan> Seeds = new List<Plan>();
    public Guid BuildingHome;
    public long Revision, ValidationRevision;
    public float Minimum;
    // 0 simple scan, 1 simple validation, 2 extended scan, 3 extended validation.
    public int Phase, ValidateIndex;
    public Plan ValidatedBest;
    public int SeedIndex, ExtensionHomeIndex, ExtensionSteps, ExtensionVisits;
    public Guid ExtensionHome, ExtensionAfterResident;
    public List<Guid> ExtensionResidents = new List<Guid>();
    public int ExtensionResidentIndex;
    public bool ExtensionSeedStarted;
}
public sealed class ExtensionCursor
{
    public int Seed;
    public Guid Home, Resident;
    public bool SeedStarted;
}
public sealed class OptimizerState
{
    public int Schema = 3;
    public List<Guid> Queue = new List<Guid>();
    public int Next;
    public Search Active;
    public long Evaluated, Moves, Swaps, Chains, Rotations, Recoveries, Revision;
    public int Day, Admissions, FeedTurn;
    public bool Dirty, DailyScan, RecheckAll;
    public Guid DailyAfter, RecheckAfter, AfterDistrict;
    public SortedSet<Guid> Queued = new SortedSet<Guid>();
    public SortedSet<Guid> Priority = new SortedSet<Guid>();
    public SortedDictionary<Guid, Guid> RecheckDistricts = new SortedDictionary<Guid, Guid>();
    public OrderedIds RecheckDistrictIds = new OrderedIds();
    public SortedDictionary<Guid, long> DistrictRevisions = new SortedDictionary<Guid, long>();
    public SortedDictionary<Guid, ExtensionCursor> Extensions = new SortedDictionary<Guid, ExtensionCursor>();
    public HousingCatalog Catalog = new HousingCatalog();
    public bool WatchersInitialized;
    public SortedSet<Guid> ObservedPeople = new SortedSet<Guid>();
    public SortedSet<Guid> ObservedHomes = new SortedSet<Guid>();
    public SortedDictionary<Guid, List<Guid>> Homes = new SortedDictionary<Guid, List<Guid>>();
    public SortedDictionary<Guid, ResidentGroups> Groups = new SortedDictionary<Guid, ResidentGroups>();
    public SortedDictionary<Guid, WorkplaceMinimum> Minima = new SortedDictionary<Guid, WorkplaceMinimum>();
    public SortedDictionary<Guid, RestingSearch> Resting = new SortedDictionary<Guid, RestingSearch>();
}
public sealed class Optimizer
{
    public const int StepsPerTick = 16, CandidatesKept = 16, ExtensionBudget = 256;
    public const float MinimumSaving = 0.5f;
    public OptimizerState State { get; }
    private readonly IHousingWorld _world;
    public Optimizer(IHousingWorld world, OptimizerState state = null)
    {
        _world = world; State = state ?? new OptimizerState();
        if (State.Schema < 1 || State.Schema > 3 || State.Queue == null || State.Next < 0 || State.Next > State.Queue.Count)
            throw new InvalidOperationException("Unsupported or invalid IncrementalHousing save state.");
        if (State.Schema < 3)
        {
            if (State.Active?.Actor != null) State.Queue.Insert(State.Next, State.Active.Actor.Id);
            State.Active = null; State.Schema = 3;
            State.Homes.Clear(); State.Groups.Clear(); State.Minima.Clear(); State.Resting.Clear();
            State.WatchersInitialized = false;
            for (int i = State.Next; i < State.Queue.Count; i++) State.Queued.Add(State.Queue[i]);
        }
    }
    public void MarkWorldChanged()
    {
        State.Dirty = true;
        if (!State.RecheckAll) { State.RecheckAll = true; State.RecheckAfter = Guid.Empty; }
    }
    public void MarkHousingChanged(Guid district, Guid home, Guid person = default)
    {
        if (home != Guid.Empty) State.Groups.Remove(home);
        if (district != Guid.Empty)
        {
            State.DistrictRevisions.TryGetValue(district, out var version);
            State.DistrictRevisions[district] = version + 1;
            if (!State.RecheckDistricts.ContainsKey(district)) { State.RecheckDistricts[district] = Guid.Empty; State.RecheckDistrictIds.Add(district); }
        }
        if (person != Guid.Empty) Enqueue(person, true);
    }
    public void MarkHomeListChanged(Guid district, Guid home)
    {
        State.Homes.Remove(district);
        State.Catalog.HomeVersions.TryGetValue(district, out var version);
        State.Catalog.HomeVersions[district] = version + 1;
        MarkHousingChanged(district, home);
    }
    private long Version(Guid district)
    { State.DistrictRevisions.TryGetValue(district, out var version); return State.Revision + version; }
    private long HomeVersion(Guid district)
    { State.Catalog.HomeVersions.TryGetValue(district, out var version); return version; }
    public void ForgetPerson(Guid id)
    {
        State.Priority.Remove(id); State.Queued.Remove(id); State.Resting.Remove(id); State.Extensions.Remove(id);
    }
    private void Invalidate()
    {
        State.Revision++;
        State.Homes.Clear(); State.Groups.Clear(); State.Minima.Clear(); State.Resting.Clear();
        State.Dirty = false;
    }
    public void StartDay()
    {
        State.Day++;
        if (!State.DailyScan) { State.DailyScan = true; State.DailyAfter = Guid.Empty; }
    }
    public void Enqueue(Guid id, bool urgent = false)
    {
        if (id == Guid.Empty) return;
        if (urgent) { State.Priority.Add(id); State.Resting.Remove(id); }
        else if (State.Active?.Actor.Id != id && !State.Priority.Contains(id) && State.Queued.Add(id))
        {
            if (State.Next == State.Queue.Count) { State.Queue.Clear(); State.Next = 0; }
            State.Queue.Add(id);
        }
    }
    public bool HasPendingWork => State.Active != null || State.Next < State.Queue.Count || State.Priority.Count > 0 ||
        State.DailyScan || State.RecheckAll || State.RecheckDistricts.Count > 0;
    // Enumeration costs a work step, including exhausted cursors and stale queue entries.
    private bool FeedOne()
    {
        // Rotate scan classes and districts so a busy district cannot starve daily work.
        for (int attempt = 0; attempt < 3; attempt++)
        {
            int kind = State.FeedTurn; State.FeedTurn = (kind + 1) % 3;
            if (kind == 0 && State.RecheckAll)
            {
                if (_world.NextPerson(Guid.Empty, State.RecheckAfter, out var id))
                { State.RecheckAfter = id; Enqueue(id, true); }
                else State.RecheckAll = false;
                return true;
            }
            if (kind == 1 && State.RecheckDistrictIds.Count > 0)
            {
                if (!HousingCatalog.Next(State.RecheckDistrictIds, State.AfterDistrict, out var district))
                    district = State.RecheckDistrictIds[0];
                State.AfterDistrict = district;
                if (_world.NextPerson(district, State.RecheckDistricts[district], out var id))
                { State.RecheckDistricts[district] = id; Enqueue(id, true); }
                else { State.RecheckDistricts.Remove(district); State.RecheckDistrictIds.Remove(district); }
                return true;
            }
            if (kind == 2 && State.DailyScan)
            {
                if (_world.NextPerson(Guid.Empty, State.DailyAfter, out var id))
                { State.DailyAfter = id; Enqueue(id); }
                else State.DailyScan = false;
                return true;
            }
        }
        return false;
    }
    private bool TakeOne(out Guid id)
    {
        id = Guid.Empty;
        bool regular = State.Next < State.Queue.Count;
        if (State.Priority.Count > 0 && (!regular || (State.Admissions = (State.Admissions + 1) % 4) != 0))
        { id = State.Priority.Min; State.Priority.Remove(id); State.Queued.Remove(id); return true; }
        if (!regular) return false;
        var next = State.Queue[State.Next++];
        if (State.Queued.Remove(next)) { id = next; State.Priority.Remove(next); }
        return true;
    }
    public void Tick()
    {
        if (State.Dirty) Invalidate();
        if (State.Active != null && !SameAssignment(_world.GetPerson(State.Active.Actor.Id), State.Active.Actor))
        { Enqueue(State.Active.Actor.Id, true); State.Active = null; return; }
        for (int step = 0; step < StepsPerTick; step++)
        {
            if (step % 4 == 0 && FeedOne()) continue;
            if (State.Active == null)
            {
                if (!TakeOne(out var id)) { if (FeedOne()) continue; return; }
                if (id == Guid.Empty) continue;
                var actor = _world.GetPerson(id); State.Evaluated++;
                if (actor == null || !actor.Adult || actor.Home == Guid.Empty || actor.Work == Guid.Empty || actor.WorkUnavailable ||
                    !_world.UsableHome(actor.Home, actor.District)) continue;
                if (State.Resting.TryGetValue(actor.Id, out var rest) && rest.Revision == Version(actor.District) &&
                    State.Day < rest.UntilDay && SameAssignment(actor, rest.Actor)) continue;
                var status = ReadDistance(actor.Home, actor.Work, out var cost);
                if (status == RouteStatus.Invalid || (status == RouteStatus.Reachable && cost <= 0)) continue;
                if (status == RouteStatus.Reachable && State.Minima.TryGetValue(actor.Work, out var minimum) && minimum.Day == State.Day &&
                    minimum.District == actor.District && minimum.HomeVersion == HomeVersion(actor.District) && cost <= minimum.Cost) continue;
                if (!State.Homes.TryGetValue(actor.District, out var homes)) homes = new List<Guid>();
                State.Active = new Search { Actor = actor, Homes = homes, PreparingHomes = !State.Homes.ContainsKey(actor.District),
                    Revision = Version(actor.District), Minimum = status == RouteStatus.Reachable ? cost : float.MaxValue };
                continue;
            }
            var s = State.Active;
            if (s.PreparingHomes)
            {
                if (_world.NextHome(s.Actor.District, s.HomeAfter, out var id)) { s.HomeAfter = id; s.Homes.Add(id); }
                else { s.PreparingHomes = false; if (s.Revision == Version(s.Actor.District)) State.Homes[s.Actor.District] = s.Homes; }
                continue;
            }
            if (s.BuildingHome != Guid.Empty)
            {
                var group = Group(s.BuildingHome);
                BuildGroupStep(group, s.BuildingHome, s.Actor.District);
                if (group.Representatives != null)
                {
                    if (s.Phase == 2) { s.ExtensionResidents = group.Representatives; PositionExtensionResident(s); }
                    else s.Residents = group.Representatives;
                    s.BuildingHome = Guid.Empty;
                }
                if (s.Phase == 2 && ++s.ExtensionSteps >= ExtensionBudget) EndExtension(s);
                continue;
            }
            if (s.Phase == 1 || s.Phase == 3)
            {
                if (s.ValidationRevision != Version(s.Actor.District))
                { s.ValidationRevision = Version(s.Actor.District); s.ValidateIndex = 0; s.ValidatedBest = null; }
                if (s.ValidateIndex < s.Candidates.Count)
                {
                    var plan = Score(s.Actor, s.Candidates[s.ValidateIndex++]);
                    if (plan != null && (s.ValidatedBest == null || Better(plan, s.ValidatedBest))) s.ValidatedBest = plan;
                    continue;
                }
                if (s.ValidatedBest != null)
                {
                    var plan = Score(s.Actor, s.ValidatedBest);
                    if (plan != null) Apply(s.Actor, plan);
                    State.Active = null; return;
                }
                if (s.Phase == 1 && s.Seeds.Count > 0)
                {
                    s.Phase = 2; s.Candidates.Clear(); s.Best = null; ResumeExtension(s); continue;
                }
                if (s.Revision == Version(s.Actor.District))
                    State.Resting[s.Actor.Id] = new RestingSearch { Actor = s.Actor, Revision = Version(s.Actor.District), UntilDay = State.Day + 3 };
                State.Active = null; return;
            }
            if (s.Phase == 2) { ExtendStep(s); continue; }
            if (s.ResidentIndex < s.Residents.Count)
            {
                var id = s.Residents[s.ResidentIndex++];
                var partner = _world.GetPerson(id);
                if (EligiblePartner(partner, s.Actor, s.Target) && partner.Work != s.Actor.Work)
                {
                    // All seeds, in stable home/representative order; execution remains budgeted.
                    s.Seeds.Add(new Plan { Home = s.Target, Partner = id });
                    Consider(s, Score(s.Actor, new Plan { Home = s.Target, Partner = id }));
                }
                continue;
            }
            if (s.HomeIndex >= s.Homes.Count)
            {
                if (s.Revision == Version(s.Actor.District))
                    State.Minima[s.Actor.Work] = new WorkplaceMinimum { District = s.Actor.District, Work = s.Actor.Work,
                        Cost = s.Minimum, Day = State.Day, HomeVersion = HomeVersion(s.Actor.District) };
                BeginValidation(s, 1); continue;
            }
            var target = s.Homes[s.HomeIndex++];
            if (target == s.Actor.Home || !_world.UsableHome(target, s.Actor.District) ||
                !Distance(target, s.Actor.Work, out var after)) continue;
            s.Minimum = Math.Min(s.Minimum, after);
            var beforeStatus = ReadDistance(s.Actor.Home, s.Actor.Work, out var before);
            if (beforeStatus == RouteStatus.Invalid || (beforeStatus == RouteStatus.Reachable && before <= after)) continue;
            s.Target = target; s.Residents = new List<Guid>(); s.ResidentIndex = 0;
            if (_world.HasAdultVacancy(target)) Consider(s, Score(s.Actor, new Plan { Home = target }));
            UseGroups(s, target, false);
        }
    }
    private ResidentGroups Group(Guid home)
    {
        if (!State.Groups.TryGetValue(home, out var group) || (group.Representatives != null && State.Day - group.Day > 3))
        { group = new ResidentGroups { Day = State.Day }; State.Groups[home] = group; }
        return group;
    }
    private void UseGroups(Search s, Guid home, bool extension)
    {
        var group = Group(home);
        if (group.Representatives == null) s.BuildingHome = home;
        else if (extension) { s.ExtensionResidents = group.Representatives; PositionExtensionResident(s); }
        else s.Residents = group.Representatives;
    }
    private static void PositionExtensionResident(Search s)
    {
        int at = s.ExtensionResidents.BinarySearch(s.ExtensionAfterResident);
        s.ExtensionResidentIndex = at >= 0 ? at + 1 : ~at;
    }
    private void BuildGroupStep(ResidentGroups group, Guid home, Guid district)
    {
        if (group.Representatives != null) return;
        if (_world.NextAdult(home, group.After, out var id))
        {
            group.After = id;
            var p = _world.GetPerson(id);
            if (p != null && p.Adult && !p.WorkUnavailable && p.Home == home && p.District == district && !group.ByWork.ContainsKey(p.Work))
            { group.ByWork[p.Work] = p.Id; group.Preparing.Add(p.Id); }
        }
        else { group.Representatives = group.Preparing; group.Preparing = null; group.Day = State.Day; }
    }
    private void ResumeExtension(Search s)
    {
        if (!State.Extensions.TryGetValue(s.Actor.Id, out var cursor) || s.Homes.Count == 0) return;
        s.SeedIndex = cursor.Seed % s.Seeds.Count;
        // Binary search is logarithmic; the home list was built in ID order.
        int at = s.Homes.BinarySearch(cursor.Home);
        s.ExtensionHomeIndex = at >= 0 ? at : Math.Min(~at, s.Homes.Count - 1);
        s.ExtensionHome = s.Homes[s.ExtensionHomeIndex];
        s.ExtensionSeedStarted = cursor.SeedStarted && at >= 0;
        s.ExtensionAfterResident = at >= 0 ? cursor.Resident : Guid.Empty;
        if (s.ExtensionHome != s.Actor.Home && _world.UsableHome(s.ExtensionHome, s.Actor.District)) UseGroups(s, s.ExtensionHome, true);
    }
    private void ExtendStep(Search s)
    {
        if (++s.ExtensionSteps > ExtensionBudget || s.Seeds.Count == 0 || s.Homes.Count == 0 || s.ExtensionVisits >= s.Homes.Count)
        { EndExtension(s); return; }
        if (s.ExtensionHome == Guid.Empty)
        {
            s.ExtensionHome = s.Homes[s.ExtensionHomeIndex % s.Homes.Count];
            s.ExtensionResidents = new List<Guid>(); s.ExtensionResidentIndex = 0;
            if (s.ExtensionHome != s.Actor.Home && _world.UsableHome(s.ExtensionHome, s.Actor.District)) UseGroups(s, s.ExtensionHome, true);
            else NextExtensionHome(s);
            return;
        }
        if (s.ExtensionHome == s.Actor.Home) { NextExtensionHome(s); return; }
        var seed = s.Seeds[s.SeedIndex];
        if (!s.ExtensionSeedStarted)
        {
            s.ExtensionSeedStarted = true;
            if (s.ExtensionHome != s.Actor.Home && s.ExtensionHome != seed.Home && _world.HasAdultVacancy(s.ExtensionHome))
                Consider(s, Score(s.Actor, new Plan { Home = seed.Home, Partner = seed.Partner, LastHome = s.ExtensionHome }));
            return;
        }
        if (s.ExtensionResidentIndex < s.ExtensionResidents.Count)
        {
            var third = s.ExtensionResidents[s.ExtensionResidentIndex++];
            // Resuming a partial resident scan never reprocesses the prefix in one tick.
            if (third.CompareTo(s.ExtensionAfterResident) <= 0) return;
            s.ExtensionAfterResident = third;
            Consider(s, Score(s.Actor, new Plan { Home = seed.Home, Partner = seed.Partner, LastHome = s.ExtensionHome, Third = third }));
            return;
        }
        s.SeedIndex++; s.ExtensionSeedStarted = false; s.ExtensionAfterResident = Guid.Empty; s.ExtensionResidentIndex = 0;
        if (s.SeedIndex >= s.Seeds.Count) { s.SeedIndex = 0; NextExtensionHome(s); }
    }
    private static void NextExtensionHome(Search s)
    {
        s.SeedIndex = 0; s.ExtensionVisits++; s.ExtensionHomeIndex = (s.ExtensionHomeIndex + 1) % s.Homes.Count;
        s.ExtensionHome = Guid.Empty; s.ExtensionSeedStarted = false; s.ExtensionAfterResident = Guid.Empty;
        s.ExtensionResidentIndex = 0; s.ExtensionResidents = new List<Guid>();
    }
    private static void BeginValidation(Search s, int phase)
    { s.Phase = phase; s.ValidateIndex = 0; s.ValidatedBest = null; s.BuildingHome = Guid.Empty; s.ValidationRevision = -1; }
    private void EndExtension(Search s)
    {
        State.Extensions[s.Actor.Id] = new ExtensionCursor { Seed = s.SeedIndex,
            Home = s.ExtensionHome != Guid.Empty ? s.ExtensionHome : (s.Homes.Count == 0 ? Guid.Empty : s.Homes[s.ExtensionHomeIndex % s.Homes.Count]),
            Resident = s.ExtensionAfterResident, SeedStarted = s.ExtensionSeedStarted };
        BeginValidation(s, 3);
    }
    private Plan Score(Person actor, Plan proposed)
    {
        if (actor.Work == Guid.Empty || actor.WorkUnavailable || !_world.UsableHome(actor.Home, actor.District) ||
            proposed.Home == actor.Home || !_world.UsableHome(proposed.Home, actor.District)) return null;
        Person partner = null, third = null;
        if (proposed.Partner != Guid.Empty)
        {
            partner = _world.GetPerson(proposed.Partner);
            if (!EligiblePartner(partner, actor, proposed.Home)) return null;
            if (proposed.LastHome == Guid.Empty && partner.Work == actor.Work) return null;
        }
        var beforeStatus = ReadDistance(actor.Home, actor.Work, out var before);
        if (beforeStatus == RouteStatus.Invalid || !Distance(proposed.Home, actor.Work, out var after) ||
            (beforeStatus == RouteStatus.Reachable && before <= after)) return null;
        bool recovery = beforeStatus == RouteStatus.Unreachable;
        float gain = recovery ? -after : before - after; int pairGain = 0;
        if (partner == null)
        {
            if (proposed.LastHome != Guid.Empty || !SafeMove(actor.Home, proposed.Home, out pairGain)) return null;
        }
        else
        {
            Guid destination = actor.Home;
            if (proposed.LastHome != Guid.Empty)
            {
                destination = proposed.LastHome;
                if (destination == actor.Home || destination == proposed.Home || !_world.UsableHome(destination, actor.District)) return null;
                if (proposed.Third != Guid.Empty)
                {
                    third = _world.GetPerson(proposed.Third);
                    if (!EligiblePartner(third, actor, destination) || third.Id == partner.Id) return null;
                }
                else if (!SafeMove(actor.Home, destination, out pairGain)) return null;
            }
            if (!Distance(partner.Home, partner.Work, out var oldOther) || !Distance(destination, partner.Work, out var newOther) ||
                (recovery && newOther > oldOther)) return null;
            gain += oldOther - newOther;
            if (third != null)
            {
                if (!Distance(third.Home, third.Work, out var oldThird) || !Distance(actor.Home, third.Work, out var newThird) ||
                    (recovery && newThird > oldThird)) return null;
                gain += oldThird - newThird;
            }
        }
        if (float.IsNaN(gain) || float.IsInfinity(gain)) return null;
        return recovery || gain > MinimumSaving ? new Plan { Home = proposed.Home, Partner = proposed.Partner, LastHome = proposed.LastHome,
            Third = proposed.Third, Gain = gain, NewDistance = after, PairGain = pairGain, Recovery = recovery } : null;
    }
    private bool SafeMove(Guid sourceId, Guid targetId, out int pairGain)
    {
        pairGain = 0;
        if (!_world.HasAdultVacancy(targetId)) return false;
        var source = _world.GetPopulation(sourceId); var target = _world.GetPopulation(targetId);
        if (source == null || target == null || source.Adults < 1) return false;
        var a = source.WithAdults(source.Adults - 1); var b = target.WithAdults(target.Adults + 1);
        pairGain = a.Pairs + b.Pairs - source.Pairs - target.Pairs;
        return b.Free >= b.BirthSlots && pairGain >= 0 && a.BirthSlots + b.BirthSlots >= source.BirthSlots + target.BirthSlots;
    }
    private enum RouteStatus { Unreachable, Reachable, Invalid }
    private RouteStatus ReadDistance(Guid home, Guid work, out float value)
    {
        if (work == Guid.Empty) { value = 0; return RouteStatus.Reachable; }
        if (!_world.TryDistance(home, work, out value)) return RouteStatus.Unreachable;
        return value >= 0 && !float.IsNaN(value) && !float.IsInfinity(value) ? RouteStatus.Reachable : RouteStatus.Invalid;
    }
    private bool Distance(Guid home, Guid work, out float value) => ReadDistance(home, work, out value) == RouteStatus.Reachable;
    private static bool EligiblePartner(Person p, Person a, Guid home) =>
        p != null && p.Adult && !p.WorkUnavailable && p.Id != a.Id && p.Home == home && p.District == a.District;
    private static void Consider(Search s, Plan plan)
    {
        if (plan == null) return;
        s.Candidates.Add(plan); s.Candidates.Sort((a,b) => Better(a,b) ? -1 : Better(b,a) ? 1 : 0);
        if (s.Candidates.Count > CandidatesKept) s.Candidates.RemoveAt(s.Candidates.Count - 1);
        s.Best = s.Candidates[0];
    }
    private static bool Better(Plan a, Plan b)
    {
        if (a.Recovery != b.Recovery) return a.Recovery;
        if (a.Gain != b.Gain) return a.Gain > b.Gain;
        if (a.PairGain != b.PairGain) return a.PairGain > b.PairGain;
        if (a.NewDistance != b.NewDistance) return a.NewDistance < b.NewDistance;
        if ((a.Partner == Guid.Empty) != (b.Partner == Guid.Empty)) return a.Partner == Guid.Empty;
        if (a.Home != b.Home) return a.Home.CompareTo(b.Home) < 0;
        if (a.Partner != b.Partner) return a.Partner.CompareTo(b.Partner) < 0;
        if (a.LastHome != b.LastHome) return a.LastHome.CompareTo(b.LastHome) < 0;
        return a.Third.CompareTo(b.Third) < 0;
    }
    private void Apply(Person actor, Plan plan)
    {
        if (plan.LastHome != Guid.Empty)
        { _world.Transfer(actor.Id, plan.Home, plan.Partner, plan.LastHome, plan.Third); if (plan.Third == Guid.Empty) State.Chains++; else State.Rotations++; }
        else if (plan.Partner == Guid.Empty) { _world.Move(actor.Id, plan.Home); State.Moves++; }
        else { _world.Swap(actor.Id, plan.Partner); State.Swaps++; }
        if (plan.Recovery) State.Recoveries++;
        MarkHousingChanged(actor.District, actor.Home);
        MarkHousingChanged(actor.District, plan.Home);
        if (plan.LastHome != Guid.Empty) MarkHousingChanged(actor.District, plan.LastHome);
    }
    private static bool SameAssignment(Person a, Person b) => a != null && a.Adult && a.Home == b.Home &&
        a.Work == b.Work && a.WorkUnavailable == b.WorkUnavailable && a.District == b.District;
}





