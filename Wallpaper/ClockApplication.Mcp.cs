using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Wallpaper
{
    internal sealed partial class ClockApplication
    {
        private readonly CancellationTokenSource mcpStopping = new CancellationTokenSource();
        private Task mcpLifetime;
        private string optionsEditingId;

        internal void WaitForMcpExit()
        {
            mcpStopping.Cancel();
            if (mcpLifetime != null) mcpLifetime.GetAwaiter().GetResult();
            mcpStopping.Dispose();
        }

        private void StartMcp()
        {
            var context = new WidgetMcpContext(preferences, Dispatcher, () => preferences.Save(), ApplyMcpOptions,
                id => memoEditingId == id || optionsEditingId == id, WidgetNames,
                id => snapshots.GetValueOrDefault(id));
            var desktop = new DesktopMcpContext(context, McpDisplays,
                () => editorWindow != null || memoEditingId != null || optionsEditingId != null ||
                    (settingsWindow != null && !settingsWindow.IsEnabled),
                RefreshMcpWidgets, () => { ReloadScenes(); SyncMonitors(); settingsWindow?.RefreshDisplays(); });
            mcpLifetime = Task.Run(async () =>
            {
                try
                {
                    var config = McpConfiguration.Load();
                    if (!config.Enabled) return;
                    await using (var server = new LocalMcpServer(new WidgetMcpRegistry(context, desktop), config))
                    {
                        await server.StartAsync(mcpStopping.Token).ConfigureAwait(false);
                        SceneLibrary.LogError("MCP 서버 시작 " + config.Endpoint);
                        await Task.Delay(Timeout.Infinite, mcpStopping.Token).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException) when (mcpStopping.IsCancellationRequested) { }
                catch (Exception error) { SceneLibrary.LogError("MCP 서버 오류: " + error.Message); }
            });
        }

        private McpDisplay[] McpDisplays()
        {
            var screens = Monitors.All();
            var main = MainScreen(screens);
            return screens.Select(screen =>
            {
                monitors.TryGetValue(screen.DeviceName, out var monitor);
                return new McpDisplay
                {
                    Id = Monitors.Key(screen.DeviceName), Device = screen.DeviceName, Main = screen.DeviceName == main?.DeviceName,
                    X = screen.Bounds.X, Y = screen.Bounds.Y, Width = screen.Bounds.Width, Height = screen.Bounds.Height,
                    SceneReady = monitor?.Scene != null,
                    VisibleWidgets = monitor?.Items.Keys.ToArray() ?? System.Array.Empty<string>(),
                    FailedWidgets = monitor?.Failed.ToArray() ?? System.Array.Empty<string>()
                };
            }).ToArray();
        }

        private void RefreshMcpWidgets()
        {
            foreach (var monitor in monitors.Values) monitor.Failed.Clear();
            SyncWidgets();
            foreach (var monitor in monitors.Values)
            {
                foreach (var runtime in monitor.Items.Values) runtime.Spot = Spot(runtime.Settings, monitor);
                PlaceWidgets(monitor);
            }
            settingsWindow?.RefreshDisplays();
        }

        private void ApplyMcpOptions(WidgetSettings widget)
        {
            if (widget.Kind == "hardware")
            {
                StopFeed(widget.Id);
                foreach (var monitor in monitors.Values.Where(m => m.Items.ContainsKey(widget.Id))) DestroyWidget(monitor, widget.Id);
                SyncWidgets();
            }
            else ApplyOptions(widget);
        }
    }
}
