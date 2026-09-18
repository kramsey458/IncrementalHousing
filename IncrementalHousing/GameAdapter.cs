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
    public void StartMod(IModEnvironment environment) => Debug.Log("[IncrementalHousing] Preview 6 (0.5.0) loaded.");
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
    private bool _disabled, _watching, _mutating;
    private readonly Dictionary<Guid, Beaver> _watchedPeople = new Dictionary<Guid, Beaver>();
    private readonly Dictionary<Guid, Dwelling> _watchedHomes = new Dictionary<Guid, Dwelling>();
    private readonly Dictionary<Guid, DistrictCenter> _watchedDistricts = new Dictionary<Guid, DistrictCenter>();
    private readonly Dictionary<(Guid, Guid), (bool, float)> _distances = new Dictionary<(Guid, Guid), (bool, float)>();
    private readonly RouteCache<(Guid, Guid, Vector3, Vector3)> _routes = new RouteCache<(Guid, Guid, Vector3, Vector3)>();
    private readonly Dictionary<Guid, HousingPopulation> _populations = new Dictionary<Guid, HousingPopulation>();
    private HousingCatalog Catalog => _optimizer.State.Catalog;

    public HousingService(EventBus events, DistrictCenterRegistry districts, EntityRegistry entities,
        ISingletonLoader loader, ModRepository mods, LoadingScreen loadingScreen)
    { _events = events; _districts = districts; _entities = entities; _loader = loader; _mods = mods; _loadingScreen = loadingScreen; }
    public void Load()
    {
        foreach (var mod in _mods.EnabledMods)
            if (mod.Manifest.Id == "BobHousingOptimize" || mod.Manifest.Id == "BobCommuteBalancer" || mod.Manifest.Id == "housingoptimize")
            { _disabled = true; Debug.LogWarning("[IncrementalHousing] Disabled because another housing assignment mod is enabled."); }
        OptimizerState state = null;
        if (_loader.TryGetSingleton(SaveKey, out var saved)) state = JsonConvert.DeserializeObject<OptimizerState>(saved.Get(StateKey));
        _optimizer = new Optimizer(this, state);
        _loadingScreen.LoadingScreenDisabled += Ready;
        if (!_disabled) _events.Register(this);
    }
    public void Save(ISingletonSaver saver) => saver.GetSingleton(SaveKey).Set(StateKey, JsonConvert.SerializeObject(_optimizer.State));
    [OnEvent] public void OnDaytimeStart(DaytimeStartEvent ev)
    { _routes.Clear(); _optimizer.StartDay(); }
    public void Tick()
    {
        if (_disabled) return;
        if (!_watching) Ready(this, EventArgs.Empty);
        _distances.Clear(); _populations.Clear(); _optimizer.Tick();
    }
    public void Unload()
    {
        _loadingScreen.LoadingScreenDisabled -= Ready;
        foreach (var person in _watchedPeople.Values) UnsubscribePerson(person);
        foreach (var home in _watchedHomes.Values) if (home) home.NumberOfDwellersChanged -= HomeChanged;
        foreach (var district in _watchedDistricts.Values) UnsubscribeDistrict(district);
        _watchedPeople.Clear(); _watchedHomes.Clear(); _watchedDistricts.Clear();
    }
    private void Ready(object sender, EventArgs args)
    {
        if (_disabled || _watching) return;
        // Initial catalog construction and subscription restoration happen during loading.
        // Subsequent daily preparation walks saved ordered catalogs one ID per work step.
        bool fresh = !_optimizer.State.WatchersInitialized;
        if (fresh)
        {
            _optimizer.State.Catalog = new HousingCatalog();
            _optimizer.State.ObservedPeople.Clear(); _optimizer.State.ObservedHomes.Clear();
        }
        else
        {
            foreach (var id in new List<Guid>(_optimizer.State.ObservedPeople))
            {
                var person = Component<Beaver>(id);
                if (person) WatchPerson(person, false);
                else { Catalog.RemovePerson(id); _optimizer.State.ObservedPeople.Remove(id); _optimizer.ForgetPerson(id); }
            }
            foreach (var id in new List<Guid>(_optimizer.State.ObservedHomes))
            {
                var home = Component<Dwelling>(id);
                if (home) WatchHome(home);
                else { Catalog.RemoveHome(id); Catalog.HomeAdults.Remove(id); _optimizer.State.ObservedHomes.Remove(id); _optimizer.State.Groups.Remove(id); }
            }
        }
        foreach (var district in _districts.FinishedDistrictCenters)
        {
            WatchDistrict(district);
            if (!fresh) continue;
            foreach (var beaver in district.DistrictPopulation.Beavers) WatchPerson(beaver, true);
            foreach (var home in district.DistrictBuildingRegistry.GetEnabledBuildings<Dwelling>())
            { WatchHome(home); Catalog.SetHome(Id(home), Id(district)); }
        }
        _optimizer.State.WatchersInitialized = true; _watching = true;
    }
    private void WatchDistrict(DistrictCenter district)
    {
        var id = Id(district);
        if (_watchedDistricts.ContainsKey(id)) return;
        _watchedDistricts[id] = district;
        district.DistrictPopulation.CitizenAssigned += CitizenAdded;
        district.DistrictBuildingRegistry.FinishedBuildingRegistered += BuildingAdded;
        district.DistrictBuildingRegistry.FinishedBuildingUnregistered += BuildingRemoved;
    }
    private void UnsubscribeDistrict(DistrictCenter district)
    {
        if (!district) return;
        district.DistrictPopulation.CitizenAssigned -= CitizenAdded;
        district.DistrictBuildingRegistry.FinishedBuildingRegistered -= BuildingAdded;
        district.DistrictBuildingRegistry.FinishedBuildingUnregistered -= BuildingRemoved;
    }
    [OnEvent] public void OnDistrictsChanged(DistrictCenterRegistryChangedEvent ev)
    {
        if (!_watching) return;
        foreach (var district in _districts.FinishedDistrictCenters)
            if (!_watchedDistricts.ContainsKey(Id(district)))
            {
                WatchDistrict(district);
                foreach (var beaver in district.DistrictPopulation.Beavers) WatchPerson(beaver, true);
                foreach (var home in district.DistrictBuildingRegistry.GetEnabledBuildings<Dwelling>()) RegisterHome(home, Id(district));
            }
    }
    private void CitizenAdded(object sender, CitizenAssignedEventArgs args)
    {
        var beaver = args.Citizen.GetComponent<Beaver>();
        if (beaver) { WatchPerson(beaver, true); RefreshPerson(beaver); }
    }
    private void BuildingAdded(object sender, FinishedBuildingRegisteredEventArgs args)
    {
        var home = args.Building.GetComponent<Dwelling>();
        if (home) RegisterHome(home, Id(((DistrictBuildingRegistry)sender).GetComponent<DistrictCenter>()));
    }
    private void BuildingRemoved(object sender, FinishedBuildingUnregisteredEventArgs args)
    {
        var home = args.Building.GetComponent<Dwelling>();
        if (!home) return;
        var id = Id(home);
        if (Catalog.HomeDistricts.TryGetValue(id, out var district))
        { Catalog.RemoveHome(id); _optimizer.MarkHomeListChanged(district, id); }
    }
    private void RegisterHome(Dwelling home, Guid district)
    {
        WatchHome(home);
        var id = Id(home);
        if (Catalog.HomeDistricts.TryGetValue(id, out var old))
        { if (old == district) return; _optimizer.MarkHomeListChanged(old, id); }
        Catalog.SetHome(id, district); _optimizer.MarkHomeListChanged(district, id);
    }
    [OnEvent] public void OnEntityInitialized(EntityInitializedEvent ev)
    {
        if (!_watching) return;
        var beaver = ev.Entity.GetComponent<Beaver>();
        if (beaver) { WatchPerson(beaver, true); RefreshPerson(beaver); }
    }
    [OnEvent] public void OnEntityDeleted(EntityDeletedEvent ev)
    {
        if (!_watching) return;
        var id = ev.Entity.EntityId;
        if (_watchedPeople.TryGetValue(id, out var beaver)) RemovePerson(beaver);
        if (_watchedHomes.TryGetValue(id, out var home))
        {
            if (Catalog.HomeDistricts.TryGetValue(id, out var district)) _optimizer.MarkHomeListChanged(district, id);
            home.NumberOfDwellersChanged -= HomeChanged;
            _watchedHomes.Remove(id); _optimizer.State.ObservedHomes.Remove(id);
            Catalog.RemoveHome(id); Catalog.HomeAdults.Remove(id); _optimizer.State.Groups.Remove(id);
        }
        if (_watchedDistricts.TryGetValue(id, out var center))
        { UnsubscribeDistrict(center); _watchedDistricts.Remove(id); }
    }
    public void OnNavMeshUpdated(NavMeshUpdate update)
    {
        _routes.Clear(); _distances.Clear();
        // Global navigation can connect distant districts; keep this conservative.
        if (_watching) _optimizer.MarkWorldChanged();
    }
    private void Changed(object sender, EventArgs args)
    {
        var component = sender as BaseComponent;
        if (component) { var beaver = component.GetComponent<Beaver>(); if (beaver) RefreshPerson(beaver); }
    }
    private void DistrictChanged(object sender, ChangeAssignedDistrictEventArgs args) => Changed(sender, EventArgs.Empty);
    private void RefreshPerson(Beaver beaver)
    {
        var id = Id(beaver);
        Catalog.People.TryGetValue(id, out var old);
        var person = Snapshot(beaver);
        if (person == null) { RemovePerson(beaver); return; }
        Catalog.SetPerson(person);
        if (!_watching || _mutating) return;
        if (old != null) _optimizer.MarkHousingChanged(old.District, old.Home);
        _optimizer.MarkHousingChanged(person.District, person.Home, id);
    }
    private void RemovePerson(Beaver beaver)
    {
        var id = Id(beaver);
        if (Catalog.People.TryGetValue(id, out var old) && _watching) _optimizer.MarkHousingChanged(old.District, old.Home);
        UnsubscribePerson(beaver); _watchedPeople.Remove(id);
        Catalog.RemovePerson(id); _optimizer.State.ObservedPeople.Remove(id); _optimizer.ForgetPerson(id);
    }
    private void HomeChanged(object sender, EventArgs args)
    {
        var home = (Dwelling)sender;
        _populations.Remove(Id(home));
        if (_watching && !_mutating) _optimizer.MarkHousingChanged(Id(home.GetComponent<DistrictBuilding>()?.District), Id(home));
    }
    private void WatchPerson(Beaver beaver, bool initialize)
    {
        var id = Id(beaver);
        if (_watchedPeople.ContainsKey(id)) return;
        _watchedPeople[id] = beaver; _optimizer.State.ObservedPeople.Add(id);
        var worker = beaver.GetComponent<Worker>();
        if (worker) { worker.GotEmployed += Changed; worker.GotUnemployed += Changed; }
        var dweller = beaver.GetComponent<Dweller>(); if (dweller) dweller.RelationsChanged += Changed;
        var citizen = beaver.GetComponent<Citizen>(); if (citizen) citizen.ChangedAssignedDistrict += DistrictChanged;
        var character = beaver.GetComponent<Character>(); if (character) character.Died += Changed;
        if (initialize) { var person = Snapshot(beaver); if (person != null) Catalog.SetPerson(person); }
    }
    private void UnsubscribePerson(Beaver beaver)
    {
        if (!beaver) return;
        var worker = beaver.GetComponent<Worker>();
        if (worker) { worker.GotEmployed -= Changed; worker.GotUnemployed -= Changed; }
        var dweller = beaver.GetComponent<Dweller>(); if (dweller) dweller.RelationsChanged -= Changed;
        var citizen = beaver.GetComponent<Citizen>(); if (citizen) citizen.ChangedAssignedDistrict -= DistrictChanged;
        var character = beaver.GetComponent<Character>(); if (character) character.Died -= Changed;
    }
    private void WatchHome(Dwelling home)
    {
        var id = Id(home);
        if (_watchedHomes.ContainsKey(id)) return;
        _watchedHomes[id] = home; _optimizer.State.ObservedHomes.Add(id);
        home.NumberOfDwellersChanged += HomeChanged;
    }
    private Person Snapshot(Beaver beaver)
    {
        var character = beaver.GetComponent<Character>();
        var dweller = beaver.GetComponent<Dweller>();
        if (!character || !character.Alive || !dweller) return null;
        var district = beaver.GetComponent<Citizen>()?.AssignedDistrict;
        var worker = beaver.GetComponent<Worker>();
        // Preserve an assigned job even when paused, automated off, or inaccessible.
        var work = worker ? worker.Workplace : null;
        return new Person { Id = Id(beaver), Home = Id(dweller.Home), Work = Id(work), District = Id(district),
            WorkUnavailable = work && (!work.Enabled || work.GetComponent<DistrictBuilding>()?.District != district),
            Adult = !beaver.GetComponent<Child>() };
    }
    public Person GetPerson(Guid id)
    {
        var beaver = Component<Beaver>(id);
        if (!beaver) return null;
        var person = Snapshot(beaver);
        return person != null && person.District != Guid.Empty && Component<DistrictCenter>(person.District)?.Enabled == true ? person : null;
    }
    public bool NextPerson(Guid district, Guid after, out Guid person) => district == Guid.Empty
        ? HousingCatalog.Next(Catalog.PeopleIds, after, out person)
        : HousingCatalog.Next(Catalog.DistrictPeople, district, after, out person);
    public bool NextHome(Guid district, Guid after, out Guid home) => HousingCatalog.Next(Catalog.DistrictHomes, district, after, out home);
    public bool NextAdult(Guid home, Guid after, out Guid adult) => HousingCatalog.Next(Catalog.HomeAdults, home, after, out adult);
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
        // AllComponents avoids the game's blacklist on GetComponents<BaseComponent>.
        foreach (var component in home.AllComponents)
            if (component != null && component.GetType().FullName == "Timberborn.Reproduction.ProcreationHouse") { breeding = true; break; }
        var population = new HousingPopulation { Adults = home.NumberOfAdultDwellers, Children = home.NumberOfChildDwellers,
            Capacity = home.MaxBeavers, ChildSlots = home.ChildSlots, Breeding = breeding };
        _populations[homeId] = population; return population;
    }
    public bool TryDistance(Guid homeId, Guid workId, out float distance)
    {
        var key = (homeId, workId);
        if (_distances.TryGetValue(key, out var cached)) { distance = cached.Item2; return cached.Item1; }
        var home = Component<Dwelling>(homeId); var work = Component<Workplace>(workId);
        var start = home ? home.GetEnabledComponent<Accessible>() : null;
        var end = work ? work.GetEnabledComponent<Accessible>() : null;
        distance = 0;
        if (!start || !end || !BuildingUsable(home) || !work.Enabled ||
            !start.ValidAccessible || !end.ValidAccessible || !start.HasSingleAccess || end.Accesses.Count == 0) return false;
        var blocked = work.GetComponent<BlockableObject>();
        if (blocked && !blocked.IsUnblocked) return false;
        var from = start.UnblockedSingleAccess; var to = end.HasSingleAccess ? end.UnblockedSingleAccess : null;
        bool cacheable = from.HasValue && to.HasValue;
        var routeKey = (homeId, workId, from.GetValueOrDefault(), to.GetValueOrDefault());
        bool ok = cacheable && _routes.TryGet(routeKey, out distance);
        if (!ok) { ok = start.FindRoadPath(end, out distance); if (ok && cacheable) _routes.Store(routeKey, distance); }
        _distances[key] = (ok, distance); return ok;
    }
    public void Move(Guid person, Guid home)
    {
        _mutating = true;
        try { Component<Dwelling>(home).AssignDweller(Component<Dweller>(person)); }
        finally { _mutating = false; }
    }
    public void Swap(Guid person, Guid other)
    {
        var a = Component<Dweller>(person); var b = Component<Dweller>(other);
        var oldA = a.Home; var oldB = b.Home;
        _mutating = true;
        try { a.UnassignFromHome(); b.UnassignFromHome(); oldB.AssignDweller(a); oldA.AssignDweller(b); }
        finally { _mutating = false; }
    }
    public void Transfer(Guid actor, Guid target, Guid partner, Guid lastHome, Guid third)
    {
        var a = Component<Dweller>(actor); var b = Component<Dweller>(partner);
        var c = third == Guid.Empty ? null : Component<Dweller>(third);
        var first = a.Home; var middle = Component<Dwelling>(target); var last = Component<Dwelling>(lastHome);
        _mutating = true;
        try
        {
            a.UnassignFromHome(); b.UnassignFromHome(); if (c) c.UnassignFromHome();
            last.AssignDweller(b); middle.AssignDweller(a); if (c) first.AssignDweller(c);
        }
        finally { _mutating = false; }
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
        var pause = building.GetComponent<PausableBuilding>(); if (pause && pause.Paused) return false;
        var automation = building.GetComponent<Automatable>();
        if (automation && automation.IsAutomated && automation.State == ConnectionState.Off) return false;
        var blocked = building.GetComponent<BlockableObject>(); return !blocked || blocked.IsUnblocked;
    }
}
