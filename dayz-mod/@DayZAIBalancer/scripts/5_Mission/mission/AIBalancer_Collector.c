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
            sb = sb + "{";
            sb = sb + "\"className\":\"" + EscapeJson(b.className) + "\"";
            sb = sb + ",\"spawnedCount\":" + b.spawnedCount;
            sb = sb + ",\"nominal\":0,\"min\":0,\"lifetime\":0,\"cost\":0";
            sb = sb + ",\"category\":null,\"usages\":[],\"values\":[]";
            sb = sb + ",\"flags\":{";
            sb = sb + "\"crafted\":false";
            sb = sb + ",\"deloot\":false";
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
        first = true;
        for (int z = 0; z < zombies.Count(); z++)
        {
            if (!first) sb = sb + ",";
            first = false;
            sb = sb + "{\"eventName\":\"" + EscapeJson(zombies.GetKey(z)) + "\",\"alive\":" + zombies.GetElement(z) + ",\"nominal\":0}";
        }
        sb = sb + "]";
        sb = sb + "}";

        sb = sb + "}";
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
