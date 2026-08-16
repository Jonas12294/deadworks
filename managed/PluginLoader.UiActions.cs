using System.Reflection;
using DeadworksManaged.Api;

namespace DeadworksManaged;

internal static partial class PluginLoader
{
    // --- [UiActionHandler] discovery ---

    // Registered per plugin path so unload cancels every subscription, whether
    // or not the plugin ever created a UiApp to own it.
    private static readonly Dictionary<string, List<IHandle>> _pluginUiActionHandlers = new(StringComparer.OrdinalIgnoreCase);

    // Must be called under _lock.
    private static void RegisterPluginUiActionHandlers(string normalizedPath, List<IDeadworksPlugin> plugins)
    {
        foreach (var plugin in plugins)
        {
            var methods = plugin.GetType().GetMethods(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

            foreach (var method in methods)
                foreach (var attr in method.GetCustomAttributes<UiActionHandlerAttribute>())
                    TryRegisterUiActionHandler(normalizedPath, plugin, method, attr.EventName);
        }
    }

    private static void TryRegisterUiActionHandler(string normalizedPath, IDeadworksPlugin plugin,
                                                   MethodInfo method, string eventName)
    {
        var invoke = BindUiActionHandler(plugin, method);
        if (invoke is null)
            return;

        IHandle handle;
        try
        {
            // The owner is what lets e.Emit/e.UpdateNode find the plugin's app.
            handle = UI.OnAction(eventName, invoke, owner: plugin);
        }
        catch (ArgumentException ex)
        {
            // A bad event name is the plugin author's mistake, not a reason to
            // fail the whole load.
            Console.WriteLine($"[PluginLoader] {plugin.Name}.{method.Name}: cannot register [UiActionHandler] — {ex.Message}");
            return;
        }

        if (!_pluginUiActionHandlers.TryGetValue(normalizedPath, out var handles))
        {
            handles = [];
            _pluginUiActionHandlers[normalizedPath] = handles;
        }
        handles.Add(handle);

        PluginRegistrationTracker.Add(normalizedPath, "ui-action", eventName);
        Console.WriteLine($"[PluginLoader] Registered UI action handler: {plugin.Name}.{method.Name} -> {eventName}");
    }

    /// <summary>
    /// Binds <c>void OnX(UiAction)</c> or <c>HookResult OnX(UiAction)</c> to a
    /// common shape; a void handler always continues. Returns null (having
    /// logged) for any other signature.
    /// </summary>
    private static Func<UiAction, HookResult>? BindUiActionHandler(IDeadworksPlugin plugin, MethodInfo method)
    {
        var parameters = method.GetParameters();
        bool takesAction = parameters.Length == 1 && parameters[0].ParameterType == typeof(UiAction);
        bool returnsResult = method.ReturnType == typeof(HookResult);

        if (takesAction && (returnsResult || method.ReturnType == typeof(void)))
        {
            try
            {
                if (returnsResult)
                    return (Func<UiAction, HookResult>)Delegate.CreateDelegate(
                        typeof(Func<UiAction, HookResult>), plugin, method);

                var call = (Action<UiAction>)Delegate.CreateDelegate(typeof(Action<UiAction>), plugin, method);
                return e => { call(e); return HookResult.Continue; };
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[PluginLoader] {plugin.Name}.{method.Name}: cannot bind [UiActionHandler] — {ex.Message}");
                return null;
            }
        }

        Console.WriteLine($"[PluginLoader] {plugin.Name}.{method.Name}: cannot bind [UiActionHandler] — " +
                          "signature must be void(UiAction) or HookResult(UiAction).");
        return null;
    }

    // Must be called under _lock.
    private static void UnregisterPluginUiActionHandlers(string normalizedPath)
    {
        if (!_pluginUiActionHandlers.Remove(normalizedPath, out var handles))
            return;

        foreach (var handle in handles)
            handle.Cancel();
    }

    /// <summary>Runs attribute discovery over plugin instances; for tests only.</summary>
    internal static void RegisterPluginUiActionHandlersForTests(string normalizedPath, List<IDeadworksPlugin> plugins)
    {
        lock (_lock)
            RegisterPluginUiActionHandlers(normalizedPath, plugins);
    }

    /// <summary>Cancels what a path registered, as unloading would; for tests only.</summary>
    internal static void UnregisterPluginUiActionHandlersForTests(string normalizedPath)
    {
        lock (_lock)
            UnregisterPluginUiActionHandlers(normalizedPath);
    }

    // Must be called under _lock.
    private static void ClearUiActionHandlers()
    {
        foreach (var handles in _pluginUiActionHandlers.Values)
            foreach (var handle in handles)
                handle.Cancel();
        _pluginUiActionHandlers.Clear();
    }
}
