using IncrementalHousing;
using Newtonsoft.Json;

static partial class Program
{
    static (World, Optimizer) Cycle()
    {
        var w=new World();
        for(int i=0;i<3;i++){
            w.Homes[G(10+i)]=new Home();
            w.People[G(1+i)]=new Person{Id=G(1+i),Home=G(10+i),Work=G(100+i),District=G(999),Adult=true};
            for(int h=0;h<3;h++)w.Routes[(G(10+h),G(100+i))]=h==i?20:h==(i+1)%3?10:30;
        }
        var o=new Optimizer(w);o.EnqueueDay(new[]{G(1)});return(w,o);
    }
    static void Preview5Checks()
    {
        foreach(bool invalid in new[]{true,false}) Test("stale winner falls back to better valid candidate: "+invalid,()=>{
            var(w,o)=Setup(20,1);w.Homes[G(25)]=new Home();w.Routes[(G(25),G(100))]=5;AddDistantHouses(w,40);
            o.Tick();w.Routes[(G(20),G(100))]=invalid?30:19;Drain(o);
            Check(w.People[G(1)].Home==G(25),"Fallback lost");
        });
        Test("two sub-threshold individual gains form a qualifying swap",()=>{
            var(w,o)=Setup(10,9.6f);Partner(w,10,9.6f);Drain(o);Check(o.State.Swaps==1,"Combined gain missed");
        });
        Test("sub-threshold starting commute may initiate a beneficial swap",()=>{
            var(w,o)=Setup(0.4f,0.1f);Partner(w,10,9.6f);Drain(o);Check(o.State.Swaps==1,"Initial gate missed combined gain");
        });
        Test("single cache insertion evicts one old entry, not the working set",()=>{
            var c=new RouteCache<int>(8192);for(int i=0;i<8192;i++)c.Store(i,i);c.Store(8192,1);
            Check(!c.TryGet(0,out _)&&c.Count==8192,"Eviction bound");for(int i=1;i<8192;i++)Check(c.TryGet(i,out var cost)&&cost==i,"Useful route evicted");
            c.Store(1,50);c.Store(8193,1);Check(!c.TryGet(1,out _)&&c.Count==8192,"Updating an entry duplicated eviction order");
            c.Clear();c.Store(4,7);Check(c.Count==1&&c.TryGet(4,out _),"Clear left stale eviction order");
        });
        Test("stable shared-workplace district skips redundant house scans",()=>{
            var w=new World();for(int h=0;h<80;h++){w.Homes[G(10+h)]=new Home{Capacity=3,AdultLimit=3};w.Routes[(G(10+h),G(100))]=10;}
            for(int p=1;p<=200;p++)w.People[G(p)]=new Person{Id=G(p),Home=G(10+(p-1)/3),Work=G(100),District=G(999),Adult=true};
            var o=new Optimizer(w);o.EnqueueDay(w.People.Keys);int ticks=0;while(o.State.Active!=null||o.State.Next<o.State.Queue.Count){o.Tick();ticks++;}
            Check(w.Trace.Count==0&&w.HomeSnapshots==1&&ticks<40&&w.Calls<500,"Repeated stable scans");
            Console.WriteLine($"Stable district: {ticks} ticks, {w.HomeSnapshots} home snapshot, {w.Calls} distance lookups (Preview 4: 1200 / 200 / 31800).");
        });
        Test("unchanged no-improvement actor backs off with periodic fallback",()=>{
            var(w,o)=Setup(20,30);Drain(o);int first=w.HomeSnapshots;o.EnqueueDay(w.People.Keys);Drain(o);Check(w.HomeSnapshots==first,"No-change rescan");
            o.EnqueueDay(w.People.Keys);Drain(o);Check(w.HomeSnapshots==first,"Fallback too early");
            o.EnqueueDay(w.People.Keys);Drain(o);Check(w.HomeSnapshots>first,"Periodic fallback starved");
        });
        Test("world change wakes a resting actor and discards stale minima",()=>{
            var(w,o)=Setup(20,30);Drain(o);o.EnqueueDay(w.People.Keys);w.Routes[(G(20),G(100))]=5;o.MarkWorldChanged();Drain(o);
            Check(o.State.Moves==1,"Change ignored");
        });
        Test("saved pending invalidation is consumed identically after reload",()=>{
            var(w,a)=Setup(20,30);Drain(a);a.EnqueueDay(w.People.Keys);w.Routes[(G(20),G(100))]=5;a.MarkWorldChanged();var peer=w.Copy();var b=Reload(peer,a);
            for(int t=0;t<40;t++){a.Tick();b.Tick();Check(JsonConvert.SerializeObject(a.State)==JsonConvert.SerializeObject(b.State),"Dirty restore diverged");}
            Check(a.State.Moves==1&&string.Join(";",w.Trace)==string.Join(";",peer.Trace),"Dirty decision diverged");
        });
        Test("resident grouping reduces repeated equivalent partner work",()=>{
            var(w,o)=Setup();w.Homes[G(10)].Capacity=50;w.Homes[G(10)].AdultLimit=50;w.Homes[G(20)].Capacity=50;w.Homes[G(20)].AdultLimit=50;
            for(int p=2;p<=100;p++){AddAdult(w,p,p<=50?10:20);w.People[G(p)].Work=G(100);}
            o.EnqueueDay(w.People.Keys);int ticks=0;while(o.State.Active!=null||o.State.Next<o.State.Queue.Count){o.Tick();ticks++;}
            Check(w.Trace.Count==0&&ticks<80,"Repeated coworker scans");Console.WriteLine($"50 actors facing 50 equivalent full-home coworkers: {ticks} ticks.");
        });
        Test("three-way rotation escapes pairwise local optimum",()=>{
            var(w,o)=Cycle();Drain(o);Check(o.State.Rotations==1&&w.Total()==30,"Rotation missed");
        });
        Test("rotation preserves breeding pairs and child counts",()=>{
            var(w,o)=Cycle();for(int h=10;h<13;h++){Breed(w,G(h),3);AddAdult(w,1000+h,h);w.People[G(1000+h)].Work=G(100+h-10);AddAdult(w,2000+h,h);w.People[G(2000+h)].Adult=false;}
            Drain(o);Check(o.State.Rotations==1,"Breeding cycle missed");foreach(var h in w.Homes.Keys){var pop=w.GetPopulation(h);Check(pop.Adults==2&&pop.Children==1,"Household counts changed");}
        });
        Test("vacancy chain can use a neutral partner relocation",()=>{
            var(w,o)=Setup(20,5);Partner(w,5,30);w.Homes[G(30)]=new Home();w.Routes[(G(30),G(100))]=30;w.Routes[(G(30),G(200))]=5;
            Drain(o);Check(o.State.Chains==1&&w.People[G(1)].Home==G(20)&&w.People[G(2)].Home==G(30),"Chain missed");
        });
        Test("vacancy chain cannot consume reserved newborn capacity",()=>{
            var(w,o)=Setup(20,5);Partner(w,5,30);w.Homes[G(30)]=new Home();Breed(w,G(30),3);AddAdult(w,3,30);AddAdult(w,4,30);
            w.Routes[(G(30),G(100))]=30;w.Routes[(G(30),G(200))]=5;Drain(o);Check(o.State.Chains==0,"Reserved chain bed consumed");
        });
        Test("vacancy chain cannot split a breeding pair at its source",()=>{
            var(w,o)=Setup(20,5);Breed(w,G(10),3);AddAdult(w,3,10);Partner(w,5,30);w.Homes[G(30)]=new Home();Breed(w,G(30),3);
            w.Routes[(G(30),G(100))]=30;w.Routes[(G(30),G(200))]=5;Drain(o);Check(o.State.Chains==0,"Pair split by chain");
        });
        Test("rotation search save/load matches every tick and action",()=>{
            var(w,a)=Cycle();a.State.Next=a.State.Queue.Count;a.State.Active=new Search{Actor=w.GetPerson(G(1)),Phase=2,Homes=new[]{G(10),G(11),G(12)},Seeds=new List<Plan>{new Plan{Home=G(11),Partner=G(2)}}};
            var peer=w.Copy();peer.Reverse=true;var b=Reload(peer,a);
            for(int t=0;t<100;t++){w.Calls=0;a.Tick();b.Tick();Check(w.Calls<=96,"Extended path budget");Check(JsonConvert.SerializeObject(a.State)==JsonConvert.SerializeObject(b.State),"Extension restore diverged");}
            Check(a.State.Rotations==1&&string.Join(";",w.Trace)==string.Join(";",peer.Trace),"Extension actions diverged");
        });
        foreach(string mutation in new[]{"job","death","route","blocked"})Test("rotation commit revalidates "+mutation,()=>{
            var(w,o)=Cycle();o.State.Next=o.State.Queue.Count;o.State.Active=new Search{Actor=w.GetPerson(G(1)),Phase=3,Candidates=new List<Plan>{new Plan{Home=G(11),Partner=G(2),LastHome=G(12),Third=G(3)}}};
            if(mutation=="job")w.People[G(2)].Work=G(9999);if(mutation=="death")w.People.Remove(G(2));if(mutation=="route")w.Routes[(G(12),G(101))]=100;if(mutation=="blocked")w.Homes[G(12)].Usable=false;
            Drain(o);Check(o.State.Rotations==0,"Stale rotation applied");
        });
        Test("old schema resumes pending actor under new rules",()=>{
            var(w,o)=Setup();var old=new OptimizerState{Schema=1,Queue=new List<Guid>{G(1)},Next=1,Active=new Search{Actor=w.GetPerson(G(1))}};
            var upgraded=new Optimizer(w,old);Check(upgraded.State.Schema==2&&upgraded.State.Active==null,"Migration failed");Drain(upgraded);Check(upgraded.State.Moves==1,"Active actor lost");
        });
        Test("shortlist revalidation and resident-group save restore match at every boundary",()=>{
            var(w,a)=Cycle();for(int i=50;i<85;i++){w.Homes[G(i)]=new Home{Capacity=3,AdultLimit=3};for(int j=100;j<103;j++)w.Routes[(G(i),G(j))]=100;}
            int ticks=0;while((a.State.Active!=null||a.State.Next<a.State.Queue.Count)&&ticks++<300){
                var peer=w.Copy();var b=Reload(peer,a);a.Tick();b.Tick();Check(JsonConvert.SerializeObject(a.State)==JsonConvert.SerializeObject(b.State),"Boundary save diverged");Check(string.Join(";",w.Trace)==string.Join(";",peer.Trace),"Boundary action diverged");
            }Check(ticks<300&&a.State.Rotations==1,"Bounded search failed");
        });
        Test("extended search has a fixed step cap in a large district",()=>{
            var(w,o)=Setup();Partner(w,1,40);AddDistantHouses(w,400);
            foreach(var h in w.Homes.Keys)if(h!=G(10)&&h!=G(20))w.Routes[(h,G(200))]=100;
            int ticks=0;while((o.State.Active!=null||o.State.Next<o.State.Queue.Count)&&ticks++<150){
                w.Calls=0;o.Tick();Check(w.Calls<=96,"Path cap");Check(o.State.Active==null||o.State.Active.ExtensionSteps<=Optimizer.ExtensionBudget+1,"Unbounded extension");
            }Check(ticks<150&&w.Trace.Count==0,"Extended search failed to stop");
        });
        Test("live change invalidates shared resident representatives",()=>{
            var(w,o)=Setup();Partner(w,5,20);Drain(o);o.EnqueueDay(new[]{G(1)});
            w.People[G(2)].Work=Guid.Empty;o.MarkWorldChanged();Drain(o);Check(o.State.Swaps==1,"Old partner grouping persisted");
        });
        Test("birth before chain commit cancels an occupied last bed",()=>{
            var(w,o)=Setup();Partner(w,5,30);w.Homes[G(30)]=new Home();w.Routes[(G(30),G(100))]=30;w.Routes[(G(30),G(200))]=5;
            o.State.Next=o.State.Queue.Count;o.State.Active=new Search{Actor=w.GetPerson(G(1)),Phase=3,Candidates=new List<Plan>{new Plan{Home=G(20),Partner=G(2),LastHome=G(30)}}};
            AddAdult(w,3,30);w.People[G(3)].Adult=false;Drain(o);Check(o.State.Chains==0&&w.Trace.Count==0,"New occupant displaced");
        });
    }
}

