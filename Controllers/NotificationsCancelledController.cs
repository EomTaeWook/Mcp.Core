using MCP.Core.Models.Protocol;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MCP.Core.Controllers
{
    [Controller("notifications/cancelled")]
    internal class NotificationsCancelledController : McpControllerBase
    {
        private readonly Func<JsonNode, Task> _cancelRequest;

        public NotificationsCancelledController(Func<JsonNode, Task> cancelRequest)
        {
            _cancelRequest = cancelRequest;
        }

        public override async Task<JsonObject> ExecuteAsync(McpRequest request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (request.IsModern == true)
            {
                return null;
            }
            JsonNode id = request.Parameters["requestId"];
            if (id == null)
            {
                return null;
            }
            if (id.GetValueKind() != JsonValueKind.String)
            {
                if (id.GetValueKind() != JsonValueKind.Number)
                {
                    return null;
                }
                if (long.TryParse(id.ToJsonString(), NumberStyles.AllowLeadingSign,
                    CultureInfo.InvariantCulture, out _) == false)
                {
                    return null;
                }
            }
            await _cancelRequest(id).ConfigureAwait(false);
            return null;
        }
    }
}
