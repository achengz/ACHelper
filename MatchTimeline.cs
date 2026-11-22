using System.Text.Json;

namespace LolMatchAssistant;

internal static class MatchTimeline
{
    public static bool NeedsReview(Match match) => !match.HasTimelineReview && !match.EarlyEnd &&
        match.Duration.TotalMinutes >= 8 && match.Players.Length == 10 &&
        match.Queue is 400 or 420 or 430 or 440 or 450 or 480 or 490 or 2400 &&
        match.Players.Any(p => p.Combat?.Feats is not { EnemyTowerKills: not null, OwnTowerKills: not null, KillDeficit: not null });

    public static Match? Enrich(Match match, JsonElement timeline)
    {
        if (timeline.Field("json").ValueKind == JsonValueKind.Object) timeline = timeline.Field("json");
        if (timeline.Field("info").ValueKind == JsonValueKind.Object) timeline = timeline.Field("info");
        var frames = timeline.Field("frames").Items();
        if (frames.Length < 2) return null;
        var byPuuid = timeline.Field("participants").Items().Where(row => row.Text("puuid").Length > 0)
            .DistinctBy(row => row.Text("puuid")).ToDictionary(row => row.Text("puuid"), row => (int)row.Number("participantId"));
        int Slot(Participant player) => byPuuid.GetValueOrDefault(player.Player.Puuid, player.SlotId);
        var known = match.Players.Where(p => Slot(p) > 0 && p.Team is 100 or 200).ToArray();
        if (known.Length != 10 || known.Select(Slot).Distinct().Count() != 10) return null;
        var teamBySlot = known.ToDictionary(Slot, p => p.Team);
        if (teamBySlot.Values.Count(team => team == 100) != 5) return null;

        var events = frames.SelectMany(frame => frame.Field("events").Items()).ToArray();
        var towers = events.Where(entry => entry.Text("type") == "BUILDING_KILL" && entry.Text("buildingType") == "TOWER_BUILDING")
            .Select(entry => (Team: (int)entry.Number("teamId"), At: entry.Number("timestamp"), Point: Position(entry)))
            .Where(tower => tower.Team is 100 or 200 && tower.At > 0 && tower.Point.HasValue).ToArray();
        var deaths = events.Where(entry => entry.Text("type") == "CHAMPION_KILL").OrderBy(entry => entry.Number("timestamp")).ToArray();
        if (deaths.Length == 0) return null;
        var attacks = new Dictionary<int, long>();
        var defenses = new Dictionary<int, long>();
        var kills = new Dictionary<int, long> { [100] = 0, [200] = 0 };
        var killDeficit = new Dictionary<int, long> { [100] = 0, [200] = 0 };
        foreach (var death in deaths)
        {
            var killer = (int)death.Number("killerId");
            if (!teamBySlot.TryGetValue(killer, out var team) ||
                !teamBySlot.TryGetValue((int)death.Number("victimId"), out var enemy) || team == enemy) continue;
            kills[team]++;
            killDeficit[enemy] = Math.Max(killDeficit[enemy], kills[team] - kills[enemy]);
            if (Position(death) is not { } point) continue;
            var at = death.Number("timestamp");
            var nearby = towers.Where(tower => tower.At > at && Near(point, tower.Point!.Value)).ToArray();
            if (nearby.Any(tower => tower.Team == enemy)) attacks[killer] = attacks.GetValueOrDefault(killer) + 1;
            if (nearby.Any(tower => tower.Team == team)) defenses[killer] = defenses.GetValueOrDefault(killer) + 1;
        }

        foreach (var team in new[] { 100, 200 })
        {
            // Guard against attributing events from an incomplete or mismatched timeline.
            if (known.Where(p => p.Team == team).All(p => p.Combat?.Kills is >= 0) &&
                known.Where(p => p.Team == team).Sum(p => p.Combat!.Kills!.Value) != kills[team]) return null;
        }

        var goldDeficit = new Dictionary<int, long> { [100] = 0, [200] = 0 };
        foreach (var frame in frames)
        {
            var economy = frame.Field("participantFrames");
            var gold = teamBySlot.Keys.Select(slot => (Slot: slot, Gold: economy.Field(slot.ToString()).OptionalNumber("totalGold"))).ToArray();
            if (gold.Any(entry => entry.Gold is null or < 0)) continue;
            var delta = gold.Sum(entry => teamBySlot[entry.Slot] == 100 ? entry.Gold!.Value : -entry.Gold!.Value);
            goldDeficit[100] = Math.Max(goldDeficit[100], -delta);
            goldDeficit[200] = Math.Max(goldDeficit[200], delta);
        }
        return match with
        {
            HasTimelineReview = true,
            Players = match.Players.Select(player =>
            {
                if (player.Combat is not { Feats: { } feats } stats) return player;
                var slot = Slot(player);
                return player with { Combat = stats with { Feats = feats with
                {
                    EnemyTowerKills = feats.EnemyTowerKills ?? attacks.GetValueOrDefault(slot),
                    OwnTowerKills = feats.OwnTowerKills ?? defenses.GetValueOrDefault(slot),
                    KillDeficit = feats.KillDeficit ?? killDeficit[player.Team],
                    GoldDeficit = goldDeficit[player.Team]
                } } };
            }).ToArray()
        };
    }

    private static (long X, long Y)? Position(JsonElement entry)
    {
        var point = entry.Field("position");
        return point.OptionalNumber("x") is { } x && point.OptionalNumber("y") is { } y ? (x, y) : null;
    }

    private static bool Near((long X, long Y) point, (long X, long Y) tower)
    {
        var x = (double)point.X - tower.X;
        var y = (double)point.Y - tower.Y;
        return x * x + y * y <= 800 * 800;
    }
}
