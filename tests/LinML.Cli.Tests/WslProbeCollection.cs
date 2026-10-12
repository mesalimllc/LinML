using Xunit;

namespace LinML.Cli.Tests;

/// <summary>
/// Tests that replace the process-wide WSL2 probe or environment variables must not run
/// alongside any other test class.
/// </summary>
[CollectionDefinition("WslProbe", DisableParallelization = true)]
public sealed class WslProbeCollection;
