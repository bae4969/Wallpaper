using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.AspNetCore;
using ModelContextProtocol.Protocol;

namespace Wallpaper
{
    internal sealed class McpConfiguration
    {
        public bool Enabled { get; set; } = true;
        public int Port { get; set; } = 47831;
        public string Token { get; set; }
        public string Endpoint { get { return "http://127.0.0.1:" + Port + "/mcp"; } }
        public static string FilePath { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Wallpaper", "mcp.json"); } }

        public static McpConfiguration Load(string path = null)
        {
            path = path ?? FilePath;
            McpConfiguration config;
            if (File.Exists(path))
                config = JsonSerializer.Deserialize<McpConfiguration>(File.ReadAllText(path));
            else
            {
                config = new McpConfiguration { Token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)) };
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path, JsonSerializer.Serialize(new { config.Enabled, config.Port, config.Token }, new JsonSerializerOptions { WriteIndented = true }));
            }
            if (config == null || config.Port < 1024 || config.Port > 65535 || String.IsNullOrWhiteSpace(config.Token) ||
                config.Token.Length < 32 || config.Token.Length > 256 || config.Token.Any(c => c <= ' ' || c > '~'))
                throw new InvalidDataException("mcp.json의 Port(1024~65535)와 Token(공백 없는 ASCII 32~256자)을 확인해 주세요.");
            return config;
        }
    }

    // 앱과 함께 시작·종료되는 루프백 전용 서버. 실패해도 바탕화면 기능은 계속 동작한다.
    internal sealed class LocalMcpServer : IAsyncDisposable
    {
        private readonly WebApplication host;

        public LocalMcpServer(WidgetMcpRegistry registry, McpConfiguration config)
        {
            var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
            {
                Args = Array.Empty<string>(), ApplicationName = typeof(LocalMcpServer).Assembly.GetName().Name,
                ContentRootPath = AppContext.BaseDirectory
            });
            // 외부 appsettings·환경 변수에 선언된 Kestrel 엔드포인트를 가져오지 않는다.
            builder.Configuration.Sources.Clear();
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(options =>
            {
                options.Listen(IPAddress.Loopback, config.Port);
                options.Limits.MaxRequestBodySize = 1024 * 1024;
            });
            var mcp = builder.Services.AddMcpServer(options =>
            {
                options.ServerInfo = new Implementation { Name = "Wallpaper", Version = "1.0.0" };
            }).WithHttpTransport(options => options.SessionMode = HttpServerSessionMode.Stateless);
            registry.Register(mcp);
            host = builder.Build();
            var tokenHash = SHA256.HashData(Encoding.UTF8.GetBytes(config.Token));
            host.Use(async (context, next) =>
            {
                var request = context.Request;
                bool validHost = (request.Host.Host == "127.0.0.1" || request.Host.Host == "localhost") && request.Host.Port == config.Port;
                var origin = request.Headers.Origin;
                bool validOrigin = origin.Count == 0 || (origin.Count == 1 &&
                    (origin[0] == "http://127.0.0.1:" + config.Port || origin[0] == "http://localhost:" + config.Port));
                if (!validHost || !validOrigin) { context.Response.StatusCode = 403; return; }
                var authorization = request.Headers.Authorization.ToString();
                if (!authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ||
                    !CryptographicOperations.FixedTimeEquals(tokenHash, SHA256.HashData(Encoding.UTF8.GetBytes(authorization.Substring(7)))))
                {
                    context.Response.StatusCode = 401;
                    context.Response.Headers.WWWAuthenticate = "Bearer";
                    return;
                }
                await next(context).ConfigureAwait(false);
            });
            host.MapMcp("/mcp");
        }

        public Task StartAsync(CancellationToken cancellationToken) { return host.StartAsync(cancellationToken); }

        public async ValueTask DisposeAsync()
        {
            using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2)))
            {
                try { await host.StopAsync(timeout.Token).ConfigureAwait(false); }
                finally { await host.DisposeAsync().ConfigureAwait(false); }
            }
        }
    }
}
