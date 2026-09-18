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
    public void StartMod(IModEnvironment environment) => Debug.Log("[IncrementalHousing] Preview 1 loaded.");
}

public sealed class HousingService : ILoadableSingleton, ISaveableSingleton, ITickableSingleton, IHousingWorld
{
    private static readonly SingletonKey SaveKey = new SingletonKey("IncrementalHousing");
    private static readonly PropertyKey<string> StateKey = new PropertyKey<string>("State");
    private readonly EventBus _events;
    private readonly DistrictCenterRegistry _districts;
    private readonly EntityRegistry _entities;
    private readonly ISingletonLoader _loader;
    private readonly ModRepository _mods;
    private Optimizer _optimizer;
    private bool _disabled;
    // Tick-local only: warm/cold caches cannot affect the candidate budget or saved cursor.
    private readonly Dictionary<(Guid, Guid), (bool, float)> _distances = new Dictionary<(Guid, Guid), (bool, float)>();
    private readonly List<BaseComponent> _components = new List<BaseComponent>();

    public HousingService(EventBus events, DistrictCenterRegistry districts, EntityRegistry entities,
        ISingletonLoader loader, ModRepository mods)
    {
        _events = events; _districts = districts; _entities = entities; _loader = loader; _mods = mods;
    }

    public void Load()
    {
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
        if (!_disabled) _events.Register(this);
    }

    public void Save(ISingletonSaver saver) => saver.GetSingleton(SaveKey).Set(StateKey, JsonConvert.SerializeObject(_optimizer.State));

    [OnEvent]
    public void OnDaytimeStart(DaytimeStartEvent ev)
    {
        var ids = new List<Guid>();
        foreach (var district in _districts.FinishedDistrictCenters)
            foreach (var beaver in district.DistrictPopulation.Beavers)
                ids.Add(Id(beaver));
        _optimizer.EnqueueDay(ids);
    }

    public void Tick()
    {
        if (_disabled) return;
        _distances.Clear();
        _optimizer.Tick();
    }

    public Person GetPerson(Guid id)
    {
        var beaver = Component<Beaver>(id);
        if (!beaver) return null;
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
        foreach (var home in district.DistrictBuildingRegistry.GetEnabledBuildings<Dwelling>()) ids.Add(Id(home));
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
        if (home.FreeAdultSlots > 0) return true;
        // The game's ProcreationHouse type is internal. Inspect component identity without
        // publicizing game assemblies or invoking private methods; only needed at the adult limit.
        _components.Clear();
        home.GetComponents(_components);
        foreach (var component in _components)
            if (component.GetType().FullName == "Timberborn.Reproduction.ProcreationHouse") return false;
        return true;
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
        bool ok = start && end && start.ValidAccessible && end.ValidAccessible && start.HasSingleAccess &&
            end.Accesses.Count > 0 && start.FindRoadPath(end, out distance);
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
