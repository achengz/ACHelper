using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace LolMatchAssistant;

internal sealed class LeagueService(ILeagueTransport transport)
{
    public const int PageSize = 20;
    private const string Perks = "/lol-perks/v1";
    private const string PageName = "战绩助手 · 本局";
    private static readonly IReadOnlyDictionary<string, string> ShardHosts = new Dictionary<string, string>
    {
        ["HN1"] = "hn1-k8s-sgp", ["HN10"] = "hn10-k8s-sgp", ["TJ100"] = "tj100-sgp",
        ["TJ101"] = "tj101-sgp", ["NJ100"] = "nj100-sgp", ["GZ100"] = "gz100-sgp",
        ["CQ100"] = "cq100-sgp", ["BGP2"] = "bgp2-k8s-sgp"
    };
    private readonly Dictionary<int, string> _champions = [];
    private readonly Dictionary<int, string> _championAliases = [];
    private readonly Dictionary<string, int> _championIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<int, string> _items = [];
    private readonly Dictionary<int, string> _itemIcons = [];
    private readonly Dictionary<int, string> _perks = [];
    private readonly Dictionary<int, string> _perkIcons = [];
    private readonly Dictionary<int, string> _spells = [];
    private readonly Dictionary<int, string> _spellIcons = [];
    private readonly Dictionary<int, string> _augments = [];
    private readonly Dictionary<int, string> _augmentIcons = [];
    private readonly Dictionary<int, string> _perkDescriptions = [];
    private readonly Dictionary<int, string> _itemDescriptions = [];
    private readonly Dictionary<int, string> _spellDescriptions = [];
    private readonly Dictionary<int, string> _augmentDescriptions = [];
    private static readonly Lazy<Dictionary<string, string>> AugmentText = new(() =>
    {
        using var data = typeof(LeagueService).Assembly.GetManifestResourceStream("ACHelper.Augments.zh-CN.json");
        return data is null ? [] : JsonSerializer.Deserialize<Dictionary<string, string>>(data) ?? [];
    });
    public string Platform { get; private set; } = "";
    public string Version { get; private set; } = "";
    public string ChampionName(int id) => id == 0 ? "待公开" : _champions.GetValueOrDefault(id, $"英雄 {id}");
    public int ChampionId(string name) => _championIds.GetValueOrDefault(name);
    public string ChampionAlias(int id) => _championAliases.GetValueOrDefault(id, "");
    public ChampionChoice[] Champions => _champions.Select(champion => new ChampionChoice(champion.Key, champion.Value))
        .OrderBy(champion => champion.Name, StringComparer.CurrentCulture).ToArray();
    public string PerkName(int id) => _perks.GetValueOrDefault(id, $"符文 {id}");
    public string PerkIcon(int id) => _perkIcons.GetValueOrDefault(id, "");
    public string ItemName(int id) => _items.GetValueOrDefault(id, $"装备 {id}");
    public string ItemIcon(int id) => _itemIcons.GetValueOrDefault(id, "");
    public string SpellName(int id) => _spells.GetValueOrDefault(id, $"召唤师技能 {id}");
    public string SpellIcon(int id) => _spellIcons.GetValueOrDefault(id, "");
    public string AugmentName(int id) => _augments.GetValueOrDefault(id, $"强化符文 {id}");
    public string AugmentIcon(int id) => _augmentIcons.GetValueOrDefault(id, "");
    public string PerkDescription(int id) => _perkDescriptions.GetValueOrDefault(id, "");
    public string ItemDescription(int id) => _itemDescriptions.GetValueOrDefault(id, "");
    public string SpellDescription(int id) => _spellDescriptions.GetValueOrDefault(id, "");
    public string AugmentDescription(int id) => _augmentDescriptions.GetValueOrDefault(id, "");

    public async Task<Player> Current(CancellationToken ct)
    {
        var player = Player.Read(await Get("/lol-summoner/v1/current-summoner", ct));
        if (player.Puuid.Length == 0) throw new LeagueException(0, "请登录并选择英雄联盟大区。");
        return player;
    }

    public async Task LoadMetadata(CancellationToken ct)
    {
        var region = await Optional("/riotclient/region-locale", ct);
        var party = await Optional("/lol-lobby/v1/parties/player", ct);
        var chat = await Optional("/lol-chat/v1/me", ct);
        var reported = chat.Field("lol").Text("gamePlatformId");
        Platform = JsonFields.First(reported, chat.Field("lol").Text("platformId"), party.Text("platformId"), region.Text("webRegion"), region.Text("region")).ToUpperInvariant();
        if (!ShardHosts.ContainsKey(Platform))
        {
            var auth = await Optional("/entitlements/v1/token", ct);
            if (TokenShard(auth) is { } shard) Platform = shard;
        }
        var version = await Optional("/lol-patch/v1/game-version", ct);
        if (version.ValueKind == JsonValueKind.String) Version = version.GetString()?.Split('+')[0] ?? "";
        await LoadNames("/lol-game-data/assets/v1/champion-summary.json", _champions, ct);
        await LoadNames("/lol-game-data/assets/v1/items.json", _items, ct, _itemIcons, descriptions: _itemDescriptions);
        await LoadNames("/lol-game-data/assets/v1/perks.json", _perks, ct, _perkIcons, descriptions: _perkDescriptions);
        await LoadNames("/lol-game-data/assets/v1/summoner-spells.json", _spells, ct, _spellIcons, descriptions: _spellDescriptions);
        await LoadNames("/lol-game-data/assets/v1/cherry-augments.json", _augments, ct, _augmentIcons,
            nameField: "nameTRA", iconField: "augmentSmallIconPath", descriptions: _augmentDescriptions,
            descriptionFallback: entry => AugmentText.Value.GetValueOrDefault(entry.Text("augmentNameId"), ""));
        var styles = await Optional("/lol-game-data/assets/v1/perkstyles.json", ct);
        foreach (var style in styles.Field("styles").Items())
        {
            var id = (int)style.Number("id");
            var name = style.Text("name");
            if (id <= 0 || name.Length == 0) continue;
            _perks[id] = name;
            _perkIcons[id] = style.Text("iconPath");
            _perkDescriptions[id] = PlainDescription(style.Text("description"));
        }
    }

    private async Task LoadNames(string path, Dictionary<int, string> target, CancellationToken ct,
        Dictionary<int, string>? icons = null, string nameField = "name", string iconField = "iconPath",
        Dictionary<int, string>? descriptions = null, Func<JsonElement, string>? descriptionFallback = null)
    {
        foreach (var entry in (await Optional(path, ct)).Items())
        {
            var id = (int)entry.Number("id");
            var name = JsonFields.First(ReferenceEquals(target, _champions) ? entry.Text("description") : "", entry.Text(nameField), entry.Text("name"));
            if (id <= 0 || name.Length == 0) continue;
            target[id] = name;
            if (descriptions is not null)
            {
                var text = PlainDescription(JsonFields.First(entry.Text("longDesc"), entry.Text("descTRA"),
                    entry.Text("summaryDescription"), entry.Text("description"), entry.Text("shortDesc"),
                    entry.Text("tooltipTRA"), entry.Text("tooltip")));
                descriptions[id] = text.Length > 0 ? text : descriptionFallback?.Invoke(entry) ?? "";
            }
            if (icons is not null)
            {
                var icon = entry.Text(iconField);
                if (icon.Length > 0) icons[id] = icon;
            }
            if (ReferenceEquals(target, _champions))
            {
                _championIds.TryAdd(name, id);
                var alias = entry.Text("alias");
                if (alias.Length > 0)
                {
                    _championIds.TryAdd(alias, id);
                    _championAliases[id] = alias;
                }
            }
        }
    }

    internal static string PlainDescription(string text)
    {
        var lines = Regex.Replace(text, @"<(?:br\s*/?|/?li|/p|hr)\s*>", "\n", RegexOptions.IgnoreCase);
        return System.Net.WebUtility.HtmlDecode(Regex.Replace(lines, "<[^>]*>", ""))
            .Replace("\r", "").Trim();
    }

    public async Task<Player> Find(string riotId, Player current, CancellationToken ct)
    {
        if (current.Name.Equals(riotId, StringComparison.OrdinalIgnoreCase)) return current;
        var split = riotId.LastIndexOf('#');
        if (split <= 0 || split >= riotId.Length - 1)
            throw new LeagueException(0, "请输入完整游戏名和标签，例如 玩家名#1234。");
        var name = Escape(riotId[..split].Trim());
        var tag = Escape(riotId[(split + 1)..].Trim());
        if (name.Length == 0 || tag.Length == 0) throw new LeagueException(0, "请输入完整游戏名和标签。");
        var alias = await Optional($"/lol-summoner/v1/alias/lookup?gameName={name}&tagLine={tag}", ct, 400, 404, 405);
        var player = Player.Read(alias);
        if (player.Puuid.Length == 0 && player.Id > 0)
            player = Player.Read(await Optional($"/lol-summoner/v1/summoners/{player.Id}", ct, 404));
        if (player.Puuid.Length == 0)
            player = Player.Read(await Optional($"/lol-summoner/v1/summoners?name={Escape(riotId)}", ct, 400, 404));
        return player.Puuid.Length > 0 ? player with { Name = JsonFields.First(player.Name, riotId) }
            : throw new LeagueException(404, "当前大区找不到该玩家，请核对游戏名和标签。");
    }

    public async Task<HistoryPage> History(Player player, Player current, int offset, bool preferShard, CancellationToken ct)
    {
        HistoryPage? shardPage = null;
        if (preferShard)
        {
            try
            {
                shardPage = await ShardHistoryPage(player, offset, ct);
                if (offset > 0 || shardPage.Matches.Any(match => match.For(player) is not null))
                    return shardPage;
            }
            catch (LeagueException) when (offset == 0) { }
        }
        try { return await ClientHistory(player, current, offset, ct); }
        catch (LeagueException) when (shardPage is not null) { return shardPage; }
    }

    private async Task<HistoryPage> ClientHistory(Player player, Player current, int offset, CancellationToken ct)
    {
        var query = $"?begIndex={offset}&endIndex={offset + PageSize - 1}";
        var route = $"/lol-match-history/v1/products/lol/{Escape(player.Puuid)}/matches{query}";
        JsonElement history;
        try { history = await Get(route, ct); }
        catch (LeagueException ex) when (player.Puuid == current.Puuid && ex.Status is not 401)
        {
            history = await Get($"/lol-match-history/v1/products/lol/current-summoner/matches{query}", ct);
        }
        var games = history.Field("games");
        if (games.ValueKind != JsonValueKind.Array) games = games.Field("games");
        if (games.ValueKind != JsonValueKind.Array) throw new LeagueException(0, "客户端战绩格式无法识别。");
        return ParsePage(games, false);
    }

    internal static HistoryPage ParsePage(JsonElement entries, bool shard)
    {
        var rows = entries.Items();
        var matches = rows.Select(Match.Read).Where(game => game.Id > 0).DistinctBy(game => game.Id).ToArray();
        return new(matches, rows.Length, rows.Length >= PageSize, shard);
    }

    internal async Task<JsonElement> ShardHistory(Player player, int offset, CancellationToken ct)
        => await ShardRequest(_ => $"/match-history-query/v1/products/lol/player/{Escape(player.Puuid)}/SUMMARY?startIndex={offset}&count={PageSize}", ct);

    internal async Task<HistoryPage> ShardHistoryPage(Player player, int offset, CancellationToken ct)
    {
        var entries = (await ShardHistory(player, offset, ct)).Field("games");
        if (entries.ValueKind != JsonValueKind.Array) throw new LeagueException(0, "大区战绩格式无法识别。");
        return ParsePage(entries, true);
    }

    internal Task<JsonElement> ShardMatchSummary(long matchId, CancellationToken ct)
        => ShardRequest(region => $"/match-history-query/v1/products/lol/{region}_{matchId}/SUMMARY", ct);

    internal async Task<JsonElement> Timeline(long matchId, CancellationToken ct)
    {
        var local = await Optional($"/lol-match-history/v1/game-timelines/{matchId}", ct);
        if (local.Field("frames").ValueKind == JsonValueKind.Array || local.Field("info").Field("frames").ValueKind == JsonValueKind.Array)
            return local;
        try { return await ShardRequest(region => $"/match-history-query/v1/products/lol/{region}_{matchId}/DETAILS", ct); }
        catch (LeagueException) { return default; }
    }

    private async Task<JsonElement> ShardRequest(Func<string, string> path, CancellationToken ct)
    {
        var auth = await Get("/entitlements/v1/token", ct);
        var bearer = auth.Text("accessToken");
        var issuerShard = TokenShard(auth);
        var shard = issuerShard ?? Platform;
        if (bearer.Length == 0) throw new LeagueException(0, "客户端暂未提供大区战绩会话。");
        if (!ShardHosts.TryGetValue(shard, out var host) ||
            (ShardHosts.ContainsKey(Platform) && issuerShard is not null && Platform != issuerShard))
            throw new LeagueException(0, "当前大区的历史翻页暂不可用。");
        var version = Regex.IsMatch(Version, @"^\d+(\.\d+)+$") ? Version : "16.19.8217343";
        var uri = new Uri($"https://{host}.lol.qq.com:21019{path(shard)}");
        return await transport.Shard(uri, bearer, version, ct);
    }

    internal static string? IssuerShard(string issuer)
    {
        if (!Uri.TryCreate(issuer, UriKind.Absolute, out var uri)) return null;
        var match = Regex.Match(uri.Host, @"^([a-z0-9]+)-bcs-internal\.lol\.qq\.com$", RegexOptions.IgnoreCase);
        return match.Success ? match.Groups[1].Value.ToUpperInvariant() : null;
    }

    private static string? TokenShard(JsonElement auth)
    {
        if (IssuerShard(auth.Text("issuer")) is { } shard) return shard;
        var parts = auth.Text("accessToken").Split('.');
        if (parts.Length != 3 || parts[1].Length > 16384) return null;
        try
        {
            var body = parts[1].Replace('-', '+').Replace('_', '/');
            body = body.PadRight((body.Length + 3) / 4 * 4, '=');
            using var token = JsonDocument.Parse(Convert.FromBase64String(body));
            return IssuerShard(token.RootElement.Text("iss"));
        }
        catch (Exception ex) when (ex is FormatException or JsonException) { return null; }
    }

    public async Task<Match> Details(Match match, CancellationToken ct, Player? viewed = null)
    {
        if (match.HasCompleteLineup) return match;

        bool IsUsable(Match candidate) => candidate.Id == match.Id && candidate.Players.Length > 0 &&
            (viewed is null || candidate.For(viewed) is not null);

        var local = Match.Read(await Optional($"/lol-match-history/v1/games/{match.Id}", ct));
        if (local.HasCompleteLineup && IsUsable(local)) return local;
        var best = IsUsable(local) && local.Players.Length > match.Players.Length ? local : match;
        try
        {
            var shard = Match.Read(await ShardMatchSummary(match.Id, ct));
            if (IsUsable(shard) && (shard.HasCompleteLineup || shard.Players.Length > best.Players.Length))
                return shard;
        }
        catch (LeagueException) { }
        return best;
    }

    public async Task<Selection> State(CancellationToken ct)
    {
        var phaseValue = await Get("/lol-gameflow/v1/gameflow-phase", ct);
        if (phaseValue.ValueKind != JsonValueKind.String) throw new LeagueException(0, "客户端暂未返回对局阶段。");
        var phase = phaseValue.GetString() ?? "None";
        if (phase != "ChampSelect") return new(phase, "", 0, "", 0, Version);
        var pick = await Get("/lol-champ-select/v1/session", ct);
        var localCell = pick.OptionalNumber("localPlayerCellId");
        var self = pick.Field("myTeam").Items().FirstOrDefault(p => localCell is >= 0 && p.OptionalNumber("cellId") == localCell);
        var flow = await Optional("/lol-gameflow/v1/session", ct);
        var map = (int)flow.Field("map").Number("id");
        if (map == 0) map = (int)flow.Field("gameData").Field("queue").Number("mapId");
        if (map == 0) map = (int)flow.Field("map").Number("mapId");
        if (map == 0) map = (int)flow.Field("gameData").Number("mapId");
        if (map == 0) map = (int)pick.Number("mapId");
        var champion = (int)self.Number("championId");
        if (champion == 0) champion = (int)self.Number("championPickIntent");
        var game = flow.Field("gameData");
        var custom = pick.Flag("isCustomGame") || game.Flag("isCustomGame") || game.Field("queue").Text("type") == "CUSTOM";
        var queue = pick.Number("queueId");
        if (queue <= 0) queue = game.Field("queue").Number("id");
        return new(phase, JsonFields.First(pick.Text("gameId"), game.Text("gameId")),
            champion, self.Text("assignedPosition").ToLowerInvariant(), map, Version, custom, (int)queue);
    }

    public async Task<bool> AcceptReadyCheck(CancellationToken ct)
    {
        const string path = "/lol-matchmaking/v1/ready-check";
        var check = await Get(path, ct);
        if (check.Text("state") != "InProgress" || check.Text("playerResponse") != "None") return false;
        await transport.Call(HttpMethod.Post, $"{path}/accept", ct);
        return true;
    }

    public async Task<string> AutoPick(Selection selection, int champion, bool lockIn, CancellationToken ct)
    {
        if (!selection.IsPicking || champion <= 0) return "";
        const string path = "/lol-champ-select/v1/session";
        var session = await Get(path, ct);
        if (session.Text("gameId") != selection.Session ||
            session.OptionalNumber("localPlayerCellId") is not { } ownCell) return "";
        var own = session.Field("myTeam").Items().FirstOrDefault(player => player.OptionalNumber("cellId") == ownCell);
        if (own.ValueKind != JsonValueKind.Object) return "";
        var actions = session.Field("actions").Items().SelectMany(group => group.Items()).ToArray();
        var action = actions.FirstOrDefault(pick => pick.Text("type") == "pick" &&
            pick.OptionalNumber("actorCellId") == ownCell && !pick.Flag("completed"));
        if (action.Number("id") <= 0) return "";
        var selected = action.Number("championId");
        var intent = own.Number("championPickIntent");
        var current = own.Number("championId");
        if ((selected > 0 && selected != champion) || (intent > 0 && intent != champion) ||
            (current > 0 && current != champion)) return "";
        var inTurn = action.Flag("isInProgress") && session.Field("timer").Text("phase") != "PLANNING";
        if (!inTurn && (selected == champion || intent == champion)) return "";
        if (inTurn && selected == champion && !lockIn) return "";
        if (await Get("/lol-champ-select/v1/pickable-champion-ids", ct) is not { ValueKind: JsonValueKind.Array } pickable ||
            !pickable.Items().Any(id => id.TryGetInt32(out var number) && number == champion))
            return $"{ChampionName(champion)} 当前不可选";
        var completed = inTurn && lockIn;
        object update = inTurn ? new { type = "pick", championId = champion, completed } : new { championId = champion };
        await transport.Call(HttpMethod.Patch, $"{path}/actions/{action.Number("id")}", ct, update);
        return $"{(completed ? "已锁定" : "已预选")} {ChampionName(champion)}";
    }

    public async Task<Rune[]> Recommendations(Selection selection, CancellationToken ct)
    {
        if (selection.Champion == 0 || selection.Map == 0) return [];
        var position = selection.Position;
        if (position.Length == 0 || position is "fill" or "unselected")
        {
            var positions = await Optional("/lol-perks/v1/recommended-champion-positions", ct);
            var recommended = positions.Field(selection.Champion.ToString()).Field("recommendedPositions").Items().FirstOrDefault();
            position = recommended.ValueKind == JsonValueKind.String ? recommended.GetString() ?? "" : "";
        }
        var data = await Optional($"{Perks}/recommended-pages/champion/{selection.Champion}/position/{Escape(position.Length > 0 ? position.ToUpperInvariant() : "MIDDLE")}/map/{selection.Map}", ct);
        var pages = data.ValueKind == JsonValueKind.Array ? data.Items() : data.Field("pages").Items();
        return pages.Select(row =>
        {
            var page = row.Field("page");
            if (page.ValueKind != JsonValueKind.Object) page = row;
            var ids = page.Field("selectedPerkIds").Items()
                .Where(id => id.ValueKind == JsonValueKind.Number && id.TryGetInt32(out _)).Select(id => id.GetInt32()).ToArray();
            if (ids.Length == 0) ids = page.Field("perks").Items().Select(perk => (int)perk.Number("id")).ToArray();
            return new Rune(JsonFields.First(page.Text("name"), page.Field("keystone").Text("name"), PerkName(ids.FirstOrDefault())),
                (int)Math.Max(page.Number("primaryStyleId"), page.Number("primaryPerkStyleId")),
                (int)Math.Max(page.Number("subStyleId"), page.Number("secondaryPerkStyleId")), ids,
                "国服客户端推荐");
        }).Where(r => r.IsValid).ToArray();
    }

    public async Task Apply(Selection expected, Rune rune, CancellationToken ct)
    {
        if (!rune.IsValid) throw new LeagueException(0, "符文配置不完整。");
        await EnsureSelection(expected, ct);
        var pages = (await Get($"{Perks}/pages", ct)).Items();
        var owned = pages.FirstOrDefault(p => p.Text("name") == PageName && p.Flag("isEditable"));
        var id = owned.Number("id");
        var payload = new { id, name = PageName, rune.PrimaryStyleId, rune.SubStyleId, rune.SelectedPerkIds, current = false };
        await EnsureSelection(expected, ct);
        if (id > 0)
        {
            await transport.Call(HttpMethod.Put, $"{Perks}/pages/{id}", ct, payload);
        }
        else
        {
            try
            {
                var inventory = await Optional($"{Perks}/inventory", ct);
                var available = inventory.Number("ownedPageCount");
                if (available > 0 && pages.Count(p => p.Flag("isEditable")) >= available)
                    throw new LeagueException(0, "符文页已满，请在客户端腾出一页后重试。");
                var created = await transport.Call(HttpMethod.Post, $"{Perks}/pages", ct,
                    new { name = PageName, rune.PrimaryStyleId, rune.SubStyleId, rune.SelectedPerkIds, current = false });
                id = created.Number("id");
                if (id == 0)
                    id = (await Get($"{Perks}/pages", ct)).Items().FirstOrDefault(p => p.Text("name") == PageName && p.Flag("isEditable")).Number("id");
            }
            catch (LeagueException ex) when (ex.Status is 400 or 409)
            {
                throw new LeagueException(ex.Status, "客户端未能创建符文页。请确认有空余页且配置有效。");
            }
        }
        if (id == 0) throw new LeagueException(0, "无法确认助手符文页已保存。");
        await EnsureSelection(expected, ct);
        await transport.Call(HttpMethod.Put, $"{Perks}/currentpage", ct, id);
        var active = await Get($"{Perks}/currentpage", ct);
        if (active.Number("id") != id || active.Number("primaryStyleId") != rune.PrimaryStyleId ||
            active.Number("subStyleId") != rune.SubStyleId ||
            !active.Field("selectedPerkIds").Items().Select(value => value.ToString()).SequenceEqual(rune.SelectedPerkIds.Select(value => value.ToString())))
            throw new LeagueException(0, "客户端当前符文与所选配置不一致，请检查。");
    }

    private async Task EnsureSelection(Selection expected, CancellationToken ct)
    {
        var actual = await State(ct);
        if (!actual.IsPicking || actual.Champion == 0 || actual != expected)
            throw new LeagueException(0, "选人阶段或所选英雄已变化，请重新选择符文。");
    }

    public async Task<Seat[]> PickingRoster(Selection selection, Player current, CancellationToken ct)
    {
        if (!selection.IsPicking) return [];
        var pick = await Get("/lol-champ-select/v1/session", ct);
        if (selection.Session.Length > 0 && pick.Number("gameId") > 0 && pick.Text("gameId") != selection.Session)
            return [];
        var localCell = pick.OptionalNumber("localPlayerCellId");
        return await Task.WhenAll(pick.Field("myTeam").Items().Select(async member =>
        {
            var champion = (int)member.Number("championId");
            if (champion == 0) champion = (int)member.Number("championPickIntent");
            var position = member.Text("assignedPosition");
            var cell = member.OptionalNumber("cellId");
            if (localCell is >= 0 && cell == localCell) return new Seat(current, champion, 1, Position: position);
            var bot = member.Flag("isBot") || member.Text("playerType").Equals("BOT", StringComparison.OrdinalIgnoreCase);
            if (bot) return new Seat(new("", 0, ""), champion, 1, IsBot: true, Position: position);
            var (player, hidden) = await PickIdentity(member, selection, cell, ct);
            // A missing or false isHumanoid flag is not enough to discard a public player ID.
            bot = selection.IsCustom && !hidden && !player.HasLookupIdentity &&
                member.Field("isHumanoid").ValueKind == JsonValueKind.False;
            return new Seat(player, champion, 1, IsBot: bot, Position: position, IdentityHidden: hidden);
        }));
    }

    private async Task<(Player Player, bool Hidden)> PickIdentity(JsonElement member, Selection selection, long? cell, CancellationToken ct)
    {
        if (IsHidden(member)) return (new("", 0, ""), true);
        var player = ReadPickPlayer(member);
        var visible = Unhidden(member);
        var knownQueue = selection.IsCustom || selection.Queue is > 0 and not 420;
        if (!CanQueryPickPlayer(player, visible, knownQueue) && cell is >= 0)
        {
            var details = await Optional($"/lol-champ-select/v1/summoners/{cell}", ct);
            if (IsHidden(details)) return (new("", 0, ""), true);
            if (!details.Flag("isPlaceholder"))
            {
                var slot = ReadPickPlayer(details);
                player = player with
                {
                    Puuid = JsonFields.First(slot.Puuid, player.Puuid),
                    Id = slot.Id > 0 ? slot.Id : player.Id,
                    Name = JsonFields.First(slot.Name, player.Name),
                    IconId = slot.IconId >= 0 ? slot.IconId : player.IconId,
                    Level = slot.Level > 0 ? slot.Level : player.Level
                };
                visible |= Unhidden(details);
            }
        }
        return (CanQueryPickPlayer(player, visible, knownQueue) ? player : new("", 0, ""), false);
    }

    private static Player ReadPickPlayer(JsonElement row)
    {
        var player = row.Flag("isPlaceholder") ? new Player("", 0, "") : Player.Read(row);
        return Guid.TryParse(player.Puuid, out var id) && id == Guid.Empty ? player with { Puuid = "" } : player;
    }

    private static bool CanQueryPickPlayer(Player player, bool visible, bool knownQueue) =>
        player.Puuid.Length > 0 || player.HasRiotId || player.Id > 0 ||
        player.Name.Length > 0 && (visible || knownQueue);

    public async Task<(string Game, Seat[] Seats)> Roster(Selection selection, CancellationToken ct)
    {
        if (!selection.CanShowRoster) return ("", []);
        var data = await Optional("/lol-gameflow/v1/session", ct);
        var game = data.Field("gameData");
        var seats = Seats(game);
        if (seats.Length < 10 || seats.Any(seat => !seat.IsBot && seat.Player.Name.Length == 0))
            seats = ReconcileRoster(seats, LiveSeats(await transport.LivePlayers(ct)));
        return (game.Text("gameId"), seats);
    }

    internal static Seat[] ReconcileRoster(Seat[] session, Seat[] live)
    {
        if (live.Length == 0) return session;
        return new[] { 1, 2 }.SelectMany(side =>
        {
            var available = session.Where(seat => seat.Side == side).ToList();
            var joined = new List<Seat>();
            foreach (var seat in live.Where(seat => seat.Side == side))
            {
                var known = available.FindIndex(candidate => candidate.Player.Name.Length > 0 &&
                    candidate.Player.Name.Equals(seat.Player.Name, StringComparison.OrdinalIgnoreCase));
                if (known >= 0)
                {
                    joined.Add(available[known] with { ChampionLabel = seat.ChampionLabel });
                    available.RemoveAt(known);
                    continue;
                }
                joined.Add(seat);
                var placeholder = available.FindIndex(candidate => candidate.Player.Name.Length == 0 && candidate.IsBot == seat.IsBot);
                if (placeholder >= 0) available.RemoveAt(placeholder);
            }
            return joined.Concat(available);
        }).ToArray();
    }

    private static bool Unhidden(JsonElement row) =>
        row.Text("nameVisibilityType").ToUpperInvariant() is "UNHIDDEN" or "VISIBLE";

    internal static bool IsHidden(JsonElement row) =>
        row.Flag("isAnonymous") || row.Flag("isHidden") || (row.Text("nameVisibilityType").Length > 0 && !Unhidden(row));

    private static Player PublicPlayer(JsonElement row)
    {
        if (IsHidden(row)) return new("", 0, "");
        var player = Player.Read(row);
        return string.IsNullOrWhiteSpace(player.Name) ? new("", 0, "") : player;
    }

    internal static Seat[] Seats(JsonElement game)
    {
        return new[] { "teamOne", "teamTwo" }.SelectMany((key, side) => game.Field(key).Items().Select(row =>
        {
            var bot = row.Flag("isBot");
            return new Seat(bot ? new("", 0, "") : PublicPlayer(row), (int)row.Number("championId"), side + 1,
                IsBot: bot, Position: row.Text("assignedPosition"));
        })).ToArray();
    }

    internal static Seat[] LiveSeats(JsonElement data)
    {
        return data.Items().Select(row =>
        {
            var side = row.Text("team") switch { "ORDER" => 1, "CHAOS" => 2, _ => 0 };
            var bot = row.Flag("isBot");
            var player = bot ? new("", 0, "") : PublicPlayer(row);
            return new Seat(player, 0, side, row.Text("championName"), bot, row.Text("position"));
        }).Where(seat => seat.Side != 0).ToArray();
    }

    public async Task<PlayerRecord> Profile(Player player, Player current, CancellationToken ct, Action<RankSummary>? rankReady = null)
    {
        if (player.Puuid.Length == 0) player = await ResolvePublicPlayer(player, current, ct);
        if (player.Puuid.Length == 0) return new(null, 0, 0, "资料暂不可用");
        var rankTask = Optional($"/lol-ranked/v1/ranked-stats/{Escape(player.Puuid)}", ct);
        var historyTask = RecentProfileHistory(player, current, ct);
        var rank = ParseRank((await rankTask).Field("queueMap").Field("RANKED_SOLO_5x5"));
        rankReady?.Invoke(rank);
        var page = await historyTask;
        if (page is null) return new(rank, 0, 0, "战绩暂不可用");
        var records = page.Matches.OrderByDescending(match => match.Started)
            .SelectMany(match => match.For(player) is { } entry ? new[] { new PlayerMatch(match, entry) } : []).ToArray();
        var matches = records.Where(record => !record.Match.EarlyEnd && record.Participant.OutcomeKnown)
            .Select(record => record.Participant).ToArray();
        var wins = matches.Count(p => p.Won);
        return new(rank, wins, matches.Length - wins, "", HistoryInsights.RosterTags(records), records.Take(10).ToArray());
    }

    private async Task<HistoryPage?> RecentProfileHistory(Player player, Player current, CancellationToken ct)
    {
        try { return await History(player, current, 0, true, ct); }
        catch (LeagueException) { return null; }
    }

    public async Task<MasteryEntry[]?> Mastery(Player viewed, Player current, CancellationToken ct)
    {
        if (viewed.Puuid.Length == 0) return null;
        var path = viewed.Puuid == current.Puuid ? "local-player" : Escape(viewed.Puuid);
        var data = await Optional($"/lol-champion-mastery/v1/{path}/champion-mastery", ct);
        if (data.ValueKind != JsonValueKind.Array) return null;
        return data.Items().Select(row => new MasteryEntry((int)row.Number("championId"),
                row.Number("championLevel"), row.Number("championPoints")))
            .Where(row => row.Champion > 0 && row.Points > 0).OrderByDescending(row => row.Points)
            .DistinctBy(row => row.Champion).ToArray();
    }

    public async Task<CollectionCount[]> Collection(Player current, CancellationToken ct)
    {
        var skins = LoadSkins(current, ct);
        var champions = Optional("/lol-champions/v1/owned-champions-minimal", ct);
        var wards = Inventory("WARD_SKIN", ct);
        var icons = Inventory("SUMMONER_ICON", ct);
        var emotes = Inventory("EMOTE", ct);
        var cosmetics = await skins;
        return [
            new("英雄", OwnedCount(await champions)),
            new("皮肤", cosmetics.Skins),
            new("炫彩皮肤", cosmetics.Chromas),
            new("守卫皮肤", OwnedCount(await wards)),
            new("召唤师图标", OwnedCount(await icons)),
            new("表情", OwnedCount(await emotes))
        ];
    }

    private Task<JsonElement> Inventory(string type, CancellationToken ct) =>
        Optional($"/lol-inventory/v2/inventory/{type}", ct);

    private async Task<(int? Skins, int? Chromas)> LoadSkins(Player current, CancellationToken ct)
    {
        var inventory = await Inventory("CHAMPION_SKIN", ct);
        var fromInventory = inventory.ValueKind == JsonValueKind.Array;
        if (!fromInventory && current.Id > 0)
            inventory = await Optional($"/lol-champions/v1/inventories/{current.Id}/skins-minimal", ct);
        if (inventory.ValueKind != JsonValueKind.Array) return (null, null);
        var owned = inventory.Items().Where(row => IsOwned(row, fromInventory)).ToArray();
        var ids = owned.Select(InventoryId).Where(id => id > 0).ToHashSet();
        var catalog = await Optional("/lol-game-data/assets/v1/skins.json", ct);
        var entries = catalog.ValueKind == JsonValueKind.Object
            ? catalog.EnumerateObject().Select(property => property.Value).ToArray() : catalog.Items();
        var chromaIds = entries.SelectMany(skin => skin.Field("chromas").Items()).Select(InventoryId).Where(id => id > 0).ToHashSet();
        foreach (var chroma in owned.SelectMany(row => row.Field("chromas").Items()).Where(row => IsOwned(row, false)))
        {
            var id = InventoryId(chroma);
            if (id <= 0) continue;
            chromaIds.Add(id);
            ids.Add(id);
        }
        foreach (var row in owned.Where(row => row.Flag("isChroma")))
            if (InventoryId(row) is > 0 and var id) chromaIds.Add(id);
        var skins = ids.Count(id => id % 1000 != 0 && !chromaIds.Contains(id));
        return (skins, chromaIds.Count > 0 ? ids.Count(chromaIds.Contains) : null);
    }

    private static int? OwnedCount(JsonElement data) => data.ValueKind != JsonValueKind.Array ? null :
        data.Items().Where(row => IsOwned(row, true)).Select(InventoryId).Where(id => id > 0).Distinct().Count();

    private static long InventoryId(JsonElement row) => row.OptionalNumber("itemId") ?? row.Number("id");

    private static bool IsOwned(JsonElement row, bool ownedEndpoint)
    {
        var ownership = row.Field("ownership");
        if (row.Flag("rental") || ownership.Field("rental").Flag("rented") || ownership.Flag("rental") ||
            row.OptionalNumber("ownedQuantity") is <= 0) return false;
        var owned = ownership.Field("owned");
        if (owned.ValueKind == JsonValueKind.Undefined) owned = row.Field("owned");
        return owned.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => ownership.ValueKind is JsonValueKind.True or JsonValueKind.False ? ownership.GetBoolean() : ownedEndpoint
        };
    }

    public async Task<(RankSummary Solo, RankSummary Flex)> Ranks(Player player, CancellationToken ct, bool isOwn = false)
    {
        var ranks = (await Optional($"/lol-ranked/v1/ranked-stats/{Escape(player.Puuid)}", ct)).Field("queueMap");
        return (ParseRank(ranks.Field("RANKED_SOLO_5x5"), isOwn), ParseRank(ranks.Field("RANKED_FLEX_SR"), isOwn));
    }

    internal static RankSummary ParseRank(JsonElement rank, bool isOwn = false)
    {
        var tier = rank.Text("tier");
        var tierName = tier switch
        {
            "IRON" => "黑铁", "BRONZE" => "青铜", "SILVER" => "白银", "GOLD" => "黄金",
            "PLATINUM" => "铂金", "EMERALD" => "翡翠", "DIAMOND" => "钻石", "MASTER" => "大师",
            "GRANDMASTER" => "宗师", "CHALLENGER" => "王者", _ => tier
        };
        var division = rank.Text("division");
        var label = tier.Length > 0 ? $"{tierName}{(division is "" or "NA" ? "" : $" {division}")}"
            : rank.ValueKind == JsonValueKind.Object ? "未定级" : "段位暂不可用";
        var wins = rank.OptionalNumber("wins");
        var losses = rank.OptionalNumber("losses");
        var totalGames = rank.OptionalNumber("gamesPlayed") ?? rank.OptionalNumber("totalGames");
        if (totalGames is >= 0 && wins is >= 0 && totalGames >= wins &&
            (losses is null or 0 || totalGames == wins + losses))
            losses = totalGames - wins;
        // Public rank responses can pad an unavailable loss count with zero.
        var verified = wins is >= 0 && losses is >= 0 && wins + losses > 0 &&
            (totalGames is null || totalGames == wins + losses) &&
            (isOwn || losses > 0 || totalGames == wins + losses);
        var record = verified ? $"{wins} 胜 {losses} 负 · {(double)wins!.Value / (wins.Value + losses!.Value):P0}" : "";
        var points = tier.Length > 0 ? rank.OptionalNumber("leaguePoints") : null;
        if (points is { } lp) record = record.Length > 0 ? $"{record} · {lp} LP" : $"{lp} LP";
        return new(label, record, points, tier, verified);
    }

    internal async Task<Player> ResolvePublicPlayer(Player player, Player current, CancellationToken ct)
    {
        if (player.Puuid.Length > 0) return player;
        if (player.Id > 0)
        {
            var resolved = Player.Read(await Optional($"/lol-summoner/v1/summoners/{player.Id}", ct, 400, 403, 404, 405));
            if (resolved.Puuid.Length > 0) return resolved with { Name = JsonFields.First(resolved.Name, player.Name) };
        }
        if (player.HasRiotId || player.Name.Equals(current.Name, StringComparison.OrdinalIgnoreCase))
            return await Find(player.Name, current, ct);
        if (player.Name.Length == 0) throw new LeagueException(404, "客户端暂未返回该玩家的查询标识。");
        var legacy = Player.Read(await Optional($"/lol-summoner/v1/summoners?name={Escape(player.Name)}", ct, 400, 404));
        return legacy.Puuid.Length > 0 ? legacy with { Name = JsonFields.First(legacy.Name, player.Name) }
            : throw new LeagueException(404, "当前大区暂未返回该玩家的查询标识。");
    }

    internal async Task<Player> CompletePublicProfile(Player player, CancellationToken ct)
    {
        if (player.Puuid.Length == 0 || (player.HasRiotId && player.IconId >= 0 && player.Level > 0))
            return player;
        var profile = PublicPlayer(await Optional($"/lol-summoner/v2/summoners/puuid/{Escape(player.Puuid)}", ct));
        if (profile.Puuid != player.Puuid && player.Id > 0)
            profile = PublicPlayer(await Optional($"/lol-summoner/v1/summoners/{player.Id}", ct));
        if (profile.Puuid != player.Puuid) return player;
        return player with
        {
            Id = player.Id > 0 ? player.Id : profile.Id,
            Name = player.HasRiotId ? player.Name : JsonFields.First(profile.Name, player.Name),
            IconId = profile.IconId >= 0 ? profile.IconId : player.IconId,
            Level = profile.Level > 0 ? profile.Level : player.Level
        };
    }

    private Task<JsonElement> Get(string path, CancellationToken ct) => transport.Call(HttpMethod.Get, path, ct);
    private async Task<JsonElement> Optional(string path, CancellationToken ct, params int[] statuses)
    {
        try { return await Get(path, ct); }
        catch (LeagueException ex) when (statuses.Length == 0 || statuses.Contains(ex.Status)) { return default; }
    }

    private static string Escape(string text) => Uri.EscapeDataString(text);
}
