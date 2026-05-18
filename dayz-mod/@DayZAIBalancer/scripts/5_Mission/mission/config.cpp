class CfgPatches
{
    class DayZAIBalancer
    {
        units[] = {};
        weapons[] = {};
        requiredVersion = 0.1;
        requiredAddons[] = { "DZ_Data", "DZ_Scripts" };
    };
};

class CfgMods
{
    class DayZAIBalancer
    {
        dir = "DayZAIBalancer";
        picture = "";
        action = "";
        hideName = 0;
        hidePicture = 0;
        name = "DayZ AI Balancer";
        credits = "SCPLEGION";
        author = "SCPLEGION";
        authorID = "0";
        version = "1.0";
        extra = 0;
        type = "mod";
        dependencies[] = { "Game", "World", "Mission" };
        class defs
        {
            class engineScriptModule  { files[] = {}; };
            class gameLibScriptModule { files[] = {}; };
            class gameScriptModule    { files[] = {}; };
            class worldScriptModule   { files[] = {}; };
            class missionScriptModule
            {
                value = "";
                files[] = { "DayZAIBalancer/scripts/5_Mission" };
            };
        };
    };
};
