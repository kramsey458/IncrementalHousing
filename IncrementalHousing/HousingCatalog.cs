using System;
using System.Collections.Generic;

namespace IncrementalHousing;

// Authoritative, saved ID indexes. Runtime subscriptions are rebuilt from these exact
// memberships on load. Traversal never depends on registry order or cache warmth.
public sealed class HousingCatalog
{
    public SortedDictionary<Guid, Person> People = new SortedDictionary<Guid, Person>();
    public OrderedIds PeopleIds = new OrderedIds();
    public SortedDictionary<Guid, OrderedIds> DistrictPeople = new SortedDictionary<Guid, OrderedIds>();
    public SortedDictionary<Guid, OrderedIds> DistrictHomes = new SortedDictionary<Guid, OrderedIds>();
    public SortedDictionary<Guid, OrderedIds> HomeAdults = new SortedDictionary<Guid, OrderedIds>();
    public SortedDictionary<Guid, Guid> HomeDistricts = new SortedDictionary<Guid, Guid>();
    public SortedDictionary<Guid, long> HomeVersions = new SortedDictionary<Guid, long>();
    public static bool Next(OrderedIds ids, Guid after, out Guid id)
    {
        id = Guid.Empty;
        if (ids == null) return false;
        int at = ids.BinarySearch(after);
        at = at >= 0 ? at + 1 : ~at;
        if (at >= ids.Count) return false;
        id = ids[at]; return true;
    }
    public static bool Next(SortedDictionary<Guid, OrderedIds> map, Guid key, Guid after, out Guid id)
    { map.TryGetValue(key, out var ids); return Next(ids, after, out id); }
    private static void Add(SortedDictionary<Guid, OrderedIds> map, Guid key, Guid id)
    {
        if (key == Guid.Empty) return;
        if (!map.TryGetValue(key, out var ids)) { ids = new OrderedIds(); map[key] = ids; }
        ids.Add(id);
    }
    private static void Remove(SortedDictionary<Guid, OrderedIds> map, Guid key, Guid id)
    {
        if (map.TryGetValue(key, out var ids)) { ids.Remove(id); if (ids.Count == 0) map.Remove(key); }
    }
    public void SetPerson(Person person)
    {
        if (People.TryGetValue(person.Id, out var old))
        {
            if (old.District != person.District)
            { Remove(DistrictPeople, old.District, person.Id); Add(DistrictPeople, person.District, person.Id); }
            if (old.Adult && (!person.Adult || old.Home != person.Home)) Remove(HomeAdults, old.Home, person.Id);
            if (person.Adult && (!old.Adult || old.Home != person.Home)) Add(HomeAdults, person.Home, person.Id);
        }
        else
        {
            PeopleIds.Add(person.Id); Add(DistrictPeople, person.District, person.Id);
            if (person.Adult) Add(HomeAdults, person.Home, person.Id);
        }
        People[person.Id] = person;
    }
    public void RemovePerson(Guid id)
    {
        if (!People.TryGetValue(id, out var old)) return;
        Remove(DistrictPeople, old.District, id); Remove(HomeAdults, old.Home, id);
        People.Remove(id); PeopleIds.Remove(id);
    }
    public void SetHome(Guid id, Guid district)
    { RemoveHome(id); HomeDistricts[id] = district; Add(DistrictHomes, district, id); }
    public void RemoveHome(Guid id)
    {
        if (HomeDistricts.TryGetValue(id, out var district)) Remove(DistrictHomes, district, id);
        HomeDistricts.Remove(id);
    }
}


// SortedSet.GetViewBetween recounts its range in Timberborn's Mono runtime.
// These lists use allocation-free binary-search cursors; insert/remove shifts
// IDs only when membership changes, not on every job update or planner step.
// JSON stores the ordered items directly, without runtime enumerator state.
public sealed class OrderedIds : List<Guid>
{
    public new bool Add(Guid id)
    {
        int at = BinarySearch(id);
        if (at >= 0) return false;
        Insert(~at, id); return true;
    }
    public new bool Remove(Guid id)
    {
        int at = BinarySearch(id);
        if (at < 0) return false;
        RemoveAt(at); return true;
    }
}
