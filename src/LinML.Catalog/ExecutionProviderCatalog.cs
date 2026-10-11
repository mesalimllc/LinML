using LinML.Contracts;

namespace LinML.Catalog;

/// <summary>The catalog of execution providers LinML can vendor and register.</summary>
public sealed class ExecutionProviderCatalog : IExecutionProviderCatalog
{
    /// <summary>
    /// Default plugin root, <c>~/.local/linml-deps</c>: the location
    /// <c>scripts/fetch-ep-plugins.sh</c> vendors to, so script-vendored plugins are found.
    /// </summary>
    public static string DefaultInstallRoot { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "linml-deps");

    private static readonly Lazy<ExecutionProviderCatalog> Default = new(() => Create(DefaultInstallRoot));

    private readonly IReadOnlyList<IExecutionProvider> _providers;

    private ExecutionProviderCatalog(IReadOnlyList<IExecutionProvider> providers)
    {
        _providers = providers;
    }

    /// <summary>The process-wide catalog rooted at <see cref="DefaultInstallRoot"/>.</summary>
    public static IExecutionProviderCatalog GetDefault() => Default.Value;

    /// <summary>
    /// A catalog rooted at <paramref name="installRoot"/>, e.g. an application's
    /// own data directory. Registration with ORT stays process-wide: create one
    /// catalog per process and reuse it.
    /// </summary>
    public static ExecutionProviderCatalog Create(string installRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(installRoot);
        return Create(installRoot, new PluginInstaller(), OrtRegistrar.Instance, isSupportedPlatform: null);
    }

    internal static ExecutionProviderCatalog Create(
        string installRoot,
        PluginInstaller installer,
        IOrtRegistrar registrar,
        Func<bool>? isSupportedPlatform)
    {
        IExecutionProvider[] providers = KnownPlugins.All
            .Select(spec => (IExecutionProvider)new PluginExecutionProvider(spec, installRoot, installer, registrar, isSupportedPlatform))
            .ToArray();
        return new ExecutionProviderCatalog(providers);
    }

    /// <inheritdoc />
    public IReadOnlyList<IExecutionProvider> FindAllProviders() => _providers;

    /// <inheritdoc />
    public IExecutionProvider? Find(ProviderId id)
    {
        ArgumentNullException.ThrowIfNull(id);
        return _providers.FirstOrDefault(p => p.Id == id);
    }
}
