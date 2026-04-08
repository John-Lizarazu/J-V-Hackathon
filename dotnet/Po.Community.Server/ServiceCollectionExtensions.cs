#pragma warning disable MCPEXP001

using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using Po.Community.Core;

namespace Po.Community.Server;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddMcpServices(this IServiceCollection services)
    {
        services
            .AddMcpServer(options =>
            {
                options.Capabilities = new ServerCapabilities
                {
                    Extensions = new Dictionary<string, object>
                    {
                        ["ai.promptopinion/fhir-context"] = new JsonObject()
                    }
                };
            })
            .WithHttpTransport(options =>
            {
                options.Stateless = true;
            })
            .WithListToolsHandler(McpClientListToolsService.Handler)
            .WithCallToolHandler(McpClientCallToolService.Handler);

        var mcpToolTypes = new List<Type>();
        foreach (var type in typeof(IMcpTool).Assembly.GetTypes())
        {
            if (type.IsInterface || type.IsAbstract)
            {
                continue;
            }

            if (type.IsAssignableTo(typeof(IMcpTool)))
            {
                mcpToolTypes.Add(type);
            }
        }

        foreach (var mcpToolType in mcpToolTypes)
        {
            services.AddScoped(typeof(IMcpTool), mcpToolType);
        }

        return services;
    }
}

#pragma warning restore MCPEXP001