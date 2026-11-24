using System;
using System.Collections.Generic;
using UnityEngine;

namespace UniSpring.Data
{
    /// <summary>
    /// Marks an interface the container implements as a read-only catalog of game data (shop items, part tables,
    /// liveries): one item per text asset in a <c>Resources</c> folder, the asset's name its id. Items go through
    /// the serializer, the type's mapper and its validators when the container starts; a broken item stops the
    /// start, naming the asset. Query methods are implemented by name as in a repository (<c>FindBy</c>,
    /// <c>CountBy</c>, <c>ExistsBy</c>). Implemented by a run-time proxy: Mono only.
    /// </summary>
    [AttributeUsage(AttributeTargets.Interface, Inherited = false)]
    public sealed class CatalogAttribute : Attribute
    {
        /// <param name="folder">The folder under a <c>Resources</c> folder, for example <c>"Shop/Items"</c>.</param>
        public CatalogAttribute(string folder)
        {
            Folder = folder ?? throw new ArgumentNullException(nameof(folder));
        }

        public string Folder { get; }
    }

    /// <summary>Game data of one type, loaded once, never written.</summary>
    public interface ICatalog<T>
        where T : class
    {
        /// <summary>The item with the id, or null.</summary>
        T Find(string id);

        bool Exists(string id);

        /// <summary>Every item, in the order of their ids.</summary>
        IReadOnlyList<T> All();

        /// <summary>The ids, sorted ordinally.</summary>
        IReadOnlyList<string> Ids();
    }

    /// <summary>
    /// Binds a settings class to a resource, as Spring's <c>@ConfigurationProperties</c>: ask for the class in a
    /// constructor and the container hands out one instance, loaded when it is first needed and checked by the
    /// type's validators (a failure stops the start). A <see cref="ScriptableObject"/> class is loaded as that asset
    /// (edit it in the inspector); any other class is read from a text asset through the serializer. A missing
    /// resource gives the class's defaults, unless <see cref="Required"/>. An instance registered with
    /// <c>RegisterInstance</c> wins over the resource.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class, Inherited = false)]
    public sealed class ConfigurationPropertiesAttribute : Attribute
    {
        /// <param name="path">The resource path under a <c>Resources</c> folder, without extension.</param>
        public ConfigurationPropertiesAttribute(string path)
        {
            Path = path ?? throw new ArgumentNullException(nameof(path));
        }

        public string Path { get; }

        /// <summary>When true, a missing resource stops the start instead of giving defaults.</summary>
        public bool Required { get; set; }
    }

    /// <summary>
    /// Where catalogs and settings are read from. The default reads Unity's <c>Resources</c>; register another
    /// implementation as a bean (Addressables, files, a test double).
    /// </summary>
    public interface IResourceSource
    {
        /// <summary>The bytes of every text asset directly in the folder, by asset name.</summary>
        IReadOnlyList<KeyValuePair<string, byte[]>> LoadAll(string folder);

        /// <summary>The bytes of one text asset, or null when there is none.</summary>
        byte[] Load(string path);

        /// <summary>A Unity asset of the type, or null when there is none.</summary>
        UnityEngine.Object LoadAsset(string path, Type type);
    }

    /// <summary>Unity's <c>Resources</c>; used when the application registers no <see cref="IResourceSource"/>.</summary>
    public sealed class ResourcesSource : IResourceSource
    {
        public IReadOnlyList<KeyValuePair<string, byte[]>> LoadAll(string folder)
        {
            var assets = Resources.LoadAll<TextAsset>(folder);
            var result = new List<KeyValuePair<string, byte[]>>(assets.Length);
            foreach (var asset in assets)
                result.Add(new KeyValuePair<string, byte[]>(asset.name, asset.bytes));
            return result;
        }

        public byte[] Load(string path)
        {
            var asset = Resources.Load<TextAsset>(path);
            return asset != null ? asset.bytes : null;
        }

        public UnityEngine.Object LoadAsset(string path, Type type) => Resources.Load(path, type);
    }
}
