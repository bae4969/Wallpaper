using System;
using System.Collections.Generic;
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
            var context = new WidgetMcpContext(preferences, Dispatcher, () => preferences.Save(), ApplyOptions,
                id => memoEditingId == id || optionsEditingId == id, WidgetNames,
                id => snapshots.GetValueOrDefault(id));
            mcpLifetime = Task.Run(async () =>
            {
                try
                {
                    var config = McpConfiguration.Load();
                    if (!config.Enabled) return;
                    await using (var server = new LocalMcpServer(new WidgetMcpRegistry(context), config))
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
    }
}
