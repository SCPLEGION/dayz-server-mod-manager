class AIBalancer_ItemBucket
{
    string className;
    int spawnedCount;
    int countInMap;
    int countInCargo;
    int countInPlayer;
}

class AIBalancer_Collector
{
    /// <summary>Walks live entities, groups them by class, and serializes a snapshot to JSON.</summary>
    static string BuildSnapshotJson()
    {
        if (!GetGame().IsServer()) return "";

        map<string, ref AIBalancer_ItemBucket> items = new map<string, ref AIBalancer_ItemBucket>();
        map<string, int> zombies = new map<string, int>();
        int zombieTotal = 0;
        map<string, int> animals = new map<string, int>();
        int animalTotal = 0;

        array<Object> objs = new array<Object>();
        GetGame().GetObjectsAtPosition3D(vector.Zero, 100000.0, objs, null);

        for (int i = 0; i < objs.Count(); i++)
        {
            Object o = objs.Get(i);
            if (!o) continue;

            EntityAI ent = EntityAI.Cast(o);
            if (!ent) continue;

            string type = ent.GetType();
            if (type == "") continue;

            // Loot items
            if (ent.IsInherited(ItemBase))
            {
                AIBalancer_ItemBucket bucket;
                if (!items.Find(type, bucket))
                {
                    bucket = new AIBalancer_ItemBucket();
                    bucket.className = type;
                    items.Insert(type, bucket);
                }
                bucket.spawnedCount++;

                EntityAI parent = ent.GetHierarchyParent();
                if (!parent)               bucket.countInMap++;
                else if (parent.IsInherited(PlayerBase)) bucket.countInPlayer++;
                else                        bucket.countInCargo++;
                continue;
            }

            // Zombies
            if (ent.IsInherited(ZombieBase))
            {
                int prior = 0;
                zombies.Find(type, prior);
                zombies.Set(type, prior + 1);
                zombieTotal++;
                continue;
            }

            // Animals
            if (ent.IsInherited(AnimalBase))
            {
                int priorA = 0;
                animals.Find(type, priorA);
                animals.Set(type, priorA + 1);
                animalTotal++;
                continue;
            }
        }

        int playersOnline = 0;
        array<Man> players = new array<Man>();
        GetGame().GetPlayers(players);
        if (players) playersOnline = players.Count();

        int playersMax = GetGame().ServerConfigGetInt("maxPlayers");
        if (playersMax <= 0) playersMax = 60;

        int uptime = GetGame().GetTickTime();

        // ---- Build JSON ----
        string sb = "";
        sb = sb + "{";
        sb = sb + "\"timestamp\":" + GetUnixTime();
        sb = sb + ",\"playersOnline\":" + playersOnline;
        sb = sb + ",\"playersMax\":" + playersMax;
        sb = sb + ",\"serverUptime\":" + uptime;

        sb = sb + ",\"items\":[";
        bool first = true;
        for (int k = 0; k < items.Count(); k++)
        {
            AIBalancer_ItemBucket b = items.GetElement(k);
            if (!first) sb = sb + ",";
            first = false;

            AIBalancer_TypeConfig typeCfg;
            bool hasCfg = AIBalancer_EconomyConfig.FindType(b.className, typeCfg);

            sb = sb + "{";
            sb = sb + "\"className\":\"" + EscapeJson(b.className) + "\"";
            sb = sb + ",\"spawnedCount\":" + b.spawnedCount;
            if (hasCfg)
            {
                sb = sb + ",\"nominal\":" + typeCfg.nominal;
                sb = sb + ",\"min\":" + typeCfg.min;
                sb = sb + ",\"lifetime\":" + typeCfg.lifetime;
                sb = sb + ",\"cost\":" + typeCfg.cost;
                if (typeCfg.category != "")
                    sb = sb + ",\"category\":\"" + EscapeJson(typeCfg.category) + "\"";
                else
                    sb = sb + ",\"category\":null";
                sb = sb + ",\"usages\":" + StringArrayToJson(typeCfg.usages);
                sb = sb + ",\"values\":" + StringArrayToJson(typeCfg.values);
            }
            else
            {
                sb = sb + ",\"nominal\":0,\"min\":0,\"lifetime\":0,\"cost\":0";
                sb = sb + ",\"category\":null,\"usages\":[],\"values\":[]";
            }
            sb = sb + ",\"flags\":{";
            sb = sb + "\"crafted\":" + ((hasCfg && typeCfg.crafted) ? "true" : "false");
            sb = sb + ",\"deloot\":" + ((hasCfg && typeCfg.deloot) ? "true" : "false");
            sb = sb + ",\"countInMap\":" + (b.countInMap > 0 ? "true" : "false");
            sb = sb + ",\"countInCargo\":" + (b.countInCargo > 0 ? "true" : "false");
            sb = sb + ",\"countInHoarder\":false";
            sb = sb + ",\"countInPlayer\":" + (b.countInPlayer > 0 ? "true" : "false");
            sb = sb + "}";
            sb = sb + "}";
        }
        sb = sb + "]";

        sb = sb + ",\"zombies\":{";
        sb = sb + "\"totalAlive\":" + zombieTotal;
        sb = sb + ",\"totalMax\":0";
        sb = sb + ",\"typeBreakdown\":[";
        sb = sb + SpawnBreakdownToJson(zombies);
        sb = sb + "]";
        sb = sb + "}";

        sb = sb + ",\"animals\":{";
        sb = sb + "\"totalAlive\":" + animalTotal;
        sb = sb + ",\"typeBreakdown\":[";
        sb = sb + SpawnBreakdownToJson(animals);
        sb = sb + "]";
        sb = sb + "}";

        sb = sb + "}";
        return sb;
    }

    /// <summary>Serializes a className->aliveCount map into events.xml-aware typeBreakdown JSON.</summary>
    private static string SpawnBreakdownToJson(map<string, int> aliveByClass)
    {
        string sb = "";
        bool first = true;
        for (int z = 0; z < aliveByClass.Count(); z++)
        {
            if (!first) sb = sb + ",";
            first = false;

            string className = aliveByClass.GetKey(z);
            int alive = aliveByClass.GetElement(z);

            AIBalancer_EventChildConfig evtCfg;
            bool hasEvt = AIBalancer_EconomyConfig.FindEventChild(className, evtCfg);

            string eventName = className; // fallback when no event match is found
            int nominal = 0;
            int nMin = 0;
            int nMax = 0;
            int lifetime = 0;
            if (hasEvt)
            {
                if (evtCfg.eventName != "") eventName = evtCfg.eventName;
                nominal  = evtCfg.nominal;
                nMin     = evtCfg.min;
                nMax     = evtCfg.max;
                lifetime = evtCfg.lifetime;
            }

            sb = sb + "{\"eventName\":\"" + EscapeJson(eventName) + "\"";
            sb = sb + ",\"className\":\"" + EscapeJson(className) + "\"";
            sb = sb + ",\"alive\":" + alive;
            sb = sb + ",\"nominal\":" + nominal;
            sb = sb + ",\"min\":" + nMin;
            sb = sb + ",\"max\":" + nMax;
            sb = sb + ",\"lifetime\":" + lifetime;
            sb = sb + "}";
        }
        return sb;
    }

    private static string StringArrayToJson(array<string> arr)
    {
        string sb = "[";
        for (int i = 0; i < arr.Count(); i++)
        {
            if (i > 0) sb = sb + ",";
            sb = sb + "\"" + EscapeJson(arr.Get(i)) + "\"";
        }
        sb = sb + "]";
        return sb;
    }

    private static int GetUnixTime()
    {
        int year, month, day, hour, minute, second;
        GetYearMonthDay(year, month, day);
        GetHourMinuteSecond(hour, minute, second);
        // Coarse approximation — engine has no direct unix-time API.
        // (year - 1970) * 31_557_600 + ... — server-side timestamps are mainly informational.
        int yrs = year - 1970;
        return yrs * 31557600 + month * 2629800 + day * 86400 + hour * 3600 + minute * 60 + second;
    }

    private static string EscapeJson(string s)
    {
        string r = "";
        for (int i = 0; i < s.Length(); i++)
        {
            string c = s.Get(i);
            if (c == "\\")      r = r + "\\\\";
            else if (c == "\"") r = r + "\\\"";
            else if (c == "\n") r = r + "\\n";
            else if (c == "\r") r = r + "\\r";
            else if (c == "\t") r = r + "\\t";
            else                r = r + c;
        }
        return r;
    }
}
