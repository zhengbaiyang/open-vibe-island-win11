using System.Text.Json;
using OpenIsland.Core.Models;

namespace OpenIsland.Core.State;

public static class SessionPersistence
{
    public static List<AgentSession> Load()
    {
        try
        {
            if (!File.Exists(BridgeConstants.SessionsCachePath)) return new List<AgentSession>();
            var json = File.ReadAllText(BridgeConstants.SessionsCachePath);
            return JsonSerializer.Deserialize<List<AgentSession>>(json, BridgeConstants.JsonOptions)
                   ?? new List<AgentSession>();
        }
        catch
        {
            return new List<AgentSession>();
        }
    }

    public static void Save(IEnumerable<AgentSession> sessions)
    {
        try
        {
            Directory.CreateDirectory(BridgeConstants.AppDataDir);
            var list = sessions.Where(s => s.Phase != SessionPhase.Ended).Take(50).ToList();
            var json = JsonSerializer.Serialize(list, new JsonSerializerOptions { WriteIndented = true });
            var tmp = BridgeConstants.SessionsCachePath + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, BridgeConstants.SessionsCachePath, overwrite: true);
        }
        catch
        {
            // Best-effort persistence
        }
    }
}
