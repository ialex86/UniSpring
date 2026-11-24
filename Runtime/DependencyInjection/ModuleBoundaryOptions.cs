using System;
using System.Collections.Generic;

namespace UniSpring.DependencyInjection
{
    /// <summary>
    /// Optional check of the "Api-only between modules" convention.
    ///
    /// The container itself does not require any assembly naming. This policy
    /// is a project architecture rule: enable it and describe your module
    /// layout when you want the container to reject dependencies on another
    /// module's internals.
    /// </summary>
    public sealed class ModuleBoundaryOptions
    {
        /// <summary>Off by default: most projects do not use the module layout.</summary>
        public bool Enabled { get; set; }

        /// <summary>
        /// When true, a violation fails resolution. When false, violations are
        /// only collected in <see cref="ApplicationContext.BoundaryViolations"/>.
        /// </summary>
        public bool Strict { get; set; } = true;

        /// <summary>Assembly name suffix that marks a module's public contract.</summary>
        public string ApiSuffix { get; set; } = ".Api";

        /// <summary>
        /// Assembly name suffixes that mark a module's non-public layer. A
        /// dependency on such an assembly is allowed only inside the same module.
        /// </summary>
        public string[] InternalLayerSuffixes { get; set; } =
        {
            ".Runtime",
            ".Presentation",
            ".Editor",
            ".Tests",
            ".PlayTests",
            ".PlayModeTests"
        };

        /// <summary>Assemblies that every module may depend on.</summary>
        public ISet<string> SharedAssemblies { get; set; } = new HashSet<string>(StringComparer.Ordinal)
        {
            "UniSpring.DependencyInjection",
            "UniSpring.Transactions",
            "UniSpring.Data",
            "UniSpring.Logging"
        };
    }
}
