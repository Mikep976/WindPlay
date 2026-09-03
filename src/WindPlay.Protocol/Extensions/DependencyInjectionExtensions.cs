using AirPlay.Core2.Models.Configs;
using AirPlay.Core2.Services;
using AirPlay.Core2.Security;
using Makaretu.Dns;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AirPlay.Core2.Extensions;

public static class DependencyInjectionExtensions
{
    extension(IServiceCollection serviceDescriptors)
    {
        public void UseAirPlayService()
        {
            serviceDescriptors.AddOptions<AirTunesConfig>();
            serviceDescriptors.AddOptions<AirPlayConfig>();
            serviceDescriptors.TryAddSingleton(_ => ReceiverIdentity.CreateRandom());

            serviceDescriptors.AddSingleton<AirTunesService>();
            serviceDescriptors.AddHostedService(s => s.GetRequiredService<AirTunesService>());

            serviceDescriptors.AddSingleton<AirPlayService>();
            serviceDescriptors.AddHostedService(s => s.GetRequiredService<AirPlayService>());

            serviceDescriptors.AddSingleton<DacpDiscoveryService>();
            serviceDescriptors.AddHostedService(s => s.GetRequiredService<DacpDiscoveryService>());

            serviceDescriptors.AddSingleton<SessionManager>();
            serviceDescriptors.AddSingleton<MulticastService>();
            serviceDescriptors.AddSingleton<AirPlayPublisher>();

            serviceDescriptors.AddHostedService(s => s.GetRequiredService<AirPlayPublisher>());
        }
    }
}
