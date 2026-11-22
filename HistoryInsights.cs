namespace LolMatchAssistant;

internal record TeammateRecord(Player Player, int Champion, int Wins, int Games);
internal record SharedMatch(PlayerMatch Viewed, Participant Self);
internal record HistoryMetric(string Label, string Value);

internal static class HistoryInsights
{
    public static TeammateRecord[] Teammates(PlayerMatch[] records) => records
        .Where(record => record.Participant.OutcomeKnown && !record.Match.EarlyEnd)
        .SelectMany(record => record.Match.Players.Where(p => p.Team > 0 && p.Team == record.Participant.Team &&
                !ReferenceEquals(p, record.Participant) && p.Player.Name.Length > 0 &&
                !SamePlayer(p.Player, record.Participant.Player))
            .DistinctBy(p => PlayerKey(p.Player)).Select(p => (p, record.Participant.Won)))
        .GroupBy(entry => PlayerKey(entry.p.Player), StringComparer.OrdinalIgnoreCase)
        .Select(group => new TeammateRecord(group.First().p.Player, group.First().p.Champion,
            group.Count(entry => entry.Won), group.Count()))
        .OrderByDescending(entry => entry.Games).Take(6).ToArray();

    public static SharedMatch[] Encounters(PlayerMatch[] records, Player self) => records.SelectMany(record =>
        record.Match.For(self) is { } participant && !SamePlayer(self, record.Participant.Player) &&
        participant.Team > 0 && record.Participant.Team > 0 ? new[] { new SharedMatch(record, participant) } : [])
        .Take(8).ToArray();

    public static bool SamePlayer(Player left, Player right) =>
        (left.Puuid.Length > 0 && left.Puuid == right.Puuid) || (left.Id > 0 && left.Id == right.Id);

    public static HistoryMetric[] Summary(PlayerMatch[] records)
    {
        var metrics = new List<HistoryMetric>();
        var kda = Kda(records.Select(record => record.Participant));
        if (kda != "—") metrics.Add(new("平均 KDA", kda));
        AddShare("参团率", stats => stats.Kills, stats => stats.Kills.HasValue && stats.Assists.HasValue
            ? stats.Kills + stats.Assists : null);
        AddShare("伤害比", stats => stats.ChampionDamage);
        AddShare("承伤比", stats => stats.DamageTaken);
        AddShare("经济比", stats => stats.Gold);
        return metrics.ToArray();

        void AddShare(string title, Func<CombatStats, long?> denominator, Func<CombatStats, long?>? numerator = null)
        {
            var shares = Shares(records, denominator, numerator);
            if (shares.Length > 0) metrics.Add(new(title, $"{shares.Average():P0}"));
        }
    }

    public static ReviewTag[] RosterTags(PlayerMatch[] records)
    {
        var reviews = records.Select(record => PerformanceReview.Evaluate(record.Match, record.Participant));
        var recent = PerformanceReview.Recent(reviews).Where(tag => tag.Kind != "sample").ToArray();
        var metrics = MetricTags(records, recent: true);
        var streaks = metrics.Where(tag => tag.Kind is "high" or "low").ToArray();
        return recent.Take(1).Concat(streaks).Concat(recent.Skip(1))
            .Concat(metrics.Where(tag => tag.Kind is "damage" or "vision" or "objective"))
            .DistinctBy(tag => tag.Label.Split(' ', 2)[0]).Take(3).ToArray();
    }

    public static ReviewTag[] MetricTags(PlayerMatch[] records, bool recent = false)
    {
        var sample = records.Where(record => !record.Match.EarlyEnd).ToArray();
        var tags = new List<ReviewTag>();
        if (recent && sample.Length > 1)
        {
            var won = sample[0].Participant.Won;
            var streak = sample.TakeWhile(record => record.Participant.OutcomeKnown && record.Participant.Won == won).Count();
            if (streak >= 2)
                tags.Add(new($"{streak} 连{(won ? "胜" : "败")}", "从近期记录中最新一场开始统计的连续胜负",
                    won ? "high" : "low"));
        }
        Add("出伤", "damage", "本人对英雄输出占同队总输出", Shares(sample, stats => stats.ChampionDamage), true);
        Add("承伤", "support", "本人承受的伤害占同队总承伤", Shares(sample, stats => stats.DamageTaken), true);
        Add("经济", "economy", "本人金币占同队总金币", Shares(sample, stats => stats.Gold), true);
        var efficiency = sample.Select(record => record.Participant.Combat is { ChampionDamage: >= 0, Gold: > 0 } stats
            ? stats.ChampionDamage!.Value / (double)stats.Gold!.Value : (double?)null).OfType<double>().ToArray();
        Add("伤转率", "objective", "对英雄伤害 ÷ 本人金币", efficiency, true);
        AddCount("视野", "vision", "每场视野得分", stats => stats.Vision);
        AddCount("单杀", "control", "每场单杀次数", stats => stats.SoloKills);
        AddCount("问号", "ping", "每场发送敌人消失信号次数", stats => stats.MissingPings);
        return tags.ToArray();

        void AddCount(string title, string kind, string meaning, Func<CombatStats, long?> get)
        {
            var values = sample.Select(record => record.Participant.Combat is { } stats ? get(stats) : null)
                .Where(value => value is >= 0).Select(value => (double)value!.Value).ToArray();
            if (values.Any(value => value > 0)) Add(title, kind, meaning, values, false);
        }
        void Add(string title, string kind, string meaning, double[] values, bool percentage)
        {
            if (values.Length == 0) return;
            var average = values.Average();
            var value = percentage ? average.ToString("P0") : average.ToString(recent ? "0.0" : "0");
            tags.Add(new($"{title} {value}", $"{meaning} · {values.Length} 场有效样本", kind));
        }
    }

    private static double[] Shares(PlayerMatch[] records, Func<CombatStats, long?> denominator,
        Func<CombatStats, long?>? numerator = null) => records.Select(record =>
    {
        var team = record.Match.Players.Where(p => p.Team == record.Participant.Team && p.Team > 0).ToArray();
        if (team.Length != 5 || record.Participant.Combat is not { } stats ||
            (numerator ?? denominator)(stats) is not { } amount || amount < 0) return (double?)null;
        var totals = team.Select(p => p.Combat is { } combat ? denominator(combat) : null).ToArray();
        var sum = totals.Sum() ?? 0;
        return totals.All(value => value is >= 0) && sum > 0 ? (double)amount / sum : null;
    }).OfType<double>().ToArray();

    public static string Kda(IEnumerable<Participant> players)
    {
        var complete = players.Select(p => p.Combat).Where(stats => stats is
            { Kills: >= 0, Deaths: >= 0, Assists: >= 0 }).ToArray();
        return complete.Length > 0
            ? $"{(double)complete.Sum(s => s!.Kills!.Value + s.Assists!.Value) / Math.Max(1, complete.Sum(s => s!.Deaths!.Value)):F2}" : "—";
    }

    internal static string PlayerKey(Player player) => player.Puuid.Length > 0 ? player.Puuid :
        player.Id > 0 ? $"id:{player.Id}" : $"name:{player.Name}";
}
