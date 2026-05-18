// Hook into the server gamemode. Reuses the existing CustomMission* if any.
modded class MissionServer
{
    override void OnInit()
    {
        super.OnInit();
        AIBalancer_Main.Init();
    }
}

class AIBalancer_Main
{
    static bool s_Initialized;

    static void Init()
    {
        if (s_Initialized) return;
        s_Initialized = true;

        AIBalancerConfig.Load();

        if (!AIBalancerConfig.ENABLED)
        {
            Print("[AIBalancer] Disabled in config — skipping setup.");
            return;
        }

        Print("[AIBalancer] Scheduling first collection in 60 seconds.");
        GetGame().GetCallQueue(CALL_CATEGORY_SYSTEM).CallLater(CollectAndSend, 60 * 1000, false);
    }

    static void CollectAndSend()
    {
        if (!GetGame().IsServer())
            return;

        string json = AIBalancer_Collector.BuildSnapshotJson();
        if (json == "")
        {
            Print("[AIBalancer] Skipping send — empty snapshot.");
        }
        else
        {
            AIBalancer_HttpSender.Send(json);
        }

        int delayMs = AIBalancerConfig.INTERVAL_MINUTES * 60 * 1000;
        if (delayMs < 60000) delayMs = 60000;
        GetGame().GetCallQueue(CALL_CATEGORY_SYSTEM).CallLater(CollectAndSend, delayMs, false);
    }
}
