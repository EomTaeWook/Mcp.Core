using System.Text.Json.Nodes;

namespace MCP.Core.Models.Server
{
    internal class McpLegacySamplingRequest
    {
        internal McpLegacySamplingRequest(string sessionId, CancellationToken cancellationToken)
        {
            SessionId = sessionId;
            CancellationToken = cancellationToken;
            Completion = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        internal string SessionId { get; private set; }
        internal CancellationToken CancellationToken { get; private set; }
        internal TaskCompletionSource<JsonObject> Completion { get; private set; }
    }
}
