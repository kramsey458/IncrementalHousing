using IncrementalHousing;
using Newtonsoft.Json;
using System.Diagnostics;

static partial class Program
{
    static int passed;
    static Guid G(int n) => new Guid(n, 0, 0, new byte[8]);
    static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    static void Test(string name, Action action) { action(); passed++; Console.WriteLine("PASS " + name); }
    static (World, Optimizer) Setup(float oldDistance = 20, float newDistance = 5)
    {
        var w = new World();
        w.Homes[G(10)] = new Home(); w.Homes[G(20)] = new Home();
        w.People[G(1)] = new Person { Id = G(1), Home = G(10), Work = G(100), District = G(999), Adult = true };
        w.Routes[(G(10), G(100))] = oldDistance; w.Routes[(G(20), G(100))] = newDistance;
        var o = new Optimizer(w); o.EnqueueDay(new[] { G(1) }); return (w, o);
    }
    static void Partner(World w, float before, float after, bool employed = true)
    {
        w.People[G(2)] = new Person { Id = G(2), Home = G(20), Work = employed ? G(200) : Guid.Empty, District = G(999), Adult = true };
        w.Routes[(G(20), G(200))] = before; w.Routes[(G(10), G(200))] = after;
    }
    static void Drain(Optimizer o, int limit = 10000)
    {
        while ((o.State.Active != null || o.State.Next < o.State.Queue.Count) && limit-- > 0) o.Tick();
        Check(limit > 0, "Queue did not drain");
    }
    static Optimizer Reload(World w, Optimizer o) => new Optimizer(w, JsonConvert.DeserializeObject<OptimizerState>(JsonConvert.SerializeObject(o.State)));
    static int Main(string[] args)
    {
        try { Run(args); return 0; }
        catch (Exception exception)
        {
            Console.Error.WriteLine("TEST FAILURE: " + exception);
            return 1;
        }
    }
    static void Run(string[] args)
    {
        if (args.Length == 1 && args[0] == "--verify-failure-handler")
            throw new InvalidOperationException("Intentional failure-handler check.");
        Test("vacant bed shortens commute without evicting unrelated residents", () => {
            var (w,o)=Setup(); w.Homes[G(20)].Capacity=2; w.Homes[G(20)].AdultLimit=2; Partner(w,5,30); Drain(o);
            Check(w.People[G(1)].Home==G(20) && w.People[G(2)].Home==G(20) && o.State.Moves==1,"Move");
        });
        Test("beneficial full-home swap", () => {var(w,o)=Setup(); Partner(w,10,15); Drain(o); Check(o.State.Swaps==1 && w.People[G(2)].Home==G(10),"Swap");});
        Test("reject swap whose inconvenience exceeds benefit", () => {var(w,o)=Setup(); Partner(w,1,30); Drain(o); Check(w.Trace.Count==0,"Harmful swap");});
        Test("reject equal total commute swap", () => {var(w,o)=Setup(); Partner(w,5,20); Drain(o); Check(w.Trace.Count==0,"Equal swap");});
        Test("unemployed adult swap partner costs zero commute", () => {var(w,o)=Setup(); Partner(w,0,0,false); Drain(o); Check(o.State.Swaps==1,"Unemployed partner");});
        Test("child is never a swap partner", () => {var(w,o)=Setup(); Partner(w,0,0,false); w.People[G(2)].Adult=false; Drain(o); Check(w.Trace.Count==0,"Child moved");});
        Test("reserved child bed is not taken by a direct move", () => {var(w,o)=Setup(); w.Homes[G(20)].AdultLimit=0; Drain(o); Check(w.Trace.Count==0,"Child slot");});
        Test("sub-threshold changes do not churn homes", () => {var(w,o)=Setup(10,9.5f); Drain(o); Check(w.Trace.Count==0,"Threshold");});
        foreach (var value in new[] { float.NaN, float.PositiveInfinity, -1f })
            Test("invalid path distance rejected: "+value, () => {var(w,o)=Setup(20,value); Drain(o); Check(w.Trace.Count==0,"Invalid route");});
        Test("unreachable house skipped", () => {var(w,o)=Setup(); w.Routes.Remove((G(20),G(100))); Drain(o); Check(w.Trace.Count==0,"Unreachable");});
        Test("unreachable existing commute left to vanilla recovery", () => {var(w,o)=Setup(); w.Routes.Remove((G(10),G(100))); Drain(o); Check(w.Trace.Count==0,"Old unreachable");});
        Test("paused or blocked house skipped", () => {var(w,o)=Setup(); w.Homes[G(20)].Usable=false; Drain(o); Check(w.Trace.Count==0,"Blocked");});
        Test("cross-district house skipped", () => {var(w,o)=Setup(); w.Homes[G(20)].District=G(998); Drain(o); Check(w.Trace.Count==0,"District");});
        foreach (var mode in new[] { "child", "homeless", "unemployed", "deleted" })
            Test("skip "+mode+" actor", () => {var(w,o)=Setup(); var p=w.People[G(1)]; if(mode=="child")p.Adult=false; if(mode=="homeless")p.Home=Guid.Empty; if(mode=="unemployed")p.Work=Guid.Empty; if(mode=="deleted")w.People.Remove(G(1)); Drain(o); Check(w.Trace.Count==0,"Ineligible");});
        Test("chooses best total gain instead of first house", () => {var(w,o)=Setup(); w.Homes[G(30)]=new Home(); w.Routes[(G(30),G(100))]=1; Drain(o); Check(w.People[G(1)].Home==G(30),"Best");});
        Test("deterministic house tie-break across reverse registry order", () => {
            var(w,o)=Setup(); w.Homes[G(30)]=new Home(); w.Routes[(G(30),G(100))]=5; w.Reverse=true; Drain(o); Check(w.People[G(1)].Home==G(20),"Tie");
        });
        Test("net benefit preferred to actor-only benefit", () => {var(w,o)=Setup(); Partner(w,1,14); w.Homes[G(30)]=new Home(); w.Routes[(G(30),G(100))]=10; Drain(o); Check(w.People[G(1)].Home==G(30),"Net choice");});
        Test("candidate budget bounds dense occupied housing", () => {
            var(w,o)=Setup(); w.Homes[G(20)].Capacity=100; w.Homes[G(20)].AdultLimit=100;
            for(int i=2;i<102;i++)w.People[G(i)]=new Person{Id=G(i),Home=G(20),Work=G(200),District=G(999),Adult=true};
            w.Routes[(G(20),G(200))]=10; w.Routes[(G(10),G(200))]=15;
            o.Tick(); Check(o.State.Active!=null && w.Calls<=Optimizer.StepsPerTick*4,"Budget"); Drain(o); Check(o.State.Swaps==1,"Completion");
        });
        foreach(var mutation in new[]{"job","home","migration","death","target full","target blocked","route changed"})
            Test("revalidates across ticks: "+mutation, () => {
                var(w,o)=Setup(); AddDistantHouses(w,40); o.Tick(); Check(o.State.Active!=null,"Need active");
                if(mutation=="job")w.People[G(1)].Work=G(101);
                if(mutation=="home")w.People[G(1)].Home=G(30);
                if(mutation=="migration")w.People[G(1)].District=G(998);
                if(mutation=="death")w.People.Remove(G(1));
                if(mutation=="target full")Partner(w,1,100);
                if(mutation=="target blocked")w.Homes[G(20)].Usable=false;
                if(mutation=="route changed")w.Routes[(G(20),G(100))]=100;
                Drain(o); Check(w.Trace.Count==0,"Stale plan applied");
            });
        Test("save/reload preserves unfinished scan and exact commit tick", () => {
            var(w,a)=Setup(); AddDistantHouses(w,80); a.Tick(); var w2=w.Copy(); var b=Reload(w2,a);
            for(int i=0;i<100;i++){a.Tick();b.Tick();Check(JsonConvert.SerializeObject(a.State)==JsonConvert.SerializeObject(b.State),"State diverged");Check(string.Join(";",w.Trace)==string.Join(";",w2.Trace),"Moves diverged");}
        });
        Test("save/reload preserves resident cursor and best swap", () => {
            var(w,a)=Setup(); w.Homes[G(20)].Capacity=60;
            for(int i=2;i<62;i++)w.People[G(i)]=new Person{Id=G(i),Home=G(20),Work=G(200),District=G(999),Adult=true};
            w.Routes[(G(20),G(200))]=10; w.Routes[(G(10),G(200))]=15;
            a.Tick(); var w2=w.Copy();w2.Reverse=true;var b=Reload(w2,a);
            Drain(a);Drain(b);Check(JsonConvert.SerializeObject(a.State)==JsonConvert.SerializeObject(b.State),"Resident restore");Check(w2.People[G(2)].Home==G(10),"Partner tie");
        });
        Test("new day keeps backlog ahead of already processed actors", () => {
            var(w,o)=Setup(); Partner(w,20,5); o.EnqueueDay(new[]{G(2),G(1)}); o.Tick(); o.EnqueueDay(new[]{G(1),G(2),G(3)});
            Check(o.State.Queue.SequenceEqual(new[]{G(2),G(1),G(3)}),"Backlog priority");
        });
        Test("new day does not duplicate active actor", () => {var(w,o)=Setup();AddDistantHouses(w,40);o.Tick();o.EnqueueDay(new[]{G(1),G(1),G(2)});Check(o.State.Queue.SequenceEqual(new[]{G(2)}),"Active duplicate");});
        Test("reject unknown save schema", () => {bool threw=false;try{new Optimizer(new World(),new OptimizerState{Schema=99});}catch(InvalidOperationException){threw=true;}Check(threw,"Schema");});
        Test("randomized mirrored peers: occupancy, monotonic commute, determinism", () => {
            var random=new Random(7401);
            for(int trial=0;trial<40;trial++){
                var w=new World();for(int h=10;h<20;h++)w.Homes[G(h)]=new Home{Capacity=3,AdultLimit=3};
                for(int p=1;p<=25;p++)w.People[G(p)]=new Person{Id=G(p),Home=G(10+(p-1)/3),Work=G(100+p%6),District=G(999),Adult=true};
                foreach(var h in w.Homes.Keys)for(int j=100;j<106;j++)w.Routes[(h,G(j))]=random.Next(1,100);
                var w2=w.Copy();w2.Reverse=true;var a=new Optimizer(w);var b=new Optimizer(w2);
                for(int day=0;day<3;day++){
                    a.EnqueueDay(w.People.Keys);b.EnqueueDay(w2.People.Keys.Reverse());
                    for(int tick=0;tick<300;tick++){
                        float before=w.Total();w.Calls=0;a.Tick();b.Tick();
                        Check(w.Total()<=before,"Total commute increased");Check(w.Calls<=96,"Path budget");
                        Check(JsonConvert.SerializeObject(a.State)==JsonConvert.SerializeObject(b.State),"Peer state diverged");
                        Check(string.Join(";",w.Trace)==string.Join(";",w2.Trace),"Peer moves diverged");
                        foreach(var h in w.Homes)Check(w.People.Values.Count(p=>p.Home==h.Key)<=h.Value.Capacity,"Capacity exceeded");
                    }
                }
            }
        });
        Test("Folktails commute move cannot split the last pair", () => {
            var(w,o)=Setup(); Breed(w,G(10),3);Breed(w,G(20),3); AddAdult(w,2,10);Drain(o);
            Check(w.Trace.Count==0,"Last breeding pair split");
        });
        Test("unemployed actors remain with vanilla housing", () => {
            var(w,o)=Setup(); Breed(w,G(10),3);Breed(w,G(20),3);Partner(w,0,0,false);w.People[G(1)].Work=Guid.Empty;Drain(o);
            Check(w.Trace.Count==0,"Moved without commute saving");
        });
        Test("breeding repair cannot justify a worse commute", () => {
            var(w,o)=Setup(5,50);Breed(w,G(10),3);Breed(w,G(20),3);Partner(w,0,0,false);Drain(o);
            Check(w.Trace.Count==0,"Commute increased for repair");
        });
        Test("adult swap still considered when direct move would split a pair", () => {
            var(w,o)=Setup();Breed(w,G(10),6);Breed(w,G(20),6);AddAdult(w,3,10);Partner(w,30,1);Drain(o);
            Check(o.State.Swaps==1&&w.GetPopulation(G(10)).Adults==2&&w.GetPopulation(G(20)).Adults==1,"Safe swap lost");
        });
        Test("child-only home reserves no nonexistent pair capacity", () => {
            var(w,o)=Setup();Breed(w,G(10),6);Breed(w,G(20),3);AddAdult(w,3,10);AddAdult(w,4,10);
            AddAdult(w,5,20);w.People[G(5)].Adult=false;AddAdult(w,6,20);w.People[G(6)].Adult=false;Drain(o);
            Check(o.State.Moves==1&&w.GetPopulation(G(20)).Adults==1,"Child-only capacity wasted");
        });
        foreach(int capacity in new[]{3,6,9}) Test("newborn capacity protected for lodge capacity "+capacity, () => {
            var(w,o)=Setup();Breed(w,G(10),9);Breed(w,G(20),capacity);
            // An incoming adult would create another pair but leave insufficient newborn space.
            for(int i=0;i<capacity-1;i++)AddAdult(w,1000+i,20);
            Drain(o);Check(o.State.Moves==0,"Reserved newborn bed consumed");
        });
        Test("existing child already satisfies the pair reservation", () => {
            var(w,o)=Setup();Breed(w,G(10),6);Breed(w,G(20),3);AddAdult(w,3,10);AddAdult(w,4,10);
            AddAdult(w,5,20);AddAdult(w,6,20);w.People[G(6)].Adult=false;Drain(o);
            Check(o.State.Moves==1&&w.GetPopulation(G(20)).Adults==2,"Existing child counted twice");
        });
        Test("new birth during unfinished scan invalidates reserved capacity", () => {
            var(w,o)=Setup();Breed(w,G(10),6);Breed(w,G(20),3);AddAdult(w,3,10);AddAdult(w,4,10);AddDistantHouses(w,40);
            o.Tick();AddAdult(w,5,20);AddAdult(w,6,20);AddAdult(w,7,20);w.People[G(7)].Adult=false;Drain(o);
            Check(o.State.Moves==0,"Stale capacity used");
        });
        Test("non-breeding housing can still use its final bed", () => {var(w,o)=Setup();Drain(o);Check(o.State.Moves==1,"Non-breeding regression");});
        Test("saved breeding repair resumes deterministically", () => {
            var(w,a)=Setup(50,5);Breed(w,G(10),3);Breed(w,G(20),3);Partner(w,0,0,false);AddDistantHouses(w,40);
            a.Tick();var w2=w.Copy();w2.Reverse=true;var b=Reload(w2,a);Drain(a);Drain(b);
            Check(a.State.Moves==1&&JsonConvert.SerializeObject(a.State)==JsonConvert.SerializeObject(b.State),"Repair restore");
            Check(string.Join(";",w.Trace)==string.Join(";",w2.Trace),"Repair moves diverged");
        });
        Test("Preview 1 saved move is checked against new breeding rules", () => {
            var(w,o)=Setup();Breed(w,G(10),3);Breed(w,G(20),3);AddAdult(w,3,10);
            o.State.Active=new Search{Actor=w.GetPerson(G(1)),Best=new Plan{Home=G(20),Gain=15,NewDistance=5}};
            o.State.Next=o.State.Queue.Count;Drain(o);Check(w.Trace.Count==0,"Old unsafe plan applied");
        });
        Test("larger commute saving outranks creating an extra pair", () => {
            var(w,o)=Setup(50,30);Breed(w,G(10),3);Breed(w,G(20),3);Partner(w,0,0,false);
            w.Homes[G(30)]=new Home{Capacity=3,AdultLimit=3,Breeding=true};w.Routes[(G(30),G(100))]=1;
            Drain(o);Check(w.People[G(1)].Home==G(30),"Pair priority overrode commute");
        });
        Test("saved Preview 2 pair-repair plan cannot lengthen commute", () => {
            var(w,o)=Setup(5,50);Breed(w,G(10),3);Breed(w,G(20),3);Partner(w,0,0,false);
            o.State.Active=new Search{Actor=w.GetPerson(G(1)),Best=new Plan{Home=G(20),PairGain=1,Gain=-45,NewDistance=50}};
            o.State.Next=o.State.Queue.Count;Drain(o);Check(w.Trace.Count==0,"Legacy harmful plan committed");
        });
        Test("coworker swaps skip all partner path queries", () => {
            var(w,o)=Setup();w.Homes[G(20)].Capacity=100;
            for(int i=2;i<102;i++)w.People[G(i)]=new Person{Id=G(i),Home=G(20),Work=G(100),District=G(999),Adult=true};
            o.Tick();Check(o.State.Active!=null,"Need resident scan");w.Calls=0;o.Tick();
            Check(w.Calls==0,"Queried zero-gain coworkers");Drain(o);Check(w.Trace.Count==0,"Coworkers swapped");
        });
        Test("skipped actors share the fixed budget without starving a worker", () => {
            var(w,o)=Setup();var ids=new List<Guid>();
            for(int i=200;i<240;i++){AddAdult(w,i,10);w.People[G(i)].Adult=false;ids.Add(G(i));}
            ids.Add(G(1));o.State.Queue=ids;o.State.Next=0;o.Tick();
            Check(o.State.Next==16&&o.State.Evaluated==16,"Unbounded skip batch");o.Tick();o.Tick();
            Check(o.State.Moves==1,"Eligible worker delayed by one tick per child");
        });
        Test("already minimal commute does not snapshot district homes", () => {
            var(w,o)=Setup(0,0);AddDistantHouses(w,500);Drain(o);
            Check(w.HomeSnapshots==0&&w.Calls==1,"Scanned unimprovable worker");
        });
        Test("route cache invalidation, endpoint identity and bounded growth", () => {
            var cache=new RouteCache<(int,int,int,int)>(2);var key=(10,100,1,2);
            cache.Store(key,7);Check(cache.TryGet(key,out var cost)&&cost==7,"Warm miss");
            Check(!cache.TryGet((10,100,1,3),out _),"Changed access reused cost");
            cache.Clear();Check(!cache.TryGet(key,out _),"Navigation invalidation stale");
            cache.Store(key,float.NaN);cache.Store(key,-1);cache.Store(key,float.PositiveInfinity);Check(cache.Count==0,"Invalid route cached");
            for(int i=0;i<100;i++){cache.Store((i,100,1,2),i);Check(cache.Count<=2,"Unbounded cache");}
        });
        Test("warm and cold route caches preserve exact decisions and commit ticks", () => {
            var(w,a)=Setup();AddDistantHouses(w,80);var w2=w.Copy();w.UseCache=true;w2.UseCache=true;
            foreach(var route in w.Routes)w.Cache.Store(route.Key,route.Value);
            var b=new Optimizer(w2);b.EnqueueDay(w2.People.Keys);
            for(int tick=0;tick<100;tick++){
                a.Tick();w2.Cache.Clear();b.Tick();
                Check(JsonConvert.SerializeObject(a.State)==JsonConvert.SerializeObject(b.State),"Cache changed commit tick");
                Check(string.Join(";",w.Trace)==string.Join(";",w2.Trace),"Cache changed moves");
            }
            Check(w.NativeCalls<w2.NativeCalls,"No cache reuse");
        });
        Test("shared workplace route cache reduces repeated native calls", () => {
            var(w,o)=Setup();w.UseCache=true;AddDistantHouses(w,80);w.Homes[G(10)].Capacity=20;w.Homes[G(20)].Capacity=20;w.Homes[G(20)].AdultLimit=20;
            for(int i=2;i<=20;i++){AddAdult(w,i,10);w.People[G(i)].Work=G(100);}
            o.EnqueueDay(w.People.Keys);Drain(o);
            Check(w.NativeCalls==82,"Repeated stable home/work routes");
            Console.WriteLine($"Shared-workplace fixture: {w.Calls} distance lookups, {w.NativeCalls} native-query stand-ins.");
        });
        Test("randomized breeding peers keep commutes monotonic and pairs intact", () => {
            var random=new Random(8214);
            for(int trial=0;trial<40;trial++){
                var w=new World();for(int h=10;h<20;h++)w.Homes[G(h)]=new Home{Capacity=6,AdultLimit=6,Breeding=true};
                for(int p=1;p<=30;p++)w.People[G(p)]=new Person{Id=G(p),Home=G(10+(p-1)/3),Work=G(100+p%6),District=G(999),Adult=true};
                foreach(var h in w.Homes.Keys)for(int j=100;j<106;j++)w.Routes[(h,G(j))]=random.Next(1,100);
                var peer=w.Copy();peer.Reverse=true;var a=new Optimizer(w);var b=new Optimizer(peer);a.EnqueueDay(w.People.Keys);b.EnqueueDay(peer.People.Keys.Reverse());
                for(int tick=0;tick<400;tick++){
                    var cost=w.Total();var pairs=w.Homes.Keys.Sum(h=>w.GetPopulation(h).Pairs);w.Calls=0;a.Tick();b.Tick();
                    Check(w.Total()<=cost&&w.Homes.Keys.Sum(h=>w.GetPopulation(h).Pairs)>=pairs,"Commute or breeding regression");
                    Check(w.Calls<=96,"Budget exceeded");Check(JsonConvert.SerializeObject(a.State)==JsonConvert.SerializeObject(b.State),"Breeding peers diverged");
                    Check(string.Join(";",w.Trace)==string.Join(";",peer.Trace),"Breeding decisions diverged");
                    foreach(var h in w.Homes.Keys){var population=w.GetPopulation(h);Check(population.Free>=population.BirthSlots,"Newborn capacity consumed");}
                }
            }
        });
        Preview5Checks();
        if (args.Length == 2) Test("compiled adapter follows installed component API contract", () => AdapterApiChecks.Verify(args[0], args[1]));
        Console.WriteLine($"{passed} checks passed. Native Unity execution and two-player playtest are not exercised.");
    }
    static void AddDistantHouses(World w,int count){for(int i=30;i<30+count;i++){w.Homes[G(i)]=new Home();w.Routes[(G(i),G(100))]=100;}}
    static void Breed(World w,Guid home,int capacity){w.Homes[home].Breeding=true;w.Homes[home].Capacity=capacity;w.Homes[home].AdultLimit=capacity;}
    static void AddAdult(World w,int person,int home){w.People[G(person)]=new Person{Id=G(person),Home=G(home),District=G(999),Adult=true};}
    sealed class Home { public int Capacity=1,AdultLimit=1; public bool Usable=true; public bool Breeding; public Guid District=G(999); }
    sealed class World : IHousingWorld
    {
        public Dictionary<Guid,Person> People=new();public Dictionary<Guid,Home> Homes=new();
        public Dictionary<(Guid,Guid),float> Routes=new();public List<string> Trace=new();public bool Reverse,UseCache;public int Calls,NativeCalls,HomeSnapshots;
        public RouteCache<(Guid,Guid)> Cache=new();
        public Person GetPerson(Guid id)=>People.TryGetValue(id,out var p)?new Person{Id=p.Id,Home=p.Home,Work=p.Work,District=p.District,Adult=p.Adult}:null;
        public Guid[] GetHomes(Guid district){HomeSnapshots++;return(Reverse?Homes.Keys.Reverse():Homes.Keys).ToArray();}
        public Guid[] GetAdults(Guid home){var ids=People.Values.Where(p=>p.Home==home&&p.Adult).Select(p=>p.Id);return(Reverse?ids.Reverse():ids).ToArray();}
        public bool UsableHome(Guid home,Guid district)=>Homes.TryGetValue(home,out var h)&&h.Usable&&h.District==district;
        public HousingPopulation GetPopulation(Guid home)=>Homes.TryGetValue(home,out var h)?new HousingPopulation{Adults=People.Values.Count(p=>p.Home==home&&p.Adult),Children=People.Values.Count(p=>p.Home==home&&!p.Adult),Capacity=h.Capacity,ChildSlots=h.Capacity/3,Breeding=h.Breeding}:null;
        public bool HasAdultVacancy(Guid home)=>Homes.TryGetValue(home,out var h)&&People.Values.Count(p=>p.Home==home)<h.Capacity&&People.Values.Count(p=>p.Home==home&&p.Adult)<h.AdultLimit;
        public bool TryDistance(Guid home,Guid work,out float distance){Calls++;if(UseCache&&Cache.TryGet((home,work),out distance))return true;NativeCalls++;var ok=Routes.TryGetValue((home,work),out distance);if(ok&&UseCache)Cache.Store((home,work),distance);return ok;}
        public void Move(Guid person,Guid home){Check(HasAdultVacancy(home),"Full destination");People[person].Home=home;Trace.Add($"M:{person}:{home}");}
        public void Swap(Guid person,Guid other){(People[person].Home,People[other].Home)=(People[other].Home,People[person].Home);Trace.Add($"S:{person}:{other}");}
        public void Transfer(Guid actor,Guid target,Guid partner,Guid lastHome,Guid third){var first=People[actor].Home;People[actor].Home=target;People[partner].Home=lastHome;if(third!=Guid.Empty)People[third].Home=first;Trace.Add($"T:{actor}:{partner}:{third}:{lastHome}");foreach(var home in Homes)Check(People.Values.Count(p=>p.Home==home.Key)<=home.Value.Capacity,"Transfer capacity");}
        public float Total()=>People.Values.Where(p=>p.Work!=Guid.Empty).Sum(p=>Routes[(p.Home,p.Work)]);
        public World Copy(){var w=new World{Reverse=Reverse};foreach(var p in People)w.People[p.Key]=GetPerson(p.Key);foreach(var h in Homes)w.Homes[h.Key]=new Home{Capacity=h.Value.Capacity,AdultLimit=h.Value.AdultLimit,Usable=h.Value.Usable,Breeding=h.Value.Breeding,District=h.Value.District};foreach(var r in Routes)w.Routes[r.Key]=r.Value;w.Trace.AddRange(Trace);return w;}
    }
}



