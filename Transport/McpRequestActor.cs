using Dignus.Actor.Abstractions;
using Dignus.Actor.Core;
using System.Text.Json.Nodes;

namespace MCP.Core.Transport
{
    internal class McpRequestActor : ActorBase
    {
        private readonly McpDispatcher _dispatcher;

        internal McpRequestActor(McpDispatcher dispatcher)
        {
            _dispatcher = dispatcher;
        }

        protected override async ValueTask OnReceive(IActorMessage message, IActorRef sender)
        {
            if (message is not McpRequestExecution execution)
            {
                return;
            }
            try
            {
                JsonObject response = await _dispatcher.DispatchAsync(execution.Request, execution.Token);
                execution.DispatchCompletion.TrySetResult(response);
            }
            catch (OperationCanceledException) when (execution.Token.IsCancellationRequested == true)
            {
                execution.DispatchCompletion.TrySetCanceled(execution.Token);
            }
            catch (Exception error)
            {
                execution.DispatchCompletion.TrySetException(error);
            }
        }
    }
}
