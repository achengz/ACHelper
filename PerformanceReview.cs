namespace LolMatchAssistant;

internal record ReviewTag(string Label, string Reason, string Kind = "specialty");
internal record MatchReview(ReviewTag? Grade, double? Place, ReviewTag[] Specialties)
{
    public ReviewTag[] Tags => Grade is null ? Specialties : [Grade, .. Specialties];
}

internal static class PerformanceReview
{
    private static readonly (string Label, string Kind)[] Grades =
        [("汗血宝马", "gold"), ("上等马", "high"), ("中等马", "middle"), ("下等马", "low"), ("路边一条", "bottom")];

    public static MatchReview Evaluate(Match match, Participant player)
    {
        var minutes = match.Duration.TotalMinutes;
        var rift = match.Queue is 400 or 420 or 430 or 440 or 480 or 490;
        if (match.EarlyEnd || minutes < 8 || (!rift && match.Queue is not (450 or 2400)) || player.Combat is not { } stats)
            return new(null, null, []);
        var team = match.Players.Where(peer => peer.Team == player.Team).ToArray();
        var complete = match.Players.Length == 10 && player.Team is 100 or 200 && team.Length == 5 &&
            match.Players.Count(peer => peer.Team == (player.Team == 100 ? 200 : 100)) == 5;
        var teamKills = team.Sum(peer => peer.Combat?.Kills ?? 0);
        var tags = new List<ReviewTag>();
        void Add(string label, string evidence, string? kind = null) => tags.Add(new(label, evidence, kind ?? (label switch
        {
            "伤害" => "damage", "控制" => "control", "护盾" or "治疗" => "support",
            "补兵" => "economy", "拆塔" or "拆塔专家" or "塔之子" => "objective", "视野" => "vision", "参团" => "participation",
            _ => "specialty"
        })));

        if (stats.Feats is { } feats)
        {
            foreach (var (size, label, count) in new[]
                { (5, "五杀", feats.Pentas), (4, "四杀", feats.Quadras), (3, "三杀", feats.Triples), (2, "双杀", feats.Doubles) })
            {
                if (count is > 0) Add(label, $"客户端记录 {count} 次{label}", "multikill");
                else if (count is null && feats.Largest == size) Add(label, $"本局最大连杀为 {size} 杀", "multikill");
            }
            if (feats.EnemyTowerKills is > 0 and var dives)
                Add(dives > 2 ? $"越塔 × {dives}" : "越塔", $"在敌方防御塔附近击杀 {dives} 次", "dive");
            if (feats.OwnTowerKills is >= 2 and var defense)
                Add("塔之子", $"在己方防御塔下击杀 {defense} 次");
        }

        if (stats.Control is { } control && control / minutes >= 2) Add("控制", $"对敌控制 {control:N0} 秒");
        if (stats.Shield is { } shield && shield / minutes >= 70) Add("护盾", $"为队友提供 {shield:N0} 护盾");
        if (stats.Heal is { } heal && heal / minutes >= 70) Add("治疗", $"为队友回复 {heal:N0} 生命");
        if (rift && stats.Minions is { } cs && cs / minutes >= 6.5) Add("补兵", $"小兵 {cs:N0} · {cs / minutes:F1} 个 / 分");
        if (stats.Turrets is > 0 || stats.TurretDamage is >= 3000)
        {
            var evidence = new List<string>();
            if (stats.Turrets is { } towers) evidence.Add($"摧毁 {towers} 座塔");
            if (stats.TurretDamage is { } turretDamage) evidence.Add($"对塔伤害 {turretDamage:N0}");
            Add(stats.Turrets is >= 2 || stats.TurretDamage is >= 6000 ? "拆塔专家" : "拆塔", string.Join(" · ", evidence));
        }
        if (rift && stats.Vision is { } vision && vision / minutes >= 1.4) Add("视野", $"视野得分 {vision:N0}");
        if (complete && team.All(peer => peer.Combat?.ChampionDamage is >= 0) && stats.ChampionDamage is > 0)
        {
            var share = (double)stats.ChampionDamage.Value / team.Sum(peer => peer.Combat!.ChampionDamage!.Value);
            if (share >= .27) Add("伤害", $"英雄伤害占队伍 {share:P0}");
        }
        if (complete && teamKills >= 5 && team.All(peer => peer.Combat?.Kills is >= 0) && stats.Kills is { } kills &&
            stats.Assists is { } assists && (double)(kills + assists) / teamKills >= .65)
            Add("参团", $"击杀参与率 {(double)(kills + assists) / teamKills:P0}");

        var deficit = team.Select(peer => peer.Combat?.Feats?.KillDeficit ?? 0).DefaultIfEmpty().Max();
        var goldDeficit = team.Select(peer => peer.Combat?.Feats?.GoldDeficit ?? 0).DefaultIfEmpty().Max();
        if (player.OutcomeKnown && player.Won && (deficit >= 8 || goldDeficit >= 5000))
        {
            var gap = goldDeficit >= 5000 ? $"{goldDeficit:N0} 金币" : $"{deficit} 个人头";
            Add("翻盘局", $"本方一度落后 {gap}，最终获胜。", "comeback");
        }
        if (player.OutcomeKnown && player.Won && match.Surrendered && minutes <= (rift ? 18 : 12))
            Add("福利局", $"对方在 {minutes:F1} 分钟投降。", "support");
        if (complete && player.OutcomeKnown && match.Players.All(peer => peer.Combat?.Kills is >= 0))
        {
            var opponentKills = match.Players.Where(peer => peer.Team != player.Team).Sum(peer => peer.Combat!.Kills!.Value);
            var winnerKills = player.Won ? teamKills : opponentKills;
            var loserKills = player.Won ? opponentKills : teamKills;
            if (winnerKills - loserKills >= (rift ? 12 : 16) && winnerKills >= loserKills * 1.75)
                Add("碾压局", $"获胜方击杀 {winnerKills} · 落败方击杀 {loserKills}", "damage");
        }

        if (!complete || teamKills < 3 || team.Any(peer => peer.Combat is not
            { Kills: >= 0, Deaths: >= 0, Assists: >= 0, ChampionDamage: >= 0 }))
            return new(null, null, tags.ToArray());

        double Relative(CombatStats member, Func<CombatStats, long?> field)
        {
            if (team.Any(peer => !field(peer.Combat!).HasValue)) return 0;
            var maximum = team.Max(peer => field(peer.Combat!)!.Value);
            return maximum > 0 ? (field(member) ?? 0) / (double)maximum : 0;
        }
        double Score(CombatStats member)
        {
            var takedowns = member.Kills!.Value + member.Assists!.Value;
            var participation = Math.Min(1, (double)takedowns / teamKills);
            var efficiency = Math.Min(1, (takedowns + 1) / (4d * (member.Deaths!.Value + 1)));
            var damage = Relative(member, value => value.ChampionDamage);
            var utility = new[] { Relative(member, value => value.Control), Relative(member, value => value.Shield),
                Relative(member, value => value.Heal), rift ? Relative(member, value => value.Vision) : 0 }.Max();
            var pressure = Math.Max(Relative(member, value => value.TurretDamage), Relative(member, value => value.Turrets));
            return Math.Round(100 * (.35 * participation + .25 * efficiency + .30 * Math.Max(damage, utility) + .10 * pressure), 2);
        }
        var score = Score(stats);
        var scores = team.Select(peer => Score(peer.Combat!)).ToArray();
        var place = 1 + scores.Count(value => value > score) + (scores.Count(value => value == score) - 1) / 2d;
        var index = place == 1 && score >= 70 ? 0 : place <= 2 ? 1 :
            place >= 4.5 ? (score < 35 ? 4 : 3) : 2;
        var reason = $"队内相对评分 {score:F1} / 100 · 名次 {place:0.#} / 5\n参考参团、KDA、伤害或团队支援，以及推塔。";
        if (place == 1 && player.OutcomeKnown)
            tags.Insert(0, new(player.Won ? "MVP" : "SVP", $"{(player.Won ? "胜方" : "败方")}队内评分第一\n{reason}",
                player.Won ? "mvp" : "svp"));
        if (player.OutcomeKnown && !player.Won && place <= 2 && score >= 60)
            Add("尽力局", $"本方落败，但队内评分 {score:F1}，名次 {place:0.#} / 5。", "svp");
        return new(Grade(index, reason), place, tags.ToArray());
    }

    public static ReviewTag[] Recent(IEnumerable<MatchReview> reviews)
    {
        var sample = reviews.Take(12).ToArray();
        var places = sample.Where(review => review.Place.HasValue).Select(review => review.Place!.Value).ToArray();
        var tags = new List<ReviewTag>();
        if (places.Length >= 3)
        {
            var average = places.Average();
            var bottom = sample.Count(review => review.Grade?.Kind == "bottom");
            var index = average <= 1.6 ? 0 : average <= 2.5 ? 1 : average >= 4.3
                ? (average >= 4.7 && bottom * 2 >= places.Length ? 4 : 3) : 2;
            tags.Add(Grade(index, $"{places.Length} 场可评分 · 平均队内名次 {average:F1} / 5"));
            tags.Add(new($"{places.Length} 场样本", "样本为最近可评分的完整对局。", "sample"));
        }
        tags.AddRange(sample.SelectMany(review => review.Specialties).GroupBy(tag => tag.Kind == "dive" ? "越塔" : tag.Label)
            .Where(group => group.Count() >= 2).OrderByDescending(group => group.Count()).Take(3)
            .Select(group => new ReviewTag(group.Key, $"最近 {sample.Length} 场中有 {group.Count()} 场符合该专项标准", group.First().Kind)));
        return tags.ToArray();
    }

    private static ReviewTag Grade(int index, string reason) => new(Grades[index].Label, reason, Grades[index].Kind);
}
