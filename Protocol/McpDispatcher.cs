using Dignus.DependencyInjection;
using MCP.Core.Attributes;
using MCP.Core.Controllers;
using MCP.Core.Models.Protocol;
using MCP.Core.Models.Server;
using MCP.Core.Tools;
using System.Reflection;
using System.Text.Json.Nodes;

namespace MCP.Core
{
    internal class McpDispatcher
    {
        private readonly McpServerOptions _options;
        private readonly Dictionary<string, Type> _controllerTypes;
        private readonly IServiceProvider _services;

        internal McpDispatcher(McpServerOptions options, IReadOnlyList<McpTool> tools, Action<Exception> reportError, Func<JsonNode, Task> cancelRequest, Assembly controllerAssembly, Action<ServiceContainer> configureServices)
        {
            _options = options;
            Dictionary<string, McpTool> registeredTools = new Dictionary<string, McpTool>(StringComparer.Ordinal);
            foreach (McpTool tool in tools)
            {
                ArgumentNullException.ThrowIfNull(tool);
                if (registeredTools.TryAdd(tool.Name, tool) == false)
                {
                    throw new ArgumentException("An MCP tool name is registered more than once: " + tool.Name, nameof(tools));
                }
            }
            ServiceContainer services = new ServiceContainer();
            if (configureServices != null)
            {
                configureServices(services);
            }
            services.RegisterType(options);
            services.RegisterType<IReadOnlyDictionary<string, McpTool>>(registeredTools);
            services.RegisterType<Action<Exception>>(reportError);
            services.RegisterType<Func<JsonNode, Task>>(cancelRequest);
            _controllerTypes = new Dictionary<string, Type>(StringComparer.Ordinal);
            Assembly protocolAssembly = typeof(McpControllerBase).Assembly;
            RegisterControllers(services, protocolAssembly);
            if (controllerAssembly != null)
            {
                if (controllerAssembly != protocolAssembly)
                {
                    RegisterControllers(services, controllerAssembly);
                }
            }
            _services = services.Build();
        }

        private void RegisterControllers(ServiceContainer services, Assembly assembly)
        {
            foreach (Type controllerType in assembly.GetTypes().OrderBy(controllerType => controllerType.FullName, StringComparer.Ordinal))
            {
                if (controllerType.IsClass == false)
                {
                    continue;
                }
                if (controllerType.IsAbstract == true)
                {
                    continue;
                }
                ControllerAttribute controller = controllerType.GetCustomAttribute<ControllerAttribute>(false);
                if (controller == null)
                {
                    continue;
                }
                if (typeof(McpControllerBase).IsAssignableFrom(controllerType) == false)
                {
                    throw new ArgumentException("The attributed MCP controller must inherit McpControllerBase: " + controllerType.FullName, nameof(assembly));
                }
                if (controllerType.ContainsGenericParameters == true)
                {
                    throw new ArgumentException("An MCP controller cannot be an open generic type: " + controllerType.FullName, nameof(assembly));
                }
                if (_controllerTypes.TryAdd(controller.Name, controllerType) == false)
                {
                    throw new ArgumentException("An MCP controller name is registered more than once: " + controller.Name, nameof(assembly));
                }
                services.RegisterType(controllerType, controllerType, LifeScope.Transient);
            }
        }

        internal async Task<JsonObject> DispatchAsync(McpRequest request, CancellationToken token)
        {
            if (request.IsNotification == true)
            {
                if (_controllerTypes.TryGetValue(request.Method, out Type notificationType) == true)
                {
                    token.ThrowIfCancellationRequested();
                    McpControllerBase notification = (McpControllerBase)_services.GetService(notificationType);
                    await notification.ExecuteAsync(request, token).ConfigureAwait(false);
                }
                return null;
            }
            if (request.Method.StartsWith("notifications/", StringComparison.Ordinal) == true)
            {
                throw MethodNotFound(request);
            }
            if (_controllerTypes.TryGetValue(request.Method, out Type controllerType) == false)
            {
                throw MethodNotFound(request);
            }
            token.ThrowIfCancellationRequested();
            McpControllerBase controller = (McpControllerBase)_services.GetService(controllerType);
            JsonObject result = await controller.ExecuteAsync(request, token).ConfigureAwait(false);
            if (request.IsModern == true)
            {
                result["resultType"] = "complete";
                result["_meta"] = new JsonObject
                {
                    ["io.modelcontextprotocol/serverInfo"] = McpProtocol.ServerInfo(_options)
                };
            }
            return McpProtocol.Response(request.Id, result);
        }

        private McpProtocolException MethodNotFound(McpRequest request)
        {
            int status = 200;
            if (request.IsModern == true)
            {
                status = 404;
            }
            return new McpProtocolException(-32601, "Method not found: " + request.Method, status);
        }
    }
}
