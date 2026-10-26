using Dignus.Actor.Abstractions;
using MCP.Core.Models.Protocol;
using System.Text.Json.Nodes;

namespace MCP.Core.Transport
{
    internal class McpRequestExecution : IActorMessage, IDisposable
    {
        private readonly CancellationTokenSource _cancellation;
        private JsonNode _requestId;
        private int _clientCancelled;

        internal McpRequestExecution(CancellationToken lifetime, TimeSpan timeout)
        {
            _cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
            _cancellation.CancelAfter(timeout);
        }

        internal Task Completion { get; set; } = Task.CompletedTask;
        internal McpRequest Request { get; set; }
        internal TaskCompletionSource<JsonObject> DispatchCompletion { get; } = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);

        internal CancellationToken Token
        {
            get
            {
                return _cancellation.Token;
            }
        }

        internal bool IsClientCancelled
        {
            get
            {
                return Volatile.Read(ref _clientCancelled) == 1;
            }
        }

        internal void Identify(JsonNode requestId)
        {
            Volatile.Write(ref _requestId, requestId?.DeepClone());
        }

        internal bool Matches(JsonNode requestId)
        {
            JsonNode current = Volatile.Read(ref _requestId);
            return current != null && JsonNode.DeepEquals(current, requestId) == true;
        }

        internal async Task CancelFromClientAsync()
        {
            Interlocked.Exchange(ref _clientCancelled, 1);
            try
            {
                await _cancellation.CancelAsync().ConfigureAwait(false);
            }
            catch (ObjectDisposedException)
            {
            }
        }

        public void Dispose()
        {
            _cancellation.Dispose();
        }
    }
}
