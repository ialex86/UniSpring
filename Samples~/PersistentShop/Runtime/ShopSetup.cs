// Kept in sync with Documentation~/getting-started.md, which shows this code step by step.
using System;
using System.IO;
using System.Text;
using UniSpring.Data;
using UniSpring.DependencyInjection;
using UnityEngine;

namespace ShopExample
{
    [Service]
    public sealed class ShopJsonSerializer : IDocumentSerializer
    {
        public byte[] Write(object record) => Encoding.UTF8.GetBytes(JsonUtility.ToJson(record));
        public object Read(Type type, byte[] bytes) => JsonUtility.FromJson(Encoding.UTF8.GetString(bytes), type);
    }

    public static class ShopSetup
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void Configure()
        {
            RuntimeContext.Configure(context => FileDocumentStore.Install(context,
                Path.Combine(Application.persistentDataPath, "UniSpringShopTutorial")));
        }
    }
}
