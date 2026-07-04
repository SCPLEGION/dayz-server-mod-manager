class AIBalancer_TypeConfig
{
    string className;
    int nominal;
    int min;
    int lifetime;
    int cost;
    string category;
    ref array<string> usages;
    ref array<string> values;
    bool crafted;
    bool deloot;

    void AIBalancer_TypeConfig()
    {
        category = "";
        usages = new array<string>();
        values = new array<string>();
    }
}

class AIBalancer_EventChildConfig
{
    string eventName;
    string childClassName;
    int nominal;
    int min;
    int max;
    int lifetime;
}

/// <summary>
/// Best-effort tag-scanning reader for the mission's already-merged db/types.xml and
/// db/events.xml (Enforce Script has no XML DOM without Community Framework/Expansion). Gives
/// the AI Balancer real economy values instead of the zeros it used to always report.
/// Malformed/unexpected entries are skipped individually rather than aborting the whole load.
/// </summary>
class AIBalancer_EconomyConfig
{
    private static ref map<string, ref AIBalancer_TypeConfig> s_TypesByName;
    private static ref map<string, ref AIBalancer_EventChildConfig> s_EventChildByClassName;
    private static bool s_Loaded;

    static void EnsureLoaded()
    {
        if (s_Loaded) return;
        s_Loaded = true;

        s_TypesByName = new map<string, ref AIBalancer_TypeConfig>();
        s_EventChildByClassName = new map<string, ref AIBalancer_EventChildConfig>();

        LoadTypesXml("$mission:db/types.xml");
        LoadEventsXml("$mission:db/events.xml");

        Print("[AIBalancer] Economy config loaded: " + s_TypesByName.Count() + " types, " + s_EventChildByClassName.Count() + " event-linked classes.");
    }

    static bool FindType(string className, out AIBalancer_TypeConfig cfg)
    {
        EnsureLoaded();
        return s_TypesByName.Find(className, cfg);
    }

    static bool FindEventChild(string className, out AIBalancer_EventChildConfig cfg)
    {
        EnsureLoaded();
        return s_EventChildByClassName.Find(className, cfg);
    }

    private static string ReadWholeFile(string path)
    {
        if (!FileExist(path)) return "";
        FileHandle fh = OpenFile(path, FileMode.READ);
        if (fh == 0) return "";

        string all = "";
        string line;
        while (FGets(fh, line) >= 0)
            all = all + line + "\n";
        CloseFile(fh);
        return all;
    }

    private static string ExtractAttrValue(string chunk, string attr, string def)
    {
        string needle = attr + "=\"";
        int idx = chunk.IndexOf(needle);
        if (idx < 0) return def;
        int start = idx + needle.Length();
        int end = chunk.IndexOfFrom(start, "\"");
        if (end < 0) return def;
        return chunk.Substring(start, end - start);
    }

    private static string ExtractFirstAttrValue(string body, string tagName, string attr, string def)
    {
        string needle = "<" + tagName + " ";
        int idx = body.IndexOf(needle);
        if (idx < 0) return def;
        int closeIdx = body.IndexOfFrom(idx, ">");
        if (closeIdx < 0) return def;
        string chunk = body.Substring(idx, closeIdx - idx + 1);
        return ExtractAttrValue(chunk, attr, def);
    }

    private static array<string> ExtractAllAttrValues(string body, string tagName, string attr)
    {
        array<string> results = new array<string>();
        string needle = "<" + tagName + " ";
        int searchFrom = 0;

        while (true)
        {
            int idx = body.IndexOfFrom(searchFrom, needle);
            if (idx < 0) break;
            int closeIdx = body.IndexOfFrom(idx, ">");
            if (closeIdx < 0) break;

            string chunk = body.Substring(idx, closeIdx - idx + 1);
            string val = ExtractAttrValue(chunk, attr, "");
            if (val != "") results.Insert(val);

            searchFrom = closeIdx + 1;
        }

        return results;
    }

    private static string ExtractTagValue(string body, string tag, string def)
    {
        string openTag = "<" + tag + ">";
        int idx = body.IndexOf(openTag);
        if (idx < 0) return def;
        int start = idx + openTag.Length();
        int end = body.IndexOfFrom(start, "</" + tag + ">");
        if (end < 0) return def;
        return body.Substring(start, end - start);
    }

    private static int ExtractTagInt(string body, string tag, int def)
    {
        string v = ExtractTagValue(body, tag, "");
        v = v.Trim();
        if (v == "") return def;
        return v.ToInt();
    }

    private static void LoadTypesXml(string path)
    {
        string all = ReadWholeFile(path);
        if (all == "") return;

        int searchFrom = 0;
        while (true)
        {
            int typeIdx = all.IndexOfFrom(searchFrom, "<type ");
            if (typeIdx < 0) break;

            int tagCloseIdx = all.IndexOfFrom(typeIdx, ">");
            if (tagCloseIdx < 0) break;

            int endIdx = all.IndexOfFrom(tagCloseIdx, "</type>");
            if (endIdx < 0) break;

            string openTagChunk = all.Substring(typeIdx, tagCloseIdx - typeIdx + 1);
            string inner = all.Substring(tagCloseIdx + 1, endIdx - tagCloseIdx - 1);

            string name = ExtractAttrValue(openTagChunk, "name", "");
            if (name != "")
            {
                AIBalancer_TypeConfig cfg = new AIBalancer_TypeConfig();
                cfg.className = name;
                cfg.nominal   = ExtractTagInt(inner, "nominal", 0);
                cfg.min       = ExtractTagInt(inner, "min", 0);
                cfg.lifetime  = ExtractTagInt(inner, "lifetime", 0);
                cfg.cost      = ExtractTagInt(inner, "cost", 0);
                cfg.category  = ExtractFirstAttrValue(inner, "category", "name", "");
                cfg.usages    = ExtractAllAttrValues(inner, "usage", "name");
                cfg.values    = ExtractAllAttrValues(inner, "value", "name");
                cfg.crafted   = inner.IndexOf("<crafted>1</crafted>") >= 0;
                cfg.deloot    = inner.IndexOf("<deloot>1</deloot>") >= 0;

                s_TypesByName.Set(name, cfg);
            }

            searchFrom = endIdx + "</type>".Length();
        }
    }

    private static void LoadEventsXml(string path)
    {
        string all = ReadWholeFile(path);
        if (all == "") return;

        int searchFrom = 0;
        while (true)
        {
            int eventIdx = all.IndexOfFrom(searchFrom, "<event ");
            if (eventIdx < 0) break;

            int tagCloseIdx = all.IndexOfFrom(eventIdx, ">");
            if (tagCloseIdx < 0) break;

            int endIdx = all.IndexOfFrom(tagCloseIdx, "</event>");
            if (endIdx < 0) break;

            string openTagChunk = all.Substring(eventIdx, tagCloseIdx - eventIdx + 1);
            string inner = all.Substring(tagCloseIdx + 1, endIdx - tagCloseIdx - 1);

            string eventName = ExtractAttrValue(openTagChunk, "name", "");
            if (eventName != "")
            {
                int nominal  = ExtractTagInt(inner, "nominal", 0);
                int min      = ExtractTagInt(inner, "min", 0);
                int max      = ExtractTagInt(inner, "max", 0);
                int lifetime = ExtractTagInt(inner, "lifetime", 0);

                array<string> childTypes = ExtractAllAttrValues(inner, "child", "type");
                for (int c = 0; c < childTypes.Count(); c++)
                {
                    string childClass = childTypes.Get(c);

                    // First event a class is seen under wins - a class rarely spawns under more
                    // than one event, and this keeps lookups (and later apply-back) unambiguous.
                    AIBalancer_EventChildConfig existing;
                    if (s_EventChildByClassName.Find(childClass, existing)) continue;

                    AIBalancer_EventChildConfig ecfg = new AIBalancer_EventChildConfig();
                    ecfg.eventName = eventName;
                    ecfg.childClassName = childClass;
                    ecfg.nominal = nominal;
                    ecfg.min = min;
                    ecfg.max = max;
                    ecfg.lifetime = lifetime;

                    s_EventChildByClassName.Set(childClass, ecfg);
                }
            }

            searchFrom = endIdx + "</event>".Length();
        }
    }
}
