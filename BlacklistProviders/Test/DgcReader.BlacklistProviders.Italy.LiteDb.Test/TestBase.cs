using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace DgcReader.BlacklistProviders.Italy.LiteDb.Test
{
    public abstract class TestBase
    {
        protected TestBase()
        {
            Configuration = LoadConfiguration();
            InitializeServices();
        }

        protected IConfiguration Configuration;
        protected IServiceProvider ServiceProvider { get; private set; }


        protected virtual void ConfigureServices(IServiceCollection services)
        {
            services.AddLogging(options =>
            {
                options
                    .AddFilter("Microsoft", LogLevel.Warning)
                    .AddFilter("System.Net.HttpClient", LogLevel.Warning)
                    .AddFilter("System", LogLevel.Warning)
                    .AddConsole().AddDebug();
            });
        }

        private void InitializeServices()
        {
            var serviceCollection = new ServiceCollection();
            ConfigureServices(serviceCollection);

            ServiceProvider = serviceCollection.BuildServiceProvider();
        }

        private IConfiguration LoadConfiguration()
        {
            var env = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT");
            var builder = new ConfigurationBuilder()
                .AddJsonFile($"appsettings.json", true, true)
                .AddJsonFile($"appsettings.{env}.json", true, true)
                .AddEnvironmentVariables();

            var config = builder.Build();
            return config;
        }
    }

}
