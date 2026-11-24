using System;
using System.Collections;
using System.Collections.Generic;
using ShopExample;
using UniSpring.Data;
using UniSpring.DependencyInjection;
using UnityEngine;

namespace UniSpringSmoke
{
    /// <summary>
    /// Runs in the IL2CPP player: exercises DI, generated proxies (transactions, repositories, catalog), events,
    /// ticks, lifecycle, a value-type strategy key and the file store, then quits with 0 when everything held.
    /// </summary>
    public sealed class SmokeRunner : MonoBehaviour
    {
        private readonly List<string> m_Failures = new List<string>();

        private IEnumerator Start()
        {
            try
            {
                RuntimeContext.Get<IDocumentReset>().DeleteAll();
                Check("backend is IL2CPP", Application.platform != RuntimePlatform.OSXEditor);

                var shop = RuntimeContext.Get<IShop>();
                Check("[Transactional] service is a generated proxy", shop is GeneratedProxy);
                var events = RuntimeContext.Get<IEventPublisher>();
                var purchases = 0;
                var subscription = events.Subscribe<ItemPurchased>(evt => purchases++);

                Check("starting balance", shop.Balance() == 100);
                shop.Buy("sword");
                Check("balance after purchase", shop.Balance() == 75);
                Check("sword owned", shop.Owns("sword"));
                Check("purchase event", purchases == 1);
                Check("second purchase refused", Throws<InvalidOperationException>(() => shop.Buy("sword")));
                Check("unknown item refused", Throws<ArgumentException>(() => shop.Buy("nitro")));
                Check("balance unchanged by refusals", shop.Balance() == 75);
                subscription.Dispose();

                var catalog = RuntimeContext.Get<IShopCatalog>();
                Check("catalog is a generated proxy", catalog is GeneratedProxy);
                Check("catalog item", catalog.Find("sword") != null && catalog.Find("sword").price == 25);
                Check("catalog ids", catalog.Ids().Count == 1);

                var inventory = RuntimeContext.Get<IInventoryRepository>();
                Check("derived query", inventory.FindByItemId("sword").Count == 1);

                var strategies = RuntimeContext.Strategies<SmokeKind, ISmokeStrategy>();
                Check("enum-keyed strategy", strategies.Require(SmokeKind.Beta).Name == "beta");

                var store = RuntimeContext.Get<IDocumentStore>() as FileDocumentStore;
                Check("file store installed", store != null);
                Check("saves reach the disk", store != null && store.WaitForDisk(TimeSpan.FromSeconds(5)));
            }
            catch (Exception exception)
            {
                m_Failures.Add("unexpected " + exception);
            }

            for (var frame = 0; frame < 10; frame++)
                yield return null;
            try
            {
                var counters = RuntimeContext.Get<SmokeCounters>();
                Check("lifecycle started once", counters.Starts == 1);
                Check("ticks run", counters.Ticks >= 5);
                Check("injected enum-keyed registry", counters.Strategy == "alpha");
            }
            catch (Exception exception)
            {
                m_Failures.Add("unexpected " + exception);
            }

            foreach (var failure in m_Failures)
                Debug.Log("SMOKE FAILED: " + failure);
            Debug.Log(m_Failures.Count == 0 ? "UNISPRING SMOKE PASSED" : "UNISPRING SMOKE FAILED");
            Application.Quit(m_Failures.Count == 0 ? 0 : 1);
        }

        private void Check(string what, bool held)
        {
            Debug.Log("SMOKE " + (held ? "ok   " : "FAIL ") + what);
            if (!held)
                m_Failures.Add(what);
        }

        private static bool Throws<T>(Action action)
            where T : Exception
        {
            try
            {
                action();
                return false;
            }
            catch (T)
            {
                return true;
            }
        }
    }
}
