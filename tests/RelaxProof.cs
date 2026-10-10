using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using osu.Framework.Bindables;
using osu.Framework.IO.Network;
using osu.Framework.Localisation;
using osu.Game;
using osu.Game.Beatmaps;
using osu.Game.Online;
using osu.Game.Online.API;
using osu.Game.Online.API.Requests;
using osu.Game.Online.API.Requests.Responses;
using osu.Game.Rulesets;
using osu.Game.Rulesets.Mods;
using osu.Game.Screens.Play.Leaderboards;
using osu.Game.Users;

public static class RelaxProof
{
    private const BindingFlags instance = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
    private static int checks;
    private static void require(bool condition, string name)
    {
        if (!condition) throw new InvalidOperationException("FAIL RX " + name);
        checks++;
        Console.WriteLine("PASS RX " + name);
    }

    internal static int Run(Assembly bridge, string? endpoint = null, bool buildersOnly = false)
    {
        var api = createApi();
        if (endpoint is not null) api.Endpoint = endpoint;
        var fake = (IAPIProvider)api;
        var game = (OsuGameBase)RuntimeHelpers.GetUninitializedObject(typeof(OsuGameBase));
        var mods = new Bindable<IReadOnlyList<Mod>>(Array.Empty<Mod>());
        typeof(OsuGameBase).GetField("SelectedMods", instance)!.SetValue(game, mods);
        var context = bridge.GetType("Illyaverse.Transfiguration.RelaxContext", true)!;
        context.GetMethod("capture", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [fake, game]);
        var osu = new RulesetInfo("osu", "fixture osu", string.Empty, 0);
        var mania = new RulesetInfo("mania", "fixture mania", string.Empty, 3);
        var taiko = new RulesetInfo("taiko", "fixture taiko", string.Empty, 1);
        var fruits = new RulesetInfo("fruits", "fixture catch", string.Empty, 2);
        var rx = new FixtureRelax();
        var nativeUri = typeof(APIRequest).GetProperty("Uri", instance)!;
        string uri(APIRequest request)
        {
            request.AttachAPI(fake);
            return (string)nativeUri.GetValue(request)!;
        }
        (string Key, string Value)[] query(APIRequest request, string expectedVariant)
        {
            request.AttachAPI(fake);
            using var web = (WebRequest)request.GetType().GetMethod("CreateWebRequest", instance)!.Invoke(request, null)!;
            var parameters = ((IEnumerable<(string Key, string Value)>)typeof(WebRequest)
                .GetField("queryParameters", instance)!.GetValue(web)!).ToArray();
            require(!web.Url.Contains('?'), "parameterized native read keeps its base URL query-free");
            require(parameters.Count(p => p.Key == "variant") == 1
                && parameters.Single(p => p.Key == "variant").Value == expectedVariant,
                "native builder carries exactly one immutable variant parameter");
            // The pinned framework joins these tuples with '?' at send time.
            // Reconstructing that address is inert: no Perform/Queue is called.
            var wire = web.Url + "?" + string.Join("&", parameters.Select(p => p.Key + "=" + Uri.EscapeDataString(p.Value)));
            require(wire.Count(c => c == '?') == 1 && !wire.Contains("variant=" + expectedVariant + "?", StringComparison.Ordinal),
                "native parameter tuples produce one query delimiter");
            return parameters;
        }
        var queuedVanillaRanking = new GetUserRankingsRequest(osu);
        var queuedVanillaBoard = new GetScoresRequest(DispatchProxy.Create<IBeatmapInfo, FakeBeatmap>(), osu);
        var vanilla = new GetUserRequest(42, osu);
        require(uri(vanilla).EndsWith("&variant=vanilla", StringComparison.Ordinal), "profile preserves its existing key query and defaults vanilla");
        mods.Value = [rx];
        require(uri(vanilla).EndsWith("&variant=vanilla", StringComparison.Ordinal), "queued vanilla profile keeps its immutable context after RX toggle");
        require(uri(new GetUserRequest(42, osu)).EndsWith("&variant=relax", StringComparison.Ordinal), "new osu profile uses relax partition");
        require(uri(new GetUserRequest(42, taiko)).EndsWith("&variant=relax", StringComparison.Ordinal), "new taiko profile uses relax partition");
        require(uri(new GetUserRequest(42, fruits)).EndsWith("&variant=relax", StringComparison.Ordinal), "new catch profile uses relax partition");
        require(uri(new GetUserRequest(42, mania)).EndsWith("&variant=vanilla", StringComparison.Ordinal), "mania explicitly stays vanilla with RX selected elsewhere");
        require(uri(new GetMeRequest()).EndsWith("?variant=relax", StringComparison.Ordinal), "default native me profile uses base osu relax partition");
        require(uri(new GetMeRequest(mania)).EndsWith("?variant=vanilla", StringComparison.Ordinal), "explicit mania me profile stays vanilla");
        require(query(new GetUserRankingsRequest(osu), "relax").Any(p => p == ("page", "1")), "native PP rankings keep the stock page parameter");
        require(query(new GetUserRankingsRequest(osu, UserRankingsType.Score, 2, CountryCode.JP), "relax")
            .Where(p => p.Key != "variant").SequenceEqual([("page", "2"), ("country", "JP")]), "native score rankings select relax and preserve page/country");
        require(query(new GetUserScoresRequest(42, ScoreType.Best, new PaginationParameters(20, 6), osu), "relax")
            .Where(p => p.Key != "variant").SequenceEqual([("offset", "20"), ("limit", "6"), ("mode", "osu")]), "native best score builder preserves its complete stock pagination and selects relax");
        require(query(new GetUserScoresRequest(42, ScoreType.Recent, new PaginationParameters(50, 51), mania), "vanilla")
            .Where(p => p.Key != "variant").SequenceEqual([("offset", "50"), ("limit", "51"), ("mode", "mania")]), "native mania recent score builder preserves its complete stock pagination and stays vanilla");
        require(query(queuedVanillaRanking, "vanilla").Any(p => p == ("page", "1")), "queued vanilla rankings keep their constructor scope after RX toggle");
        require(query(queuedVanillaBoard, "vanilla").Where(p => p.Key != "variant")
            .SequenceEqual([("type", "global"), ("mode", "osu"), ("limit", "50")]), "queued vanilla board preserves complete stock query parameters");
        require(!uri(new GetUserScoresRequest(42, ScoreType.Pinned, new PaginationParameters(), osu)).Contains("variant=", StringComparison.Ordinal), "unsupported pinned score read is untouched");
        var beatmap = DispatchProxy.Create<IBeatmapInfo, FakeBeatmap>();
        var relaxBoard = new GetScoresRequest(beatmap, osu);
        require(query(relaxBoard, "relax").Where(p => p.Key != "variant")
            .SequenceEqual([("type", "global"), ("mode", "osu"), ("limit", "50")]), "native global map leaderboard preserves complete stock query parameters");
        var filtered = new GetScoresRequest(beatmap, osu, BeatmapLeaderboardScope.Global, [new FixtureNoMod()]);
        var filterMods = (IEnumerable<IMod>)typeof(GetScoresRequest).GetField("mods", instance)!.GetValue(filtered)!;
        require(filterMods.Select(m => m.Acronym).SequenceEqual(["RX"]), "native NM filter is normalized to RX in relax context");
        require(query(filtered, "relax").Where(p => p.Key != "variant")
            .SequenceEqual([("type", "global"), ("mode", "osu"), ("mods[]", "RX"), ("limit", "100")]), "filtered native map read preserves normalized mods and supporter limit");
        var countryBoard = new GetScoresRequest(beatmap, osu, BeatmapLeaderboardScope.Country);
        require(query(countryBoard, "relax").Any(p => p == ("type", "country")), "native country map leaderboard preserves its scope");
        var friendBoard = new GetScoresRequest(beatmap, osu, BeatmapLeaderboardScope.Friend);
        require(query(friendBoard, "relax").Any(p => p == ("type", "friend")), "native friend map leaderboard preserves its scope");
        var maniaBoard = new GetScoresRequest(beatmap, mania, BeatmapLeaderboardScope.Global, [rx]);
        require(query(maniaBoard, "vanilla").Any(p => p == ("mode", "mania"))
            && !((IEnumerable<IMod>)typeof(GetScoresRequest).GetField("mods", instance)!.GetValue(maniaBoard)!).Any(), "mania board query and mod filter reject RX context");
        mods.Value = [];
        var vanillaBoard = new GetScoresRequest(beatmap, osu);
        require(!vanillaBoard.Equals(relaxBoard), "native board cache equality separates vanilla and relax with no explicit mod filter");
        require(query(relaxBoard, "relax").Any(p => p == ("type", "global")), "queued relax map read remains relax after toggling off");
        foreach (var target in new[] { "chat/channels", "oauth/token", "scores/7/download", "beatmaps/7/scores" })
            require(!uri(new UntouchedRequest(target)).Contains("variant=", StringComparison.Ordinal), "unlisted request remains untouched even for " + target);

        if (buildersOnly)
        {
            require(api.Requests.Count == 0 && api.UnexpectedCalls == 0, "detached native builder proof never queues an API request or reads auth");
            Console.WriteLine($"PASS {checks} RX inert native request-builder checks; no Perform/Queue/API worker/GameHost/config/Realm/player auth/network");
            return checks;
        }

        api.DrainScheduled();
        var provider = new LocalUserStatisticsProvider();
        typeof(LocalUserStatisticsProvider).GetProperty("api", instance)!.SetValue(provider, fake);
        provider.RefetchStatistics(osu);
        var first = (GetUserRequest)api.Requests.Last();
        deliver(first, new APIUser { Id = 42, Statistics = new UserStatistics { PP = 12 } });
        api.DrainScheduled();
        require(provider.GetStatisticsFor(osu)?.PP == 12, "vanilla local statistics response populates stock cache");
        provider.RefetchStatistics(osu);
        var delayedVanilla = (GetUserRequest)api.Requests.Last();
        mods.Value = [rx];
        require(provider.GetStatisticsFor(osu) is null, "RX toggle makes stale vanilla cache immediately unavailable");
        deliver(delayedVanilla, new APIUser { Id = 42, Statistics = new UserStatistics { PP = 99 } });
        api.DrainScheduled();
        var currentRelax = (GetUserRequest)api.Requests.Last();
        require(!ReferenceEquals(currentRelax, delayedVanilla) && uri(currentRelax).EndsWith("&variant=relax", StringComparison.Ordinal), "toggle clears/refetches the local statistics partition on the fake update scheduler");
        require(provider.GetStatisticsFor(osu)?.PP is null, "delayed vanilla response cannot restore its old PP into the new relax cache");
        require(provider.GetStatisticsFor(osu)?.User?.Id == 42, "empty new-scope statistics identify the current local user");
        deliver(currentRelax, new APIUser { Id = 42, Statistics = new UserStatistics { PP = 34 } });
        api.DrainScheduled();
        require(provider.GetStatisticsFor(osu)?.PP == 34, "current relax response updates stock statistics");
        provider.RefetchStatistics(osu);
        var firstRelax = (GetUserRequest)api.Requests.Last();
        provider.RefetchStatistics(osu);
        var secondRelax = (GetUserRequest)api.Requests.Last();
        deliver(secondRelax, new APIUser { Id = 42, Statistics = new UserStatistics { PP = 56 } });
        deliver(firstRelax, new APIUser { Id = 42, Statistics = new UserStatistics { PP = 11 } });
        api.DrainScheduled();
        require(provider.GetStatisticsFor(osu)?.PP == 56, "older same-partition request cannot overwrite a newer refetch");
        mods.Value = [];
        api.DrainScheduled();
        var currentVanilla = (GetUserRequest)api.Requests.Last();
        require(uri(currentVanilla).EndsWith("&variant=vanilla", StringComparison.Ordinal), "toggling RX off queues vanilla statistics again");
        deliver(currentVanilla, new APIUser { Id = 42, Statistics = new UserStatistics { PP = 78 } });
        api.User.Value = new APIUser { Id = 43 };
        api.DrainScheduled();
        require(provider.GetStatisticsFor(osu)?.PP is null, "an old-account completion cannot populate a newly selected account");
        require(api.UnexpectedCalls == 0, "fake API only reads endpoints/user and queues or schedules; no auth/network/cache access");
        Console.WriteLine($"PASS {checks} RX native request and generation-cache checks; no Game/APIAccess/GameHost/database/GUI constructed");
        return checks;
    }

    private static void deliver(GetUserRequest request, APIUser response) => typeof(APIRequest<APIUser>)
        .GetMethod("TriggerSuccess", instance, null, [typeof(APIUser)], null)!.Invoke(request, [response]);

    private static FakeApi createApi()
    {
        // The official interface's scheduler member is internal. This permission belongs only
        // to the in-memory test double assembly, never to the shipped bridge.
        var assembly = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName("Illyaverse.Proof.FakeApi"), AssemblyBuilderAccess.Run);
        assembly.SetCustomAttribute(new CustomAttributeBuilder(typeof(IgnoresAccessChecksToAttribute).GetConstructor([typeof(string)])!, ["osu.Game"]));
        var type = assembly.DefineDynamicModule("proof").DefineType("ProofApi", TypeAttributes.Public | TypeAttributes.Sealed, typeof(FakeApi), [typeof(IAPIProvider)]);
        foreach (var contract in typeof(IAPIProvider).GetMethods(instance))
        {
            var parameters = contract.GetParameters().Select(p => p.ParameterType).ToArray();
            var implementation = type.DefineMethod(contract.Name, MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.Final | MethodAttributes.NewSlot | MethodAttributes.HideBySig,
                contract.ReturnType, parameters);
            var il = implementation.GetILGenerator();
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldstr, contract.Name);
            il.Emit(OpCodes.Ldc_I4, parameters.Length);
            il.Emit(OpCodes.Newarr, typeof(object));
            for (var i = 0; i < parameters.Length; i++)
            {
                il.Emit(OpCodes.Dup);
                il.Emit(OpCodes.Ldc_I4, i);
                il.Emit(OpCodes.Ldarg, i + 1);
                if (parameters[i].IsValueType) il.Emit(OpCodes.Box, parameters[i]);
                il.Emit(OpCodes.Stelem_Ref);
            }
            il.Emit(OpCodes.Call, typeof(FakeApi).GetMethod(nameof(FakeApi.Handle))!);
            if (contract.ReturnType == typeof(void)) il.Emit(OpCodes.Pop);
            else if (contract.ReturnType.IsValueType) il.Emit(OpCodes.Unbox_Any, contract.ReturnType);
            else il.Emit(OpCodes.Castclass, contract.ReturnType);
            il.Emit(OpCodes.Ret);
            type.DefineMethodOverride(implementation, contract);
        }
        return (FakeApi)Activator.CreateInstance(type.CreateType()!)!;
    }

    public class FakeApi
    {
        internal string Endpoint = "http://127.0.0.1:18376";
        internal readonly List<APIRequest> Requests = new();
        internal readonly Queue<Action> Scheduled = new();
        internal readonly Bindable<APIUser> User = new(new APIUser { Id = 42 });
        internal int UnexpectedCalls;
        public object? Handle(string name, object?[] args)
        {
            switch (name)
            {
                case "get_Endpoints": return new EndpointConfiguration { APIUrl = Endpoint };
                case "get_LocalUser": return User;
                case "Queue": var request = (APIRequest)args![0]!; request.AttachAPI((IAPIProvider)(object)this); Requests.Add(request); return null;
                case "Schedule": Scheduled.Enqueue((Action)args![0]!); return null;
                default: UnexpectedCalls++; throw new InvalidOperationException("Unexpected fake API operation " + name);
            }
        }
        internal void DrainScheduled() { while (Scheduled.TryDequeue(out var callback)) callback(); }
    }

    public class FakeBeatmap : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => targetMethod!.Name switch
        {
            "get_OnlineID" => 7,
            "Equals" => ReferenceEquals(this, args![0]),
            _ => throw new InvalidOperationException("Unexpected fake beatmap operation " + targetMethod.Name)
        };
    }
    private sealed class FixtureRelax : ModRelax { public override LocalisableString Description => "fixture RX"; }
    private sealed class FixtureNoMod : Mod { public override string Name => "fixture NM"; public override string Acronym => "NM"; public override ModType Type => ModType.System; public override LocalisableString Description => "fixture NM"; }
    private sealed class UntouchedRequest(string target) : APIRequest { protected override string Target => target; }
}
