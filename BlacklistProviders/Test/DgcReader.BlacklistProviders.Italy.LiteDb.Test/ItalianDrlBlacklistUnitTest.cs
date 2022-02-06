using DgcReader.Interfaces.BlacklistProviders;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using DgcReader.RuleValidators.Italy;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.DependencyInjection;



namespace DgcReader.BlacklistProviders.Italy.LiteDb.Test
{
    [TestClass]
    public class ItalianDrlBlacklistUnitTest : TestBase
    {
        static ItalianDrlBlacklistLiteDbProviderOptions Options = new ItalianDrlBlacklistLiteDbProviderOptions
        {
            MinRefreshInterval = TimeSpan.Zero,
            RefreshInterval = TimeSpan.FromSeconds(10),
            UseAvailableValuesWhileRefreshing = false,
        };
        IBlacklistProvider BlacklistProvider { get; set; }

        [TestInitialize]
        public async Task Initialize()
        {
            BlacklistProvider = ServiceProvider.GetRequiredService<IBlacklistProvider>();
        }

        [TestMethod]
        public async Task TestRefreshBlacklist()
        {
            await BlacklistProvider.RefreshBlacklist();
        }

        /// <summary>
        /// Check if all the identifiers in the old blacklist are present in the new one
        /// </summary>
        /// <returns></returns>
        [TestMethod]
        public async Task TestOldBlacklist()
        {

            // The validator implementing the old blacklist
            var rulesValidator = ServiceProvider.GetRequiredService<DgcItalianRulesValidator>();

            // Reading the old identifiers
            var oldIdentifiers = await rulesValidator.GetBlacklist();

            Console.WriteLine($"Checking {oldIdentifiers.Count()} old identifiers");

            var missing = new List<string>();
            foreach (var oldIdentifier in oldIdentifiers)
            {
                var isBlacklisted = await BlacklistProvider.IsBlacklisted(oldIdentifier);
                if (!isBlacklisted)
                {
                    missing.Add(oldIdentifier);
                    Console.WriteLine($"{oldIdentifier} is missing");
                }
            }

            if (missing.Any())
                Assert.Inconclusive($"{missing.Count} identifiers out of {oldIdentifiers.Count()} are missing from the new Blacklist");
        }

        [TestMethod]
        public void TestServiceDI()
        {
            var instance = ServiceProvider.GetRequiredService<IBlacklistProvider>();
            Assert.IsNotNull(instance);
        }

        protected override void ConfigureServices(IServiceCollection services)
        {
            base.ConfigureServices(services);

            services.AddDgcReader()
                .AddItalianRulesValidator()
                .AddItalianDrlBlacklistLiteDbProvider((ItalianDrlBlacklistLiteDbProviderOptions o) =>
                {
                    o.RefreshInterval = Options.RefreshInterval;
                    o.MinRefreshInterval = Options.MinRefreshInterval;
                    o.BasePath = Options.BasePath;
                    o.UseAvailableValuesWhileRefreshing = Options.UseAvailableValuesWhileRefreshing;
                });
        }
    }
}