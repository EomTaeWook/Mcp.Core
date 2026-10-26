using System.Text.Json.Nodes;

namespace MCP.Core
{
    [Controller("ping")]
    internal class PingController : McpControllerBase
    {

        public PingController()
        {
        }

        public override Task<JsonObject> ExecuteAsync(McpRequest request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            return Task.FromResult(new JsonObject());
        }
    }
}
