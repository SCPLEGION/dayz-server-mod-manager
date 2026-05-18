// Callback receiver — registered via CF/Expansion HTTP layer.
class AIBalancer_HttpCallback : RestCallback
{
    override void OnSuccess(string data, int code)
    {
        Print("[AIBalancer] Snapshot sent OK (HTTP " + code + ")");
    }

    override void OnError(string data, int code)
    {
        Print("[AIBalancer] HTTP error: " + code + " " + data);
    }

    override void OnTimeout()
    {
        Print("[AIBalancer] HTTP timeout — is the manager listener running?");
    }
}

class AIBalancer_HttpSender
{
    static ref AIBalancer_HttpCallback s_cb;

    static void Send(string jsonBody)
    {
        if (jsonBody == "") return;

        // Prefer Expansion HTTP if available.
        if (TrySendExpansion(jsonBody))
            return;

        // Fall back to CF RestContext.
        if (TrySendCF(jsonBody))
            return;

        Print("[AIBalancer] No HTTP backend available. Install CF or Expansion to enable outbound HTTP.");
    }

    private static bool TrySendExpansion(string jsonBody)
    {
        // ExpansionHttpClient.Post — guarded by typename lookup so the compile doesn't hard-fail without Expansion.
        typename t = typename.EnumValuesStringToName("ExpansionHttpClient").ToType();
        if (!t) return false;

        // Reflection-style instantiation isn't really a thing in Enforce — keep a soft fallback.
        // (User can replace this with explicit Expansion calls if Expansion is guaranteed.)
        return false;
    }

    private static bool TrySendCF(string jsonBody)
    {
        // CF provides GetRestApi() globally.
        RestApi api = GetRestApi();
        if (!api) return false;

        if (!s_cb) s_cb = new AIBalancer_HttpCallback();

        RestContext ctx = api.GetRestContext(AIBalancerConfig.ENDPOINT);
        if (!ctx) return false;

        ctx.SetHeader("Content-Type: application/json");
        ctx.SetHeader("Authorization: Bearer " + AIBalancerConfig.SECRET);
        ctx.POST(s_cb, "", jsonBody);
        return true;
    }
}
