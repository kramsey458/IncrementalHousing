using IncrementalHousing;
using Newtonsoft.Json;
using System.Diagnostics;

static class Program
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
    static void Main()
    {
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
                        Check(w.Total()<=before,"Total commute increased");Check(w.Calls<=64,"Path budget");
                        Check(JsonConvert.SerializeObject(a.State)==JsonConvert.SerializeObject(b.State),"Peer state diverged");
                        Check(string.Join(";",w.Trace)==string.Join(";",w2.Trace),"Peer moves diverged");
                        foreach(var h in w.Homes)Check(w.People.Values.Count(p=>p.Home==h.Key)<=h.Value.Capacity,"Capacity exceeded");
                    }
                }
            }
        });
        Console.WriteLine($"{passed} checks passed. Native Unity execution and two-player playtest are not exercised.");
    }
    static void AddDistantHouses(World w,int count){for(int i=30;i<30+count;i++){w.Homes[G(i)]=new Home();w.Routes[(G(i),G(100))]=100;}}
    sealed class Home { public int Capacity=1,AdultLimit=1; public bool Usable=true; public Guid District=G(999); }
    sealed class World : IHousingWorld
    {
        public Dictionary<Guid,Person> People=new();public Dictionary<Guid,Home> Homes=new();
        public Dictionary<(Guid,Guid),float> Routes=new();public List<string> Trace=new();public bool Reverse;public int Calls;
        public Person GetPerson(Guid id)=>People.TryGetValue(id,out var p)?new Person{Id=p.Id,Home=p.Home,Work=p.Work,District=p.District,Adult=p.Adult}:null;
        public Guid[] GetHomes(Guid district)=>(Reverse?Homes.Keys.Reverse():Homes.Keys).ToArray();
        public Guid[] GetAdults(Guid home){var ids=People.Values.Where(p=>p.Home==home&&p.Adult).Select(p=>p.Id);return(Reverse?ids.Reverse():ids).ToArray();}
        public bool UsableHome(Guid home,Guid district)=>Homes.TryGetValue(home,out var h)&&h.Usable&&h.District==district;
        public bool HasAdultVacancy(Guid home)=>Homes.TryGetValue(home,out var h)&&People.Values.Count(p=>p.Home==home)<h.Capacity&&People.Values.Count(p=>p.Home==home&&p.Adult)<h.AdultLimit;
        public bool TryDistance(Guid home,Guid work,out float distance){Calls++;return Routes.TryGetValue((home,work),out distance);}
        public void Move(Guid person,Guid home){Check(HasAdultVacancy(home),"Full destination");People[person].Home=home;Trace.Add($"M:{person}:{home}");}
        public void Swap(Guid person,Guid other){(People[person].Home,People[other].Home)=(People[other].Home,People[person].Home);Trace.Add($"S:{person}:{other}");}
        public float Total()=>People.Values.Where(p=>p.Work!=Guid.Empty).Sum(p=>Routes[(p.Home,p.Work)]);
        public World Copy(){var w=new World{Reverse=Reverse};foreach(var p in People)w.People[p.Key]=GetPerson(p.Key);foreach(var h in Homes)w.Homes[h.Key]=new Home{Capacity=h.Value.Capacity,AdultLimit=h.Value.AdultLimit,Usable=h.Value.Usable,District=h.Value.District};foreach(var r in Routes)w.Routes[r.Key]=r.Value;w.Trace.AddRange(Trace);return w;}
    }
}
