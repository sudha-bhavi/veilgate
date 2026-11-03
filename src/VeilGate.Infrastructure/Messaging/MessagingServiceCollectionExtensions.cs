using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using VeilGate.Application.Abstractions;

namespace VeilGate.Infrastructure.Messaging;

public static class MessagingServiceCollectionExtensions
{
    /// <summary>
    /// Picks the transport from Messaging:Transport. "Kafka" (the default) sends to the broker named in Kafka:BootstrapServers;
    /// "InMemory" keeps messages in memory, which is useful when Kafka is not running.
    /// </summary>
    public static IServiceCollection AddEventTransport(this IServiceCollection services, IConfiguration config)
    {
        if (string.Equals(config["Messaging:Transport"], "InMemory", StringComparison.OrdinalIgnoreCase))
        {
            services.AddSingleton<InMemoryEventTransport>();
            services.AddSingleton<IEventTransport>(sp => sp.GetRequiredService<InMemoryEventTransport>());
            return services;
        }

        services.AddOptions<KafkaOptions>().BindConfiguration(KafkaOptions.Section);
        services.AddSingleton<IEventTransport, KafkaEventTransport>();
        return services;
    }
}
