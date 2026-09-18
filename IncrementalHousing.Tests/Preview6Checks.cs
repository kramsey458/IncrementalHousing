using IncrementalHousing;
using Newtonsoft.Json;

static partial class Program
{
    static void Preview6Checks()
    {
        Test("daily population and home preparation stay within the work budget",()=>{
            var w=new World();
            for(int h=10;h<1010;h++){w.Homes[G(h)]=new Home{Capacity=2,AdultLimit=2};w.Routes[(G(h),G(2000))]=20;}
            for(int p=1;p<=1000;p++)w.People[G(p)]=new Person{Id=G(p),Home=G(10+(p-1)/2),Work=G(2000),District=G(999),Adult=true};
            var o=new Optimizer(w);o.StartDay();
            Check(w.PreparationCalls==0&&o.State.Queue.Count==0,"Day start eagerly enumerated the population");
            int ticks=0;
            while(o.HasPendingWork&&ticks++<2000){
                w.PreparationCalls=0;w.Calls=0;o.Tick();
                Check(w.PreparationCalls<=16&&w.Calls<=96,"Preparation exceeded per-tick budget");
            }
            Check(!o.HasPendingWork&&o.State.Evaluated==1000&&w.HomeSnapshots==1,"Incremental sweep failed");
        });
        Test("job change after drained queue is handled before another morning",()=>{
            var(w,o)=Setup(20,30);Drain(o);
            w.People[G(1)].Work=G(200);w.Routes[(G(10),G(200))]=30;w.Routes[(G(20),G(200))]=5;
            o.MarkHousingChanged(G(999),G(10),G(1));Drain(o);
            Check(o.State.Day==1&&o.State.Moves==1,"Waited for next day");
        });
        Test("navigation change wakes a drained queue in the same day",()=>{
            var(w,o)=Setup(20,30);Drain(o);w.Routes[(G(20),G(100))]=5;o.MarkWorldChanged();Drain(o);
            Check(o.State.Day==1&&o.State.Moves==1,"Shortcut ignored until morning");
        });
        Test("priority requests deduplicate and cannot starve regular actors",()=>{
            var(w,o)=Setup(0,0);o.State.Queue.Clear();o.State.Next=0;o.State.Queued.Clear();
            for(int p=2;p<=30;p++){AddAdult(w,p,10);o.Enqueue(G(p));}
            for(int k=0;k<1000;k++)o.Enqueue(G(1),true);
            Check(o.State.Priority.Count==1,"Priority queue duplicated an actor");
            for(int t=0;t<10;t++){o.Enqueue(G(1),true);o.Tick();}
            Check(o.State.Next==o.State.Queue.Count,"Urgent actor starved normal queue");
        });
        Test("housing changes retain unrelated districts and route minima",()=>{
            var(w,o)=Setup(20,30);Drain(o);
            var otherHomes=new List<Guid>{G(70)};
            var otherGroup=new ResidentGroups{Representatives=new List<Guid>{G(9)}};
            o.State.Homes[G(998)]=otherHomes;o.State.Groups[G(70)]=otherGroup;
            var minimum=o.State.Minima[G(100)];
            o.MarkHousingChanged(G(999),G(10),G(1));
            Check(ReferenceEquals(o.State.Homes[G(998)],otherHomes)&&ReferenceEquals(o.State.Groups[G(70)],otherGroup),"Unrelated cache cleared");
            Check(ReferenceEquals(o.State.Minima[G(100)],minimum),"Occupancy invalidated route minimum");
        });
        Test("unrelated district events do not restart candidate validation",()=>{
            var(w,o)=Setup();AddDistantHouses(w,30);
            o.State.Next=o.State.Queue.Count;o.State.Queued.Clear();
            var candidates=new List<Plan>();for(int i=0;i<16;i++)candidates.Add(new Plan{Home=G(20)});
            o.State.Active=new Search{Actor=w.GetPerson(G(1)),Phase=1,Candidates=candidates};
            for(int t=0;t<5&&o.State.Moves==0;t++){o.MarkHousingChanged(G(998),G(70));o.Tick();}
            Check(o.State.Moves==1,"Unrelated changes perpetually restarted validation");
        });
        Test("new house invalidates home lists and minimum-route shortcut",()=>{
            var(w,o)=Setup(20,30);Drain(o);var minimum=o.State.Minima[G(100)];
            w.Homes[G(5)]=new Home();w.Routes[(G(5),G(100))]=1;o.MarkHomeListChanged(G(999),G(5));Drain(o);
            Check(w.People[G(1)].Home==G(5),"New lower-ID house hidden by old minimum");
        });
        Test("unavailable assigned worker cannot be used as a zero-cost unemployed partner",()=>{
            var(w,o)=Setup();Partner(w,0,0);w.People[G(2)].WorkUnavailable=true;Drain(o);
            Check(w.Trace.Count==0&&w.People[G(2)].Work==G(200),"Unavailable job discarded");
        });
        Test("retained assigned job still counts against an otherwise attractive swap",()=>{
            var(w,o)=Setup();Partner(w,1,100);Drain(o);Check(w.Trace.Count==0,"Partner's assigned commute ignored");
        });
        Test("resumed assigned worker wakes after availability changes",()=>{
            var(w,o)=Setup();w.People[G(1)].WorkUnavailable=true;Drain(o);
            w.People[G(1)].WorkUnavailable=false;o.MarkHousingChanged(G(999),G(10),G(1));Drain(o);
            Check(o.State.Moves==1,"Resumed worker not reconsidered");
        });
        Test("recovery cannot split a breeding pair",()=>{
            var(w,o)=Setup();Breed(w,G(10),3);Breed(w,G(20),3);AddAdult(w,2,10);
            w.Routes.Remove((G(10),G(100)));Drain(o);Check(o.State.Recoveries==0,"Recovery broke pair");
        });
        Test("recovery cannot consume reserved newborn slots",()=>{
            var(w,o)=Setup();Breed(w,G(20),3);AddAdult(w,2,20);AddAdult(w,3,20);
            w.Routes.Remove((G(10),G(100)));
            // Partners also have no valid destination commute, preventing a safe swap.
            foreach(int p in new[]{2,3})w.People[G(p)].Work=G(200);
            w.Routes[(G(20),G(200))]=5;Drain(o);Check(o.State.Recoveries==0,"Reserved slot consumed");
        });
        Test("recovery swap must not worsen the other worker's commute",()=>{
            var(w,o)=Setup();Partner(w,5,6);w.Routes.Remove((G(10),G(100)));Drain(o);
            Check(w.Trace.Count==0,"Recovery transferred inconvenience");
        });
        Test("recovery swap accepts reachable equal-cost partner destination",()=>{
            var(w,o)=Setup();Partner(w,5,5);w.Routes.Remove((G(10),G(100)));Drain(o);
            Check(o.State.Swaps==1&&o.State.Recoveries==1,"Safe recovery missed");
        });
        Test("invalid numeric current route is not mistaken for disconnection",()=>{
            var(w,o)=Setup(float.NaN,5);Drain(o);Check(o.State.Recoveries==0&&w.Trace.Count==0,"Invalid score recovered");
        });
        Test("recovery candidate is rescored if original route returns",()=>{
            var(w,o)=Setup(2,5);
            o.State.Next=o.State.Queue.Count;o.State.Queued.Clear();
            o.State.Active=new Search{Actor=w.GetPerson(G(1)),Phase=1,Candidates=new List<Plan>{new Plan{Home=G(20),Recovery=true}}};
            Drain(o);Check(w.Trace.Count==0,"Old recovery flag overrode live cost");
        });
        Test("late seed is explored beyond the old eight-seed cutoff",()=>{
            var w=new World();foreach(int h in new[]{10,30,40})w.Homes[G(h)]=new Home();
            for(int h=20;h<28;h++)w.Homes[G(h)]=new Home();
            for(int h=1000;h<1300;h++)w.Homes[G(h)]=new Home();
            w.People[G(1)]=new Person{Id=G(1),Home=G(10),Work=G(100),District=G(999),Adult=true};
            w.People[G(3)]=new Person{Id=G(3),Home=G(30),Work=G(300),District=G(999),Adult=true};
            w.People[G(4)]=new Person{Id=G(4),Home=G(40),Work=G(400),District=G(999),Adult=true};
            for(int h=20;h<28;h++)w.People[G(h)]=new Person{Id=G(h),Home=G(h),Work=G(200+h),District=G(999),Adult=true};
            foreach(var h in w.Homes.Keys)foreach(var p in w.People.Values)w.Routes[(h,p.Work)]=100;
            w.Routes[(G(10),G(100))]=20;w.Routes[(G(30),G(100))]=10;w.Routes[(G(40),G(100))]=30;
            for(int h=20;h<28;h++){w.Routes[(G(h),G(100))]=10;w.Routes[(G(h),G(200+h))]=20;w.Routes[(G(10),G(200+h))]=30;}
            w.Routes[(G(30),G(300))]=20;w.Routes[(G(40),G(300))]=10;w.Routes[(G(10),G(300))]=30;
            w.Routes[(G(40),G(400))]=20;w.Routes[(G(10),G(400))]=10;w.Routes[(G(30),G(400))]=30;
            var o=new Optimizer(w);int day=0;
            for(;day<30&&o.State.Rotations==0;day++){o.EnqueueDay(new[]{G(1)});Drain(o);}
            Check(o.State.Rotations>0&&w.Total()==190,"Later seed starved");
            Console.WriteLine($"Late-seed fixture: cost 220 -> {w.Total()}, found within {day} daily passes.");
        });
        Test("saved extended cursor skips a large processed resident prefix",()=>{
            var(w,o)=Cycle();
            var many=new List<Guid>();for(int i=1000;i<1400;i++)many.Add(G(i));many.Add(G(2000));
            // Resume after 400 previously scored representatives. The next one forms the cycle.
            w.People[G(2000)]=w.People[G(3)];w.People[G(2000)].Id=G(2000);w.People.Remove(G(3));
            o.State.Groups[G(12)]=new ResidentGroups{Day=o.State.Day,Representatives=many};
            o.State.Extensions[G(1)]=new ExtensionCursor{Home=G(12),Seed=0,Resident=G(1399),SeedStarted=true};
            Drain(o);Check(o.State.Rotations==1,"Resident prefix consumed every extension budget");
        });
        Test("cold resident preparation larger than extension budget eventually progresses",()=>{
            var(w,o)=Setup();Partner(w,5,100);w.Homes[G(30)]=new Home{Capacity=600,AdultLimit=600};
            w.Routes[(G(30),G(100))]=30;w.Routes[(G(30),G(200))]=5;
            for(int p=1000;p<1600;p++){
                AddAdult(w,p,30);w.People[G(p)].Work=G(2000+p);
                w.Routes[(G(30),G(2000+p))]=5;w.Routes[(G(10),G(2000+p))]=p==1599?5:100;
            }
            for(int day=0;day<90&&o.State.Rotations==0;day++){if(day>0)o.EnqueueDay(new[]{G(1)});Drain(o);}
            Check(o.State.Rotations>0,"Cold group/late resident starved");
        });
        Test("catalog serialization preserves canonical membership after edits",()=>{
            var c=new HousingCatalog();
            for(int p=10;p>=1;p--)c.SetPerson(new Person{Id=G(p),Home=G(20),District=G(999),Adult=true});
            c.SetHome(G(20),G(999));c.SetHome(G(30),G(999));
            var peer=JsonConvert.DeserializeObject<HousingCatalog>(JsonConvert.SerializeObject(c))!;
            foreach(var x in new[]{c,peer}){
                x.RemovePerson(G(4));x.SetPerson(new Person{Id=G(4),Home=G(30),District=G(999),Adult=true});
                x.RemoveHome(G(20));x.SetHome(G(20),G(998));
            }
            Check(JsonConvert.SerializeObject(c)==JsonConvert.SerializeObject(peer),"Catalog restore ordering differs");
        });
        Test("actual schema 2 JSON with old partial groups migrates safely",()=>{
            string json="{\"Schema\":2,\"Queue\":[\""+G(1)+"\"],\"Next\":1,\"Active\":{\"Actor\":{\"Id\":\""+G(1)+"\"},\"Homes\":[],\"BuildingGroups\":{\"Members\":[],\"Next\":0},\"Phase\":2},\"Groups\":{\""+G(20)+"\":{\"Members\":[],\"Next\":0,\"Representatives\":[]}},\"ExtensionOffsets\":{},\"WatchersInitialized\":true}";
            var(w,_)=Setup();var o=new Optimizer(w,JsonConvert.DeserializeObject<OptimizerState>(json));
            Check(o.State.Active==null&&o.State.Groups.Count==0&&o.State.Queued.Contains(G(1)),"Legacy fields leaked");
            Drain(o);Check(o.State.Moves==1,"Legacy active actor lost");
        });
        Test("catalog cursors survive removal, insertion and reverse registration order",()=>{
            var c=new HousingCatalog();
            foreach(int p in new[]{5,3,1})c.SetPerson(new Person{Id=G(p),Home=G(10),District=G(999),Adult=true});
            Check(HousingCatalog.Next(c.PeopleIds,Guid.Empty,out var first)&&first==G(1),"Unsorted catalog");
            c.RemovePerson(G(1));c.SetPerson(new Person{Id=G(2),Home=G(20),District=G(998),Adult=true});
            Check(HousingCatalog.Next(c.PeopleIds,G(1),out var next)&&next==G(2),"Removed cursor skipped next ID");
            Check(HousingCatalog.Next(c.HomeAdults,G(10),Guid.Empty,out next)&&next==G(3),"Old resident retained");
            c.SetPerson(new Person{Id=G(3),Home=G(20),District=G(998),Adult=true});
            Check(c.HomeAdults[G(10)].SequenceEqual(new[]{G(5)})&&c.DistrictPeople[G(998)].SequenceEqual(new[]{G(2),G(3)}),"Migration left stale indexes");
        });
        Test("deleted person removes saved queue and observer-related work",()=>{
            var(w,o)=Setup(20,30);Drain(o);o.State.Extensions[G(1)]=new ExtensionCursor();
            o.Enqueue(G(1),true);o.ForgetPerson(G(1));
            Check(!o.State.Priority.Contains(G(1))&&!o.State.Resting.ContainsKey(G(1))&&!o.State.Extensions.ContainsKey(G(1)),"Dead IDs retained");
        });
        Test("incremental daily preparation and targeted events replay identically after reload",()=>{
            var(w,a)=Setup();AddDistantHouses(w,80);Partner(w,5,40);a.StartDay();
            for(int tick=0;tick<250;tick++){
                if(tick==3)a.MarkHousingChanged(G(998),G(70));
                if(tick==12)a.MarkHousingChanged(G(999),G(20),G(2));
                var peer=w.Copy();var b=Reload(peer,a);
                w.PreparationCalls=0;w.Calls=0;a.Tick();b.Tick();
                Check(w.PreparationCalls<=16&&w.Calls<=96,"Budget exceeded");
                Check(JsonConvert.SerializeObject(a.State)==JsonConvert.SerializeObject(b.State),"Saved cursor or queue diverged at tick "+tick);
                Check(string.Join(";",w.Trace)==string.Join(";",peer.Trace),"Replay action diverged");
                if(!a.HasPendingWork)break;
            }
            Check(!a.HasPendingWork,"Replay fixture failed to finish");
        });
        Test("schema 2 migration retains backlog and requeues active actor",()=>{
            var(w,o)=Setup();var old=new OptimizerState{Schema=2,Queue=new List<Guid>{G(1),G(2)},Next=1,
                Active=new Search{Actor=w.GetPerson(G(1)),Phase=2}};
            old.WatchersInitialized=true;var upgraded=new Optimizer(w,old);
            Check(upgraded.State.Schema==3&&!upgraded.State.WatchersInitialized&&upgraded.State.Queued.Contains(G(1)),"Migration lost actor/catalog rebuild");
            Drain(upgraded);Check(upgraded.State.Moves==1,"Migrated actor did not resume");
        });
    }
}




