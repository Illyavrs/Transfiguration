using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using osu.Framework.Bindables;
using osu.Framework.Localisation;
using osu.Game;
using osu.Game.Configuration;
using osu.Game.Online;
using osu.Game.Online.API;
using osu.Game.Online.API.Requests.Responses;
using osu.Game.Overlays;
using osu.Game.Overlays.Notifications;

namespace Illyaverse.Transfiguration;

internal static class SessionWelcome
{
    private const BindingFlags instance = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
    private static readonly ConditionalWeakTable<APIAccess, SessionWelcomeState> sessions = new();
    private static ConstructorInfo apiConstructor = null!;
    private static MethodInfo schedule = null!;

    internal static void VerifyContracts()
    {
        apiConstructor = typeof(APIAccess).GetConstructor([typeof(OsuGameBase), typeof(OsuConfigManager), typeof(EndpointConfiguration), typeof(string)])!;
        schedule = typeof(IAPIProvider).GetMethod("Schedule", instance)!;
        if (apiConstructor?.GetMethodBody() is null || schedule is null || schedule.IsStatic || schedule.ReturnType != typeof(void)
            || !schedule.GetParameters().Select(parameter => parameter.ParameterType).SequenceEqual([typeof(Action)])) reject();
        var mapping = typeof(APIAccess).GetInterfaceMap(typeof(IAPIProvider));
        var schedulerIndex = Array.IndexOf(mapping.InterfaceMethods, schedule);
        if (schedulerIndex < 0 || mapping.TargetMethods[schedulerIndex].DeclaringType != typeof(APIAccess)
            || mapping.TargetMethods[schedulerIndex].GetMethodBody() is null) reject();
        property(typeof(APIAccess), "State", typeof(IBindable<APIState>));
        property(typeof(APIAccess), "LocalUser", typeof(IBindable<APIUser>));
        property(typeof(APIAccess), "PostNotification", typeof(Action<Notification>), writable: true);
        property(typeof(APIUser), "Id", typeof(int));
        property(typeof(SimpleNotification), "Text", typeof(LocalisableString), writable: true);
        if (typeof(SimpleNotification).IsAbstract || !typeof(Notification).IsAssignableFrom(typeof(SimpleNotification))
            || typeof(SimpleNotification).GetConstructor(Type.EmptyTypes)?.GetMethodBody() is null) reject();
        var overlayPost = typeof(INotificationOverlay).GetMethod("Post", [typeof(Notification)]);
        if (overlayPost?.ReturnType != typeof(void) || overlayPost.IsStatic) reject();
        var overlayMapping = typeof(NotificationOverlay).GetInterfaceMap(typeof(INotificationOverlay));
        var overlayIndex = Array.IndexOf(overlayMapping.InterfaceMethods, overlayPost);
        if (overlayIndex < 0 || overlayMapping.TargetMethods[overlayIndex].DeclaringType != typeof(NotificationOverlay)) reject();
    }

    internal static void Install(Harmony harmony, MethodBase alreadyRegisteredConstructor)
    {
        if (!Equals(alreadyRegisteredConstructor, apiConstructor)) reject();
        // This target already has account isolation and RX prefixes. Attach an
        // observation-only postfix without adding another native patch target.
        harmony.Patch(alreadyRegisteredConstructor, postfix: new HarmonyMethod(typeof(SessionWelcome)
            .GetMethod(nameof(ApiConstructorPostfix), BindingFlags.NonPublic | BindingFlags.Static)!) { priority = Priority.Last });
    }

    private static void property(Type declaringType, string name, Type result, bool writable = false)
    {
        var value = declaringType.GetProperty(name, instance | BindingFlags.DeclaredOnly);
        if (value?.PropertyType != result || value.GetMethod is not { IsPublic: true, IsStatic: false }
            || (writable && value.SetMethod is not { IsPublic: true, IsStatic: false })) reject();
    }

    private static bool privateProvider(APIAccess api) => api.GetType() == typeof(APIAccess)
        && api.Endpoints.APIUrl == NetworkPolicy.Api && api.Endpoints.WebsiteUrl == NetworkPolicy.Website
        && api.Endpoints.APIClientID == NetworkPolicy.ClientId && api.Endpoints.APIClientSecret == NetworkPolicy.ClientSecret;

    private static void ApiConstructorPostfix(APIAccess __instance)
    {
        if (!LaunchState.Active || !privateProvider(__instance)) return;
        var state = sessions.GetValue(__instance, api => new SessionWelcomeState(
            () => LaunchState.Active && privateProvider(api) && api.State.Value == APIState.Online && api.LocalUser.Value is { Id: > 1 },
            () => api.PostNotification is not null,
            callback => schedule.Invoke(api, [callback]),
            callback => _ = defer(api, callback),
            // Stock OsuGame connects this delegate to its INotificationOverlay.
            // Do not invent an achievement, a chat message or a server event.
            () => api.PostNotification?.Invoke(new SimpleNotification { Text = "Welcome to Illyaverse." })));
        __instance.State.BindValueChanged(_ =>
        {
            try { schedule.Invoke(__instance, [new Action(state.Observe)]); }
            catch { /* An optional welcome must not interrupt authentication. */ }
        }, true);
    }

    private static async Task defer(APIAccess api, Action callback)
    {
        try
        {
            // Notification wiring can finish after a remembered login becomes
            // online. Requeue on the API update thread without a busy loop.
            await Task.Delay(250).ConfigureAwait(false);
            schedule.Invoke(api, [callback]);
        }
        catch { /* Disposed client schedulers need no UI fallback. */ }
    }

    internal static void ExplicitLogout(IAPIProvider provider)
    {
        if (provider is APIAccess api && sessions.TryGetValue(api, out var state)) state.ExplicitLogout();
    }

    private static void reject() => throw SafeStartupDiagnostic.Rejection("welcome_contract_incompatible");
}
