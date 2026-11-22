using System.Net.Http;
using System.Text.Json;

namespace LolMatchAssistant;

internal static class Diagnostics
{
    public static async Task<(int, string)> Check(CancellationToken ct, string? lockfile)
    {
        var results = new List<string>();
        try
        {
            var connection = ClientDiscovery.Find(lockfile);
            if (connection is null) return (2, "无法发现本机客户端连接。");
            var transport = new LeagueTransport(connection);
            var league = new LeagueService(transport);
            var current = await league.Current(ct);
            results.Add("客户端连接与当前账号读取通过。");
            var collection = league.Collection(current, ct);
            var mastery = league.Mastery(current, current, ct);
            var ranks = await league.Ranks(current, ct);
            results.Add($"排位资料 · {ranks.Solo.Label} / {ranks.Flex.Label}");
            await league.LoadMetadata(ct);
            results.Add($"大区 {league.Platform} · 客户端版本 {league.Version}");
            var state = await league.State(ct);
            results.Add($"对局阶段 {state.Phase}");
            var page = await league.History(current, current, 0, true, ct);
            results.Add($"本人对局 {page.Matches.Length} 场 · {page.Source}");
            foreach (var summary in page.Matches.Where(match => !match.HasCompleteLineup && match.For(current) is not null).Take(3))
            {
                var complete = await league.Details(summary, ct, current);
                var grade = complete.For(current) is { } player ? PerformanceReview.Evaluate(complete, player).Grade?.Label : null;
                results.Add($"主战绩卡片补全 · 队列 {summary.Queue} · {summary.Players.Length} → {complete.Players.Length} 位玩家 · " +
                    $"可见名称 {complete.Players.Count(p => p.Player.Name.Length > 0)} 位 · {grade ?? "暂无足够数据评分"}");
            }
            var reviews = page.Matches.Where(match => match.For(current) is not null)
                .Select(match => PerformanceReview.Evaluate(match, match.For(current)!)).ToArray();
            results.Add($"点评 · {reviews.Count(review => review.Grade is not null)} 场可评分 · {reviews.Count(review => review.Specialties.Length > 0)} 场有专项标签");
            var feats = page.Matches.SelectMany(match => match.Players).Select(p => p.Combat?.Feats).OfType<MatchFeats>().ToArray();
            results.Add($"对局标签字段 · 多杀 {feats.Count(f => f.Largest.HasValue || f.Doubles.HasValue)} / 越塔 {feats.Count(f => f.EnemyTowerKills.HasValue)} / 最大击杀落后 {feats.Count(f => f.KillDeficit.HasValue)}");
            var recent = page.Matches.Select(match => (match, entry: match.For(current)))
                .Where(result => result.entry is not null).Select(result => new PlayerMatch(result.match, result.entry!)).ToArray();
            var valid = recent.Where(record => !record.Match.EarlyEnd && record.Participant.OutcomeKnown).ToArray();
            results.Add($"近期胜负有效样本 · {valid.Count(record => record.Participant.Won)} 胜 {valid.Count(record => !record.Participant.Won)} 负 · 共 {valid.Length} 场");
            results.Add($"历史总览 · {HistoryInsights.Summary(recent).Length} 项指标 · 最近队友 {HistoryInsights.Teammates(recent).Length} 位");
            var metricKinds = HistoryInsights.MetricTags(recent, recent: true).Select(tag => tag.Label.Split(' ')[0]);
            results.Add($"彩色数据标签 · {string.Join("、", metricKinds)}");
            var loaded = page.Matches.ToDictionary(match => match.Id);
            if (page.CanContinue)
            {
                var offset = page.Entries;
                var older = page;
                for (var index = 1; index < 5 && older.CanContinue; index++)
                {
                    older = await league.History(current, current, offset, page.IsShard, ct);
                    offset += older.Entries;
                    foreach (var match in older.Matches) loaded.TryAdd(match.Id, match);
                }
                results.Add($"较早战绩探测 · {loaded.Count} 场不同对局 · 最早 {loaded.Values.Min(match => match.Started):yyyy-MM-dd}");
            }
            await AuditMatchReviews(league, current, loaded.Values, results, ct);
            if (page.IsShard)
            {
                try
                {
                    var local = await league.History(current, current, 0, false, ct);
                    var second = await league.History(current, current, LeagueService.PageSize, false, ct);
                    results.Add($"客户端翻页探测 · 首批 {local.Entries} 场 · 第二批返回 {second.Entries} 场 · " +
                        $"新增 {second.Matches.Select(match => match.Id).Except(local.Matches.Select(match => match.Id)).Count()} 场");
                }
                catch (LeagueException ex) { results.Add($"客户端翻页探测 · {ex.Message}"); }
            }
            if (page.Matches.FirstOrDefault() is { } first)
            {
                if (first.For(current) is { Champion: > 0 } own && await transport.ChampionIcon(own.Champion, ct) is { } icon)
                {
                    var portrait = ChampionPortraits.Decode(icon);
                    results.Add($"本机英雄头像读取通过 · {portrait.PixelWidth} × {portrait.PixelHeight}");
                    var itemId = own.Items.Split(" · ").Select(value => int.TryParse(value, out var id) ? id : 0)
                        .FirstOrDefault(id => id > 0 && league.ItemIcon(id).Length > 0);
                    if (itemId > 0 && await transport.ReadIcon(league.ItemIcon(itemId), ct) is { } item)
                    {
                        ChampionPortraits.Decode(item);
                        results.Add("本机装备图标读取通过。");
                    }
                }
                var detail = await league.Details(first, ct);
                results.Add($"首场对局详情 {detail.Players.Length} 位玩家");
                if (MatchTimeline.NeedsReview(detail) && MatchTimeline.Enrich(detail, await league.Timeline(detail.Id, ct)) is { } reviewed)
                {
                    results.Add($"时间轴补充 · {reviewed.Players.Sum(p => p.Combat?.Feats?.EnemyTowerKills ?? 0)} 次敌方塔附近击杀 · " +
                        $"最大击杀落后 {reviewed.Players.Max(p => p.Combat?.Feats?.KillDeficit ?? 0)}");
                }
                var sample = page.Matches.FirstOrDefault(match => match.Players.Length == 10) ?? detail;
                results.Add($"总览数据 · {sample.Players.Count(p => p.Loadout?.Spells.Length > 0)} 位有召唤师技能 · " +
                    $"{sample.Players.Count(p => p.Loadout is { Runes.Length: > 0 } or { Augments.Length: > 0 })} 位有配置 · " +
                    $"{sample.Objectives?.Length ?? 0} 支队伍有目标数据");
                var loadouts = page.Matches.SelectMany(match => match.Players).Select(p => p.Loadout).OfType<MatchLoadout>().ToArray();
                results.Add($"配置说明 · {Descriptions(loadouts.SelectMany(loadout => loadout.Spells), league.SpellDescription)} 种技能 / " +
                    $"{Descriptions(loadouts.SelectMany(loadout => loadout.Runes), league.PerkDescription)} 种符文 / " +
                    $"{Descriptions(loadouts.SelectMany(loadout => loadout.Augments), league.AugmentDescription)} 种强化");
                results.Add($"单局雷达 · {recent.Count(game => RadarSnapshot.Build(game.Match, game.Participant) is not null)} 场数据可用");
                var other = detail.Players.Select(p => p.Player).FirstOrDefault(p =>
                    p.Puuid.Length > 0 && p.Puuid != current.Puuid && p.Name.Contains('#'));
                if (other is not null)
                {
                    var found = await league.ResolvePublicPlayer(other, current, ct);
                    var profile = await league.CompletePublicProfile(found with { IconId = -1, Level = 0 }, ct);
                    if (profile.IconId >= 0)
                    {
                        var image = await transport.ReadIcon($"/lol-game-data/assets/v1/profile-icons/{profile.IconId}.jpg", ct) ??
                            await transport.ReadIcon($"/lol-game-data/assets/v1/profile-icons/{profile.IconId}.png", ct);
                        if (image is not null)
                        {
                            var avatar = ChampionPortraits.Decode(image);
                            results.Add($"同区玩家档案头像 · {avatar.PixelWidth} × {avatar.PixelHeight}");
                        }
                    }
                    var otherHistory = await league.History(found, current, 0, true, ct);
                    var (publicSolo, publicFlex) = await league.Ranks(found, ct);
                    results.Add("同区玩家单双排 · " + MainWindow.RankRecordText(publicSolo, otherHistory.Matches, found, 420).Replace('\n', ' '));
                    results.Add("同区玩家灵活排位 · " + MainWindow.RankRecordText(publicFlex, otherHistory.Matches, found, 440).Replace('\n', ' '));
                    var otherResults = otherHistory.Matches.Where(match => !match.EarlyEnd).Select(match => match.For(found))
                        .OfType<Participant>().Where(p => p.OutcomeKnown).ToArray();
                    results.Add($"同区玩家战绩 · {otherHistory.Matches.Length} 场 · 有效样本 {otherResults.Count(p => p.Won)} 胜 {otherResults.Count(p => !p.Won)} 负");
                }
            }
            if (state is { IsPicking: true })
            {
                results.Add($"客户端符文推荐 {(await league.Recommendations(state, ct)).Length} 套 · 英雄 {state.Champion} · 地图 {state.Map} · 自定义 {state.IsCustom}");
                var teammates = await league.PickingRoster(state, current, ct);
                var publicTeammates = teammates.Where(seat => seat.Player.HasLookupIdentity &&
                    !HistoryInsights.SamePlayer(seat.Player, current)).ToArray();
                results.Add($"选人队伍 {teammates.Length} 位 · 公开队友 {publicTeammates.Length} 位 · " +
                    $"身份隐藏 {teammates.Count(seat => seat.IdentityHidden)} 位 · 机器人 {teammates.Count(seat => seat.IsBot)} 位");
                foreach (var seat in publicTeammates)
                {
                    try
                    {
                        var profile = await league.Profile(seat.Player, current, ct);
                        results.Add($"选人队友 · 可预览 {profile.Games?.Length ?? 0} 场 · {profile.Status}");
                    }
                    catch (LeagueException ex) { results.Add($"选人队友资料 · {ex.Message}"); }
                }
            }
            if (state.CanShowRoster)
            {
                var (_, seats) = await league.Roster(state, ct);
                results.Add($"场上阵容 · 蓝方 {seats.Count(seat => seat.Side == 1)} 位 · 红方 {seats.Count(seat => seat.Side == 2)} 位 · 公开身份 {seats.Count(seat => seat.Player.Name.Length > 0)} 位");
            }
            results.Add($"英雄点数 · {(await mastery)?.Length.ToString() ?? "暂不可用"}");
            results.Add("资产 · " + string.Join(" / ", (await collection).Select(count => $"{count.Label} {count.Value}")));
            return (0, string.Join(Environment.NewLine, results));
        }
        catch (Exception ex) when (ex is LeagueException or OperationCanceledException)
        {
            results.Add(ex is LeagueException ? ex.Message : "探测超时。");
            return (1, string.Join(Environment.NewLine, results));
        }

        static int Descriptions(IEnumerable<int> ids, Func<int, string> description) => ids.Distinct().Count(id => description(id).Length > 0);
    }

    private static async Task AuditMatchReviews(LeagueService league, Player current, IEnumerable<Match> matches,
        List<string> results, CancellationToken ct)
    {
        var grades = new Dictionary<string, int>();
        var missing = new Dictionary<string, int>();
        var inconsistent = 0;
        foreach (var summary in matches.OrderByDescending(match => match.Started))
        {
            ct.ThrowIfCancellationRequested();
            var supported = summary.Queue is 400 or 420 or 430 or 440 or 450 or 480 or 490 or 2400;
            var match = supported && !summary.EarlyEnd && !summary.HasCompleteLineup && summary.For(current) is not null
                ? await league.Details(summary, ct, current) : summary;
            var player = match.For(current);
            var grade = player is not null ? PerformanceReview.Evaluate(match, player).Grade?.Label : null;
            var team = player is not null ? match.Players.Where(peer => peer.Team == player.Team).ToArray() : [];
            var reason = player is null ? "缺少本人记录" : match.EarlyEnd ? "重开" : !supported ? $"队列 {summary.Queue} 不参与评分" :
                match.Duration.TotalMinutes < 8 ? "时长不足 8 分钟" :
                !match.HasCompleteLineup ? "阵容不完整" :
                team.Any(peer => peer.Combat is not { Kills: >= 0, Deaths: >= 0, Assists: >= 0, ChampionDamage: >= 0 }) ? "缺少评分字段" :
                team.Sum(peer => peer.Combat?.Kills ?? 0) < 3 ? "本方击杀不足 3" : "";
            if ((grade is null) != (reason.Length > 0)) inconsistent++;
            var counts = grade is null ? missing : grades;
            var label = grade ?? JsonFields.First(reason, "应评分但未生成");
            counts[label] = counts.GetValueOrDefault(label) + 1;
        }
        results.Add("逐局标签核对 · " + string.Join(" / ", grades.Select(entry => $"{entry.Key} {entry.Value}")));
        results.Add("逐局未评分 · " + string.Join(" / ", missing.Select(entry => $"{entry.Key} {entry.Value}")));
        results.Add($"标签与评分条件不一致 {inconsistent} 场");
    }

    public static async Task<(int, string)> WatchRoster(CancellationToken ct)
    {
        var lines = new List<string>();
        var seen = new HashSet<string>();
        ClientConnection? previous = null;
        LeagueService? league = null;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    var connection = ClientDiscovery.Find(null);
                    if (connection != previous)
                    {
                        previous = connection;
                        league = connection is null ? null : new LeagueService(new LeagueTransport(connection));
                    }
                    if (league is not null)
                    {
                        var state = await league.State(ct);
                        if (seen.Add(state.Phase)) lines.Add($"阶段 {state.Phase}");
                        if (state.CanShowRoster)
                        {
                            var (_, seats) = await league.Roster(state, ct);
                            var live = seats.Where(seat => seat.Player.Name.Length > 0).ToArray();
                            var counts = $"{state.Phase} · 蓝方 {seats.Count(seat => seat.Side == 1)} 位 · 红方 {seats.Count(seat => seat.Side == 2)} 位 · 公开玩家 {live.Length} 位";
                            if (seen.Add(counts)) lines.Add(counts);
                            if (state.Phase == "InProgress" && live.Length > 0)
                            {
                                var current = await league.Current(ct);
                                var own = live.FirstOrDefault(seat => seat.Player.Puuid == current.Puuid || seat.Player.Id == current.Id) ?? live[0];
                                var profile = await league.Profile(own.Player, current, ct);
                                lines.Add($"公开玩家资料 · {profile.Rank?.WithPoints ?? "—"} · " +
                                    $"近期 {profile.Wins} 胜 {profile.Losses} 负 · {profile.WinRateLabel}");
                                return (0, string.Join(Environment.NewLine, lines));
                            }
                        }
                    }
                }
                catch (LeagueException ex)
                {
                    if (seen.Add(ex.Message)) lines.Add(ex.Message);
                }
                await Task.Delay(750, ct);
            }
        }
        catch (OperationCanceledException) { }
        lines.Add("等待结束，未观察到带公开身份的进行中阵容。");
        return (2, string.Join(Environment.NewLine, lines));
    }

    public static async Task<(int, string)> SelfTest()
    {
        var completed = new List<string>();
        try
        {
            var lcu = new FixtureTransport();
            var league = new LeagueService(lcu);
            await league.LoadMetadata(default);
            Assert(league.AugmentDescription(1079).Contains("致命节奏") &&
                league.AugmentDescription(2125).Contains("移动速度") &&
                league.SpellDescription(4) == "位移到附近位置。" &&
                league.PerkDescription(8005) == "攻击可叠加层数。", "Packaged Chinese augment fallback, spell and rune tooltips");
            var emblem = new System.Windows.Controls.Image();
            MainWindow.SetRankEmblem(emblem, "EMERALD", "翡翠 IV");
            Assert(emblem.Source is { Width: > 0 }, "Ranked emblem resource");
            completed.Add("符文、技能和国服海克斯说明，排位徽章资源");
            var current = await league.Current(default);
            var other = await league.Find("友方#1234", current, default);
            Assert(await league.CompletePublicProfile(other, default) is { IconId: 0, Level: 122 },
                "A public player with only a PUUID gets profile data, including default icon zero");
            Assert((await league.Collection(current, default)).Select(item => item.Count).SequenceEqual(new int?[] { 1, 1, 1, 1, 1, 1 }),
                "Collection counts exclude rentals and split skin and chroma IDs");
            Assert(await league.Mastery(current, current, default) is [
                { Champion: 157, Level: 8, Points: 50000 }, { Champion: 84, Level: 5, Points: 20000 }] &&
                await league.Mastery(other, current, default) is null, "Mastery is sorted and distinguishes unavailable profiles");
            var linkedPlayer = await league.ResolvePublicPlayer(new Player("", 2, "友方"), current, default);
            Assert(linkedPlayer.Puuid == other.Puuid, "Visible roster link resolves a player with a summoner ID but no tag");
            var page = await league.History(other, current, 0, true, default);
            Assert(page.Matches.Length == 1 && page.Matches[0].For(other) is { Won: true, Kda: "4 / 2 / 6" }, "LCU query and history fallback");
            Assert(!page.CanContinue && (await league.Details(page.Matches[0], default)).Players.Length == 2, "Match detail");
            var outcomes = Match.Read(JsonSerializer.Deserialize<JsonElement>("""
                {"participants":[{"teamId":100,"win":"true"},{"teamId":200},{"teamId":300}],"teams":[{"teamId":200,"win":"Fail"}]}
                """));
            Assert(outcomes.Players is [{ Won: true, OutcomeKnown: true }, { Won: false, OutcomeKnown: true }, { OutcomeKnown: false }],
                "Participant, team and absent outcomes are distinguished");
            completed.Add("客户端读取、搜索、战绩回退和单局详情");
            await CheckHistoryPagination(current, completed);
            await CheckIncompleteMatchCard(current, completed);
            lcu.SgpAvailable = true;
            page = await league.History(other, current, 0, true, default);
            Assert(page.Entries == 20 && page.CanContinue && page.Matches.Length == 1 &&
                page.Matches[0].For(other)?.Player.Name == "友方#1234", "SGP shape and match deduplication");
            Assert(page.Matches[0].For(other)?.Loadout is { Level: 17, Spells: [4, 14], Runes: [8005], Augments: [12] } &&
                page.Matches[0].Objectives is [{ Towers: 4, Dragons: 0 }, { Towers: 5, Barons: 1 }],
                "SGP match loadout and current or legacy objective fields");
            var ranked = page.Matches[0];
            var claimedWins = LeagueService.ParseRank(JsonSerializer.Deserialize<JsonElement>(
                """{"tier":"GOLD","wins":12,"losses":0,"leaguePoints":20}"""));
            var verifiedWins = LeagueService.ParseRank(JsonSerializer.Deserialize<JsonElement>(
                """{"tier":"GOLD","wins":12,"gamesPlayed":20,"leaguePoints":20}"""));
            var threeWins = new[] { ranked, ranked with { Id = 201 }, ranked with { Id = 202 } };
            var rankedLoss = ranked with { Id = 203, Players = [ranked.For(other)! with { Won = false }] };
            Assert(!claimedWins.HasVerifiedRecord && !claimedWins.Record.Contains('%') &&
                verifiedWins is { HasVerifiedRecord: true } && verifiedWins.Record.Contains("8 负") &&
                MainWindow.RankRecordText(claimedWins, [ranked], other, 420).Contains("近 1 场 · 1 胜 0 负") &&
                !MainWindow.RankRecordText(claimedWins, [ranked], other, 420).Contains('%') &&
                MainWindow.RankRecordText(claimedWins, [ranked], other, 440).Contains("暂无胜负数据") &&
                MainWindow.RankRecordText(claimedWins, threeWins, other, 420).Contains("100") &&
                MainWindow.RankRecordText(claimedWins, [.. threeWins, rankedLoss], other, 420).Contains("75"),
                "Public ranked stats require verified counters or show queue-specific recent results");
            Assert(!MainWindow.UsesAugments(ranked, ranked.For(other)!) &&
                MainWindow.UsesAugments(ranked with { Queue = 2400 }, ranked.For(other)!),
                "Ranked cards use runes and Mayhem cards use augments");
            Assert((await league.History(other, current, 20, true, default)).Entries == 0, "End of SGP pagination");
            completed.Add("大区战绩解码、重复数据及末页");
            var state = await league.State(default);
            var picks = await league.PickingRoster(state, current, default);
            Assert(state.Queue == 420, "Queue can be read from the champion select session");
            Assert(picks is [{ Side: 1, Player.Puuid: "own" }, { Side: 1, Player.Puuid: "other", Player.Name: "", Champion: 84, IsBot: false },
                { Side: 1, Player.Puuid: "", Player.Id: 0, IdentityHidden: true, IsBot: false },
                { Side: 1, Player.Name: "", Player.Id: 0, IdentityHidden: true },
                { Side: 1, Player.Name: "", Player.Id: 2, IsBot: false }, { Side: 1, Player.Name: "友方#1234" },
                { Side: 1, IsBot: true, Player.Id: 0 }],
                "Select stage accepts only public allies and local identity");
            Assert(lcu.PickSlotLookups.SequenceEqual([3, 5]), "Slot lookup skips explicitly hidden and already identified seats");
            Assert((await league.Profile(picks[1].Player, current, default)).Games is [{ Match.Id: 101 }] &&
                await league.CompletePublicProfile(picks[1].Player, default) is { Name: "友方#1234", IconId: 0 },
                "A public PUUID with no display name loads ranked champion select history before lock-in");
            lcu.EmptyShardProfile = true;
            Assert((await league.Profile(picks[1].Player, current, default)).Games is [{ Match.Id: 100 }],
                "Champion select history falls back to the local client if the shard has no record for the player");
            lcu.EmptyShardProfile = false;
            Assert((await league.Profile(picks[4].Player, current, default)).Games is [{ Match.Id: 101 }] &&
                (await league.Profile(picks[5].Player, current, default)).Games is [{ Match.Id: 101 }],
                "A public numeric ID and a public slot name both load recent games before lock-in");
            lcu.ProfileMatchCount = 12;
            var recentProfile = await league.Profile(picks[1].Player, current, default);
            Assert(recentProfile is { Wins: 12, Losses: 0, WinRateLabel: "100% 胜率" },
                "Current roster summary uses the actual recent win and loss counts");
            var recentGames = recentProfile.Games;
            Assert(recentGames is not null &&
                recentGames.Select(game => game.Match.Id).SequenceEqual(Enumerable.Range(103, 10).Reverse().Select(id => (long)id)),
                "Roster cards show the ten newest games");
            lcu.ProfileMatchCount = 0;
            var runes = await league.Recommendations(state, default);
            Assert(runes.Length == 1, "Rune recommendation");
            await league.Apply(state, runes[0], default);
            Assert(lcu.Writes == 2, "Page creation and activation");
            lcu.Phase = "GameStart";
            try
            {
                await league.Apply(state, runes[0], default);
                throw new InvalidOperationException("Write after loading started");
            }
            catch (LeagueException) { }
            var (_, seats) = await league.Roster(await league.State(default), default);
            Assert(lcu.Writes == 2 && seats is [{ Side: 1 }, { Side: 2 }] && seats[1].Player.Puuid == "", "Roster privacy and phase guard");
            var live = LeagueService.LiveSeats(JsonSerializer.Deserialize<JsonElement>("""[{"team":"ORDER","riotIdGameName":"友方","riotIdTagLine":"1234"},{"team":"CHAOS","riotId":"机器#NA","isBot":true}]"""));
            Assert(live is [{ Side: 1, Player.Name: "友方#1234" }, { Side: 2, IsBot: true, Player.Name: "" }], "Live roster and bots");
            lcu.Phase = "InProgress";
            lcu.SessionAvailable = false;
            lcu.LiveRoster = """[{"team":"ORDER","riotId":"玩家#NA"},{"team":"CHAOS","riotId":"匿名#NA","nameVisibilityType":"HIDDEN"}]""";
            var (_, fallback) = await league.Roster(await league.State(default), default);
            Assert(fallback is [{ Side: 1, Player.Name: "玩家#NA" }, { Side: 2, Player.Name: "" }], "Live fallback when session is unavailable");
            completed.Add("公开选人队友、匿名占位与开局阵容回退");
            completed.Add("符文推荐、创建、激活、回读、加载阶段写入限制和匿名阵容");
            lcu.Phase = "ChampSelect";
            lcu.SessionAvailable = true;
            lcu.Full = true;
            lcu.Created = false;
            try
            {
                await league.Apply(state, runes[0], default);
                throw new InvalidOperationException("Full inventory was overwritten");
            }
            catch (LeagueException ex) when (ex.Message.Contains("已满")) { }
            Assert(lcu.Writes == 2, "Full inventory must not overwrite");
            completed.Add("符文页满时保留现有配置");
            Assert(await league.AcceptReadyCheck(default) && !await league.AcceptReadyCheck(default) && lcu.Writes == 3,
                "Ready check only accepts a pending confirmation");
            lcu.PickFixture = true;
            Assert((await league.AutoPick(await league.State(default), 84, true, default)).StartsWith("已预选") &&
                lcu.LastPick is { } hover && !hover.Flag("completed"), "Preselection never locks ahead of the player's turn");
            lcu.InPickTurn = true;
            lcu.PickedChampion = 157;
            Assert(await league.AutoPick(await league.State(default), 84, true, default) == "" && lcu.Writes == 4,
                "A different manual selection takes precedence");
            lcu.PickedChampion = 84;
            Assert((await league.AutoPick(await league.State(default), 84, true, default)).StartsWith("已锁定") &&
                lcu.LastPick is { } lockIn && lockIn.Flag("completed"), "Optional lock-in is limited to the active action");
            Assert(MainWindow.OpGgUrl("MonkeyKing", 2).AbsolutePath.EndsWith("/monkeyking/build/jungle") &&
                MainWindow.OpGgUrl("", 3).Query == "?position=mid" && MainWindow.OpGgUrl("", 4).Query == "?position=adc",
                "OP.GG champion and role links");
            completed.Add("模拟匹配确认、预选、锁定与手动优先，OP.GG 路由");
            var owner = new System.Windows.Rect(30, 72, 1000, 700);
            var desktop = new System.Windows.Rect(0, 0, 1920, 1040);
            var beside = OpGgWindow.SidePlacement(owner, desktop);
            var narrow = OpGgWindow.SidePlacement(owner, new System.Windows.Rect(0, 0, 1280, 720));
            Assert(beside.Left > owner.Right && beside.Top == owner.Top && narrow.Right == 1280 && narrow.Bottom == 720 &&
                OpGgWindow.IsOpGgAddress("https://www.op.gg/") && !OpGgWindow.IsOpGgAddress("https://op.gg.example.org/"),
                "The OP.GG side window stays on screen and restricts top-level navigation");
            CheckPerformance();
            completed.Add("队内点评、专项数据、重开跳过与近期样本规则");
            CheckHistoryInsights();
            completed.Add("最近队友聚合、共同对局和指标字段校验");
            var window = new MainWindow(league, current);
            var delayedTransport = new FixtureTransport();
            var delayedLeague = new LeagueService(delayedTransport);
            var delayed = new MainWindow(delayedLeague, current, new(AutoAccept: true, AcceptDelay: 1));
            var ready = state with { Phase = "ReadyCheck" };
            await delayed.RunAutomation(delayedLeague, ready, default);
            Assert(delayedTransport.Writes == 0, "Auto-accept waits for the chosen delay");
            await delayed.RunAutomation(delayedLeague, ready with { Phase = "Matchmaking" }, default);
            await delayed.RunAutomation(delayedLeague, ready, default);
            Assert(delayedTransport.Writes == 0, "Leaving ReadyCheck clears the previous timer");
            await Task.Delay(1100);
            await delayed.RunAutomation(delayedLeague, ready, default);
            Assert(delayedTransport.Writes == 1, "Auto-accept submits after the chosen delay");
            delayed.Close();
            completed.Add("自动接受倒计时、切换阶段取消及延迟后接受");
            lcu.MultipleMatches = true;
            window.Pages.SelectedItem = window.RosterTab;
            window.UpdateSeats(picks, state);
            var numericCard = (System.Windows.FrameworkElement)window.BlueSeats.ItemTemplate.LoadContent();
            numericCard.DataContext = window.BlueSeats.Items[4];
            await window.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.DataBind);
            Assert(window.RosterStatus.Text.Contains("3 位队友可查询") && window.RosterStatus.Text.Contains("2 位身份隐藏") &&
                FindButtonWithContent(numericCard, "友方#1234") is not null,
                "The active roster distinguishes hidden seats and makes a numeric-only public seat clickable");
            window.QueueFilter.SelectedIndex = 1;
            await window.OpenPlayerHistory(picks[1].Player, true);
            Assert(window.Pages.SelectedItem == window.HistoryTab && window.QueueFilter.SelectedIndex == 0 &&
                window.HistoryTitle.Text == other.Name && window.MatchGrid.Items.Count > 0 &&
                window.BackToRosterButton.Visibility == System.Windows.Visibility.Visible,
                "Roster navigation opens the complete player history route");
            window.BackToRosterButton.RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            Assert(window.Pages.SelectedItem == window.RosterTab, "Return restores roster page");
            completed.Add("队友战绩跳转、筛选重置和返回阵容");
            window.Pages.SelectedItem = window.HistoryTab;
            window.QueueFilter.SelectedIndex = 1;
            Assert(window.RecentRate.Text == "—" && window.RecentRecord.Text.Contains("2 胜 0 负"),
                "Two wins in another player's ranked filter do not appear as a reliable win rate");
            window.MatchGrid.SelectedIndex = 0;
            var expanded = window.MatchGrid.SelectedItem;
            Assert(expanded is not null, "Ranked fixture match is visible");
            await window.OpenSelectedMatch();
            window.MatchGrid.SelectedIndex = 1;
            var second = window.MatchGrid.SelectedItem;
            Assert(second is not null && !ReferenceEquals(second, expanded), "A second distinct match is visible");
            await window.OpenSelectedMatch();
            await window.OpenDetailPlayerHistory(current);
            Assert(window.HistoryTitle.Text == current.Name && window.MatchGrid.Items.Count == 2 &&
                window.QueueFilter.SelectedIndex == 0 && window.BackToMatchButton.Visibility == System.Windows.Visibility.Visible,
                $"Scoreboard player opens their full history ({window.HistoryTitle.Text}, {window.MatchGrid.Items.Count}, {window.QueueFilter.SelectedIndex}, {window.BackToMatchButton.Visibility}, {window.HistoryStatus.Text})");
            window.BackToMatchButton.RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            Assert(window.HistoryTitle.Text == other.Name && window.QueueFilter.SelectedIndex == 1 &&
                ReferenceEquals(window.MatchGrid.Items[0], expanded) && ReferenceEquals(window.MatchGrid.Items[1], second),
                "Returning restores both independently expanded matches and previous filter");
            Assert(window.HistoryPlayerTabs.Items.Count == 2, "The fixed own page and the other player page are both open");
            window.HistoryPlayerTabs.SelectedIndex = 0;
            Assert(window.HistoryTitle.Text == current.Name && window.QueueFilter.SelectedIndex == 0,
                "Selecting the fixed tab immediately returns to own history");
            await window.OpenPlayerHistory(other, false);
            Assert(window.HistoryPlayerTabs.Items.Count == 2 && window.QueueFilter.SelectedIndex == 1 &&
                ReferenceEquals(window.MatchGrid.Items[0], expanded) && window.AccountSubtitle.Text.Contains("122 级"),
                "Opening the same player selects that tab and restores its history and profile");
            var badge = (System.Windows.Controls.Border)window.HistoryTags.ItemTemplate.LoadContent();
            badge.DataContext = new ReviewTag("控制", "对英雄施加 82 秒控制");
            await window.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.DataBind);
            badge.GetBindingExpression(System.Windows.FrameworkElement.ToolTipProperty)?.UpdateTarget();
            Assert((string?)badge.ToolTip == "对英雄施加 82 秒控制", $"Tag tooltip reads the same badge's explanation ({badge.ToolTip})");
            var strip = new ReviewTagStrip
            {
                BadgeTemplate = window.HistoryTags.ItemTemplate,
                Tags = new ReviewTag[] { new("控制", "对敌控制 82 秒"), new("拆塔专家", "摧毁两座塔"), new("五杀", "本局拿到五杀") }
            };
            strip.Measure(new System.Windows.Size(90, 40));
            var extra = ((System.Windows.Controls.ContentPresenter)strip.Children[3]).Content as ReviewTag;
            Assert(extra is { Label: "+2" } && extra.Reason.Contains("拆塔专家") && extra.Reason.Contains("五杀"),
                "Overflow retains the label and explanation of every hidden tag");
            AssistantTheme.Apply(2);
            Assert(window.Background.ToString() == "#FFEDF2F2", "Light palette updates the live window");
            AssistantTheme.Apply(3);
            Assert(window.Background.ToString() == "#FF171E2D", "Blue palette updates the live window");
            AssistantTheme.Apply(window.ThemePicker.SelectedIndex);
            completed.Add("多场详情并行展开、战绩跳转与恢复、标签说明、亮色和深蓝主题");
            window.Measure(new System.Windows.Size(1120, 750));
            window.Arrange(new System.Windows.Rect(0, 0, 1120, 750));
            window.UpdateLayout();
            Assert(window.Content is not null, "WPF window initializes");
            var otherTab = (System.Windows.FrameworkElement)window.HistoryPlayerTabs.ItemTemplate.LoadContent();
            otherTab.DataContext = window.HistoryPlayerTabs.Items[1];
            await window.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.DataBind);
            var close = FindButtonWithContent(otherTab, "×");
            Assert(close is not null, "The visitor tab can be closed");
            close!.RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            Assert(window.HistoryPlayerTabs.Items.Count == 1 && window.HistoryTitle.Text == current.Name,
                "Closing the visitor tab selects the fixed own page");
            var entry = (System.Windows.FrameworkElement)((System.Windows.DataTemplate)window.FindResource("CardRosterEntry")).LoadContent();
            entry.DataContext = new MainWindow.MatchRosterRow(other, "亚索", null, false);
            await window.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.DataBind);
            var playerLink = FindButtonWithContent(entry, "友方");
            Assert(playerLink is { Visibility: System.Windows.Visibility.Visible }, "Visible player from match card has an accessible link");
            playerLink!.RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            Assert(window.HistoryPlayerTabs.Items.Count == 2 && window.HistoryTitle.Text == other.Name &&
                window.CopyPlayerIdButton.Visibility == System.Windows.Visibility.Visible,
                "The match card link opens the player tab with a complete ID to copy");
            completed.Add("玩家头像补全、战绩标签去重、切换保留及关闭");
            if (Environment.GetEnvironmentVariable("LOL_ASSISTANT_PREVIEW") is { Length: > 0 } folder)
                await CapturePreview(window, folder);
            if (Environment.GetEnvironmentVariable("LOL_ASSISTANT_CHECK_OPGG") is { Length: > 0 } profile)
                completed.Add(await CheckOpGgWindow(profile));
            if (Environment.GetEnvironmentVariable("LOL_ASSISTANT_CHECK_CHROME") == "1")
                await CheckMainWindowChrome(league, current, completed);
            window.Close();
            completed.Add("WPF 窗口初始化和布局");
            return (0, string.Join(Environment.NewLine, completed));
        }
        catch (Exception ex)
        {
            completed.Add(ex.ToString());
            return (1, string.Join(Environment.NewLine, completed));
        }
    }

    private static System.Windows.Controls.Button? FindButtonWithContent(System.Windows.DependencyObject element, string content)
    {
        if (element is System.Windows.Controls.Button button && Equals(button.Content, content)) return button;
        var count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(element);
        for (var index = 0; index < count; index++)
            if (FindButtonWithContent(System.Windows.Media.VisualTreeHelper.GetChild(element, index), content) is { } child) return child;
        return null;
    }

    private static async Task CheckIncompleteMatchCard(Player current, List<string> completed)
    {
        var transport = new FixtureTransport { SgpAvailable = true, PartialCard = true };
        var league = new LeagueService(transport);
        var window = new MainWindow(league, current);
        try
        {
            var other = await league.Find("友方#1234", current, default);
            await window.OpenPlayerHistory(other, false);
            var template = (System.Windows.DataTemplate)window.FindResource("MatchCard");
            var root = (System.Windows.FrameworkElement)template.LoadContent();
            root.DataContext = window.MatchGrid.Items[0];
            var allies = (System.Windows.Controls.ItemsControl)root.FindName("CardAllies");
            var opponents = (System.Windows.Controls.ItemsControl)root.FindName("CardOpponents");
            var tags = (ReviewTagStrip)root.FindName("CardReviewTags");
            await window.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.DataBind);
            Assert(allies.Items.Count == 1 && opponents.Items.Count == 0,
                $"Fixture starts with only the viewed player ({allies.Items.Count}, {opponents.Items.Count})");
            var card = (System.Windows.FrameworkElement)root.FindName("MatchPreviewCard");
            card.RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.FrameworkElement.LoadedEvent));
            await window.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.DataBind);
            Assert(allies.Items.Count == 5 && opponents.Items.Count == 5 &&
                tags.Tags?.OfType<ReviewTag>().Any(tag => tag.Label == "汗血宝马") == true,
                "A collapsed card restores both five-player lineups and recalculates its grade");
            window.MatchGrid.SelectedIndex = 0;
            await window.OpenSelectedMatch();
            Assert(transport.CardSummaryLookups == 1, "Opening the scoreboard reuses the restored match");
            completed.Add("折叠战绩卡片自动补齐双方阵容、复算汗血宝马并复用详情数据");
        }
        finally { window.Close(); }
    }

    private static async Task CheckHistoryPagination(Player current, List<string> completed)
    {
        foreach (var (useShard, recoverShard) in new[] { (true, false), (false, false), (false, true) })
        {
            var transport = new FixtureTransport { SgpAvailable = useShard, PagedHistory = true };
            var league = new LeagueService(transport);
            var window = new MainWindow(league, current);
            try
            {
                var other = await league.Find("友方#1234", current, default);
                await window.OpenPlayerHistory(other, false);
                Assert(window.MatchGrid.Items.Count == 20 && window.MoreButton.IsEnabled, "The first full history page offers older games");
                transport.SgpAvailable = useShard || recoverShard;
                await window.LoadMoreHistory();
                Assert(window.MatchGrid.Items.Count == 22 && !window.MoreButton.IsEnabled &&
                    window.HistoryStatus.Text.StartsWith(useShard || recoverShard ? "大区战绩" : "客户端战绩") &&
                    window.HistoryStatus.Text.Contains("最早 2023-"),
                    "Paging skips duplicate pages and uses the correct offset after recovering the shard source");
            }
            finally { window.Close(); }
        }
        completed.Add("大区及客户端战绩连续翻页、跳过全重复页和最早日期");
    }

    private static async Task CheckMainWindowChrome(LeagueService league, Player current, List<string> completed)
    {
        var window = new MainWindow(league, current, new AssistantPreferences())
        {
            WindowStartupLocation = System.Windows.WindowStartupLocation.Manual, Left = -16000, Top = 80,
            ShowActivated = false, ShowInTaskbar = false, Opacity = 0
        };
        window.Show();
        try
        {
            window.UpdateLayout();
            var handle = new System.Windows.Interop.WindowInteropHelper(window).Handle;
            Assert(System.Windows.Shell.WindowChrome.GetWindowChrome(window).CornerRadius == new System.Windows.CornerRadius(12),
                "The desktop window uses a rounded frame");
            var caption = window.TitleBar.PointToScreen(new System.Windows.Point(240, 25));
            var button = window.MaximizeButton.PointToScreen(new System.Windows.Point(22, 18));
            var resize = ((System.Windows.FrameworkElement)window.Content).PointToScreen(new System.Windows.Point(2, 110));
            Assert(HitTest(caption) == 2 && HitTest(button) == 1 && HitTest(resize) == 10,
                $"Window hit-testing distinguishes caption, clickable controls and resize border ({HitTest(caption)}, {HitTest(button)}, {HitTest(resize)})");
            var click = System.Windows.Controls.Primitives.ButtonBase.ClickEvent;
            window.MaximizeButton.RaiseEvent(new System.Windows.RoutedEventArgs(click));
            await Task.Delay(100);
            Assert(window.WindowState == System.Windows.WindowState.Maximized, "Maximize button invokes native resizing");
            window.MaximizeButton.RaiseEvent(new System.Windows.RoutedEventArgs(click));
            await Task.Delay(100);
            Assert(window.WindowState == System.Windows.WindowState.Normal, "Restore button restores the window");
            window.MinimizeButton.RaiseEvent(new System.Windows.RoutedEventArgs(click));
            await Task.Delay(100);
            Assert(window.WindowState == System.Windows.WindowState.Minimized, "Minimize button minimizes the window");
            completed.Add("圆角窗口、原生标题栏拖动、边缘缩放及最大化／还原／最小化");

            long HitTest(System.Windows.Point point)
            {
                var coordinates = (int)(ushort)(short)point.X | ((int)(ushort)(short)point.Y << 16);
                return SendMessage(handle, 0x0084, IntPtr.Zero, new IntPtr(coordinates)).ToInt64();
            }
        }
        finally { window.Close(); }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "SendMessageW")]
    private static extern IntPtr SendMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

    private static async Task<string> CheckOpGgWindow(string profile)
    {
        var side = new OpGgWindow(profile) { Left = -16000, Top = 0, Height = 780 };
        side.Show();
        try
        {
            await side.OpenPage(MainWindow.OpGgUrl("", 3), "中路 · 英雄排行").WaitAsync(TimeSpan.FromSeconds(20));
            var web = side.Browser.CoreWebView2;
            Assert(web is not null, side.PageStatus.Text);
            var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            web!.NavigationCompleted += (_, args) =>
            {
                if (args.WebErrorStatus != Microsoft.Web.WebView2.Core.CoreWebView2WebErrorStatus.OperationCanceled)
                    done.TrySetResult(args.IsSuccess);
            };
            Assert(await done.Task.WaitAsync(TimeSpan.FromSeconds(25)) &&
                web.Source.Contains("position=mid", StringComparison.Ordinal),
                $"OP.GG role page rendered in WebView2 ({web.Source}, {web.DocumentTitle})");
            done = new(TaskCreationOptions.RunContinuationsAsynchronously);
            await side.OpenPage(MainWindow.OpGgUrl("Ahri", 3), "九尾妖狐 · 中路");
            Assert(await done.Task.WaitAsync(TimeSpan.FromSeconds(25)) &&
                web.Source.Contains("/ahri/build/mid", StringComparison.Ordinal) &&
                web.DocumentTitle.Contains("OP.GG", StringComparison.OrdinalIgnoreCase),
                $"OP.GG hero page rendered in WebView2 ({web.Source}, {web.DocumentTitle})");
            return "侧边窗口 WebView2 已加载 OP.GG 排行并切换至英雄攻略";
        }
        finally { side.Close(); }
    }

    private static void Assert(bool pass, string name)
    {
        if (!pass) throw new InvalidOperationException(name);
    }

    private static void CheckPerformance()
    {
        var leader = new CombatStats(10, 1, 12, 30000, 210, 100, 3000, null, 2, 6000, 45,
            Feats: new(2, 1, 1, 1, 5, 3, 2, 9));
        var ordinary = new CombatStats(4, 6, 6, 10000, 110, 10, 0, null, 0, 0, 12);
        var trailing = new CombatStats(0, 12, 1, 2000, 20, null, null, null, 0, null, 2);
        var players = Enumerable.Range(0, 10).Select(i => new Participant(new($"{i}", i, $"示例 {i}"),
            1, i < 5 ? 100 : 200, i < 5, "", 0, 0, "",
            i == 0 ? leader : i == 4 ? trailing : ordinary)).ToArray();
        var match = new Match(1, 420, null, TimeSpan.FromMinutes(30), players);
        var best = PerformanceReview.Evaluate(match, players[0]);
        Assert(best.Grade?.Label == "汗血宝马" && best.Place == 1 &&
            new[] { "控制", "护盾", "补兵", "拆塔专家", "视野", "伤害", "参团", "双杀", "三杀", "四杀", "五杀",
                "越塔 × 3", "塔之子", "MVP", "翻盘局" }.All(label => best.Tags.Any(tag => tag.Label == label)),
            "Complete match earns documented labels");
        var worst = PerformanceReview.Evaluate(match, players[4]);
        Assert(worst.Grade?.Label == "路边一条" && worst.Tags.All(tag => tag.Label is not ("护盾" or "拆塔")),
            "Missing metric is distinct from a recorded zero");
        var losing = players.Select(p => p with { Won = !p.Won }).ToArray();
        var tough = PerformanceReview.Evaluate(match with { Players = losing }, losing[0]);
        Assert(tough.Tags.Any(tag => tag.Label == "SVP") && tough.Tags.Any(tag => tag.Label == "尽力局") &&
            tough.Tags.All(tag => tag.Label != "翻盘局"), "Losing leader gets SVP and a close-performance label");
        var brief = match with { Duration = TimeSpan.FromMinutes(15), Surrendered = true };
        Assert(PerformanceReview.Evaluate(brief, players[0]).Tags.Any(tag => tag.Label == "福利局") &&
            PerformanceReview.Evaluate(brief, players[5]).Tags.All(tag => tag.Label != "福利局"),
            "Early surrender only gives an easy-win label to the winner");
        var quickDive = players[0] with { Combat = leader with { Feats = leader.Feats! with { EnemyTowerKills = 2 } } };
        Assert(PerformanceReview.Evaluate(match with { Players = [quickDive] }, quickDive).Tags.Any(tag => tag.Label == "越塔"),
            "One or two dives omit the number from the label");
        var statsSource = JsonSerializer.Deserialize<JsonElement>("""{"doubleKills":3,"largestMultiKill":4,"challenges":{"killsNearEnemyTurret":2,"maxKillDeficit":11}}""");
        Assert(MatchFeats.Read(statsSource, default) is { Doubles: 3, Largest: 4, EnemyTowerKills: 2, KillDeficit: 11 },
            "Match achievement fields support direct and nested challenge data");
        var timelinePlayers = players.Select((p, i) => p with { SlotId = i + 1, Combat = p.Combat! with
            { Kills = i == 0 ? 3 : i == 5 ? 1 : 0, Feats = MatchFeats.Read(default, default) } }).ToArray();
        var timeline = JsonSerializer.Deserialize<JsonElement>("""
            {"frames":[{"events":[
              {"type":"CHAMPION_KILL","killerId":6,"victimId":1,"timestamp":100,"position":{"x":0,"y":0}},
              {"type":"CHAMPION_KILL","killerId":1,"victimId":6,"timestamp":200,"position":{"x":4000,"y":4000}},
              {"type":"BUILDING_KILL","buildingType":"TOWER_BUILDING","teamId":200,"timestamp":300,"position":{"x":4000,"y":4000}},
              {"type":"CHAMPION_KILL","killerId":1,"victimId":6,"timestamp":400,"position":{"x":4000,"y":4000}},
              {"type":"CHAMPION_KILL","killerId":1,"victimId":6,"timestamp":450,"position":{"x":1000,"y":1000}}]},
              {"events":[{"type":"BUILDING_KILL","buildingType":"TOWER_BUILDING","teamId":100,"timestamp":1200,"position":{"x":1000,"y":1000}}]}]}
            """);
        var enriched = MatchTimeline.Enrich(match with { Players = timelinePlayers }, timeline);
        Assert(enriched?.Players[0].Combat?.Feats is { EnemyTowerKills: 1, OwnTowerKills: 1, KillDeficit: 1 },
            "Timeline counts the towers still standing when a champion was killed");
        Assert(PerformanceReview.Evaluate(match with { Players = [players[0]] }, players[0]).Grade is null,
            "Partial roster must not receive a team grade");
        Assert(PerformanceReview.Evaluate(match with { EarlyEnd = true }, players[0]).Tags.Length == 0, "Remake is excluded");
        Assert(PerformanceReview.Recent([best, best]).All(tag => tag.Kind != "gold") &&
            PerformanceReview.Recent([best, best, best]).Any(tag => tag.Label == "3 场样本"),
            "Grade requires three complete games");
        var lcu = CombatStats.Read(JsonSerializer.Deserialize<JsonElement>("""{"kills":0,"damageDealtToObjectives":9999}"""));
        Assert(lcu.Kills == 0 && lcu.Shield is null && lcu.TurretDamage is null, "No objectives-to-turrets substitution");
    }

    private static void CheckHistoryInsights()
    {
        var stats = new CombatStats(3, 2, 6, 10000, 100, null, null, null, null, null, null, 9000, 8500);
        var self = new Participant(new("own", 1, "本机#1234"), 84, 100, true, "3 / 2 / 6", 100, 10000, "", stats);
        var ally = self with { Player = new("friend", 2, "友方#1234"), Champion = 157 };
        var opponent = self with { Player = new("other", 3, "对方#1234"), Team = 200 };
        var hidden = self with { Player = new("", 0, "") };
        var game = new Match(50, 420, null, TimeSpan.FromMinutes(30), [self, ally, ally, opponent, hidden]);
        var recent = new[] { new PlayerMatch(game, self) };
        Assert(HistoryInsights.Teammates(recent) is [{ Player.Puuid: "friend", Wins: 1, Games: 1 }],
            "Teammates exclude the target, duplicates, the enemy team and hidden names");
        Assert(HistoryInsights.Encounters([new(game, ally)], self.Player) is [{ Self.Player.Puuid: "own" }] &&
            HistoryInsights.Encounters(recent, self.Player).Length == 0, "Shared games link two distinct players");
        Assert(HistoryInsights.Summary(recent) is [{ Label: "平均 KDA", Value: "4.50" }],
            "Partial lineups exclude team shares");
        var fullTeam = new[] { self, ally, hidden, hidden with { Champion = 99 }, hidden with { Champion = 111 } };
        var complete = HistoryInsights.Summary([new(game with { Players = fullTeam }, self)]);
        Assert(complete.Any(metric => metric is { Label: "承伤比", Value: "20%" }) &&
            complete.Any(metric => metric is { Label: "经济比", Value: "20%" }), "Team metrics use actual per-participant fields");
        var matchWithCompleteTeam = game with { Players = fullTeam };
        var radar = RadarSnapshot.Build(matchWithCompleteTeam, self);
        Assert(radar is { Axes.Length: >= 3 } && radar.Axes.Any(axis => axis.Name == "承伤") &&
            RadarSnapshot.Build(game with { Players = [self] }, self) is null &&
            LeagueService.PlainDescription("A<br><b>B</b> &amp; C") == "A\nB & C", "Radar uses measured team fields and cleans description markup");
        var metricTags = HistoryInsights.MetricTags([new(matchWithCompleteTeam, self), new(matchWithCompleteTeam, self)], recent: true);
        Assert(metricTags.Any(tag => tag is { Label: "2 连胜", Kind: "high" }) &&
            metricTags.Any(tag => tag.Kind == "damage" && tag.Label.StartsWith("出伤 ") && tag.Reason.Contains("2 场")) &&
            metricTags.Any(tag => tag.Kind == "objective" && tag.Label.StartsWith("伤转率 ")) &&
            metricTags.All(tag => !tag.Label.StartsWith("问号 ") && !tag.Label.StartsWith("单杀 ")),
            "Colored metric tags use ratios, sample counts and optional fields");
        var optional = new Participant(self.Player, 84, 100, true, self.Kda, 100, 10000, "",
            stats with { SoloKills = 2, MissingPings = 4, Vision = 29 });
        var personal = HistoryInsights.MetricTags([new(game with { Players = [optional] }, optional)]);
        Assert(personal.Any(tag => tag is { Label: "单杀 2", Kind: "control" }) &&
            personal.Any(tag => tag is { Label: "问号 4", Kind: "ping" }) &&
            personal.Any(tag => tag is { Label: "视野 29", Kind: "vision" }) &&
            personal.All(tag => !tag.Label.StartsWith("出伤 ")), "Individual metrics work without inventing team shares");
        var rosterTags = HistoryInsights.RosterTags(
            [new(matchWithCompleteTeam, self), new(matchWithCompleteTeam, self), new(game with { Players = [optional] }, optional)]);
        Assert(rosterTags.Length == 3 && rosterTags.Any(tag => tag.Label == "3 连胜") &&
            rosterTags.All(tag => tag.Kind is not ("sample" or "ping" or "economy")),
            "Current game cards keep three useful tags and omit lower-priority metrics");
    }

    private static async Task CapturePreview(MainWindow window, string folder)
    {
        var rows = new[]
        {
            (84, "阿卡丽", true, "12 / 4 / 8"),
            (157, "亚索", false, "6 / 7 / 5"),
            (103, "阿狸", true, "9 / 2 / 11"),
            (222, "金克丝", true, "14 / 3 / 6"),
            (266, "亚托克斯", false, "5 / 5 / 7")
        };
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var connection = ClientDiscovery.Find(null);
        var transport = connection is null ? null : new LeagueTransport(connection);
        var itemIcons = new List<object>();
        var spellIcons = new List<object>();
        var runeIcons = new List<object>();
        var augmentIcons = new List<object>();
        if (transport is not null)
        {
            var league = new LeagueService(transport);
            await league.LoadMetadata(timeout.Token);
            async Task LoadIcons(List<object> icons, int[] ids, Func<int, string> iconPath, Func<int, string> iconName, bool augment = false)
            {
                foreach (var id in ids)
                {
                    var path = iconPath(id);
                    if (path.Length == 0 || await transport.ReadIcon(path, timeout.Token) is not { } bytes) continue;
                    var portrait = new ChampionPortrait();
                    portrait.SetImage(ChampionPortraits.Decode(bytes));
                    icons.Add(new { Portrait = portrait, Tooltip = iconName(id), IsAugment = augment });
                }
            }
            await LoadIcons(itemIcons, [3020, 3089, 3157, 3137, 4645, 3340], league.ItemIcon, league.ItemName);
            await LoadIcons(spellIcons, [4, 14], league.SpellIcon, league.SpellName);
            await LoadIcons(runeIcons, [8005, 9111, 9104, 8014, 8139, 8135], league.PerkIcon, league.PerkName);
            await LoadIcons(augmentIcons, [1079, 2125, 2009, 2096, 1004, 2055], league.AugmentIcon, league.AugmentName, augment: true);
        }
        var portraits = new List<ChampionPortrait>();
        foreach (var row in rows)
        {
            var portrait = new ChampionPortrait();
            if (transport is not null && await transport.ChampionIcon(row.Item1, timeout.Token) is { } bytes)
                portrait.SetImage(ChampionPortraits.Decode(bytes));
            portraits.Add(portrait);
        }
        window.ConnectionStatus.Text = "已连接 · 示例玩家";
        window.HistoryTitle.Text = "示例玩家#1234";
        window.CopyPlayerIdButton.Visibility = System.Windows.Visibility.Visible;
        window.SearchInput.Text = "示例玩家#1234";
        window.AccountAvatar.DataContext = portraits[0];
        window.HistoryPlayerTabs.ItemsSource = new[]
        {
            new { Title = "我的战绩", Tooltip = "本机玩家#1234", CanClose = false, Avatar = portraits[2] },
            new { Title = "示例玩家#1234", Tooltip = "示例玩家#1234", CanClose = true, Avatar = portraits[0] },
            new { Title = "队友#5678", Tooltip = "队友#5678", CanClose = true, Avatar = portraits[1] }
        };
        window.HistoryPlayerTabs.SelectedIndex = 1;
        window.AccountSubtitle.Text = "292 级";
        window.SoloRank.Text = "翡翠 IV";
        window.SoloRecord.Text = "35 胜 25 负 · 58% · 12 LP";
        window.FlexRank.Text = "铂金 II";
        window.FlexRecord.Text = "27 胜 21 负 · 56% · 23 LP";
        MainWindow.SetRankEmblem(window.SoloEmblem, "EMERALD", "翡翠 IV");
        MainWindow.SetRankEmblem(window.FlexEmblem, "PLATINUM", "铂金 II");
        window.RecentRate.Text = "60%";
        window.SetWinRing(.60);
        window.RecentRecord.Text = "3 胜 2 负";
        window.RecentCs.Text = "6.7 补刀 / 分";
        window.HistoryStatus.Text = "大区战绩 · 已读取 5 场";
        window.MoreButton.Visibility = System.Windows.Visibility.Visible;
        window.MoreButton.IsEnabled = true;
        window.StatsCaption.Text = "当前筛选 · 5 场";
        window.MasteryStatus.Text = "总等级 83";
        window.MasteryList.ItemsSource = rows.Select((row, index) => new
            { Champion = row.Item2, Level = 25 - index * 4, Points = 297_500 - index * 45_000, Portrait = portraits[index] }).ToArray();
        window.AssetStatus.Text = "";
        window.AssetList.ItemsSource = new CollectionCount[]
            { new("英雄", 167), new("皮肤", 142), new("炫彩皮肤", 51), new("守卫皮肤", 19), new("召唤师图标", 94), new("表情", 118) };
        var sampleTags = new ReviewTag[]
        {
            new("汗血宝马", "示例队内评分 89 / 100", "gold"),
            new("MVP", "胜方队内评分第一", "mvp"),
            new("翻盘局", "本方一度落后 9 个人头，最终获胜。", "comeback"),
            new("三杀", "客户端记录 1 次三杀", "multikill"),
            new("越塔 × 4", "在敌方防御塔附近击杀 4 次", "dive"),
            new("控制", "对敌控制 82 秒", "control"),
            new("护盾", "为队友提供 2,500 护盾", "support"),
            new("补兵", "小兵 210 · 7.0 个 / 分", "economy"),
            new("拆塔", "摧毁 2 座塔", "objective")
        };
        var sampleMetrics = new ReviewTag[]
        {
            new("出伤 23%", "本人输出占同队总输出", "damage"),
            new("承伤 19%", "本人承伤占同队总承伤", "support"),
            new("经济 21%", "本人金币占同队总金币", "economy"),
            new("伤转率 179%", "对英雄伤害 ÷ 本人金币", "objective"),
            new("视野 29.5", "场均视野得分", "vision"),
            new("单杀 1.1", "场均单杀", "control"),
            new("问号 2.3", "场均敌人消失信号次数", "ping")
        };
        window.HistoryTags.ItemsSource = new ReviewTag[] { new("上等马", "示例点评", "high"), new("5 场样本", "示例样本", "middle") };
        object[] PreviewPlayers(int offset) => rows.Select((_, i) =>
        {
            var index = (i + offset) % rows.Length;
            return new
            {
                Champion = rows[index].Item2, Player = $"玩家 {i + 1 + offset}",
                CombatLine = $"{rows[index].Item4.Replace(" ", "")} ({58 + i * 4}%)",
                KdaLine = $"{3.2 + (2 - i) * 0.6:F2} KDA", LevelText = $"{18 - i % 3}",
                Damage = 22320 + (i + offset) * 1371, Taken = 19100 + (4 - i + offset) * 1250, Gold = 11520 + i * 560,
                DamagePercent = 51d + (i + offset) * 5, TakenPercent = 57d + (4 - i + offset) * 4,
                Cs = 172 + i * 9, GoldPerMinute = $"{(11520 + i * 560) / 28d:N0} / 分",
                Portrait = portraits[index], Items = itemIcons, Spells = spellIcons, Modifiers = runeIcons,
                ReviewNote = sampleTags[0].Reason, CanOpenHistory = true
            };
        }).ToArray();
        var blue = PreviewPlayers(0);
        var red = PreviewPlayers(5);
        var radar = new RadarSnapshot([
            new("伤害", "22,320", "18,886", .81, .69), new("承伤", "24,100", "17,370", .90, .64),
            new("金币", "11,520", "10,864", .83, .78), new("补刀", "172", "148", .81, .70),
            new("KDA", "5.00", "3.20", .89, .57), new("参团率", "58%", "61%", .72, .76), new("治疗", "1,260", "1,920", .46, .70)
        ]);
        object[] MatchSamples(bool expanded) => rows.Select((row, i) => new
        {
            Champion = row.Item2, Won = row.Item3, Result = row.Item3 ? "胜利" : "失败", Kda = row.Item4,
            Damage = (22320 + i * 1371).ToString("N0"), Cs = 172 + i * 9,
            Caption = $"{row.Item2}  ·  {(i == 1 ? "海克斯大乱斗" : "单双排")}  ·  11-21 20:{44 - i * 9:00}  ·  28:{i * 9:00}", Portrait = portraits[i],
            Items = itemIcons, Spells = spellIcons, PreviewModifiers = i == 1 ? augmentIcons : runeIcons.Take(2), Radar = radar,
            KdaSummary = "3.20 KDA · 61%", DamageShare = $" {22 - i * 2}%", CsPerMinute = "6.7 / 分",
            IsExpanded = expanded && i == 0, DetailLabel = expanded && i == 0 ? "收起详情" : "对局详情 ›", DetailStatus = "",
            Tags = i == 0 ? sampleTags.Concat(sampleMetrics.Take(4)).ToArray() :
                [new ReviewTag("中等马", "示例队内评分", "middle"), .. sampleMetrics.Take(4)],
            Teams = new[]
            {
                new { Name = "蓝方", Won = true, Result = "胜利", Summary = "45 / 25 / 64  ·  63,200 金币", Objectives = "塔 8  ·  龙 3  ·  男爵 1", Players = blue },
                new { Name = "红方", Won = false, Result = "失败", Summary = "25 / 45 / 49  ·  57,100 金币", Objectives = "塔 4  ·  龙 1  ·  男爵 0", Players = red }
            },
            Allies = portraits.Select((p, n) => new { Portrait = p, Title = $"示例队友 {n + 1}", Tooltip = rows[n].Item2, IsViewed = n == i, CanOpenHistory = true }).ToArray(),
            Opponents = portraits.AsEnumerable().Reverse().Select((p, n) => new { Portrait = p, Title = $"示例对手 {n + 1}", Tooltip = "对方英雄", IsViewed = false, CanOpenHistory = true }).ToArray()
        }).ToArray();
        window.MatchGrid.ItemsSource = MatchSamples(false);
        window.StatsList.ItemsSource = rows.Select((row, i) => new
        {
            Champion = row.Item2, Summary = row.Item3 ? "1 场 · 1 胜 0 负" : "1 场 · 0 胜 1 负", RateLabel = "样本少", Portrait = portraits[i]
        }).ToArray();
        window.HistoryMetrics.ItemsSource = new HistoryMetric[] { new("平均 KDA", "3.32"), new("参团率", "64%"),
            new("伤害比", "23%"), new("承伤比", "19%"), new("经济比", "21%") };
        window.TeammatesList.ItemsSource = rows.Select((row, i) => new
            { Title = $"队友 {i + 1}", CanOpenHistory = true, Record = "2 胜 1 负", Portrait = portraits[i] }).ToArray();
        window.RunePortrait.DataContext = portraits[0];
        window.RuneTitle.Text = "阿卡丽";
        window.RuneList.ItemsSource = new[] { new { Name = "征服者", Summary = "国服客户端推荐", Perks = "征服者、气定神闲、传说、坚毅不倒、复苏之风、过度生长" } };
        object[] Games(int offset) => Enumerable.Range(0, 10).Select(n =>
        {
            var index = (n + offset) % rows.Length;
            var game = rows[index];
            return new
            {
                Mode = n % 2 == 0 ? "灵活排位" : "匹配",
                Time = new DateTime(2026, 11, 21, 19, 54, 0).AddMinutes(-n * 15).ToString("MM-dd HH:mm"),
                Won = game.Item3, Kda = game.Item4, Result = game.Item3 ? "胜" : "负", Portrait = portraits[index]
            };
        }).ToArray();
        window.RosterStatus.Text = "点击玩家名称或近期对局";
        window.PickTools.Visibility = System.Windows.Visibility.Visible;
        window.OpponentPanel.Visibility = System.Windows.Visibility.Collapsed;
        window.OpRole.SelectedIndex = 3;
        window.OpBuildButton.IsEnabled = true;
        var seatRanks = new RankSummary[]
        {
            new("黄金 III", "", 14, "GOLD"), new("未定级", "", null), new("翡翠 IV", "", 0, "EMERALD"),
            new("未定级", "", null), new("铂金 II", "", 32, "PLATINUM")
        };
        var seatWins = new[] { 13, 20, 8, 10, 17 };
        window.BlueSeats.ItemsSource = rows.Select((row, i) => new
        {
            Title = $"蓝方玩家 {i + 1}", Champion = row.Item2, Summary = "", Portrait = portraits[i],
            Stats = new PlayerRecord(seatRanks[i], seatWins[i], 20 - seatWins[i], ""),
            RankIcon = MainWindow.RankEmblem(seatRanks[i].Tier),
            Tags = i == 0 ? [new ReviewTag("3 连胜", "近期连胜", "high"), .. sampleMetrics.Take(2)] : sampleMetrics.Take(3).ToArray(),
            CanOpenHistory = true, Games = Games(i)
        }).ToArray();
        window.RedSeats.ItemsSource = rows.Reverse().Select((row, i) => new
        {
            Title = $"红方玩家 {i + 1}", Champion = row.Item2, Summary = "", Portrait = portraits[rows.Length - 1 - i],
            Stats = new PlayerRecord(seatRanks[i], seatWins[i], 20 - seatWins[i], ""),
            RankIcon = MainWindow.RankEmblem(seatRanks[i].Tier),
            Tags = sampleMetrics.Take(3).ToArray(),
            CanOpenHistory = true, Games = Games(i + 2)
        }).ToArray();
        var root = (System.Windows.FrameworkElement)window.Content;
        System.IO.Directory.CreateDirectory(folder);
        for (var i = 0; i < 4; i++)
        {
            window.Pages.SelectedIndex = i % 3;
            if (i == 3) window.MatchGrid.ItemsSource = MatchSamples(true);
            var size = i == 3 ? new System.Windows.Size(1170, 1070) : new System.Windows.Size(1170, 725);
            SavePreview(root, size, $"page-{i}.png");
        }
        var tip = new System.Windows.Controls.ToolTip { Content = radar };
        SavePreview(tip, new System.Windows.Size(375, 340), "radar.png");

        void SavePreview(System.Windows.FrameworkElement element, System.Windows.Size size, string name)
        {
            element.Measure(size);
            element.Arrange(new System.Windows.Rect(size));
            element.UpdateLayout();
            var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap((int)size.Width, (int)size.Height, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
            bitmap.Render(element);
            var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
            encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
            using var file = System.IO.File.Create(System.IO.Path.Combine(folder, name));
            encoder.Save(file);
        }
    }

    private sealed class FixtureTransport : ILeagueTransport
    {
        private const string Other = """{"puuid":"other","summonerId":2,"gameName":"友方","tagLine":"1234"}""";
        private const string Own = """{"puuid":"own","summonerId":1,"gameName":"本机","tagLine":"1234"}""";
        private const string Match = """{"gameId":100,"queueId":420,"participants":[{"participantId":1,"championId":157,"teamId":100,"stats":{"kills":4,"deaths":2,"assists":6,"win":true}}],"participantIdentities":[{"participantId":1,"player":{"puuid":"other","summonerId":2}}]}""";
        private const string Rune = """{"id":92,"name":"战绩助手 · 本局","isEditable":true,"primaryStyleId":8000,"subStyleId":8100,"selectedPerkIds":[8008,9111,9104,8014,8139,8135,5005,5008,5001]}""";
        public int Writes { get; private set; }
        public bool Created { get; set; }
        public bool Full { get; set; }
        public bool SgpAvailable { get; set; }
        public string Phase { get; set; } = "ChampSelect";
        public bool SessionAvailable { get; set; } = true;
        public bool PickFixture { get; set; }
        public bool InPickTurn { get; set; }
        public int PickedChampion { get; set; }
        public bool MultipleMatches { get; set; }
        public int ProfileMatchCount { get; set; }
        public bool EmptyShardProfile { get; set; }
        public bool PartialCard { get; set; }
        public bool PagedHistory { get; set; }
        public int CardSummaryLookups { get; private set; }
        private bool _accepted;
        public JsonElement LastPick { get; private set; }
        public List<int> PickSlotLookups { get; } = [];
        public string LiveRoster { get; set; } = "[]";

        public Task<JsonElement> Call(HttpMethod method, string path, CancellationToken ct, object? body = null)
        {
            ct.ThrowIfCancellationRequested();
            if (method != HttpMethod.Get) Writes++;
            var response = path switch
            {
                "/lol-summoner/v1/current-summoner" => Own,
                "/lol-champions/v1/owned-champions-minimal" => """[{"id":84,"ownership":{"owned":true}},{"id":157,"ownership":{"owned":false}}]""",
                "/lol-inventory/v2/inventory/CHAMPION_SKIN" => """[{"itemId":84001},{"itemId":84003},{"itemId":84000},{"itemId":84005,"rental":true}]""",
                "/lol-game-data/assets/v1/skins.json" => """{"84001":{"id":84001,"chromas":[{"id":84003}]}}""",
                "/lol-inventory/v2/inventory/WARD_SKIN" or "/lol-inventory/v2/inventory/SUMMONER_ICON" or "/lol-inventory/v2/inventory/EMOTE"
                    => """[{"itemId":25},{"itemId":25}]""",
                "/lol-champion-mastery/v1/local-player/champion-mastery" => """[{"championId":84,"championLevel":5,"championPoints":20000},{"championId":157,"championLevel":8,"championPoints":50000}]""",
                "/lol-game-data/assets/v1/cherry-augments.json" => """[{"id":1079,"augmentNameId":"ARAM_SymphonyofWar","nameTRA":"战争交响乐","summaryDescription":""},{"id":2125,"augmentNameId":"ItsGoTime","nameTRA":"前进时间到"}]""",
                "/lol-game-data/assets/v1/summoner-spells.json" => """[{"id":4,"name":"闪现","description":"位移到附近位置。"}]""",
                "/lol-game-data/assets/v1/perks.json" => """[{"id":8005,"name":"强攻","longDesc":"<attention>攻击</attention>可叠加层数。"}]""",
                "/lol-summoner/v1/summoners/2" => Other,
                "/lol-summoner/v2/summoners/puuid/other" => """{"puuid":"other","summonerId":2,"gameName":"友方","tagLine":"1234","profileIconId":0,"summonerLevel":122}""",
                var alias when alias.StartsWith("/lol-summoner/v1/alias/lookup") => Other,
                "/entitlements/v1/token" => """{"issuer":"http://nj100-bcs-internal.lol.qq.com:28088","accessToken":"sample"}""",
                var history when PagedHistory && history.StartsWith("/lol-match-history/v1/products/lol/") =>
                    """{"games":{"games":[""" + HistoryFixture(history.Contains("begIndex=40&")) + "]}}",
                var history when history.StartsWith("/lol-match-history/v1/products/lol/") => """{"games":{"games":[""" + Match + "]}}",
                "/lol-match-history/v1/games/100" => """{"gameId":100,"participants":[{"teamId":100},{"teamId":200}]}""",
                "/lol-gameflow/v1/gameflow-phase" => JsonSerializer.Serialize(Phase),
                "/lol-matchmaking/v1/ready-check" => _accepted ? """{"state":"InProgress","playerResponse":"Accepted"}""" : """{"state":"InProgress","playerResponse":"None"}""",
                "/lol-matchmaking/v1/ready-check/accept" when method == HttpMethod.Post => JsonSerializer.Serialize(_accepted = true),
                "/lol-champ-select/v1/session" when PickFixture => JsonSerializer.Serialize(new
                {
                    gameId = 55, localPlayerCellId = 0, myTeam = new[] { new { cellId = 0, championId = PickedChampion } },
                    actions = new[] { new[] { new { id = 37, actorCellId = 0, type = "pick", isInProgress = InPickTurn, championId = PickedChampion } } }
                }),
                "/lol-champ-select/v1/pickable-champion-ids" => "[84]",
                "/lol-champ-select/v1/session/actions/37" when method == HttpMethod.Patch => (LastPick = JsonSerializer.SerializeToElement(body)).GetRawText(),
                "/lol-champ-select/v1/session" => """{"gameId":55,"queueId":420,"localPlayerCellId":0,"myTeam":[{"cellId":0,"championId":157,"assignedPosition":"middle"},{"cellId":1,"championPickIntent":84,"puuid":"other","isHumanoid":false},{"cellId":2,"nameVisibilityType":"HIDDEN","puuid":"hidden","summonerId":99,"gameName":"隐藏","isHumanoid":false},{"cellId":3,"puuid":"00000000-0000-0000-0000-000000000000","obfuscatedSummonerId":9,"obfuscatedPuuid":"unknown"},{"cellId":4,"summonerId":2},{"cellId":5},{"cellId":7,"playerType":"BOT"}],"theirTeam":[{"cellId":6,"nameVisibilityType":"UNHIDDEN","gameName":"对手","puuid":"enemy"}]}""",
                "/lol-champ-select/v1/summoners/3" => ReadPickSlot(3, """{"isAnonymous":true,"displayName":"占位"}"""),
                "/lol-champ-select/v1/summoners/5" => ReadPickSlot(5, """{"displayName":"友方","riotIdTagline":"1234"}"""),
                "/lol-gameflow/v1/session" when !SessionAvailable => throw new LeagueException(404, "fixture session unavailable"),
                "/lol-gameflow/v1/session" => $$$"""{"phase":"{{{Phase}}}","map":{"id":11},"gameData":{"gameId":55,"teamOne":[{{{Own}}}],"teamTwo":[{"gameName":"匿名","puuid":"hidden","isAnonymous":true}]}}""",
                "/lol-perks/v1/recommended-pages/champion/157/position/MIDDLE/map/11" => """[{"primaryPerkStyleId":8000,"secondaryPerkStyleId":8100,"perks":[{"id":8008},{"id":9111},{"id":9104},{"id":8014},{"id":8139},{"id":8135},{"id":5005},{"id":5008},{"id":5001}]}]""",
                "/lol-perks/v1/pages" when method == HttpMethod.Post => CreatePage(),
                "/lol-perks/v1/pages" when Full => """[{"id":89,"name":"用户配置","isEditable":true}]""",
                "/lol-perks/v1/pages" when Created => "[" + Rune + "]",
                "/lol-perks/v1/pages" => "[]",
                "/lol-perks/v1/inventory" => """{"ownedPageCount":1}""",
                "/lol-perks/v1/currentpage" => Rune,
                _ => throw new LeagueException(404, "fixture path unavailable")
            };
            return Task.FromResult(Json(response));
        }

        public Task<JsonElement> Shard(Uri url, string bearer, string version, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (!SgpAvailable) throw new LeagueException(503, "fixture unavailable");
            if (PagedHistory && url.Query.Contains("startIndex="))
                return Task.FromResult(Json("""{"games":[""" + HistoryFixture(url.Query.Contains("startIndex=40&")) + "]}"));
            if (PartialCard && url.AbsolutePath.EndsWith("_100/SUMMARY"))
            {
                CardSummaryLookups++;
                return Task.FromResult(JsonSerializer.SerializeToElement(new
                {
                    json = new
                    {
                        gameId = 100, queueId = 420, gameDuration = 1800,
                        participants = Enumerable.Range(0, 10).Select(i => new
                        {
                            participantId = i + 1, puuid = i == 0 ? "other" : $"peer-{i}",
                            gameName = i == 0 ? "友方" : $"队员 {i}", tagLine = "1234",
                            championId = 157, teamId = i < 5 ? 100 : 200, win = i < 5,
                            kills = i == 0 ? 10 : 4, deaths = i == 0 ? 1 : 6, assists = i == 0 ? 12 : 6,
                            totalDamageDealtToChampions = i == 0 ? 30000 : 10000, damageDealtToTurrets = i == 0 ? 6000 : 0
                        }).ToArray()
                    }
                }));
            }
            if (PartialCard && url.Query.Contains("startIndex=0&"))
                return Task.FromResult(Json("""{"games":[""" + Match + "]}"));
            if (EmptyShardProfile || !url.Query.Contains("startIndex=0&")) return Task.FromResult(Json("""{"games":[]}"""));
            const string game = """{"json":{"gameId":101,"queueId":420,"gameDuration":1680,"participants":[{"puuid":"other","riotIdGameName":"友方","riotIdTagline":"1234","teamId":200,"championId":157,"win":true,"champLevel":17,"spell1Id":4,"spell2Id":14,"perks":{"styles":[{"selections":[{"perk":8005}]}]},"playerAugment1":12},{"puuid":"own","riotIdGameName":"本机","riotIdTagline":"1234","teamId":200,"championId":84,"win":true}],"teams":[{"teamId":200,"objectives":{"tower":{"kills":4},"dragon":{"kills":0}}},{"teamId":100,"towerKills":5,"baronKills":1}]}}""";
            var games = MultipleMatches ? new[] { game, game.Replace("\"gameId\":101", "\"gameId\":102") } : Enumerable.Repeat(game, 20);
            if (ProfileMatchCount > 0)
                games = Enumerable.Range(0, ProfileMatchCount).Select(index => game.Replace("\"gameId\":101",
                    $"\"gameId\":{101 + index},\"gameCreation\":{1700000000000L + index * 60000L}"));
            return Task.FromResult(Json("""{"games":[""" + string.Join(",", games) + "]}"));
        }

        public Task<JsonElement> LivePlayers(CancellationToken ct) => Task.FromResult(Json(LiveRoster));
        private string ReadPickSlot(int cell, string result) { PickSlotLookups.Add(cell); return result; }
        private string CreatePage() { Created = true; return Rune; }
        private static string HistoryFixture(bool oldest)
            => string.Join(",", Enumerable.Range(oldest ? 0 : 2, oldest ? 2 : 20).Select(index =>
                Match.Replace("\"gameId\":100", $"\"gameId\":{100 + index},\"gameCreation\":{1700000000000L + index * 60000L}")));
        private static JsonElement Json(string text) => JsonSerializer.Deserialize<JsonElement>(text);
    }
}
