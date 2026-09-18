using System;
using System.Collections.Generic;

namespace IncrementalHousing;

public sealed class Person { public Guid Id, Home, Work, District; public bool Adult; }
public interface IHousingWorld
{
    Person GetPerson(Guid id);
    Guid[] GetHomes(Guid district);
    Guid[] GetAdults(Guid home);
    bool UsableHome(Guid home, Guid district);
    bool HasAdultVacancy(Guid home);
    HousingPopulation GetPopulation(Guid home);
    bool TryDistance(Guid home, Guid work, out float distance);
    void Move(Guid person, Guid home);
    void Swap(Guid person, Guid other);
    // Called only after all participants and the net benefit have been validated.
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
}
public sealed class ResidentGroups
{
    public Guid[] Members = Array.Empty<Guid>();
    public int Next;
    public Dictionary<Guid, Guid> ByWork = new Dictionary<Guid, Guid>();
    public Guid[] Representatives;
}
public sealed class RestingSearch
{
    public Person Actor;
    public long Revision;
    public int UntilDay;
}
public sealed class WorkplaceMinimum
{
    public Guid District, Work;
    public float Cost;
}
public sealed class Search
{
    public Person Actor;
    public Guid[] Homes = Array.Empty<Guid>();
    public int HomeIndex;
    public Guid Target;
    public Guid[] Residents = Array.Empty<Guid>();
    public int ResidentIndex;
    public Plan Best;
    public List<Plan> Candidates = new List<Plan>();
    public List<Plan> Seeds = new List<Plan>();
    public ResidentGroups BuildingGroups;
    public long Revision;
    public float Minimum;
    // 0: simple scan; 1: simple validation; 2: bounded chains/rotations; 3: extended validation.
    public int Phase, ValidateIndex;
    public Plan ValidatedBest;
    public int SeedIndex, ExtensionHomeIndex, ExtensionSteps, ExtensionStart;
    public Guid ExtensionHome;
    public Guid[] ExtensionResidents = Array.Empty<Guid>();
    public int ExtensionResidentIndex;
}
public sealed class OptimizerState
{
    public int Schema = 2;
    public List<Guid> Queue = new List<Guid>();
    public int Next;
    public Search Active;
    public long Evaluated, Moves, Swaps, Chains, Rotations, Revision;
    public int Day;
    public bool Dirty;
    // Subscription membership is saved so a joining peer observes exactly the
    // same entities as the host, including entities discovered since day start.
    public bool WatchersInitialized;
    public SortedSet<Guid> ObservedPeople = new SortedSet<Guid>();
    public SortedSet<Guid> ObservedHomes = new SortedSet<Guid>();
    public Dictionary<Guid, Guid[]> Homes = new Dictionary<Guid, Guid[]>();
    public Dictionary<Guid, ResidentGroups> Groups = new Dictionary<Guid, ResidentGroups>();
    public Dictionary<Guid, WorkplaceMinimum> Minima = new Dictionary<Guid, WorkplaceMinimum>();
    public Dictionary<Guid, RestingSearch> Resting = new Dictionary<Guid, RestingSearch>();
    public Dictionary<Guid, int> ExtensionOffsets = new Dictionary<Guid, int>();
}
public sealed class Optimizer
{
    public const int StepsPerTick = 16;
    public const int CandidatesKept = 16, SeedsKept = 8, ExtensionBudget = 256;
    public const float MinimumSaving = 0.5f;
    public OptimizerState State { get; }
    private readonly IHousingWorld _world;
    public Optimizer(IHousingWorld world, OptimizerState state = null)
    {
        _world = world; State = state ?? new OptimizerState();
        if ((State.Schema != 1 && State.Schema != 2) || State.Queue == null || State.Next < 0 || State.Next > State.Queue.Count)
            throw new InvalidOperationException("Unsupported or invalid IncrementalHousing save state.");
        if (State.Schema == 1)
        {
            // Old best-only scans cannot supply a shortlist. Requeue the actor without moving anyone.
            if (State.Active?.Actor != null) State.Queue.Insert(State.Next, State.Active.Actor.Id);
            State.Active = null; State.Schema = 2;
        }
    }
    // The adapter records live simulation changes. This pending flag is saved as well.
    public void MarkWorldChanged() => State.Dirty = true;
    private void Invalidate()
    {
        State.Revision++;
        State.Homes.Clear(); State.Groups.Clear(); State.Minima.Clear(); State.Resting.Clear();
        State.Dirty = false;
        if (State.Active != null && (State.Active.Phase == 1 || State.Active.Phase == 3))
        { State.Active.ValidateIndex = 0; State.Active.ValidatedBest = null; }
        // Keep the active snapshot; its shortlist is always freshly scored at the end.
    }
    public void EnqueueDay(IEnumerable<Guid> population)
    {
        State.Day++;
        if (State.Active != null) State.Active.Revision = -1;
        State.Homes.Clear(); State.Groups.Clear(); State.Minima.Clear();
        var pending = new List<Guid>(); var seen = new HashSet<Guid>();
        if (State.Active != null) seen.Add(State.Active.Actor.Id);
        for (int i = State.Next; i < State.Queue.Count; i++) if (seen.Add(State.Queue[i])) pending.Add(State.Queue[i]);
        var today = new List<Guid>(population); today.Sort();
        var alive = new HashSet<Guid>(today);
        foreach (var id in new List<Guid>(State.Resting.Keys)) if (!alive.Contains(id)) State.Resting.Remove(id);
        foreach (var id in new List<Guid>(State.ExtensionOffsets.Keys)) if (!alive.Contains(id)) State.ExtensionOffsets.Remove(id);
        foreach (var id in today) if (seen.Add(id)) pending.Add(id);
        State.Queue = pending; State.Next = 0;
    }
    public void Tick()
    {
        if (State.Dirty) Invalidate();
        if (State.Active != null && !SameAssignment(_world.GetPerson(State.Active.Actor.Id), State.Active.Actor))
        { State.Active = null; return; }
        for (int step = 0; step < StepsPerTick; step++)
        {
            if (State.Active == null)
            {
                if (State.Next >= State.Queue.Count) return;
                var actor = _world.GetPerson(State.Queue[State.Next++]); State.Evaluated++;
                if (actor == null || !actor.Adult || actor.Home == Guid.Empty || actor.Work == Guid.Empty ||
                    !_world.UsableHome(actor.Home, actor.District)) continue;
                if (State.Resting.TryGetValue(actor.Id, out var rest) && rest.Revision == State.Revision &&
                    State.Day < rest.UntilDay && SameAssignment(actor, rest.Actor)) continue;
                if (!Distance(actor.Home, actor.Work, out var cost) || cost <= 0) continue;
                if (State.Minima.TryGetValue(actor.Work, out var minimum) && minimum.District == actor.District && cost <= minimum.Cost) continue;
                if (!State.Homes.TryGetValue(actor.District, out var homes))
                { homes = _world.GetHomes(actor.District); Array.Sort(homes); State.Homes[actor.District] = homes; }
                State.Active = new Search { Actor = actor, Homes = homes, Revision = State.Revision, Minimum = cost };
                continue;
            }
            var s = State.Active;
            // One actor per tick completion, and no unbounded commit-time work.
            if (s.BuildingGroups != null)
            {
                BuildGroupStep(s.BuildingGroups, s.Phase == 2 ? s.ExtensionHome : s.Target, s.Actor.District);
                if (s.BuildingGroups.Representatives != null)
                {
                    if (s.Revision == State.Revision) State.Groups[s.Phase == 2 ? s.ExtensionHome : s.Target] = s.BuildingGroups;
                    if (s.Phase == 2) s.ExtensionResidents = s.BuildingGroups.Representatives;
                    else s.Residents = s.BuildingGroups.Representatives;
                    s.BuildingGroups = null;
                }
                if (s.Phase == 2 && ++s.ExtensionSteps >= ExtensionBudget) EndExtension(s);
                continue;
            }
            if (s.Phase == 1 || s.Phase == 3)
            {
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
                    s.Phase = 2; s.Candidates.Clear(); s.Best = null;
                    State.ExtensionOffsets.TryGetValue(s.Actor.Id, out s.ExtensionStart);
                    continue;
                }
                if (s.Revision == State.Revision)
                    State.Resting[s.Actor.Id] = new RestingSearch { Actor = s.Actor, Revision = State.Revision, UntilDay = State.Day + 3 };
                State.Active = null; return;
            }
            if (s.Phase == 2) { ExtendStep(s); continue; }
            if (s.ResidentIndex < s.Residents.Length)
            {
                var id = s.Residents[s.ResidentIndex++];
                var partner = _world.GetPerson(id);
                if (EligiblePartner(partner, s.Actor, s.Target) && partner.Work != s.Actor.Work)
                {
                    Keep(s.Seeds, new Plan { Home = s.Target, Partner = id }, SeedsKept);
                    Consider(s, Score(s.Actor, new Plan { Home = s.Target, Partner = id }));
                }
                continue;
            }
            if (s.HomeIndex >= s.Homes.Length)
            {
                if (s.Revision == State.Revision)
                    State.Minima[s.Actor.Work] = new WorkplaceMinimum { District = s.Actor.District, Work = s.Actor.Work, Cost = s.Minimum };
                BeginValidation(s, 1); continue;
            }
            var target = s.Homes[s.HomeIndex++];
            if (target == s.Actor.Home || !_world.UsableHome(target, s.Actor.District) ||
                !Distance(target, s.Actor.Work, out var after)) continue;
            s.Minimum = Math.Min(s.Minimum, after);
            if (!Distance(s.Actor.Home, s.Actor.Work, out var before) || before <= after) continue;
            s.Target = target; s.Residents = Array.Empty<Guid>(); s.ResidentIndex = 0;
            if (_world.HasAdultVacancy(target)) Consider(s, Score(s.Actor, new Plan { Home = target }));
            UseGroups(s, target, false);
        }
    }
    private void UseGroups(Search s, Guid home, bool extension)
    {
        if (!State.Groups.TryGetValue(home, out var group))
        {
            var ids = _world.GetAdults(home); Array.Sort(ids);
            group = new ResidentGroups { Members = ids };
        }
        if (group.Representatives == null) s.BuildingGroups = group;
        else if (extension) s.ExtensionResidents = group.Representatives;
        else s.Residents = group.Representatives;
    }
    private void BuildGroupStep(ResidentGroups group, Guid home, Guid district)
    {
        if (group.Next < group.Members.Length)
        {
            var p = _world.GetPerson(group.Members[group.Next++]);
            if (p != null && p.Adult && p.Home == home && p.District == district && !group.ByWork.ContainsKey(p.Work)) group.ByWork[p.Work] = p.Id;
        }
        if (group.Next == group.Members.Length)
        { var ids = new List<Guid>(group.ByWork.Values); ids.Sort(); group.Representatives = ids.ToArray(); }
    }
    private void ExtendStep(Search s)
    {
        if (++s.ExtensionSteps > ExtensionBudget || s.SeedIndex >= s.Seeds.Count || s.Homes.Length == 0)
        {
            EndExtension(s); return;
        }
        var seed = s.Seeds[s.SeedIndex];
        if (s.ExtensionResidentIndex < s.ExtensionResidents.Length)
        {
            var third = s.ExtensionResidents[s.ExtensionResidentIndex++];
            Consider(s, Score(s.Actor, new Plan { Home = seed.Home, Partner = seed.Partner, LastHome = s.ExtensionHome, Third = third }));
            return;
        }
        if (s.ExtensionHomeIndex >= s.Homes.Length)
        { s.SeedIndex++; s.ExtensionHomeIndex = 0; s.ExtensionResidents = Array.Empty<Guid>(); return; }
        var home = s.Homes[(s.ExtensionStart + s.ExtensionHomeIndex++) % s.Homes.Length];
        s.ExtensionResidents = Array.Empty<Guid>(); s.ExtensionResidentIndex = 0; s.ExtensionHome = home;
        if (home == s.Actor.Home || home == seed.Home || !_world.UsableHome(home, s.Actor.District)) return;
        if (_world.HasAdultVacancy(home))
            Consider(s, Score(s.Actor, new Plan { Home = seed.Home, Partner = seed.Partner, LastHome = home }));
        UseGroups(s, home, true);
    }
    private static void BeginValidation(Search s, int phase)
    { s.Phase = phase; s.ValidateIndex = 0; s.ValidatedBest = null; s.BuildingGroups = null; }
    private void EndExtension(Search s)
    {
        State.ExtensionOffsets[s.Actor.Id] = s.Homes.Length == 0 ? 0 : (s.ExtensionStart + Math.Max(1, s.ExtensionHomeIndex)) % s.Homes.Length;
        BeginValidation(s, 3);
    }
    private Plan Score(Person actor, Plan proposed)
    {
        if (actor.Work == Guid.Empty || !_world.UsableHome(actor.Home, actor.District) ||
            proposed.Home == actor.Home || !_world.UsableHome(proposed.Home, actor.District)) return null;
        Person partner = null, third = null;
        if (proposed.Partner != Guid.Empty)
        {
            partner = _world.GetPerson(proposed.Partner);
            if (!EligiblePartner(partner, actor, proposed.Home)) return null;
            if (proposed.LastHome == Guid.Empty && partner.Work == actor.Work) return null;
        }
        if (!Distance(actor.Home, actor.Work, out var before) || !Distance(proposed.Home, actor.Work, out var after) || before <= after) return null;
        float gain = before - after; int pairGain = 0;
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
            if (!Distance(partner.Home, partner.Work, out var oldOther) || !Distance(destination, partner.Work, out var newOther)) return null;
            gain += oldOther - newOther;
            if (third != null)
            {
                if (!Distance(third.Home, third.Work, out var oldThird) || !Distance(actor.Home, third.Work, out var newThird)) return null;
                gain += oldThird - newThird;
            }
        }
        return gain > MinimumSaving ? new Plan { Home = proposed.Home, Partner = proposed.Partner, LastHome = proposed.LastHome,
            Third = proposed.Third, Gain = gain, NewDistance = after, PairGain = pairGain } : null;
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
    private bool Distance(Guid home, Guid work, out float value)
    {
        if (work == Guid.Empty) { value = 0; return true; }
        return _world.TryDistance(home, work, out value) && value >= 0 && !float.IsNaN(value) && !float.IsInfinity(value);
    }
    private static bool EligiblePartner(Person p, Person a, Guid home) => p != null && p.Adult && p.Id != a.Id && p.Home == home && p.District == a.District;
    private static void Keep(List<Plan> plans, Plan plan, int limit)
    {
        plans.Add(plan); plans.Sort((a,b) => Better(a,b) ? -1 : Better(b,a) ? 1 : 0);
        if (plans.Count > limit) plans.RemoveAt(plans.Count - 1);
    }
    private static void Consider(Search s, Plan plan)
    { if (plan == null) return; Keep(s.Candidates, plan, CandidatesKept); s.Best = s.Candidates[0]; }
    private static bool Better(Plan a, Plan b)
    {
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
        // Also covers worlds without event subscriptions; no stale groups after our own moves.
        MarkWorldChanged();
    }
    private static bool SameAssignment(Person a, Person b) => a != null && a.Adult && a.Home == b.Home && a.Work == b.Work && a.District == b.District;
}
