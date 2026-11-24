// Kept in sync with Documentation~/getting-started.md, which shows this code step by step.
using System;
using UniSpring.Data;
using UniSpring.DependencyInjection;
using UnityEngine;

namespace ShopExample
{
    public sealed class ShopDemo : AutowiredMonoBehaviour
    {
        [Autowired] private IShop m_Shop;
        [Autowired] private IDocumentStore m_Store;

        protected override void OnDependenciesInjected()
        {
            Debug.Log("Wallet: " + m_Shop.Balance());
        }

        // Connect to a UI Button, or use this temporary OnGUI control.
        public void BuySword()
        {
            try
            {
                m_Shop.Buy("sword");
                Debug.Log("Wallet after purchase: " + m_Shop.Balance());
            }
            catch (Exception error) { Debug.LogWarning(error.Message); }
        }

        // Diagnostic only: this waits synchronously, so do not use it every frame.
        public void Checkpoint()
        {
            if (!(m_Store is FileDocumentStore store)) return;
            bool saved = store.WaitForDisk(TimeSpan.FromSeconds(2));
            Debug.Log($"Checkpoint: {saved}; lost changes: {store.LostChanges}");
        }

        private void OnGUI()
        {
            if (m_Shop == null) return;
            if (GUILayout.Button("Buy sword")) BuySword();
            if (GUILayout.Button("Check disk")) Checkpoint();
        }
    }
}
