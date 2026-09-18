using System;
using System.Collections.Generic;
using Bindito.Core;
using Newtonsoft.Json;
using Timberborn.Automation;
using Timberborn.BaseComponentSystem;
using Timberborn.Beavers;
using Timberborn.BlockingSystem;
using Timberborn.Buildings;
using Timberborn.Characters;
using Timberborn.DwellingSystem;
using Timberborn.EntitySystem;
using Timberborn.GameDistricts;
using Timberborn.Modding;
using Timberborn.ModManagerScene;
using Timberborn.Navigation;
using Timberborn.Persistence;
using Timberborn.SceneLoading;
using Timberborn.SingletonSystem;
using Timberborn.TickSystem;
using Timberborn.TimeSystem;
using Timberborn.WorldPersistence;
using Timberborn.WorkSystem;
using UnityEngine;

namespace IncrementalHousing;

[Context("Game")]
public sealed class HousingConfigurator : Configurator
{
    protected override void Configure() => Bind<HousingService>().AsSingleton();
}

public sealed class ModStarter : IModStarter
{
    public void StartMod(IModEnvironment environment) => Debug.Log("[IncrementalHousing] Preview 5 (0.4.0) loaded.");
}

public sealed class HousingService : ILoadableSingleton, IUnloadableSingleton, ISaveableSingleton, ITickableSingleton, IHousingWorld, ISingletonNavMeshListener
{
    private static readonly SingletonKey SaveKey = new SingletonKey("IncrementalHousing");
    private static readonly PropertyKey<string> StateKey = new PropertyKey<string>("State");
    private readonly EventBus _events;
    private readonly DistrictCenterRegistry _districts;
    private readonly EntityRegistry _entities;
    private readonly ISingletonLoader _loader;
    private readonly ModRepository _mods;
    private readonly LoadingScreen _loadingScreen;
    private Optimizer _optimizer;
    private bool _disabled;
    private bool _watching;
    private readonly HashSet<Guid> _watchedPeople = new HashSet<Guid>();
    private readonly HashSet<Guid> _watchedHomes = new HashSet<Guid>();
    // Tick-local only: warm/cold caches cannot affect the candidate budget or saved cursor.
    private readonly Dictionary<(Guid, Guid), (bool, float)> _distances = new Dictionary<(Guid, Guid), (bool, float)>();
    private readonly RouteCache<(Guid, Guid, Vector3, Vector3)> _routes = new RouteCache<(Guid, Guid, Vector3, Vector3)>();
    private readonly Dictionary<Guid, HousingPopulation> _populations = new Dictionary<Guid, HousingPopulation>();

    public HousingService(EventBus events, DistrictCenterRegistry districts, EntityRegistry entities,
        ISingletonLoader loader, ModRepository mods, LoadingScreen loadingScreen)
    {
        _events = events; _districts = districts; _entities = entities; _loader = loader; _mods = mods;
        _loadingScreen = loadingScreen;
    }

    public void Load()
    {
        _routes.Clear();
        foreach (var mod in _mods.EnabledMods)
            if (mod.Manifest.Id == "BobHousingOptimize" || mod.Manifest.Id == "BobCommuteBalancer" || mod.Manifest.Id == "housingoptimize")
            {
                _disabled = true;
                Debug.LogWarning("[IncrementalHousing] Disabled: turn off Housing Optimize / Commute Balancer before using this mod.");
            }
        OptimizerState state = null;
        if (_loader.TryGetSingleton(SaveKey, out var saved))
            state = JsonConvert.DeserializeObject<OptimizerState>(saved.Get(StateKey));
        _optimizer = new Optimizer(this, state);
        _loadingScreen.LoadingScreenDisabled += Ready;
        if (!_disabled) _events.Register(this);
    }

    public void Save(ISingletonSaver saver) => saver.GetSingleton(SaveKey).Set(StateKey, JsonConvert.SerializeObject(_optimizer.State));

    [OnEvent]
    public void OnDaytimeStart(DaytimeStartEvent ev)
    {
        _routes.Clear();
        var ids = new List<Guid>();
        foreach (var district in _districts.FinishedDistrictCenters)
            foreach (var beaver in district.DistrictPopulation.Beavers)
                ids.Add(Id(beaver));
        _optimizer.EnqueueDay(ids);
    }

    public void Tick()
    {
        if (_disabled) return;
        if (!_watching) Ready(this, EventArgs.Empty);
        _distances.Clear();
        _populations.Clear();
        _optimizer.Tick();
    }

    public void Unload() => _loadingScreen.LoadingScreenDisabled -= Ready;

    private void Ready(object sender, EventArgs args)
    {
        if (_disabled) return;
        // SceneLoader hides the loading screen after scene initialization and before
        // simulation resumes. Subscribe here, not after other services' first tick.
        // Restore the saved membership without treating load events as world changes.
        if (!_watching)
        {
            if (_optimizer.State.WatchersInitialized)
            {
                foreach (var id in new List<Guid>(_optimizer.State.ObservedPeople)) { var p = Component<Beaver>(id); if (p) WatchPerson(p); }
                foreach (var id in new List<Guid>(_optimizer.State.ObservedHomes)) { var h = Component<Dwelling>(id); if (h) WatchHome(h); }
            }
            else { WatchPopulation(); _optimizer.State.WatchersInitialized = true; }
            _watching = true;
        }
    }

    // Regular, committed navigation updates include added/removed/blocked road
    // and zipline edges. Preview placement changes are not simulation routes.
    public void OnNavMeshUpdated(NavMeshUpdate update)
    {
        _routes.Clear();
        _distances.Clear();
        if (_watching) _optimizer.MarkWorldChanged();
    }

    private void Changed(object sender, EventArgs args) => _optimizer.MarkWorldChanged();

    private void WatchPopulation()
    {
        foreach (var district in _districts.FinishedDistrictCenters)
        {
            foreach (var beaver in district.DistrictPopulation.Beavers) WatchPerson(beaver);
            foreach (var home in district.DistrictBuildingRegistry.GetEnabledBuildings<Dwelling>()) WatchHome(home);
        }
    }
    private void WatchPerson(Beaver beaver)
    {
        if (!_watchedPeople.Add(Id(beaver))) return;
        _optimizer.State.ObservedPeople.Add(Id(beaver));
        var worker = beaver.GetComponent<Worker>();
        if (worker) { worker.GotEmployed += Changed; worker.GotUnemployed += Changed; }
        var dweller = beaver.GetComponent<Dweller>();
        if (dweller) dweller.RelationsChanged += Changed;
        var citizen = beaver.GetComponent<Citizen>();
        if (citizen) citizen.ChangedAssignedDistrict += (sender, args) => _optimizer.MarkWorldChanged();
        var character = beaver.GetComponent<Character>();
        if (character) character.Died += Changed;
    }
    private void WatchHome(Dwelling home)
    {
        if (_watchedHomes.Add(Id(home)))
        { _optimizer.State.ObservedHomes.Add(Id(home)); home.NumberOfDwellersChanged += Changed; }
    }

    public Person GetPerson(Guid id)
    {
        var beaver = Component<Beaver>(id);
        if (!beaver) return null;
        if (_watching) WatchPerson(beaver);
        var character = beaver.GetComponent<Character>();
        var dweller = beaver.GetComponent<Dweller>();
        var district = beaver.GetComponent<Citizen>()?.AssignedDistrict;
        if (!character || !character.Alive || !dweller || !district || !district.Enabled) return null;
        var worker = beaver.GetComponent<Worker>();
        var work = worker && worker.Employed ? worker.Workplace : null;
        if (work && (!BuildingUsable(work) || work.GetComponent<DistrictBuilding>()?.District != district)) work = null;
        return new Person { Id = id, Home = Id(dweller.Home), Work = Id(work), District = Id(district),
            Adult = !beaver.GetComponent<Child>() };
    }

    public Guid[] GetHomes(Guid districtId)
    {
        var district = Component<DistrictCenter>(districtId);
        if (!district) return Array.Empty<Guid>();
        var ids = new List<Guid>();
        // Use committed simulation registries and road graph, never placement-preview state.
        foreach (var home in district.DistrictBuildingRegistry.GetEnabledBuildings<Dwelling>()) { ids.Add(Id(home)); if (_watching) WatchHome(home); }
        return ids.ToArray();
    }

    public Guid[] GetAdults(Guid homeId)
    {
        var home = Component<Dwelling>(homeId);
        if (!home) return Array.Empty<Guid>();
        var ids = new List<Guid>();
        foreach (var dweller in home.AdultDwellers) ids.Add(Id(dweller));
        return ids.ToArray();
    }

    public bool UsableHome(Guid homeId, Guid districtId)
    {
        var home = Component<Dwelling>(homeId);
        if (!home || !BuildingUsable(home) || Id(home.GetComponent<DistrictBuilding>()?.District) != districtId) return false;
        var accessible = home.GetEnabledComponent<Accessible>();
        return accessible && accessible.ValidAccessible && accessible.HasSingleAccess;
    }

    public bool HasAdultVacancy(Guid homeId)
    {
        var home = Component<Dwelling>(homeId);
        if (!home || !home.HasFreeSlots) return false;
        var population = GetPopulation(homeId);
        var after = population.WithAdults(population.Adults + 1);
        return after.Free >= after.BirthSlots;
    }

    public HousingPopulation GetPopulation(Guid homeId)
    {
        if (_populations.TryGetValue(homeId, out var cached)) return cached;
        var home = Component<Dwelling>(homeId);
        if (!home) return null;
        bool breeding = false;
        // The game's ProcreationHouse type is internal. Inspect component identity without
        // publicizing game assemblies or invoking private methods. Cache only for this tick.
        // BaseComponent is blacklisted by GetComponents<T> at runtime, even though
        // that call compiles. AllComponents is the supported untyped enumeration.
        foreach (var component in home.AllComponents)
            if (component != null && component.GetType().FullName == "Timberborn.Reproduction.ProcreationHouse") { breeding = true; break; }
        var population = new HousingPopulation { Adults = home.NumberOfAdultDwellers, Children = home.NumberOfChildDwellers,
            Capacity = home.MaxBeavers, ChildSlots = home.ChildSlots, Breeding = breeding };
        _populations[homeId] = population;
        return population;
    }

    public bool TryDistance(Guid homeId, Guid workId, out float distance)
    {
        var key = (homeId, workId);
        if (_distances.TryGetValue(key, out var cached)) { distance = cached.Item2; return cached.Item1; }
        var home = Component<Dwelling>(homeId);
        var work = Component<Workplace>(workId);
        var start = home ? home.GetEnabledComponent<Accessible>() : null;
        var end = work ? work.GetEnabledComponent<Accessible>() : null;
        distance = 0;
        if (!start || !end || !BuildingUsable(home) || !BuildingUsable(work) ||
            !start.ValidAccessible || !end.ValidAccessible || !start.HasSingleAccess || end.Accesses.Count == 0) return false;
        // Recheck endpoint blocking and exact positions before every cross-tick hit.
        // Multi-access workplaces retain the game's native minimum-over-accesses query.
        var from = start.UnblockedSingleAccess;
        var to = end.HasSingleAccess ? end.UnblockedSingleAccess : null;
        bool cacheable = from.HasValue && to.HasValue;
        var routeKey = (homeId, workId, from.GetValueOrDefault(), to.GetValueOrDefault());
        bool ok = cacheable && _routes.TryGet(routeKey, out distance);
        if (!ok)
        {
            ok = start.FindRoadPath(end, out distance);
            if (ok && cacheable) _routes.Store(routeKey, distance);
        }
        _distances[key] = (ok, distance);
        return ok;
    }

    public void Move(Guid person, Guid home)
    {
        // Planner revalidates immediately before entering this method on the simulation thread.
        Component<Dwelling>(home).AssignDweller(Component<Dweller>(person));
    }

    public void Swap(Guid person, Guid other)
    {
        var a = Component<Dweller>(person);
        var b = Component<Dweller>(other);
        var oldA = a.Home;
        var oldB = b.Home;
        // Free both beds before assigning: no transient over-capacity exception on full homes.
        // This operation does not yield or cross a tick/save boundary.
        a.UnassignFromHome();
        b.UnassignFromHome();
        oldB.AssignDweller(a);
        oldA.AssignDweller(b);
    }

    public void Transfer(Guid actor, Guid target, Guid partner, Guid lastHome, Guid third)
    {
        var a = Component<Dweller>(actor); var b = Component<Dweller>(partner);
        var c = third == Guid.Empty ? null : Component<Dweller>(third);
        var first = a.Home; var middle = Component<Dwelling>(target); var last = Component<Dwelling>(lastHome);
        // Final occupancies have been validated. Free all involved adult beds before
        // assigning; this method never yields across a tick or save boundary.
        a.UnassignFromHome(); b.UnassignFromHome(); if (c) c.UnassignFromHome();
        last.AssignDweller(b); middle.AssignDweller(a); if (c) first.AssignDweller(c);
    }

    private T Component<T>(Guid id) where T : BaseComponent
    {
        if (id == Guid.Empty) return null;
        var entity = _entities.GetEntity(id);
        return entity && !entity.Deleted ? entity.GetComponent<T>() : null;
    }

    private static Guid Id(BaseComponent component) => component ? component.GetComponent<EntityComponent>().EntityId : Guid.Empty;

    private static bool BuildingUsable(BaseComponent building)
    {
        if (!building || !building.Enabled) return false;
        var pause = building.GetComponent<PausableBuilding>();
        if (pause && pause.Paused) return false;
        var automation = building.GetComponent<Automatable>();
        if (automation && automation.IsAutomated && automation.State == ConnectionState.Off) return false;
        var blocked = building.GetComponent<BlockableObject>();
        return !blocked || blocked.IsUnblocked;
    }
}


