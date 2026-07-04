using System.Collections.Generic;

namespace DayZModManager.Services;

/// <summary>
/// Tracks when each Workshop mod was last successfully deployed to the server root, backed by
/// the <c>mod_deploy_state</c> table. Used to detect when a newer Workshop version is available.
/// </summary>
internal static class ModDeployStateStore
{
    public static void RecordDeployed(IEnumerable<ulong> ids, long deployedTimeUnix)
    {
        using var conn = Database.Open();
        using var tx = conn.BeginTransaction();
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = @"INSERT INTO mod_deploy_state (workshop_id, deployed_time_unix)
            VALUES ($id, $t)
            ON CONFLICT(workshop_id) DO UPDATE SET deployed_time_unix = excluded.deployed_time_unix";
        var pId = cmd.CreateParameter(); pId.ParameterName = "$id"; cmd.Parameters.Add(pId);
        var pT = cmd.CreateParameter(); pT.ParameterName = "$t"; cmd.Parameters.Add(pT);

        foreach (var id in ids)
        {
            pId.Value = (long)id;
            pT.Value = deployedTimeUnix;
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }

    public static Dictionary<ulong, long> LoadAll()
    {
        var map = new Dictionary<ulong, long>();
        using var conn = Database.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT workshop_id, deployed_time_unix FROM mod_deploy_state";
        using var rd = cmd.ExecuteReader();
        while (rd.Read())
            map[(ulong)rd.GetInt64(0)] = rd.GetInt64(1);
        return map;
    }
}
