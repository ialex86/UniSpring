# Catalogs and typed settings

[Documentation index](index.md) · [Data reference](data.md)

Choose a repository for mutable player saves, a catalog for shipped content (items, levels, definitions), and configuration properties for application settings. Catalogs have no save/delete API. Treat their returned items and lists as immutable, even if the underlying records are mutable C# objects.

## Resource layout

```text
Assets/Resources/Shop/Items/sword.json
Assets/Resources/Shop/Items/shield.json
Assets/Resources/Shop/Settings.json
```

IDs are asset names, paths omit the Resources prefix and extension, and each catalog file contains one record. Keep IDs unique and stable. With the JSON serializer in [the shop tutorial](getting-started.md), a file containing `{"label":"Sword","price":25}` maps to `ShopItem` and `{"startingBalance":100}` maps to `ShopSettings`.

```csharp
[Catalog("Shop/Items")]
public interface IShopCatalog : ICatalog<ShopItem>
{
    IReadOnlyList<ShopItem> FindByPrice(int price);
}

[Serializable]
[ConfigurationProperties("Shop/Settings", Required = true)]
public sealed class ShopSettings
{
    public long startingBalance;
}
```

This alternative catalog contract adds an equality query to the tutorial model. Import `System`, `System.Collections.Generic` and `UniSpring.Data`. Do not declare two alternatives with the same name in one assembly. Catalogs and plain settings use the registered `IDocumentSerializer`; mappers/validators can be registered as beans. A settings type deriving from ScriptableObject instead loads a Unity asset of that type. Configure a required resource when missing defaults would be unsafe. Register an explicit settings instance before warm-up to override resource binding; its owner is responsible for validating it.

## Loading and failure behavior

Resolution builds and caches the provider's object. An Application service depending on the catalog forces loading during warm-up; a Scene-only dependency may load on first use later. Malformed content or failed validation causes resolution to fail with diagnostics. Use one mapper per domain type; two mappers for one type are an error. `All` and `Ids` are read-only, but the items themselves are whatever objects the mapper made: do not mutate them. Do not destroy a ScriptableObject loaded from Resources; one made for missing defaults is destroyed with the container.

There is no automatic hot reload. Use a deliberate content reload architecture if your game needs one. On IL2CPP catalogs use editor-generated proxies (see [IL2CPP](il2cpp.md)).

## Replace Resources in tests or custom projects

`IResourceSource` exposes synchronous methods:

- `LoadAll(string folder)` returns name/byte pairs for a catalog.
- `Load(string path)` returns bytes for a text resource or null when absent.
- `LoadAsset(string path, Type type)` returns a Unity object or null.

Supply one implementation as a bean, or use `DocumentData.InMemory(serializer).Catalog<T>(source)` without the DI container. The tutorial includes a complete fake. Register mappers and validators via `.With(...)` before asking for data contracts. Do not block the Unity frame waiting for asynchronous Addressables work inside a synchronous resource-source method; preload outside the resolution path if you design such an adapter. Validate platform constraints and asset ownership explicitly.
