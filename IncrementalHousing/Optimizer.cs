using System;
using System.Collections.Generic;

namespace IncrementalHousing;

// Pure managed planner: no frame time, random numbers, or Unity object ordering.
public sealed class Person
{
    public Guid Id, Home, Work, District;
    public bool Adult;
}

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
    public Guid Home, Partner;
    public float Gain, NewDistance;
    public int PairGain;
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
}

public sealed class OptimizerState
{
    public int Schema = 1;
    public List<Guid> Queue = new List<Guid>();
    public int Next;
    public Search Active;
    public long Evaluated, Moves, Swaps;
}

public sealed class Optimizer
{
    public const int StepsPerTick = 16;
    public const float MinimumSaving = 0.5f;
    public OptimizerState State { get; }
    private readonly IHousingWorld _world;

    public Optimizer(IHousingWorld world, OptimizerState state = null)
    {
        _world = world;
        State = state ?? new OptimizerState();
        if (State.Schema != 1 || State.Queue == null || State.Next < 0 || State.Next > State.Queue.Count)
            throw new InvalidOperationException("Unsupported or invalid IncrementalHousing save state.");
    }

    public void EnqueueDay(IEnumerable<Guid> population)
    {
        // Keep unfinished work first. Large settlements must not starve the end of the queue.
        var pending = new List<Guid>();
        var seen = new HashSet<Guid>();
        if (State.Active != null) seen.Add(State.Active.Actor.Id);
        for (int i = State.Next; i < State.Queue.Count; i++)
            if (seen.Add(State.Queue[i])) pending.Add(State.Queue[i]);
        var today = new List<Guid>(population);
        today.Sort();
        foreach (var id in today)
            if (seen.Add(id)) pending.Add(id);
        State.Queue = pending;
        State.Next = 0;
    }

    public void Tick()
    {
        if (State.Active == null)
        {
            if (State.Next >= State.Queue.Count) return;
            var actor = _world.GetPerson(State.Queue[State.Next++]);
            State.Evaluated++;
            // Unemployed adults can repair fragmented breeding households.
            if (actor == null || !actor.Adult || actor.Home == Guid.Empty ||
                !_world.UsableHome(actor.Home, actor.District)) return;
            if (actor.Work == Guid.Empty && _world.GetPopulation(actor.Home)?.Breeding != true) return;
            State.Active = new Search { Actor = actor, Homes = _world.GetHomes(actor.District) };
            Array.Sort(State.Active.Homes);
        }

        var search = State.Active;
        var current = _world.GetPerson(search.Actor.Id);
        if (!SameAssignment(current, search.Actor)) { State.Active = null; return; }

        for (int step = 0; step < StepsPerTick; step++)
        {
            if (search.ResidentIndex < search.Residents.Length)
            {
                var partner = search.Residents[search.ResidentIndex++];
                Consider(search, Score(search.Actor, search.Target, partner));
                continue;
            }
            if (search.HomeIndex >= search.Homes.Length)
            {
                Commit(search);
                State.Active = null;
                return; // At most one actor completes per simulation tick.
            }
            var target = search.Homes[search.HomeIndex++];
            if (target == search.Actor.Home || !_world.UsableHome(target, search.Actor.District)) continue;
            search.Target = target;
            search.Residents = Array.Empty<Guid>();
            search.ResidentIndex = 0;
            if (_world.HasAdultVacancy(target))
            {
                Consider(search, Score(search.Actor, target, Guid.Empty));
            }
            if (ImprovesActor(search.Actor, target))
            {
                search.Residents = _world.GetAdults(target);
                Array.Sort(search.Residents);
            }
        }
    }

    private bool ImprovesActor(Person actor, Guid target)
    {
        return Distance(actor.Home, actor.Work, out var before) && Distance(target, actor.Work, out var after)
            && before - after > MinimumSaving;
    }

    private Plan Score(Person actor, Guid target, Guid partnerId)
    {
        if (!_world.UsableHome(actor.Home, actor.District) || !_world.UsableHome(target, actor.District) ||
            !Distance(actor.Home, actor.Work, out var before) || !Distance(target, actor.Work, out var after)) return null;
        float gain = before - after;
        int pairGain = 0;
        if (partnerId == Guid.Empty)
        {
            if (!_world.HasAdultVacancy(target)) return null;
            var source = _world.GetPopulation(actor.Home);
            var destination = _world.GetPopulation(target);
            if (source == null || destination == null || source.Adults < 1) return null;
            var sourceAfter = source.WithAdults(source.Adults - 1);
            var destinationAfter = destination.WithAdults(destination.Adults + 1);
            // Existing children already occupy breeding slots. Reserve only the remaining
            // newborn capacity, using the same floor(adults / 2) rule as Timberborn.
            if (destinationAfter.Free < destinationAfter.BirthSlots) return null;
            pairGain = sourceAfter.Pairs + destinationAfter.Pairs - source.Pairs - destination.Pairs;
            if (pairGain < 0 || sourceAfter.BirthSlots + destinationAfter.BirthSlots < source.BirthSlots + destination.BirthSlots)
                return null;
            if (pairGain == 0 && (actor.Work == Guid.Empty || gain <= MinimumSaving)) return null;
        }
        else
        {
            if (actor.Work == Guid.Empty || gain <= MinimumSaving) return null;
            var partner = _world.GetPerson(partnerId);
            if (partner == null || !partner.Adult || partner.Id == actor.Id ||
                partner.Home != target || partner.District != actor.District) return null;
            // An unemployed adult has no assigned-workplace commute to worsen.
            if (partner.Work != Guid.Empty)
            {
                if (!Distance(target, partner.Work, out var oldOther) ||
                    !Distance(actor.Home, partner.Work, out var newOther)) return null;
                gain += oldOther - newOther;
            }
        }
        return pairGain > 0 || gain > MinimumSaving ? new Plan { Home = target, Partner = partnerId, Gain = gain, NewDistance = after, PairGain = pairGain } : null;
    }

    private bool Distance(Guid home, Guid work, out float value)
    {
        if (work == Guid.Empty) { value = 0; return true; }
        return _world.TryDistance(home, work, out value) && value >= 0 && !float.IsNaN(value) && !float.IsInfinity(value);
    }

    private static void Consider(Search search, Plan plan)
    {
        if (plan == null) return;
        var old = search.Best;
        if (old == null || Better(plan, old)) search.Best = plan;
    }

    private static bool Better(Plan a, Plan b)
    {
        if (a.PairGain != b.PairGain) return a.PairGain > b.PairGain;
        if (a.Gain != b.Gain) return a.Gain > b.Gain;
        if (a.NewDistance != b.NewDistance) return a.NewDistance < b.NewDistance;
        if ((a.Partner == Guid.Empty) != (b.Partner == Guid.Empty)) return a.Partner == Guid.Empty;
        if (a.Home != b.Home) return a.Home.CompareTo(b.Home) < 0;
        return a.Partner.CompareTo(b.Partner) < 0;
    }

    private void Commit(Search search)
    {
        if (search.Best == null) return;
        var actor = _world.GetPerson(search.Actor.Id);
        if (!SameAssignment(actor, search.Actor)) return;
        // Never trust a plan spanning ticks: recheck occupancy, jobs, reachability and benefit.
        var plan = Score(actor, search.Best.Home, search.Best.Partner);
        if (plan == null) return;
        if (plan.Partner == Guid.Empty) { _world.Move(actor.Id, plan.Home); State.Moves++; }
        else { _world.Swap(actor.Id, plan.Partner); State.Swaps++; }
    }

    private static bool SameAssignment(Person a, Person b) => a != null && a.Adult &&
        a.Home == b.Home && a.Work == b.Work && a.District == b.District;
}
