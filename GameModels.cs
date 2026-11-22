using System.Globalization;
using System.Text.Json;

namespace LolMatchAssistant;

internal static class JsonFields
{
    public static JsonElement Field(this JsonElement value, string name) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var found) ? found : default;

    public static string Text(this JsonElement value, string name)
    {
        var field = value.Field(name);
        return field.ValueKind is JsonValueKind.String or JsonValueKind.Number ? field.ToString() : "";
    }

    public static long Number(this JsonElement value, string name) =>
        value.OptionalNumber(name) ?? 0;

    public static long? OptionalNumber(this JsonElement value, string name) =>
        long.TryParse(value.Text(name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var result) ? result : null;

    public static bool Flag(this JsonElement value, string name) => value.Field(name).ValueKind == JsonValueKind.True;
    public static bool? Outcome(this JsonElement value) => value.Text("win").ToLowerInvariant() switch
    {
        "true" or "win" or "victory" or "1" => true,
        "false" or "fail" or "loss" or "0" => false,
        _ => value.Field("win").ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null
        }
    };
    public static JsonElement[] Items(this JsonElement value) =>
        value.ValueKind == JsonValueKind.Array ? value.EnumerateArray().ToArray() : [];
    public static string First(params string[] values) => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? "";
}

internal record Player(string Puuid, long Id, string Name, int IconId = -1, long Level = 0)
{
    public bool HasLookupIdentity => Puuid.Length > 0 || Id > 0 || Name.Length > 0;
    public bool HasRiotId => Name.LastIndexOf('#') is var separator && separator > 0 && separator < Name.Length - 1;

    public static Player Read(JsonElement value)
    {
        var name = JsonFields.First(value.Text("gameName"), value.Text("riotIdGameName"), value.Text("riotIdName"),
            value.Field("riotId").Text("gameName"), value.Text("displayName"), value.Text("summonerName"), value.Text("name"));
        var tag = JsonFields.First(value.Text("tagLine"), value.Text("riotIdTagline"), value.Text("riotIdTagLine"), value.Field("riotId").Text("tagLine"));
        var riotId = name.Length > 0 && tag.Length > 0 && !name.Contains('#') ? $"{name}#{tag}" : name;
        var fullId = value.Text("riotId");
        return new(value.Text("puuid"), value.Number("summonerId"),
            fullId.Contains('#') ? fullId : JsonFields.First(riotId, fullId),
            (int)(value.OptionalNumber("profileIconId") ?? -1), value.Number("summonerLevel"));
    }
}

internal record Participant(Player Player, int Champion, int Team, bool Won, string Kda, long Cs, long Damage, string Items,
    CombatStats? Combat = null, MatchLoadout? Loadout = null, bool OutcomeKnown = true, int SlotId = 0);

internal record MatchLoadout(int Level, int[] Spells, int[] Runes, int[] Augments, int SecondaryStyle = 0)
{
    public static MatchLoadout Read(JsonElement entry, JsonElement stats)
    {
        var spellIds = new[] { (int)(entry.OptionalNumber("summoner1Id") ?? entry.Number("spell1Id")),
            (int)(entry.OptionalNumber("summoner2Id") ?? entry.Number("spell2Id")) };
        var perks = entry.Field("perks");
        var styles = perks.Field("styles").Items();
        var runes = styles.SelectMany(style => style.Field("selections").Items())
            .Select(perk => (int)perk.Number("perk")).Where(id => id > 0).ToArray();
        if (runes.Length == 0) runes = Enumerable.Range(0, 6).Select(index => (int)stats.Number($"perk{index}"))
            .Where(id => id > 0).ToArray();
        var augments = Enumerable.Range(1, 6).Select(index => (int)stats.Number($"playerAugment{index}")).Where(id => id > 0).ToArray();
        var secondary = styles.FirstOrDefault(style => style.Text("description") == "subStyle").Number("style");
        if (secondary == 0 && styles.Length >= 2) secondary = styles[1].Number("style");
        if (secondary == 0) secondary = stats.Number("perkSubStyle");
        var shards = perks.Field("statPerks");
        var shardIds = new[] { shards.Number("offense"), shards.Number("flex"), shards.Number("defense") };
        if (shardIds.All(id => id == 0))
            shardIds = Enumerable.Range(0, 3).Select(index => stats.Number($"statPerk{index}")).ToArray();
        return new((int)stats.Number("champLevel"), spellIds.Where(id => id > 0).ToArray(),
            [.. runes, .. shardIds.Where(id => id > 0).Select(id => (int)id)], augments, (int)secondary);
    }
}

internal record TeamObjective(int Team, long? Towers, long? Dragons, long? Barons, long? Heralds, long? Inhibitors, long? Grubs)
{
    public static TeamObjective Read(JsonElement row)
    {
        var objectives = row.Field("objectives");
        long? Kills(string name, string legacy) => objectives.Field(name).OptionalNumber("kills") ?? row.OptionalNumber(legacy);
        return new((int)row.Number("teamId"), Kills("tower", "towerKills"), Kills("dragon", "dragonKills"),
            Kills("baron", "baronKills"), Kills("riftHerald", "riftHeraldKills"),
            Kills("inhibitor", "inhibitorKills"), Kills("horde", "hordeKills"));
    }
}

internal record CombatStats(long? Kills, long? Deaths, long? Assists, long? ChampionDamage, long? Minions,
    long? Control, long? Shield, long? Heal, long? Turrets, long? TurretDamage, long? Vision,
    long? DamageTaken = null, long? Gold = null, long? SoloKills = null, long? MissingPings = null, long? TotalHealing = null,
    MatchFeats? Feats = null)
{
    public static CombatStats Read(JsonElement stats, JsonElement entry = default) => new(stats.OptionalNumber("kills"), stats.OptionalNumber("deaths"),
        stats.OptionalNumber("assists"), stats.OptionalNumber("totalDamageDealtToChampions"), stats.OptionalNumber("totalMinionsKilled"),
        stats.OptionalNumber("timeCCingOthers"), stats.OptionalNumber("totalDamageShieldedOnTeammates"),
        stats.OptionalNumber("totalHealsOnTeammates"), stats.OptionalNumber("turretKills"),
        stats.OptionalNumber("damageDealtToTurrets"), stats.OptionalNumber("visionScore"),
        stats.OptionalNumber("totalDamageTaken"), stats.OptionalNumber("goldEarned"),
        stats.Field("challenges").OptionalNumber("soloKills") ?? stats.OptionalNumber("soloKills"),
        stats.OptionalNumber("enemyMissingPings"), stats.OptionalNumber("totalHeal"), MatchFeats.Read(stats, entry));
}
internal record MatchFeats(long? Doubles, long? Triples, long? Quadras, long? Pentas, long? Largest,
    long? EnemyTowerKills, long? OwnTowerKills, long? KillDeficit, long? GoldDeficit = null)
{
    public static MatchFeats Read(JsonElement stats, JsonElement entry)
    {
        long? Read(string name) => stats.Field("challenges").OptionalNumber(name) ??
            entry.Field("challenges").OptionalNumber(name) ?? stats.OptionalNumber(name) ?? entry.OptionalNumber(name);
        return new(Read("doubleKills"), Read("tripleKills"), Read("quadraKills"), Read("pentaKills"),
            Read("largestMultiKill"), Read("killsNearEnemyTurret"), Read("killsUnderOwnTurret"), Read("maxKillDeficit"));
    }
}
internal record Match(long Id, int Queue, DateTimeOffset? Started, TimeSpan Duration, Participant[] Players,
    bool EarlyEnd = false, TeamObjective[]? Objectives = null, bool Surrendered = false, bool HasTimelineReview = false)
{
    public bool HasCompleteLineup => Queue is 400 or 420 or 430 or 440 or 450 or 480 or 490 or 2400
        ? Players.Count(player => player.Team == 100) == 5 && Players.Count(player => player.Team == 200) == 5
        : Players.Select(player => player.Team).Where(team => team > 0).Distinct().Take(2).Count() == 2;

    public string Mode => Queue switch
    {
        420 => "单双排", 440 => "灵活排位", 450 => "大乱斗", 2400 => "海克斯大乱斗",
        400 or 430 or 480 or 490 => "匹配", 0 => "自定义", _ => $"队列 {Queue}"
    };
    public static Match Read(JsonElement raw)
    {
        var data = raw.Field("json");
        if (data.ValueKind != JsonValueKind.Object) data = raw;
        var identities = data.Field("participantIdentities").Items();
        var entries = data.Field("participants").Items();
        var teams = data.Field("teams").Items();
        var players = entries.Select(entry =>
        {
            var identity = identities.FirstOrDefault(row => row.Number("participantId") == entry.Number("participantId")).Field("player");
            var source = identity.ValueKind == JsonValueKind.Object ? identity : entry;
            var player = Player.Read(source);
            if (LeagueService.IsHidden(source) || LeagueService.IsHidden(entry)) player = player with { Name = "" };
            var stats = entry.Field("stats");
            if (stats.ValueKind != JsonValueKind.Object) stats = entry;
            var items = Enumerable.Range(0, 7).Select(i => stats.Number($"item{i}")).Where(item => item > 0);
            var team = (int)entry.Number("teamId");
            var outcome = stats.Outcome() ?? entry.Outcome() ?? teams.FirstOrDefault(row => row.Number("teamId") == team).Outcome();
            return new Participant(player, (int)entry.Number("championId"), team, outcome == true,
                $"{stats.Number("kills")} / {stats.Number("deaths")} / {stats.Number("assists")}",
                stats.Number("totalMinionsKilled") + stats.Number("neutralMinionsKilled"),
                stats.Number("totalDamageDealtToChampions"), string.Join(" · ", items), CombatStats.Read(stats, entry), MatchLoadout.Read(entry, stats),
                outcome.HasValue, (int)entry.Number("participantId"));
        }).ToArray();
        var timestamp = data.Number("gameCreation");
        var seconds = data.Number("gameDuration");
        if (seconds > 86400) seconds /= 1000;
        return new(data.Number("gameId"), (int)data.Number("queueId"),
            timestamp is > 0 and < 253402300800000 ? DateTimeOffset.FromUnixTimeMilliseconds(timestamp).ToLocalTime() : null,
            TimeSpan.FromSeconds(seconds), players, entries.Any(entry => entry.Flag("gameEndedInEarlySurrender") ||
                entry.Field("stats").Flag("gameEndedInEarlySurrender")),
            teams.Select(TeamObjective.Read).ToArray(), entries.Any(entry => entry.Flag("gameEndedInSurrender") ||
                entry.Field("stats").Flag("gameEndedInSurrender")));
    }

    public Participant? For(Player player) => Players.FirstOrDefault(entry =>
        (player.Puuid.Length > 0 && entry.Player.Puuid == player.Puuid) || (player.Id > 0 && entry.Player.Id == player.Id)) ??
        (player.HasRiotId ? Players.FirstOrDefault(entry =>
            (player.Puuid.Length == 0 || entry.Player.Puuid.Length == 0) &&
            entry.Player.Name.Equals(player.Name, StringComparison.OrdinalIgnoreCase)) : null);
}

internal record HistoryPage(Match[] Matches, int Entries, bool CanContinue, bool IsShard)
{
    public string Source => IsShard ? "大区战绩" : "客户端战绩";
}
internal record Selection(string Phase, string Session, int Champion, string Position, int Map, string Version, bool IsCustom = false, int Queue = 0)
{
    public bool IsPicking => Phase == "ChampSelect";
    public bool CanShowRoster => Phase is "GameStart" or "InProgress" or "Reconnect";
}
internal record Seat(Player Player, int Champion, int Side, string ChampionLabel = "", bool IsBot = false, string Position = "",
    bool IdentityHidden = false);
internal record Rune(string Name, int PrimaryStyleId, int SubStyleId, int[] SelectedPerkIds, string Source,
    double? PickRate = null, double? WinRate = null, int? Games = null, string? Region = null)
{
    public bool IsValid => !string.IsNullOrWhiteSpace(Name) && PrimaryStyleId > 0 && SubStyleId > 0 &&
        PrimaryStyleId != SubStyleId && SelectedPerkIds is { Length: 9 } && SelectedPerkIds.All(id => id > 0);
    public string Summary => Games is > 0 && PickRate.HasValue && WinRate.HasValue
        ? $"{Source} · {Region} · 选用 {PickRate:P1} · 胜率 {WinRate:P1} · {Games:N0} 场" : Source;
}

internal record RunePreset(int ChampionId, string Position, int MapId, string Version,
    string Name, int PrimaryStyleId, int SubStyleId, int[] SelectedPerkIds);

internal record RuneSample(RunePreset Preset, string Source, string Region, int Games, int Wins, int EligibleGames)
{
    public bool IsValid => Preset is { ChampionId: > 0, MapId: > 0 } && !string.IsNullOrWhiteSpace(Preset.Position) &&
        !string.IsNullOrWhiteSpace(Preset.Version) && !string.IsNullOrWhiteSpace(Source) &&
        !string.IsNullOrWhiteSpace(Region) && Games > 0 && Wins >= 0 && Wins <= Games && EligibleGames >= Games &&
        ToRune().IsValid;

    public Rune ToRune() => new(Preset.Name, Preset.PrimaryStyleId, Preset.SubStyleId, Preset.SelectedPerkIds,
        Source, (double)Games / EligibleGames, (double)Wins / Games, Games, Region);
}

internal record PlayerMatch(Match Match, Participant Participant);
internal record PlayerRecord(RankSummary? Rank, int Wins, int Losses, string Status, ReviewTag[]? Tags = null,
    PlayerMatch[]? Games = null, Player? Identity = null)
{
    public int SampleSize => Wins + Losses;
    public bool HasResults => SampleSize > 0;
    public double WinPercent => HasResults ? Wins * 100d / SampleSize : 0;
    public int WinBalance => SampleSize >= 3 ? Wins.CompareTo(Losses) : 0;
    public string WinRateLabel => SampleSize >= 3 ? $"{WinPercent:0}% 胜率" : "— 胜率";
}
internal record RankSummary(string Label, string Record, long? Points, string Tier = "", bool HasVerifiedRecord = false)
{
    public string WithPoints => Points is { } points ? $"{Label} · {points} LP" : Label;
}
internal record MasteryEntry(int Champion, long Level, long Points);
internal record CollectionCount(string Label, int? Count)
{
    public string Value => Count?.ToString("N0") ?? "—";
}
