using MCP.Core.Models.Protocol;
using MCP.Core.Models.Server;
using MCP.Core.Protocol;
using System.Text.Json.Nodes;

namespace MCP.Core.Controllers
{
    [Controller("server/discover")]
    internal class ServerDiscoverController : McpControllerBase
    {
        private readonly McpServerOptions _options;

        public ServerDiscoverController(McpServerOptions options)
        {
            _options = options;
        }

        public override Task<JsonObject> ExecuteAsync(McpRequest request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (request.IsModern == false)
            {
                throw new McpProtocolException(-32601, "server/discover requires the current MCP protocol.", 200);
            }
            return Task.FromResult(new JsonObject
            {
                ["supportedVersions"] = new JsonArray(McpProtocol.CurrentVersion),
                ["capabilities"] = McpProtocol.ToolCapabilities(),
                ["instructions"] = _options.Instructions,
                ["ttlMs"] = 0,
                ["cacheScope"] = "private"
            });
        }
    }
}
