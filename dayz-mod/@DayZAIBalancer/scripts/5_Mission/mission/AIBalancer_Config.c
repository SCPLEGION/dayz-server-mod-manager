class AIBalancerConfig
{
    static string ENDPOINT = "http://127.0.0.1:7823/api/ingest";
    static string SECRET = "CHANGE_ME_IN_CONFIG";
    static int INTERVAL_MINUTES = 10;
    static bool ENABLED = true;

    static string ConfigPath()
    {
        // Server profile / AIBalancer / config.json
        return "$profile:AIBalancer\\config.json";
    }

    static void Load()
    {
        string path = ConfigPath();

        if (!FileExist(path))
        {
            Print("[AIBalancer] No config at " + path + " — using defaults. Will create one.");
            Save();
            return;
        }

        FileHandle fh = OpenFile(path, FileMode.READ);
        if (fh == 0)
        {
            Print("[AIBalancer] Could not open " + path + " — using defaults.");
            return;
        }

        string content = "";
        string line;
        while (FGets(fh, line) >= 0)
            content = content + line;
        CloseFile(fh);

        // Naive key=value extraction — Enforce Script has no real JSON parser without CF.
        ENDPOINT          = ExtractString(content, "ENDPOINT", ENDPOINT);
        SECRET            = ExtractString(content, "SECRET", SECRET);
        INTERVAL_MINUTES  = ExtractInt(content, "INTERVAL_MINUTES", INTERVAL_MINUTES);
        ENABLED           = ExtractBool(content, "ENABLED", ENABLED);

        Print("[AIBalancer] Config loaded: endpoint=" + ENDPOINT + ", interval=" + INTERVAL_MINUTES + "min, enabled=" + ENABLED);
    }

    static void Save()
    {
        string path = ConfigPath();
        MakeDirectory("$profile:AIBalancer");
        FileHandle fh = OpenFile(path, FileMode.WRITE);
        if (fh == 0) return;
        FPrintln(fh, "{");
        FPrintln(fh, "  \"ENDPOINT\": \"" + ENDPOINT + "\",");
        FPrintln(fh, "  \"SECRET\": \"" + SECRET + "\",");
        FPrintln(fh, "  \"INTERVAL_MINUTES\": " + INTERVAL_MINUTES + ",");
        FPrintln(fh, "  \"ENABLED\": " + (ENABLED ? "true" : "false"));
        FPrintln(fh, "}");
        CloseFile(fh);
    }

    private static string ExtractString(string body, string key, string def)
    {
        // search for "key" : "value"
        int idx = body.IndexOf("\"" + key + "\"");
        if (idx < 0) return def;
        int colon = body.IndexOfFrom(idx, ":");
        if (colon < 0) return def;
        int q1 = body.IndexOfFrom(colon, "\"");
        if (q1 < 0) return def;
        int q2 = body.IndexOfFrom(q1 + 1, "\"");
        if (q2 < 0) return def;
        return body.Substring(q1 + 1, q2 - q1 - 1);
    }

    private static int ExtractInt(string body, string key, int def)
    {
        int idx = body.IndexOf("\"" + key + "\"");
        if (idx < 0) return def;
        int colon = body.IndexOfFrom(idx, ":");
        if (colon < 0) return def;
        string tail = body.Substring(colon + 1, body.Length() - colon - 1);
        string num = "";
        for (int i = 0; i < tail.Length(); i++)
        {
            string ch = tail.Get(i);
            if (ch == " " || ch == "\t" || ch == "\n" || ch == "\r") continue;
            if (ch == "-" || (ch.ToInt() >= 0 && ch.ToInt() <= 9) || (ch != "," && ch != "}" && ch != "\""))
            {
                int code = ch.Hash();
                // very permissive — just stop on comma / brace / quote
                if (ch == "," || ch == "}" || ch == "\"") break;
                num = num + ch;
            }
            else break;
        }
        num = num.Trim();
        if (num == "") return def;
        return num.ToInt();
    }

    private static bool ExtractBool(string body, string key, bool def)
    {
        int idx = body.IndexOf("\"" + key + "\"");
        if (idx < 0) return def;
        int colon = body.IndexOfFrom(idx, ":");
        if (colon < 0) return def;
        string tail = body.Substring(colon + 1, body.Length() - colon - 1);
        tail = tail.Trim();
        if (tail.IndexOf("true") == 0) return true;
        if (tail.IndexOf("false") == 0) return false;
        return def;
    }
}
