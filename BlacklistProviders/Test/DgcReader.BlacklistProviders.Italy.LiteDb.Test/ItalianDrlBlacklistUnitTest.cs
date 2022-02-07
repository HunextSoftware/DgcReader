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
using System.Diagnostics;

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

        ItalianDrlBlacklistLiteDbProvider BlacklistProvider { get; set; }

        [TestInitialize]
        public async Task Initialize()
        {
            BlacklistProvider = ServiceProvider.GetRequiredService<ItalianDrlBlacklistLiteDbProvider>();
        }

        [TestMethod]
        public async Task TestRefreshBlacklist()
        {
            await BlacklistProvider.RefreshBlacklist();
        }

        [TestMethod]
        public async Task TestDrlProgressEvents()
        {
            DownloadProgressEventArgs? lastProgress = null;
            BlacklistProvider.DownloadProgressChanged += (sender, args) =>
            {
                lastProgress = args;
                Debug.WriteLine(args);
            };

            await BlacklistProvider.RefreshBlacklist();

            if (lastProgress != null)
            {
                Assert.IsTrue(lastProgress.IsCompleted);
                Assert.AreEqual(lastProgress.TotalProgressPercent, 1f);
            }
            else
            {
                Assert.Inconclusive("No values refreshed, no events");
            }

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