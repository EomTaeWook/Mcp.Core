using System.Text.Json.Nodes;

namespace MCP.Core
{
    public abstract class McpControllerBase
    {
        protected McpControllerBase()
        {
        }

        public abstract Task<JsonObject> ExecuteAsync(McpRequest request, CancellationToken cancellationToken);
    }
}
